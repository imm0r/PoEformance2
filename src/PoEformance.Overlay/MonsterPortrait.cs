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
    /// <summary>How many pixels square the model is drawn at, whatever the pane's width.</summary>
    /// <remarks>
    /// FIXED SO THAT DRAGGING DOES NOT RE-RENDER AT A NEW SIZE EVERY FRAME. A pane being resized
    /// changes its width continuously; tying the render to it would redraw the mesh on each of
    /// those frames. ImGui scales the quad, which is free.
    /// </remarks>
    public const int Size = 384;

    /// <summary>How far the mouse drags to turn the model once around.</summary>
    public const float Sweep = 360f;

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

    private float _turn;
    private float _tilt;
    private float _drawnTurn = float.NaN;
    private float _drawnTilt = float.NaN;

    /// <param name="install">How to read a file out of the game, or null where there is none.</param>
    /// <param name="upload">Hands pixels to the renderer and gives back a handle.</param>
    /// <param name="release">Gives a handle back.</param>
    public MonsterPortrait(
        Func<string, byte[]?>? install,
        Func<string, Image<Rgba32>, bool, IntPtr>? upload,
        Action<string>? release)
    {
        _install = install;
        _upload = upload;
        _release = release;
    }

    /// <summary>Whether a picture could be drawn at all - an install and a renderer.</summary>
    public bool Possible => _install is not null && _upload is not null;

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
        Finished();

        if (_texture == IntPtr.Zero)
        {
            ImGui.TextDisabled(
                _loading is { IsCompleted: false }
                    ? "reading the model…"
                    : Why.Length > 0 ? ImGuiText.Escape(Why) : "no model");
            return;
        }

        float side = Math.Clamp(wide, 64f, Size);
        ImGui.Image(_texture, new Vector2(side, side));

        // DRAGGED ON THE PICTURE ITSELF, which is where somebody reaches for it. The item is the
        // image just drawn, so this has to come straight after it.
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            Vector2 moved = ImGui.GetIO().MouseDelta;
            _turn -= moved.X / Sweep * MathF.Tau;
            _tilt = Math.Clamp(_tilt + (moved.Y / Sweep * MathF.Tau), -MathF.PI / 3f, MathF.PI / 3f);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Drag to turn. Double-click to face front again.");
        }

        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            _turn = 0f;
            _tilt = 0f;
        }
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
    private void Finished()
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

        bool moved = !string.Equals(_shown, _wanted, StringComparison.Ordinal)
            || _drawnTurn != _turn
            || _drawnTilt != _tilt;

        if (!moved)
        {
            return;
        }

        Render();
    }

    /// <summary>Draws the mesh and hands the pixels to the renderer.</summary>
    private void Render()
    {
        try
        {
            GamePicture drawn = MeshPicture.Of(_model.Mesh, Size, _turn, _tilt, default, _model.Skin);
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
