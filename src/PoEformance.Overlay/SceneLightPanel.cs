using System.Globalization;
using System.Numerics;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.Files;

namespace PoEformance.Overlay;

/// <summary>
/// The tile book's switches for lighting a picture the game's way: which lights, which environment, and the candidate readings where no file says.
/// </summary>
/// <remarks>
/// EVERY PART HAS ITS OWN SWITCH, as asked: the room's point lights, the sun, its shadows, the player's
/// light, the ambient (none, the picture's flat one, the environment's cube) and the exposure - so each
/// can be held against a screenshot from the game on its own.
///
/// THE CANDIDATE READINGS ARE CHOICES, NOT SETTINGS: how the sun's phi and theta, and the cube's two
/// angles, become directions is worked out on the game's processor and written nowhere a file shows.
/// Each reading is offered so the one that matches a screenshot can be found, and the line under the
/// panel prints the numbers it gave.
///
/// THE ENVIRONMENT IS THE AREA'S OWN BY DEFAULT - its WorldAreas row's Environments row - and any .env
/// in the install can be picked instead, for a room seen outside its area.
/// </remarks>
public sealed class SceneLightPanel
{
    /// <summary>The player light's radius until a file says what it is - a candidate, see the slider's tooltip.</summary>
    private const float UsualPlayerRadius = 500f;

    /// <summary>How far above the ground the player light stands - a candidate as well.</summary>
    private const float UsualPlayerHeight = 100f;

    /// <summary>How far the player may move before the light follows, so walking does not redraw a room every frame.</summary>
    private const float PlayerStep = 25f;

    /// <summary>Most environments the picker lists for a filter.</summary>
    private const int MostListed = 200;

    private static readonly string SunReadings = string.Join('\0', [
        "theta from up, phi round (to the sun)",
        "theta as height, phi round (to the sun)",
        "phi from up, theta round (to the sun)",
        "phi as height, theta round (to the sun)",
        "theta from up, phi round (light's way)",
        "theta as height, phi round (light's way)",
        "phi from up, theta round (light's way)",
        "phi as height, theta round (light's way)",
    ]) + "\0";

    private static readonly string CubeReadings = "no turn\0z by hor, then x by vert\0x by vert, then z by hor\0z by hor, then y by vert\0y by vert, then z by hor\0";

    private static readonly string PointShapes = "a = 0 (the player light's)\0a = 1\0a = penumbra_dist\0";

    private static readonly string Ambients = "no ambient\0flat ambient\0cube ambient\0";

    private readonly Func<string, byte[]?>? _read;
    private readonly Func<IReadOnlyList<string>> _environments;
    private readonly Func<string> _areaEnvironment;
    private readonly Func<(float X, float Y, float Ground)?> _player;

    private bool _on;
    private bool _points = true;
    private bool _sun = true;
    private bool _shadows = true;
    private bool _playerLight = true;
    private bool _exposure = true;
    private int _ambient = (int)SceneAmbient.Cube;
    private int _sunReading;
    private int _cubeReading;
    private int _pointShape;
    private float _playerRadius = UsualPlayerRadius;
    private float _playerHeight = UsualPlayerHeight;
    private float _flat = SceneLight.UsualFlatAmbient;
    private string _picked = string.Empty;
    private string _filter = string.Empty;
    private int _version;

    private string _loadedPath = "\0";
    private EnvironmentSettings _environment = EnvironmentSettings.None;
    private CubeMap? _cube;
    private string _cubeSaid = string.Empty;

    private MonsterModel? _builtModel;
    private int _builtVersion = -1;
    private string _builtPath = string.Empty;
    private Vector3 _builtPlayer = new(float.NaN);
    private SceneLight? _built;

