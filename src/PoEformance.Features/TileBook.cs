using System.Globalization;

namespace PoEformance.Features;

/// <summary>
/// Every terrain tile and room the install has, as the columns a grid draws and the text a search reads.
/// </summary>
/// <remarks>
/// THE MONSTER AND ITEM BOOKS' SHAPE: the same grid, the same query grammar, the searchable text
/// built once per table. Two sources go into it - the install's own list of <c>.tdt</c> files, and
/// the tiles the game has placed in the area being stood in - and a tile in either is a row, so the
/// area's tiles are listed even before (or without) the install's walk.
///
/// ROOMS ARE ROWS TOO - an <c>.arm</c> beside the <c>.tdt</c> files, told apart by the "kind" column -
/// because they are found the same two ways: the install's walk, and what the area has loaded.
///
/// "HERE" IS A COLUMN AND NOT A SEPARATE LIST, so "only this area" is one word in the query -
/// <c>here:yes</c> - and the rail counts it like any other field. The book is rebuilt when the area
/// changes; the install's list does not change in a session.
/// </remarks>
public sealed class TileBook : IQuerySource
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

    private readonly string[] _find;
    private readonly Dictionary<string, int> _rows;
    private readonly Dictionary<string, Held> _held;
    private readonly Dictionary<string, int> _numbers;

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
    {
        Store = store;
        Paths = paths;
        HereRows = here;
        Placed = placed;
        Groups = groups;
        Shown = shown;
        _find = find;
        _rows = rows;
        _held = held;
        _numbers = numbers;
    }

    /// <summary>A book with no tiles in it.</summary>
    public static TileBook Empty { get; } = new(ColumnStore.Empty, [], [], [], [], [], [], [], [], []);

    /// <summary>The columns, in the order they are drawn.</summary>
    public ColumnStore Store { get; }

    /// <summary>Each row's <c>.tdt</c> path.</summary>
    public string[] Paths { get; }

    /// <summary>Which rows are placed in the current area, for the grid to ink.</summary>
    public bool[] HereRows { get; }

    /// <summary>How many times each row is placed in the current area.</summary>
    public int[] Placed { get; }

    /// <summary>Which heading each column is offered under. One per column.</summary>
    public string[] Groups { get; }

    /// <summary>Which columns a table starts with. One per column.</summary>
    public bool[] Shown { get; }

    /// <summary>How many tiles it holds.</summary>
    public int Count => Paths.Length;

    /// <summary>
    /// Works the two lists into one table. Never throws.
    /// </summary>
    /// <param name="install">Every <c>.tdt</c> and <c>.arm</c> the install has, or empty where it has not been walked.</param>
    /// <param name="placed">How many times each tile or room is placed in the current area, by path - or empty.</param>
    public static TileBook Of(IReadOnlyList<string>? install, IReadOnlyDictionary<string, int>? placed)
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

            find[at] = (path + " " + kinds[at] + (times > 0 ? " here" : string.Empty)).ToLowerInvariant();
        }

        (DataColumn Column, string Group, bool Shown)[] laid =
        [
            (DataColumn.Words("name", names), Named, true),
            (DataColumn.Labels("kind", kinds), Named, true),
            (DataColumn.Labels("set", sets), Named, true),
            (DataColumn.Words("folder", folders), Named, true),
            (DataColumn.Labels("here", heres), Area, true),
            (DataColumn.Magnitudes("placed", string.Empty, placings, placingsText), Area, false),
        ];

        ColumnStore store = ColumnStore.Of([.. laid.Select(one => one.Column)]);

        var held = new Dictionary<string, Held>(StringComparer.Ordinal);
        var numbers = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var column = 0; column < store.Columns.Length; column++)
        {
            DataColumn one = store.Columns[column];
            string key = ColumnQuery.Field(one.Name);
            if (one.Number.Length > 0)
            {
                numbers[key] = column;
                continue;
            }

            held[key] = Held.Of(one.Text, count);
        }

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
    public static bool IsRoom(string path) => path.EndsWith(".arm", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    int IQuerySource.Rows => Count;

    /// <summary>The rows whose searchable text holds this word.</summary>
    public void Words(string word, RowSet into)
    {
        ArgumentNullException.ThrowIfNull(into);

        string looking = (word ?? string.Empty).ToLowerInvariant();
        if (looking.Length == 0)
        {
            into.All();
            return;
        }

        for (var row = 0; row < _find.Length; row++)
        {
            if (_find[row].Contains(looking, StringComparison.Ordinal))
            {
                into.Add(row);
            }
        }
    }

    /// <summary>The rows where a field holds a value, by substring, or false where there is no such field.</summary>
    public bool Value(string field, string value, RowSet into)
    {
        ArgumentNullException.ThrowIfNull(into);

        if (!_held.TryGetValue(ColumnQuery.Field(field ?? string.Empty), out Held? held))
        {
            return false;
        }

        string looking = (value ?? string.Empty).ToLowerInvariant();
        for (var at = 0; at < held.Lower.Length; at++)
        {
            if (held.Lower[at].Contains(looking, StringComparison.Ordinal))
            {
                into.Or(held.Rows[at]);
            }
        }

        return true;
    }

    /// <summary>The rows where a field's number is in a range, or false where there is none.</summary>
    public bool Number(string field, double least, double most, RowSet into)
    {
        ArgumentNullException.ThrowIfNull(into);

        if (!_numbers.TryGetValue(ColumnQuery.Field(field ?? string.Empty), out int column))
        {
            return false;
        }

        double[] numbers = Store.Columns[column].Number;
        for (var row = 0; row < numbers.Length; row++)
        {
            if (numbers[row] >= least && numbers[row] <= most)
            {
                into.Add(row);
            }
        }

        return true;
    }

    /// <summary>The rows a query matches, or null where it names something this table has not got.</summary>
    public RowSet? Matching(QueryTerm? query, out string error)
    {
        var rows = new RowSet(Count);
        return ColumnQuery.Run(query, this, rows, out error) ? rows : null;
    }

    /// <summary>What a field holds within a set of rows, most first - the other books' rail rule.</summary>
    public void Facets(RowSet within, string field, List<Facet> into, int most = 0)
    {
        ArgumentNullException.ThrowIfNull(within);
        ArgumentNullException.ThrowIfNull(into);
        into.Clear();

        if (!_held.TryGetValue(ColumnQuery.Field(field ?? string.Empty), out Held? held))
        {
            return;
        }

        for (var at = 0; at < held.Values.Length; at++)
        {
            into.Add(new Facet(held.Values[at], within.CountAnd(held.Rows[at])));
        }

        into.Sort(static (left, right) => right.Count != left.Count
            ? right.Count.CompareTo(left.Count)
            : string.Compare(left.Value, right.Value, StringComparison.OrdinalIgnoreCase));

        if (most > 0 && into.Count > most)
        {
            into.RemoveRange(most, into.Count - most);
        }
    }

    /// <summary>Which row a path is, or -1.</summary>
    public int Row(string? path) => path is { Length: > 0 } && _rows.TryGetValue(path, out int row) ? row : -1;

    /// <summary>Every value a word column holds, as spelt and lowercased once, and which rows hold each.</summary>
    private sealed record Held(string[] Values, string[] Lower, RowSet[] Rows)
    {
        public static Held Of(string[] text, int count)
        {
            var ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var values = new List<string>();
            var lower = new List<string>();
            var rows = new List<RowSet>();

            for (var row = 0; row < text.Length; row++)
            {
                if (text[row] is not { Length: > 0 } value)
                {
                    continue;
                }

                if (!ids.TryGetValue(value, out int id))
                {
                    id = values.Count;
                    ids[value] = id;
                    values.Add(value);
                    lower.Add(value.ToLowerInvariant());
                    rows.Add(new RowSet(count));
                }

                rows[id].Add(row);
            }

            return new Held([.. values], [.. lower], [.. rows]);
        }
    }
}
