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
}
