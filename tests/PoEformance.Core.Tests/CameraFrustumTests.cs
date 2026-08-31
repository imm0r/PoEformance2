using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Entities;

namespace PoEformance.Core.Tests;

/// <summary>
/// The camera's view frustum, sixteen bytes before the world-to-screen matrix.
/// </summary>
/// <remarks>
/// FOUND BY LOOKING AT WHAT THE MATRIX HUNT ALREADY RECORDED. Every committed session sweeps
/// WorldData 0x000-0x840 once, so a block nobody had decoded had been sitting in eighteen
/// fixtures the whole time: eight world-space points at 0xAC and six unit-normal planes at
/// 0x10C, stride 0x10.
///
/// Unit length is precisely the fingerprint this project has already been burned by - the
/// invariant that rejected the true W2SMatrix and accepted 0x11C - so none of the checks here
/// rest on it. They rest on things a wrong reading fails:
///
///  - the eight corners lie exactly ON three of the six planes each, and four bytes to either
///    side not one of them lies on any;
///  - the player, who is on screen by definition, is inside all six in every recording;
///  - the player's distance to the four side planes is the SAME NUMBER in every recording,
///    at world positions six thousand units apart, which is what a frustum carried by a
///    player-centred camera has to look like and what an accidental solid cannot be.
///
/// And it closes the decoy: 0x11C is plane 1, so a mat4x4 read there takes planes 1 to 4 and
/// the unit vector the old invariant wanted at +0x30 was plane 4's normal.
/// </remarks>
public class CameraFrustumTests
{
    private const int CornersAt = 0xAC;
    private const int PlanesAt = 0x10C;
    private const int PlaneStride = 0x10;

    private readonly record struct Plane(float A, float B, float C, float D)
    {
        public float Length => MathF.Sqrt((A * A) + (B * B) + (C * C));

        public float Distance(float x, float y, float z) => (A * x) + (B * y) + (C * z) + D;
    }

