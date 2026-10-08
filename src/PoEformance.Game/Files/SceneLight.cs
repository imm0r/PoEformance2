using System.Numerics;

namespace PoEformance.Game.Files;

/// <summary>Where a lit picture's ambient light comes from - see <see cref="SceneLight.Ambient"/>.</summary>
public enum SceneAmbient
{
    /// <summary>None: only the lights.</summary>
    None,

    /// <summary>The picture's own flat ambient, the same in every direction.</summary>
    Flat,

    /// <summary>The environment's diffuse cube, the game's way.</summary>
    Cube,
}

/// <summary>
/// How a picture is lit the game's way: an area's sun, the player's light, a room's point lights, the environment cube and the exposure - each switchable.
/// </summary>
/// <remarks>
/// THE ARITHMETIC IS THE GAME'S SHADERS', line for line where they have it (renderer/lighting.ffx,
/// postprocessuber.hlsl; NEW_LIGHT is 1 in PoE2):
/// <list type="bullet">
/// <item>A POINT LIGHT - ComputePointLightParamsNew: the cutoff is <c>radius * sqrt(sum|colour| * 0.2 / 0.1)</c>,
/// the radius multiplier <c>lerp(1/sqrt(0.02), 100, saturate(a))</c>, and the light at a distance d
/// <c>colour * min(10, 0.2 m^2 * saturate(1 - (d/cutoff)^4)^2 / (d m / radius + 1)^2)</c>.</item>
/// <item>THE SUN - ComputeDirectionalLightParams: its colour, from a fixed direction.</item>
/// <item>EACH ADDS <c>saturate(N.L) * light * albedo</c> (ComputeDiffuse) and its GGX lobe where the
/// material is glossy (ComputeLight), and every light's intensity is summed into <c>total</c>.</item>
/// <item>THE CUBE - AddDiffuseEnvironmentLight: <c>cube(N turned by env_map_rotation) * (cube_brightness.x
/// + total * direct_light_env_ratio) * albedo * (1 - gi_env_occlusion)</c>.</item>
/// <item>THE EXPOSURE - ApplyToneMapping with GGG_POE_1 off: the colour times <c>max(1, exposure)</c>.</item>
/// </list>
///
/// WHAT THE SHADERS DO NOT SAY is turned into vectors on the processor, and those are the candidate
/// readings, each held against the game by a screenshot rather than chosen: how the .env's phi and
/// theta make the sun's direction (<see cref="SunReading"/>), how hor_angle and vert_angle make
/// env_map_rotation (<see cref="CubeReading"/>), and which number a room light's light_position_data.a
/// is (<see cref="PointShape"/>). The player light's radius and height are in no file read so far.
///
/// WHERE GI OWNS THE AMBIENT (gi_env_occlusion one, Seepage among them) the game's own global
/// illumination stands in for the cube, and that is not something this draws: the flat ambient is
/// what is left, and the line under the picture says so.
///
/// IN MODEL SPACE: the lights, the vertices and the normals are all in the picture's model space,
/// which for a room is the room's own - the area's, for a room drawn as the area laid it.
/// </remarks>
public sealed class SceneLight
{
    /// <summary>The cutoff radius's colour threshold and zero-intensity multiplier - GetPointCutoffRadius.</summary>
    private const float ColourThreshold = 0.1f;

    /// <inheritdoc cref="ColourThreshold"/>
    private const float ZeroIntensity = 0.2f;

    /// <summary>The cells of the light grid each way, at most.</summary>
    private const int MostCells = 32;

    private readonly Light[] _lights;
    private readonly int[] _cellStarts;
    private readonly int[] _cellEntries;
    private readonly Vector3 _gridLeast;
    private readonly float _cellSize;
    private readonly int _cellsX, _cellsY, _cellsZ;