    /// <param name="read">How to read a file out of the install.</param>
    /// <param name="environments">Every .env in the install.</param>
    /// <param name="areaEnvironment">The current area's own .env, or empty.</param>
    /// <param name="player">The player's place in the world and the ground's height under it, or null.</param>
    public SceneLightPanel(
        Func<string, byte[]?>? read,
        Func<IReadOnlyList<string>> environments,
        Func<string> areaEnvironment,
        Func<(float X, float Y, float Ground)?> player)
    {
        _read = read;
        _environments = environments;
        _areaEnvironment = areaEnvironment;
        _player = player;
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
        var reading = (SceneLight.SunReading)_sunReading;
        _built = new SceneLight(
            _points ? model.Lights : [],
            (SceneLight.PointShape)_pointShape,
            _playerLight ? new SceneLight.PlayerLamp(player, env.PlayerLight, _playerRadius) : null)
        {
            SunColour = _sun ? env.SunLight : Vector3.Zero,
            SunDirection = SceneLight.SunFrom(env.Phi ?? 0f, env.Theta ?? 0f, reading),
            SunShadows = _shadows,
            Ambient = (SceneAmbient)_ambient,
            FlatAmbient = _flat,
            Cube = _cube,
            CubeTurn = SceneLight.CubeTurnFrom(env.HorAngle ?? 0f, env.VertAngle ?? 0f, (SceneLight.CubeReading)_cubeReading),
            CubeBrightness = env.EnvBrightness ?? 1f,
            DirectLightEnvRatio = env.DirectLightEnvRatio ?? 0f,
            GiEnvOcclusion = Math.Clamp(env.GiEnvOcclusion ?? 0f, 0f, 1f),
            Exposure = _exposure ? MathF.Max(1f, env.Exposure ?? 1f) : 1f,
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
        if (!_on)
        {
            return;
        }

        // A LABEL COLUMN, so the rows read as what they are: which lights, from where the rest of
        // the light comes, which environment, and the readings no file settles.
        float column = ImGui.CalcTextSize("environment").X + (ImGui.GetStyle().ItemSpacing.X * 3f);

        Label("lights", column);
        Switch("room##scene-points", ref _points, "The point lights the room's doodads carry in their .ao Lights blocks, the default state of each.");
        ImGui.SameLine();
        Switch("sun##scene-sun", ref _sun, "directional_light: its colour times its multiplier. Seepage's is nought.");
        ImGui.SameLine();
        Switch("shadows##scene-shadows", ref _shadows, "The sun's shadows: the room drawn once more along the sun's light, and a pixel is shaded where something nearer the sun covers it.");
        ImGui.SameLine();
        Switch("player##scene-player", ref _playerLight, "player_light: its colour times its intensity, a point light at the player's feet in a laid room, else over the middle of the picture.");
        ImGui.SameLine();
        Switch("exposure##scene-exposure", ref _exposure, "camera.exposure: the colour times max(1, exposure), the game's own tone mapping for PoE2. The colour grade (post_transform) is not applied.");

        Label("ambient", column);
        ImGui.SetNextItemWidth(130f);
        if (ImGui.Combo("##scene-ambient", ref _ambient, Ambients))
        {
            _version++;
        }

        OverlayLayout.Hint("Where the light that comes from everywhere comes from.\n"
            + "cube: the environment's diffuse cube, by the turned normal, times env_brightness - the game's way.\n"
            + "flat: the picture's own, the same from every direction.\n"
            + "Where the .env gives gi_env_occlusion, that share of the cube is the game's GI instead, which is not drawn - the flat ambient stands in for it.");
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled("flat");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110f);
        if (ImGui.SliderFloat("##scene-flat", ref _flat, 0f, 0.5f, "%.3f"))
        {
            _version++;
        }

        OverlayLayout.Hint("The flat ambient's light, linear. The picture's usual is 0.04 - 0.22 on the screen's value.");

        Label("environment", column);
        Picker();

        Label("readings", column);
        Readings(column);

        Said(model);
    }

