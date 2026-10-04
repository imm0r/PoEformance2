using System.Runtime.InteropServices;

namespace PoEformance.Features;

/// <summary>Where one exported family's pair of pictures goes on the sheet.</summary>
/// <param name="Family">The export's stem: "IgnagdukBoss" for IgnagdukBossActive.png.</param>
/// <param name="Active">The Active picture's cell, counted from ZERO.</param>
/// <param name="Inactive">The Inactive picture's cell, counted from ZERO.</param>
/// <param name="Fresh">
/// True for a cell nobody has named yet, false for one already in the custom table that is
/// being painted over with a newer export of the same boss.
/// </param>
public readonly record struct IconPlacement(string Family, int Active, int Inactive, bool Fresh);

/// <summary>What laying a set of exports into the sheet comes to.</summary>
/// <param name="Placed">The families that get cells, in the order they were given.</param>
/// <param name="Game">Families the game's own art already carries, which are never painted over.</param>
/// <param name="Full">Families there was no room left for under <see cref="IconSheet.MaxEdge"/>.</param>
public sealed record IconGrowth(List<IconPlacement> Placed, List<string> Game, List<string> Full)
{
    /// <summary>The fewest rows the sheet needs to hold every placement, never fewer than it has.</summary>
    public int RowsFor(int columns, int rowsNow)
    {
        int last = -1;
        foreach (IconPlacement placement in Placed)
        {
            last = Math.Max(last, Math.Max(placement.Active, placement.Inactive));
        }

        return Math.Max(rowsNow, (last / Math.Max(1, columns)) + 1);
    }
}

/// <summary>
/// Puts exported boss pictures onto the icon sheet: which file is which, where each goes, and
/// the copy itself.
/// </summary>
/// <remarks>
/// WHY THIS IS SHARED, AND WHY IT IS BYTES. Two things lay exports into the sheet: the overlay,
/// live, so a boss posed a minute ago is already on the map; and tools/IconBaker, which writes
/// the same cells into assets/icons.png for good. If they decided placement separately, the
/// cell a marker wore this session and the cell it wears after the bake could differ - and a
/// style file that stored the first number would quietly point at somebody else's boss. One
/// rule in one place is what keeps the two agreeing, and the rule only needs RGBA bytes, so it
/// lives here where neither side's image library has to come with it.
///
/// THE ORDER IS DETERMINISTIC FOR THE SAME REASON: families are sorted by name before they are
/// placed, so the live sheet and a bake of the same exports put the same boss in the same cell.
///
/// THE GAME'S ART IS NEVER PAINTED OVER. A family the generated table already names in the
/// game's rows is reported and skipped; only cells past <see cref="IconSheet.OwnRow"/> are
/// ever written, and those are ours.
/// </remarks>
public static class IconSheetGrowth
{
    /// <summary>Bytes per pixel. Everything here is RGBA, eight bits a channel.</summary>
    public const int Channels = 4;

