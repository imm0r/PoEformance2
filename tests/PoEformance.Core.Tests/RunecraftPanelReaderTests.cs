using PoEformance.Core.Schema;
using PoEformance.Game.Ui;

namespace PoEformance.Core.Tests;

/// <summary>
/// Finding the Runeshape Combinations panel and reading its rows, against the fixture.
/// </summary>
public class RunecraftPanelReaderTests
{
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

    private static UiScale Window() => new(2560, 1600, 0);

    private static RunecraftPanelReader Reader(RunecraftPanelFixture panel, OffsetSchema schema)
        => new(panel.Reader, schema, new UiElementReader(panel.Reader, schema));

    [Fact]
    public void TheOpenPanelIsFound_PastTheDecoyAndTheShutTwin()
    {
        OffsetSchema schema = Schema();
        var panel = new RunecraftPanelFixture(schema);
        RunecraftPanelReader reader = Reader(panel, schema);

        RunecraftPanelState state = reader.Resolve(UiTree.At(RunecraftPanelFixture.Root), 0);

        Assert.True(state.Open);
        Assert.Equal(UiTree.At(RunecraftPanelFixture.Gate), state.Gate);
        Assert.Equal(UiTree.At(RunecraftPanelFixture.Viewport), state.Viewport);
        Assert.Equal(UiTree.At(RunecraftPanelFixture.Container), state.Container);
        Assert.Equal(2, reader.GateIndex);
        Assert.Contains("root child 2", state.Named, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRowsAreReadWithTheirRecipesAndRewards_AndTheGuardsHold()
    {
        OffsetSchema schema = Schema();
        var panel = new RunecraftPanelFixture(schema);
        RunecraftPanelReader reader = Reader(panel, schema);
        RunecraftPanelState state = reader.Resolve(UiTree.At(RunecraftPanelFixture.Root), 0);

        List<RunecraftRow> rows = reader.Rows(state.Container);

        // Five shown rows; the hidden one is not among them.
        Assert.Equal(5, rows.Count);
        Assert.DoesNotContain(rows, row => row.Address == UiTree.At(RunecraftPanelFixture.HiddenRow));

        RunecraftRow exalted = rows.Single(row => row.Address == UiTree.At(RunecraftPanelFixture.ExaltedRow));
        Assert.Equal("3x Exalted Orb", exalted.Label);
        Assert.Equal("4SlotExaltedOrb3", exalted.RecipeId);
        Assert.Equal("Metadata/Items/Currency/CurrencyAddModToRare", exalted.RewardPath);
        Assert.Equal("Exalted Orb", exalted.RewardName);
        Assert.Equal("Art/2DItems/Currency/CurrencyAddModToRare.dds", exalted.RewardArt);
        Assert.Equal(3, exalted.RewardCount);
        Assert.Equal((1, 100), (exalted.MinLevel, exalted.MaxLevel));

        // Art that does not read as a path under Art/ is refused - the offset is computed.
        RunecraftRow regal = rows.Single(row => row.Address == UiTree.At(RunecraftPanelFixture.RegalRow));
        Assert.Equal("Metadata/Items/Currency/CurrencyUpgradeMagicToRare2", regal.RewardPath);
        Assert.Equal(string.Empty, regal.RewardArt);

        // A rolled reward: a recipe, no item, the gem level the game rolls from.
        RunecraftRow gem = rows.Single(row => row.Address == UiTree.At(RunecraftPanelFixture.GemRow));
        Assert.Equal("2SlotUncutSkillGem1", gem.RecipeId);
        Assert.Equal(string.Empty, gem.RewardPath);
        Assert.Equal(19, gem.RewardGemLevel);

        // The sentinel the reference hit: not null, not a pointer, and NOT a recipe.
        RunecraftRow sentinel = rows.Single(row => row.Address == UiTree.At(RunecraftPanelFixture.SentinelRow));
        Assert.Equal("1x Mirror of Kalandra", sentinel.Label);
        Assert.Equal(string.Empty, sentinel.RecipeId);
        Assert.Equal(string.Empty, sentinel.RewardPath);
    }

    [Fact]
    public void TheRowsArePlacedUnderTheScroll_AndTheViewportIsReported()
    {
        OffsetSchema schema = Schema();
        var panel = new RunecraftPanelFixture(schema);
        RunecraftPanelReader reader = Reader(panel, schema);
        RunecraftPanelState state = reader.Resolve(UiTree.At(RunecraftPanelFixture.Root), 0);
        List<RunecraftRow> rows = reader.Rows(state.Container);

        (List<RunecraftPlace> placed, ScreenRect? viewport) = reader.Place(state, [.. rows.Select(row => row.Address)], Window());

        Assert.Equal(new ScreenRect(300, 200, 1070, 1000), viewport);

        // The viewport sits at 300,200 and has scrolled its content up by 120: the first row's
        // top is therefore 80, not 200, and the row the game keeps far below is placed where it
        // is - clipping it is the drawing's business, and the viewport above is what it clips to.
        Assert.Equal(5, placed.Count);
        Assert.Equal(new ScreenRect(300, 80, 1000, 140), placed.Single(p => p.Address == UiTree.At(RunecraftPanelFixture.ExaltedRow)).Where);
        Assert.Equal(new ScreenRect(300, 150, 1000, 210), placed.Single(p => p.Address == UiTree.At(RunecraftPanelFixture.RegalRow)).Where);
        Assert.Equal(new ScreenRect(300, 1480, 1000, 1540), placed.Single(p => p.Address == UiTree.At(RunecraftPanelFixture.FarRow)).Where);
    }

    [Fact]
    public void TheScrollIsCountedOnce_WhicheverWayTheGameSetsTheFlag()
    {
        // With the content frame asking for its parent's modifier, the ordinary walk adds the
        // scroll; the reader must then add nothing, or the list scrolls at twice its speed.
        OffsetSchema schema = Schema();
        var panel = new RunecraftPanelFixture(schema, contentTakesModifier: true);
        RunecraftPanelReader reader = Reader(panel, schema);
        RunecraftPanelState state = reader.Resolve(UiTree.At(RunecraftPanelFixture.Root), 0);

        (List<RunecraftPlace> placed, _) = reader.Place(state, [UiTree.At(RunecraftPanelFixture.ExaltedRow)], Window());

        Assert.Equal(new ScreenRect(300, 80, 1000, 140), Assert.Single(placed).Where);
    }

    [Fact]
    public void AShutPanelIsShut_AndCostsTwoReadsAndAWalkOnceTheGateIsKnown()
    {
        OffsetSchema schema = Schema();
        var panel = new RunecraftPanelFixture(schema);
        RunecraftPanelReader reader = Reader(panel, schema);
        ulong root = UiTree.At(RunecraftPanelFixture.Root);
        Assert.True(reader.Resolve(root, 0).Open);

        panel.SetOpen(false);
        long before = panel.Reader.Reads;
        RunecraftPanelState shut = reader.Resolve(root, 33);
        long cost = panel.Reader.Reads - before;

        Assert.False(shut.Open);
        Assert.Equal("panel shut", shut.Why);
        Assert.Equal(UiTree.At(RunecraftPanelFixture.Gate), reader.Gate);

        // Two reads to trust the gate, then its own flag and a walk up to the root: nothing
        // like the scan of a hundred and fifty root children that found it.
        Assert.True(cost <= 8, $"a shut tick cost {cost} reads");

        // Reopened, it is open again from the same gate, without a scan: the gate, the walk up
        // from it, and two reads each to trust the list and the viewport.
        panel.SetOpen(true);
        before = panel.Reader.Reads;
        Assert.True(reader.Resolve(root, 66).Open);
        long reopened = panel.Reader.Reads - before;
        Assert.True(reopened <= 16, $"re-opening cost {reopened} reads - did it re-scan the root?");
    }

    [Fact]
    public void WhileNoGateIsKnownTheScanIsThrottled()
    {
        OffsetSchema schema = Schema();
        var panel = new RunecraftPanelFixture(schema, gateVisible: false);
        RunecraftPanelReader reader = Reader(panel, schema);
        ulong root = UiTree.At(RunecraftPanelFixture.Root);

        long before = panel.Reader.Reads;
        RunecraftPanelState first = reader.Resolve(root, 1000);
        long scan = panel.Reader.Reads - before;
        Assert.False(first.Open);
        Assert.Contains("no open panel", first.Why, StringComparison.Ordinal);
        Assert.True(scan > 3, "the first look did not scan");

        // Inside the pause nothing is read; past it the root is scanned again.
        before = panel.Reader.Reads;
        Assert.Equal("panel not found yet", reader.Resolve(root, 1000 + RunecraftPanelReader.SearchAgainMs - 1).Why);
        Assert.Equal(0, panel.Reader.Reads - before);

        before = panel.Reader.Reads;
        Assert.Contains("no open panel", reader.Resolve(root, 1000 + RunecraftPanelReader.SearchAgainMs).Why, StringComparison.Ordinal);
        Assert.True(panel.Reader.Reads - before > 3, "the second look did not scan");

        // And the panel opening is found on the next scan.
        panel.SetOpen(true);
        Assert.True(reader.Resolve(root, 1000 + (2 * RunecraftPanelReader.SearchAgainMs)).Open);
    }

    [Fact]
    public void AListThatStopsBeingAnElementReadsAsShut_AndTheGateIsLookedForAgain()
    {
        OffsetSchema schema = Schema();
        var panel = new RunecraftPanelFixture(schema);
        RunecraftPanelReader reader = Reader(panel, schema);
        ulong root = UiTree.At(RunecraftPanelFixture.Root);
        Assert.True(reader.Resolve(root, 0).Open);

        // The container's memory stops saying it is an element: the gate is still open, so the
        // list is re-walked from it, found missing, and the whole thing forgotten.
        ulong self = UiTree.At(RunecraftPanelFixture.Container) + (ulong)schema.Structs["UiElementBase"].OffsetOf("Self");
        panel.Reader.Place<ulong>(self, 0UL);

        RunecraftPanelState lost = reader.Resolve(root, 33);
        Assert.False(lost.Open);
        Assert.Contains("no recipe list", lost.Why, StringComparison.Ordinal);
        Assert.Equal(0UL, reader.Gate);

        // An element again, it is found by the next scan - at the remembered index first.
        panel.Reader.Place<ulong>(self, UiTree.At(RunecraftPanelFixture.Container));
        Assert.True(reader.Resolve(root, 33 + RunecraftPanelReader.SearchAgainMs).Open);
    }

    [Fact]
    public void NoInterfaceRootIsShut()
    {
        OffsetSchema schema = Schema();
        var panel = new RunecraftPanelFixture(schema);
        RunecraftPanelReader reader = Reader(panel, schema);

        Assert.False(reader.Resolve(0, 0).Open);
        Assert.Empty(reader.Rows(0));
        Assert.Empty(reader.Place(RunecraftPanelState.Shut("x"), [], Window()).Rows);
    }
}
