using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Diagnostics;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// The matrix hunt, and the failure it exists to prevent.
/// </summary>
/// <remarks>
/// A wrong matrix was once declared "proven" because the player projected to the screen
/// centre. That check is not enough: inflating w drives every numerator to ~0, so a
/// completely wrong matrix centres the player AND everything else. These tests pin the
/// stronger property - the scene must SPREAD - which is what tells the two apart.
/// </remarks>
public class MatrixHuntTests
{
    private const ulong WorldData = 0x50_0000;
    private const int MatrixOffset = 0x1A0;

    /// <summary>The player sits at the centre of a small crowd of entities.</summary>
    private static WorldSnapshot Scene()
    {
        var player = new WorldEntity(1, 0x1000, "Metadata/Characters/Int/IntFour", EntityKind.Player, 1000, 2000, -80);
        var entities = new List<WorldEntity> { player };
        for (int i = 1; i <= 6; i++)
        {
            entities.Add(new WorldEntity(
                (uint)(1 + i), 0x1000UL + (ulong)i, "Metadata/Monsters/Skeleton", EntityKind.Monster,
                1000 + (i * 120), 2000 - (i * 90), -80));
        }

        return new WorldSnapshot(true, player, entities, new float[16]);
    }

    /// <summary>
    /// A matrix in the game's convention: clip.x is a dot with column {0,4,8,12}, clip.w
    /// with {3,7,11,15}. This one centres the player and divides by a plausible camera
    /// distance, so the scene spreads out.
    /// </summary>
    /// <remarks>
    /// The z term is not decoration. Height must move a point on screen or the hunt refuses
    /// the matrix outright, and a synthetic one without it would be testing a shape the real
    /// thing cannot have. It cancels at the player - the constant carries -playerY + playerZ -
    /// so the crowd, all standing on the same ground, projects exactly as it did before.
    /// </remarks>
    private static float[] HealthyMatrix(float playerX, float playerY, float playerZ, float w)
    {
        var m = new float[16];
        m[0] = 1; m[12] = -playerX;              // clip.x = x - playerX
        m[5] = 1; m[9] = -1;                     // clip.y = y - z ...
        m[13] = playerZ - playerY;               // ... - playerY + playerZ
        m[15] = w;                               // clip.w = constant camera distance
        return m;
    }

    private static FakeMemoryReader PlaceMatrix(float[] matrix, int offset = MatrixOffset)
    {
        var fake = new FakeMemoryReader();

        // The hunt sweeps a whole window in 0x100 chunks and a partially-mapped read fails
        // outright, exactly as ReadProcessMemory does - so map the window before overlaying.
        fake.Place(WorldData, new byte[0x900]);
        var bytes = new byte[64];
        Buffer.BlockCopy(matrix, 0, bytes, 0, 64);
        fake.Place(WorldData + (ulong)offset, bytes);
        return fake;
    }

    [Fact]
    public void Hunt_FindsAMatrixThatCentresThePlayerAndSpreadsTheScene()
    {
        FakeMemoryReader fake = PlaceMatrix(HealthyMatrix(1000, 2000, -80, 1000));

        List<ProjectionCandidate> found = MatrixHunt.Find(fake, WorldData, Scene());

        Assert.NotEmpty(found);
        ProjectionCandidate best = found[0];
        Assert.Equal(MatrixOffset, best.Offset);
        Assert.False(best.Transposed);
        Assert.True(best.PlayerOffCentre < 0.001, $"player off-centre {best.PlayerOffCentre}");
        Assert.True(best.Spread > 0.5, $"spread {best.Spread}");
        Assert.True(best.DepthResponse > 0.01, $"depth response {best.DepthResponse}");
    }

