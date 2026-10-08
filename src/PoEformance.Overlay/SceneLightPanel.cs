using System.Globalization;
using System.Numerics;
using ImGuiNET;
using PoEformance.Core.Diagnostics;
using PoEformance.Features;
using PoEformance.Game.Diagnostics;
using PoEformance.Game.Files;

namespace PoEformance.Overlay;

/// <summary>A sun the model window can move by hand - see <see cref="SceneLightPanel"/>.</summary>
public interface IMovableSun
{
    /// <summary>The way the hand-set sun's light travels, in the picture's space - null while the sun is the environment's.</summary>
    Vector3? Free { get; }

    /// <summary>Sets the sun to shine this way, freeing it from the environment if it was not.</summary>
    /// <param name="travels">The way its light travels, unit length.</param>
    /// <param name="moving">True while the hand is still moving it - the shadows are drawn coarse until it stops.</param>
    void Shine(Vector3 travels, bool moving);
}

/// <summary>
/// The tile book's switches for lighting a picture the game's way: which lights, which environment, and the sky's turn where no file says.
/// </summary>
/// <remarks>
/// EVERY PART HAS ITS OWN SWITCH, as asked: the room's point lights, the sun, its shadows, the player's
/// light, the ambient (none, the picture's flat one, the environment's cube), the exposure and the
/// colour grade - so each can be held against a screenshot from the game on its own.
///
/// THE SUN IS NO LONGER A CHOICE: how phi and theta become its direction was found in the game's
/// memory (<see cref="SceneLight.GameSun"/>), and the row says where it throws the shadows on the
/// game's screen and how high it stands. The sky's turn is still a candidate - its matrix was not
/// found - offered only where it can change the picture and only as the turns that differ. "find the
/// readings" asks the game (<see cref="LightHunt"/>): it checks the sun against the reading drawn,
/// takes a sky turn found alone, and reports what lies beside the sun's vector.
///
/// THE FREE SUN stands anywhere: two sliders - its bearing on the game's screen and its height - and
/// shift + drag in the model window (<see cref="IMovableSun"/>). While it moves its shadows are drawn
/// on a coarse map, and on the full one once it stops.
///
/// THE ENVIRONMENT IS THE AREA'S OWN BY DEFAULT - its WorldAreas row's Environments row - and any .env
/// in the install can be picked instead, for a room seen outside its area. The hunt always asks about
/// the area's own: that is the one the game has loaded.
///
/// THE ROWS WRAP (<see cref="OverlayLayout.Flow"/>): the pane is narrow, and set side by side they ran
/// past its edge - the player light's height slider was there and could not be seen.
/// </remarks>
public sealed class SceneLightPanel : IMovableSun
{
    /// <summary>The player light's radius until a file says what it is - a candidate, see the slider's tooltip.</summary>
    private const float UsualPlayerRadius = 500f;

    /// <summary>How far above the ground the player light stands - a candidate as well.</summary>
    private const float UsualPlayerHeight = 100f;

    /// <summary>How far the player may move before the light follows, so walking does not redraw a room every frame.</summary>
    private const float PlayerStep = 25f;

    /// <summary>Most environments the picker lists for a filter.</summary>
    private const int MostListed = 200;

    /// <summary>The free sun's lowest and highest, in degrees: below the first the ground is all shadow.</summary>
    private const float LowestSun = 2f, HighestSun = 90f;

    /// <summary>What each sun reading means, for its hint and the hunt's report - the panel names them by what they do.</summary>
    private static readonly string[] SunMeanings =
    [
        "theta from straight up, phi round from x; the vector points at the sun",
        "theta as the height above the ground, phi round from x; the vector points at the sun",
        "phi from straight up, theta round from x; the vector points at the sun",
        "phi as the height above the ground, theta round from x; the vector points at the sun",
        "theta from straight up, phi round from x; the vector is the way the light travels",
        "theta as the height above the ground, phi round from x; the vector is the way the light travels",
        "phi from straight up, theta round from x; the vector is the way the light travels",
        "phi as the height above the ground, theta round from x; the vector is the way the light travels",
    ];

    private static readonly string[] Ways = ["up", "up-right", "right", "down-right", "down", "down-left", "left", "up-left"];

    private static readonly string PointShapes = "soft (a = 0, the player light's)\0sharp (a = 1)\0from penumbra_dist\0";

    private static readonly string Ambients = "no ambient\0flat ambient\0cube ambient\0";

    private readonly Func<string, byte[]?>? _read;
    private readonly Func<IReadOnlyList<string>> _environments;
    private readonly Func<string> _areaEnvironment;
    private readonly Func<(float X, float Y, float Ground)?> _player;
    private readonly Func<SceneLight.GroundOnScreen?> _screen;
    private readonly Func<IReadOnlyList<FloatNeedle>, FloatHuntProgress, Task<FloatHuntResult>?>? _hunt;