    /// <param name="points">The room's point lights, in model space - <see cref="PointLight"/>.</param>
    /// <param name="shape">Which number stands for each one's light_position_data.a.</param>
    /// <param name="player">The player's light where it is drawn, else null.</param>
    public SceneLight(IReadOnlyList<PointLight> points, PointShape shape, PlayerLamp? player)
    {
        ArgumentNullException.ThrowIfNull(points);
        var lights = new List<Light>(points.Count + 1);
        foreach (PointLight one in points)
        {
            float a = shape switch
            {
                PointShape.One => 1f,
                PointShape.Penumbra => one.PenumbraDist,
                _ => 0f,
            };

            if (Of(one.Position, one.Colour, one.Radius, a) is { } light)
            {
                lights.Add(light);
            }
        }

        // THE PLAYER'S LIGHT IS A POINT LIGHT TOO, with a of nought: the shader's own comment on the
        // radius multiplier - "1.0f / sqrt(0.02f) -- params of player light for backwards compatibility".
        if (player is { } lamp && Of(lamp.Position, lamp.Colour, lamp.Radius, 0f) is { } own)
        {
            lights.Add(own);
        }

        _lights = [.. lights];
        PointCount = _lights.Length;

        // A GRID OVER THE LIGHTS' REACH, so a pixel asks only the lights whose cutoff reaches its
        // cell - a room with a hundred torches costs each pixel the two or three near it.
        if (_lights.Length == 0)
        {
            _cellStarts = [0, 0];
            _cellEntries = [];
            _cellsX = _cellsY = _cellsZ = 1;
            _cellSize = 1f;
            return;
        }

        Vector3 least = new(float.MaxValue), most = new(float.MinValue);
        float widest = 0f;
        foreach (Light one in _lights)
        {
            least = Vector3.Min(least, one.Position - new Vector3(one.Cutoff));
            most = Vector3.Max(most, one.Position + new Vector3(one.Cutoff));
            widest = MathF.Max(widest, one.Cutoff);
        }

        Vector3 span = most - least;
        float cell = MathF.Max(MathF.Max(span.X, MathF.Max(span.Y, span.Z)) / MostCells, MathF.Max(widest * 0.5f, 1f));
        _gridLeast = least;
        _cellSize = cell;
        _cellsX = Math.Clamp((int)MathF.Ceiling(span.X / cell), 1, MostCells);
        _cellsY = Math.Clamp((int)MathF.Ceiling(span.Y / cell), 1, MostCells);
        _cellsZ = Math.Clamp((int)MathF.Ceiling(span.Z / cell), 1, MostCells);
        int cells = _cellsX * _cellsY * _cellsZ;
        var counts = new int[cells + 1];
        for (var pass = 0; pass < 2; pass++)
        {
            int[]? cursors = pass == 1 ? (int[])counts.Clone() : null;
            int[] entries = pass == 1 ? new int[counts[cells]] : [];
            for (var at = 0; at < _lights.Length; at++)
            {
                Light one = _lights[at];
                (int x0, int y0, int z0) = Cell(one.Position - new Vector3(one.Cutoff));
                (int x1, int y1, int z1) = Cell(one.Position + new Vector3(one.Cutoff));
                for (int z = z0; z <= z1; z++)
                {
                    for (int y = y0; y <= y1; y++)
                    {
                        for (int x = x0; x <= x1; x++)
                        {
                            int index = (((z * _cellsY) + y) * _cellsX) + x;
                            if (pass == 0)
                            {
                                counts[index + 1]++;
                            }
                            else
                            {
                                entries[cursors![index]++] = at;
                            }
                        }
                    }
                }
            }

            if (pass == 0)
            {
                for (var at = 0; at < cells; at++)
                {
                    counts[at + 1] += counts[at];
                }
            }
            else
            {
                _cellEntries = entries;
            }
        }

        _cellStarts = counts;
        _cellEntries ??= [];
    }

    /// <summary>What stands for a room light's light_position_data.a, which no file names - a candidate reading.</summary>
    public enum PointShape
    {
        /// <summary>Nought: the player light's own value, a soft core.</summary>
        Zero,

        /// <summary>One: the sharpest core the shader allows.</summary>
        One,

        /// <summary>The light's penumbra_dist line, clamped as the shader clamps it.</summary>
        Penumbra,
    }

    /// <summary>How the .env's phi and theta make the sun's direction - candidate readings, see the class remarks.</summary>
    public enum SunReading
    {
        /// <summary>Theta from straight up, phi round it from x; the vector points at the sun.</summary>
        PolarTheta,

