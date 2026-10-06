using PoEformance.Core.Schema;
using PoEformance.Features;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>The reader-thread half of the monolith prices: from a snapshot to a priced, placed view.</summary>
public class MonolithWatchTests
{
    internal const ulong GameStates = 0x1400003000;
    private const ulong GameStateAddr = 0x20_0000;
    private const ulong InGameStateAddr = 0x30_0000;

    private static OffsetSchema Schema() => MonolithFixture.ShippedSchema();

    /// <summary>The chain from the GameStates static down to the fixture's area instance.</summary>
    internal static void Chain(FakeMemoryReader fake, OffsetSchema schema)
    {
        fake.Place(GameStates, GameStateAddr);

        StructDef gs = schema.Structs["GameState"];
        fake.Place(GameStateAddr + (ulong)gs.OffsetOf("CurrentStateVecLast"), 0UL);
        long entrySize = gs.Constants["StateEntrySize"];
        for (long i = 0; i < gs.Constants["TotalStates"]; i++)
        {
            fake.Place(
                GameStateAddr + (ulong)gs.OffsetOf("States") + (ulong)(i * entrySize),
                i == gs.Constants["InGameStateIndex"] ? InGameStateAddr : 0x21_0000UL + (ulong)(i * 0x1000));
        }

        StructDef igs = schema.Structs["InGameState"];
        fake.Place(InGameStateAddr + (ulong)igs.OffsetOf("AreaInstanceData"), MonolithFixture.Area);
        fake.Place(InGameStateAddr + (ulong)igs.OffsetOf("WorldData"), 0UL);
        fake.Place(InGameStateAddr + (ulong)igs.OffsetOf("UiRootStructPtr"), 0UL);
        fake.Place(InGameStateAddr + (ulong)igs.OffsetOf("GamepadUiRootStructPtr"), 0UL);
    }

    private static PriceBook Book(double exaltedPerDivine = 500)
    {
        var book = new PriceBook();
        Assert.True(book.Add(PriceKind.Exchange, $$"""
            {
              "core": { "rates": { "exalted": {{exaltedPerDivine}} } },
              "items": [
                { "id": "exalted", "name": "Exalted Orb", "image": "/gen/image/x/CurrencyAddModToRare.png" },
                { "id": "regal", "name": "Regal Orb", "image": "/gen/image/x/CurrencyUpgradeMagicToRare.png" }
              ],
              "lines": [
                { "id": "exalted", "primaryValue": {{1.0 / exaltedPerDivine}}, "volumePrimaryValue": 50 },
                { "id": "regal", "primaryValue": {{0.5 / exaltedPerDivine}}, "volumePrimaryValue": 50 }
              ]
            }
            """) > 0);
        return book;
    }

    /// <summary>Two Opulent-anchored recipes - the Regal one with Bond in its last hole - and a Bond-anchored Chaos.</summary>
    private static RecipeCatalog Catalog() => RecipeCatalogTests.With(
        [
            new RecipeCatalogTests.Recipe("5SlotExaltedOrb3", [7, 11, 20, 0, 3], "Metadata/Items/Currency/CurrencyAddModToRare", "Exalted Orb", 3),
            new RecipeCatalogTests.Recipe("5SlotRegalOrb1", [7, 11, 20, 0, 26], "Metadata/Items/Currency/CurrencyUpgradeMagicToRare", "Regal Orb"),
            new RecipeCatalogTests.Recipe("5SlotChaosOrb1", [7, 11, 26, 0, 3], "Metadata/Items/Currency/CurrencyRerollRare", "Chaos Orb"),
        ]);

    private static WorldEntity Player() => new(1, 0x1000, "Metadata/Characters/Int/IntFour", EntityKind.Player, 1000f, 1000f, 0f);

    /// <summary>The monolith, ten grid cells east of the player.</summary>
    private static WorldEntity Monolith(string icon = "Expedition2RemnantActive", int? rememberedForMs = null)
        => new(
            MonolithFixture.DeviceId, MonolithFixture.Device, MonolithFixture.DevicePath, EntityKind.Unknown,
            1000f + (10f * MapView.WorldToGrid), 1000f, 0f, TerrainHeight: 40f, MapIcon: icon, RememberedForMs: rememberedForMs);

    private static WorldSnapshot Snapshot(params WorldEntity[] entities)
        => new(true, Player(), [.. entities], new float[16], AreaHash: 7, AreaLevel: 75);

