using System.Numerics;
using System.Text;
using PoEformance.Features;
using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// Lighting a picture the game's way: the environment cube, the .env, a room's lights, the shader's arithmetic, and the picture drawn under it.
/// </summary>
public class SceneLightTests
{
    // ---- the cube ----

    /// <summary>A direction reads the face its largest component points at, in Direct3D's order: +x, -x, +y, -y, +z, -z.</summary>
    [Fact]
    public void ACUBEFaceIsChosenByTheDirectionsLargestComponent()
    {
        int[] values = [100, 200, 300, 400, 500, 600];
        byte[] dds = Bc6hCube(4, [.. values.Select(one => Constant(one, one / 2, one / 3))]);
        CubeMap? cube = CubeMap.Read(dds, out string why);
        Assert.True(cube is not null, why);
        Assert.Equal(4, cube.Size);

        Vector3[] directions = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];
        for (var face = 0; face < 6; face++)
        {
            Vector3 expected = Decoded(Constant(values[face], values[face] / 2, values[face] / 3), 0);
            Assert.Equal(expected, cube.Sample(directions[face] * 3f));
        }
    }

    /// <summary>
    /// Within a face the texel follows the table every card uses: on +x, minus z is to the right; on +y, plus z is down.
    /// </summary>
    [Fact]
    public void AFACEIsReadTheWayTheCardReadsIt()
    {
        byte[] right = Ramp(rightColumnBright: true);
        byte[] dark = Constant(0, 0, 0);
        byte[] dds = Bc6hCube(4, [right, dark, right, dark, dark, dark]);
        CubeMap cube = CubeMap.Read(dds, out _)!;

        // +X: sc = -z, so a direction leaning to minus z reads the right-hand column.
        Assert.True(cube.Sample(new Vector3(1f, 0f, -0.99f)).X > cube.Sample(new Vector3(1f, 0f, 0.99f)).X);

        // +Y: sc = +x, so leaning to plus x reads the right-hand column.
        Assert.True(cube.Sample(new Vector3(0.99f, 1f, 0f)).X > cube.Sample(new Vector3(-0.99f, 1f, 0f)).X);
    }

    /// <summary>The one cube that is not BC6H - 32-bit colour by the old header's masks - is read too, and a file that is no cube says why.</summary>
    [Fact]
    public void THEOLDHEADERSThirtyTwoBitCubeIsReadAndNotACubeSaysWhy()
    {
        const int size = 2;
        var dds = new byte[128 + (6 * size * size * 4)];
        Header(dds, size, levels: 0);
        BitConverter.TryWriteBytes(dds.AsSpan(80), 0x41u);
        BitConverter.TryWriteBytes(dds.AsSpan(88), 32);
        BitConverter.TryWriteBytes(dds.AsSpan(92), 0x00FF0000u);
        BitConverter.TryWriteBytes(dds.AsSpan(96), 0x0000FF00u);
        BitConverter.TryWriteBytes(dds.AsSpan(100), 0x000000FFu);
        BitConverter.TryWriteBytes(dds.AsSpan(104), 0xFF000000u);
        BitConverter.TryWriteBytes(dds.AsSpan(112), 0x200u | (0x3Fu << 10));
        for (var face = 0; face < 6; face++)
        {
            for (var pixel = 0; pixel < size * size; pixel++)
            {
                int at = 128 + (((face * size * size) + pixel) * 4);
                dds[at] = 0;
                dds[at + 1] = 0;
                dds[at + 2] = (byte)(face * 40);
                dds[at + 3] = 255;
            }
        }

        CubeMap? cube = CubeMap.Read(dds, out string why);
        Assert.True(cube is not null, why);
        Assert.Equal(new Vector3(120f / 255f, 0f, 0f), cube.Sample(-Vector3.UnitY));

        BitConverter.TryWriteBytes(dds.AsSpan(112), 0u);
        Assert.Null(CubeMap.Read(dds, out why));
        Assert.Contains("cube", why, StringComparison.Ordinal);
    }

    // ---- the .env ----

    /// <summary>Seepage's numbers: no sun, a warm player light, GI owning the ambient - and what a missing key was taken as.</summary>
    [Fact]
    public void ANENVIRONMENTIsReadWithWhatItLeftOutSaid()
    {
        const string seepage = """
            {
              "directional_light": { "multiplier": 0.0 },
              "player_light": { "colour": [ 1.0, 0.85869, 0.61868 ], "intensity": 1.23556 },
              "environment_mapping": { "diffuse_cube": "Art/2DArt/Cubemaps/seepagev2_diffuse.dds", "env_brightness": 0.21999, "hor_angle": -1.815 },
              "global_illumination": { "gi_env_occlusion": 1.0 },
              "post_transform": { "texture": "Metadata/EnvironmentSettings/x.env.post.dds" }
            }
            """;
        EnvironmentSettings env = EnvironmentSettings.Read("Seepage.env", Encoding.UTF8.GetBytes(seepage));
        Assert.True(env.Ready, env.Why);
        Assert.Equal(Vector3.Zero, env.SunLight);
        Assert.Equal(new Vector3(1f, 0.85869f, 0.61868f) * 1.23556f, env.PlayerLight);
        Assert.Equal("Art/2DArt/Cubemaps/seepagev2_diffuse.dds", env.DiffuseCube);
        Assert.Equal(0.21999f, env.EnvBrightness);
        Assert.Equal(1f, env.GiEnvOcclusion);
        Assert.Equal(-1.815f, env.HorAngle);
        Assert.Null(env.VertAngle);
        Assert.Equal("Metadata/EnvironmentSettings/x.env.post.dds", env.PostTransform);

        IReadOnlyList<string> assumed = env.Assumed();
        Assert.Contains("camera.exposure absent, taken as 1", assumed);
        Assert.Contains("environment_mapping.vert_angle absent, taken as 0", assumed);
        Assert.DoesNotContain(assumed, one => one.StartsWith("directional_light.multiplier", StringComparison.Ordinal));
    }

    /// <summary>A sun is its colour times its multiplier, with its angles kept as written; a file that is not JSON says so.</summary>
    [Fact]
    public void ASUNIsColourTimesMultiplierAndNotJsonSaysWhy()
    {
        const string gallows = """
            { "directional_light": { "shadows_enabled": true, "colour": [ 0.1955, 0.26078, 0.4259 ], "multiplier": 5.0, "phi": 2.19911, "theta": 1.64058 },
              "camera": { "exposure": 1.5 } }
            """;
        EnvironmentSettings env = EnvironmentSettings.Read("g.env", Encoding.UTF8.GetBytes(gallows));
        Assert.Equal(new Vector3(0.1955f, 0.26078f, 0.4259f) * 5f, env.SunLight);
        Assert.Equal(2.19911f, env.Phi);
        Assert.Equal(1.64058f, env.Theta);
        Assert.True(env.SunShadows);
        Assert.Equal(1.5f, env.Exposure);

        EnvironmentSettings broken = EnvironmentSettings.Read("b.env", Encoding.UTF8.GetBytes("not json"));
        Assert.False(broken.Ready);
        Assert.StartsWith("not JSON", broken.Why, StringComparison.Ordinal);
        Assert.False(EnvironmentSettings.Read("none.env", null).Ready);
    }

    /// <summary>An area's dust colour is area.dust_color; where the file sets none it is the assumed grey, said as assumed.</summary>
    [Fact]
    public void THEDUSTCOLOURIsTheAreasAndTheAssumedGreyWhereItSetsNone()
    {
        EnvironmentSettings dusty = EnvironmentSettings.Read("d.env", Encoding.UTF8.GetBytes(
            """{ "area": { "dust_color": [ 0.87177, 0.44367, 0.00906 ], "ground_scale": 0.004 } }"""));
        Assert.Equal(new Vector3(0.87177f, 0.44367f, 0.00906f), dusty.Dust);
        Assert.Equal(dusty.Dust, dusty.DustColour);
        Assert.DoesNotContain(dusty.Assumed(), one => one.StartsWith("area.dust_color", StringComparison.Ordinal));

        EnvironmentSettings bare = EnvironmentSettings.Read("b.env", Encoding.UTF8.GetBytes("""{ "camera": { "exposure": 1.0 } }"""));
        Assert.Null(bare.Dust);
        Assert.Equal(EnvironmentSettings.AssumedDust, bare.DustColour);
        Assert.Contains("area.dust_color absent, taken as 0.5 grey", bare.Assumed());
        Assert.Equal(EnvironmentSettings.AssumedDust, EnvironmentSettings.None.DustColour);
    }

    // ---- a room's lights ----

    /// <summary>
    /// A Lights block's light is drawn in its default state, a state with no colour is off, a spot light is counted, and a fixed_ao piece's light goes through the line's place and then the doodad's.
    /// </summary>
    [Fact]
    public void ADOODADSLightsAreReadInTheirDefaultStateAndPlaced()
    {
        const string doodad = "Metadata/Terrain/Doodads/Test/Lamp.ao";
        const string piece = "Metadata/Terrain/Doodads/Lights/Fire.ao";
        const string lamp = """
            version 3
            AOSet
            {
            	fixed_ao = "10 0 -25 0 0 0 2 0 Metadata/Terrain/Doodads/Lights/Fire.ao"
            }
            client
            {
            	Lights
            	{
            		light = "point_light"
            			state = "lit"
            				colour = "1 0.5 0.25"
            				position = "0 0 -100"
            				radius = 300
            				penumbra_dist = 0.5
            			state = "dark"
            				default_state = true
            				radius = 300
            		light = "point_light"
            			state = "on"
            				colour = "2 2 2"
            				position = "5 6 7"
            				radius = 400
            		light = "spot_light"
            	}
            }
            """;
        const string fire = """
            version 3
            client
            {
            	Lights
            	{
            		disable_mesh_lights = true
            		light = "point_light"
            			state = ""
            				colour = "1.26723 0.491207 0.0415635"
            				position = "0 12 1"
            				radius = 500
            				penumbra_dist = 0.5
            	}
            }
            """;
        byte[]? Read(string path) => path switch
        {
            doodad => Encoding.UTF8.GetBytes(lamp),
            piece => Encoding.UTF8.GetBytes(fire),
            _ => null,
        };

        var gathered = new RoomLights.Gathered(Read);
        gathered.Add(doodad, Matrix4x4.CreateTranslation(1000f, 0f, 0f));
        Assert.Equal(2, gathered.Lights.Count);

        PointLight on = gathered.Lights.Single(one => one.Colour == new Vector3(2f));
        Assert.Equal(new Vector3(1005f, 6f, 7f), on.Position);
        Assert.Equal(400f, on.Radius);

        // THE PIECE: its own (0, 12, 1) through the line's scale of two and shift, then the doodad's.
        PointLight hung = gathered.Lights.Single(one => one.Radius == 500f);
        Assert.Equal(new Vector3(1010f, 24f, -23f), hung.Position);
        Assert.Equal(0.5f, hung.PenumbraDist);

        string said = gathered.Said();
        Assert.Contains("2 point lights from 1 doodads", said, StringComparison.Ordinal);
        Assert.Contains("1 in a state with no colour (off)", said, StringComparison.Ordinal);
        Assert.Contains("1 spot lights not drawn", said, StringComparison.Ordinal);
    }

    // ---- the arithmetic ----

    /// <summary>
    /// A point light gives what ComputePointLightParamsNew gives - its cutoff from the colour, its core from a - and nothing past the cutoff.
    /// </summary>
    [Theory]
    [InlineData(50f, SceneLight.PointShape.Zero)]
    [InlineData(300f, SceneLight.PointShape.Zero)]
    [InlineData(300f, SceneLight.PointShape.One)]
    [InlineData(900f, SceneLight.PointShape.Penumbra)]
    public void APOINTLightFallsOffAsTheShaderSays(float distance, SceneLight.PointShape shape)
    {
        var colour = new Vector3(1.26723f, 0.491207f, 0.0415635f);
        const float radius = 500f;
        const float penumbra = 0.5f;
        var light = new SceneLight([new PointLight(new Vector3(0f, 0f, -distance), colour, radius, penumbra, "test")], shape, null)
        {
            Ambient = SceneAmbient.None,
        };

        Vector3 got = light.Shade(Vector3.One, Vector3.Zero, -Vector3.UnitZ, -Vector3.UnitZ, 1f, false, default, 0f);

        // The shader, written out again here rather than called.
        float a = shape switch { SceneLight.PointShape.One => 1f, SceneLight.PointShape.Penumbra => penumbra, _ => 0f };
        float cutoff = radius * MathF.Sqrt((colour.X + colour.Y + colour.Z) * 0.2f / 0.1f);
        float multiplier = (1f / MathF.Sqrt(0.02f)) + ((100f - (1f / MathF.Sqrt(0.02f))) * a);
        float zero = 10f * 0.02f * multiplier * multiplier;
        float divisor = (distance / (radius / multiplier)) + 1f;
        float fade = MathF.Max(0f, 1f - MathF.Pow(distance / cutoff, 4f));
        float attenuation = MathF.Min(10f, zero * fade * fade / (divisor * divisor));
        Assert.Equal(colour.X * attenuation, got.X, 4);
        Assert.Equal(colour.Z * attenuation, got.Z, 4);

        Vector3 beyond = light.Shade(Vector3.One, new Vector3(0f, 0f, cutoff + 10f), -Vector3.UnitZ, -Vector3.UnitZ, 1f, false, default, 0f);
        Assert.Equal(Vector3.Zero, beyond);
    }

    /// <summary>The sun lights by the cosine, its shadow takes it away, and the exposure multiplies the lot.</summary>
    [Fact]
    public void THESUNIsLambertianShadowedAndExposed()
    {
        var sun = new Vector3(1f, 0.5f, 0.25f);
        var light = new SceneLight([], SceneLight.PointShape.Zero, null)
        {
            Ambient = SceneAmbient.None,
            SunColour = sun,
            SunDirection = Vector3.Normalize(new Vector3(1f, 0f, 1f)),
            Exposure = 2f,
        };

        // A floor facing up (minus z), the sun coming down at 45 degrees.
        Vector3 got = light.Shade(Vector3.One, Vector3.Zero, -Vector3.UnitZ, -Vector3.UnitZ, 1f, false, default, 0f);
        AssertNear(sun * MathF.Sqrt(0.5f) * 2f, got);
        Assert.Equal(Vector3.Zero, light.Shade(Vector3.One, Vector3.Zero, -Vector3.UnitZ, -Vector3.UnitZ, 0f, false, default, 0f));

        // A face turned from the sun takes none of it.
        Assert.Equal(Vector3.Zero, light.Shade(Vector3.One, Vector3.Zero, Vector3.UnitX, Vector3.UnitX, 1f, false, default, 0f));
    }

    /// <summary>The cube's ambient is its colour times env_brightness, and the share gi_env_occlusion gives the game's GI is the flat ambient instead.</summary>
    [Fact]
    public void THECUBESAmbientLeavesGisShareToTheFlatOne()
    {
        byte[] block = Constant(400, 400, 400);
        CubeMap cube = CubeMap.Read(Bc6hCube(4, [block, block, block, block, block, block]), out _)!;
        Vector3 sky = Decoded(block, 0);
        var light = new SceneLight([], SceneLight.PointShape.Zero, null)
        {
            Ambient = SceneAmbient.Cube,
            Cube = cube,
            CubeBrightness = 0.5f,
            GiEnvOcclusion = 0.25f,
            FlatAmbient = 0.1f,
        };

        Vector3 got = light.Shade(Vector3.One, Vector3.Zero, -Vector3.UnitZ, -Vector3.UnitZ, 1f, false, default, 0f);
        AssertNear((sky * 0.5f * 0.75f) + new Vector3(0.1f * 0.25f), got);
    }

    /// <summary>The sun readings: theta from straight up at nought, or as a height of a quarter turn, is the sun overhead - its light falling along plus z.</summary>
    [Fact]
    public void THESUNREADINGSPutAnOverheadSunOverhead()
    {
        AssertNear(Vector3.UnitZ, SceneLight.SunFrom(1.3f, 0f, SceneLight.SunReading.PolarTheta));
        AssertNear(Vector3.UnitZ, SceneLight.SunFrom(-2f, MathF.PI / 2f, SceneLight.SunReading.ElevationTheta));
        AssertNear(Vector3.UnitZ, SceneLight.SunFrom(0f, 0.7f, SceneLight.SunReading.PolarPhi));
        AssertNear(-Vector3.UnitZ, SceneLight.SunFrom(1.3f, 0f, SceneLight.SunReading.PolarThetaFalling));

        // Low in the sky towards +x, the light travels towards -x and down.
        Vector3 low = SceneLight.SunFrom(0f, 0.2f, SceneLight.SunReading.ElevationTheta);
        Assert.True(low.X < -0.9f && low.Z > 0f);
    }

    // ---- the picture ----

    /// <summary>A floor under a point light is brightest under the light, and no light leaves the picture as it was.</summary>
    [Fact]
    public void APICTUREUnderAPointLightIsBrightestUnderIt()
    {
        SkinnedMesh floor = Floor(200f, 0f);
        const int size = 64;
        var canvas = new MeshPicture.Canvas(size, threads: 1);
        byte[] plain = [.. MeshPicture.Of(floor, canvas, 0f, Down).Rgba];

        canvas.Light = new SceneLight([new PointLight(new Vector3(0f, 0f, -60f), new Vector3(1f), 120f, 0f, "test")], SceneLight.PointShape.Zero, null)
        {
            Ambient = SceneAmbient.None,
        };
        byte[] lit = [.. MeshPicture.Of(floor, canvas, 0f, Down).Rgba];
        Assert.NotEqual(plain, lit);

        int middle = ((size / 2) * size) + (size / 2);
        int edge = ((size / 2) * size) + 10;
        Assert.True(lit[middle * 4] > lit[edge * 4] + 20, $"middle {lit[middle * 4]}, edge {lit[edge * 4]}");

        canvas.Light = null;
        Assert.Equal(plain, MeshPicture.Of(floor, canvas, 0f, Down).Rgba);
    }

    /// <summary>
    /// The sun's shadow darkens the floor under a slab and nothing else: a floor alone is not shaded by its own shadow map - on the full map and on the coarse one a moving sun is drawn with.
    /// </summary>
    [Theory]
    [InlineData(ShadowMap.Usual)]
    [InlineData(ShadowMap.Coarse)]
    public void THESUNSShadowFallsUnderASlabAndNotOnTheFloorItself(int side)
    {
        const int size = 96;
        SkinnedMesh alone = Floor(200f, 0f);
        SkinnedMesh slabbed = Joined(Floor(200f, 0f), Floor(40f, -80f));
        Vector3 slant = Vector3.Normalize(new Vector3(0.6f, 0f, 1f));

        byte[] Drawn(SkinnedMesh mesh, bool shadows)
        {
            var canvas = new MeshPicture.Canvas(size, threads: 2)
            {
                Light = new SceneLight([], SceneLight.PointShape.Zero, null)
                {
                    Ambient = SceneAmbient.Flat,
                    FlatAmbient = 0.05f,
                    SunColour = Vector3.One,
                    SunDirection = slant,
                    SunShadows = shadows,
                    ShadowSide = side,
                },
            };
            return [.. MeshPicture.Of(mesh, canvas, 0f, Down).Rgba];
        }

        Assert.Equal(Drawn(alone, shadows: false), Drawn(alone, shadows: true));

        byte[] open = Drawn(slabbed, shadows: false);
        byte[] shaded = Drawn(slabbed, shadows: true);
        int darker = 0, brighter = 0;
        for (var at = 0; at < open.Length; at += 4)
        {
            darker += shaded[at] + 10 < open[at] ? 1 : 0;
            brighter += shaded[at] > open[at] ? 1 : 0;
        }

        Assert.True(darker > 50, $"{darker} pixels darker");
        Assert.Equal(0, brighter);
    }

    /// <summary>The tilt that looks straight down on the floor - positive looks down.</summary>
    private const float Down = MathF.PI / 2f;

    private static SkinnedMesh Floor(float half, float z)
    {
        Vector3[] places =
        [
            new(-half, -half, z), new(half, -half, z), new(half, half, z), new(-half, half, z),
        ];
        Vector3[] normals = [.. places.Select(_ => -Vector3.UnitZ)];
        return SkinnedMesh.Of(places, normals, [0, 1, 2, 0, 2, 3], new Vector3(-half, -half, z), new Vector3(half, half, z));
    }

    private static SkinnedMesh Joined(SkinnedMesh first, SkinnedMesh second)
    {
        Vector3[] places = [.. first.Positions, .. second.Positions];
        Vector3[] normals = [.. first.Normals, .. second.Normals];
        int[] indices = [.. first.Indices, .. second.Indices.Select(one => one + first.Positions.Length)];
        return SkinnedMesh.Of(places, normals, indices, Vector3.Min(first.Least, second.Least), Vector3.Max(first.Most, second.Most));
    }

    private static void AssertNear(Vector3 expected, Vector3 got)
    {
        Assert.True(Vector3.Distance(expected, got) < 1e-4f, $"expected {expected}, got {got}");
    }

    // ---- making BC6H cubes ----

    /// <summary>A mode-11 block (10.10 endpoints, no partitions) of one colour: both endpoints the same, every index nought.</summary>
    private static byte[] Constant(int r, int g, int b) => Mode11(r, g, b, r, g, b, _ => 0);

    /// <summary>A mode-11 block dark on its left column and bright on its right.</summary>
    private static byte[] Ramp(bool rightColumnBright) => Mode11(0, 0, 0, 900, 900, 900, texel => (texel % 4 == 3) == rightColumnBright ? 15 : 0);

    private static byte[] Mode11(int r0, int g0, int b0, int r1, int g1, int b1, Func<int, int> index)
    {
        var bits = new BitWriter();
        bits.Write(0b00011, 5);
        foreach (int one in (int[])[r0, g0, b0, r1, g1, b1])
        {
            bits.Write(one, 10);
        }

        for (var texel = 0; texel < 16; texel++)
        {
            bits.Write(index(texel), texel == 0 ? 3 : 4);
        }

        return bits.Bytes();
    }

    /// <summary>What the decoder makes of one block's texel - the decoder itself is held against bcdec in CubeMapTests.</summary>
    private static Vector3 Decoded(byte[] block, int texel)
    {
        var rgb = new float[48];
        Bc6h.Block(block, rgb, signed: false);
        return new Vector3(rgb[texel * 3], rgb[(texel * 3) + 1], rgb[(texel * 3) + 2]);
    }

    /// <summary>A DX10 BC6H_UF16 cube of one level whose faces are these blocks - a face of four texels across is one block.</summary>
    private static byte[] Bc6hCube(int size, byte[][] faces)
    {
        var dds = new byte[128 + 20 + (6 * 16)];
        Header(dds, size, levels: 1);
        BitConverter.TryWriteBytes(dds.AsSpan(80), 0x4u);
        Encoding.ASCII.GetBytes("DX10").CopyTo(dds, 84);
        BitConverter.TryWriteBytes(dds.AsSpan(128), 95);
        BitConverter.TryWriteBytes(dds.AsSpan(132), 3);
        BitConverter.TryWriteBytes(dds.AsSpan(136), 0x4u);
        BitConverter.TryWriteBytes(dds.AsSpan(140), 1);
        for (var face = 0; face < 6; face++)
        {
            faces[face].CopyTo(dds, 148 + (face * 16));
        }

        return dds;
    }

    private static void Header(byte[] dds, int size, int levels)
    {
        Encoding.ASCII.GetBytes("DDS ").CopyTo(dds, 0);
        BitConverter.TryWriteBytes(dds.AsSpan(4), 124);
        BitConverter.TryWriteBytes(dds.AsSpan(12), size);
        BitConverter.TryWriteBytes(dds.AsSpan(16), size);
        BitConverter.TryWriteBytes(dds.AsSpan(28), levels);
    }

    /// <summary>128 bits written from the bottom, as BC6H reads them.</summary>
    private sealed class BitWriter
    {
        private UInt128 _bits;
        private int _at;

        public void Write(int value, int count)
        {
            _bits |= ((UInt128)(uint)value & ((UInt128.One << count) - 1)) << _at;
            _at += count;
        }

        public byte[] Bytes()
        {
            var bytes = new byte[16];
            for (var at = 0; at < 16; at++)
            {
                bytes[at] = (byte)(_bits >> (at * 8));
            }

            return bytes;
        }
    }
}
