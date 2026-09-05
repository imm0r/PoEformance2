using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Where a read's time went.
/// </summary>
/// <remarks>
/// One number says a frame was expensive and nothing about why. The lesson the AHK tool wrote
/// down after chasing this twice is that an expensive phase is usually a REDUNDANCY - a stats
/// component read twice per tick, a scan running every frame that need not - and neither was
/// findable without a breakdown, while both were cheap to fix once seen.
///
/// A replay reads from memory rather than from a process, so the ABSOLUTE numbers here mean
/// nothing. What can be checked is that the accounting is sound: that the parts do not exceed
/// the whole, and that each phase is attributed to itself rather than all landing in one.
/// </remarks>
public class ReadCostTests
{
    private static OffsetSchema Schema()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "schema", "poe2.offsets.json")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return SchemaJson.Load(Path.Combine(dir.FullName, "schema", "poe2.offsets.json")).AsOf(RealSessionTests.PrePatchEra);
    }

    private static WorldSnapshot Read()
    {
        var replay = ReplayMemoryReader.Load(File.OpenRead(RealSessionTests.SceneFixturePath));
        return new WorldReader(replay, Schema()).Read(replay.ResolvedStatics["GameStates"]);
    }

    [Fact]
    public void AReadReportsWhatItCost()
    {
        ReadCost cost = Read().Cost;

        Assert.True(cost.TotalMs > 0, "a read that took no time at all was not measured");
        Assert.True(cost.Entities > 0);
    }

    [Fact]
    public void ThePartsDoNotExceedTheWhole()
    {
        // The accounting error that makes a breakdown worse than none: phases that overlap,
        // so the biggest one is whichever was double-counted rather than whichever was slow.
        ReadCost cost = Read().Cost;

        double parts = cost.EntitiesMs + cost.PlayerMs + cost.TerrainMs + cost.MapsMs;

        Assert.True(parts <= cost.TotalMs + 0.5, $"phases sum to {parts:F2} of a {cost.TotalMs:F2} ms read");
        Assert.True(cost.OtherMs >= 0);
    }

    [Fact]
    public void TheEntityPhaseIsWhereTheEntityWorkLands()
    {
        // Not a performance claim - a wiring one. If the timestamps bracketed the wrong
        // stages every phase would read near zero and the total would be all "other", which
        // looks like a fast read rather than a broken measurement.
        ReadCost cost = Read().Cost;

        Assert.True(
            cost.EntitiesMs > 0,
            "reading a hundred entities was attributed to no phase at all");
    }

    [Fact]
    public void ItSaysHowManyTheFilterRefused()
    {
        // The skipped count is the noise filter's only visible effect on cost, and the number
        // that says whether it is earning its place.
        var replay = ReplayMemoryReader.Load(File.OpenRead(RealSessionTests.SceneFixturePath));
        var world = new WorldReader(replay, Schema());
        WorldSnapshot filtered = world.Read(replay.ResolvedStatics["GameStates"]);

        Assert.True(filtered.Cost.Skipped > 0, "the fixture is known to hold noise entities");
    }

    [Fact]
    public void ItReadsAsOneLine()
    {
        // It goes in a status readout, so it has to fit one and name its parts.
        string line = Read().Cost.ToString();

        Assert.DoesNotContain('\n', line);
        Assert.Contains("ent", line, StringComparison.Ordinal);
        Assert.Contains("terrain", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptySnapshotClaimsNoCost()
    {
        // Outside the game there is no read to account for, and a phantom measurement in the
        // status line would be the tool reporting work it did not do.
        Assert.Equal(0, WorldSnapshot.Empty.Cost.TotalMs);
    }
}