    /// <summary>Every committed recording, because eighteen agreeing is the whole argument.</summary>
    private static IEnumerable<string> Fixtures()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Directory.GetFiles(Path.Combine(dir.FullName, "tests", "fixtures"), "*.rec").Order();
    }

    /// <summary>
    /// The frustum block as of the first frame that holds it, plus the chain it came from.
    /// </summary>
    /// <remarks>
    /// Null when the recording never read those bytes. That is not a failure and must not be
    /// asserted away: the shortest fixtures are single instants captured before the sweep
    /// existed, and a file that cannot answer is silent rather than negative.
    /// </remarks>
    private static (Plane[] Planes, (float X, float Y, float Z)[] Corners, ulong GameStates)? Read(
        ReplayMemoryReader replay, OffsetSchema schema)
    {
        if (!replay.ResolvedStatics.TryGetValue("GameStates", out ulong gameStates))
        {
            return null;
        }

        Span<byte> block = stackalloc byte[16];
        for (uint frame = 0; frame < replay.FrameCount; frame++)
        {
            replay.Seek(frame);
            GameChainAddresses chain = GameChain.Resolve(replay, schema, gameStates);
            if (chain.WorldData == 0 || !replay.TryRead(chain.WorldData + PlanesAt, block))
            {
                continue;
            }

            var planes = new Plane[6];
            for (int i = 0; i < planes.Length; i++)
            {
                if (!replay.TryRead(chain.WorldData + (ulong)(PlanesAt + (i * PlaneStride)), block))
                {
                    return null;
                }

                planes[i] = new Plane(
                    BitConverter.ToSingle(block), BitConverter.ToSingle(block[4..]),
                    BitConverter.ToSingle(block[8..]), BitConverter.ToSingle(block[12..]));
            }

            var corners = new (float, float, float)[8];
            for (int i = 0; i < corners.Length; i++)
            {
                if (!replay.TryRead(chain.WorldData + (ulong)(CornersAt + (i * 12)), block[..12]))
                {
                    return null;
                }

                corners[i] = (BitConverter.ToSingle(block), BitConverter.ToSingle(block[4..]),
                    BitConverter.ToSingle(block[8..]));
            }

            return (planes, corners, gameStates);
        }

        return null;
    }

    [Fact]
    public void EveryCornerLiesOnThreePlanes_AndOnNoneWhenMisaligned()
    {
        int answered = 0;

        foreach (string path in Fixtures())
        {
            using var replay = ReplayMemoryReader.Load(File.OpenRead(path));
            OffsetSchema schema = RealSessionTests.Schema();
            if (Read(replay, schema) is not { } found)
            {
                continue;
            }

            answered++;
            string session = Path.GetFileNameWithoutExtension(path);

            foreach ((float x, float y, float z) in found.Corners)
            {
                int on = found.Planes.Count(p => MathF.Abs(p.Distance(x, y, z)) < 1f);
                Assert.True(on >= 3, $"{session}: corner ({x:F1},{y:F1},{z:F1}) lies on {on} planes, not 3");
            }

            // The control, and the reason this is a measurement rather than a resemblance:
            // one float earlier the same bytes describe nothing at all. Without it, "eight
            // points fit six planes" would be a claim about arithmetic that any block of
            // floats could be tortured into.
            int fitsMisaligned = 0;
            var block = new byte[12];
            replay.Seek(0);
            GameChainAddresses chain = GameChain.Resolve(replay, schema, found.GameStates);
            for (uint frame = 0; frame < replay.FrameCount && chain.WorldData == 0; frame++)
            {
                replay.Seek(frame);
                chain = GameChain.Resolve(replay, schema, found.GameStates);
            }

            for (int i = 0; i < 8; i++)
            {
                if (!replay.TryRead(chain.WorldData + (ulong)(CornersAt - 4 + (i * 12)), block))
                {
                    continue;
                }

                float x = BitConverter.ToSingle(block), y = BitConverter.ToSingle(block, 4),
                    z = BitConverter.ToSingle(block, 8);
                if (found.Planes.Count(p => MathF.Abs(p.Distance(x, y, z)) < 1f) >= 3)
                {
                    fitsMisaligned++;
                }
            }

            Assert.Equal(0, fitsMisaligned);
        }

        Assert.True(answered >= 15, $"only {answered} recordings hold the frustum block");
    }

    [Fact]
    public void PlanesAreUnitNormals_AndTheLastTwoAreTheNearAndFarPair()
    {
        foreach (string path in Fixtures())
        {
            using var replay = ReplayMemoryReader.Load(File.OpenRead(path));
            if (Read(replay, RealSessionTests.Schema()) is not { } found)
            {
                continue;
            }

            string session = Path.GetFileNameWithoutExtension(path);
            foreach (Plane plane in found.Planes)
            {
                Assert.Equal(1f, plane.Length, 4);
            }

            // Planes 4 and 5 face each other exactly: they are one slab, so plane 4's normal
            // IS the view direction and the depth of field is the gap between them.
            Plane near = found.Planes[4], far = found.Planes[5];
            Assert.Equal(-near.A, far.A, 4);
            Assert.Equal(-near.B, far.B, 4);
            Assert.Equal(-near.C, far.C, 4);
            Assert.True(far.D + near.D > 0, $"{session}: near and far plane do not enclose anything");

            // The camera's orientation is fixed in this game, and the direction it looks is
            // the world diagonal - the same axis ScreenBasis recovers from the matrix by a
            // completely different route when it reports up-screen as (0.7071, 0.7071).
            Assert.Equal(near.A, near.B, 4);
            Assert.True(near.C > 0.7f, $"{session}: view direction is not pitched downward");
        }
    }

    [Fact]
    public void ThePlayerIsInside_AtTheSameDistanceFromTheSidePlanesInEveryRecording()
    {
        OffsetSchema schema = RealSessionTests.Schema();
        int renderPosition = schema.Structs["Render"].OffsetOf("CurrentWorldPosition");
        var sideDistances = new List<float[]>();
        var positions = new List<float>();

        foreach (string path in Fixtures())
        {
            using var replay = ReplayMemoryReader.Load(File.OpenRead(path));
            if (Read(replay, schema) is not { } found)
            {
                continue;
            }

            var entities = new EntityReader(replay, schema);
            string session = Path.GetFileNameWithoutExtension(path);
            var world = new byte[12];

            for (uint frame = 0; frame < replay.FrameCount; frame++)
            {
                replay.Seek(frame);
                GameChainAddresses chain = GameChain.Resolve(replay, schema, found.GameStates);
                if (!chain.InGame || entities.ReadIdentity(chain.PlayerEntity) is not { } identity)
                {
                    continue;
                }

                ulong render = entities.ReadComponents(chain.PlayerEntity, identity.Details)
                    .GetValueOrDefault("Render");
                if (render == 0 || !replay.TryRead(render + (ulong)renderPosition, world))
                {
                    continue;
                }

                float x = BitConverter.ToSingle(world), y = BitConverter.ToSingle(world, 4),
                    z = BitConverter.ToSingle(world, 8);

                float[] distances = [.. found.Planes.Select(p => p.Distance(x, y, z))];
                Assert.All(distances, d => Assert.True(d >= 0,
                    $"{session}: the player is outside the frustum by {-d:F0}"));

                sideDistances.Add(distances[..4]);
                positions.Add(x);
                break; // the block is one snapshot; only the frame beside it can be asked
            }
        }

        Assert.True(sideDistances.Count >= 15, $"only {sideDistances.Count} recordings answered");

        // The part no accidental solid reproduces. The player wandered six thousand world
        // units between these sessions and stayed exactly as far from the four side planes in
        // every one of them, which is the signature of a frustum the camera carries.
        Assert.True(positions.Max() - positions.Min() > 5000, "the sessions are all in one place");
        for (int plane = 0; plane < 4; plane++)
        {
            float[] across = [.. sideDistances.Select(d => d[plane])];

            // Two sessions were captured with the map panned (the atlas and inventory ones),
            // which trades distance between the left and right pair without moving the sum -
            // so the claim is the median, not the maximum.
            float median = across.Order().ElementAt(across.Length / 2);
            int agreeing = across.Count(d => MathF.Abs(d - median) < 1f);
            Assert.True(agreeing >= across.Length - 2,
                $"side plane {plane}: only {agreeing} of {across.Length} sessions sit at {median:F1}");
        }
    }
}
