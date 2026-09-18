using System.Numerics;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.Entities;
using PoEformance.Game.Files;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PoEformance.Overlay;

/// <summary>
/// A monster's model, drawn in a pane, turned with the mouse and played through its animations.
/// </summary>
/// <remarks>
/// THREE THREADS' WORTH OF RULES IN ONE SMALL CLASS, which is why it is its own class rather than
/// another two hundred lines of the book's window:
///
///   - WALKING THE CHAIN IS SLOW. Five files out of the bundles and a texture to decode measured
///     around a tenth of a second, which is six frames. It happens on a task.
///   - UPLOADING A TEXTURE IS NOT. It is a call into the renderer and belongs on the thread that
///     draws, so the task hands over PIXELS and the draw picks them up.
///   - AND A HANDLE HAS TO BE GIVEN BACK. Every monster somebody clicks on would otherwise leave
///     a texture behind - the book has 2792 rows, and browsing is what it is for.
///
/// NOTHING HERE THROWS ON THE DRAW THREAD. An exception escaping a frame ends the session, and
/// every input to this is a file out of a game's bundles - see <see cref="MonsterModels"/>, which
/// answers with a reason rather than an exception for the same reason.
///
/// THE PICTURE IS REDRAWN WHEN SOMETHING CHANGES, not per frame: a new monster, a turn, a tilt or
/// a different size. Holding still costs one textured quad. PLAYING IS THE EXCEPTION, and it is
/// paid for the way a drag is: the picture is drawn one rung of <see cref="PictureLadder"/> lower
/// for as long as it moves, because a rung is about four times the work and thirty of them a
/// second at the resting size is what the rasteriser was never sized for. Stop it and the next
/// frame is drawn full size again.
///
/// THE KEYFRAMES ARE UNPACKED ON A TASK, ONE ANIMATION AT A TIME. A bundled rig holds twelve
/// megabytes of them; the animation being played is a few tens of kilobytes of that, and Oodle
/// on the draw thread is a call this class has no measurement for and does not make.
/// </remarks>
public sealed class MonsterPortrait
{
    /// <summary>How far the mouse drags to turn the model once around.</summary>
    public const float Sweep = 360f;

    /// <summary>How much one notch of the wheel changes the zoom.</summary>
    /// <remarks>
    /// MULTIPLIED AND NOT ADDED, because zoom is a ratio: a fixed step crawls when pulled back and
    /// leaps when pushed in. Notches compound, so the whole range is about eight of them.
    /// </remarks>
    public const float Notch = 1.18f;

    /// <summary>The animation chosen when a monster arrives, where its rig has one by that name.</summary>
    /// <remarks>On 1328 of the install's 1628 rigs, which is more than any other name by far.</remarks>
    public const string Idle = "idle_01";

    /// <summary>What an animation plays at when its header says nothing usable.</summary>
    private const float UsualRate = 30f;

    /// <summary>The id ImGui knows the picture by, which is what makes it something to grab.</summary>
    private const string Grip = "##monster-model";

    /// <summary>
    /// How the picture is handed to the renderer, which is CONTIGUOUS and that is not a preference.
    /// </summary>
    /// <remarks>
    /// THE RENDERER UPLOADS A TEXTURE BY TAKING THE IMAGE'S SINGLE PIXEL SPAN, and ImageSharp's
    /// default allocator splits anything over its four-megabyte pool block across several buffers,
    /// for which that span does not exist. Measured on every rung of the ladder: up to 1024 px,
    /// which is exactly four megabytes, the picture arrives whole; at 1536 and 2048 it arrives
    /// split; with this configuration all seven arrive whole. <see cref="IconCache"/> and
    /// <see cref="TerrainLayer"/> learned the same thing the same way.
    ///
    /// HOW IT SHOWED UP HERE IS WORTH WRITING DOWN, because it hid for half an hour at a time. An
    /// animation is drawn one rung down, which under the usual cap is 512 px and one megabyte, so
    /// playing never split. Pausing goes back up to the rung that covers the pane, and on a pane
    /// wider than 1024 px that is a split image: the upload threw, the picture was dropped, and
    /// with it the controls that could have started the animation again - the viewer was gone
    /// until the app was restarted. The guard test that pins this rule had filtered its files on
    /// the spelling "_upload(", and this class writes "_upload!(".
    ///
    /// A cloned configuration rather than the global default, for the reason IconCache gives.
    /// </remarks>
    private static readonly Configuration Contiguous = Contiguously();