    [Fact]
    public void Hunt_RejectsACollapsedMatrix_EvenThoughThePlayerLooksPerfectlyCentred()
    {
        // THE REGRESSION. w is inflated a thousandfold, so every entity - the player
        // included - divides down onto the same point. The old single-point check called
        // this "MATRIX PROVEN"; the hunt must refuse it.
        float[] collapsed = HealthyMatrix(1000, 2000, -80, 10_000_000f);
        FakeMemoryReader fake = PlaceMatrix(collapsed);

        // The player really does land dead centre - that is precisely the trap.
        (double x, double y, double w) = MatrixHunt.Clip(collapsed, transposed: false, 1000, 2000, -80);
        Assert.True(Math.Sqrt(((x / w) * (x / w)) + ((y / w) * (y / w))) < 0.001);

        Assert.DoesNotContain(MatrixHunt.Find(fake, WorldData, Scene()), c => c.Offset == MatrixOffset);
    }

    [Fact]
    public void Hunt_ReportsNothingWhenTheWindowHoldsNoMatrix()
    {
        var empty = new FakeMemoryReader();
        empty.Place(WorldData, new byte[0x900]);

        Assert.Empty(MatrixHunt.Find(empty, WorldData, Scene()));
    }

    [Fact]
    public void RealScene_TheHuntPicksTheDocumentedMatrixOffset()
    {
        // The decisive test, against real memory: with 123 entities in view the hunt must
        // land on 0x1A0 - the offset GameHelper2's CameraStructure(0x98)+0x108 and the AHK
        // tool both give. Two things corroborate it in the same run: 0x1E0 scores
        // identically, because the game stores the matrix twice back to back exactly as the
        // reference notes, and no decoy comes close on linearity.
        var replay = ReplayMemoryReader.Load(File.OpenRead(RealSessionTests.SceneFixturePath));
        OffsetSchema schema = RealSessionTests.Schema();
        var chain = PoEformance.Core.Diagnostics.GameChain.Resolve(replay, schema, replay.ResolvedStatics["GameStates"]);
        WorldSnapshot snapshot = new WorldReader(replay, schema).Read(replay.ResolvedStatics["GameStates"]);

        List<ProjectionCandidate> found = MatrixHunt.Find(replay, chain.WorldData, snapshot);

        Assert.NotEmpty(found);
        ProjectionCandidate best = found[0];
        Assert.Equal(0x1A0, best.Offset);
        Assert.False(best.Transposed);
        Assert.Equal(0x1A0, schema.Structs["WorldData"].OffsetOf("W2SMatrix"));

        // The duplicate right after it, and a clear margin over everything else.
        Assert.Contains(found, c => c.Offset == 0x1E0 && Math.Abs(c.Linearity - best.Linearity) < 0.001);

        // AND IT MUST BE 0x1A0 RATHER THAN ITS TWIN EVERY TIME. The two score identically -
        // the same sixteen floats 0x40 apart - and List.Sort is not stable, so before the
        // offset tie-break which of them the report named was decided by the sort's internals.
        // An answer that changes between runs over the same memory is the failure this pins.
        Assert.True(
            found.FindIndex(c => c.Offset == 0x1A0) < found.FindIndex(c => c.Offset == 0x1E0),
            "the tie between the matrix and its duplicate did not resolve to the lower offset");
        Assert.True(best.Linearity > 0.9, $"linearity {best.Linearity:F4}");
        Assert.True(
            found.Where(c => c.Offset is not (0x1A0 or 0x1E0)).All(c => c.Linearity < best.Linearity - 0.15),
            "a decoy scored within 0.15 linearity of the real matrix");

        // And it is a real 3D projection: height must move the result.
        Assert.True(best.DepthResponse > 0.01, $"depth response {best.DepthResponse:F4}");
    }