        /// <summary>Theta as the height above the ground, phi round it from x; the vector points at the sun.</summary>
        ElevationTheta,

        /// <summary>Phi from straight up, theta round it from x; the vector points at the sun.</summary>
        PolarPhi,

        /// <summary>Phi as the height above the ground, theta round it from x; the vector points at the sun.</summary>
        ElevationPhi,

        /// <summary>As <see cref="PolarTheta"/>, the vector the way the light travels.</summary>
        PolarThetaFalling,

        /// <summary>As <see cref="ElevationTheta"/>, the vector the way the light travels.</summary>
        ElevationThetaFalling,

        /// <summary>As <see cref="PolarPhi"/>, the vector the way the light travels.</summary>
        PolarPhiFalling,

        /// <summary>As <see cref="ElevationPhi"/>, the vector the way the light travels.</summary>
        ElevationPhiFalling,
    }

    /// <summary>How hor_angle and vert_angle make env_map_rotation - candidate readings.</summary>
    public enum CubeReading
    {
        /// <summary>No turn at all.</summary>
        None,

        /// <summary>About z by hor_angle, then about x by vert_angle.</summary>
        ZThenX,

        /// <summary>About x by vert_angle, then about z by hor_angle.</summary>
        XThenZ,

        /// <summary>About z by hor_angle, then about y by vert_angle.</summary>
        ZThenY,

        /// <summary>About y by vert_angle, then about z by hor_angle.</summary>
        YThenZ,
    }

    /// <summary>The player's light: where, what colour (its colour times its intensity), how far.</summary>
    public readonly record struct PlayerLamp(Vector3 Position, Vector3 Colour, float Radius);

    /// <summary>How many point lights are drawn, the player's included.</summary>
    public int PointCount { get; }

    /// <summary>The sun's light, its colour times its multiplier - zero for none.</summary>
    public Vector3 SunColour { get; init; }

    /// <summary>The way the sun's light travels, unit length, in model space.</summary>
    public Vector3 SunDirection { get; init; } = new(0f, 0f, 1f);

    /// <summary>Whether the sun casts shadows - <see cref="ShadowMap"/>.</summary>
    public bool SunShadows { get; init; }

    /// <summary>Where the ambient comes from.</summary>
    public SceneAmbient Ambient { get; init; } = SceneAmbient.Flat;

    /// <summary>The flat ambient's light, linear - the picture's usual one unless the caller says otherwise.</summary>
    public float FlatAmbient { get; init; } = UsualFlatAmbient;

    /// <summary>The picture's own ambient as light: 0.22 on the sRGB value, which is what an unlit picture shades with.</summary>
    public static float UsualFlatAmbient { get; } = MathF.Pow((0.22f + 0.055f) / 1.055f, 2.4f);

    /// <summary>The diffuse cube, where <see cref="Ambient"/> is <see cref="SceneAmbient.Cube"/>.</summary>
    public CubeMap? Cube { get; init; }

    /// <summary>env_map_rotation: what a normal is put through before the cube is read.</summary>
    public Matrix4x4 CubeTurn { get; init; } = Matrix4x4.Identity;

    /// <summary>cube_brightness.x - env_brightness.</summary>
    public float CubeBrightness { get; init; } = 1f;

    /// <summary>direct_light_env_ratio: how much the cube is brightened by the light that falls here.</summary>
    public float DirectLightEnvRatio { get; init; }

    /// <summary>gi_env_occlusion: the share of the cube's light the game's GI replaces.</summary>
    public float GiEnvOcclusion { get; init; }

    /// <summary>The colour is multiplied by this before it is shown - max(1, camera.exposure); one for none.</summary>
    public float Exposure { get; init; } = 1f;