    private bool _on;
    private bool _points = true;
    private bool _sun = true;
    private bool _shadows = true;
    private bool _playerLight = true;
    private bool _exposure = true;
    private bool _grade = true;
    private int _ambient = (int)SceneAmbient.Cube;
    private int _cubeReading;
    private int _pointShape;
    private float _playerRadius = UsualPlayerRadius;
    private float _playerHeight = UsualPlayerHeight;
    private float _flat = SceneLight.UsualFlatAmbient;
    private string _picked = string.Empty;
    private string _filter = string.Empty;
    private int _version;

    private bool _freeSun;
    private float _freeRound;
    private float _freeHeight = MathF.PI / 4f;
    private bool _moving;

    private string _loadedPath = "\0";
    private EnvironmentSettings _environment = EnvironmentSettings.None;
    private CubeMap? _cube;
    private string _cubeSaid = string.Empty;
    private ColourGrade? _gradeTable;
    private string _gradeSaid = string.Empty;

    private string _labelsFor = "\0";
    private SceneLight.GroundOnScreen _labelsFrame;
    private string _sunSaid = string.Empty;
    private string _cubeItems = string.Empty;
    private float _cubeWidest;
    private int[] _cubeRows = [0];
    private int[] _cubeRowOf = [0, 0, 0, 0, 0];

    private Task<FloatHuntResult>? _hunting;
    private LightHunt? _huntOf;
    private FloatHuntProgress? _huntProgress;
    private LightHuntVerdict? _verdict;
    private string _huntWhy = string.Empty;

    private MonsterModel? _builtModel;
    private int _builtVersion = -1;
    private string _builtPath = string.Empty;
    private Vector3 _builtPlayer = new(float.NaN);
    private SceneLight? _built;

    /// <param name="read">How to read a file out of the install.</param>
    /// <param name="environments">Every .env in the install.</param>
    /// <param name="areaEnvironment">The current area's own .env, or empty.</param>
    /// <param name="player">The player's place in the world and the ground's height under it, or null.</param>
    /// <param name="screen">How the area's ground runs on the game's screen by the live camera, or null where it is not known - see <see cref="SceneLight.GroundOnScreen"/>.</param>
    /// <param name="hunt">Starts a search of the game's memory for these needles, or null where there is no reader that can - see <see cref="FloatHunt"/>.</param>
    public SceneLightPanel(
        Func<string, byte[]?>? read,
        Func<IReadOnlyList<string>> environments,
        Func<string> areaEnvironment,
        Func<(float X, float Y, float Ground)?> player,
        Func<SceneLight.GroundOnScreen?>? screen = null,
        Func<IReadOnlyList<FloatNeedle>, FloatHuntProgress, Task<FloatHuntResult>?>? hunt = null)
    {
        _read = read;
        _environments = environments;
        _areaEnvironment = areaEnvironment;
        _player = player;
        _screen = screen ?? (() => null);
        _hunt = hunt;
    }

    /// <inheritdoc/>
    public Vector3? Free => _on && _sun && _freeSun ? SceneLight.SunToward(_freeRound, _freeHeight) : null;

    /// <inheritdoc/>
    public void Shine(Vector3 travels, bool moving)
    {
        (float round, float height) = SceneLight.SunStands(travels);
        _freeRound = round;
        _freeHeight = Math.Clamp(height, LowestSun * MathF.PI / 180f, HighestSun * MathF.PI / 180f);
        _freeSun = true;
        _on = true;
        _sun = true;
        _moving = moving;
        _version++;
    }

    /// <summary>
    /// The light a model is drawn under, or null for the picture's own - the same object frame after frame while nothing changes, so a redraw is asked for only when something did.
    /// </summary>
    public SceneLight? For(MonsterModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!_on || !model.Ready)
        {
            return null;
        }

        string path = Chosen();
        Load(path);
        Vector3 player = PlayerPlace(model);
        bool moved = float.IsNaN(_builtPlayer.X) || Vector3.Distance(player, _builtPlayer) > PlayerStep;
        if (_built is not null && ReferenceEquals(_builtModel, model) && _builtVersion == _version
            && string.Equals(_builtPath, path, StringComparison.OrdinalIgnoreCase) && !moved)
        {
            return _built;
        }

        EnvironmentSettings env = _environment;

