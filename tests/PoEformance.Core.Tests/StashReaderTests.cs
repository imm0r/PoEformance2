using PoEformance.Core.Schema;
using PoEformance.Game.Items;

namespace PoEformance.Core.Tests;

/// <summary>
/// Walking every inventory the player has, stash tabs included.
/// </summary>
/// <remarks>
/// The fixture follows the schema wherever it points, so nothing here vouches for an address.
/// What it covers is the walk: that one item spanning six cells is one item and not six, that a
/// pointer which is not an inventory is refused rather than parsed, that a length out of game
/// memory cannot become a loop of any size, and that the two-server-data-struct hop is taken.
///
/// The de-duplication is the one that would otherwise go unnoticed for a long time: the item
/// list holds one entry per occupied CELL, so a tab of large items reports several times what is
/// in it, and every number built on that count is wrong by a plausible-looking factor.
/// </remarks>
public class StashReaderTests
{
    private const ulong ServerData = 0x10_0000;
    private const ulong Inner = 0x11_0000;
    private const ulong Vector = 0x12_0000;
    private const ulong Entries = 0x13_0000;
    private const ulong Inventory = 0x14_0000;
    private const ulong ItemList = 0x15_0000;
    private const ulong Slots = 0x16_0000;
    private const ulong Items = 0x17_0000;

    private static OffsetSchema Schema() => RealSessionTests.Schema();

    /// <summary>One inventory holding the given items, reached through the two-struct hop.</summary>
    private static FakeMemoryReader Stash(
        OffsetSchema schema,
        int id,
        (int Left, int Top, int Right, int Bottom, int Item)[] cells,
        int columns = 12,
        int rows = 12,
        bool flat = false)
    {
        StructDef server = schema.Structs["ServerDataStructure"];
        StructDef array = schema.Structs["InventoryArray"];
        StructDef inventory = schema.Structs["Inventory"];
        StructDef slot = schema.Structs["InventoryItem"];

        int entrySize = (int)array.Constants["EntrySize"];
        var fake = new FakeMemoryReader();

        // The inventory vector hangs off the INNER struct, reached through a vector of
        // pointers - unless the fixture is asked for the flat layout, which the reader also
        // accepts.
        ulong holder = flat ? ServerData : Inner;
        if (!flat)
        {
            fake.Place(ServerData + (ulong)schema.Structs["ServerDataOffsets"].OffsetOf("PlayerServerData"), Vector);
            fake.Place(Vector, Inner);
        }

        fake.Place(holder + (ulong)server.OffsetOf("PlayerInventories"), Entries);
        fake.Place(holder + (ulong)server.OffsetOf("PlayerInventories") + 8, Entries + (ulong)entrySize);

        fake.Place(Entries + (ulong)array.OffsetOf("InventoryId"), id);
        fake.Place(Entries + (ulong)array.OffsetOf("InventoryPtr0"), Inventory);

        fake.Place(Inventory + (ulong)inventory.OffsetOf("TotalBoxes"), columns);
        fake.Place(Inventory + (ulong)inventory.OffsetOf("TotalBoxesY"), rows);
        fake.Place(Inventory + (ulong)inventory.OffsetOf("ItemList"), ItemList);
        fake.Place(Inventory + (ulong)inventory.OffsetOf("ItemListLast"), ItemList + (8 * (ulong)cells.Length));

        for (int i = 0; i < cells.Length; i++)
        {
            ulong here = Slots + ((ulong)i * 0x100);
            fake.Place(ItemList + (8 * (ulong)i), here);
            fake.Place(here + (ulong)slot.OffsetOf("Item"), Items + ((ulong)cells[i].Item * 0x1000));
            fake.Place(here + (ulong)slot.OffsetOf("SlotStart"), cells[i].Left);
            fake.Place(here + (ulong)slot.OffsetOf("SlotStartY"), cells[i].Top);
            fake.Place(here + (ulong)slot.OffsetOf("SlotEnd"), cells[i].Right);
            fake.Place(here + (ulong)slot.OffsetOf("SlotEndY"), cells[i].Bottom);
        }

        return fake;
    }

