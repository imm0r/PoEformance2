using System.Numerics;
using PoEformance.Core.Schema;
using PoEformance.Game.Ui;

namespace PoEformance.Core.Tests;

/// <summary>
/// Browsing the interface tree: positions, visibility, hit-testing, search and paths.
/// </summary>
/// <remarks>
/// Built against the SHIPPED schema, so an offset that drifts fails here rather than showing
/// a browser full of plausible nonsense - which is the failure mode that matters for a
/// reverse-engineering tool, since its whole job is to be believed.
/// </remarks>
public class UiTreeReaderTests
{
    /// <summary>
    /// Element n lives here. The tree fixture itself is shared - see <see cref="UiTree"/>,
    /// which HudReader's tests build on too.
    /// </summary>
    private static ulong At(int index) => UiTree.At(index);

    private static OffsetSchema Schema() => RealSessionTests.Schema();

    /// <summary>A 16:10 window, where both scale factors are 1 - so positions read directly.</summary>
    private static UiScale Window() => new(2560, 1600, 0);

    [Theory]
    // 16:10 with no letterbox, where BOTH scale factors are 1. A test that only ran this
    // would pass with the scale-space conversion deleted, because at this aspect ratio there
    // is nothing to convert - the exact shape of bug that only appears on someone else's
    // monitor.
    [InlineData(2560, 1600, 0)]
    // 16:9: width factor 1, height factor 0.9. Now the two spaces genuinely differ.
    [InlineData(2560, 1440, 0)]
    // Ultrawide with real letterbox bars, so the cull shift is in play as well.
    [InlineData(3440, 1440, 128)]
    public void DescendingAgreesWithClimbing(int width, int height, int cull)
    {
        // THE invariant. UiElementReader resolves one element by walking UP its parents;
        // the browser walks DOWN so each node costs a fixed handful of reads instead of
        // re-reading every ancestor. Two implementations of the same accumulation is exactly
        // the arrangement that drifts apart, and a browser whose rectangles disagree with the
        // map's is worse than no browser - it would be believed.
        //
        // The chain exercises every rule that is easy to drop: a child that opts into its
        // parent's position modifier, a child with a different multiplier, and a child on
        // scale index 3, which takes one factor per axis.
        OffsetSchema schema = Schema();
        var tree = new UiTree(schema)
            .Add(0, relative: new Vector2(100, 50), positionModifier: new Vector2(7, 3), children: [1])
            .Add(1, parent: 0, relative: new Vector2(20, 10), modifiesPosition: true, children: [2])
            .Add(2, parent: 1, relative: new Vector2(5, 5), scaleIndex: 1, multiplier: 2f, children: [3])
            .Add(3, parent: 2, relative: new Vector2(9, 4), scaleIndex: 3);

        var elements = new UiElementReader(tree.Reader, schema);
        var browser = new UiTreeReader(tree.Reader, schema);
        var scale = new UiScale(width, height, cull);

        Dictionary<ulong, UiNode> nodes = browser.ReadTree(
            At(0), scale, new HashSet<ulong> { At(0), At(1), At(2) });

        Assert.Equal(4, nodes.Count);
        foreach ((ulong address, UiNode node) in nodes)
        {
            UiElement? climbed = elements.Read(address, scale);
            Assert.NotNull(climbed);
            Assert.Equal(climbed!.Position.X, node.Position.X, 3);
            Assert.Equal(climbed.Position.Y, node.Position.Y, 3);
        }

        // ...and the deepest element really is somewhere non-trivial, so the agreement above
        // is not two implementations agreeing on zero.
        Assert.NotEqual(0f, nodes[At(3)].Position.X);
        Assert.NotEqual(0f, nodes[At(3)].Position.Y);
    }

    [Fact]
    public void OnlyExpandedNodesAreRead()
    {
        // The tree IS the request: what gets read follows what the user opened, not the size
        // of the interface. The game's root has over a hundred children, each with their own,
        // so reading eagerly would read the whole game's UI to show one collapsed row.
        OffsetSchema schema = Schema();
        var tree = new UiTree(schema)
            .Add(0, stringId: "root", children: [1, 2])
            .Add(1, parent: 0, stringId: "open", children: [3])
            .Add(2, parent: 0, stringId: "closed", children: [4])
            .Add(3, parent: 1, stringId: "under-open")
            .Add(4, parent: 2, stringId: "under-closed");

        Dictionary<ulong, UiNode> nodes = new UiTreeReader(tree.Reader, schema)
            .ReadTree(At(0), Window(), new HashSet<ulong> { At(0), At(1) });

        Assert.Contains(At(3), nodes.Keys);       // under the opened node
        Assert.DoesNotContain(At(4), nodes.Keys); // under the closed one

        // ...and the closed node still advertises that there is something inside it, or the
        // tree would show it as a leaf and there would be no way in.
        Assert.Equal(1, nodes[At(2)].ChildCount);
    }