        // A FREE SUN IN AN ENVIRONMENT WITHOUT ONE shines white at one, or there would be nothing to move.
        Vector3 sunColour = !_sun ? Vector3.Zero : env.SunLight != Vector3.Zero ? env.SunLight : _freeSun ? Vector3.One : Vector3.Zero;
        _built = new SceneLight(
            _points ? model.Lights : [],
            (SceneLight.PointShape)_pointShape,
            _playerLight ? new SceneLight.PlayerLamp(player, env.PlayerLight, _playerRadius) : null)
        {
            SunColour = sunColour,
            SunDirection = _freeSun ? SceneLight.SunToward(_freeRound, _freeHeight) : SceneLight.SunFrom(env.Phi ?? 0f, env.Theta ?? 0f, SceneLight.GameSun),
            SunShadows = _shadows,
            ShadowSide = _moving ? ShadowMap.Coarse : ShadowMap.Usual,
            Ambient = (SceneAmbient)_ambient,
            FlatAmbient = _flat,
            Cube = _cube,
            CubeTurn = SceneLight.CubeTurnFrom(env.HorAngle ?? 0f, env.VertAngle ?? 0f, (SceneLight.CubeReading)_cubeReading),
            CubeBrightness = env.EnvBrightness ?? 1f,
            DirectLightEnvRatio = env.DirectLightEnvRatio ?? 0f,
            GiEnvOcclusion = Math.Clamp(env.GiEnvOcclusion ?? 0f, 0f, 1f),
            Exposure = _exposure ? MathF.Max(1f, env.Exposure ?? 1f) : 1f,
            Grade = _grade ? _gradeTable : null,
        };
        _builtModel = model;
        _builtVersion = _version;
        _builtPath = path;
        _builtPlayer = player;
        return _built;
    }

    /// <summary>The panel: the switches, the environment picker, the readings and what they came to - under a fold the book draws.</summary>
    public void Draw(MonsterModel? model)
    {
        if (ImGui.Checkbox("game light##scene-on", ref _on))
        {
            _version++;
        }

        OverlayLayout.Hint("Lights the picture the game's way, from the area's .env and the room's own lights, instead of the picture's lamp."
            + " Every part below has its own switch, so each can be held against a screenshot from the game.");
        Finished();
        if (!_on)
        {
            return;
        }

        string path = Chosen();
        Load(path);
        SceneLight.GroundOnScreen frame = Frame();
        Labels(path, frame);

        // A LABEL COLUMN, so the rows read as what they are, and every row wraps at the pane's edge
        // back to that column rather than running past it.
        float column = ImGui.CalcTextSize("environment").X + (ImGui.GetStyle().ItemSpacing.X * 3f);
        float right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;

        float start = Label("lights", column);
        var row = new Row(right, start);
        Switch("room##scene-points", ref _points, "The point lights the room's doodads carry in their .ao Lights blocks, the default state of each.", row, "sun");
        Switch("sun##scene-sun", ref _sun, "directional_light: its colour times its multiplier. Seepage's is nought.", row, "shadows");
        Switch("shadows##scene-shadows", ref _shadows,
            "The sun's shadows: the room drawn once more along the sun's light, and a pixel is shaded where something nearer the sun covers it.", row, "player");
        Switch("player##scene-player", ref _playerLight,
            "player_light: its colour times its intensity, a point light at the player's feet in a laid room, else over the middle of the picture.", row, "exposure");
        Switch("exposure##scene-exposure", ref _exposure, "camera.exposure: the colour times max(1, exposure), the game's own tone mapping for PoE2.", row, "colour grade");
        Switch("colour grade##scene-grade", ref _grade, "post_transform: the environment's 3D colour table, applied after the exposure the way the game's post process applies it"
            + " - looked up by the colour with a gamma of 2.2 taken off, giving back linear light.", row, null);

        start = Label("ambient", column);
        ImGui.SetNextItemWidth(ComboWidth("cube ambient"));
        if (ImGui.Combo("##scene-ambient", ref _ambient, Ambients))
        {
            _version++;
        }

        float flatWidth = ImGui.GetFontSize() * 7f;
        OverlayLayout.Flow(ImGui.CalcTextSize("flat").X + ImGui.GetStyle().ItemSpacing.X + flatWidth, right, start);
        OverlayLayout.Hint("Where the light that comes from everywhere comes from.\n"
            + "cube: the environment's diffuse cube, by the turned normal, times env_brightness - the game's way.\n"
            + "flat: the picture's own, the same from every direction.\n"
            + "Where the .env gives gi_env_occlusion, that share of the cube is the game's GI instead, which is not drawn - the flat ambient stands in for it.");
        Named("flat");
        ImGui.SetNextItemWidth(flatWidth);
        if (ImGui.SliderFloat("##scene-flat", ref _flat, 0f, 0.5f, "%.3f"))
        {
            _version++;
        }

        OverlayLayout.Hint("The flat ambient's light, linear. The picture's usual is 0.04 - 0.22 on the screen's value.");

        Label("environment", column);
        Picker(path);

        start = Label("sun", column);
        SunRow(right, start, frame);

        Label("sky", column);
        SkyRow();

        start = Label("lamps", column);
        Lamps(right, start);

        start = Label("in the game", column);
        HuntRow(right, start);

        Said(model, frame);
    }

    /// <summary>A row's label in the label column, the row's controls after it - and where those start, for a row that wraps.</summary>
    private static float Label(string text, float column)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(text);
        ImGui.SameLine(column);
        return ImGui.GetCursorPosX();
    }

    /// <summary>A combo just wide enough for its widest item, or as wide as is left where that is less.</summary>
    private static void Fitted(float widest, float right)
    {
        float room = right - ImGui.GetCursorScreenPos().X;
        ImGui.SetNextItemWidth(MathF.Max(ImGui.GetFontSize() * 6f, MathF.Min(widest, room)));
    }

    private static float ComboWidth(string widest)
        => ImGui.CalcTextSize(widest).X + ImGui.GetFrameHeight() + (ImGui.GetStyle().FramePadding.X * 2f) + ImGui.GetStyle().ItemInnerSpacing.X;

    /// <summary>
    /// A switch, then the place of the next one - a checkbox labelled <paramref name="next"/>, beside it or on the row's next line - then its hint.
    /// </summary>
    /// <remarks>In that order, so the hint's tooltip can never be what the next one's place is measured from.</remarks>
    private void Switch(string label, ref bool value, string hint, Row row, string? next)
    {
        if (ImGui.Checkbox(label, ref value))
        {
            _version++;
        }

        if (next is not null)
        {
            OverlayLayout.Flow(OverlayLayout.CheckboxWidth(next), row.Right, row.Start);
        }

        OverlayLayout.Hint(hint);
    }

    /// <summary>A row that wraps at its right edge back to where its controls start.</summary>
    private readonly record struct Row(float Right, float Start);

    /// <summary>The environment: the area's own, or one picked from the install's by a filter - its file's name, the path on hover.</summary>
    private void Picker(string chosen)
    {
        string shown = chosen.Length > 0 ? chosen[(chosen.Replace('\\', '/').LastIndexOf('/') + 1)..] : "(none known - pick one)";
        ImGui.SetNextItemWidth(Math.Clamp(ImGui.GetContentRegionAvail().X - 90f, 120f, 300f));
        if (ImGui.BeginCombo("##scene-env", ImGuiText.Escape(shown)))
        {
            ImGui.SetNextItemWidth(320f);
            ImGui.InputTextWithHint("##scene-env-filter", "filter...", ref _filter, 128);
            int listed = 0;
            foreach (string one in _environments())
            {
                if (_filter.Length > 0 && one.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                if (++listed > MostListed)
                {
                    ImGui.TextDisabled("(more - narrow the filter)");
                    break;
                }

                if (ImGui.Selectable(ImGuiText.Escape(one), string.Equals(one, _picked, StringComparison.OrdinalIgnoreCase)))
                {
                    _picked = one;
                    _version++;
                }
            }

            ImGui.EndCombo();
        }

        if (chosen.Length > 0 && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(ImGuiText.Escape(chosen));
        }

        ImGui.SameLine();
        if (_picked.Length > 0)
        {
            if (ImGui.SmallButton("area's##scene-area"))
            {
                _picked = string.Empty;
                _version++;
            }

            OverlayLayout.Hint("Back to the area's own environment - its WorldAreas row's.");
        }
        else
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled("the area's");
        }
    }

    /// <summary>
    /// The sun: the environment's by a reading named for where it throws the shadows, or a free one set by its bearing and height.
    /// </summary>
    private void SunRow(float right, float start, SceneLight.GroundOnScreen frame)
    {
        if (!_freeSun)
        {
            ImGui.AlignTextToFramePadding();
            ImGuiText.Wrapped(OverlayInk.Quiet, ImGuiText.Escape(_sunSaid));
            OverlayLayout.Hint("The environment's sun as the game reads its angles: phi is its height above the ground, theta its bearing -"
                + " found in the game's memory, where that one reading's vector was kept and no other's (SceneLight.GameSun).");
            ImGui.SetCursorPosX(start);
        }

        if (ImGui.Checkbox("free sun##scene-free", ref _freeSun))
        {
            if (_freeSun)
            {
                // FROM WHERE THE ENVIRONMENT'S STANDS, so freeing it moves nothing until it is moved.
                EnvironmentSettings env = _environment;
                (_freeRound, float stands) = SceneLight.SunStands(SceneLight.SunFrom(env.Phi ?? 0f, env.Theta ?? 0f, SceneLight.GameSun));
                _freeHeight = stands * 180f / MathF.PI is >= LowestSun and <= HighestSun ? stands : MathF.PI / 4f;
            }

            _moving = false;
            _version++;
        }

        float sliderWidth = ImGui.GetFontSize() * 5.5f;
        if (_freeSun)
        {
            OverlayLayout.Flow(Slid("from", sliderWidth), right, start);
        }

        OverlayLayout.Hint("A sun of your own, anywhere: its bearing on the game's screen and its height, or shift + drag in the model window"
            + " - across turns it round, up and down raises and lowers it. Its shadows are coarse while it moves and sharp once it stops."
            + " Where the environment has no sun it shines white.");
        if (!_freeSun)
        {
            return;
        }

        Vector2 way = frame.Way(new Vector3(MathF.Cos(_freeRound), MathF.Sin(_freeRound), 0f));
        float bearing = SceneLight.GroundOnScreen.Bearing(way);
        float height = _freeHeight * 180f / MathF.PI;
        Named("from");
        ImGui.SetNextItemWidth(sliderWidth);
        if (ImGui.SliderFloat("##scene-free-from", ref bearing, 0f, 360f, "%.0f°"))
        {
            Vector2 ground = frame.Ground(SceneLight.GroundOnScreen.OfBearing(bearing));
            _freeRound = MathF.Atan2(ground.Y, ground.X);
            _version++;
        }

        OverlayLayout.Flow(Slid("height", sliderWidth), right, start);
        OverlayLayout.Hint("Where the sun stands as the game's screen shows it: nought at the top, ninety on the right, clockwise. The shadows fall the other way.");
        Named("height");
        ImGui.SetNextItemWidth(sliderWidth);
        if (ImGui.SliderFloat("##scene-free-height", ref height, LowestSun, HighestSun, "%.0f°"))
        {
            _freeHeight = height * MathF.PI / 180f;
            _version++;
        }

        OverlayLayout.Flow(ImGui.CalcTextSize("area's sun").X + (ImGui.GetStyle().FramePadding.X * 2f), right, start);
        OverlayLayout.Hint("How high the sun stands above the ground: ninety is straight overhead.");
        if (ImGui.SmallButton("area's sun##scene-free-off"))
        {
            _freeSun = false;
            _moving = false;
            _version++;
        }

        OverlayLayout.Hint("Back to the environment's sun, by the reading picked.");
    }

    /// <summary>The room lights' core and the player light's two numbers no file gives.</summary>
    private void Lamps(float right, float start)
    {
        Named("core");
        ImGui.SetNextItemWidth(ComboWidth("soft (a = 0, the player light's)"));
        if (ImGui.Combo("##scene-core", ref _pointShape, PointShapes))
        {
            _version++;
        }

        float sliderWidth = ImGui.GetFontSize() * 4.5f;
        OverlayLayout.Flow(Slid("player radius", sliderWidth), right, start);
        OverlayLayout.Hint("How sharp a room light's core is - the shader's light_position_data.a, lerp(1/sqrt(0.02), 100, a), which no line in a Lights"
            + " block is named for. It changes the light near the lamp, hardly at all past its radius.");
        Named("player radius");
        ImGui.SetNextItemWidth(sliderWidth);
        if (ImGui.SliderFloat("##scene-player-radius", ref _playerRadius, 50f, 2000f, "%.0f"))
        {
            _version++;
        }

        OverlayLayout.Flow(Slid("height", sliderWidth), right, start);
        OverlayLayout.Hint("The player light's radius. No file read so far gives it - a candidate to match against a screenshot.");
        Named("height");
        ImGui.SetNextItemWidth(sliderWidth);
        if (ImGui.SliderFloat("##scene-player-height", ref _playerHeight, 0f, 500f, "%.0f"))
        {
            _version++;
        }

        OverlayLayout.Hint("How far above the ground the player light stands. Not in any file read so far either.");
    }

    /// <summary>The hunt: a button that asks the game which readings it holds, what it is doing, and what it found.</summary>
    private void HuntRow(float right, float start)
    {
        bool running = _hunting is { IsCompleted: false };
        ImGui.BeginDisabled(_hunt is null || running);
        if (ImGui.SmallButton("find the readings##scene-hunt"))
        {
            StartHunt();
        }

        ImGui.EndDisabled();
        string said = running
            ? string.Create(CultureInfo.InvariantCulture, $"looking... {(_huntProgress?.Bytes ?? 0) / (1024.0 * 1024 * 1024):0.00} GB")
            : _huntWhy.Length > 0 ? _huntWhy
            : _verdict?.Summary ?? string.Empty;
        if (said.Length > 0)
        {
            OverlayLayout.Flow(ImGui.CalcTextSize(said).X, right, start);
        }

        OverlayLayout.Hint(_hunt is null
            ? "Not attached to the game's memory in a way that can search it."
            : "Works out every sun reading's vector and every sky turn from the area's own .env and searches the game's memory for them - the game"
                + " keeps the one it uses. The sun is checked against the reading the picture draws; a sky turn found alone is taken. A few seconds.");
        if (said.Length == 0)
        {
            return;
        }

        ImGuiText.Wrapped(OverlayInk.Quiet, ImGuiText.Escape(said));
        if (!running && _verdict is { } verdict)
        {
            OverlayLayout.Flow(ImGui.CalcTextSize("copy report").X + (ImGui.GetStyle().FramePadding.X * 2f), right, start);
            if (ImGui.SmallButton("copy report##scene-hunt-copy"))
            {
                ImGui.SetClipboardText(verdict.Report);
            }

            OverlayLayout.Hint("The whole report - every reading, where each was found, the raw angles beside them - for pasting.");
        }
    }

    private void StartHunt()
    {
        _verdict = null;
        _huntWhy = string.Empty;
        string path = _areaEnvironment();
        if (path.Length == 0 || _read is null || _hunt is null)
        {
            _huntWhy = path.Length == 0 ? "the area's environment is not known" : "nothing to read or search with";
            return;
        }

        EnvironmentSettings env = string.Equals(path, _loadedPath, StringComparison.OrdinalIgnoreCase)
            ? _environment
            : EnvironmentSettings.Read(path, _read(path.Replace('\\', '/').Trim()));
        LightHunt? asked = LightHunt.For(path, env, SunReported, out string why);
        if (asked is null)
        {
            _huntWhy = why;
            return;
        }

        var progress = new FloatHuntProgress();
        Task<FloatHuntResult>? task = _hunt(asked.Needles, progress);
        if (task is null)
        {
            _huntWhy = "the search could not start";
            return;
        }

        _huntOf = asked;
        _huntProgress = progress;
        _hunting = task;
    }

    /// <summary>Takes a finished hunt's answer - the readings it found, where there was exactly one each.</summary>
    private void Finished()
    {
        if (_hunting is not { IsCompleted: true } done || _huntOf is not { } asked)
        {
            return;
        }

        _hunting = null;
        _huntOf = null;
        if (!done.IsCompletedSuccessfully)
        {
            _huntWhy = "the search failed: " + (done.Exception?.GetBaseException().Message ?? "cancelled");
            return;
        }

        _verdict = asked.Read(done.Result);
        if (_verdict.Cube is { } cube)
        {
            _cubeReading = cube;
            _version++;
        }
    }

    /// <summary>A sun reading as the hunt's report names it: what the panel calls it and what it means.</summary>
    private string SunReported(int reading)
        => string.Create(CultureInfo.InvariantCulture, $"reading {reading + 1} ({SunMeanings[Math.Clamp(reading, 0, SunMeanings.Length - 1)]})");

    /// <summary>How wide a slider is with its name before it.</summary>
    private static float Slid(string name, float width) => ImGui.CalcTextSize(name).X + ImGui.GetStyle().ItemSpacing.X + width;

    /// <summary>A control's name before it, quiet.</summary>
    private static void Named(string text)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(text);
        ImGui.SameLine();
    }

    /// <summary>How the ground runs on the game's screen: the live camera's where there is one, else the map's.</summary>
    private SceneLight.GroundOnScreen Frame() => _screen() is { Ready: true } live ? live : SceneLight.GroundOnScreen.Map;

    /// <summary>
    /// The readings' names for this environment and screen: each sun reading by where its shadows fall and how high its sun stands, each sky reading by its turn in degrees.
    /// </summary>
    /// <remarks>
    /// MADE ONCE PER ENVIRONMENT AND CAMERA, not per frame: they are strings, and the camera does not
    /// move in this game.
    /// </remarks>
    private void Labels(string path, SceneLight.GroundOnScreen frame)
    {
        if (string.Equals(path, _labelsFor, StringComparison.OrdinalIgnoreCase) && Near(frame, _labelsFrame))
        {
            return;
        }

        _labelsFor = path;
        _labelsFrame = frame;
        EnvironmentSettings env = _environment;
        _sunSaid = env.Phi is { } phi && env.Theta is { } theta
            ? env.SunLight == Vector3.Zero
                ? "the environment's sun has no light (its multiplier is nought)"
                : Throws(SceneLight.SunFrom(phi, theta, SceneLight.GameSun), frame)
            : "the environment gives no sun angles";

        // ONLY THE TURNS THAT DIFFER: with one of the two angles nought the four orders come to one or
        // two turns, and offering four rows that draw the same picture read as a switch that did nothing.
        float horizontal = env.HorAngle ?? 0f, vertical = env.VertAngle ?? 0f;
        float h = horizontal * 180f / MathF.PI, v = vertical * 180f / MathF.PI;
        var turns = new List<Matrix4x4> { Matrix4x4.Identity };
        var rows = new List<int> { 0 };
        var labels = new List<string> { "not turned" };
        int cubes = Enum.GetValues<SceneLight.CubeReading>().Length;
        _cubeRowOf = new int[cubes];
        for (var reading = 1; reading < cubes; reading++)
        {
            Matrix4x4 turn = SceneLight.CubeTurnFrom(horizontal, vertical, (SceneLight.CubeReading)reading);
            int same = turns.FindIndex(one => Near(one, turn));
            if (same >= 0)
            {
                _cubeRowOf[reading] = same;
                continue;
            }

            _cubeRowOf[reading] = turns.Count;
            turns.Add(turn);
            rows.Add(reading);
            bool aboutX = (SceneLight.CubeReading)reading is SceneLight.CubeReading.ZThenX or SceneLight.CubeReading.XThenZ;
            bool turnFirst = (SceneLight.CubeReading)reading is SceneLight.CubeReading.ZThenX or SceneLight.CubeReading.ZThenY;
            string tip = string.Create(CultureInfo.InvariantCulture, $"tipped {v:0}° about {(aboutX ? "x" : "y")}");
            string round = string.Create(CultureInfo.InvariantCulture, $"turned {h:0}° round");
            string how = vertical == 0f ? round : horizontal == 0f ? tip : turnFirst ? $"{round}, then {tip}" : $"{tip}, then {round}";
            labels.Add(string.Create(CultureInfo.InvariantCulture, $"{how} (reading {reading + 1})"));
        }

        _cubeRows = [.. rows];
        _cubeItems = string.Join('\0', labels) + "\0";
        _cubeWidest = labels.Max(one => ComboWidth(one));
    }

    /// <summary>
    /// The sky: the turns the environment's two angles can give it, or why there is nothing to choose - a cube that lights nothing here, or no turn at all.
    /// </summary>
    private void SkyRow()
    {
        EnvironmentSettings env = _environment;
        string why = _ambient != (int)SceneAmbient.Cube ? "not used - the ambient is not the cube"
            : _cube is null ? "no sky cube read - see details"
            : env.GiEnvOcclusion is >= 1f ? string.Create(CultureInfo.InvariantCulture,
                $"lights nothing here: gi_env_occlusion {env.GiEnvOcclusion:0.##} leaves all the light from around to the game's own GI, which is not drawn - the flat ambient stands in")
            : _cubeRows.Length < 2 ? "not turned - the environment gives no hor_angle or vert_angle"
            : string.Empty;
        if (why.Length > 0)
        {
            ImGui.AlignTextToFramePadding();
            ImGuiText.Wrapped(OverlayInk.Quiet, ImGuiText.Escape(why));
            return;
        }

        int row = Math.Clamp(_cubeRowOf[Math.Clamp(_cubeReading, 0, _cubeRowOf.Length - 1)], 0, _cubeRows.Length - 1);
        Fitted(_cubeWidest, ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X);
        if (ImGui.Combo("##scene-cube-reading", ref row, _cubeItems))
        {
            _cubeReading = _cubeRows[Math.Clamp(row, 0, _cubeRows.Length - 1)];
            _version++;
        }

        OverlayLayout.Hint("The sky is the light that comes from all around - the environment's cube - and the environment turns it by two angles:"
            + " hor_angle round the up axis, vert_angle tipped over. In which order, and about which axis it tips, is worked out on the processor and"
            + " written in no file, so the turns those orders give are offered - only the ones that differ. \"find the readings\" asks the game.");
    }

    /// <summary>What a sun shining this way does on the game's screen: where the shadows fall, and how high it stands.</summary>
    private static string Throws(Vector3 travels, SceneLight.GroundOnScreen frame)
    {
        float height = MathF.Asin(Math.Clamp(travels.Z, -1f, 1f)) * 180f / MathF.PI;
        if (height <= 0f)
        {
            return string.Create(CultureInfo.InvariantCulture, $"sun {-height:0}° below the ground - no sunlight");
        }

        Vector2 way = frame.Way(travels);
        return way == Vector2.Zero
            ? string.Create(CultureInfo.InvariantCulture, $"sun straight overhead")
            : string.Create(CultureInfo.InvariantCulture, $"shadows fall {Way(way)} ({SceneLight.GroundOnScreen.Bearing(way):0}°), sun {height:0}° high");
    }

    /// <summary>A screen way in words, by the nearest of eight.</summary>
    private static string Way(Vector2 way)
    {
        float bearing = SceneLight.GroundOnScreen.Bearing(way);
        return Ways[(int)MathF.Round(bearing / 45f) % Ways.Length];
    }

    private static bool Near(SceneLight.GroundOnScreen a, SceneLight.GroundOnScreen b)
        => Vector2.DistanceSquared(a.X, b.X) < 1e-6f && Vector2.DistanceSquared(a.Y, b.Y) < 1e-6f;

    private static bool Near(Matrix4x4 a, Matrix4x4 b)
        => MathF.Abs(a.M11 - b.M11) + MathF.Abs(a.M12 - b.M12) + MathF.Abs(a.M13 - b.M13)
            + MathF.Abs(a.M21 - b.M21) + MathF.Abs(a.M22 - b.M22) + MathF.Abs(a.M23 - b.M23)
            + MathF.Abs(a.M31 - b.M31) + MathF.Abs(a.M32 - b.M32) + MathF.Abs(a.M33 - b.M33) < 1e-5f;

    /// <summary>
    /// What the light came to - the environment's numbers, what was assumed, the lights, the cube, the grade, the hunt's report - folded under a one-line summary.
    /// </summary>
    /// <remarks>
    /// FOLDED, because these are what to read when the picture and the game disagree, not while
    /// switching: open, they were a paragraph longer than the switches above them.
    /// </remarks>
    private void Said(MonsterModel? model, SceneLight.GroundOnScreen frame)
    {
        EnvironmentSettings env = _environment;
        var lines = new List<string> { env.Said() };
        IReadOnlyList<string> assumed = env.Ready ? env.Assumed() : [];
        if (assumed.Count > 0)
        {
            lines.Add("assumed: " + string.Join("; ", assumed));
        }

        string sunSaid = !env.Ready ? "no environment" : env.SunLight == Vector3.Zero && !_freeSun ? "no sun" : "sun";
        if (_sun && (_freeSun || (env.Ready && env.SunLight != Vector3.Zero)))
        {
            Vector3 travels = _freeSun ? SceneLight.SunToward(_freeRound, _freeHeight) : SceneLight.SunFrom(env.Phi ?? 0f, env.Theta ?? 0f, SceneLight.GameSun);
            float height = MathF.Asin(Math.Clamp(travels.Z, -1f, 1f)) * 180f / MathF.PI;
            sunSaid = string.Create(CultureInfo.InvariantCulture, $"{(_freeSun ? "free sun" : "sun")} {height:0}° high{(height < 0f ? " - BELOW the ground" : string.Empty)}");
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"{(_freeSun ? "free sun" : "sun")}: light travels {travels.X:0.##} {travels.Y:0.##} {travels.Z:0.##} - {Throws(travels, frame)}"
                + $" on the screen{(_screen() is { Ready: true } ? string.Empty : " (by the map's transform - no live camera)")}"));
            if (model?.AreaOrigin is null)
            {
                lines.Add("sun: this room is drawn as its file has it - turned however the area turned it, so the picture's shadows may not fall as the screen's do");
            }
        }

        if (_ambient == (int)SceneAmbient.Cube)
        {
            lines.Add(_cubeSaid);
            if (env.GiEnvOcclusion is > 0f)
            {
                lines.Add(string.Create(CultureInfo.InvariantCulture,
                    $"gi_env_occlusion {env.GiEnvOcclusion:0.##}: that share of the cube is the game's GI, not drawn - the flat ambient stands in for it"));
            }
        }

        if (model is not null)
        {
            lines.Add(model.LightsSaid.Length > 0 ? model.LightsSaid : "no room lights - not a room, or none read");
            lines.Add(model.AreaOrigin is null
                ? "player light: over the middle of the picture - the room is not laid in the area"
                : _player() is null ? "player light: over the middle - the player is not known" : "player light: at the player");
        }

        lines.Add(!_grade ? "colour grade: off" : _gradeSaid);
        if (_verdict is { } verdict)
        {
            lines.AddRange(verdict.Report.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        string summary = string.Create(
            CultureInfo.InvariantCulture,
            $"details: {sunSaid} · {model?.Lights.Count ?? 0} room lights · {assumed.Count} assumed###scene-details");
        if (!ImGui.TreeNodeEx(summary, ImGuiTreeNodeFlags.SpanAvailWidth))
        {
            return;
        }

        foreach (string line in lines.Where(one => one.Length > 0))
        {
            ImGuiText.Wrapped(OverlayInk.Quiet, ImGuiText.Escape(line));
        }

        ImGui.TreePop();
    }

    /// <summary>The .env in use: the picked one, else the area's.</summary>
    private string Chosen() => _picked.Length > 0 ? _picked : _areaEnvironment();

    /// <summary>Reads an environment, its diffuse cube and its colour grade once, when it changes.</summary>
    private void Load(string path)
    {
        if (string.Equals(path, _loadedPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _loadedPath = path;
        _cube = null;
        _cubeSaid = string.Empty;
        _gradeTable = null;
        _gradeSaid = string.Empty;
        _version++;
        if (path.Length == 0 || _read is null)
        {
            _environment = EnvironmentSettings.None with { Why = path.Length == 0 ? "no environment: the area's is not known - pick one" : "no install to read" };
            return;
        }

        _environment = EnvironmentSettings.Read(path, _read(path.Replace('\\', '/').Trim()));
        if (_environment.PostTransform.Length == 0)
        {
            _gradeSaid = "colour grade: the environment names none";
        }
        else
        {
            _gradeTable = ColourGrade.Read(GameArt.ReadRaw(_read, _environment.PostTransform), out string gradeWhy);
            _gradeSaid = _gradeTable is null
                ? $"colour grade not applied: {_environment.PostTransform} not read - {gradeWhy}"
                : $"colour grade {_environment.PostTransform}: {_gradeTable.Format}";
        }

        if (_environment.DiffuseCube.Length == 0)
        {
            _cubeSaid = "cube: the environment names no diffuse cube - the flat ambient is used";
            return;
        }

        _cube = CubeMap.Read(GameArt.ReadRaw(_read, _environment.DiffuseCube), out string why);
        _cubeSaid = _cube is null
            ? $"cube {_environment.DiffuseCube} not read: {why} - the flat ambient is used"
            : string.Create(CultureInfo.InvariantCulture, $"cube {_environment.DiffuseCube}: {_cube.Format}, average {Say(_cube.Average())}");
    }

    /// <summary>Where the player light stands in the model's space: at the player in a laid room, else over the middle of the picture.</summary>
    private Vector3 PlayerPlace(MonsterModel model)
    {
        // UP IS MINUS Z, so standing above the ground is less z.
        if (model.AreaOrigin is { } origin && _player() is { } player)
        {
            return new Vector3(player.X - origin.X, player.Y - origin.Y, player.Ground - _playerHeight);
        }

        Vector3 least = model.Mesh.Least, most = model.Mesh.Most;
        return new Vector3((least.X + most.X) * 0.5f, (least.Y + most.Y) * 0.5f, most.Z - _playerHeight);
    }

    private static string Say(Vector3 value) => string.Create(CultureInfo.InvariantCulture, $"{value.X:0.###} {value.Y:0.###} {value.Z:0.###}");
}