    [Fact]
    public void ASTASHTabIsJustAnotherInventory()
    {
        // The whole reason this is possible: a tab is not a separate structure to be found, it
        // is a grid with an id in the same vector as the backpack.
        OffsetSchema schema = Schema();
        FakeMemoryReader fake = Stash(schema, id: 143, [(0, 0, 1, 1, 0)], columns: 24, rows: 24);

        StashInventory tab = Assert.Single(new StashReader(fake, schema).Read(ServerData));

        Assert.Equal(143, tab.Id);
        Assert.Equal(InventoryKind.Stash, tab.Kind);
        Assert.Equal((24, 24), (tab.Columns, tab.Rows));
        Assert.Equal("Stash tab 143", tab.Called);
    }

    [Fact]
    public void WHENTheLeagueIsNotWhereTheSchemaSaysTheStructIsSweptForIt()
    {
        // The read that answers "where did it go". A drifted string cannot be guessed at, and
        // it cannot be found in an old recording either - a replay only holds the reads the
        // running build made, so a field nobody read is simply absent. This is the read that
        // puts the answer in the NEXT one.
        OffsetSchema schema = Schema();
        FakeMemoryReader fake = Stash(schema, id: 1, [(0, 0, 1, 1, 0)]);

        // The struct's own bytes, from just past the inventory vector - a read that crosses
        // an unmapped page fails WHOLE, so the sweep has to survive a window that is only
        // partly there, and a fixture of nothing but headers would never show that.
        fake.Place(Inner + 0x330, new byte[0x2CD0]);

        // The league, moved. Eight characters is already too many for the inline buffer -
        // it holds seven and a terminator - so even "Standard" is kept elsewhere.
        fake.PlaceStdWString(Inner + 0x2210, "Standard", 0x18_8000);

        // And a short one, which the header carries itself.
        fake.PlaceStdWString(Inner + 0x2300, "Abyss", 0);

        // And a longer one kept elsewhere, as a hardcore league name would be.
        fake.PlaceStdWString(Inner + 0x2400, "HC Runes of Aldur", 0x18_0000);

        IReadOnlyList<StashReader.FoundText> found =
            new StashReader(fake, schema).StringsIn(Inner, 0x300, 0x3000);

        Assert.Contains(found, one => one.Offset == 0x2210 && one.Text == "Standard");
        Assert.Contains(found, one => one.Offset == 0x2300 && one.Text == "Abyss");
        Assert.Contains(found, one => one.Offset == 0x2400 && one.Text == "HC Runes of Aldur");

        // And nothing at the league's own offset, which is the shape of the finding this was
        // written for: the field is not on this struct at all.
        Assert.DoesNotContain(found, one => one.Offset == schema.Structs["ServerDataOffsets"].OffsetOf("League"));
    }

    [Fact]
    public void ANDTheSweepIsBoundedRatherThanLedAroundByWhatItReads()
    {
        // Every row of a struct is offered to the string test, so a window full of numbers that
        // happen to look like lengths must not become thousands of reads on the read thread.
        OffsetSchema schema = Schema();
        FakeMemoryReader fake = Stash(schema, id: 1, [(0, 0, 1, 1, 0)]);
        fake.Place(Inner + 0x330, new byte[0x2CD0]);

        for (var i = 0; i < 200; i++)
        {
            fake.PlaceStdWString(Inner + 0x1000 + ((ulong)i * 0x20), $"string number {i}", 0x19_0000 + ((ulong)i * 0x80));
        }

        int found = new StashReader(fake, schema).StringsIn(Inner, 0x300, 0x3000).Count;

        Assert.Equal(StashReader.MostStrings, found);
    }

