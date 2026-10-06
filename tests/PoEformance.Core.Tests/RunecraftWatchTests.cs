using PoEformance.Core.Schema;
using PoEformance.Features;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// The reader-thread half of the Runeshape prices: from the game chain to a drawn view.
/// </summary>
public class RunecraftWatchTests
{
    private const ulong GameStates = 0x1400003000;
    private const ulong GameStateAddr = 0x20_0000;
    private const ulong InGameStateAddr = 0x30_0000;

    private static OffsetSchema Schema()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "schema", "poe2.offsets.json")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return SchemaJson.Load(Path.Combine(dir!.FullName, "schema", "poe2.offsets.json"));
    }

    /// <summary>The chain from the GameStates static down to the interface root the fixture holds.</summary>
    private static void Chain(FakeMemoryReader fake, OffsetSchema schema)
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
        fake.Place(InGameStateAddr + (ulong)igs.OffsetOf("AreaInstanceData"), 0UL);
        fake.Place(InGameStateAddr + (ulong)igs.OffsetOf("WorldData"), 0UL);
        fake.Place(InGameStateAddr + (ulong)igs.OffsetOf("UiRootStructPtr"), UiTree.At(RunecraftPanelFixture.Root));
        fake.Place(InGameStateAddr + (ulong)igs.OffsetOf("GamepadUiRootStructPtr"), 0UL);
    }

    private static PriceBook Book(double exaltedPerDivine = 500, double regalDivine = 0.02)
    {
        var book = new PriceBook();
        Assert.True(book.Add(PriceKind.Exchange, $$"""
            {
              "core": { "rates": { "exalted": {{exaltedPerDivine}} } },
              "items": [
                { "id": "exalted", "name": "Exalted Orb", "image": "/gen/image/x/CurrencyAddModToRare.png" },
                { "id": "greater-regal-orb", "name": "Greater Regal Orb", "image": "/gen/image/x/CurrencyUpgradeMagicToRare.png" },
                { "id": "chaos", "name": "Chaos Orb", "image": "/gen/image/x/CurrencyRerollRare.png" }
              ],
              "lines": [
                { "id": "exalted", "primaryValue": {{1.0 / exaltedPerDivine}}, "volumePrimaryValue": 50 },
                { "id": "greater-regal-orb", "primaryValue": {{regalDivine}}, "volumePrimaryValue": 50 },
                { "id": "chaos", "primaryValue": 0.2, "volumePrimaryValue": 50 }
              ]
            }
            """) > 0);
        return book;
    }

    private static string? Shipped(string? path) => path switch
    {
        "Metadata/Items/Currency/CurrencyAddModToRare" => "Exalted Orb",
        "Metadata/Items/Currency/CurrencyUpgradeMagicToRare2" => "Greater Regal Orb",
        "Metadata/Items/Currency/CurrencyRerollRare" => "Chaos Orb",
        _ => null,
    };

    private static (RunecraftWatch Watch, RunecraftPanelFixture Panel) Make(OffsetSchema schema, bool enabled = true)
    {
        var panel = new RunecraftPanelFixture(schema);
        Chain(panel.Reader, schema);
        var watch = new RunecraftWatch(panel.Reader, schema, GameStates, Shipped)
        {
            Settings = new RunecraftSettings(Enabled: enabled),
        };
        return (watch, panel);
    }

    [Fact]
    public void SwitchedOffItPublishesNothing_AndReadsNothing()
    {
        OffsetSchema schema = Schema();
        (RunecraftWatch watch, RunecraftPanelFixture panel) = Make(schema, enabled: false);

        long before = panel.Reader.Reads;
        watch.Service(new UiScale(2560, 1600, 0), 0, Book());

        Assert.False(watch.View.Open);
        Assert.Equal("runecraft prices off", watch.View.Status);
        Assert.Equal(0, panel.Reader.Reads - before);
    }

    [Fact]
    public void TheOpenPanelComesBackPricedAndPlaced()
    {
        OffsetSchema schema = Schema();
        (RunecraftWatch watch, _) = Make(schema);

        watch.Service(new UiScale(2560, 1600, 0), 0, Book());
        RunecraftView view = watch.View;

        Assert.True(view.Open);
        Assert.Equal(new ScreenRect(300, 200, 1070, 1000), view.Viewport);
        Assert.Equal(5, view.Rewards.Count);
        Assert.Equal(3, view.Priced);
        Assert.Equal(2, view.Unpriced);
        Assert.Contains("3 priced", view.Status, StringComparison.Ordinal);
        Assert.Contains("root child 2", view.Named, StringComparison.Ordinal);

        // Three Exalted at one each; the Greater Regal at ten; the far-off Chaos at a hundred,
        // which is the best - and the median of the three is the Regal.
        RunecraftReward exalted = view.Rewards.Single(r => r.Label == "3x Exalted Orb");
        Assert.Equal(3.0, exalted.Total!.Value, 6);
        Assert.Equal("name", exalted.Price.Via);
        Assert.Equal(new ScreenRect(300, 80, 1000, 140), exalted.Where);
        Assert.Equal(new ScreenRect(686, 99, 986, 129), exalted.Text);
        Assert.True(exalted.TextAnchors(6f));
        Assert.Equal(680f, exalted.Before(6f));
        Assert.False(exalted.Best);

        RunecraftReward regal = view.Rewards.Single(r => r.Label == "1x Greater Regal Orb");
        Assert.Equal(10.0, regal.Total!.Value, 6);
        Assert.Equal(10.0, view.Median, 6);

        RunecraftReward chaos = view.Rewards.Single(r => r.Where.Top > 1000);
        Assert.Equal(100.0, chaos.Total!.Value, 6);
        Assert.True(chaos.Best);

        // The rolled gem and the sentinel row say why they have no price.
        Assert.Equal("no fixed reward", view.Rewards.Single(r => r.Label.StartsWith("Uncut", StringComparison.Ordinal)).Price.Via);
        Assert.Equal("no recipe", view.Rewards.Single(r => r.Label == "1x Mirror of Kalandra").Price.Via);

        // And the tab's own list carries the same rows.
        Assert.Equal(5, watch.Studied.Count);
    }

    [Fact]
    public void ANewBookRepricesAtOnce_AndTheRowsAreReStudiedOnTheClock()
    {
        OffsetSchema schema = Schema();
        (RunecraftWatch watch, RunecraftPanelFixture panel) = Make(schema);
        var scale = new UiScale(2560, 1600, 0);
        PriceBook first = Book(regalDivine: 0.02);
        watch.Service(scale, 0, first);
        Assert.Equal(10.0, watch.View.Rewards.Single(r => r.Label == "1x Greater Regal Orb").Total!.Value, 6);

        // The same book on the next tick: the rows are placed again and nothing is re-read.
        long before = panel.Reader.Reads;
        watch.Service(scale, 33, first);
        long placing = panel.Reader.Reads - before;

        // A different book, same tick: re-priced without waiting for the study.
        watch.Service(scale, 66, Book(regalDivine: 0.04));
        Assert.Equal(20.0, watch.View.Rewards.Single(r => r.Label == "1x Greater Regal Orb").Total!.Value, 6);

        // A row relabelled under the panel - another monolith's offer - shows on the next study
        // and not before. The study costs what placing alone did not: the labels and recipes.
        panel.Relabel(20, "5x Exalted Orb");
        watch.Service(scale, 100, first);
        Assert.Contains(watch.View.Rewards, r => r.Label == "3x Exalted Orb");

        before = panel.Reader.Reads;
        watch.Service(scale, RunecraftWatch.RestudyMs, first);
        long studying = panel.Reader.Reads - before;
        Assert.Contains(watch.View.Rewards, r => r.Label == "5x Exalted Orb");
        Assert.True(studying > placing, $"a study ({studying} reads) cost no more than placing ({placing})");
    }

    [Fact]
    public void TheInstallsCatalogueIsTakenUpWhenItArrives()
    {
        OffsetSchema schema = Schema();
        var panel = new RunecraftPanelFixture(schema);
        Chain(panel.Reader, schema);

        // No shipped names at all: the pictures read off the rows price the two rewards whose
        // art passed the guard, and the Greater Regal - its art refused - is priced off its
        // LABEL, which only works because this fixture paints in English.
        var watch = new RunecraftWatch(panel.Reader, schema, GameStates)
        {
            Settings = new RunecraftSettings(Enabled: true),
        };
        var scale = new UiScale(2560, 1600, 0);
        PriceBook book = Book();

        watch.Service(scale, 0, book);
        Assert.Equal(3, watch.View.Priced);
        Assert.Equal("picture", watch.View.Rewards.Single(r => r.Label == "3x Exalted Orb").Price.Via);
        Assert.Equal("label", watch.View.Rewards.Single(r => r.Label == "1x Greater Regal Orb").Price.Via);

        // The install's names land: both are named now, and the name door answers before the
        // picture and the label even where those would have done.
        watch.Catalog = RewardCatalogTests.With(
            ("Metadata/Items/Currency/CurrencyAddModToRare", new RewardName("Exalted Orb", "")),
            ("Metadata/Items/Currency/CurrencyUpgradeMagicToRare2", new RewardName("Greater Regal Orb", "")));

        watch.Service(scale, 33, book);
        Assert.Equal(3, watch.View.Priced);
        Assert.Equal("name", watch.View.Rewards.Single(r => r.Label == "3x Exalted Orb").Price.Via);
        Assert.Equal("name", watch.View.Rewards.Single(r => r.Label == "1x Greater Regal Orb").Price.Via);
    }

    [Fact]
    public void ThePriceSitsBeforeTheText_OrAtTheRowsEdgeWhenTheTextIsNotOne()
    {
        var row = new ScreenRect(300, 80, 1000, 140);
        var price = new RunecraftPrice(1, 1, "Exalted Orb", "name");

        // A text sized to itself, inside the row: the price's edge a gap before it.
        var anchored = new RunecraftReward(row, new ScreenRect(686, 99, 986, 129), "1x Exalted Orb", price, false);
        Assert.True(anchored.TextAnchors(6f));
        Assert.Equal(680f, anchored.Before(6f));

        // No element read: the row's own edge, as before.
        var none = new RunecraftReward(row, null, "1x Exalted Orb", price, false);
        Assert.False(none.TextAnchors(6f));
        Assert.Equal(994f, none.Before(6f));

        // An element spanning the row from its left edge is not a text beside the icons - a
        // drifted child index - and so is one hanging past the row's right edge, or an empty one.
        Assert.Equal(994f, (anchored with { Text = new ScreenRect(300, 80, 1000, 140) }).Before(6f));
        Assert.Equal(994f, (anchored with { Text = new ScreenRect(686, 99, 1100, 129) }).Before(6f));
        Assert.Equal(994f, (anchored with { Text = new ScreenRect(686, 99, 686, 129) }).Before(6f));
    }

    /// <summary>The fixture's rows as recipes, with the gold socket - hole 2 - holding a different rune on each.</summary>
    private static RecipeCatalog Recipes() => RecipeCatalogTests.With(
        [
            new RecipeCatalogTests.Recipe("4SlotExaltedOrb3", [7, 20, 3, 0], "Metadata/Items/Currency/CurrencyAddModToRare", "Exalted Orb", 3),
            new RecipeCatalogTests.Recipe("4SlotGreaterRegalOrb1", [7, 20, 26, 0], "Metadata/Items/Currency/CurrencyUpgradeMagicToRare2", "Greater Regal Orb"),
            new RecipeCatalogTests.Recipe("3SlotChaosOrb1", [7, 20, 32], "Metadata/Items/Currency/CurrencyRerollRare", "Chaos Orb"),
        ]);

    /// <summary>A monolith whose panel is open, framing hole 2, standing where the site says.</summary>
    private static MonolithsView Open(ChainSite site, int mode = 1)
    {
        var station = new MonolithStation(0x5000, 4, 20, 1, false, [2], mode, false, string.Empty, PanelOpen: true, string.Empty);
        var view = new MonolithView(
            9, 0x4000, MonolithFixture.DevicePath, "Expedition2RemnantActive", 0, 0, 0, 0, true,
            station, new MonolithStates(4, 1, false, string.Empty), [], 0, 0, "Opulent")
        {
            Site = site,
        };
        return new MonolithsView([view], "one", 0);
    }

    [Fact]
    public void EachRowNamesTheRuneItWouldPropagate_AndTheStrongestIsFramed()
    {
        OffsetSchema schema = Schema();
        (RunecraftWatch watch, _) = Make(schema);
        watch.Recipes = Recipes();
        var scale = new UiScale(2560, 1600, 0);
        PriceBook book = Book();

        // No monolith open: prices only.
        watch.Service(scale, 0, book);
        Assert.All(watch.View.Rewards, r => Assert.False(r.Runed));

        // The open monolith frames hole 2: Tempest on the Exalted row (no loot effect), Bond
        // on the Regal, Power on the Chaos - the strongest, so it wears the amber ring.
        watch.Service(scale, 1, book, Open(ChainSite.Alone));
        RunecraftView view = watch.View;
        RunecraftReward exalted = view.Rewards.Single(r => r.Label == "3x Exalted Orb");
        Assert.Equal("Tempest", exalted.Rune);
        Assert.Equal(1.0, exalted.RuneMult, 6);
        Assert.False(exalted.BestRune);

        RunecraftReward regal = view.Rewards.Single(r => r.Label == "1x Greater Regal Orb");
        Assert.Equal("Bond", regal.Rune);
        Assert.Equal(1.25, regal.RuneMult, 6);
        Assert.False(regal.BestRune);

        RunecraftReward chaos = view.Rewards.Single(r => r.Where.Top > 1000);
        Assert.Equal("Power", chaos.Rune);
        Assert.Equal(1.30, chaos.RuneMult, 6);
        Assert.True(chaos.BestRune);
        Assert.True(chaos.Best);
        Assert.Contains("3 with a rune", view.Status, StringComparison.Ordinal);

        // The rolled gem's recipe puts nothing on hole 2 (two holes), the sentinel has none.
        Assert.False(view.Rewards.Single(r => r.Label.StartsWith("Uncut", StringComparison.Ordinal)).Runed);
        Assert.False(view.Rewards.Single(r => r.Label == "1x Mirror of Kalandra").Runed);

        // Power already in the chain elsewhere: the Chaos row says so and Bond is the best left.
        watch.Service(scale, 2, book, Open(new ChainSite(1UL << 32, false)));
        view = watch.View;
        Assert.Equal("Power (taken)", view.Rewards.Single(r => r.Where.Top > 1000).Rune);
        Assert.Equal(1.0, view.Rewards.Single(r => r.Where.Top > 1000).RuneMult, 6);
        Assert.True(view.Rewards.Single(r => r.Label == "1x Greater Regal Orb").BestRune);

        // A monolith the game frames no socket on (the standalone one) names nothing.
        watch.Service(scale, 3, book, Open(ChainSite.Alone, mode: 0));
        Assert.All(watch.View.Rewards, r => Assert.False(r.Runed));

        // And with the chain switched off, nothing either.
        watch.Settings = new RunecraftSettings(Enabled: true, ChainEnabled: false);
        watch.Service(scale, 4, book, Open(ChainSite.Alone));
        Assert.All(watch.View.Rewards, r => Assert.False(r.Runed));
    }

    [Fact]
    public void AShutPanelPublishesShut_AndForgetsItsRows()
    {
        OffsetSchema schema = Schema();
        (RunecraftWatch watch, RunecraftPanelFixture panel) = Make(schema);
        var scale = new UiScale(2560, 1600, 0);
        watch.Service(scale, 0, Book());
        Assert.True(watch.View.Open);

        panel.SetOpen(false);
        watch.Service(scale, 33, Book());

        Assert.False(watch.View.Open);
        Assert.Equal("panel shut", watch.View.Status);
        Assert.Empty(watch.View.Rewards);
        Assert.Empty(watch.Studied);
    }
}