    /// <summary>The way a sun with these angles shines, as the reading takes them - unit length, the way the light travels.</summary>
    public static Vector3 SunFrom(float phi, float theta, SunReading reading)
    {
        // THE GAME'S UP IS MINUS Z, so "up" in a reading is -z; x and y are the area's.
        (float round, float tilt, bool polar) = reading switch
        {
            SunReading.PolarTheta or SunReading.PolarThetaFalling => (phi, theta, true),
            SunReading.ElevationTheta or SunReading.ElevationThetaFalling => (phi, theta, false),
            SunReading.PolarPhi or SunReading.PolarPhiFalling => (theta, phi, true),
            _ => (theta, phi, false),
        };

        float across = polar ? MathF.Sin(tilt) : MathF.Cos(tilt);
        float up = polar ? MathF.Cos(tilt) : MathF.Sin(tilt);
        var pointing = new Vector3(across * MathF.Cos(round), across * MathF.Sin(round), -up);
        bool falling = reading is SunReading.PolarThetaFalling or SunReading.ElevationThetaFalling
            or SunReading.PolarPhiFalling or SunReading.ElevationPhiFalling;

        // POINTING AT THE SUN, the light travels the other way.
        Vector3 travels = falling ? pointing : -pointing;
        return travels.LengthSquared() > 0f ? Vector3.Normalize(travels) : new Vector3(0f, 0f, 1f);
    }

    /// <summary>env_map_rotation as the reading takes hor_angle and vert_angle.</summary>
    public static Matrix4x4 CubeTurnFrom(float horizontal, float vertical, CubeReading reading) => reading switch
    {
        CubeReading.ZThenX => Matrix4x4.CreateRotationZ(horizontal) * Matrix4x4.CreateRotationX(vertical),
        CubeReading.XThenZ => Matrix4x4.CreateRotationX(vertical) * Matrix4x4.CreateRotationZ(horizontal),
        CubeReading.ZThenY => Matrix4x4.CreateRotationZ(horizontal) * Matrix4x4.CreateRotationY(vertical),
        CubeReading.YThenZ => Matrix4x4.CreateRotationY(vertical) * Matrix4x4.CreateRotationZ(horizontal),
        _ => Matrix4x4.Identity,
    };

    /// <summary>
    /// One pixel lit: its albedo under every light, the ambient, the specular light of a glossy material, exposed - linear.
    /// </summary>
    /// <param name="albedo">The surface's colour, linear.</param>
    /// <param name="place">Where the pixel is, model space.</param>
    /// <param name="normal">Its normal, unit length, turned to face the eye.</param>
    /// <param name="toEye">The way to the eye, unit length.</param>
    /// <param name="sunShadow">How much of the sun reaches it, nought to one.</param>
    /// <param name="glossy">Whether the material's specular light is worked out - its program has a gloss.</param>
    /// <param name="specular">The material's specular colour, linear.</param>
    /// <param name="gloss">Its gloss.</param>
    public Vector3 Shade(Vector3 albedo, Vector3 place, Vector3 normal, Vector3 toEye, float sunShadow, bool glossy, Vector3 specular, float gloss)
    {
        Vector3 diffuse = Vector3.Zero;
        Vector3 shine = Vector3.Zero;
        Vector3 total = Vector3.Zero;
        float facing = MathF.Max(Vector3.Dot(normal, toEye), 1e-4f);

        if (SunColour != Vector3.Zero && sunShadow > 0f)
        {
            Vector3 light = SunColour * sunShadow;
            Vector3 towards = -SunDirection;
            Lit(light, towards, normal, toEye, facing, glossy, specular, gloss, ref diffuse, ref shine);
            total += light;
        }

        if (_lights.Length > 0)
        {
            int cell = CellIndex(place);
            if (cell >= 0)
            {
                for (int at = _cellStarts[cell], upto = _cellStarts[cell + 1]; at < upto; at++)
                {
                    ref readonly Light one = ref _lights[_cellEntries[at]];
                    Vector3 offset = one.Position - place;
                    float squared = offset.LengthSquared();
                    if (squared >= one.Cutoff * one.Cutoff)
                    {
                        continue;
                    }

                    float distance = MathF.Sqrt(squared);
                    float ratio = distance / one.Cutoff;
                    float fade = MathF.Max(0f, 1f - (ratio * ratio * ratio * ratio));
                    float divisor = (distance / one.Core) + 1f;
                    float attenuation = MathF.Min(10f, one.Zero * fade * fade / (divisor * divisor));
                    if (!(attenuation > 0f))
                    {
                        continue;
                    }

                    Vector3 light = one.Colour * attenuation;
                    Vector3 towards = distance > 1e-4f ? offset / distance : normal;
                    Lit(light, towards, normal, toEye, facing, glossy, specular, gloss, ref diffuse, ref shine);
                    total += light;
                }
            }
        }

        Vector3 colour = diffuse * albedo;
        Vector3 surround = Vector3.Zero;
        switch (Ambient)
        {
            case SceneAmbient.Cube when Cube is not null:
                // THE SHARE GI OWNS IS THE FLAT AMBIENT - the game's GI is not drawn; see the remarks.
                surround = (Cube.Sample(Vector3.TransformNormal(normal, CubeTurn))
                    * (new Vector3(CubeBrightness) + (total * DirectLightEnvRatio)) * (1f - GiEnvOcclusion))
                    + new Vector3(FlatAmbient * GiEnvOcclusion);
                break;
            case SceneAmbient.Flat:
            case SceneAmbient.Cube:
                surround = new Vector3(FlatAmbient);
                break;
        }

        colour += surround * albedo;

        // THE ENVIRONMENT'S SPECULAR, as the unlit picture works it out - a uniform environment as
        // bright as the ambient - since the specular cube is not read yet.
        if (glossy)
        {
            GlossLight.Environment(facing, gloss, out float bias, out float scale);
            float level = (surround.X + surround.Y + surround.Z) / 3f;
            shine += new Vector3(level * bias) + (level * scale * specular);
        }

        return (colour + shine) * Exposure;
    }