    [Fact]
    public void AHiddenAncestorHidesTheChild()
    {
        // The two halves are reported separately on purpose: which of them is false is the
        // answer to "why is this not on screen", and a single boolean cannot say.
        OffsetSchema schema = Schema();
        var tree = new UiTree(schema)
            .Add(0, children: [1])
            .Add(1, parent: 0, visible: false, children: [2])
            .Add(2, parent: 1, visible: true);

        Dictionary<ulong, UiNode> nodes = new UiTreeReader(tree.Reader, schema)
            .ReadTree(At(0), Window(), new HashSet<ulong> { At(0), At(1) });

        Assert.False(nodes[At(2)].Visible);
        Assert.True(nodes[At(2)].SelfVisible);
    }

    [Fact]
    public void HitTestReturnsTheChainToTheDeepestElement()
    {
        // The chain rather than the leaf, because the browser has to open the tree down to
        // what was picked - and because the path is usually the interesting part.
        OffsetSchema schema = Schema();
        var tree = new UiTree(schema)
            .Add(0, stringId: "root", size: new Vector2(2560, 1600), children: [1])
            .Add(1, parent: 0, stringId: "panel", relative: new Vector2(100, 100),
                size: new Vector2(400, 300), children: [2])
            .Add(2, parent: 1, stringId: "slot", relative: new Vector2(20, 20), size: new Vector2(50, 50));

        List<ulong> chain = new UiTreeReader(tree.Reader, schema)
            .HitTest(At(0), new Vector2(130, 130), Window());

        Assert.Equal([At(0), At(1), At(2)], chain);
    }

    [Fact]
    public void OverlappingSiblings_TheLastOneWins()
    {
        // The game's own draw order: later siblings are painted on top, so the last one
        // containing the point is the one actually visible there. Taking the first match
        // would report whatever happens to be underneath.
        OffsetSchema schema = Schema();
        var tree = new UiTree(schema)
            .Add(0, size: new Vector2(2560, 1600), children: [1, 2])
            .Add(1, parent: 0, stringId: "below", size: new Vector2(500, 500))
            .Add(2, parent: 0, stringId: "above", size: new Vector2(500, 500));

        List<ulong> chain = new UiTreeReader(tree.Reader, schema)
            .HitTest(At(0), new Vector2(50, 50), Window());

        Assert.Equal(At(2), chain[^1]);
    }

    [Fact]
    public void ATopmostFullScreenLayerDoesNotSwallowThePick()
    {
        // The reported fault, and the reason the walk is no longer greedy. The interface has
        // full-screen transparent layers - a notification host, a tooltip surface - that
        // contain EVERY point and are drawn last. Taking the topmost match at each level and
        // descending into it commits to that layer, finds nothing under the cursor inside it,
        // and reports the layer while the panel actually being pointed at sits in an earlier
        // sibling. Which branch goes deeper cannot be known without walking both.
        OffsetSchema schema = Schema();
        var tree = new UiTree(schema)
            .Add(0, stringId: "GameUi", size: new Vector2(2560, 1600), children: [1, 5])
            .Add(1, parent: 0, stringId: "panel", size: new Vector2(600, 400), children: [2])
            .Add(2, parent: 1, stringId: "row", size: new Vector2(600, 60), children: [3])
            .Add(3, parent: 2, stringId: "slot", size: new Vector2(50, 50), children: [4])
            .Add(4, parent: 3, stringId: "icon", size: new Vector2(40, 40))
            // Drawn LAST and covering everything, with nothing under the cursor inside it.
            .Add(5, parent: 0, stringId: "notification_display", size: new Vector2(2560, 1600));

        List<ulong> chain = new UiTreeReader(tree.Reader, schema)
            .HitTest(At(0), new Vector2(20, 20), Window());

        Assert.Equal(At(4), chain[^1]);
        Assert.Equal([At(0), At(1), At(2), At(3), At(4)], chain);
    }