    private static MonolithWatch Make(MonolithFixture fixture, OffsetSchema schema, bool enabled = true, bool chain = true)
    {
        Chain(fixture.Reader, schema);
        return new MonolithWatch(fixture.Reader, schema, GameStates)
        {
            Settings = new RunecraftSettings(Enabled: enabled, ChainEnabled: chain),
            Catalog = Catalog(),
        };
    }

    [Fact]
    public void SwitchedOffItPublishesNothing_AndReadsNothing()
    {
        OffsetSchema schema = Schema();
        var fixture = new MonolithFixture(schema);
        MonolithWatch watch = Make(fixture, schema, enabled: false);

        long before = fixture.Reader.Reads;
        watch.Service(Snapshot(Monolith()), 0, Book());

        Assert.False(watch.View.Any);
        Assert.Equal("runecraft prices off", watch.View.Status);
        Assert.Equal(0, fixture.Reader.Reads - before);
    }

    [Fact]
    public void AMonolithComesBackReadOfferedAndPriced()
    {
        OffsetSchema schema = Schema();
        var fixture = new MonolithFixture(schema, holes: 5, anchorRune: 20, anchorHole: 2, glow: [2]);
        MonolithWatch watch = Make(fixture, schema);

        watch.Service(Snapshot(Monolith()), 0, Book());
        MonolithsView view = watch.View;

        MonolithView monolith = Assert.Single(view.Monoliths);
        Assert.Equal(MonolithFixture.DeviceId, monolith.EntityId);
        Assert.True(monolith.Listed);
        Assert.True(monolith.Station.Resolved);
        Assert.Equal(5, monolith.HoleCount);
        Assert.Equal("Opulent", monolith.Anchor);
        Assert.Equal(10f, monolith.Distance, 3);
        Assert.False(monolith.Collected);
        Assert.False(monolith.Foreign);
        Assert.False(monolith.PanelOpen);

        // Two recipes carry Opulent in hole 2; the Chaos one carries Bond there and is not
        // offered. Three Exalted at one each beat one Regal at half.
        Assert.Equal(["5SlotExaltedOrb3", "5SlotRegalOrb1"], monolith.Candidates.Select(c => c.Recipe.Id));
        Assert.Equal("Exalted Orb", monolith.Candidates[0].Reward);
        Assert.Equal(3.0, monolith.Best, 6);
        Assert.Equal(3.0, monolith.BestOffered, 6);
        Assert.Equal(3.0, view.MaxBest, 6);
        Assert.Contains("Opulent", monolith.Headline, StringComparison.Ordinal);
        Assert.Contains("hole 3/5", monolith.Headline, StringComparison.Ordinal);
        Assert.Contains("1 monoliths (1 read, 0 remembered), 1 priced", view.Status, StringComparison.Ordinal);
        Assert.Contains("1 area tags", view.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void AChosenRecipeNarrowsTheOffersToOne_AndKeepsWhatWasOffered()
    {
        OffsetSchema schema = Schema();
        var fixture = new MonolithFixture(schema, selected: "5SlotRegalOrb1", panelOpen: true);
        MonolithWatch watch = Make(fixture, schema);

        watch.Service(Snapshot(Monolith()), 0, Book());
        MonolithView monolith = Assert.Single(watch.View.Monoliths);

        Assert.Equal("5SlotRegalOrb1", Assert.Single(monolith.Candidates).Recipe.Id);
        Assert.Equal(0.5, monolith.Best, 6);
        Assert.Equal(3.0, monolith.BestOffered, 6);
        Assert.True(monolith.PanelOpen);
        Assert.Same(monolith, watch.View.Open);
        Assert.StartsWith("[chosen] Regal Orb", monolith.Headline, StringComparison.Ordinal);

        // The gold socket is the anchor's hole, so what it propagates is Opulent - a fact now,
        // and one the map should say in place of a price the player gave up.
        Assert.Equal(20, monolith.LockedRune);
        Assert.Equal("Opulent", monolith.ChosenRune);
        Assert.Equal(1.35, monolith.ChosenMult, 6);
        Assert.Equal(MonolithRuneLabel.Replace, monolith.RuneOnMap);
        Assert.Equal(5, monolith.ExpectedWaves);
    }

    [Fact]
    public void TheChainPicksTheRecipeWorthMostAllTold_NotTheDearestReward()
    {
        OffsetSchema schema = Schema();

        // The gold socket is the LAST hole: the Exalted recipe puts Tempest there (no loot
        // effect), the Regal one Bond (1.25). Five waves of Bond at 7.5 ex each beat the
        // Exalted's two and a half ex of extra reward.
        var fixture = new MonolithFixture(schema, holes: 5, anchorRune: 20, anchorHole: 2, glow: [4]);
        MonolithWatch watch = Make(fixture, schema);

        watch.Service(Snapshot(Monolith()), 0, Book());
        MonolithView monolith = Assert.Single(watch.View.Monoliths);

        Assert.Equal(3.0, monolith.Best, 6);
        Assert.Equal("5SlotRegalOrb1", monolith.ChainRecipeId);
        Assert.Equal(26, monolith.ChainRune);
        Assert.Equal("Bond", monolith.ChainRuneName);
        Assert.Equal(37.5, monolith.ChainEx, 6);
        Assert.Equal(38.0, monolith.Joint, 6);
        Assert.Equal(5, monolith.ExpectedWaves);
        Assert.Equal(-1, monolith.LockedRune);
        Assert.Equal(MonolithRuneLabel.None, monolith.RuneOnMap);
        Assert.Equal(["Bond"], monolith.Scout);
        Assert.Contains("chain unordered", watch.View.Status, StringComparison.Ordinal);

        // The offers are ordered all told; the Exalted row's Tempest is a rune worth nothing.
        Assert.Equal(["5SlotRegalOrb1", "5SlotExaltedOrb3"], monolith.Candidates.Select(c => c.Recipe.Id));
        MonolithCandidate exalted = monolith.Candidates[1];
        Assert.Equal(3, exalted.Rune);
        Assert.Equal(0.0, exalted.ChainEx, 6);
        Assert.False(exalted.Taken);
        Assert.Equal(3.0, exalted.Joint, 6);
        Assert.Contains("+37.5 ex Bond", monolith.Headline, StringComparison.Ordinal);

        // Switched off, the dearest reward leads again and nothing is valued.
        watch.Settings = new RunecraftSettings(Enabled: true, ChainEnabled: false);
        watch.Service(Snapshot(Monolith()), 1, Book());
        MonolithView plain = Assert.Single(watch.View.Monoliths);
        Assert.Equal(["5SlotExaltedOrb3", "5SlotRegalOrb1"], plain.Candidates.Select(c => c.Recipe.Id));
        Assert.Equal(-1, plain.ChainRune);
        Assert.Equal(3.0, plain.Joint, 6);
        Assert.Empty(plain.Scout);
        Assert.All(plain.Candidates, c => Assert.Equal(-1, c.Rune));
    }

    [Fact]
    public void APlannedOrderCountsOnlyTheWavesAhead_AndTheStandaloneMonolithIsOutOfTheChain()
    {
        OffsetSchema schema = Schema();
        var fixture = new MonolithFixture(schema, glow: [4]);
        MonolithWatch watch = Make(fixture, schema);
        PriceBook book = Book();

        // On an order of its own, nothing is ahead of it: Bond over its own five waves only.
        watch.PlannedOrder = [MonolithFixture.DeviceId];
        watch.Service(Snapshot(Monolith()), 0, book);
        MonolithView planned = Assert.Single(watch.View.Monoliths);
        Assert.Equal(37.5, planned.ChainEx, 6);
        Assert.Contains("chain over 1 planned", watch.View.Status, StringComparison.Ordinal);

        // A new order re-values at once, without waiting for the scan clock.
        watch.PlannedOrder = [];
        watch.Service(Snapshot(Monolith()), 1, book);
        Assert.Contains("chain unordered", watch.View.Status, StringComparison.Ordinal);

        // The standalone monolith (mode 0) frames no socket and joins no chain.
        var foreign = new MonolithFixture(schema, glow: [4], mode: 0);
        MonolithWatch other = Make(foreign, schema);
        other.Service(Snapshot(Monolith()), 0, book);
        MonolithView outside = Assert.Single(other.View.Monoliths);
        Assert.True(outside.Foreign);
        Assert.Equal(-1, outside.ChainRune);
        Assert.Equal(0.0, outside.ChainEx, 6);
        Assert.Equal(["5SlotExaltedOrb3", "5SlotRegalOrb1"], outside.Candidates.Select(c => c.Recipe.Id));
    }

    [Fact]
    public void ASealedMonolithShowsBothOnTheMap_AndTheRuneAloneWhenUnpriced()
    {
        OffsetSchema schema = Schema();
        var fixture = new MonolithFixture(schema, selected: "5SlotRegalOrb1", rerolled: true);
        MonolithWatch watch = Make(fixture, schema);

        watch.Service(Snapshot(Monolith()), 0, Book());
        MonolithView sealedUp = Assert.Single(watch.View.Monoliths);
        Assert.True(sealedUp.Rerolled);
        Assert.Equal("Opulent", sealedUp.ChosenRune);
        Assert.Equal(MonolithRuneLabel.Append, sealedUp.RuneOnMap);

        // No price for the chosen reward: the rune takes the whole label.
        Assert.Equal(MonolithRuneLabel.Replace, (sealedUp with { Best = 0 }).RuneOnMap);

        // Nothing chosen, nothing to say; a player who took the money is told the money.
        Assert.Equal(MonolithRuneLabel.None, (sealedUp with { ChosenRune = string.Empty }).RuneOnMap);
        Assert.Equal(MonolithRuneLabel.None, (sealedUp with { States = sealedUp.States with { Rerolled = false }, Best = 3, BestOffered = 3 }).RuneOnMap);
    }

    [Fact]
    public void OutOfRangeTheLastReadingIsKept_AndACollectedOneLeavesTheMap()
    {
        OffsetSchema schema = Schema();
        var fixture = new MonolithFixture(schema);
        MonolithWatch watch = Make(fixture, schema);
        PriceBook book = Book();

        watch.Service(Snapshot(Monolith()), 0, book);
        Assert.True(Assert.Single(watch.View.Monoliths).Listed);

        // The game stopped listing it: its address is stale, its reading is not.
        long before = fixture.Reader.Reads;
        watch.Service(Snapshot(Monolith(rememberedForMs: 4000)), MonolithWatch.ScanMs, book);
        MonolithView kept = Assert.Single(watch.View.Monoliths);
        Assert.False(kept.Listed);
        Assert.Equal(3.0, kept.Best, 6);
        Assert.Equal(0, fixture.Reader.Reads - before);
        Assert.Contains("0 read, 1 remembered", watch.View.Status, StringComparison.Ordinal);

        // Collected, says the icon: it keeps its row on the tab and comes off the map's best.
        watch.Service(Snapshot(Monolith(icon: "Expedition2RemnantDeactivated", rememberedForMs: 4000)), 2 * MonolithWatch.ScanMs, book);
        MonolithView collected = Assert.Single(watch.View.Monoliths);
        Assert.True(collected.Collected);
        Assert.Equal(0.0, watch.View.MaxBest);
    }

    [Fact]
    public void BetweenScansNothingIsRead_AndANewBookRepricesAtOnce()
    {
        OffsetSchema schema = Schema();
        var fixture = new MonolithFixture(schema);
        MonolithWatch watch = Make(fixture, schema);
        PriceBook book = Book();

        watch.Service(Snapshot(Monolith()), 0, book);
        long before = fixture.Reader.Reads;
        watch.Service(Snapshot(Monolith()), 33, book);
        Assert.Equal(0, fixture.Reader.Reads - before);

        watch.Service(Snapshot(Monolith()), 66, Book(exaltedPerDivine: 250));
        Assert.Equal(3.0, Assert.Single(watch.View.Monoliths).Best, 6);
        Assert.True(fixture.Reader.Reads > before);
    }

    [Fact]
    public void ANewAreaForgetsTheOldReadings_AndNoMonolithsSaysSo()
    {
        OffsetSchema schema = Schema();
        var fixture = new MonolithFixture(schema);
        MonolithWatch watch = Make(fixture, schema);
        PriceBook book = Book();

        watch.Service(Snapshot(Monolith()), 0, book);
        Assert.True(watch.View.Any);

        var elsewhere = new WorldSnapshot(true, Player(), [], new float[16], AreaHash: 8, AreaLevel: 70);
        watch.Service(elsewhere, MonolithWatch.ScanMs, book);
        Assert.False(watch.View.Any);
        Assert.Equal("no monoliths in this area", watch.View.Status);

        var loading = new WorldSnapshot(false, null, [], new float[16]);
        watch.Service(loading, 2 * MonolithWatch.ScanMs, book);
        Assert.Equal("not in an area", watch.View.Status);
    }
}
