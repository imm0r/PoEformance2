using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Skipping the drawing reads for entities the camera cannot see, and the evidence for it.
/// </summary>
/// <remarks>
/// A culling gate is the shape of change that goes wrong silently: it can only ever REMOVE
/// work, so when it removes the wrong work nothing crashes and nothing warns - a ray stops
/// being drawn and the first anybody hears of it is that the overlay "feels unreliable". This
/// project has paid for that failure mode more than once, so the gate is not shipped on the
/// argument that it ought to be equivalent. It is shipped on a measurement:
///
///   THE GATE AND THE OVERLAY DECIDE THE SAME THING, BY DIFFERENT ROUTES. What gets drawn is
///   decided by projecting through the matrix; what gets read is decided by the frustum. Over
///   every committed recording, at the frame each one actually read the frustum, the two agree
///   on every single point tested - which is not luck, because the frustum's corners project
///   onto the edges of the viewport exactly (CameraFrustumTests), so they are one predicate
///   computed twice.
///
/// The rest of the file pins the three properties that make the first result usable: the split
/// between drawing reads and the action read, failing open when there is no frustum, and the
/// switch actually restoring the reads.
/// </remarks>
public class FrustumGateTests
{
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

    [Fact]
    public void TheGateAndTheProjectionAgreeOnEveryEntity_InEveryRecording()
    {
        OffsetSchema schema = RealSessionTests.Schema();
        int matrixAt = schema.Structs["WorldData"].OffsetOf("W2SMatrix");
        int sessions = 0, points = 0, onScreenButCulled = 0, culledButOnScreen = 0;

        foreach (string path in Fixtures())
        {
            using var replay = ReplayMemoryReader.Load(File.OpenRead(path));
            if (!replay.ResolvedStatics.TryGetValue("GameStates", out ulong gameStates))
            {
                continue;
            }

            var reader = new WorldReader(replay, schema) { ReadVisualEntities = true };
            var matrix = new float[16];

            // ONLY the frame the frustum was actually read. A replay serves the newest bytes
            // at or before the current frame, so every later frame would be comparing a fresh
            // matrix against a photograph of the frustum - which measures the staleness of the
            // recording, not whether the two agree.
            for (uint frame = 0; frame < Math.Min(replay.FrameCount, 60u); frame++)
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

                WorldSnapshot snapshot = reader.Read(gameStates, 4096);
                if (!snapshot.InGame || snapshot.Entities.Count == 0)
                {
                    continue;
                }

                sessions++;
                foreach (WorldEntity entity in snapshot.Listed)
                {
                    // Both the point it stands on and the point a marker floats at, because
                    // the overlay draws at the second and they are not the same place.
                    foreach (float z in (float[])[entity.WorldZ, entity.HealthBarZ])
                    {
                        points++;
                        bool drawn = WorldToScreen
                            .Project(matrix, entity.WorldX, entity.WorldY, z, 1920, 1080).OnScreen;
                        bool kept = frustum.Margin(entity.WorldX, entity.WorldY, z) >= 0;

                        if (drawn && !kept)
                        {
                            onScreenButCulled++;
                        }
                        else if (kept && !drawn)
                        {
                            culledButOnScreen++;
                        }
                    }
                }

                break;
            }
        }

        Assert.True(sessions >= 15, $"only {sessions} recordings could answer");
        Assert.True(points > 500, $"only {points} points tested");

        // The direction that matters: a read skipped for something the overlay would have
        // drawn. Zero, and the margin the reader ships puts another 250 world units between
        // the gate and this ever becoming non-zero.
        Assert.Equal(0, onScreenButCulled);

