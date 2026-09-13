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

    private static (int Cell, string Name)[] Rows()
    {
        string path = Path.Combine(RepositoryRoot, "assets", PoEformance.Features.IconSheet.NameTable);
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

    [Fact]
    public void EveryNamedCellIsOnTheSheet()
    {
        // Read from the PNG header rather than by decoding, so this needs no image library.
        byte[] header = new byte[24];
        using (FileStream file = File.OpenRead(Path.Combine(RepositoryRoot, "assets", "icons.png")))
        {
            file.ReadExactly(header);
        }

        int width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
        int height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
        int places = PoEformance.Features.IconSheet.CountIn(width, height);

        foreach ((int cell, string name) in Rows())
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
}
