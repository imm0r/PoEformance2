using System.Numerics;
using PoEformance.Core.Schema;
using PoEformance.Game.Ui;

namespace PoEformance.Core.Tests;

/// <summary>
/// Measuring the game's own interface, so the map overlay can stay off it.
/// </summary>
/// <remarks>
/// WHAT THIS SETTLES, and it is a correction rather than a feature: the map overlay was drawn
/// over the orbs and the bars, and the first fix had somebody drag boxes over them because
/// nothing was thought to name the pieces of the HUD. That was simply wrong. The interface is
/// one UiElement with StringId "HUD" among the UI root's own children, and its parts - both
/// orbs, the experience bar, the left and right clusters - are its children, each carrying its
/// own position and size like anything else in the tree. This is a reading, not a description.
///
/// The tree below is shaped after the real one, which the tool's own interface browser printed
/// in game: child 97 of the root, StringId "HUD", 18 children named experience_bar,
/// LifeOrbBackgroundPBRFrame, ManaOrbBackgroundPBRFrame, life_orb, mana_orb, magma_mana_orb,
/// botleft_buttons_layout, HUDLeft, changes_note, HUDRight, chatButton, fade_from_black,
/// Skip_Cutscene, and three the game does not name.
/// </remarks>
public class HudReaderTests
{
    /// <summary>A 16:10 window, where both scale factors are 1 - so positions read directly.</summary>
    private static UiScale Window() => new(2560, 1600, 0);

    private static OffsetSchema Schema() => RealSessionTests.Schema();

    /// <summary>Where the schema says to look for the HUD first.</summary>
    private static int FirstGuess()
        => (int)Schema().Structs["HudElement"].Constants["ChildFromUiRoot"];

    /// <summary>
    /// A root with the HUD at the index the schema names, and filler either side of it.
    /// </summary>
    /// <remarks>
    /// FILLER RATHER THAN A ROOT WITH ONE CHILD, because the index is the thing most likely to
    /// be wrong and a one-child root cannot tell a lookup from a scan. The real root has 156.
    /// </remarks>
    private static (UiTree Tree, ulong Root) WithHud(
        OffsetSchema schema, int hudAt, params (string Id, Vector2 At, Vector2 Size)[] parts)
    {
        var tree = new UiTree(schema);

        const int root = 0;
        const int hud = 1;
        const int firstPart = 10;

        int[] rootChildren = new int[hudAt + 3];
        for (int i = 0; i < rootChildren.Length; i++)
        {
            // Filler siblings: real elements with ids of their own, so a scan has to actually
            // read them rather than trip over the first thing it finds.
            int index = i == hudAt ? hud : 200 + i;
            rootChildren[i] = index;
            if (index != hud)
            {
                tree.Add(index, parent: root, stringId: $"filler_{i}");
            }
        }

        int[] partIndices = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            partIndices[i] = firstPart + i;
        }

        tree.Add(root, children: rootChildren);
        tree.Add(hud, parent: root, stringId: HudReader.Id, children: partIndices);

        for (int i = 0; i < parts.Length; i++)
        {
            (string id, Vector2 at, Vector2 size) = parts[i];
            tree.Add(partIndices[i], parent: hud, stringId: id, relative: at, size: size);
        }

