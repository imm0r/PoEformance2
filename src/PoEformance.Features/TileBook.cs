using System.Globalization;

namespace PoEformance.Features;

/// <summary>
/// Every terrain tile and room the install has, as the columns a grid draws and the text a search reads.
/// </summary>
/// <remarks>
/// THE MONSTER AND ITEM BOOKS' SHAPE - see <see cref="ColumnBook"/>. Two sources go into it - the
/// install's own list of <c>.tdt</c> files, and the tiles the game has placed in the area being
/// stood in - and a tile in either is a row, so the area's tiles are listed even before (or without)
/// the install's walk.
///
/// ROOMS ARE ROWS TOO - an <c>.arm</c> beside the <c>.tdt</c> files, told apart by the "kind" column -
/// because they are found the same two ways: the install's walk, and what the area has loaded.
///
/// "HERE" IS A COLUMN AND NOT A SEPARATE LIST, so "only this area" is one word in the query -
/// <c>here:yes</c> - and the rail counts it like any other field. The book is rebuilt when the area
/// changes; the install's list does not change in a session.
/// </remarks>
public sealed class TileBook : ColumnBook
{
    /// <summary>The folder every tile lives under, which the set and folder columns are read past.</summary>
    public const string Root = "Metadata/Terrain/";

    /// <summary>What the "here" column holds for a tile placed in the current area.</summary>
    public const string Here = "yes";

    /// <summary>What the "kind" column holds for a tile definition.</summary>
    public const string Tile = "tile";

    /// <summary>What the "kind" column holds for a room.</summary>
    public const string Room = "room";

    private const string Named = "What it is";
    private const string Area = "This area";

    private TileBook(
        ColumnStore store,
        string[] paths,
        bool[] here,
        int[] placed,
        string[] find,
        Dictionary<string, int> rows,
        string[] groups,
        bool[] shown,
        Dictionary<string, Held> held,
        Dictionary<string, int> numbers)
        : base(store, paths, find, rows, groups, shown, held, numbers)
    {
        HereRows = here;
        Placed = placed;
    }

    /// <summary>A book with no tiles in it.</summary>
    public static TileBook Empty { get; } = new(ColumnStore.Empty, [], [], [], [], [], [], [], [], []);

    /// <summary>Which rows are placed in the current area, for the grid to ink.</summary>
    public bool[] HereRows { get; }

    /// <summary>How many times each row is placed in the current area.</summary>
    public int[] Placed { get; }