    private readonly PictureLadder _sizes;
    private readonly Func<string, byte[]?>? _install;
    private readonly Func<ReadOnlyMemory<byte>, int, byte[]?>? _unpack;
    private readonly Func<string, Image<Rgba32>, bool, IntPtr>? _upload;
    private readonly Action<string>? _release;

    private string _shown = string.Empty;
    private string _wanted = string.Empty;
    private Task<MonsterModel>? _loading;
    private MonsterModel _model = MonsterModel.None;

    private IntPtr _texture;
    private string _key = string.Empty;
    private int _keys;

    private MeshPicture.Canvas? _canvas;

    private float _turn;
    private float _tilt;
    private float _zoom = 1f;
    private float _drawnTurn = float.NaN;
    private float _drawnTilt = float.NaN;
    private float _drawnZoom = float.NaN;
    private int _drawnSize;
    private bool _drawnGround;
    private float _drawnFrame = float.NaN;
    private int _drawnAnimation = -1;
    private bool _drawnPosed;

    private SkeletonPose? _pose;
    private AnimationTracks? _tracks;
    private Task<AnimationTracks?>? _loadingTracks;
    private int _loadingAnimation = -1;
    private int _chosen = -1;
    private float _frame;
    private bool _playing = true;
    private string _stillWhy = string.Empty;
    private string _cost = string.Empty;
    private Vector3[] _posed = [];
    private Vector3[] _posedNormals = [];

    /// <summary>Whether the model was being dragged last frame, which is what lowers the rung.</summary>
    /// <remarks>
    /// LAST FRAME AND NOT THIS ONE, because the size has to be settled before the button that
    /// reports the drag has been submitted. Being a frame behind costs one frame drawn at the
    /// wrong rung at each end of a drag, which nobody can see; asking in the right order would
    /// mean drawing the picture before knowing how big to draw it.
    /// </remarks>
    private bool _held;

    /// <param name="install">How to read a file out of the game, or null where there is none.</param>
    /// <param name="upload">Hands pixels to the renderer and gives back a handle.</param>
    /// <param name="release">Gives a handle back.</param>
    /// <param name="most">The biggest the model may be drawn, each way. See <see cref="PictureLadder"/>.</param>
    /// <param name="unpack">
    /// The install's Oodle, for keyframes kept in a bundle. Null leaves rigs from version 8 up
    /// standing still; the older ones keep their frames loose and play regardless.
    /// </param>
    public MonsterPortrait(
        Func<string, byte[]?>? install,
        Func<string, Image<Rgba32>, bool, IntPtr>? upload,
        Action<string>? release,
        int most = PictureLadder.Usual,
        Func<ReadOnlyMemory<byte>, int, byte[]?>? unpack = null)
    {
        _install = install;
        _upload = upload;
        _release = release;
        _unpack = unpack;
        _sizes = new PictureLadder(most);
    }

    /// <summary>Whether a picture could be drawn at all - an install and a renderer.</summary>
    public bool Possible => _install is not null && _upload is not null;

    /// <summary>The biggest the model will be drawn, each way.</summary>
    public int Most => _sizes.Most;

    /// <summary>Whether the grid the model stands on is drawn. On, because it is what makes a turn legible.</summary>
    public bool Ground { get; set; } = true;

    /// <summary>Why there is no picture, for the line that says so. Empty while there is one.</summary>
    public string Why { get; private set; } = string.Empty;

    /// <summary>Whether an animation is running. Stays as set across monsters, like the zoom does not.</summary>
    public bool Playing => _playing;

