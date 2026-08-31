using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Entities;
using PoEformance.Game.World;

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
///  - the player's distance to the VERTICAL pair is the same number in every one of them, at
///    world positions six thousand units apart, which is what a frustum carried by a
///    player-centred camera has to look like and what an accidental solid cannot be.
///
/// And it closes the decoy: 0x11C is plane 1, so a mat4x4 read there takes planes 1 to 4 and
/// the unit vector the old invariant wanted at +0x30 was plane 4's normal.
///
/// SINCE SETTLED, by session-2026-08-frustum.rec - the one recording made after the reader
/// started reading this block every frame. The other eighteen swept it once, so a replay of
/// them could not tell a live value from a photograph, and everything built on it was
/// provisional. It is live: 669 stored reads, 669 distinct values, changing frame for frame
/// exactly as the matrix does. The same file also narrowed a claim this file used to make
/// about all four side planes - see the last test.
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

    /// <summary>The session recorded to settle how often the game rewrites the block.</summary>
    private static string FrustumFixture
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return Path.Combine(dir.FullName, "tests", "fixtures", "session-2026-08-frustum.rec");
        }
    }

    /// <summary>Every committed recording, because nineteen agreeing is the whole argument.</summary>
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
    public void TheCornersProjectOntoTheEdgesOfTheScreen_SoTheMatrixAndTheFrustumAreOneCamera()
    {
        OffsetSchema schema = RealSessionTests.Schema();
        int matrixAt = schema.Structs["WorldData"].OffsetOf("W2SMatrix");
        int answered = 0;

        foreach (string path in Fixtures())
        {
            using var replay = ReplayMemoryReader.Load(File.OpenRead(path));
            if (!replay.ResolvedStatics.TryGetValue("GameStates", out ulong gameStates))
            {
                continue;
            }

            var matrix = new float[16];
            for (uint frame = 0; frame < replay.FrameCount; frame++)
            {
                replay.Seek(frame);
                GameChainAddresses chain = GameChain.Resolve(replay, schema, gameStates);
                if (chain.WorldData == 0
                    || CameraFrustum.Read(replay, schema, chain.WorldData) is not { } frustum
                    || !replay.TryRead(
                        chain.WorldData + (ulong)matrixAt,
                        System.Runtime.InteropServices.MemoryMarshal.AsBytes(matrix.AsSpan())))
                {
                    continue;
                }

                answered++;
                string session = Path.GetFileNameWithoutExtension(path);

                // The whole claim in one number. The eight corners of the view volume are the
                // eight corners of the viewport, so a correct matrix sends every one of them
                // to an edge of NDC space - and this is the only check on the projection here
                // that needs neither a scene nor a threshold anybody had to argue about.
                double worst = frustum.WorstCornerOffEdge(matrix);
                Assert.True(worst < 1e-4, $"{session}: worst corner {worst:F5} off the viewport edge");
                Assert.True(frustum.AgreesWith(matrix));

                // Clip w is the view depth in world units, which is why the near and far
                // distances come out round. Both are the game's own clip planes, so a reading
                // that produced arbitrary numbers here would mean the two only appear to agree.
                var depths = frustum.Corners
                    .Select(c => WorldToScreen.Clip(matrix, c.X, c.Y, c.Z).W)
                    .ToList();
                Assert.Equal(4, depths.Count(d => Math.Abs(d - depths.Min()) < 0.5));
                Assert.Equal(4, depths.Count(d => Math.Abs(d - depths.Max()) < 0.5));
                Assert.True(depths.Min() is > 50 and < 400, $"{session}: near {depths.Min():F0}");
                Assert.True(depths.Max() is > 2000 and < 20000, $"{session}: far {depths.Max():F0}");

                // The planes say the same depth as the projection does, by a route that never
                // touches the matrix: the near and far faces are one slab, and its thickness
                // is far minus near.
                Assert.Equal(depths.Max() - depths.Min(), frustum.Depth, 1);
                break;
            }
        }

        Assert.True(answered >= 15, $"only {answered} recordings held both blocks");
    }

    [Fact]
    public void TheGameRewritesTheFrustumEveryFrame_JustLikeTheMatrix()
    {
        // THE QUESTION THE OTHER EIGHTEEN RECORDINGS COULD NOT ANSWER, and the reason this
        // fixture exists. They swept WorldData once at frame 5 and read only the matrix after
        // that, so the frustum was CONSTANT in every replay of them for the same reason a
        // photograph is - and a replay cannot tell that apart from a value the game never
        // updates. Everything built on the frustum was provisional until this was settled.
        //
        // The recorder is what makes it answerable: it drops a read whose bytes match the last
        // one WRITTEN for that address (RecordingMemoryReader), so a read that made it into the
        // file is a read that CHANGED. Reading the block every frame therefore turns "how often
        // does the game rewrite this" into "how many reads are in the file".
        //
        // Answer: 669 stored reads, 669 distinct values, and the gaps between them match the
        // matrix's own frame for frame. The frustum is as live as the projection it belongs to.
        using var replay = ReplayMemoryReader.Load(File.OpenRead(FrustumFixture));
        OffsetSchema schema = RealSessionTests.Schema();
        ulong gameStates = replay.ResolvedStatics["GameStates"];

        ulong worldData = 0;
        for (uint frame = 0; frame < replay.FrameCount && worldData == 0; frame++)
        {
            replay.Seek(frame);
            worldData = GameChain.Resolve(replay, schema, gameStates).WorldData;
        }

        Assert.NotEqual(0ul, worldData);

        // Walk the frames and count how many DISTINCT frustums the session held. Replaying
        // rather than parsing the file, so this measures what a consumer would actually see.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int frames = 0;
        int changedFromPrevious = 0;
        string previous = "";

        for (uint frame = 0; frame < replay.FrameCount; frame++)
        {
            replay.Seek(frame);
            if (CameraFrustum.Read(replay, schema, worldData) is not { } frustum)
            {
                continue;
            }

            frames++;
            string key = string.Join(",", frustum.Planes.Select(p => $"{p.D:R}"))
                + string.Join(",", frustum.Corners.Select(c => $"{c.X:R}{c.Y:R}{c.Z:R}"));
            seen.Add(key);
            if (previous.Length > 0 && key != previous)
            {
                changedFromPrevious++;
            }

            previous = key;
        }

        Assert.True(frames > 1000, $"only {frames} frames held a frustum");

        // Live, not lazily written: nearly every frame carries a different view volume. The
        // few that repeat are frames where the reader itself did not advance, which the matrix
        // shows identically.
        Assert.True(seen.Count > frames * 0.55,
            $"only {seen.Count} distinct frustums over {frames} frames - the block may be stale");
        Assert.True(changedFromPrevious > frames * 0.55,
            $"the frustum changed on only {changedFromPrevious} of {frames} frames");
    }

    [Fact]
    public void TheAgreementToleranceSitsInAGapFourOrdersOfMagnitudeWide()
    {
        // Pins the reason AgreementTolerance is 0.5 and not the 0.01 it was first written as.
        // The correct matrix does not read zero every frame: it and the frustum are two
        // separate reads, so the game can write between them, and two frames in eleven hundred
        // tear far enough that a tight bound would report drift on a working camera. A drift
        // alarm that cries wolf twice a session is one nobody reads.
        //
        // The bound has room to be generous because the gap is enormous - a matrix read four
        // bytes out misses by 52 at its most forgiving and by infinity on most frames.
        OffsetSchema schema = RealSessionTests.Schema();
        int matrixAt = schema.Structs["WorldData"].OffsetOf("W2SMatrix");
        using var replay = ReplayMemoryReader.Load(File.OpenRead(FrustumFixture));
        ulong gameStates = replay.ResolvedStatics["GameStates"];

        var honest = new List<double>();
        var drifted = new List<double>();
        var real = new float[16];
        var wrong = new float[16];

        for (uint frame = 0; frame < replay.FrameCount; frame++)
        {
            replay.Seek(frame);
            GameChainAddresses chain = GameChain.Resolve(replay, schema, gameStates);
            if (chain.WorldData == 0
                || CameraFrustum.Read(replay, schema, chain.WorldData) is not { } frustum)
            {
                continue;
            }

            if (replay.TryRead(chain.WorldData + (ulong)matrixAt,
                    System.Runtime.InteropServices.MemoryMarshal.AsBytes(real.AsSpan())))
            {
                honest.Add(frustum.WorstCornerOffEdge(real));
            }

            // Four bytes out is the drift this alarm exists to catch, and the most forgiving
            // wrong reading available - eight either way is infinite on every frame.
            if (replay.TryRead(chain.WorldData + (ulong)(matrixAt - 4),
                    System.Runtime.InteropServices.MemoryMarshal.AsBytes(wrong.AsSpan())))
            {
                drifted.Add(frustum.WorstCornerOffEdge(wrong));
            }
        }

        Assert.True(honest.Count > 1000, $"only {honest.Count} frames");
        honest.Sort();

        // Typical is six orders of magnitude inside the bound; the tearing outliers are one.
        Assert.True(honest[honest.Count / 2] < 1e-4, $"median {honest[honest.Count / 2]:E2}");
        Assert.True(honest[^1] < CameraFrustum.AgreementTolerance,
            $"the worst honest reading, {honest[^1]:E2}, is outside the tolerance");
        Assert.True(honest[^1] > 1e-3,
            "no frame tore at all - if that holds up, the tolerance could be tightened");

        // And the wrong reading is nowhere near it, on every single frame.
        Assert.All(drifted, d => Assert.True(d > CameraFrustum.AgreementTolerance * 10,
            $"a matrix read four bytes out scored {d:F3}"));
    }

    [Fact]
    public void TheReaderPutsTheFrustumOnTheSnapshot_AndItContainsThePlayer()
    {
        // The per-frame read, end to end through the real reader rather than by hand. The
        // committed recordings only hold the block from the frame their one sweep touched,
        // so this asks the frames that CAN answer and asserts that the answer arrives.
        OffsetSchema schema = RealSessionTests.Schema();
        using var replay = ReplayMemoryReader.Load(File.OpenRead(RealSessionTests.MapFixturePath));
        var reader = new WorldReader(replay, schema);
        ulong gameStates = replay.ResolvedStatics["GameStates"];

        int withFrustum = 0, containingPlayer = 0;
        for (uint frame = 5; frame < Math.Min(replay.FrameCount, 60u); frame++)
        {
            replay.Seek(frame);
            WorldSnapshot snapshot = reader.Read(gameStates);
            if (snapshot.Frustum is not { } frustum || snapshot.Player is not { } player)
            {
                continue;
            }

            withFrustum++;
            if (frustum.Contains(player.WorldX, player.WorldY, player.WorldZ))
            {
                containingPlayer++;
            }

            Assert.Equal(CameraFrustum.PlaneCount, frustum.Planes.Count);
            Assert.Equal(CameraFrustum.CornerCount, frustum.Corners.Count);
        }

        Assert.True(withFrustum > 0, "the reader never produced a frustum");
        Assert.Equal(withFrustum, containingPlayer);
    }

    [Fact]
    public void AWrongMatrixDoesNotAgreeWithTheFrustum()
    {
        // The control on the check itself. Agreement is only worth reporting if disagreement
        // is possible, and the obvious near miss is the strongest case to try: 0x1E0 holds
        // the SAME sixteen floats as 0x1A0 - the game stores the matrix twice - so the decoy
        // used here is the flat transform at 0x150, which the hunt already refuses on depth.
        OffsetSchema schema = RealSessionTests.Schema();
        using var replay = ReplayMemoryReader.Load(File.OpenRead(RealSessionTests.SceneFixturePath));
        GameChainAddresses chain = GameChain.Resolve(replay, schema, replay.ResolvedStatics["GameStates"]);
        CameraFrustum? frustum = CameraFrustum.Read(replay, schema, chain.WorldData);
        Assert.NotNull(frustum);

        var real = new float[16];
        Assert.True(replay.TryRead(
            chain.WorldData + (ulong)schema.Structs["WorldData"].OffsetOf("W2SMatrix"),
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(real.AsSpan())));
        Assert.True(frustum.AgreesWith(real));

        float[] flat = PoEformance.Core.Diagnostics.MatrixScan.ReadMatrix(replay, chain.WorldData, 0x150)!;
        Assert.False(frustum.AgreesWith(flat));

        // And four bytes off the real one - the drift this is meant to catch - fails too.
        float[] shifted = PoEformance.Core.Diagnostics.MatrixScan.ReadMatrix(replay, chain.WorldData, 0x1A4)!;
        Assert.False(frustum.AgreesWith(shifted));
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
        Assert.True(positions.Max() - positions.Min() > 5000, "the sessions are all in one place");

        // The part no accidental solid reproduces, and it is the VERTICAL pair. Planes 2 and 3
        // sit at 450.3 and 644.4 from the player in EVERY recording - nineteen sessions, five
        // game launches, world positions six thousand units apart, not one exception. A solid
        // that happened to contain the player would not follow him to the decimal.
        foreach (int plane in (int[])[2, 3])
        {
            float[] across = [.. sideDistances.Select(d => d[plane])];
            Assert.All(across, d => Assert.Equal(across[0], d, 1));
        }

        // THE HORIZONTAL PAIR IS NOT, and the correction is worth more than the tidier claim
        // it replaces. This test used to assert one number for all four planes with two
        // sessions allowed to differ; a nineteenth recording made it three, which is the point
        // at which "outliers" stops being an honest word. Planes 0 and 1 take one of two
        // values - 1140.1/1140.1 in sixteen sessions and 1326.5/907.2 in three - and the two
        // do not even sum alike (2280.2 against 2233.7), so it is not the camera panning and
        // trading one for the other. Something about the viewport differs between those
        // captures. What survives, and is all the frustum needs to be usable, is that the
        // player is inside all six in every one of them.
        var horizontal = sideDistances
            .Select(d => $"{d[0]:F1}/{d[1]:F1}")
            .Distinct()
            .ToList();

        Assert.True(horizontal.Count <= 2,
            $"the horizontal half-extents take {horizontal.Count} values: {string.Join(", ", horizontal)}");
        foreach (float[] distances in sideDistances)
        {
            Assert.True(distances[0] + distances[1] > 2200, $"horizontal extent {distances[0] + distances[1]:F1}");
        }
    }
}