    /// <summary>One light's diffuse and specular share at one pixel - ComputeDiffuse and the GGX lobe of ComputeLight.</summary>
    private static void Lit(
        Vector3 light, Vector3 towards, Vector3 normal, Vector3 toEye, float facing, bool glossy, Vector3 specular, float gloss,
        ref Vector3 diffuse, ref Vector3 shine)
    {
        float lit = Vector3.Dot(normal, towards);
        if (lit <= 0f)
        {
            return;
        }

        diffuse += light * MathF.Min(lit, 1f);
        if (glossy)
        {
            Vector3 half = Vector3.Normalize(toEye + towards);
            float lobe = GlossLight.Lobe(normal, facing, towards, half, gloss);
            float fresnel = GlossLight.Fresnel(Math.Clamp(Vector3.Dot(toEye, half), 0f, 1f));
            shine += light * lobe * (new Vector3(fresnel) + (specular * (1f - fresnel)));
        }
    }

    /// <summary>A light's precomputed numbers - GetPointCutoffRadius and ComputePointLightParamsNew's constants - or null for one that adds nothing.</summary>
    private static Light? Of(Vector3 position, Vector3 colour, float radius, float a)
    {
        float intensity = MathF.Abs(colour.X) + MathF.Abs(colour.Y) + MathF.Abs(colour.Z);
        if (!(radius > 0f) || !(intensity > 0f))
        {
            return null;
        }

        float cutoff = radius * MathF.Sqrt(intensity * ZeroIntensity / ColourThreshold);
        float multiplier = float.Lerp(1f / MathF.Sqrt(0.02f), 100f, Math.Clamp(a, 0f, 1f));
        return new Light(position, colour, cutoff, radius / multiplier, 10f * 0.02f * multiplier * multiplier);
    }

    private int CellIndex(Vector3 place)
    {
        Vector3 at = (place - _gridLeast) / _cellSize;
        if (at.X < 0f || at.Y < 0f || at.Z < 0f)
        {
            return -1;
        }

        int x = (int)at.X, y = (int)at.Y, z = (int)at.Z;
        if (x >= _cellsX || y >= _cellsY || z >= _cellsZ)
        {
            return -1;
        }

        return (((z * _cellsY) + y) * _cellsX) + x;
    }

    private (int X, int Y, int Z) Cell(Vector3 place)
    {
        Vector3 at = (place - _gridLeast) / _cellSize;
        return (
            Math.Clamp((int)at.X, 0, _cellsX - 1),
            Math.Clamp((int)at.Y, 0, _cellsY - 1),
            Math.Clamp((int)at.Z, 0, _cellsZ - 1));
    }

    /// <summary>A point light ready to shade with: its cutoff, its core radius (median over multiplier), and its zero intensity.</summary>
    private readonly record struct Light(Vector3 Position, Vector3 Colour, float Cutoff, float Core, float Zero);
}