    /// <summary>A row's label in the label column, the row's controls after it.</summary>
    private static void Label(string text, float column)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(text);
        ImGui.SameLine(column);
    }

    private void Switch(string label, ref bool value, string hint)
    {
        if (ImGui.Checkbox(label, ref value))
        {
            _version++;
        }

        OverlayLayout.Hint(hint);
    }

    /// <summary>The environment: the area's own, or one picked from the install's by a filter - its file's name, the path on hover.</summary>
    private void Picker()
    {
        string chosen = Chosen();
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

    /// <summary>The candidate readings, and the player light's two numbers no file gives - two rows under one label.</summary>
    private void Readings(float column)
    {
        Named("sun");
        ImGui.SetNextItemWidth(240f);
        if (ImGui.Combo("##scene-sun-reading", ref _sunReading, SunReadings))
        {
            _version++;
        }

        OverlayLayout.Hint("How directional_light's phi and theta make the sun's direction - worked out on the processor, in no file."
            + " Up is minus z, the game's own. Pick the one whose shadows fall as the game's do.");
        ImGui.SameLine();
        Named("cube");
        ImGui.SetNextItemWidth(190f);
        if (ImGui.Combo("##scene-cube-reading", ref _cubeReading, CubeReadings))
        {
            _version++;
        }

        OverlayLayout.Hint("How environment_mapping's hor_angle and vert_angle make env_map_rotation, the turn the normal takes before the cube is read.");

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + column);
        Named("core");
        ImGui.SetNextItemWidth(190f);
        if (ImGui.Combo("##scene-core", ref _pointShape, PointShapes))
        {
            _version++;
        }

        OverlayLayout.Hint("Which number is a room light's light_position_data.a - the shader turns it into how sharp the light's core is,"
            + " lerp(1/sqrt(0.02), 100, a). No line in a Lights block is named for it. It changes the light near the lamp, hardly at all past its radius.");
        ImGui.SameLine();
        Named("player radius");
        ImGui.SetNextItemWidth(90f);
        if (ImGui.SliderFloat("##scene-player-radius", ref _playerRadius, 50f, 2000f, "%.0f"))
        {
            _version++;
        }

        OverlayLayout.Hint("The player light's radius. No file read so far gives it - a candidate to match against a screenshot.");
        ImGui.SameLine();
        Named("height");
        ImGui.SetNextItemWidth(80f);
        if (ImGui.SliderFloat("##scene-player-height", ref _playerHeight, 0f, 500f, "%.0f"))
        {
            _version++;
        }

        OverlayLayout.Hint("How far above the ground the player light stands. Not in any file read so far either.");
    }

    /// <summary>A control's name before it, quiet.</summary>
    private static void Named(string text)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(text);
        ImGui.SameLine();
    }

    /// <summary>
    /// What the light came to - the environment's numbers, what was assumed, the lights, the cube - folded under a one-line summary.
    /// </summary>
    /// <remarks>
    /// FOLDED, because these are what to read when the picture and the game disagree, not while
    /// switching: open, they were a paragraph longer than the switches above them.
    /// </remarks>
    private void Said(MonsterModel? model)
    {
        EnvironmentSettings env = _environment;
        var lines = new List<string> { env.Said() };
        IReadOnlyList<string> assumed = env.Ready ? env.Assumed() : [];
        if (assumed.Count > 0)
        {
            lines.Add("assumed: " + string.Join("; ", assumed));
        }

        string sunSaid = !env.Ready ? "no environment" : env.SunLight == Vector3.Zero ? "no sun" : "sun";
        if (_sun && env.Ready && env.SunLight != Vector3.Zero)
        {
            Vector3 travels = SceneLight.SunFrom(env.Phi ?? 0f, env.Theta ?? 0f, (SceneLight.SunReading)_sunReading);
            float height = MathF.Asin(Math.Clamp(travels.Z, -1f, 1f)) * 180f / MathF.PI;
            sunSaid = string.Create(CultureInfo.InvariantCulture, $"sun {height:0}° high{(height < 0f ? " - BELOW the ground" : string.Empty)}");
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"sun: light travels {travels.X:0.##} {travels.Y:0.##} {travels.Z:0.##} - the sun {height:0}° above the ground{(height < 0f ? ", BELOW it in this reading" : string.Empty)}"));
            if (model?.AreaOrigin is null)
            {
                lines.Add("sun: this room is drawn as its file has it - turned however the area turned it, so the sun's x and y may not be the area's");
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

        if (env.PostTransform.Length > 0 && _exposure)
        {
            lines.Add("colour grade not applied: " + env.PostTransform);
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

    /// <summary>Reads an environment and its diffuse cube once, when it changes.</summary>
    private void Load(string path)
    {
        if (string.Equals(path, _loadedPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _loadedPath = path;
        _cube = null;
        _cubeSaid = string.Empty;
        if (path.Length == 0 || _read is null)
        {
            _environment = EnvironmentSettings.None with { Why = path.Length == 0 ? "no environment: the area's is not known - pick one" : "no install to read" };
            return;
        }

        _environment = EnvironmentSettings.Read(path, _read(path.Replace('\\', '/').Trim()));
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
