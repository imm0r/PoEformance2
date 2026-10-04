namespace PoEformance.Core.Tests;

/// <summary>
/// The table naming the sheet's cells, and the ways it could be quietly wrong.
/// </summary>
/// <remarks>
/// It is GENERATED - scripts/name-icon-cells.py matches the standalone icons against the
/// sheet - so what is worth holding is not the names themselves but the shape they have to
/// keep: every cell on the sheet, named once, by something a person can read. A regenerated
/// table that broke any of those would draw wrong tooltips rather than fail, which is the
/// kind of wrong nobody reports.
/// </remarks>
public class IconNameTableTests
{
    private static string RepositoryRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "schema", "poe2.offsets.json")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return dir.FullName;
        }
    }

    private static (int Cell, string Name)[] Rows(string table = PoEformance.Features.IconSheet.NameTable)
    {
        string path = Path.Combine(RepositoryRoot, "assets", table);
        var rows = new List<(int, string)>();
        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] parts = line.Split('\t');
            Assert.Equal(2, parts.Length);
            rows.Add((int.Parse(parts[0]), parts[1]));
        }

        return [.. rows];
    }

    /// <summary>The sheet's size, read from the PNG header rather than by decoding.</summary>
    private static (int Width, int Height) SheetSize()
    {
        byte[] header = new byte[24];
        using (FileStream file = File.OpenRead(Path.Combine(RepositoryRoot, "assets", "icons.png")))
        {
            file.ReadExactly(header);
        }

        return (
            (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19],
            (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23]);
    }

    [Fact]
    public void EveryNamedCellIsOnTheSheet()
    {
        (int width, int height) = SheetSize();
        int places = PoEformance.Features.IconSheet.CountIn(width, height);

        foreach ((int cell, string name) in Rows().Concat(Rows(PoEformance.Features.IconSheet.CustomNameTable)))
        {
            // COUNTED FROM ONE, the way a style file stores it. A zero here would mean "no
            // icon" to everything that reads a style, so a table starting at zero would name
            // the first cell something no marker can ever be set to.
            Assert.InRange(cell, 1, places);
            Assert.False(string.IsNullOrWhiteSpace(name));
        }
    }

    [Fact]
    public void NoCellIsNamedTwice()
    {
        // Two names for one cell means one of them is wrong, and whichever the reader happens
        // to keep is the one that ships.
        (int Cell, string Name)[] rows = Rows();
        Assert.Equal(rows.Length, rows.Select(r => r.Cell).Distinct().Count());
    }

    /// <summary>
    /// The table says how complete it is, and it is nowhere near complete.
    /// </summary>
    /// <remarks>
    /// Pinned because the INCOMPLETENESS is load-bearing: every caller draws a cell number when
    /// there is no name, and a table that one day covered everything would let somebody quietly
    /// drop that path - which would then break the moment the sheet grew a cell.
    /// </remarks>
    [Fact]
    public void PlentyOfCellsAreDeliberatelyLeftUnnamed()
    {
        Assert.InRange(Rows().Length, 1, 1000);
    }

    /// <summary>
    /// No name is used twice, because the table is also read backwards.
    /// </summary>
    /// <remarks>
    /// IconNames.CellFor turns a game icon name into a cell, which is how an unrecognised
    /// marker draws the game's own picture. A name on two cells makes that answer arbitrary,
    /// and the wrong one is still a plausible icon - so it would read as the art being wrong
    /// rather than as the table being wrong.
    /// </remarks>
    [Fact]
    public void NoNameIsUsedTwice()
    {
        (int Cell, string Name)[] rows = Rows();
        Assert.Equal(rows.Length, rows.Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>
    /// Names the GAME was seen using land on the cells holding that art.
    /// </summary>
    /// <remarks>
    /// THE ONLY CHECK HERE THE GAME ITSELF SETTLED. Everything else in this file is a shape the
    /// table has to keep; these three strings were read out of a running client's MinimapIcon
    /// components and looked up in the picker by hand, and the cells are what it answered.
    ///
    /// That is what licenses the whole feature: MinimapIcons.dat names an icon, the art for it
    /// is published as Art/2DArt/minimap/player/&lt;that name&gt;.webp, and the table was built
    /// from those file names - so the two namespaces are one namespace. If a regeneration ever
    /// breaks that, an unrecognised marker starts drawing somebody else's picture, and this is
    /// the test that says so instead.
    /// </remarks>
    [Theory]
    [InlineData("StoryGlyph", 9)]
    [InlineData("CorruptionAltarActive", 738)]
    [InlineData("ShrineActive", 779)]
    public void NamesTheGameUsesLandOnTheirCells(string icon, int cell)
    {
        Assert.Equal(cell, Rows().Single(r => r.Name == icon).Cell);
    }

    /// <summary>
    /// Every Active sits immediately before its own Inactive.
    /// </summary>
    /// <remarks>
    /// THIS IS THE CHECK THE MATCHING COULD NOT HAVE PASSED BY BEING WRONG, and it is why the
    /// generator is allowed to name these pairs at all. They differ by a few pixels of glow;
    /// naming one backwards is invisible afterwards, because both look right.
    ///
    /// scripts/name-icon-cells.py compares 16x16 pictures and never sees a cell NUMBER. So the
    /// sheet agreeing with it - on all 162 pairs, in the same order every time - is evidence
    /// from outside the arithmetic. A generator that started swapping them would have to swap
    /// the numbering to keep this passing, and it cannot.
    /// </remarks>
    [Fact]
    public void EveryActiveSitsImmediatelyBeforeItsInactive()
    {
        Dictionary<string, int> byName = Rows().ToDictionary(r => r.Name, r => r.Cell, StringComparer.Ordinal);

        int pairs = 0;
        foreach ((string name, int cell) in byName)
        {
            if (!name.EndsWith("Active", StringComparison.Ordinal)
                || name.EndsWith("Inactive", StringComparison.Ordinal)
                || !byName.TryGetValue(string.Concat(name.AsSpan(0, name.Length - 6), "Inactive"), out int off))
            {
                continue;
            }

            pairs++;
            Assert.Equal(cell + 1, off);
        }

        // Not vacuous: a table that stopped naming pairs entirely would satisfy the loop above
        // without ever entering it, and that is exactly the regression this is here to catch.
        Assert.InRange(pairs, 100, 500);
    }

    /// <summary>
    /// The two tables keep to their own rows: the game's above the line, ours below it.
    /// </summary>
    /// <remarks>
    /// THE LINE IS WHAT KEEPS BOTH TABLES SAFE FROM EACH OTHER. scripts/name-icon-cells.py
    /// rewrites the generated table whole, and tools/IconBaker rewrites the custom one; if
    /// either could name a cell in the other's rows, a regeneration would quietly put two
    /// names on one picture - and IconNames would keep whichever it happened to read last.
    /// </remarks>
    [Fact]
    public void EachTableNamesOnlyItsOwnRows()
    {
        (int width, _) = SheetSize();
        int firstOwn = (PoEformance.Features.IconSheet.OwnRow * PoEformance.Features.IconSheet.ColumnsIn(width)) + 1;

        Assert.All(Rows(), row => Assert.InRange(row.Cell, 1, firstOwn - 1));
        Assert.All(
            Rows(PoEformance.Features.IconSheet.CustomNameTable),
            row => Assert.True(row.Cell >= firstOwn, $"{row.Name} at {row.Cell} is in the game's rows"));
    }

    /// <summary>No cell and no name appears twice across both tables together.</summary>
    /// <remarks>
    /// Read backwards, the two tables are one lookup - a boss baked under a name the game
    /// already uses would decide which picture an unrecognised marker draws by file order.
    /// </remarks>
    [Fact]
    public void TheTwoTablesTogetherNameEveryCellAndNameOnce()
    {
        (int Cell, string Name)[] both = [.. Rows(), .. Rows(PoEformance.Features.IconSheet.CustomNameTable)];
        Assert.Equal(both.Length, both.Select(r => r.Cell).Distinct().Count());
        Assert.Equal(both.Length, both.Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