    [Fact]
    public void ANDASweepOfSomethingThatIsNotTheStructFindsNothingRatherThanGuessing()
    {
        OffsetSchema schema = Schema();
        FakeMemoryReader fake = Stash(schema, id: 1, [(0, 0, 1, 1, 0)]);

        Assert.Empty(new StashReader(fake, schema).StringsIn(0, 0x300, 0x3000));
        Assert.Empty(new StashReader(fake, schema).StringsIn(Inner, 0x3000, 0x300));
    }

    [Theory]
    [InlineData(1, InventoryKind.Backpack)]
    [InlineData(2, InventoryKind.Equipped)]
    [InlineData(12, InventoryKind.Equipped)]
    [InlineData(13, InventoryKind.Stash)]
    [InlineData(27, InventoryKind.Stash)]
    [InlineData(143, InventoryKind.Stash)]
    public void WHATAnInventoryIsComesFromItsId(int id, InventoryKind expected)
    {
        // Written as a range rather than a list of known tab ids, because tab ids are not
        // knowable - they depend on how many tabs somebody has bought.
        Assert.Equal(expected, StashReader.KindOf(id));
    }

    [Fact]
    public void ONEItemAcrossSixCellsIsONEItem()
    {
        // The item list holds one entry per occupied CELL. Counted as they come, a tab of body
        // armour reports six times what is in it - and every number built on that count is
        // wrong by a factor that looks entirely plausible.
        OffsetSchema schema = Schema();
        FakeMemoryReader fake = Stash(schema,
            id: 1,
            [
                (0, 0, 2, 3, 0), (1, 0, 2, 3, 0), (0, 1, 2, 3, 0),
                (1, 1, 2, 3, 0), (0, 2, 2, 3, 0), (1, 2, 2, 3, 0),
                (5, 5, 6, 6, 1),
            ]);

        StashInventory bag = Assert.Single(new StashReader(fake, schema).Read(ServerData));

        Assert.Equal(2, bag.Items.Count);

        StashedItem armour = bag.Items[0];
        Assert.Equal((0, 0), (armour.Left, armour.Top));
        Assert.Equal((2, 3), (armour.Width, armour.Height));

        StashedItem small = bag.Items[1];
        Assert.Equal((1, 1), (small.Width, small.Height));
    }

    [Fact]
    public void ANDTheFirstEntryIsTheOneKeptBecauseItCarriesTheWholeRectangle()
    {
        // Which is why de-duplicating is safe: the later entries say nothing the first did not.
        OffsetSchema schema = Schema();
        FakeMemoryReader fake = Stash(schema, id: 1, [(3, 4, 5, 7, 0), (4, 4, 5, 7, 0)]);

        StashedItem only = Assert.Single(Assert.Single(new StashReader(fake, schema).Read(ServerData)).Items);

        Assert.Equal((3, 4), (only.Left, only.Top));
        Assert.Equal((2, 3), (only.Width, only.Height));
    }

    [Fact]
    public void APointerThatIsNotAnInventoryIsRefusedRatherThanWalked()
    {
        // A stale entry read as an inventory walks a garbage item list on the reader thread.
        // The grid dimensions are the test: an inventory always has one, and something else
        // almost never reads as a plausible grid.
        OffsetSchema schema = Schema();

        Assert.Empty(new StashReader(Stash(schema, 1, [], columns: 0), schema).Read(ServerData));
        Assert.Empty(new StashReader(Stash(schema, 1, [], columns: 9_999), schema).Read(ServerData));
        Assert.Empty(new StashReader(Stash(schema, 1, [], rows: -4), schema).Read(ServerData));
    }

    [Fact]
    public void ANDAnItemListThatIsNotAWholeNumberOfPointersIsRefusedToo()
    {
        // What a read torn across a resize looks like.
        OffsetSchema schema = Schema();
        FakeMemoryReader fake = Stash(schema, id: 1, [(0, 0, 1, 1, 0)]);

        StructDef inventory = schema.Structs["Inventory"];
        fake.Place(Inventory + (ulong)inventory.OffsetOf("ItemListLast"), ItemList + 5);

        Assert.Empty(Assert.Single(new StashReader(fake, schema).Read(ServerData)).Items);
    }