        // And the harmless direction, which is zero too - so the gate is not merely safe, it
        // is exact: it skips every read the overlay has no use for and no other.
        Assert.Equal(0, culledButOnScreen);
    }

    [Fact]
    public void AnEntityTheCameraCannotSeeLosesItsAimAndBuffs_ButNeverItsAction()
    {
        // The split, through the real reader. This tests the WIRING rather than the geometry -
        // the frustum a replay serves after its one sweep is that sweep's, so which entities
        // fall outside it here is a fact about the fixture. What it pins is that falling
        // outside costs an entity exactly the two drawing reads and nothing else.
        OffsetSchema schema = RealSessionTests.Schema();
        using var replay = ReplayMemoryReader.Load(File.OpenRead(MonstersFixture));
        ulong gameStates = replay.ResolvedStatics["GameStates"];
        var reader = new WorldReader(replay, schema)
        {
            ReadAim = true,
            ReadActions = true,
            SkipOffScreenReads = true,
        };

        int culled = 0, culledWithAnAction = 0;
        for (uint frame = 5; frame < Math.Min(replay.FrameCount, 400u); frame += 10)
        {
            replay.Seek(frame);
            WorldSnapshot snapshot = reader.Read(gameStates);
            if (snapshot.Frustum is not { } frustum)
            {
                continue;
            }

            foreach (WorldEntity entity in snapshot.Listed)
            {
                if (entity.Kind != EntityKind.Monster || entity.IsFriendly)
                {
                    continue;
                }

                bool outside = frustum.Margin(entity.WorldX, entity.WorldY, entity.WorldZ) < -reader.OffScreenMargin
                    && frustum.Margin(entity.WorldX, entity.WorldY, entity.HealthBarZ) < -reader.OffScreenMargin;
                if (!outside)
                {
                    continue;
                }

                culled++;
                Assert.Null(entity.Aim);
                Assert.Null(entity.Buffs);
                if (entity.Action is not null)
                {
                    culledWithAnAction++;
                }
            }
        }

        Assert.True(culled > 0, "no entity in the fixture fell outside the frustum");

        // The half that would be a real bug rather than a saving: an off-screen monster still
        // reports what it has committed to, because evasion asks about danger to the player
        // and a boss can commit a beam from outside the view volume.
        Assert.True(culledWithAnAction > 0,
            "no culled monster kept an action - the action read is being gated after all");
    }

    [Fact]
    public void SwitchingTheGateOffRestoresTheReads_AndTheCostSaysHowMuchItSaved()
    {
        OffsetSchema schema = RealSessionTests.Schema();

        static (int Aimed, int OffScreen) Count(OffsetSchema schema, bool gate)
        {
            using var replay = ReplayMemoryReader.Load(File.OpenRead(MonstersFixture));
            ulong gameStates = replay.ResolvedStatics["GameStates"];
            var reader = new WorldReader(replay, schema) { ReadAim = true, SkipOffScreenReads = gate };

            int aimed = 0, offScreen = 0;
            for (uint frame = 5; frame < Math.Min(replay.FrameCount, 400u); frame += 10)
            {
                replay.Seek(frame);
                WorldSnapshot snapshot = reader.Read(gameStates);
                aimed += snapshot.Listed.Count(e => e.Aim is not null);
                offScreen += snapshot.Cost.OffScreen;
            }

            return (aimed, offScreen);
        }

        (int gatedAims, int reported) = Count(schema, gate: true);
        (int allAims, int noneReported) = Count(schema, gate: false);

        // The saving is real - the gate skipped reads that would otherwise have happened...
        Assert.True(reported > 0, "the gate reported skipping nothing");
        Assert.True(allAims > gatedAims, $"gate saved nothing: {gatedAims} of {allAims} aims either way");

        // ...and it is reported, which is what lets somebody watching the status line see the
        // gate working rather than take it on trust.
        Assert.Equal(0, noneReported);
        Assert.Contains("off screen", new ReadCost(1, 1, 0, 0, 0, 10, 0, 3).ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("off screen", new ReadCost(1, 1, 0, 0, 0, 10, 0).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoFrustumNothingIsSkipped()
    {
        // Failing OPEN is the property that keeps this from becoming the bug it is shaped
        // like. An unreadable field must never be able to switch a feature off quietly - a
        // recording made before anything read the block, a drifted offset after a patch, one
        // frame during a loading screen. All of them arrive here as a null frustum, and all of
        // them must read everything rather than nothing.
        OffsetSchema schema = RealSessionTests.Schema();
        using var replay = ReplayMemoryReader.Load(File.OpenRead(RealSessionTests.SceneFixturePath));
        ulong gameStates = replay.ResolvedStatics["GameStates"];
        var reader = new WorldReader(replay, schema) { ReadAim = true, SkipOffScreenReads = true };

        int frames = 0;
        for (uint frame = 0; frame < replay.FrameCount; frame++)
        {
            replay.Seek(frame);
            WorldSnapshot snapshot = reader.Read(gameStates);
            if (!snapshot.InGame || snapshot.Frustum is not null)
            {
                continue;
            }

            frames++;
            Assert.Equal(0, snapshot.Cost.OffScreen);
        }

        Assert.True(frames > 0, "the fixture had no frame without a frustum to test with");
    }

    private static string MonstersFixture
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return Path.Combine(dir.FullName, "tests", "fixtures", "session-2026-08-monsters.rec");
        }
    }
}