        return (tree, UiTree.At(root));
    }

    private static HudReader Reader(UiTree tree, OffsetSchema schema)
        => new(tree.Reader, schema, new UiElementReader(tree.Reader, schema));

    [Fact]
    public void TheInterfaceIsReadPartByPart_NotDescribed()
    {
        // The whole correction in one test: what the map keeps off comes back as rectangles
        // that were never typed in anywhere.
        OffsetSchema schema = Schema();
        (UiTree tree, ulong root) = WithHud(
            schema,
            FirstGuess(),
            ("life_orb", new Vector2(60, 1240), new Vector2(300, 300)),
            ("mana_orb", new Vector2(2200, 1240), new Vector2(300, 300)),
            ("experience_bar", new Vector2(0, 1570), new Vector2(2560, 30)));

        List<HudPart> parts = Reader(tree, schema).Read(root, Window(), []);

        Assert.Equal(3, parts.Count);
        Assert.Equal(new ScreenRect(60f, 1240f, 360f, 1540f), parts[0].Where);
        Assert.Equal("mana_orb", parts[1].Name);
        Assert.Equal(new ScreenRect(0f, 1570f, 2560f, 1600f), parts[2].Where);
        Assert.All(parts, part => Assert.Equal(PanelExtent.Element, part.From));
    }

    [Fact]
    public void ItIsFoundByItsIdWhenTheIndexHasDrifted()
    {
        // The index is a first guess and nothing more. A patch inserting one element above the
        // HUD moves everything below it, and the failure mode of trusting the index is silent:
        // the wrong element is measured and the map is kept off a rectangle that is not there.
        OffsetSchema schema = Schema();
        (UiTree tree, ulong root) = WithHud(
            schema,
            FirstGuess() + 2,
            ("life_orb", new Vector2(60, 1240), new Vector2(300, 300)));

        HudPart part = Assert.Single(Reader(tree, schema).Read(root, Window(), []));

        Assert.Equal("life_orb", part.Name);
    }

    [Fact]
    public void AContainerThatClaimsNothingIsMeasuredFromWhatItHolds()
    {
        // A SIZE OF ZERO IS A REAL READING here, as it is for a panel: HUDLeft and HUDRight are
        // containers that lay their children out and state no extent of their own. Taking them
        // at their word would keep the map off nothing while the buttons they hold sit under it.
        OffsetSchema schema = Schema();
        var tree = new UiTree(schema);

        tree.Add(0, children: [1]);
        tree.Add(1, parent: 0, stringId: HudReader.Id, children: [2]);
        tree.Add(2, parent: 1, stringId: "HUDRight", relative: new Vector2(2000, 1400), children: [3, 4]);
        tree.Add(3, parent: 2, relative: new Vector2(0, 0), size: new Vector2(100, 100));
        tree.Add(4, parent: 2, relative: new Vector2(200, 50), size: new Vector2(100, 100));

        HudPart part = Assert.Single(Reader(tree, schema).Read(UiTree.At(0), Window(), []));

        Assert.Equal(PanelExtent.Children, part.From);
        Assert.Equal(new ScreenRect(2000f, 1400f, 2300f, 1550f), part.Where);
    }

    [Fact]
    public void AHiddenPartIsNotKeptOff()
    {
        // Every cluster holds layouts that are not on screen - the other tab, the controller
        // variant. Keeping the map off those would take away a piece of it for something
        // nobody can see.
        OffsetSchema schema = Schema();
        var tree = new UiTree(schema);

        tree.Add(0, children: [1]);
        tree.Add(1, parent: 0, stringId: HudReader.Id, children: [2, 3]);
        tree.Add(2, parent: 1, stringId: "life_orb", relative: new Vector2(60, 1240), size: new Vector2(300, 300));
        tree.Add(
            3, parent: 1, stringId: "magma_mana_orb", visible: false,
            relative: new Vector2(2200, 1240), size: new Vector2(300, 300));

        HudPart part = Assert.Single(Reader(tree, schema).Read(UiTree.At(0), Window(), []));

        Assert.Equal("life_orb", part.Name);
    }

    [Fact]
    public void NothingIsMeasuredWhileTheInterfaceItselfIsHidden()
    {
        // A loading screen, or a state with no HUD. The parts keep their own flags set when the
        // container is hidden, so asking the whole chain is what tells the two apart.
        OffsetSchema schema = Schema();
        var tree = new UiTree(schema);

        tree.Add(0, children: [1]);
        tree.Add(1, parent: 0, stringId: HudReader.Id, visible: false, children: [2]);
        tree.Add(2, parent: 1, stringId: "life_orb", relative: new Vector2(60, 1240), size: new Vector2(300, 300));

        Assert.Empty(Reader(tree, schema).Read(UiTree.At(0), Window(), []));
    }

    [Fact]
    public void TheMapsAreNeverReportedAsInterface()
    {
        // THE ONE THAT WOULD BE HARD TO DIAGNOSE. If an element the minimap lives under came
        // back as a piece of interface, the minimap would be taken out of the region it is
        // meant to be drawn ON - the radar would simply stop working, while the readout showed
        // a perfectly healthy HUD. Excluded by address, so no rearrangement of the tree can
        // reintroduce it.
        OffsetSchema schema = Schema();
        var tree = new UiTree(schema);

        tree.Add(0, children: [1]);
        tree.Add(1, parent: 0, stringId: HudReader.Id, children: [2, 3]);
        tree.Add(2, parent: 1, stringId: "life_orb", relative: new Vector2(60, 1240), size: new Vector2(300, 300));
        tree.Add(3, parent: 1, stringId: "map_parent", relative: new Vector2(2100, 40), size: new Vector2(400, 400), children: [4]);
        tree.Add(4, parent: 3, stringId: "minimap", relative: new Vector2(0, 0), size: new Vector2(400, 400));

        var elements = new UiElementReader(tree.Reader, schema);
        var notThese = new HashSet<ulong>();
        elements.AndAncestors(UiTree.At(4), notThese);

        HudPart part = Assert.Single(
            new HudReader(tree.Reader, schema, elements).Read(UiTree.At(0), Window(), notThese));

        Assert.Equal("life_orb", part.Name);
    }

    [Fact]
    public void AContainerIsNotMeasuredFromAMapItHappensToHold()
    {
        // The same guard one level down, which is the case that would slip through: a container
        // claiming nothing is measured from its children, and a map among them would stretch
        // that rectangle over the very thing being drawn on.
        OffsetSchema schema = Schema();
        var tree = new UiTree(schema);

        tree.Add(0, children: [1]);
        tree.Add(1, parent: 0, stringId: HudReader.Id, children: [2]);
        tree.Add(2, parent: 1, stringId: "HUDRight", relative: new Vector2(2000, 1400), children: [3, 4]);
        tree.Add(3, parent: 2, relative: new Vector2(0, 0), size: new Vector2(100, 100));
        tree.Add(4, parent: 2, stringId: "minimap", relative: new Vector2(-1800, -1300), size: new Vector2(400, 400));

        var elements = new UiElementReader(tree.Reader, schema);
        var notThese = new HashSet<ulong>();
        elements.AndAncestors(UiTree.At(4), notThese);

        // The map's own ancestors include HUDRight itself, so the container goes entirely -
        // which is the safe direction: a piece of map drawn over is recoverable, a minimap that
        // cannot be drawn on is the feature not working.
        Assert.Empty(new HudReader(tree.Reader, schema, elements).Read(UiTree.At(0), Window(), notThese));
    }

    [Fact]
    public void NoHudElementMeansNothingToKeepOff()
    {
        // Fails towards DRAWING, deliberately, like every other unreadable answer in the
        // interface readers: a HUD that could not be found leaves the map exactly as it was,
        // and the readout says zero parts. The other direction would blank the overlay with
        // nothing to explain it.
        OffsetSchema schema = Schema();
        var tree = new UiTree(schema);

        tree.Add(0, children: [1]);
        tree.Add(1, parent: 0, stringId: "not_the_hud");

        Assert.Empty(Reader(tree, schema).Read(UiTree.At(0), Window(), []));
        Assert.Empty(Reader(tree, schema).Read(0, Window(), []));
    }

    [Fact]
    public void TheElementIsCachedAndRecheckedRatherThanScannedEveryFrame()
    {
        // The scan is a string read per root child, which is fine once and not fine sixty times
        // a second - so the address is cached. What makes that safe is the RE-CHECK: the cached
        // element still has to answer to "HUD", so a new area or a patch costs one scan instead
        // of a wrong answer. Measured as reads, because that is the whole reason it exists.
        // The HUD is put where the index does NOT expect it, so the first read has to scan the
        // root's children for the id - which is the cost this is about. Placed at the guess, the
        // first read is a lookup and there would be nothing to compare against.
        OffsetSchema schema = Schema();
        (UiTree tree, ulong root) = WithHud(
            schema, FirstGuess() + 2, ("life_orb", new Vector2(60, 1240), new Vector2(300, 300)));

        HudReader reader = Reader(tree, schema);
        UiScale window = Window();

        reader.Read(root, window, []);
        long afterFirst = tree.Reader.Reads;

        reader.Read(root, window, []);
        long second = tree.Reader.Reads - afterFirst;

        reader.Read(root, window, []);
        long third = tree.Reader.Reads - afterFirst - second;

        Assert.Equal(second, third);
        Assert.True(
            second * 4 < afterFirst,
            $"a repeat read cost {second} of the first read's {afterFirst}: the cache is not holding");
    }
}
