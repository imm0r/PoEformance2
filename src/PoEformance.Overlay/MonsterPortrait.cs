using System.Numerics;
using System.Reflection;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.Entities;
using PoEformance.Game.Files;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

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
/// a different size. Holding still costs one textured quad. PLAYING, ORBITING AND DRAGGING ARE
/// THE EXCEPTIONS, and they are paid for alike: at the rung the pane asks for, up to the cap in
/// <see cref="PictureLadder"/>, drawn in bands on every core. It used to be one rung under the
/// cap as well, and on a 1200 px pane that was a monster drawn at 512 and stretched - the blur
/// the live client reported once the floor's own staircase was gone. Stop it and the next frame
/// is drawn at whatever size the pane is, cap or no cap. How many pictures a second that comes
/// to is shown in the pane's own row (<see cref="RedrawRate"/>), because it is the number that
/// says whether the cap is the right one for this machine.
///
/// THE KEYFRAMES ARE UNPACKED ON A TASK, ONE ANIMATION AT A TIME. A bundled rig holds twelve
/// megabytes of them; the animation being played is a few tens of kilobytes of that, and Oodle
/// on the draw thread is a call this class has no measurement for and does not make.
///
/// THE FLOOR IS DRAWN BY THE OVERLAY, NOT INTO THE PICTURE. It used to be, and a picture drawn
/// one rung down and stretched over the pane stretched the floor's one-pixel lines into steps -
/// "so unglaublich pixelig", from the live client. <see cref="ModelFloor"/> works the lines out
/// from the very camera the picture was drawn with, in shares of the side, and <see cref="Strokes"/>
/// writes them into the draw list at the screen's own resolution, anti-aliased, on every frame.
/// Under the picture while the eye is above the floor, and over it while the eye is below - the
/// model is all on one side of the plane, so that order is right at every pixel but a weapon
/// hanging under the feet.
///
/// NOTHING IS SAID IN A TOOLTIP. There was one, with the gestures and the reasons in it, and it
/// was reported as being in the way: it comes up exactly where the pointer is, which while the
/// camera is being placed is over the model. Everything it said is in short lines UNDER the
/// picture now, which are read without pointing at anything and never cover the model; the two
/// widgets that sit over the picture - the orbit button and the animation's progress - are at
/// its top edge, where a model's head seldom reaches, and the button is drawn AFTER the picture
/// so that ImGui's overlap rule gives it the pointer.
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

    /// <summary>How long the orbit takes to carry the camera once around the model, in seconds.</summary>
    /// <remarks>
    /// ASKED FOR AS "constant and rather slow", with no number: this is the number, and it is
    /// what a turntable in a product shot does. Constant in time rather than per frame, so it is
    /// the same lap on a 60 Hz and a 144 Hz monitor, and clamped per frame like the animation
    /// is so that a stalled frame does not spin the model round.
    /// </remarks>
    public const float Orbit = 30f;

    /// <summary>The animation chosen when a monster arrives, where its rig has one by that name.</summary>
    /// <remarks>On 1328 of the install's 1628 rigs, which is more than any other name by far.</remarks>
    public const string Idle = "idle_01";

    /// <summary>What an animation plays at when its header says nothing usable.</summary>
    private const float UsualRate = 30f;

    /// <summary>The id ImGui knows the picture by, which is what makes it something to grab.</summary>
    private const string Grip = "##monster-model";

    /// <summary>The id of the button that starts and stops the orbit.</summary>
    private const string OrbitId = "##monster-orbit";

    /// <summary>The key the renderer holds the orbit button's art under.</summary>
    /// <remarks>Beside the pictures' numbered keys, and never renumbered: one upload for the session.</remarks>
    private const string OrbitKey = "poeformance.monster.orbit";

    /// <summary>The art on the orbit button, by the end of its manifest resource name.</summary>
    private const string OrbitArt = "3dMV_orbit.png";

    /// <summary>The progress bar's width as a share of the picture's side.</summary>
    private const float ProgressShare = 0.4f;

    /// <summary>The most the progress bar is allowed to be, in font sizes: a wide pane does not need a wide bar.</summary>
    private const float ProgressSpan = 14f;

    /// <summary>How big the orbit button's art is, in frame heights.</summary>
    /// <remarks>
    /// THREE, and it got there in two steps from the live client. At one it could not be made out
    /// at all - a drawing of a cube inside a broken circle has detail that nineteen pixels cannot
    /// hold, and over a dark backdrop what is left reads as a smudge rather than as a control. At
    /// two it was legible and still asked to grow by half again, which is this. In frame heights
    /// rather than pixels, so it follows the text size the way every other control does.
    /// </remarks>
    private const float OrbitFaces = 3f;

    /// <summary>The line under the picture that says what the mouse does.</summary>
    private const string Gestures = "drag turns · wheel zooms at the pointer · double-click resets";

    /// <summary>The same line while the orbit runs, when a drag does nothing.</summary>
    private const string Orbiting = "orbiting · the top-right button stops it · no dragging until then";

    /// <summary>The line that says what the floor's squares are.</summary>
    private const string FloorSaid = "floor: 250 units to a tile, ten squares each · x red · y green";

    /// <summary>What the pane is painted before anything else: Blender's viewport grey, 0x3D3D3D.</summary>
    /// <remarks>
    /// THE COLOURS ARE BLENDER'S DEFAULT THEME, read out of its userdef_default_theme.c rather than
    /// eyeballed off a screenshot, because Blender's floor is what the live client asked for by
    /// name. Its 3D viewport background is 0x3D3D3D; its grid is 0x545454 at half alpha and its
    /// emphasised grid the same at full, which <see cref="ModelFloor.Faint"/> carries; and an
    /// axis is the grid colour blended half way to the axis colour - 0xFF3352 for x, 0x8BDC00 for
    /// y - and shaded down by ten, which is its make_axis_color in resources.cc. Packed the way
    /// ImGui packs a colour: alpha in the top byte, red in the bottom one.
    /// </remarks>
    private const uint Backdrop = 0xFF3D3D3D;

    /// <inheritdoc cref="Backdrop"/>
    private const uint Grid = 0x00545454;

    /// <inheritdoc cref="Backdrop"/>
    private const uint AxisX = 0x0049399F;

    /// <inheritdoc cref="Backdrop"/>
    private const uint AxisY = 0x00208E65;

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
    /// animation was drawn one rung down, which under the cap of the day was 512 px and one
    /// megabyte, so playing never split. Pausing went back up to the rung that covers the pane,
    /// and on a pane wider than 1024 px that is a split image: the upload threw, the picture was
    /// dropped, and with it the controls that could have started the animation again - the viewer
    /// was gone until the app was restarted. The guard test that pins this rule had filtered its
    /// files on the spelling "_upload(", and this class writes "_upload!(". Playing draws at the
    /// cap now, and a cap raised past 1024 splits while playing too; the configuration covers it.
    ///
    /// A cloned configuration rather than the global default, for the reason IconCache gives.
    /// </remarks>
    private static readonly Configuration Contiguous = Contiguously();

    private readonly PictureLadder _sizes;
    private readonly Func<string, byte[]?>? _install;
    private readonly Func<ReadOnlyMemory<byte>, int, byte[]?>? _unpack;
    private readonly Func<string, Image<Rgba32>, bool, IntPtr>? _upload;
    private readonly Action<string>? _release;

    /// <summary>How many pictures a second the pane is drawing, for its own row.</summary>
    private readonly RedrawRate _rate = new();

    private string _shown = string.Empty;
    private string _wanted = string.Empty;
    private Task<MonsterModel>? _loading;
    private MonsterModel _model = MonsterModel.None;

    private IntPtr _texture;
    private string _key = string.Empty;
    private int _keys;

    private MeshPicture.Canvas? _canvas;

    /// <summary>The floor's lines for this frame, in a list kept so that no frame allocates one.</summary>
    private readonly List<ModelFloor.Line> _floor = [];

    /// <summary>The lines under the picture this frame, kept for the same reason.</summary>
    private readonly List<string> _status = [];

    /// <summary>A tile in the shown model's own units - see <see cref="ModelFloor.TileOn"/>.</summary>
    private float _tile = ModelFloor.Tile;

    private float _turn;
    private float _tilt;
    private float _zoom = 1f;
    private Vector2 _pan;
    private float _drawnTurn = float.NaN;
    private float _drawnTilt = float.NaN;
    private float _drawnZoom = float.NaN;
    private Vector2 _drawnPan = new(float.NaN);
    private int _drawnSize;
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
    private Vector3[] _posed = [];
    private Vector3[] _posedNormals = [];

    /// <summary>The lines that change when a model or its keyframes land, built then and not per frame.</summary>
    private string _cost = string.Empty;
    private string _paint = string.Empty;
    private string _still = string.Empty;
    private string _count = string.Empty;

    /// <summary>The line for a model that stands in a pit, and the whole unit it was last built for.</summary>
    /// <remarks>
    /// REBUILT ONLY WHEN THE UNIT CHANGES. The lowest point is found on every redraw - one pass
    /// over the posed vertices, which is nothing beside drawing them - but a line is a string,
    /// and a walk that bobs its feet by a fraction of a unit a frame would otherwise allocate one
    /// per frame for a number that reads the same.
    /// </remarks>
    private string _planted = string.Empty;
    private int _plantedAt = int.MinValue;

    /// <summary>The combo's width, and the font size it was measured at.</summary>
    /// <remarks>
    /// AS WIDE AS THE LONGEST NAME IN IT AND NO WIDER, by request; measured once per model rather
    /// than per frame, because a rig can carry two hundred animations and the width does not
    /// change until the names or the font do.
    /// </remarks>
    private float _comboWide;
    private float _comboFont;

    /// <summary>Whether the camera is being carried round the model on its own.</summary>
    private bool _orbiting;

    /// <summary>The orbit button's art, the size it was uploaded at, and whether asking for it failed.</summary>
    /// <remarks>
    /// UPLOADED AT THE SIZE IT IS DRAWN, not at the 350 px it ships at: a button's worth of
    /// pixels sampled one to one is crisp, and the same art shrunk by the renderer's bilinear
    /// filter from twelve times the size is a smear. The size follows the frame height, so a
    /// change of font scale re-uploads it once. Asked for once: a resource that is not there is
    /// a build mistake, and not one to go looking for sixty times a second.
    /// </remarks>
    private IntPtr _orbit;
    private int _orbitPixels;
    private bool _orbitMissing;

    /// <summary>Whether the model was being dragged last frame, which is what caps the rung.</summary>
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

    /// <summary>Whether the floor the model stands on is drawn. On, because it is what makes a turn legible.</summary>
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
    /// <param name="wide">How wide the pane is.</param>
    /// <param name="tall">How tall it is. The picture is square and fits inside both, with its row and its lines.</param>
    public void Draw(MonsterVariety? one, string path, float wide, float tall)
    {
        if (!Possible)
        {
            ImGui.TextDisabled("No install to read the model from.");
            return;
        }

        Wanted(one, path);

        // THE ORBIT MOVES THE CAMERA BEFORE THIS FRAME'S PICTURE IS TAKEN, unlike the drag, which
        // moves it after: a drag is read off the picture, so it cannot come first, and being a
        // frame behind a hand is invisible. The orbit has no such reason, and a picture drawn
        // for the frame before would have the floor - worked out from the camera the picture
        // was drawn with - trailing the turn by a frame too, for no gain.
        if (_orbiting)
        {
            _turn -= Math.Clamp(ImGui.GetIO().DeltaTime, 0f, 0.25f) / Orbit * MathF.Tau;
            if (_turn < -MathF.PI)
            {
                // Kept within one turn, so an orbit left running for an evening does not walk
                // the angle out to where a float can no longer tell one frame from the next.
                _turn += MathF.Tau;
            }
        }

        // THE SIZE IS SETTLED BEFORE THE PICTURE IS TAKEN, because what it is drawn at follows
        // what it will be shown at - and that is the pane less what its row and its lines take,
        // so that nothing under the picture is pushed off the bottom and into a scrollbar. The
        // lines are known before they are drawn, so their height is exact, wrapping included.
        // TO THE RENDERER'S OWN LIMIT AND NOT TO THE CAP. The cap says what MOVING may cost; it
        // said nothing about how big the picture may be, and clamping the shown size to it was a
        // picture that stopped growing with its pane however far the boundary was dragged.
        Lines();
        float side = Math.Clamp(MathF.Min(wide, tall - Chrome(wide)), 64f, MeshPicture.Widest);
        Finished(side);

        // THE CONTROLS COME BEFORE THE QUESTION OF WHETHER THERE IS A PICTURE, so that a picture
        // which would not upload - it happened, at the rung Pause returns to - leaves the button
        // that plays it again, under the cap, and not only the reason. They draw nothing while
        // there is no skeleton to control, which is every frame the model is still loading.
        Controls(side);

        if (_texture == IntPtr.Zero)
        {
            ImGui.TextDisabled(
                _loading is { IsCompleted: false }
                    ? "reading the model…"
                    : Why.Length > 0 ? ImGuiText.Escape(Why) : "no model");
            if (_cost.Length > 0)
            {
                ImGui.TextDisabled(_cost);
            }

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
        //
        // ALLOWED TO BE OVERLAPPED, because two widgets sit on top of it. ImGui's rule for that
        // (ItemHoverable, "AllowOverlap mode requires previous frame HoveredId to be null or to
        // match") is that the LATER item wins the pointer, a frame late - so the button drawn
        // after the picture takes the click that would otherwise have started a drag.
        ImGui.SetNextItemAllowOverlap();
        ImGui.InvisibleButton(Grip, new Vector2(side, side));
        Vector2 below = ImGui.GetCursorScreenPos();

        // THE FLOOR GOES UNDER THE PICTURE OR OVER IT BY WHICH SIDE OF IT THE EYE IS ON - see
        // ModelFloor.Under. From the camera the picture was DRAWN with, not the one being dragged
        // towards: the drag and the wheel below move the camera after this frame's picture was
        // taken, and a floor a frame ahead of its model would slide under the feet on every turn.
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        Vector2 corner = ImGui.GetItemRectMin();
        draw.AddRectFilled(corner, ImGui.GetItemRectMax(), Backdrop);
        MeshPicture.Camera camera = MeshPicture.Camera.Of(_model.Mesh, _drawnTurn, _drawnTilt, _drawnZoom, _drawnPan);
        bool under = ModelFloor.Under(camera);
        if (under)
        {
            Floor(draw, corner, side, camera);
        }

        draw.AddImage(_texture, corner, ImGui.GetItemRectMax());
        if (!under)
        {
            Floor(draw, corner, side, camera);
        }

        // HELD RATHER THAN HOVERED, so the model keeps turning when the drag runs off the edge of
        // it - which it does constantly, because the picture is small and a full turn is 360 px.
        // NOT WHILE ORBITING, by request: the orbit is stopped by its button and by nothing else,
        // so a hand on the picture does nothing until it is.
        bool held = ImGui.IsItemActive() && !_orbiting;
        if (held && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            Vector2 moved = ImGui.GetIO().MouseDelta;
            _turn -= moved.X / Sweep * MathF.Tau;
            _tilt = Math.Clamp(_tilt + (moved.Y / Sweep * MathF.Tau), -MathF.PI / 3f, MathF.PI / 3f);
        }

        _held = held;

        // EVERYTHING THAT ASKS ABOUT "THE ITEM" ASKS BEFORE THE NEXT ITEM IS SUBMITTED, because
        // ImGui answers for the last item and that would then be the orbit button: the wheel's
        // owner is claimed on the picture, not on a button in its corner.
        if (ImGui.IsItemHovered())
        {
            Wheel(side);
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                _turn = 0f;
                _tilt = 0f;
                _zoom = 1f;
                _pan = Vector2.Zero;
            }
        }

        Overlays(corner, side);
        ImGui.SetCursorScreenPos(below);
        Status();
    }

    /// <summary>
    /// The row above the picture: which animation, of how many, and whether it runs, with the
    /// pane's own frame rate before the button.
    /// </summary>
    /// <remarks>
    /// ONLY WHERE THERE IS SOMETHING TO PLAY. A monster with no skeleton, or one whose keyframes
    /// this machine cannot unpack, gets no controls at all and the reason in a line under the
    /// picture - a combo box listing nothing would look like the tool had broken rather than the
    /// file lacking.
    ///
    /// THE LAYOUT IS THE ONE ASKED FOR, item by item: a label, a combo only as wide as its longest
    /// name, the count after it, all at the left; and at the right edge of the picture the rate
    /// and then the button. The rate's width is reserved for three digits so the button does not
    /// shuffle as the number changes, and the right-hand pair never runs back over the left-hand
    /// three on a narrow pane - it goes past the picture's edge instead, where it can still be
    /// read.
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
        Vector2 row = ImGui.GetCursorScreenPos();

        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled("animation");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(ComboWidth(moves, style));
        if (ImGui.BeginCombo("##monster-animation", ImGuiText.Escape(current)))
        {
            for (var one = 0; one < moves.Count; one++)
            {
                bool picked = one == _chosen;
                if (ImGui.Selectable(ImGuiText.Escape(moves[one].Name) + "##" + one, picked))
                {
                    Choose(one);
                }

                // Opens scrolled to the one that is playing rather than to the top, which on a
                // rig with two hundred animations is the difference between a glance and a hunt.
                if (picked)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(_count);

        string toggle = _playing ? "Pause" : "Play";
        float button = ImGui.CalcTextSize("Pause").X + (style.FramePadding.X * 2f);
        float reserved = ImGui.CalcTextSize("999 fps").X;
        ImGui.SameLine();
        float left = MathF.Max(ImGui.GetCursorScreenPos().X, row.X + side - (reserved + style.ItemSpacing.X + button));

        string rate = $"{_rate.At(ImGui.GetTime()):F0} fps";
        ImGui.SetCursorScreenPos(new Vector2(left + reserved - ImGui.CalcTextSize(rate).X, row.Y));
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(rate);

        ImGui.SameLine();
        ImGui.SetCursorScreenPos(new Vector2(left + reserved + style.ItemSpacing.X, row.Y));
        if (ImGui.Button(toggle + "##monster-play", new Vector2(button, 0f)))
        {
            _playing = !_playing;
        }
    }

    /// <summary>The combo's width: its longest name, the frame's padding and the arrow.</summary>
    private float ComboWidth(IReadOnlyList<SkeletonAnimation> moves, ImGuiStylePtr style)
    {
        float font = ImGui.GetFontSize();
        if (_comboFont != font)
        {
            var longest = 0f;
            foreach (SkeletonAnimation move in moves)
            {
                longest = MathF.Max(longest, ImGui.CalcTextSize(move.Name).X);
            }

            // The arrow is a square of the frame's height - BeginCombo's own arrow_size.
            _comboWide = longest + (style.FramePadding.X * 2f) + ImGui.GetFrameHeight();
            _comboFont = font;
        }

        return _comboWide;
    }

    /// <summary>
    /// The two widgets over the picture's top edge: the orbit button at the right, and the
    /// animation's progress in the middle, both the button's height.
    /// </summary>
    /// <remarks>
    /// SUBMITTED AFTER THE PICTURE, which is what gives the button the pointer over it - see the
    /// overlap remark in <see cref="Draw"/>. The bar is not an item anybody can press, so it
    /// claims nothing, and a drag that starts on it turns the model as if it were not there.
    ///
    /// THE BAR'S TEXT IS DRAWN BY HAND IN ITS MIDDLE. ImGui's own overlay rides along beside the
    /// end of the fill (ProgressBar: "fill_br.x + style.ItemSpacing.x"), which for a counter
    /// that runs through every frame of a walk is a number sliding across the bar; in the
    /// middle it holds still and reads as a counter.
    /// </remarks>
    private void Overlays(Vector2 corner, float side)
    {
        ImGuiStylePtr style = ImGui.GetStyle();
        float inset = style.ItemSpacing.X;
        float top = corner.Y + inset;

        // THE TWO ARE SIZED APART AND SHARE ONLY THE LINE THEY HANG FROM. The button had to grow
        // to be recognisable at all - three frame heights by now - while the counter did not, and
        // a bar as tall as the button would be a slab across the top of the picture. So the bar
        // keeps the height it had, a frame and a button's padding, and only the top edges line up.
        float counter = ImGui.GetFrameHeight() + (style.FramePadding.Y * 2f);

        float art = MathF.Round(ImGui.GetFrameHeight() * OrbitFaces);
        IntPtr icon = OrbitIcon((int)art);
        float wide = (icon != IntPtr.Zero ? art : ImGui.CalcTextSize("orbit").X) + (style.FramePadding.X * 2f);
        ImGui.SetCursorScreenPos(new Vector2(corner.X + side - wide - inset, top));

        bool pressed = icon != IntPtr.Zero ? Orbiter(icon, art) : Worded(wide, counter);
        if (pressed)
        {
            _orbiting = !_orbiting;
        }

        if (_tracks is not { Ready: true } tracks || !(tracks.Frames > 0f))
        {
            return;
        }

        // Kept clear of the button at both ends - the bar is centred, so the room it may take is
        // twice the gap to the button. On any pane worth reading this is nowhere near binding.
        float span = MathF.Min(side * ProgressShare, ImGui.GetFontSize() * ProgressSpan);
        span = MathF.Min(span, side - (2f * (wide + (2f * inset))));
        if (!(span > 1f))
        {
            return;
        }

        var at = new Vector2(corner.X + ((side - span) * 0.5f), top);
        var size = new Vector2(span, counter);
        ImGui.SetCursorScreenPos(at);
        ImGui.ProgressBar(_frame / tracks.Frames, size, string.Empty);

        string said = $"{(int)_frame} / {(int)MathF.Ceiling(tracks.Frames)}";
        ImGui.GetWindowDrawList().AddText(
            at + ((size - ImGui.CalcTextSize(said)) * 0.5f), ImGui.GetColorU32(ImGuiCol.Text), said);
    }

    /// <summary>
    /// The orbit button itself: the art alone, with no frame of its own under it.
    /// </summary>
    /// <remarks>
    /// NOTHING LIT WHILE NOBODY IS POINTING AT IT, which is what was asked for: the button sat in
    /// a warm box over the picture, and a box is what the row of controls above wears, not
    /// something that belongs over a model. ImageButtonEx always calls RenderFrame, so the only
    /// way to be rid of the box is to hand it nothing to draw - the button's own colour goes
    /// transparent and the border width goes to zero. The HOVER AND THE PRESS STILL SHOW: those
    /// reach for ImGuiCol_ButtonHovered and ImGuiCol_ButtonActive, which are left alone, so the
    /// control still answers the pointer and only rests invisible.
    ///
    /// AND RUNNING IS SHOWN BY TINTING THE ART rather than by a colour behind it. A background
    /// was the first attempt and it is the wrong mechanism here: the art is an OPAQUE black tile
    /// with a white drawing on it, so anything painted behind reaches only the four rounded
    /// corners. A tint multiplies instead, which leaves the black tile black and turns the
    /// drawing itself the accent - the one part of the picture anybody is looking at.
    /// </remarks>
    private bool Orbiter(IntPtr icon, float art)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f);
        try
        {
            return ImGui.ImageButton(
                OrbitId,
                icon,
                new Vector2(art),
                Vector2.Zero,
                Vector2.One,
                Vector4.Zero,
                _orbiting ? OverlayInk.Accent : Vector4.One);
        }
        finally
        {
            ImGui.PopStyleVar();
            ImGui.PopStyleColor();
        }
    }

    /// <summary>The same button where the art could not be loaded, which keeps its frame - a bare word is not a button.</summary>
    private bool Worded(float wide, float tall)
        => ImGui.Button((_orbiting ? "stop" : "orbit") + OrbitId, new Vector2(wide, tall));

    /// <summary>The orbit button's art, uploaded at the size it is drawn, or nothing where the resource is not there.</summary>
    private IntPtr OrbitIcon(int pixels)
    {
        if (_orbit != IntPtr.Zero && _orbitPixels == pixels)
        {
            return _orbit;
        }

        if (_orbitMissing || pixels <= 0)
        {
            return IntPtr.Zero;
        }

        if (_orbit != IntPtr.Zero)
        {
            _release?.Invoke(OrbitKey);
            _orbit = IntPtr.Zero;
        }

        try
        {
            Assembly assembly = typeof(MonsterPortrait).Assembly;
            string? resource = Array.Find(
                assembly.GetManifestResourceNames(),
                candidate => candidate.EndsWith(OrbitArt, StringComparison.OrdinalIgnoreCase));
            using Stream? stream = resource is null ? null : assembly.GetManifestResourceStream(resource);
            if (stream is null)
            {
                _orbitMissing = true;
                return IntPtr.Zero;
            }

            using Image<Rgba32> image =
                Image.Load<Rgba32>(new DecoderOptions { Configuration = Contiguous }, stream);
            image.Mutate(context => context.Resize(new ResizeOptions
            {
                Size = new Size(pixels, pixels),
                Mode = ResizeMode.Max,
            }));

            // The renderer's precondition, asked rather than discovered - see IconCache.Upload.
            if (!image.DangerousTryGetSinglePixelMemory(out _))
            {
                _orbitMissing = true;
                return IntPtr.Zero;
            }

            _orbit = _upload!(OrbitKey, image, IconCache.Srgb);
            _orbitPixels = pixels;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            // A picture that shipped broken is a build mistake, not something to end a session
            // over; the button falls back to its word.
            _orbitMissing = true;
        }

        return _orbit;
    }

    /// <summary>Gathers this frame's lines under the picture, from strings built when something landed.</summary>
    /// <remarks>
    /// THE GESTURE AND FLOOR LINES ARE COUNTED FROM THE MOMENT A MODEL IS ASKED FOR, not from the
    /// moment its picture arrives: the picture's size is fitted around the lines, and lines that
    /// appeared with the picture would shrink it on the frame it landed, one rung down and
    /// drawn twice.
    /// </remarks>
    private void Lines()
    {
        _status.Clear();
        if (_cost.Length > 0)
        {
            _status.Add(_cost);
        }

        if (!_model.Ready && _loading is null)
        {
            return;
        }

        _status.Add(_orbiting ? Orbiting : Gestures);
        if (Ground)
        {
            _status.Add(FloorSaid);
        }

        if (_paint.Length > 0)
        {
            _status.Add(_paint);
        }

        if (_still.Length > 0)
        {
            _status.Add(_still);
        }

        if (_planted.Length > 0)
        {
            _status.Add(_planted);
        }
    }

    /// <summary>What the row and the lines take off the pane's height, wrapping at its width included.</summary>
    private float Chrome(float wide)
    {
        float chrome = _pose is not null && _model.Moves ? ImGui.GetFrameHeightWithSpacing() : 0f;
        float spacing = ImGui.GetStyle().ItemSpacing.Y;
        foreach (string line in _status)
        {
            // The same font and the same wrap width TextWrapped will use, so the height is the
            // one it will come out at rather than an estimate of it.
            chrome += ImGui.CalcTextSize(line, wide).Y + spacing;
        }

        return chrome;
    }

    /// <summary>The lines under the picture, in the quiet ink the rest of the pane's asides use.</summary>
    private void Status()
    {
        if (_status.Count == 0)
        {
            return;
        }

        ImGui.PushStyleColor(ImGuiCol.Text, OverlayInk.Quiet);
        foreach (string line in _status)
        {
            ImGui.TextWrapped(line);
        }

        ImGui.PopStyleColor();
    }

    /// <summary>Rebuilds the lines that follow a model and its keyframes: once when either lands, not per frame.</summary>
    /// <remarks>
    /// THE COST WAS ASKED FOR FROM THE LIVE CLIENT after the first half hour of watching
    /// animations. A bundled rig holds up to twelve megabytes of keyframes and the book has 2792
    /// rows, so "what did that click just read" is a fair question, and the walk already knew
    /// the answer. THE REASONS ARE HERE BECAUSE A PICTURE CANNOT CARRY THEM: an unpainted model
    /// is drawn in a pale warm grey that reads as bare skin, and a model that holds still looks
    /// the same whether it has no skeleton, a skeleton this machine cannot unpack, or is simply
    /// paused - so both answers are worked out where the files are read and said here rather
    /// than thrown away.
    /// </remarks>
    private void Said()
    {
        if (_model.Files == 0)
        {
            _cost = string.Empty;
        }
        else
        {
            string said = $"read {ByteCount.Said(_model.Bytes)} in {_model.Files} file{(_model.Files == 1 ? string.Empty : "s")}";
            if (_tracks is { Ready: true } tracks)
            {
                said += $" · keyframes {ByteCount.Said(tracks.Bytes)}";
            }

            _cost = said;
        }

        _paint = _model.Paint.Length > 0 ? "plain ink: " + ImGuiText.Escape(_model.Paint) : string.Empty;

        string still = _model.Move.Length > 0 ? _model.Move : _stillWhy;
        _still = still.Length > 0 ? "still: " + ImGuiText.Escape(still) : string.Empty;
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
        Start(which);
        Said();
    }

    /// <summary>Sets the keyframes of one animation loading, or says why they cannot be.</summary>
    private void Start(int which)
    {
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
    private void Wheel(float side)
    {
        ImGui.SetItemKeyOwner(ImGuiKey.MouseWheelY);

        float notches = ImGui.GetIO().MouseWheel;
        if (notches == 0f)
        {
            return;
        }

        float from = _zoom;
        _zoom = Math.Clamp(
            _zoom * MathF.Pow(Notch, notches), MeshPicture.Nearest, MeshPicture.Furthest);

        // TOWARDS THE POINTER AND NOT THE MIDDLE, which was the one thing reported against the
        // wheel. The pointer as a share of the picture from its top left corner; the picture is
        // the last item, so its rectangle is the one ImGui hands back here.
        Vector2 pointer = (ImGui.GetMousePos() - ImGui.GetItemRectMin()) / MathF.Max(side, 1f);
        _pan = MeshPicture.Panned(_pan, pointer, from, _zoom);
    }

    /// <summary>Gives back the textures. Called when the window goes, and when the install changes.</summary>
    public void Forget()
    {
        if (_key.Length > 0)
        {
            _release?.Invoke(_key);
        }

        if (_orbit != IntPtr.Zero)
        {
            _release?.Invoke(OrbitKey);
        }

        _texture = IntPtr.Zero;
        _key = string.Empty;
        _orbit = IntPtr.Zero;
        _orbitPixels = 0;
        _orbitMissing = false;
        _orbiting = false;
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
        _paint = string.Empty;
        _still = string.Empty;
        _count = string.Empty;
        _planted = string.Empty;
        _plantedAt = int.MinValue;
        _comboFont = 0f;
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

        // A pan aimed at one monster's head is nowhere in particular on the next one.
        _pan = Vector2.Zero;

        // The game draws this variety's mesh scaled by its own multiplier, and the floor's tiles
        // have to shrink in the mesh's units by the same amount to stay the game's tiles.
        _tile = ModelFloor.TileOn(one?.ModelSize ?? 0);

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
            Said();
        }

        if (!_model.Ready)
        {
            Drop();
            return;
        }

        Landed();
        Advance();

        // CAPPED WHILE IT IS BEING DRAGGED, ORBITED OR PLAYED, and the pane's own rung the moment
        // it stops. The work grows with the AREA, and it is while moving that a dropped frame is
        // felt, so the cap is somebody's answer to what a moving frame may cost; the bands on
        // every core are what let the usual cap sit at the pane's own size.
        int size = Wanted(side);
        bool posed = _tracks is { Ready: true } && _pose is not null;

        bool moved = !string.Equals(_shown, _wanted, StringComparison.Ordinal)
            || _drawnTurn != _turn
            || _drawnTilt != _tilt
            || _drawnZoom != _zoom
            || _drawnPan != _pan
            || _drawnSize != size
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

        // THE BARE NUMBER IN BRACKETS, because the label before the combo already says what is
        // being counted: "animation [idle_01] 32 animations" says the word twice and reads as
        // two separate facts rather than as one.
        IReadOnlyList<SkeletonAnimation> moves = _model.Rig.Animations;
        _count = $"({moves.Count})";

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

        Said();
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

    /// <summary>What the model is drawn at: the rung that covers the pane, capped while it moves.</summary>
    private int Wanted(float side)
        => _held || _orbiting || (_playing && _tracks is { Ready: true }) ? _sizes.Moving(side) : _sizes.For(side);

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
        _drawnPan = _pan;
        _drawnSize = size;
        _drawnPosed = posed;
        _drawnFrame = _frame;
        _drawnAnimation = _chosen;

        try
        {
            // The canvas lends its pixels rather than giving them, and LoadPixelData below copies
            // them into the image straight away - so nothing here outlives the next redraw.
            GamePicture drawn;
            float lowest;
            if (posed && _pose is not null && _tracks is not null)
            {
                _pose.Take(_tracks, _frame);
                _pose.Move(_model.Mesh, _posed, _posedNormals);
                lowest = Lowest(_posed);
                drawn = MeshPicture.Of(
                    _model.Mesh, Canvas(size), _posed, _posedNormals, _turn, _tilt, default, _model.Skin, _zoom, _pan);
            }
            else
            {
                lowest = _model.Mesh.Most.Z;
                drawn = MeshPicture.Of(
                    _model.Mesh, Canvas(size), _turn, _tilt, default, _model.Skin, _zoom, _pan);
            }

            _rate.Redrawn(ImGui.GetTime());
            Planted(lowest);

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

    /// <summary>Says so, in the lines, when the model as drawn has its lowest point well under the floor.</summary>
    /// <remarks>
    /// THE COLOSSUS FROM THE LIVE CLIENT, whose feet the reader puts five tiles under the floor
    /// and is right to - see <see cref="ModelFloor.Planted"/>. Asked for as a notice and NOT as
    /// a tooltip, and put where the other notices are.
    /// </remarks>
    private void Planted(float lowest)
    {
        int whole = (int)MathF.Round(lowest);
        if (whole == _plantedAt)
        {
            return;
        }

        _plantedAt = whole;
        _planted = ModelFloor.Planted(lowest, _model.Mesh.Most.Z - _model.Mesh.Least.Z);
    }

    /// <summary>The largest z among the points, which is the lowest: the model's up is -z.</summary>
    private static float Lowest(ReadOnlySpan<Vector3> places)
    {
        float lowest = float.NegativeInfinity;
        foreach (Vector3 place in places)
        {
            lowest = MathF.Max(lowest, place.Z);
        }

        return lowest;
    }

    /// <summary>Works out this frame's floor from the camera the picture was drawn with, and draws it.</summary>
    private void Floor(ImDrawListPtr draw, Vector2 corner, float side, in MeshPicture.Camera camera)
    {
        if (!Ground)
        {
            return;
        }

        ModelFloor.Of(_floor, camera, side, _tile);
        Strokes(draw, corner, side, _floor);
    }

    /// <summary>
    /// Writes the floor's pieces into the draw list, each fading from one end to the other.
    /// </summary>
    /// <remarks>
    /// STRAIGHT INTO THE VERTEX BUFFER, and not one AddLine per piece. A line of ImGui's is one
    /// colour from end to end, and the frame's fade is not - so the fade has to be carried by the
    /// vertices, which AddLine does not take. And there are a few thousand pieces on a frame, each
    /// of which through the wrapper is a call across the interop boundary per vertex; one reserve
    /// per piece and a pointer walk is the same picture for a fraction of the frame.
    ///
    /// THE PROFILE IS THE ONE IMGUI DREW THIN LINES WITH before it baked them into its atlas: a
    /// solid centre and a one-pixel feather to nothing on either side, on the white pixel of the
    /// font atlas so that the current texture serves. That is a two-pixel tent holding one pixel's
    /// worth of ink, which is what an anti-aliased line of width one is.
    /// </remarks>
    private static unsafe void Strokes(ImDrawListPtr draw, Vector2 corner, float side, List<ModelFloor.Line> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        Vector2 white = ImGui.GetFontTexUvWhitePixel();
        ImDrawList* list = draw.NativePtr;
        float feather = list->_FringeScale;

        foreach (ModelFloor.Line line in lines)
        {
            Vector2 a = corner + (line.From * side);
            Vector2 b = corner + (line.To * side);
            Vector2 run = b - a;
            float length = run.Length();
            if (!(length > 1e-3f))
            {
                continue;
            }

            Vector2 across = new Vector2(-run.Y, run.X) * (feather / length);
            uint ink = line.Stroke switch
            {
                ModelFloor.Stroke.AxisX => AxisX,
                ModelFloor.Stroke.AxisY => AxisY,
                _ => Grid,
            };

            uint fromInk = ink | ((uint)Math.Clamp(line.FromAlpha * 255f, 0f, 255f) << 24);
            uint toInk = ink | ((uint)Math.Clamp(line.ToAlpha * 255f, 0f, 255f) << 24);

            // Six vertices - the feather's two edges and the centre at each end - and four
            // triangles between them. Reserved per piece, so ImGui's own check for running past
            // sixteen-bit indices runs per piece too.
            draw.PrimReserve(12, 6);
            ImDrawVert* vertex = list->_VtxWritePtr;
            ushort* index = list->_IdxWritePtr;
            uint at = list->_VtxCurrentIdx;

            vertex[0] = new ImDrawVert { pos = a - across, uv = white, col = ink };
            vertex[1] = new ImDrawVert { pos = a, uv = white, col = fromInk };
            vertex[2] = new ImDrawVert { pos = a + across, uv = white, col = ink };
            vertex[3] = new ImDrawVert { pos = b - across, uv = white, col = ink };
            vertex[4] = new ImDrawVert { pos = b, uv = white, col = toInk };
            vertex[5] = new ImDrawVert { pos = b + across, uv = white, col = ink };

            index[0] = (ushort)at;
            index[1] = (ushort)(at + 1);
            index[2] = (ushort)(at + 4);
            index[3] = (ushort)at;
            index[4] = (ushort)(at + 4);
            index[5] = (ushort)(at + 3);
            index[6] = (ushort)(at + 1);
            index[7] = (ushort)(at + 2);
            index[8] = (ushort)(at + 5);
            index[9] = (ushort)(at + 1);
            index[10] = (ushort)(at + 5);
            index[11] = (ushort)(at + 4);

            list->_VtxWritePtr = vertex + 6;
            list->_IdxWritePtr = index + 12;
            list->_VtxCurrentIdx = at + 6;
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
