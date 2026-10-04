using PoEformance.Features;
using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// Joining an item to the .ao that draws it, out of the install's own tables - and the book over it.
/// </summary>
/// <remarks>
/// SYNTHETIC .dat FILES, the choice MonsterTablesTests and UniqueNamesTests made: what these check
/// is the JOIN against the vendored layouts in data/item-tables.json. Whether those layouts fit a
/// real install is settled at runtime by each file's own row size, and ItemVisuals.Say reports it.
/// BaseItemTypes' 360 bytes is the one size the live client has also confirmed.
/// </remarks>
public class ItemVisualsTests
{
    private static QuestTableLayouts Layouts()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "data", "item-tables.json")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Assert.IsType<QuestTableLayouts>(
            QuestTableLayouts.Load(Path.Combine(dir!.FullName, "data", "item-tables.json")));
    }

    private static int At(QuestTableLayouts layouts, string table, string column)
    {
        int at = layouts.OffsetOf(table, column);
        Assert.True(at >= 0, $"the vendored layout has no {table}.{column}");
        return at;
    }

    [Fact]
    public void THEVENDOREDBaseItemTypesLayoutIsTheSizeTheLiveClientReported()
    {
        // 5496 rows of 360 on a live 0.5.5 client - see BaseItemTypesRow in the offsets schema. A
        // computed size agreeing with the client's means every column in front of Name is right.
        Assert.Equal(360, Layouts().RowSizeOf("BaseItemTypes"));
        Assert.True(Layouts().RowSizeOf("ItemClasses") > 0);
    }

    /// <summary>
    /// Three base types - a sword with a model, a currency without one, a bow with only AOFile2 -
    /// two classes, and two layout rows naming one unique's art, the second of them alternate.
    /// </summary>
    private static GameFiles Install(QuestTableLayouts layouts, bool withUniques = true)
    {
        var bases = new FakeDat(3, layouts.RowSizeOf("BaseItemTypes"));
        var art = new FakeDat(4, layouts.RowSizeOf("ItemVisualIdentity"));
        var classes = new FakeDat(2, layouts.RowSizeOf("ItemClasses"));
        var layout = new FakeDat(2, layouts.RowSizeOf("UniqueStashLayout"));
        var words = new FakeDat(2, layouts.RowSizeOf("Words"));

        bases
            .Text(0, At(layouts, "BaseItemTypes", "Id"), "Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/OneHandSword1")
            .Text(0, At(layouts, "BaseItemTypes", "Name"), "Shortsword")
            .Reference(0, At(layouts, "BaseItemTypes", "ItemClass"), 0)
            .I32(0, At(layouts, "BaseItemTypes", "Width"), 1)
            .I32(0, At(layouts, "BaseItemTypes", "Height"), 3)
            .I32(0, At(layouts, "BaseItemTypes", "DropLevel"), 5)
            .Reference(0, At(layouts, "BaseItemTypes", "ItemVisualIdentity"), 0)

            .Text(1, At(layouts, "BaseItemTypes", "Id"), "Metadata/Items/Currency/CurrencyRerollRare")
            .Text(1, At(layouts, "BaseItemTypes", "Name"), "Chaos Orb")
            .Null(1, At(layouts, "BaseItemTypes", "ItemClass"))
            .Reference(1, At(layouts, "BaseItemTypes", "ItemVisualIdentity"), 1)

            .Text(2, At(layouts, "BaseItemTypes", "Id"), "Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1")
            .Text(2, At(layouts, "BaseItemTypes", "Name"), "Crude Bow")
            .Reference(2, At(layouts, "BaseItemTypes", "ItemClass"), 1)
            .I32(2, At(layouts, "BaseItemTypes", "Width"), 2)
            .I32(2, At(layouts, "BaseItemTypes", "Height"), 4)
            .I32(2, At(layouts, "BaseItemTypes", "DropLevel"), 1)
            .Reference(2, At(layouts, "BaseItemTypes", "ItemVisualIdentity"), 2);

        art
            .Text(0, At(layouts, "ItemVisualIdentity", "Id"), "OneHandSword1")
            .Text(0, At(layouts, "ItemVisualIdentity", "AOFile"), "Metadata/Items/Weapons/OneHandSword1.ao")
            .Text(0, At(layouts, "ItemVisualIdentity", "DDSFile"), "Art/2DItems/Weapons/OneHandSword1.dds")

            // The currency's art row: an icon and no model, which is what most of the table is.
            .Text(1, At(layouts, "ItemVisualIdentity", "Id"), "CurrencyRerollRare")
            .Text(1, At(layouts, "ItemVisualIdentity", "DDSFile"), "Art/2DItems/Currency/CurrencyRerollRare.dds")

            .Text(2, At(layouts, "ItemVisualIdentity", "Id"), "Bow1")
            .Text(2, At(layouts, "ItemVisualIdentity", "AOFile2"), "Metadata/Items/Weapons/Bow1.ao")

            .Text(3, At(layouts, "ItemVisualIdentity", "Id"), "OneHandSwordUnique1")
            .Text(3, At(layouts, "ItemVisualIdentity", "AOFile"), "Metadata/Items/Weapons/OneHandSwordUnique1.ao")
            .Text(3, At(layouts, "ItemVisualIdentity", "AOFile2"), "Metadata/Items/Weapons/OneHandSwordUnique1Sheathed.ao");

        classes
            .Text(0, At(layouts, "ItemClasses", "Id"), "One Hand Sword")
            .Text(0, At(layouts, "ItemClasses", "Name"), "One Hand Swords")
            .Text(1, At(layouts, "ItemClasses", "Id"), "Bow")
            .Text(1, At(layouts, "ItemClasses", "Name"), "Bows");

        layout
            .Reference(0, At(layouts, "UniqueStashLayout", "WordsKey"), 1)
            .Reference(0, At(layouts, "UniqueStashLayout", "ItemVisualIdentityKey"), 3)
            .Bool(0, At(layouts, "UniqueStashLayout", "IsAlternateArt"), true)
            .Reference(1, At(layouts, "UniqueStashLayout", "WordsKey"), 0)
            .Reference(1, At(layouts, "UniqueStashLayout", "ItemVisualIdentityKey"), 3);

        words
            .Text(0, At(layouts, "Words", "Text"), "Redbeak")
            .Text(1, At(layouts, "Words", "Text"), "Redbeak (alternate art)");

        List<(string, byte[])> files =
        [
            ("data/baseitemtypes.datc64", bases.Bytes()),
            ("data/itemvisualidentity.datc64", art.Bytes()),
            ("data/itemclasses.datc64", classes.Bytes()),
        ];

        if (withUniques)
        {
            files.Add(("data/uniquestashlayout.datc64", layout.Bytes()));
            files.Add(("data/words.datc64", words.Bytes()));
        }

        GameFiles? read = FakeInstall.Of([.. files]);
        Assert.NotNull(read);
        return read!;
    }

    [Fact]
    public void ABASETYPEIsJoinedToItsModelAndItsClass()
    {
        QuestTableLayouts layouts = Layouts();
        ItemVisuals items = ItemVisuals.Read(Install(layouts), layouts);

        ItemVisual? sword = items.Find("Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/OneHandSword1");
        Assert.NotNull(sword);
        Assert.Equal("Shortsword", sword!.Name);
        Assert.Equal("One Hand Swords", sword.Class);
        Assert.Equal("Metadata/Items/Weapons/OneHandSword1.ao", sword.Model);
        Assert.Equal("Art/2DItems/Weapons/OneHandSword1.dds", sword.Icon);
        Assert.Equal((5, 1, 3), (sword.DropLevel, sword.Width, sword.Height));
        Assert.False(sword.Unique);
    }

    [Fact]
    public void ANDAnItemWithNoModelIsLeftOutAndCounted()
    {
        QuestTableLayouts layouts = Layouts();
        ItemVisuals items = ItemVisuals.Read(Install(layouts), layouts);

        Assert.Null(items.Find("Metadata/Items/Currency/CurrencyRerollRare"));
        Assert.Contains(items.Say, line => line.Contains("1 base types without one", StringComparison.Ordinal));
    }

    [Fact]
    public void ANDOnlyAOFile2StillCountsAsAModelAndIsWhatTheBookOpens()
    {
        // NEITHER COLUMN IS "THE" MODEL - nothing yet says what separates them - so an item with
        // only the second filled is an item with a model, not one without.
        QuestTableLayouts layouts = Layouts();
        ItemVisual? bow = ItemVisuals.Read(Install(layouts), layouts)
            .Find("Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1");

        Assert.NotNull(bow);
        Assert.Empty(bow!.Ao);
        Assert.Equal("Metadata/Items/Weapons/Bow1.ao", bow.Model);
        Assert.Equal("Bows", bow.Class);
    }

    [Fact]
    public void AUNIQUEIsARowOfItsOwnOncePerArtAndPlainArtNamesIt()
    {
        QuestTableLayouts layouts = Layouts();
        ItemVisuals items = ItemVisuals.Read(Install(layouts), layouts);

        ItemVisual unique = Assert.Single(items.All, one => one.Unique);
        Assert.Equal(ItemVisuals.UniquePrefix + "OneHandSwordUnique1", unique.Path);
        Assert.Equal("Redbeak", unique.Name);
        Assert.Equal("Metadata/Items/Weapons/OneHandSwordUnique1.ao", unique.Ao);
        Assert.Equal("Metadata/Items/Weapons/OneHandSwordUnique1Sheathed.ao", unique.Ao2);
        Assert.Equal(3, items.Count);
    }

    [Fact]
    public void ANDWithoutTheUniqueTablesTheBaseTypesStillRead()
    {
        QuestTableLayouts layouts = Layouts();
        ItemVisuals items = ItemVisuals.Read(Install(layouts, withUniques: false), layouts);

        Assert.Equal(2, items.Count);
        Assert.DoesNotContain(items.All, one => one.Unique);
    }

    [Fact]
    public void ANDNoInstallSaysSoRatherThanThrowing()
    {
        ItemVisuals items = ItemVisuals.Read(null, null);

        Assert.Equal(0, items.Count);
        Assert.Contains(items.Say, line => line.Contains("no install", StringComparison.Ordinal));
    }

    [Fact]
    public void THEBOOKFindsByClassByKindAndByDropLevel()
    {
        QuestTableLayouts layouts = Layouts();
        ItemBook book = ItemBook.Of(ItemVisuals.Read(Install(layouts), layouts));

        Assert.Equal(["Shortsword"], Names(book, "class:sword"));
        Assert.Equal(["Redbeak"], Names(book, "kind:unique"));
        Assert.Equal(["Shortsword"], Names(book, "drop>3"));
        Assert.Equal(["Crude Bow", "Shortsword"], Names(book, "not unique"));

        // A UNIQUE HAS NO DROP LEVEL, and "below 3" is not where it goes: as a 0 it used to be the
        // first row of every low-level answer and the first bin of the histogram.
        Assert.Equal(["Crude Bow"], Names(book, "drop<3"));
        Assert.Equal(["Shortsword"], Names(book, "cells<=8 width<2"));
        Assert.Equal(["Crude Bow", "Shortsword"], Names(book, "cells<=8"));
        DataColumn drop = book.Store.Columns.Single(one => one.Name == "drop");
        Assert.Equal(2, drop.Spread.Count);
        Assert.True(double.IsNaN(drop.Number[book.Row("Unique/OneHandSwordUnique1")]));

        // AND BY THE .ao's NAME, which is what somebody holding a path from a dump types.
        Assert.Equal(["Redbeak"], Names(book, "sheathed"));
    }

    [Fact]
    public void ANDAFieldItHasNotGotIsAnErrorRatherThanAnEmptyList()
    {
        QuestTableLayouts layouts = Layouts();
        ItemBook book = ItemBook.Of(ItemVisuals.Read(Install(layouts), layouts));

        Assert.Null(book.Matching(ColumnQuery.Parse("bogus:thing").Term, out string why));
        Assert.NotEmpty(why);
    }

    [Fact]
    public void THERAILCountsEachClassAndKindAgainstTheRowsThatAreLeft()
    {
        QuestTableLayouts layouts = Layouts();
        ItemBook book = ItemBook.Of(ItemVisuals.Read(Install(layouts), layouts));

        RowSet? all = book.Matching(ColumnQuery.Parse(string.Empty).Term, out _);
        Assert.NotNull(all);

        var kinds = new List<Facet>();
        book.Facets(all!, "kind", kinds);
        Assert.Equal([new Facet("base", 2), new Facet("unique", 1)], kinds);

        // NARROWED, AND A ZERO KEPT: with only uniques left there are no bows, and that is said.
        RowSet? uniques = book.Matching(ColumnQuery.Parse("kind:unique").Term, out _);
        var classes = new List<Facet>();
        book.Facets(uniques!, "class", classes);
        Assert.All(classes, one => Assert.Equal(0, one.Count));
        Assert.Contains(new Facet("Bows", 0), classes);
    }

    [Fact]
    public void ACLICKOnAClassLeavesTheRowsTheRailCountedAndAnotherTakesItBack()
    {
        // THE CLASSES HAVE SPACES IN THEM - "One Hand Swords" - which is what made the item book
        // the one that showed the rail writing a value the grammar could not hold as one.
        QuestTableLayouts layouts = Layouts();
        ItemBook book = ItemBook.Of(ItemVisuals.Read(Install(layouts), layouts));

        RowSet all = Assert.IsType<RowSet>(book.Matching(ColumnQuery.Parse(string.Empty).Term, out _));
        var classes = new List<Facet>();
        book.Facets(all, "class", classes);
        Assert.Contains(classes, one => one.Value == "One Hand Swords");

        foreach (Facet facet in classes)
        {
            string clicked = ColumnQuery.Toggle(string.Empty, "class", facet.Value);
            QueryResult parsed = ColumnQuery.Parse(clicked);
            Assert.True(parsed.Ok, $"'{clicked}' would not read back: {parsed.Error}");
            Assert.True(ColumnQuery.Holds(parsed.Term, "class", facet.Value), $"'{clicked}' does not tick {facet.Value}");

            RowSet after = Assert.IsType<RowSet>(book.Matching(parsed.Term, out string why));
            Assert.Equal(string.Empty, why);
            Assert.Equal(facet.Count, after.Count);

            Assert.Equal(string.Empty, ColumnQuery.Toggle(clicked, "class", facet.Value));
        }

        Assert.Equal(["Shortsword"], Names(book, "class:\"One Hand Swords\""));
    }

    [Fact]
    public void THECHOOSEROffersEveryColumnUnderAHeadingAndStartsWithAFew()
    {
        QuestTableLayouts layouts = Layouts();
        ItemBook book = ItemBook.Of(ItemVisuals.Read(Install(layouts), layouts));
        DataColumn[] columns = book.Store.Columns;

        Assert.Equal(columns.Length, book.Groups.Length);
        Assert.Equal(columns.Length, book.Shown.Length);
        Assert.True(book.Shown[0]);
        Assert.Contains(false, book.Shown);

        string[] off = [.. columns.Where((_, at) => !book.Shown[at]).Select(one => one.Name)];
        Assert.Contains("art", off);
        Assert.Contains("ao2", off);

        // AND A COLUMN THAT STARTS OFF IS STILL A FIELD A QUERY CAN NAME.
        Assert.Equal(["Redbeak"], Names(book, "ao2:sheathed"));
    }

    private static string[] Names(ItemBook book, string query)
    {
        QueryResult parsed = ColumnQuery.Parse(query);
        Assert.Empty(parsed.Error);

        RowSet? rows = book.Matching(parsed.Term, out string why);
        Assert.True(rows is not null, why);

        var shown = new List<int>();
        rows!.CopyTo(shown);
        return [.. shown.Select(row => book.Store.Columns[0].Text[row]).Order(StringComparer.Ordinal)];
    }
}