    [Fact]
    public void RealScene_TheFlatTransformAt0x150_IsRefusedBecauseItIgnoresHeight()
    {
        // 0x150 spreads the scene WIDER than the real matrix and centres the player better,
        // so a spread-led ranking preferred it - this is why the scoring changed. It is not
        // a world-to-screen matrix at all: moving a point vertically does not shift it by a
        // single pixel, which is the signature of a flat 2D transform.
        //
        // It used to be RANKED and beaten. Now it is not admitted at all, because a live run
        // showed that ranking is not enough: a candidate with no depth response won on
        // centring alone against a matrix the same run had just proven. Both halves are
        // asserted here - that the hunt no longer offers it, and the measurement that
        // disqualifies it, taken directly so the evidence does not depend on the hunt.
        var replay = ReplayMemoryReader.Load(File.OpenRead(RealSessionTests.SceneFixturePath));
        OffsetSchema schema = RealSessionTests.Schema();
        var chain = PoEformance.Core.Diagnostics.GameChain.Resolve(replay, schema, replay.ResolvedStatics["GameStates"]);
        WorldSnapshot snapshot = new WorldReader(replay, schema).Read(replay.ResolvedStatics["GameStates"]);

        Assert.DoesNotContain(
            MatrixHunt.Find(replay, chain.WorldData, snapshot), c => c.Offset == 0x150 && !c.Transposed);

        float[] flat = PoEformance.Core.Diagnostics.MatrixScan.ReadMatrix(replay, chain.WorldData, 0x150)!;
        WorldEntity player = snapshot.Player!;

        (double px, double py, double pw) = MatrixHunt.Clip(flat, transposed: false, player.WorldX, player.WorldY, player.WorldZ);
        (double hx, double hy, double hw) = MatrixHunt.Clip(flat, transposed: false, player.WorldX, player.WorldY, player.WorldZ + 100f);

        Assert.Equal(px / pw, hx / hw, 6);   // a whole character's height...
        Assert.Equal(py / pw, hy / hw, 6);   // ...and not one pixel of movement

        // And it really does spread, which is what made it dangerous to a spread-led ranking.
        double minX = double.MaxValue, maxX = double.MinValue;
        foreach (WorldEntity entity in snapshot.Entities)
        {
            (double x, _, double w) = MatrixHunt.Clip(flat, transposed: false, entity.WorldX, entity.WorldY, entity.WorldZ);
            minX = Math.Min(minX, x / w);
            maxX = Math.Max(maxX, x / w);
        }

        Assert.True(maxX - minX > 4.0, $"NDC width {maxX - minX:F3}");
    }

    [Fact]
    public void RealScene_ShowsTheDecoyAt0x11C_CollapsesEverything()
    {
        // Against the real recorded scene: the block at 0x11C satisfies every structural
        // test (four unit-length rows) yet squeezes 76 entities into a hundredth of the
        // viewport. This fixture is the evidence that structural checks were not enough.
        var replay = ReplayMemoryReader.Load(File.OpenRead(RealSessionTests.SceneFixturePath));
        OffsetSchema schema = RealSessionTests.Schema();
        var chain = PoEformance.Core.Diagnostics.GameChain.Resolve(replay, schema, replay.ResolvedStatics["GameStates"]);

        WorldSnapshot snapshot = new WorldReader(replay, schema).Read(replay.ResolvedStatics["GameStates"]);
        Assert.True(snapshot.Entities.Count > 50, $"only {snapshot.Entities.Count} entities in the fixture");

        float[] decoy = PoEformance.Core.Diagnostics.MatrixScan.ReadMatrix(replay, chain.WorldData, 0x11C)!;

        double minX = double.MaxValue, maxX = double.MinValue;
        foreach (WorldEntity entity in snapshot.Entities)
        {
            (double x, double y, double w) = MatrixHunt.Clip(decoy, transposed: false, entity.WorldX, entity.WorldY, entity.WorldZ);
            minX = Math.Min(minX, x / w);
            maxX = Math.Max(maxX, x / w);
        }

        Assert.True(maxX - minX < 0.05, $"expected a collapsed scene, got NDC width {maxX - minX:F4}");
    }
}
