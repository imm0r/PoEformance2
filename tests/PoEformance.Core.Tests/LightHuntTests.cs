using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Game.Diagnostics;
using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// Asking the game which reading it uses: the float hunt over its memory, what a finding means, the colour grade's table, and the free sun's arithmetic.
/// </summary>
public class LightHuntTests
{
    private const ulong Base = 0x2000_0000;

    // ---- the float hunt ----

    /// <summary>A vector is found within its tolerance and a near miss is not; an exact value is matched exactly; a needle of noughts and ones is not looked for.</summary>
    [Fact]
    public void AFLOATHuntFindsTheVectorAndTheExactValueAndSkipsTheUnsearchable()
    {
        var bytes = new byte[0x1000];
        Floats(bytes, 0x100, -0.51f, 0.53f, 0.68f);
        Floats(bytes, 0x200, -0.51f, 0.53f, 0.70f);
        Floats(bytes, 0x300, 2.339f);
        Floats(bytes, 0x400, 2.3390002f);
        (FakeMemoryReader memory, Space space) = Memory(bytes);

        FloatNeedle[] needles =
        [
            new("vector", [-0.51f, 0.53f, 0.68f], 1e-4f),
            new("exact", [2.339f], 0f),
            new("identity row", [1f, 0f, 0f], 1e-4f),
        ];
        FloatHuntResult result = FloatHunt.Run(memory, space, needles);

        Assert.Equal([new FloatSighting(Base + 0x100, 0), new FloatSighting(Base + 0x300, 1)], result.Sightings.OrderBy(one => one.At));
        Assert.Equal([2], result.Unsearched);
        Assert.Equal(bytes.Length, result.BytesScanned);
    }

    /// <summary>A needle that starts in one megabyte chunk and ends in the next is compared whole, and found once.</summary>
    [Fact]
    public void ANEEDLEAcrossAChunkBoundaryIsFoundOnce()
    {
        var bytes = new byte[(2 * HeapScan.ChunkBytes) + 0x1000];
        int across = HeapScan.ChunkBytes - 4;
        Floats(bytes, across, 0.25f, -0.75f, 0.5f);
        Floats(bytes, HeapScan.ChunkBytes + 0x40, 0.25f, -0.75f, 0.5f);
        (FakeMemoryReader memory, Space space) = Memory(bytes);

        FloatHuntResult result = FloatHunt.Run(memory, space, [new FloatNeedle("v", [0.25f, -0.75f, 0.5f], 1e-5f)]);

        Assert.Equal([Base + (ulong)across, Base + (ulong)HeapScan.ChunkBytes + 0x40], result.Sightings.Select(one => one.At).Order());
    }

    /// <summary>A blank in a needle - a matrix row's fourth float - matches anything.</summary>
    [Fact]
    public void ABLANKInANeedleMatchesAnything()
    {
        var bytes = new byte[0x400];
        Floats(bytes, 0x80, 0.3f, 99f, 0.7f);
        (FakeMemoryReader memory, Space space) = Memory(bytes);

        FloatHuntResult result = FloatHunt.Run(memory, space, [new FloatNeedle("padded", [0.3f, float.NaN, 0.7f], 1e-5f)]);

        Assert.Equal(Base + 0x80, Assert.Single(result.Sightings).At);
    }

    // ---- what a finding means ----