    [Fact]
    public void THEInventoryVectorIsFoundThroughTheSecondServerDataStruct()
    {
        // The trap the flask reader documents: read off the OUTER struct the vector is simply
        // zero, which is indistinguishable from an offset that has drifted.
        OffsetSchema schema = Schema();

        Assert.Single(new StashReader(Stash(schema, 1, [(0, 0, 1, 1, 0)]), schema).Read(ServerData));

        // And a build that laid them out flat still works, because whichever base yields a
        // usable vector wins.
        Assert.Single(new StashReader(Stash(schema, 1, [(0, 0, 1, 1, 0)], flat: true), schema).Read(ServerData));
    }

    [Fact]
    public void ANDNothingAtAllIsReadFromAnAddressThatResolvesToNeither()
    {
        OffsetSchema schema = Schema();
        var reader = new StashReader(new FakeMemoryReader(), schema);

        Assert.Empty(reader.Read(0));
        Assert.Empty(reader.Read(0xDEAD_BEEF));
        Assert.Equal(0ul, reader.Resolve(0));
    }

    /// <summary>
    /// Which league the character is in - the one thing here that is not an item.
    /// </summary>
    /// <remarks>
    /// It rides the same two-struct hop the inventories do, and it exists for prices: the value
    /// is spelled exactly as poe.ninja spells it, hardcore prefix and all, so it can be asked
    /// for as it comes. Typed in by hand instead it goes stale at every league start, silently,
    /// and stale prices look exactly like real ones.
    /// </remarks>
    [Theory]
    [InlineData("Standard")]
    [InlineData("HC Runes of Aldur")]
    [InlineData("Rise of the Abyssal")]
    public void THELeagueIsReadOffTheOUTERStructRatherThanTheInventoriesOne(string league)
    {
        // The two server-data structs are different things, and this is the one that cost the
        // whole price feature: the league is on the OUTER struct - what ServerDataPtr points at
        // - while the inventories are on the inner one the outer's vector leads to. Read off
        // the inner one it comes back empty, which reads as "not in the game yet".
        OffsetSchema schema = Schema();
        FakeMemoryReader fake = Stash(schema, id: 1, cells: []);
        fake.PlaceStdWString(
            ServerData + (ulong)schema.Structs["ServerDataOffsets"].OffsetOf("League"), league, 0x18_0000);

        Assert.Equal(league, new StashReader(fake, schema).League(ServerData));
    }

    [Fact]
    public void ANDTheInnerStructIsStillTriedForTheLayoutWhereBothAreOne()
    {
        // A build that laid these out flat would put the league on the only struct there is.
        // One read that almost always fails, against working on a layout this cannot see.
        OffsetSchema schema = Schema();
        FakeMemoryReader fake = Stash(schema, id: 1, cells: []);
        fake.PlaceStdWString(
            Inner + (ulong)schema.Structs["ServerDataOffsets"].OffsetOf("League"), "Standard", 0x18_0000);

        Assert.Equal("Standard", new StashReader(fake, schema).League(ServerData));
    }

    [Fact]
    public void ANDNothingReadableIsNoLeagueRatherThanRubbish()
    {
        // A drifted offset reads whatever follows the string. Handing that on as a league name
        // asks a price server about a league nobody has heard of, and the failure shows up as
        // "no prices" rather than as the offset problem it is.
        OffsetSchema schema = Schema();
        int at = schema.Structs["ServerDataOffsets"].OffsetOf("League");

        Assert.Equal(string.Empty, new StashReader(Stash(schema, 1, []), schema).League(ServerData));
        Assert.Equal(string.Empty, new StashReader(new FakeMemoryReader(), schema).League(ServerData));

        FakeMemoryReader control = Stash(schema, 1, []);
        control.PlaceStdWString(ServerData + (ulong)at, "Bad\u0001League", 0x18_0000);
        Assert.Equal(string.Empty, new StashReader(control, schema).League(ServerData));
    }
}