    /// <summary>
    /// Draws the monster, or says why it cannot.
    /// </summary>
    /// <param name="one">The monster to show, or null for none.</param>
    /// <param name="path">Its path, which is what tells one monster from another.</param>
    /// <param name="wide">How wide the pane is. The picture is square and fits inside it.</param>
    public void Draw(MonsterVariety? one, string path, float wide)
    {
        if (!Possible)
        {
            ImGui.TextDisabled("No install to read the model from.");
            return;
        }

        Wanted(one, path);

        // The size is settled BEFORE the picture is taken, because what it is drawn at follows what
        // it will be shown at - and that is only known once the pane's width is.
        // TO THE RENDERER'S OWN LIMIT AND NOT TO THE CAP. The cap says what TURNING may cost; it
        // said nothing about how big the picture may be, and clamping the shown size to it was a
        // picture that stopped growing with its pane however far the boundary was dragged.
        float side = Math.Clamp(wide, 64f, MeshPicture.Widest);
        Finished(side);

        // THE CONTROLS COME BEFORE THE QUESTION OF WHETHER THERE IS A PICTURE, so that a picture
        // which would not upload - it happened, at the rung Pause returns to - leaves the button
        // that plays it again, one rung down, and not only the reason. They draw nothing while
        // there is no skeleton to control, which is every frame the model is still loading.
        Controls(side);

        if (_texture == IntPtr.Zero)
        {
            ImGui.TextDisabled(
                _loading is { IsCompleted: false }
                    ? "reading the model…"
                    : Why.Length > 0 ? ImGuiText.Escape(Why) : "no model");
            Cost();
            return;
        }

        // A BUTTON WITH THE PICTURE PAINTED INTO IT, AND NOT ImGui.Image. An image is an item with
        // NO ID, and ImGui only hands the hover to an item that has one - imgui.cpp's ItemHoverable
        // reads "if (id != 0) SetHoveredID(id)". Two bugs came out of that one fact, and both were
        // reported from the live client:
        //
        //   - IsItemActive compares g.ActiveId against the item's id, so after an image it is
        //     NEVER true and the drag below was unreachable code that read as though it worked.
        //   - With the hover left unclaimed, the sections drawn AFTER this took the click instead.
        //     OverlayLayout.Subsection passes SpanAvailWidth, so a header's hit box runs the whole
        //     width of the pane and straight across the picture: trying to turn the monster
        //     collapsed "Type" instead. A claimed id is also what fixes that, through the guard
        //     "if (g.HoveredId != 0 && g.HoveredId != id) return false" the headers then meet.
        ImGui.InvisibleButton(Grip, new Vector2(side, side));
        ImGui.GetWindowDrawList().AddImage(_texture, ImGui.GetItemRectMin(), ImGui.GetItemRectMax());

        // HELD RATHER THAN HOVERED, so the model keeps turning when the drag runs off the edge of
        // it - which it does constantly, because the picture is small and a full turn is 360 px.
        bool held = ImGui.IsItemActive();
        if (held && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            Vector2 moved = ImGui.GetIO().MouseDelta;
            _turn -= moved.X / Sweep * MathF.Tau;
            _tilt = Math.Clamp(_tilt + (moved.Y / Sweep * MathF.Tau), -MathF.PI / 3f, MathF.PI / 3f);
        }

        _held = held;

        // EVERYTHING THAT ASKS ABOUT "THE ITEM" ASKS BEFORE THE LINE BELOW IS DRAWN, because ImGui
        // answers for the last item submitted and that would then be the line: the wheel's owner
        // is claimed on the picture, not on a string of text.
        if (ImGui.IsItemHovered())
        {
            Wheel();

            // Not while it is being turned: the hint is for somebody who has not yet noticed that
            // the picture moves, and leaving it up trails a label through the gesture it describes.
            if (!held)
            {
                ImGui.SetTooltip(Hint());
            }

            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                _turn = 0f;
                _tilt = 0f;
                _zoom = 1f;
            }
        }