    /// <summary>
    /// The family an exported file is the Active picture of, or empty when it is not one.
    /// </summary>
    /// <remarks>
    /// ORDINAL, AND INACTIVE IS RULED OUT FIRST. "FooInactive.png" ends in "Active.png" too, and
    /// matching it as the Active picture of a family called "FooIn" is exactly what a plain
    /// wildcard listing does. Case-sensitive because the export writes the suffix in exactly one
    /// spelling, and ignoring case would turn a family called "BossIn" (BossInActive) into the
    /// Inactive picture of "Boss".
    ///
    /// The full-size pair - "FooActive-1024.png" - ends in "-1024", not "Active", so it never
    /// matches: the sheet takes the 64-pixel cell the export made for it.
    /// </remarks>
    public static string FamilyOf(string file)
    {
        string name = Path.GetFileName(file ?? string.Empty);
        if (!name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        string stem = name[..^4];
        if (stem.EndsWith(BossIcons.InactiveSuffix, StringComparison.Ordinal)
            || !stem.EndsWith(BossIcons.ActiveSuffix, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return stem[..^BossIcons.ActiveSuffix.Length];
    }

    /// <summary>Every family a listing of files holds an Active picture for, sorted and once each.</summary>
    public static List<string> Families(IEnumerable<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var families = new List<string>();
        foreach (string file in files)
        {
            string family = FamilyOf(file);
            if (family.Length > 0 && seen.Add(family))
            {
                families.Add(family);
            }
        }

        families.Sort(StringComparer.OrdinalIgnoreCase);
        return families;
    }

    /// <summary>The last cell holding any opaque pixel, counted from zero, or -1 when there is none.</summary>
    /// <remarks>
    /// ALPHA ONLY. A fully transparent pixel may still carry colour - PNG keeps whatever the
    /// encoder left there - so "any byte non-zero" would find art in a cell that draws nothing.
    /// Read as one 32-bit word per pixel, where little-endian RGBA puts alpha in the top byte.
    ///
    /// From the END, because the answer is near it: the scan stops at the first cell with art.
    /// </remarks>
    public static int LastOccupied(ReadOnlySpan<byte> rgba, int width, int height)
    {
        int columns = IconSheet.ColumnsIn(width);
        int rows = IconSheet.RowsIn(height);
        if (rgba.Length < width * height * Channels)
        {
            throw new ArgumentException("fewer bytes than the sheet's size says", nameof(rgba));
        }

        ReadOnlySpan<uint> pixels = MemoryMarshal.Cast<byte, uint>(rgba[..(width * height * Channels)]);
        for (int index = (columns * rows) - 1; index >= 0; index--)
        {
            int left = index % columns * IconSheet.Tile;
            int top = index / columns * IconSheet.Tile;
            for (int y = 0; y < IconSheet.Tile; y++)
            {
                ReadOnlySpan<uint> line = pixels.Slice(((top + y) * width) + left, IconSheet.Tile);
                foreach (uint pixel in line)
                {
                    if ((pixel & 0xFF000000u) != 0)
                    {
                        return index;
                    }
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// The first cell a new pair may start at, counted from zero.
    /// </summary>
    /// <remarks>
    /// Past the game's rows, past any art, and past any NAMED cell - a pair whose Inactive
    /// picture was never exported still owns the empty cell beside its Active one, and the
    /// next boss must not move into it. Aligned to a pair, see <see cref="Aligned"/>.
    /// </remarks>
    /// <param name="columns">The sheet's columns.</param>
    /// <param name="lastOccupied">From <see cref="LastOccupied"/>.</param>
    /// <param name="lastNamed">The highest cell any name table claims, counted from zero, or -1.</param>
    public static int FirstFree(int columns, int lastOccupied, int lastNamed)
        => Aligned(Math.Max(IconSheet.OwnRow * columns, Math.Max(lastOccupied, lastNamed) + 1), columns);

    /// <summary>
    /// Moves a cell forward until a pair started there stays on one row, side by side.
    /// </summary>
    /// <remarks>
    /// Active first and Inactive right of it, which is how the game's own pairs sit (200/201,
    /// 629/630, 703/704 ...) and what makes a pair recognisable in the icon picker.
    /// </remarks>
    public static int Aligned(int index, int columns)
    {
        int across = Math.Max(2, columns);
        int start = Math.Max(0, index);
        if (start % across % 2 == 1)
        {
            start++;
        }

        if (start % across == across - 1)
        {
            start++;
        }

        return start;
    }

    /// <summary>
    /// Decides a cell for every family: an existing one of ours, a new pair, or none.
    /// </summary>
    /// <param name="families">What was exported, from <see cref="Families"/>.</param>
    /// <param name="named">
    /// The cell a name already has in the shipped tables, counted from ONE, or 0. Only the
    /// tables that travel with the sheet - never names that exist just for this session.
    /// </param>
    /// <param name="columns">The sheet's columns.</param>
    /// <param name="firstFree">From <see cref="FirstFree"/>.</param>
    /// <param name="mostRows">How many rows the sheet may grow to.</param>
    public static IconGrowth Place(
        IReadOnlyList<string> families, Func<string, int> named, int columns, int firstFree, int mostRows)
    {
        ArgumentNullException.ThrowIfNull(families);
        ArgumentNullException.ThrowIfNull(named);

        int own = IconSheet.OwnRow * columns;
        int limit = mostRows * columns;
        int cursor = Aligned(firstFree, columns);
        var growth = new IconGrowth([], [], []);

        foreach (string family in families)
        {
            int active = named(BossIcons.Named(family, cleared: false)) - 1;
            int inactive = named(BossIcons.Named(family, cleared: true)) - 1;
            int plain = named(family) - 1;

            // The game carries this picture already, under any of the names a marker looks for.
            // Painting over it would replace the game's art with ours for every map that uses it.
            if (Game(active, own) || Game(inactive, own) || Game(plain, own))
            {
                growth.Game.Add(family);
                continue;
            }

            if (active >= 0)
            {
                // Ours already: a newer export of the same boss goes into the same cells, so
                // every style and every marker that knew the old number still finds it. The
                // cell beside it was reserved with the pair even if nothing was painted there.
                growth.Placed.Add(new IconPlacement(family, active, inactive >= 0 ? inactive : active + 1, Fresh: false));
                continue;
            }

            if (cursor + 1 >= limit)
            {
                growth.Full.Add(family);
                continue;
            }

            growth.Placed.Add(new IconPlacement(family, cursor, cursor + 1, Fresh: true));
            cursor = Aligned(cursor + 2, columns);
        }

        return growth;
    }

    /// <summary>Copies one cell's pixels into the sheet at a cell, counted from zero.</summary>
    /// <param name="sheet">The sheet's RGBA bytes, row after row with no padding.</param>
    /// <param name="width">The sheet's width in pixels.</param>
    /// <param name="cell">A <see cref="IconSheet.Tile"/>-square picture's RGBA bytes.</param>
    /// <param name="index">Where it goes.</param>
    public static void Copy(Span<byte> sheet, int width, ReadOnlySpan<byte> cell, int index)
    {
        const int Line = IconSheet.Tile * Channels;
        if (cell.Length != Line * IconSheet.Tile)
        {
            throw new ArgumentException($"a cell is {IconSheet.Tile}x{IconSheet.Tile} RGBA", nameof(cell));
        }

        int columns = IconSheet.ColumnsIn(width);
        int left = index % columns * IconSheet.Tile;
        int top = index / columns * IconSheet.Tile;
        int stride = width * Channels;
        if ((top + IconSheet.Tile) * stride > sheet.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "past the end of the sheet");
        }

        for (int y = 0; y < IconSheet.Tile; y++)
        {
            cell.Slice(y * Line, Line).CopyTo(sheet.Slice(((top + y) * stride) + (left * Channels), Line));
        }
    }

    private static bool Game(int index, int own) => index >= 0 && index < own;
}
