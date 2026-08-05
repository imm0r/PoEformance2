using System.Numerics;
using PoEformance.Core.Schema;
using PoEformance.Game.Ui;

namespace PoEformance.Core.Tests;

/// <summary>
/// The UI element tree: turning a parent-relative layout in a fixed 2560x1600 space into
/// window pixels. Built against a synthetic tree so every rule can be isolated.
/// </summary>
public class UiElementReaderTests
{
    private const ulong Root = 0x40_0000;
    private const ulong Panel = 0x41_0000;
    private const ulong Leaf = 0x42_0000;
    private const ulong ChildArray = 0x43_0000;

    private const uint ShouldModifyPos = 0x400;
    private const uint IsVisible = 0x800;

    private static OffsetSchema Schema() => RealSessionTests.Schema();

    /// <summary>Lays down one element. Everything not given stays zero.</summary>
    private static void PlaceElement(
        FakeMemoryReader fake, OffsetSchema schema, ulong address,
        ulong parent = 0, float relX = 0, float relY = 0,
        uint flags = IsVisible, byte scaleIndex = 2, float multiplier = 1f,
        float sizeX = 0, float sizeY = 0, float modX = 0, float modY = 0)
    {
        StructDef ui = schema.Structs["UiElementBase"];

        // One contiguous block first: a real element is contiguous memory, and a
        // partially-mapped read fails outright just as ReadProcessMemory does.
        fake.Place(address, new byte[0x300]);

        fake.Place(address + (ulong)ui.OffsetOf("Self"), address); // the validity marker
        fake.Place(address + (ulong)ui.OffsetOf("ParentPtr"), parent);
        fake.Place(address + (ulong)ui.OffsetOf("RelativePosition"), relX);
        fake.Place(address + (ulong)ui.OffsetOf("RelativePosition") + 4, relY);
        fake.Place(address + (ulong)ui.OffsetOf("PositionModifier"), modX);
        fake.Place(address + (ulong)ui.OffsetOf("PositionModifier") + 4, modY);
        fake.Place(address + (ulong)ui.OffsetOf("LocalScaleMultiplier"), multiplier);
        fake.Place<uint>(address + (ulong)ui.OffsetOf("Flags"), flags);
        fake.Place(address + (ulong)ui.OffsetOf("ScaleIndex"), scaleIndex);
        fake.Place(address + (ulong)ui.OffsetOf("UnscaledSize"), sizeX);
        fake.Place(address + (ulong)ui.OffsetOf("UnscaledSize") + 4, sizeY);
    }

    [Fact]
    public void Scale_PicksTheAxisPairFromTheScaleIndex()
    {
        // An ultrawide with letterbox bars: the two factors differ, which is the only
        // situation where getting the pair wrong is visible at all.
        var scale = new UiScale(3440, 1440, Cull: 100);
        float width = (3440 - 200) / 2560f;   // 1.265625
        float height = 1440 / 1600f;          // 0.9

        Assert.Equal((width, width), scale.For(1, 1f));
        Assert.Equal((height, height), scale.For(2, 1f));
        Assert.Equal((width, height), scale.For(3, 1f));

        // An unknown index must leave the element UNSCALED rather than guess a pair.
        Assert.Equal((1f, 1f), scale.For(9, 1f));

        // The element's own multiplier compounds with whichever pair applies.
        Assert.Equal((height * 2f, height * 2f), scale.For(2, 2f));

        // A zero or non-finite multiplier is a misread, not a real "collapse to nothing".
        Assert.Equal((height, height), scale.For(2, 0f));
        Assert.Equal((height, height), scale.For(2, float.NaN));
    }