    [Fact]
    public void AContainerWithNoSizeIsWalkedThrough()
    {
        // Real containers report no size at all - the large map element is one - so a walk
        // that requires the parent's rectangle to contain the point never reaches their
        // children, and everything inside them is unpickable.
        OffsetSchema schema = Schema();
        var tree = new UiTree(schema)
            .Add(0, size: new Vector2(2560, 1600), children: [1])
            .Add(1, parent: 0, stringId: "group", size: Vector2.Zero, children: [2])
            .Add(2, parent: 1, stringId: "button", relative: new Vector2(300, 200), size: new Vector2(80, 30));

        List<ulong> chain = new UiTreeReader(tree.Reader, schema)
            .HitTest(At(0), new Vector2(320, 210), Window());

        Assert.Equal(At(2), chain[^1]);
    }

    [Fact]
    public void ASizedElementAwayFromThePointIsNotWalked()
    {
        // The other half of that rule. A panel that does not contain the point cannot have
        // children that do, so its subtree is skipped - which is what keeps the walk from
        // being a scan of the whole interface on every pick.
        OffsetSchema schema = Schema();
        var tree = new UiTree(schema)
            .Add(0, size: new Vector2(2560, 1600), children: [1])
            .Add(1, parent: 0, stringId: "elsewhere", relative: new Vector2(1000, 1000),
                size: new Vector2(200, 200), children: [2])
            .Add(2, parent: 1, stringId: "inside", size: new Vector2(50, 50));

        List<ulong> chain = new UiTreeReader(tree.Reader, schema)
            .HitTest(At(0), new Vector2(20, 20), Window());

        Assert.Equal([At(0)], chain);
    }

    [Fact]
    public void EqualDepthStillGoesToTheOneDrawnOnTop()
    {
        // Deepest first, draw order as the tiebreak - so the rule that was right before is
        // still right where it applies.
        OffsetSchema schema = Schema();
        var tree = new UiTree(schema)
            .Add(0, size: new Vector2(2560, 1600), children: [1, 3])
            .Add(1, parent: 0, stringId: "below", size: new Vector2(500, 500), children: [2])
            .Add(2, parent: 1, stringId: "below-leaf", size: new Vector2(100, 100))
            .Add(3, parent: 0, stringId: "above", size: new Vector2(500, 500), children: [4])
            .Add(4, parent: 3, stringId: "above-leaf", size: new Vector2(100, 100));

        List<ulong> chain = new UiTreeReader(tree.Reader, schema)
            .HitTest(At(0), new Vector2(50, 50), Window());

        Assert.Equal(At(4), chain[^1]);
    }

    [Fact]
    public void TheBudgetBoundsTheWalk()
    {
        // Following the cursor re-runs this every tick, and a pass-through container with a
        // large subtree would otherwise walk all of it. Running out returns the best found so
        // far rather than nothing.
        OffsetSchema schema = Schema();
        var tree = new UiTree(schema)
            .Add(0, size: new Vector2(2560, 1600), children: [1, 2, 3])
            .Add(1, parent: 0, size: new Vector2(500, 500))
            .Add(2, parent: 0, size: new Vector2(500, 500))
            .Add(3, parent: 0, size: new Vector2(500, 500));

        List<ulong> chain = new UiTreeReader(tree.Reader, schema)
            .HitTest(At(0), new Vector2(50, 50), Window(), budget: 1);

        Assert.Equal(At(1), chain[^1]);   // only the first child was affordable
    }

    [Fact]
    public void HiddenElementsAreNotPicked()
    {
        // A closed panel still has its rectangle. Picking it would report an element that is
        // not on screen as the thing under the cursor.
        OffsetSchema schema = Schema();
        var tree = new UiTree(schema)
            .Add(0, size: new Vector2(2560, 1600), children: [1, 2])
            .Add(1, parent: 0, stringId: "shown", size: new Vector2(500, 500))
            .Add(2, parent: 0, stringId: "hidden", visible: false, size: new Vector2(500, 500));

        List<ulong> chain = new UiTreeReader(tree.Reader, schema)
            .HitTest(At(0), new Vector2(50, 50), Window());

        Assert.Equal(At(1), chain[^1]);
    }

    [Fact]
    public void SearchMatchesTheIdAndTheDisplayedText()
    {
        // Text as well as id, because most of the tree is anonymous: the C++-built widgets
        // carry no StringId at all, so the label the game puts on screen is the only thing
        // anyone could type to find them.
        OffsetSchema schema = Schema();
        var tree = new UiTree(schema)
            .Add(0, stringId: "GameUi", children: [1, 2, 3])
            .Add(1, parent: 0, stringId: "life_orb")
            .Add(2, parent: 0, text: "Identify Items")
            .Add(3, parent: 0, stringId: "mana_orb");

        var reader = new UiTreeReader(tree.Reader, schema);
        UiScale scale = Window();

        Assert.Equal([At(1)], reader.Search(At(0), "life", scale).Select(n => n.Address));
        Assert.Equal([At(2)], reader.Search(At(0), "identify", scale).Select(n => n.Address));   // case-insensitive
        Assert.Equal(2, reader.Search(At(0), "_orb", scale).Count);
        Assert.Empty(reader.Search(At(0), "nothing here", scale));
    }