    /// <summary>
    /// Works the two lists into one table. Never throws.
    /// </summary>
    /// <param name="install">Every <c>.tdt</c> and <c>.arm</c> the install has, or empty where it has not been walked.</param>
    /// <param name="placed">How many times each tile or room is placed in the current area, by path - or empty.</param>
    /// <param name="needs">What each placed tile or room needs that the shade compiler has not got, by path - see <see cref="AreaNeeds"/> - or null before it is read.</param>
    /// <param name="clocked">Which placed tiles and rooms wear a material whose graphs read the game's clock, by path - see <see cref="AreaReading.Clocked"/> - or null before it is read.</param>
    public static TileBook Of(
        IReadOnlyList<string>? install,
        IReadOnlyDictionary<string, int>? placed,
        IReadOnlyDictionary<string, string>? needs = null,
        IReadOnlyDictionary<string, string>? clocked = null)
    {
        var order = new List<string>((install?.Count ?? 0) + (placed?.Count ?? 0));
        var rows = new Dictionary<string, int>(order.Capacity, StringComparer.OrdinalIgnoreCase);
        foreach (string one in install ?? [])
        {
            if (one.Length > 0 && rows.TryAdd(one, order.Count))
            {
                order.Add(one);
            }
        }

        // THE AREA'S TILES ARE ADDED WHERE THE INSTALL'S LIST LACKS THEM, so the filter works before
        // the walk has run - and a path the game holds that the install does not name is still shown.
        foreach (string one in placed?.Keys ?? [])
        {
            if (one.Length > 0 && rows.TryAdd(one, order.Count))
            {
                order.Add(one);
            }
        }

        int count = order.Count;
        if (count == 0)
        {
            return Empty;
        }

        var paths = new string[count];
        var here = new bool[count];
        var placedRows = new int[count];
        var find = new string[count];
        var names = new string[count];
        var kinds = new string[count];
        var sets = new string[count];
        var folders = new string[count];
        var heres = new string[count];
        var placings = new double[count];
        var placingsText = new string[count];
        var needed = new string[count];
        var clocks = new string[count];

        for (var at = 0; at < count; at++)
        {
            string path = order[at];
            paths[at] = path;

            (string set, string folder, string name) = Split(path);
            names[at] = name;
            kinds[at] = IsRoom(path) ? Room : Tile;
            sets[at] = set;
            folders[at] = folder;

            int times = placed is not null && placed.TryGetValue(path, out int many) ? many : 0;
            here[at] = times > 0;
            placedRows[at] = times;
            heres[at] = times > 0 ? Here : string.Empty;
            placings[at] = times;
            placingsText[at] = times > 0 ? times.ToString(CultureInfo.InvariantCulture) : string.Empty;
            needed[at] = needs is not null && needs.TryGetValue(path, out string? need) ? need : string.Empty;
            clocks[at] = clocked is not null && clocked.ContainsKey(path) ? Here : string.Empty;

            // THE PATH AND THE KIND, AND NOT THE WORD "here": that one is a column, asked for as
            // here:yes, and written into the free text it would also answer a bare "here" with
            // every path that happens to contain those letters.
            find[at] = (path + " " + kinds[at]).ToLowerInvariant();
        }

        (DataColumn Column, string Group, bool Shown)[] laid =
        [
            (DataColumn.Words("name", names), Named, true),
            (DataColumn.Labels("kind", kinds), Named, true),
            (DataColumn.Labels("set", sets), Named, true),
            (DataColumn.Words("folder", folders), Named, true),
            (DataColumn.Labels("here", heres), Area, true),
            (DataColumn.Magnitudes("placed", string.Empty, placings, placingsText), Area, false),

            // WHAT THE PANE WOULD SAY IS LEFT OUT, for every tile of the area at once - see
            // AreaNeeds - so needs:InputVertexColor finds the tiles worth comparing with the game.
            (DataColumn.Words("needs", needed), Area, false),

            // AND WHICH RUN WITH THE CLOCK - a material whose graphs read Time - so clock:yes finds the
            // tiles whose picture moves, to compare with the game.
            (DataColumn.Labels("clock", clocks), Area, false),
        ];

        ColumnStore store = ColumnStore.Of([.. laid.Select(one => one.Column)]);

        var held = new Dictionary<string, Held>(StringComparer.Ordinal);
        var numbers = new Dictionary<string, int>(StringComparer.Ordinal);
        Index(store, held, numbers);

        return new TileBook(
            store,
            paths,
            here,
            placedRows,
            find,
            rows,
            [.. laid.Select(one => one.Group)],
            [.. laid.Select(one => one.Shown)],
            held,
            numbers);
    }

    /// <summary>
    /// The set, the folder and the name of a tile path.
    /// </summary>
    /// <remarks>
    /// THE SET IS THE FIRST FOLDER UNDER <see cref="Root"/> - Woods, Maps, Act4 - which is the handful
    /// of answers a rail is for; the folder is whatever lies between it and the file.
    /// </remarks>
    public static (string Set, string Folder, string Name) Split(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        string rest = path.StartsWith(Root, StringComparison.OrdinalIgnoreCase) ? path[Root.Length..] : path;
        int slash = rest.LastIndexOf('/');
        string name = slash >= 0 ? rest[(slash + 1)..] : rest;
        if (name.EndsWith(".tdt", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".arm", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        string dirs = slash >= 0 ? rest[..slash] : string.Empty;
        int first = dirs.IndexOf('/', StringComparison.Ordinal);
        return first >= 0 ? (dirs[..first], dirs[(first + 1)..], name) : (dirs, string.Empty, name);
    }

    /// <summary>Whether a path is a room rather than a tile definition.</summary>
    public static bool IsRoom(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.EndsWith(".arm", StringComparison.OrdinalIgnoreCase);
    }
}