    [Fact]
    public void Position_AccumulatesUpTheParentChain_AndScalesToTheWindow()
    {
        OffsetSchema schema = Schema();
        var fake = new FakeMemoryReader();
        PlaceElement(fake, schema, Root, relX: 100, relY: 50);
        PlaceElement(fake, schema, Panel, parent: Root, relX: 30, relY: 20);
        PlaceElement(fake, schema, Leaf, parent: Panel, relX: 5, relY: 7, sizeX: 200, sizeY: 100);

        // A 16:10 window with no letterbox: both factors are 1, so UI units are pixels and
        // the accumulation is readable by eye.
        var scale = new UiScale(2560, 1600, Cull: 0);
        UiElement leaf = new UiElementReader(fake, schema).Read(Leaf, scale, withStringId: false)!;

        Assert.Equal(new Vector2(135, 77), leaf.Position);
        Assert.Equal(new Vector2(200, 100), leaf.Size);
        Assert.Equal(new Vector2(235, 127), leaf.Centre);
        Assert.True(leaf.Contains(200, 100));
        Assert.False(leaf.Contains(400, 100));
    }

    [Fact]
    public void Position_AddsTheParentModifier_OnlyWhenTheChildAsksForIt()
    {
        // The subtlety worth pinning: the modifier lives on the PARENT but is applied on the
        // CHILD's say-so. Reading it as "the parent has one, so use it" shifts every sibling.
        OffsetSchema schema = Schema();
        var scale = new UiScale(2560, 1600, Cull: 0);

        var withFlag = new FakeMemoryReader();
        PlaceElement(withFlag, schema, Root, relX: 0, relY: 0, modX: 40, modY: 60);
        PlaceElement(withFlag, schema, Leaf, parent: Root, relX: 5, relY: 5, flags: IsVisible | ShouldModifyPos);
        Assert.Equal(
            new Vector2(45, 65),
            new UiElementReader(withFlag, schema).Read(Leaf, scale, withStringId: false)!.Position);

        var withoutFlag = new FakeMemoryReader();
        PlaceElement(withoutFlag, schema, Root, relX: 0, relY: 0, modX: 40, modY: 60);
        PlaceElement(withoutFlag, schema, Leaf, parent: Root, relX: 5, relY: 5, flags: IsVisible);
        Assert.Equal(
            new Vector2(5, 5),
            new UiElementReader(withoutFlag, schema).Read(Leaf, scale, withStringId: false)!.Position);
    }

    [Fact]
    public void Position_ConvertsBetweenScaleSpaces_WhenParentAndChildDiffer()
    {
        // Parent scaled by width, child by height. The parent's accumulated position is in
        // the parent's space, so it must be converted before the child's own offset is added
        // - otherwise the two are simply summed in different units.
        OffsetSchema schema = Schema();
        var fake = new FakeMemoryReader();
        PlaceElement(fake, schema, Root, relX: 400, relY: 200, scaleIndex: 1);
        PlaceElement(fake, schema, Leaf, parent: Root, relX: 10, relY: 10, scaleIndex: 2);

        var scale = new UiScale(3440, 1440, Cull: 0);
        float widthFactor = 3440 / 2560f;
        float heightFactor = 1440 / 1600f;

        UiElement leaf = new UiElementReader(fake, schema).Read(Leaf, scale, withStringId: false)!;

        float expectedUnscaledX = (400 * widthFactor / heightFactor) + 10;
        float expectedUnscaledY = (200 * widthFactor / heightFactor) + 10;
        Assert.Equal(expectedUnscaledX * heightFactor, leaf.Position.X, 3);
        Assert.Equal(expectedUnscaledY * heightFactor, leaf.Position.Y, 3);
    }

    [Fact]
    public void Position_ShiftsByTheLetterboxBar()
    {
        OffsetSchema schema = Schema();
        var fake = new FakeMemoryReader();
        PlaceElement(fake, schema, Leaf, relX: 100, relY: 100, scaleIndex: 9); // unscaled

        Assert.Equal(
            100 + 128,
            new UiElementReader(fake, schema).Read(Leaf, new UiScale(3440, 1440, 128), false)!.Position.X);
    }