    /// <summary>The reading whose vector the game holds is the verdict, the raw angle beside it is said, and the panel's reading follows it.</summary>
    [Fact]
    public void THEREADINGWhoseVectorIsInMemoryIsTheVerdict()
    {
        EnvironmentSettings env = Sunny();
        LightHunt hunt = LightHunt.For("Sunny.env", env, one => $"reading {one + 1}", out string why)!;
        Assert.True(why.Length == 0, why);

        Vector3 held = SceneLight.SunFrom(env.Phi!.Value, env.Theta!.Value, SceneLight.SunReading.ElevationThetaFalling);
        var bytes = new byte[0x2000];
        Floats(bytes, 0x800, held.X, held.Y, held.Z);
        Floats(bytes, 0x7C0, env.Phi.Value, env.Theta.Value);
        (FakeMemoryReader memory, Space space) = Memory(bytes);

        LightHuntVerdict verdict = hunt.Read(FloatHunt.Run(memory, space, hunt.Needles));

        Assert.Equal((int)SceneLight.SunReading.ElevationThetaFalling, verdict.Sun);
        Assert.Contains("reading 6", verdict.Summary, StringComparison.Ordinal);
        Assert.Contains("theta 60 bytes before", verdict.Report, StringComparison.Ordinal);
        Assert.Contains("that is NOT the reading the picture draws the sun by", verdict.Report, StringComparison.Ordinal);
        Assert.Null(verdict.Cube);

        // WHAT LIES BESIDE IT is read back, the vector's own floats in brackets.
        Assert.Contains("beside sun reading 6 at 0x20000800:", verdict.Report, StringComparison.Ordinal);
        Assert.Contains(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"[{held.X:0.#####}]"), verdict.Report, StringComparison.Ordinal);
    }

    /// <summary>
    /// A vector held both ways round, side by side as AzmerianRanges held it, is settled by the ground: the reading whose sun stands above it is the light's way - and it is the one the picture draws.
    /// </summary>
    [Fact]
    public void AVECTORHeldBothWaysIsSettledByTheGround()
    {
        EnvironmentSettings env = Sunny();
        LightHunt hunt = LightHunt.For("Sunny.env", env, one => $"reading {one + 1}", out _)!;
        Vector3 held = SceneLight.SunFrom(env.Phi!.Value, env.Theta!.Value, SceneLight.GameSun);
        Assert.True(held.Z > 0f, "the game's reading puts this sun above the ground");
        var bytes = new byte[0x1000];
        Floats(bytes, 0x100, held.X, held.Y, held.Z, 0f, -held.X, -held.Y, -held.Z, 0f);
        (FakeMemoryReader memory, Space space) = Memory(bytes);

        LightHuntVerdict verdict = hunt.Read(FloatHunt.Run(memory, space, hunt.Needles));

        Assert.Equal((int)SceneLight.GameSun, verdict.Sun);
        Assert.Contains("1 of 1 copies side by side, sixteen bytes apart", verdict.Report, StringComparison.Ordinal);
        Assert.Contains("that is the reading the picture draws the sun by", verdict.Report, StringComparison.Ordinal);
    }

    /// <summary>A turn about an axis no reading turns about is found among the other arrangements, said, and not taken.</summary>
    [Fact]
    public void ACUBETurnInAnotherArrangementIsSaidAndNotTaken()
    {
        EnvironmentSettings env = Sunny();
        LightHunt hunt = LightHunt.For("Sunny.env", env, one => $"reading {one + 1}", out _)!;
        var turn = Matrix4x4.CreateRotationZ(env.VertAngle!.Value);
        var bytes = new byte[0x1000];
        Floats(bytes, 0x100, turn.M11, turn.M12, turn.M13, turn.M14, turn.M21, turn.M22, turn.M23, turn.M24, turn.M31, turn.M32, turn.M33, turn.M34);
        (FakeMemoryReader memory, Space space) = Memory(bytes);

        LightHuntVerdict verdict = hunt.Read(FloatHunt.Run(memory, space, hunt.Needles));

        Assert.Null(verdict.Cube);
        Assert.Contains("axes x>x y>y z>z, turned about z by +vert, rows of four: 1 place", verdict.Report, StringComparison.Ordinal);
    }

    /// <summary>A cube turn found as written is the cube's verdict; with one angle nought, the two orders that give the same turn are said to.</summary>
    [Fact]
    public void ACUBETurnFoundAsWrittenIsTheVerdict()
    {
        EnvironmentSettings env = Sunny();
        LightHunt hunt = LightHunt.For("Sunny.env", env, one => $"reading {one + 1}", out _)!;
        Matrix4x4 turn = SceneLight.CubeTurnFrom(env.HorAngle!.Value, env.VertAngle!.Value, SceneLight.CubeReading.ZThenX);
        var bytes = new byte[0x1000];
        Floats(bytes, 0x100, turn.M11, turn.M12, turn.M13, turn.M14, turn.M21, turn.M22, turn.M23, turn.M24, turn.M31, turn.M32, turn.M33, turn.M34);
        (FakeMemoryReader memory, Space space) = Memory(bytes);

        LightHuntVerdict verdict = hunt.Read(FloatHunt.Run(memory, space, hunt.Needles));

        Assert.Equal((int)SceneLight.CubeReading.ZThenX, verdict.Cube);
        Assert.Contains("cube reading 2, rows of four (turned by hor, then tipped about x by vert): 1 place", verdict.Report, StringComparison.Ordinal);
        Assert.Contains("cube reading 3 is reading 2's turn", verdict.Report, StringComparison.Ordinal);
    }

    /// <summary>An environment with no sun angles and no cube turn has nothing to look for, and says so.</summary>
    [Fact]
    public void ANENVIRONMENTWithNothingToFindSaysSo()
    {
        EnvironmentSettings env = EnvironmentSettings.Read("dark.env", Encoding.UTF8.GetBytes("""{ "directional_light": { "multiplier": 0.0 } }"""));
        Assert.Null(LightHunt.For("dark.env", env, one => one.ToString(System.Globalization.CultureInfo.InvariantCulture), out string why));
        Assert.Contains("neither", why, StringComparison.Ordinal);
    }

    // ---- the colour grade ----

    /// <summary>
    /// A neutral table of gamma-encoded texels is decoded as an sRGB view decodes it, and so hands the colour back unchanged - the game's picture is not washed out by a grade that changes nothing - and a colour past one keeps its brightness.
    /// </summary>
    [Fact]
    public void AGAMMANeutralTableIsDecodedAndHandsTheColourBack()
    {
        const int side = 16;
        ColourGrade grade = ColourGrade.Read(Volume(side, (r, g, b) => (r, g, b), srgb: false), out string why)!;
        Assert.True(why.Length == 0, why);
        Assert.Contains("R8G8B8A8_UNORM 16 x 16 x 16, held gamma-encoded", grade.Format, StringComparison.Ordinal);
        Assert.True(grade.Decoded);
        Assert.True(grade.FromGammaNeutral < 0.005f && grade.FromLinearNeutral > 0.1f, grade.Format);

        // LOOKED UP BY pow(c, 1/2.2), DECODED BY THE sRGB CURVE: the two are not each other's inverse
        // to the last digit, but near enough that a neutral table changes nothing a screen shows.
        var colour = new Vector3(0.2f, 0.5f, 0.05f);
        Vector3 graded = grade.Apply(colour);
        Assert.True(Vector3.Distance(graded, colour) < 0.015f, $"{graded} against {colour}");

        // PAST ONE: divided by the brightest channel, looked up, multiplied back - the top clamped to
        // the last texel's centre, as the game's clamping sampler clamps it.
        Vector3 bright = grade.Apply(new Vector3(4f, 2f, 1f));
        float top = MathF.Pow((15.5f / 16f + 0.055f) / 1.055f, 2.4f);
        Assert.True(MathF.Abs(bright.X - (4f * top)) < 0.05f, bright.ToString());
        Assert.True(MathF.Abs(bright.Y - 2f) < 0.08f, bright.ToString());
    }

    /// <summary>A neutral table of linear texels - each the 2.2nd power of its place - is read as it stands, and hands the colour back too.</summary>
    [Fact]
    public void ALINEARNeutralTableIsReadAsItStands()
    {
        ColourGrade grade = ColourGrade.Read(Volume(16, (r, g, b) => (MathF.Pow(r, 2.2f), MathF.Pow(g, 2.2f), MathF.Pow(b, 2.2f)), srgb: false), out _)!;
        Assert.False(grade.Decoded);
        Assert.Contains("held as linear light", grade.Format, StringComparison.Ordinal);
        var colour = new Vector3(0.2f, 0.5f, 0.3f);
        Assert.True(Vector3.Distance(grade.Apply(colour), colour) < 0.02f, grade.Apply(colour).ToString());
    }

    /// <summary>An sRGB table is decoded to linear light as a card decodes it, and a flat picture is not a volume and says so.</summary>
    [Fact]
    public void ANSRGBTableIsDecodedAndAFlatPictureIsNotAVolume()
    {
        ColourGrade grade = ColourGrade.Read(Volume(4, (_, _, _) => (0.5f, 0.5f, 0.5f), srgb: true), out _)!;
        float linear = MathF.Pow((0.5f + 0.055f) / 1.055f, 2.4f);
        Assert.True(MathF.Abs(grade.Apply(new Vector3(0.3f)).X - linear) < 0.01f);

        byte[] flat = Volume(4, (r, g, b) => (r, g, b), srgb: false);
        BitConverter.TryWriteBytes(flat.AsSpan(132), 3);
        Assert.Null(ColourGrade.Read(flat, out string why));
        Assert.StartsWith("not a volume", why, StringComparison.Ordinal);
    }

    // ---- the free sun ----

    /// <summary>Where a sun stands and the way its light travels are each other's inverse, and a sun above the ground shines downward, plus z.</summary>
    [Fact]
    public void ASUNSPlaceAndItsLightAreEachOthersInverse()
    {
        Vector3 travels = SceneLight.SunToward(1.2f, 0.6f);
        Assert.True(travels.Z > 0f);
        (float round, float height) = SceneLight.SunStands(travels);
        Assert.Equal(1.2f, round, 4);
        Assert.Equal(0.6f, height, 4);
    }

    /// <summary>The map's frame puts x up-right and y up-left, and a screen way turned back to the ground lands on the direction it came from.</summary>
    [Fact]
    public void THEGROUNDOnTheScreenRunsAsTheMapDrawsIt()
    {
        SceneLight.GroundOnScreen map = SceneLight.GroundOnScreen.Map;
        Vector2 x = map.Way(Vector3.UnitX), y = map.Way(Vector3.UnitY);
        Assert.True(x.X > 0f && x.Y > 0f, $"x runs {x}");
        Assert.True(y.X < 0f && y.Y > 0f, $"y runs {y}");

        var ground = Vector2.Normalize(new Vector2(0.3f, -0.8f));
        Vector2 back = map.Ground(map.Way(new Vector3(ground, 0f)));
        Assert.True(Vector2.Distance(ground, back) < 1e-4f, $"{ground} came back as {back}");
        Assert.Equal(90f, SceneLight.GroundOnScreen.Bearing(Vector2.UnitX), 3);
        Assert.True(Vector2.Distance(SceneLight.GroundOnScreen.OfBearing(225f), Vector2.Normalize(new Vector2(-1f, -1f))) < 1e-5f);
    }

    private static EnvironmentSettings Sunny()
    {
        const string json = """
            { "directional_light": { "colour": [ 1.0, 0.9, 0.8 ], "multiplier": 3.0, "phi": 2.339, "theta": 0.75 },
              "environment_mapping": { "hor_angle": 0.0, "vert_angle": 0.192 } }
            """;
        EnvironmentSettings env = EnvironmentSettings.Read("Sunny.env", Encoding.UTF8.GetBytes(json));
        Assert.True(env.Ready, env.Why);
        return env;
    }

    private static void Floats(byte[] into, int at, params float[] values)
        => MemoryMarshal.Cast<float, byte>(values).CopyTo(into.AsSpan(at));

    private static (FakeMemoryReader Memory, Space Space) Memory(byte[] bytes)
        => (new FakeMemoryReader().Place(Base, bytes), new Space(new MemoryRegion(Base, (ulong)bytes.Length)));

    /// <summary>A DX10 DDS volume of R8G8B8A8, its texels from a function of their centres, nought to one each way.</summary>
    private static byte[] Volume(int side, Func<float, float, float, (float R, float G, float B)> texel, bool srgb)
    {
        int texels = side * side * side;
        var dds = new byte[148 + (texels * 4)];
        "DDS "u8.CopyTo(dds);
        BitConverter.TryWriteBytes(dds.AsSpan(4), 124);
        BitConverter.TryWriteBytes(dds.AsSpan(8), 0x800000u | 0x1007u);
        BitConverter.TryWriteBytes(dds.AsSpan(12), side);
        BitConverter.TryWriteBytes(dds.AsSpan(16), side);
        BitConverter.TryWriteBytes(dds.AsSpan(24), side);
        BitConverter.TryWriteBytes(dds.AsSpan(28), 1);
        BitConverter.TryWriteBytes(dds.AsSpan(76), 32);
        BitConverter.TryWriteBytes(dds.AsSpan(80), 0x4u);
        "DX10"u8.CopyTo(dds.AsSpan(84));
        BitConverter.TryWriteBytes(dds.AsSpan(112), 0x200000u);
        BitConverter.TryWriteBytes(dds.AsSpan(128), srgb ? 29 : 28);
        BitConverter.TryWriteBytes(dds.AsSpan(132), 4);
        BitConverter.TryWriteBytes(dds.AsSpan(140), 1);
        int at = 148;
        for (var z = 0; z < side; z++)
        {
            for (var y = 0; y < side; y++)
            {
                for (var x = 0; x < side; x++)
                {
                    (float r, float g, float b) = texel((x + 0.5f) / side, (y + 0.5f) / side, (z + 0.5f) / side);
                    dds[at++] = (byte)MathF.Round(r * 255f);
                    dds[at++] = (byte)MathF.Round(g * 255f);
                    dds[at++] = (byte)MathF.Round(b * 255f);
                    dds[at++] = 255;
                }
            }
        }

        return dds;
    }

    private sealed class Space(params MemoryRegion[] regions) : IMemoryRegions
    {
        public IEnumerable<MemoryRegion> Regions() => regions;
    }
}
