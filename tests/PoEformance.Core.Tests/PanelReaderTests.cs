using PoEformance.Core.Schema;
using PoEformance.Game.Ui;

namespace PoEformance.Core.Tests;

/// <summary>
/// Which screen-filling panels are open, which is what decides whether the world overlay
/// draws at all.
/// </summary>
/// <remarks>
/// The offsets are UNVERIFIED, ported from GameHelper2, and a fixture built from the schema
/// follows the schema anywhere - so nothing here can vouch for one. What it covers is the
/// part that is a decision rather than an address, and the decision that matters most is
/// which way a WRONG answer falls: a check that misses a panel leaves the overlay drawn over
/// it, which is what happens with no check at all, while one that invents a panel takes the
/// overlay away and gives no reason. So everything unreadable has to read as shut.
/// </remarks>
public class PanelReaderTests
{
    private const ulong UiRoot = 0x10_0000;
    private const ulong Element = 0x20_0000;

    private const uint IsVisible = 0x800;

    private static OffsetSchema Schema() => RealSessionTests.Schema();

    /// <summary>An element that is a UiElement, showing or not, with no parent above it.</summary>
    private static void PlaceElement(FakeMemoryReader fake, OffsetSchema schema, ulong address, bool showing)
    {
        StructDef ui = schema.Structs["UiElementBase"];
        fake.Place(address, new byte[0x300]);
        fake.Place(address + (ulong)ui.OffsetOf("Self"), address);
        fake.Place<uint>(address + (ulong)ui.OffsetOf("Flags"), showing ? IsVisible : 0u);
    }

    private static PanelReader ReaderFor(FakeMemoryReader fake, OffsetSchema schema)
        => new(fake, schema, new UiElementReader(fake, schema));

    /// <summary>Points one of the interface root's panel fields at an element.</summary>
    private static FakeMemoryReader WithPanel(OffsetSchema schema, string field, bool showing)
    {
        var fake = new FakeMemoryReader();
        PlaceElement(fake, schema, UiRoot, showing: true);
        PlaceElement(fake, schema, Element, showing);
        fake.Place(UiRoot + (ulong)schema.Structs["ImportantUiElements"].OffsetOf(field), Element);
        return fake;
    }

    [Fact]
    public void APANELPointingAtNothingIsNotOpen()
    {
        // Which is the ordinary case and the reason these are pointers at all: the game nulls
        // them while no panel is open, so this is the state the overlay spends a map in.
        OffsetSchema schema = Schema();
        var fake = new FakeMemoryReader();
        PlaceElement(fake, schema, UiRoot, showing: true);

        Assert.Equal(GamePanel.None, ReaderFor(fake, schema).Open(UiRoot));
    }

    [Fact]
    public void ASHOWINGLeftPanelIsOpenAndAHIDDENOneIsNot()
    {
        OffsetSchema schema = Schema();

        Assert.Equal(GamePanel.Left, ReaderFor(WithPanel(schema, "LeftPanelPtr", showing: true), schema).Open(UiRoot));
        Assert.Equal(GamePanel.None, ReaderFor(WithPanel(schema, "LeftPanelPtr", showing: false), schema).Open(UiRoot));
    }

    [Fact]
    public void ANDTheOthersAreTheirOwnBitsRatherThanOneSharedYes()
    {
        // Named separately so a stuck one can be told from a working one. With a single bool
        // a wrong path is indistinguishable from a player who left a panel open.
        OffsetSchema schema = Schema();

        Assert.Equal(GamePanel.Right, ReaderFor(WithPanel(schema, "RightPanelPtr", showing: true), schema).Open(UiRoot));
        Assert.Equal(
            GamePanel.WorldMap,
            ReaderFor(WithPanel(schema, "WorldMapPanelPtr", showing: true), schema).Open(UiRoot));
    }

    [Fact]
    public void TWOAtOnceIsBothOfThemRatherThanWhicheverWasCheckedFirst()
    {
        OffsetSchema schema = Schema();
        StructDef root = schema.Structs["ImportantUiElements"];
        const ulong second = 0x30_0000;

        var fake = new FakeMemoryReader();
        PlaceElement(fake, schema, UiRoot, showing: true);
        PlaceElement(fake, schema, Element, showing: true);
        PlaceElement(fake, schema, second, showing: true);
        fake.Place(UiRoot + (ulong)root.OffsetOf("LeftPanelPtr"), Element);
        fake.Place(UiRoot + (ulong)root.OffsetOf("RightPanelPtr"), second);

        Assert.Equal(GamePanel.Left | GamePanel.Right, ReaderFor(fake, schema).Open(UiRoot));
    }

    [Fact]
    public void ANADDRESSThatIsNotAUiElementReadsAsSHUTRatherThanOPEN()
    {
        // The way a wrong offset fails, and the direction it has to fail in. A pointer into
        // nothing has no self-marker, so the visibility walk refuses it - and the overlay
        // stays drawn rather than disappearing for a reason nobody can see.
        OffsetSchema schema = Schema();
        var fake = new FakeMemoryReader();
        PlaceElement(fake, schema, UiRoot, showing: true);
        fake.Place(UiRoot + (ulong)schema.Structs["ImportantUiElements"].OffsetOf("LeftPanelPtr"), 0x7FFF_0000UL);

        Assert.Equal(GamePanel.None, ReaderFor(fake, schema).Open(UiRoot));
    }

    [Fact]
    public void ANDNoInterfaceAtAllIsNoPanels()
    {
        // Between areas the root does not resolve. Reporting a panel then would blank the
        // overlay on every loading screen and leave it blank if the read never came back.
        OffsetSchema schema = Schema();
        Assert.Equal(GamePanel.None, ReaderFor(new FakeMemoryReader(), schema).Open(0));
    }
}