        Cost();
    }

    /// <summary>The line under the picture: what this monster cost to read, and the animation on it.</summary>
    /// <remarks>
    /// ASKED FOR FROM THE LIVE CLIENT after the first half hour of watching animations. A bundled
    /// rig holds up to twelve megabytes of keyframes and the book has 2792 rows, so "what did that
    /// click just read" is a fair question, and the walk already knew the answer. Under the picture
    /// rather than in the tooltip, so it is read without hovering.
    /// </remarks>
    private void Cost()
    {
        if (_cost.Length > 0)
        {
            ImGui.TextDisabled(_cost);
        }
    }

    /// <summary>Rebuilds the line: once when a model lands and once when its keyframes do, not per frame.</summary>
    private void Costed()
    {
        if (_model.Files == 0)
        {
            _cost = string.Empty;
            return;
        }

        string said = $"read {ByteCount.Said(_model.Bytes)} in {_model.Files} file{(_model.Files == 1 ? string.Empty : "s")}";
        if (_tracks is { Ready: true } tracks)
        {
            said += $" · keyframes {ByteCount.Said(tracks.Bytes)}";
        }

        _cost = said;
    }

    /// <summary>
    /// The row above the picture: which animation, and whether it runs.
    /// </summary>
    /// <remarks>
    /// ONLY WHERE THERE IS SOMETHING TO PLAY. A monster with no skeleton, or one whose keyframes
    /// this machine cannot unpack, gets no controls at all and the reason in the tooltip - a
    /// combo box listing nothing would look like the tool had broken rather than the file lacking.
    /// </remarks>
    private void Controls(float side)
    {
        if (_pose is null || !_model.Moves)
        {
            return;
        }

        IReadOnlyList<SkeletonAnimation> moves = _model.Rig.Animations;
        string current = _chosen >= 0 && _chosen < moves.Count ? moves[_chosen].Name : string.Empty;

        ImGuiStylePtr style = ImGui.GetStyle();
        string toggle = _playing ? "Pause" : "Play";
        float button = ImGui.CalcTextSize("Pause").X + (style.FramePadding.X * 2f);
        ImGui.SetNextItemWidth(Math.Max(64f, side - button - style.ItemSpacing.X));

        if (ImGui.BeginCombo("##monster-animation", ImGuiText.Escape(current)))
        {
            for (var one = 0; one < moves.Count; one++)
            {
                if (ImGui.Selectable(ImGuiText.Escape(moves[one].Name) + "##" + one, one == _chosen))
                {
                    Choose(one);
                }
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (ImGui.Button(toggle + "##monster-play", new Vector2(button, 0f)))
        {
            _playing = !_playing;
        }
    }

    /// <summary>Starts on a different animation, from its first frame.</summary>
    private void Choose(int which)
    {
        if (which == _chosen || _pose is null)
        {
            return;
        }

        _chosen = which;
        _frame = 0f;
        _tracks = null;
        _stillWhy = string.Empty;
        Costed();

        IReadOnlyList<SkeletonAnimation> moves = _model.Rig.Animations;
        if (which < 0 || which >= moves.Count)
        {
            return;
        }

        // A BUNDLED RIG WITH NOTHING TO UNPACK IT is the one case decided here rather than by the
        // task: the model cannot know whether this machine has Oodle, and the task would only
        // come back with "the keyframes did not unpack", which is true and says less.
        AnimationSkeleton rig = _model.Rig;
        if (!rig.Loose && _unpack is null)
        {
            _stillWhy = "this rig keeps its keyframes in a bundle, and there is no Oodle here to unpack them";
            return;
        }

        SkeletonAnimation move = moves[which];
        Func<ReadOnlyMemory<byte>, int, byte[]?> unpack = _unpack ?? ((_, _) => null);
        _loadingAnimation = which;
        _loadingTracks = Task.Run(() =>
        {
            byte[]? frames = rig.Tracks(move, unpack);
            return frames is null ? null : AnimationTracks.Read(frames, move.Tracks, rig.Version);
        });
    }

    /// <summary>
    /// Zooms on the wheel, and takes the wheel off the pane underneath while doing it.
    /// </summary>
    /// <remarks>
    /// THE PANE WOULD OTHERWISE SCROLL AWAY UNDER THE MODEL. ImGui gives the wheel to the hovered
    /// WINDOW, not to the hovered item, so without this a notch both zooms and scrolls the detail
    /// pane - and the picture the wheel was aimed at slides off the top.
    ///
    /// SetItemKeyOwner IS WHY THE BUTTON HAD TO BE A BUTTON: it reads g.LastItemData.ID and does
    /// nothing at all for an id of zero, which is what ImGui.Image left behind. Ownership lands in
    /// OwnerCurr immediately but UpdateMouseWheel has already run for this frame inside NewFrame,
    /// so it bites from the NEXT one - claimed on every hovered frame, that only leaves a notch
    /// unclaimed if it arrives on the very first frame the cursor is over the picture.
    /// </remarks>
    private void Wheel()
    {
        ImGui.SetItemKeyOwner(ImGuiKey.MouseWheelY);

        float notches = ImGui.GetIO().MouseWheel;
        if (notches == 0f)
        {
            return;
        }

        _zoom = Math.Clamp(
            _zoom * MathF.Pow(Notch, notches), MeshPicture.Nearest, MeshPicture.Furthest);
    }

    /// <summary>What the tooltip says, which includes why a monster has no colour or does not move.</summary>
    /// <remarks>
    /// THE REASONS ARE HERE BECAUSE A PICTURE CANNOT CARRY THEM. An unpainted model is drawn in a
    /// pale warm grey that reads as bare skin, and a model that holds still looks the same whether
    /// it has no skeleton, a skeleton this machine cannot unpack, or is simply paused - so both
    /// answers are worked out where the files are read and shown here rather than thrown away.
    /// </remarks>
    private string Hint()
    {
        const string Gestures = "Drag to turn. Wheel to zoom. Double-click to reset.";

        string said = Gestures;
        if (_model.Paint.Length > 0)
        {
            said += $"\n\nDrawn in plain ink: {ImGuiText.Escape(_model.Paint)}";
        }

        string still = _model.Move.Length > 0 ? _model.Move : _stillWhy;
        if (still.Length > 0)
        {
            said += $"\n\nHolds still: {ImGuiText.Escape(still)}";
        }

        return said;
    }

    /// <summary>Gives back the texture. Called when the window goes, and when the install changes.</summary>
    public void Forget()
    {
        if (_key.Length > 0)
        {
            _release?.Invoke(_key);
        }

        _texture = IntPtr.Zero;
        _key = string.Empty;
        _shown = string.Empty;
        _model = MonsterModel.None;
        _drawnTurn = float.NaN;
        Rest();
    }

    /// <summary>Drops the skeleton and whatever animation was loaded or playing on it.</summary>
    private void Rest()
    {
        _pose = null;
        _tracks = null;
        _loadingTracks = null;
        _loadingAnimation = -1;
        _chosen = -1;
        _frame = 0f;
        _stillWhy = string.Empty;
        _cost = string.Empty;
        _drawnFrame = float.NaN;
        _drawnAnimation = -1;
    }

    /// <summary>Starts a load when the monster changed, and only then.</summary>
    private void Wanted(MonsterVariety? one, string path)
    {
        if (string.Equals(path, _wanted, StringComparison.Ordinal))
        {
            return;
        }

        _wanted = path;
        _model = MonsterModel.None;
        Why = string.Empty;
        Rest();

        if (one is null || path.Length == 0)
        {
            Drop();
            _loading = null;
            return;
        }

        // A load already running for the previous monster is left to finish and then ignored -
        // cancelling a handful of bundle reads costs more code than letting them land.
        Func<string, byte[]?> read = _install!;
        _loading = Task.Run(() => MonsterModels.Of(read, one));
    }

    /// <summary>Takes a finished load, and re-renders when anything it depends on moved.</summary>
    /// <param name="side">How wide the picture will be shown, which decides what it is drawn at.</param>
    private void Finished(float side)
    {
        if (_loading is { IsCompleted: true } done)
        {
            _model = done.IsCompletedSuccessfully
                ? done.Result

                // A FAULTED TASK IS NOT A CRASH HERE. MonsterModels answers with a reason rather
                // than throwing, so reaching this means something underneath it did - and the
                // window says so instead of the session ending on the next frame.
                : MonsterModel.None with { Why = Said(done.Exception) };

            Why = _model.Why;
            _loading = null;
            _shown = string.Empty;
            _drawnTurn = float.NaN;
            Rigged();
            Costed();
        }

        if (!_model.Ready)
        {
            Drop();
            return;
        }

        Landed();
        Advance();

        // ONE RUNG DOWN WHILE IT IS BEING DRAGGED OR PLAYED, and back up the moment it stops. The
        // work grows with the AREA, so a rung costs about four times the one below it: at the
        // default cap that is 8.0 ms a frame holding still against 4.3 moving, and it is while
        // moving that a dropped frame is felt. What it costs is a picture that looks a little
        // soft, for exactly as long as it moves.
        int size = Wanted(side);
        bool posed = _tracks is { Ready: true } && _pose is not null;

        bool moved = !string.Equals(_shown, _wanted, StringComparison.Ordinal)
            || _drawnTurn != _turn
            || _drawnTilt != _tilt
            || _drawnZoom != _zoom
            || _drawnSize != size
            || _drawnGround != Ground
            || _drawnPosed != posed
            || (posed && (_drawnFrame != _frame || _drawnAnimation != _chosen));

        if (!moved)
        {
            return;
        }

        Render(size, posed);
    }

    /// <summary>Builds the pose for a model that just arrived, and starts its first animation.</summary>
    private void Rigged()
    {
        Rest();
        if (!_model.Moves)
        {
            return;
        }

        _pose = SkeletonPose.Of(_model.Rig);
        if (_pose is null)
        {
            _stillWhy = "the skeleton's bone tree does not hold together";
            return;
        }

        int count = _model.Mesh.Positions.Length;
        if (_posed.Length != count)
        {
            _posed = new Vector3[count];
            _posedNormals = new Vector3[count];
        }

        IReadOnlyList<SkeletonAnimation> moves = _model.Rig.Animations;
        var first = 0;
        for (var one = 0; one < moves.Count; one++)
        {
            if (string.Equals(moves[one].Name, Idle, StringComparison.Ordinal))
            {
                first = one;
                break;
            }
        }

        Choose(first);
    }

    /// <summary>Takes a finished keyframe load, for the animation still chosen.</summary>
    private void Landed()
    {
        if (_loadingTracks is not { IsCompleted: true } done)
        {
            return;
        }

        _loadingTracks = null;
        if (_loadingAnimation != _chosen)
        {
            return;
        }

        AnimationTracks? tracks = done.IsCompletedSuccessfully ? done.Result : null;
        if (tracks is { Ready: true })
        {
            _tracks = tracks;
            _stillWhy = string.Empty;
        }
        else
        {
            _tracks = null;
            _stillWhy = tracks is null
                ? done.IsCompletedSuccessfully ? "the keyframes did not unpack" : Said(done.Exception)
                : tracks.Why.Length > 0 ? tracks.Why : "the keyframes did not read as tracks";
        }

        Costed();
    }

    /// <summary>Moves the animation on by however long the last frame took.</summary>
    /// <remarks>
    /// IN FRAMES OF THE ANIMATION, not of the screen: the keys are timed in the file's own frames
    /// at the rate its header gives, so a 30fps walk plays at the same speed on a 60Hz and a 144Hz
    /// monitor. It wraps rather than stops, because every one of these is a loop or a one-shot
    /// that looks fine looped, and the file carries no flag that says which.
    /// </remarks>
    private void Advance()
    {
        if (!_playing || _tracks is not { Ready: true } tracks || tracks.Frames <= 0f
            || _chosen < 0 || _chosen >= _model.Rig.Animations.Count)
        {
            return;
        }

        float rate = _model.Rig.Animations[_chosen].Rate;
        if (rate <= 0f)
        {
            rate = UsualRate;
        }

        float step = Math.Clamp(ImGui.GetIO().DeltaTime, 0f, 0.25f) * rate;
        _frame += step;
        if (_frame > tracks.Frames)
        {
            _frame %= tracks.Frames;
        }
    }

    /// <summary>What the model is drawn at: the rung that covers the pane, lowered while it moves.</summary>
    private int Wanted(float side)
        => _held || (_playing && _tracks is { Ready: true }) ? _sizes.Dragging(side) : _sizes.For(side);

    /// <summary>The buffers for one size, kept until the size changes.</summary>
    /// <remarks>
    /// REMADE ONLY ON A RUNG CHANGE. The pair is 4.5 MB at the default cap and allocating it per
    /// frame is what the canvas exists to avoid - see <see cref="MeshPicture.Canvas"/>. Rungs are
    /// crossed when a pane is resized past one and when a drag starts or ends, which is rare
    /// enough that the two collections it costs do not show.
    /// </remarks>
    private MeshPicture.Canvas Canvas(int size)
    {
        if (_canvas is not { } had || had.Size != size)
        {
            had = new MeshPicture.Canvas(size);
            _canvas = had;
        }

        return had;
    }

    /// <summary>Draws the mesh - posed, where an animation is loaded - and hands the pixels to the renderer.</summary>
    private void Render(int size, bool posed)
    {
        // WHAT IS BEING DRAWN IS REMEMBERED WHETHER OR NOT IT CAN BE SHOWN. A picture that fails
        // to upload is dropped and its reason shown, and the next frame would otherwise find
        // nothing recorded, draw the same picture again and fail again - 25 ms a frame at the top
        // rung, for as long as the reason stands. Recorded first, a failure holds still until
        // something moves, and the check in Finished then tries again on its own.
        _shown = _wanted;
        _drawnTurn = _turn;
        _drawnTilt = _tilt;
        _drawnZoom = _zoom;
        _drawnSize = size;
        _drawnGround = Ground;
        _drawnPosed = posed;
        _drawnFrame = _frame;
        _drawnAnimation = _chosen;

        try
        {
            // The canvas lends its pixels rather than giving them, and LoadPixelData below copies
            // them into the image straight away - so nothing here outlives the next redraw.
            GamePicture drawn;
            if (posed && _pose is not null && _tracks is not null)
            {
                _pose.Take(_tracks, _frame);
                _pose.Move(_model.Mesh, _posed, _posedNormals);
                drawn = MeshPicture.Of(
                    _model.Mesh, Canvas(size), _posed, _posedNormals, _turn, _tilt, default, _model.Skin, _zoom, Ground);
            }
            else
            {
                drawn = MeshPicture.Of(
                    _model.Mesh, Canvas(size), _turn, _tilt, default, _model.Skin, _zoom, Ground);
            }

            if (!drawn.Ready)
            {
                Why = "the model drew nothing";
                Drop();
                return;
            }

            using var image = Image.LoadPixelData<Rgba32>(Contiguous, drawn.Rgba, drawn.Width, drawn.Height);

            // A NEW KEY EACH TIME, because the renderer caches by key and the pixels change on
            // every turn - reusing one hands back the picture from the first frame forever.
            Drop();
            _key = $"poeformance.monster.{_keys++}";
            _texture = _upload!(_key, image, false);
            Why = string.Empty;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            // The draw thread is the one place an exception costs the whole session, and every
            // input here came out of a game's files.
            Why = $"the model would not draw: {exception.Message}";
            Drop();
        }
    }

    private void Drop()
    {
        if (_key.Length > 0)
        {
            _release?.Invoke(_key);
        }

        _texture = IntPtr.Zero;
        _key = string.Empty;
    }

    private static string Said(AggregateException? fault)
        => fault?.InnerException?.Message ?? fault?.Message ?? "the model could not be read";

    private static Configuration Contiguously()
    {
        Configuration configuration = Configuration.Default.Clone();
        configuration.PreferContiguousImageBuffers = true;
        return configuration;
    }
}