    [Fact]
    public void Visibility_RequiresTheWholeChain()
    {
        OffsetSchema schema = Schema();
        var fake = new FakeMemoryReader();
        PlaceElement(fake, schema, Root, flags: 0);                       // panel closed
        PlaceElement(fake, schema, Leaf, parent: Root, flags: IsVisible); // child still flagged

        var reader = new UiElementReader(fake, schema);

        // The child's own flag says visible, but a closed ancestor hides it - checking only
        // the element itself reports hidden UI as on screen.
        Assert.False(reader.IsVisible(Leaf));
        Assert.False(reader.IsVisible(Root));

        var open = new FakeMemoryReader();
        PlaceElement(open, schema, Root, flags: IsVisible);
        PlaceElement(open, schema, Leaf, parent: Root, flags: IsVisible);
        Assert.True(new UiElementReader(open, schema).IsVisible(Leaf));
    }

    [Fact]
    public void IsUiElement_RejectsAnythingWithoutTheSelfPointer()
    {
        OffsetSchema schema = Schema();
        var fake = new FakeMemoryReader();
        PlaceElement(fake, schema, Leaf);

        // A wrong or stale pointer lands on memory that is not an element; the self pointer
        // catches it before any coordinate is trusted.
        fake.Place(Panel, new byte[0x300]);
        fake.Place(Panel + (ulong)schema.Structs["UiElementBase"].OffsetOf("Self"), Root);

        var reader = new UiElementReader(fake, schema);
        Assert.True(reader.IsUiElement(Leaf));
        Assert.False(reader.IsUiElement(Panel));
        Assert.False(reader.IsUiElement(0));
        Assert.Null(reader.Read(Panel, new UiScale(2560, 1600, 0), false));
    }

    [Fact]
    public void Children_ReadsThePointerVector()
    {
        OffsetSchema schema = Schema();
        StructDef ui = schema.Structs["UiElementBase"];
        var fake = new FakeMemoryReader();
        PlaceElement(fake, schema, Root);
        PlaceElement(fake, schema, Panel, parent: Root);
        PlaceElement(fake, schema, Leaf, parent: Root);

        fake.Place(ChildArray, new byte[16]);
        fake.Place(ChildArray, Panel);
        fake.Place(ChildArray + 8, Leaf);
        fake.Place(Root + (ulong)ui.OffsetOf("ChildrenFirst"), ChildArray);
        fake.Place(Root + (ulong)ui.OffsetOf("ChildrenLast"), ChildArray + 16);

        var reader = new UiElementReader(fake, schema);
        Assert.Equal([Panel, Leaf], reader.Children(Root));
        Assert.Equal(Leaf, reader.Child(Root, 1));
        Assert.Equal(0UL, reader.Child(Root, 2));   // past the end
        Assert.Equal(0UL, reader.Child(Root, -1));
    }

    [Fact]
    public void Children_RejectsAnAbsurdVector()
    {
        // A torn read mid-resize can leave begin/end wildly apart; that must yield nothing
        // rather than an attempt to walk millions of entries.
        OffsetSchema schema = Schema();
        StructDef ui = schema.Structs["UiElementBase"];
        var fake = new FakeMemoryReader();
        PlaceElement(fake, schema, Root);
        fake.Place(Root + (ulong)ui.OffsetOf("ChildrenFirst"), ChildArray);
        fake.Place(Root + (ulong)ui.OffsetOf("ChildrenLast"), ChildArray + 0x100000);

        Assert.Empty(new UiElementReader(fake, schema).Children(Root));
    }

    [Fact]
    public void Position_SurvivesACyclicParentChain()
    {
        // The tree is live; a parent pointer can be stale or loop back. The depth cap must
        // make that terminate with a plausible answer instead of recursing forever.
        OffsetSchema schema = Schema();
        var fake = new FakeMemoryReader();
        PlaceElement(fake, schema, Root, parent: Leaf, relX: 1, relY: 1);
        PlaceElement(fake, schema, Leaf, parent: Root, relX: 2, relY: 2);

        UiElement leaf = new UiElementReader(fake, schema)
            .Read(Leaf, new UiScale(2560, 1600, 0), withStringId: false)!;

        Assert.True(float.IsFinite(leaf.Position.X));
        Assert.True(float.IsFinite(leaf.Position.Y));
    }
}
