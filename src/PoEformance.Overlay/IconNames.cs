using System.Reflection;
using PoEformance.Features;

namespace PoEformance.Overlay;

/// <summary>
/// What the cells of the icon sheet are called, where anybody knows.
/// </summary>
/// <remarks>
/// A SHEET SAYS NOTHING ABOUT ITSELF. Its 1050 pictures are a grid of art and a cell number,
/// and "cell 588" is not something anybody can look for - finding the boss marker means
/// scrolling seventy-seven rows and recognising it. The same art is published one file per
/// icon under the names the game uses, so the names exist; assets/icon-names.tsv is those two
/// piles walked past each other, by scripts/name-icon-cells.py.
///
/// INCOMPLETE ON PURPOSE, which is why every caller has to cope with a cell having no name.
/// Around half the sheet is not in the icon set at all, and the script refuses to name a cell
/// whose runner-up was nearly as close - almost always the Active and the Inactive of one
/// landmark, which differ by a few pixels of glow. Named backwards, those two would be wrong
/// in a way nobody could see; unnamed, they are a cell number, which is what every cell was
/// before this existed.
/// </remarks>
internal static class IconNames
{
    private static readonly Dictionary<int, string> ByCell = Load();
    private static readonly Dictionary<string, int> ByName = Reverse(ByCell);

    /// <summary>How many cells have a name.</summary>
    public static int Count => ByCell.Count;

    /// <summary>
    /// The cell a game icon name belongs to, counted from ONE, or 0 when nothing carries it.
    /// </summary>
    /// <remarks>
    /// THE GAME'S NAME AND THE CELL'S NAME ARE THE SAME STRING, which is the whole reason this
    /// direction is worth having. An entity's MinimapIcon component names its icon out of
    /// MinimapIcons.dat - "StoryGlyph", "CorruptionAltarActive" - and the art for that icon is
    /// published as Art/2DArt/minimap/player/&lt;that name&gt;.webp, which is where the table's
    /// names came from. So a marker nothing here can classify can still be drawn as exactly
    /// the picture the game draws for it, without anybody adding a keyword for it first.
    ///
    /// Matched case-insensitively for the usual reason and no measured one: the names line up
    /// exactly in every case seen, and a table regenerated from differently-cased file names
    /// would silently stop matching, which costs a lookup to insure against.
    /// </remarks>
    public static int CellFor(string name)
        => !string.IsNullOrEmpty(name) && ByName.TryGetValue(name, out int cell) ? cell : 0;

    /// <summary>
    /// The table read backwards, name to cell.
    /// </summary>
    /// <remarks>
    /// Built once rather than searched, because this runs per unrecognised marker per frame.
    ///
    /// A DUPLICATE NAME KEEPS THE LOWER CELL. It cannot happen with a generated table - each
    /// icon file claims one cell - and if a hand-edited one ever carries the same name twice,
    /// answering with the first is stable across runs, where TryAdd's opposite would depend on
    /// file order.
    /// </remarks>
    private static Dictionary<string, int> Reverse(Dictionary<int, string> byCell)
    {
        var byName = new Dictionary<string, int>(byCell.Count, StringComparer.OrdinalIgnoreCase);
        foreach ((int cell, string name) in byCell)
        {
            if (!byName.TryGetValue(name, out int seen) || cell < seen)
            {
                byName[name] = cell;
            }
        }

        return byName;
    }

    /// <summary>The name of a cell, counted from ONE, or empty when it has none.</summary>
    public static string For(int cell)
        => ByCell.TryGetValue(cell, out string? name) ? name : string.Empty;

    /// <summary>Whether a cell's name contains this, case-insensitively. Empty matches nothing.</summary>
    public static bool Matches(int cell, string search)
        => !string.IsNullOrWhiteSpace(search)
           && ByCell.TryGetValue(cell, out string? name)
           && name.Contains(search, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the table once, and never throws.
    /// </summary>
    /// <remarks>
    /// A missing or malformed table costs the names and nothing else: every caller already
    /// draws a cell number when there is no name, because half the sheet has none anyway.
    /// Ending a session over a cosmetic lookup table would be the wrong trade by a distance.
    /// </remarks>
    private static Dictionary<int, string> Load()
    {
        var names = new Dictionary<int, string>();
        try
        {
            Assembly assembly = typeof(IconNames).Assembly;
            string? resource = Array.Find(
                assembly.GetManifestResourceNames(),
                candidate => candidate.EndsWith(IconSheet.NameTable, StringComparison.OrdinalIgnoreCase));

            if (resource is null)
            {
                return names;
            }

            using Stream? stream = assembly.GetManifestResourceStream(resource);
            if (stream is null)
            {
                return names;
            }

            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is string line)
            {
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                int tab = line.IndexOf('\t', StringComparison.Ordinal);
                if (tab <= 0 || !int.TryParse(line.AsSpan(0, tab), out int cell))
                {
                    continue;
                }

                string name = line[(tab + 1)..].Trim();
                if (name.Length > 0)
                {
                    names[cell] = name;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or BadImageFormatException)
        {
            // Shipped broken, which is a build mistake rather than anything to end a run over.
        }

        return names;
    }
}