    [Fact]
    public void SearchIsNotConfusedByABlankQuery()
    {
        // "" contains "" for every element, so a query nobody typed would return the entire
        // interface - a browser that fills its result list the moment it is opened.
        OffsetSchema schema = Schema();
        var tree = new UiTree(schema).Add(0, stringId: "root", children: [1]).Add(1, parent: 0);

        Assert.Empty(new UiTreeReader(tree.Reader, schema).Search(At(0), "  ", Window()));
    }

    [Fact]
    public void IndexPathIsTheChildIndicesFromTheRoot()
    {
        // The form a UI path is written down in, and pasted back into code as. Without it the
        // browser identifies an element by an address that is different next session.
        OffsetSchema schema = Schema();
        var tree = new UiTree(schema)
            .Add(0, children: [1, 2])
            .Add(1, parent: 0)
            .Add(2, parent: 0, children: [3, 4, 5])
            .Add(3, parent: 2)
            .Add(4, parent: 2)
            .Add(5, parent: 2);

        var reader = new UiTreeReader(tree.Reader, schema);

        Assert.Equal([1, 2], reader.IndexPath(At(0), At(5)));
        Assert.Equal([0], reader.IndexPath(At(0), At(1)));
        Assert.Empty(reader.IndexPath(At(0), At(0)));   // the root is its own empty path
    }

    [Fact]
    public void IndexPathRefusesAnElementOutsideTheRoot()
    {
        // Better an empty path than a plausible one: a path that does not lead where it says
        // is worse than admitting the element is somewhere else entirely.
        OffsetSchema schema = Schema();
        var tree = new UiTree(schema)
            .Add(0, children: [1])
            .Add(1, parent: 0)
            .Add(9);   // its own root, unrelated to element 0

        Assert.Empty(new UiTreeReader(tree.Reader, schema).IndexPath(At(0), At(9)));
    }

    [Fact]
    public void TheLabelFallsBackFromIdToTextToAddress()
    {
        // Most of the tree has no id, so a browser that only shows ids shows a column of
        // blanks and identifies nothing.
        OffsetSchema schema = Schema();
        var tree = new UiTree(schema)
            .Add(0, children: [1, 2, 3])
            .Add(1, parent: 0, stringId: "life_orb", text: "ignored")
            .Add(2, parent: 0, text: "Identify Items")
            .Add(3, parent: 0);

        Dictionary<ulong, UiNode> nodes = new UiTreeReader(tree.Reader, schema)
            .ReadTree(At(0), Window(), new HashSet<ulong> { At(0) });

        Assert.Equal("life_orb", nodes[At(1)].Label);
        Assert.Equal("\"Identify Items\"", nodes[At(2)].Label);
        Assert.Equal($"0x{At(3):X}", nodes[At(3)].Label);
    }

    [Fact]
    public void ACycleDoesNotHang()
    {
        // Not hypothetical: pointers are read from a live process mid-mutation, and a child
        // list caught during a resize can point back up the tree. An infinite descent here
        // would wedge the thread that also feeds the overlay.
        OffsetSchema schema = Schema();
        var tree = new UiTree(schema)
            .Add(0, children: [1])
            .Add(1, parent: 0, children: [0]);

        Dictionary<ulong, UiNode> nodes = new UiTreeReader(tree.Reader, schema)
            .ReadTree(At(0), Window(), new HashSet<ulong> { At(0), At(1) });

        Assert.Equal(2, nodes.Count);
    }

    [Fact]
    public void ANonElementAddressReadsNothing()
    {
        // Every element points at itself, so a stale or wrong address is caught before any
        // of its other fields are believed.
        OffsetSchema schema = Schema();
        var tree = new UiTree(schema).Add(0);
        var reader = new UiTreeReader(tree.Reader, schema);

        Assert.Empty(reader.ReadTree(At(7), Window(), new HashSet<ulong>()));
        Assert.Null(reader.ReadOne(At(7), Window()));
        Assert.False(reader.IsElement(At(7)));
        Assert.True(reader.IsElement(At(0)));
    }
}
