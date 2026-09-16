using System.Numerics;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.Entities;
using PoEformance.Game.Files;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PoEformance.Overlay;

/// <summary>
/// A monster's model, drawn in a pane and turned with the mouse.
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
/// a different size. Holding still costs one textured quad.
/// </remarks>
public sealed class MonsterPortrait
{
    /// <summary>
    /// The sizes a model is ever drawn at, in pixels each way.
    /// </summary>
    /// <remarks>
    /// A LADDER RATHER THAN THE PANE'S OWN WIDTH, and that is the whole reason it exists. Dragging
    /// a pane's edge changes its width on every frame; a render tied to it would redraw the mesh
    /// for each of those, at a new size, which also throws away the canvas each time. Rungs are
    /// crossed rarely, ImGui scales the quad between them for nothing, and a size that changes
    /// perhaps twice during a resize costs two redraws instead of sixty.
    ///
    /// THEY DOUBLE-ISH ON PURPOSE, so that stepping down one rung while dragging (see
    /// <see cref="Draw"/>) roughly quarters the work rather than shaving a little off it.
    /// </remarks>
    public static readonly int[] Steps = [256, 384, 512, 768, 1024, 1536, MeshPicture.Widest];

    /// <summary>The default cap, and the smallest a cap may be set to.</summary>
    /// <remarks>
    /// 768 MEASURED AGAINST A REAL RIG: 8.0 ms a frame while turning, beside an overlay frame of
    /// about 10. One rung up is 12.5 and two is 25.1, which a drag cannot keep up with - so this
    /// is the largest that stays smooth, and the setting exists for people whose processor or
    /// patience differs. See OverlaySettings.MonsterModelSize.
    /// </remarks>
    public const int Usual = 768;

    /// <inheritdoc cref="Usual"/>
    public const int Smallest = 256;

    /// <summary>How far the mouse drags to turn the model once around.</summary>
    public const float Sweep = 360f;

    /// <summary>How much one notch of the wheel changes the zoom.</summary>
    /// <remarks>
    /// MULTIPLIED AND NOT ADDED, because zoom is a ratio: a fixed step crawls when pulled back and
    /// leaps when pushed in. Notches compound, so the whole range is about eight of them.
    /// </remarks>
    public const float Notch = 1.18f;

    /// <summary>The id ImGui knows the picture by, which is what makes it something to grab.</summary>
    private const string Grip = "##monster-model";

    private readonly int _most;
    private readonly Func<string, byte[]?>? _install;
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
    /// <param name="most">The biggest the model may be drawn, each way. Snapped to a rung of <see cref="Steps"/>.</param>
    public MonsterPortrait(
        Func<string, byte[]?>? install,
        Func<string, Image<Rgba32>, bool, IntPtr>? upload,
        Action<string>? release,
        int most = Usual)
    {
        _install = install;
        _upload = upload;
        _release = release;

        // SNAPPED AT THE DOOR, so that every size used afterwards is a rung and stepping down one
        // while dragging is a lookup rather than arithmetic on a number that might sit between two.
        _most = Rung(Math.Clamp(most, Smallest, MeshPicture.Widest));
    }

    /// <summary>Whether a picture could be drawn at all - an install and a renderer.</summary>
    public bool Possible => _install is not null && _upload is not null;

    /// <summary>The biggest the model will be drawn, each way.</summary>
    public int Most => _most;

    /// <summary>Whether the grid the model stands on is drawn. On, because it is what makes a turn legible.</summary>
    public bool Ground { get; set; } = true;

    /// <summary>Why there is no picture, for the line that says so. Empty while there is one.</summary>
    public string Why { get; private set; } = string.Empty;

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
        float side = Math.Clamp(wide, 64f, _most);
        Finished(side);

        if (_texture == IntPtr.Zero)
        {
            ImGui.TextDisabled(
                _loading is { IsCompleted: false }
                    ? "reading the model…"
                    : Why.Length > 0 ? ImGuiText.Escape(Why) : "no model");
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

        if (!ImGui.IsItemHovered())
        {
            return;
        }

        Wheel();

        // Not while it is being turned: the hint is for somebody who has not yet noticed that the
        // picture moves, and leaving it up trails a label through the gesture it describes.
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

    /// <summary>What the tooltip says, which includes why a monster has no colour on it.</summary>
    /// <remarks>
    /// THE PAINT REASON IS HERE BECAUSE A PICTURE CANNOT CARRY IT. An unpainted model is drawn in a
    /// pale warm grey that reads as bare skin, so "is this one missing its texture" was a question
    /// that could only be settled by reading code - see MonsterModels.Painted, which works the
    /// answer out and used to throw it away.
    /// </remarks>
    private string Hint()
    {
        const string Gestures = "Drag to turn. Wheel to zoom. Double-click to reset.";

        return _model.Paint.Length == 0
            ? Gestures
            : $"{Gestures}\n\nDrawn in plain ink: {ImGuiText.Escape(_model.Paint)}";
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
        }

        if (!_model.Ready)
        {
            Drop();
            return;
        }

        // ONE RUNG DOWN WHILE IT IS BEING DRAGGED, and back up the moment it is let go. The work
        // grows with the AREA, so a rung costs about four times the one below it: at the default
        // cap that is 8.0 ms a frame holding still against 4.3 turning, and it is while turning
        // that a dropped frame is felt. What it costs is a drag that looks a little soft, for
        // exactly as long as the button is down.
        int size = Wanted(side);

        bool moved = !string.Equals(_shown, _wanted, StringComparison.Ordinal)
            || _drawnTurn != _turn
            || _drawnTilt != _tilt
            || _drawnZoom != _zoom
            || _drawnSize != size
            || _drawnGround != Ground;

        if (!moved)
        {
            return;
        }

        Render(size);
    }

    /// <summary>What the model is drawn at: the rung that covers the pane, lowered while dragging.</summary>
    private int Wanted(float side)
    {
        int rung = Rung((int)MathF.Ceiling(side));
        return _held ? Down(rung) : rung;
    }

    /// <summary>The smallest rung that covers what is asked for, never above the cap.</summary>
    private int Rung(int want)
    {
        foreach (int step in Steps)
        {
            if (step >= want)
            {
                return Math.Min(step, _most);
            }
        }

        return _most;
    }

    /// <summary>One rung down, or the bottom one.</summary>
    private static int Down(int from)
    {
        for (int at = Steps.Length - 1; at > 0; at--)
        {
            if (Steps[at] <= from)
            {
                return Steps[at - 1];
            }
        }

        return Steps[0];
    }

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

    /// <summary>Draws the mesh and hands the pixels to the renderer.</summary>
    private void Render(int size)
    {
        try
        {
            // The canvas lends its pixels rather than giving them, and LoadPixelData below copies
            // them into the image straight away - so nothing here outlives the next redraw.
            GamePicture drawn = MeshPicture.Of(
                _model.Mesh, Canvas(size), _turn, _tilt, default, _model.Skin, _zoom, Ground);

            if (!drawn.Ready)
            {
                Why = "the model drew nothing";
                Drop();
                return;
            }

            using var image = Image.LoadPixelData<Rgba32>(drawn.Rgba, drawn.Width, drawn.Height);

            // A NEW KEY EACH TIME, because the renderer caches by key and the pixels change on
            // every turn - reusing one hands back the picture from the first frame forever.
            Drop();
            _key = $"poeformance.monster.{_keys++}";
            _texture = _upload!(_key, image, false);

            _shown = _wanted;
            _drawnTurn = _turn;
            _drawnTilt = _tilt;
            _drawnZoom = _zoom;
            _drawnSize = size;
            _drawnGround = Ground;
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
}
