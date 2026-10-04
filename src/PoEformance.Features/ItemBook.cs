using System.Globalization;
using System.Text;

namespace PoEformance.Features;

/// <summary>
/// The item table worked out once, as the columns a grid draws and the text a search reads.
/// </summary>
/// <remarks>
/// THE MONSTER BOOK'S SHAPE, ON PURPOSE: the same grid, the same query grammar, the same rule that
/// the searchable text is built once per table and a keystroke only scans strings that already
/// exist. Nothing here touches ImGui, so all of it runs in a test.
///
/// FEW COLUMNS, because an item's interesting part here is its model, and the model is in the
/// pane beside the list rather than in a cell. What the columns carry is what somebody narrows by:
/// the class, base or unique, and the drop level.
/// </remarks>
public sealed class ItemBook : IQuerySource
{
    private readonly string[] _find;
    private readonly Dictionary<string, int> _rows;
    private readonly Dictionary<string, Held> _held;
    private readonly Dictionary<string, int> _numbers;

    private ItemBook(
        ColumnStore store,
        string[] paths,
        bool[] unique,
        string[] find,
        Dictionary<string, int> rows,
        Dictionary<string, Held> held,
        Dictionary<string, int> numbers)
    {
        Store = store;
        Paths = paths;
        Unique = unique;
        _find = find;
        _rows = rows;
        _held = held;
        _numbers = numbers;
    }

    /// <summary>A book with no items in it.</summary>
    public static ItemBook Empty { get; } = new(ColumnStore.Empty, [], [], [], [], [], []);

    /// <summary>The columns, in the order they are drawn.</summary>
    public ColumnStore Store { get; }

    /// <summary>Each row's key - see <see cref="ItemVisual.Path"/>.</summary>
    public string[] Paths { get; }

    /// <summary>Which rows are uniques, for the grid to ink.</summary>
    public bool[] Unique { get; }

    /// <summary>How many items it holds.</summary>
    public int Count => Paths.Length;

    /// <summary>Works the table into columns. Never throws; an empty table gives an empty book.</summary>
    public static ItemBook Of(ItemVisuals? table)
    {
        if (table is null || table.Count == 0)
        {
            return Empty;
        }

        int count = table.Count;
        var paths = new string[count];
        var unique = new bool[count];
        var find = new string[count];
        var rows = new Dictionary<string, int>(count, StringComparer.OrdinalIgnoreCase);

        var names = new string[count];
        var classes = new string[count];
        var kinds = new string[count];
        var drops = new double[count];
        var dropsText = new string[count];
        var cells = new double[count];
        var cellsText = new string[count];
        var models = new string[count];

        var text = new StringBuilder(256);
        for (var at = 0; at < count; at++)
        {
            ItemVisual one = table.All[at];
            paths[at] = one.Path;
            unique[at] = one.Unique;
            rows.TryAdd(one.Path, at);

            names[at] = one.Name;
            classes[at] = one.Class;
            kinds[at] = one.Unique ? "unique" : "base";

            // A UNIQUE'S LEVEL AND SIZE ARE BLANK, NOT ZERO: the layout row it comes from carries
            // neither, and "level 0" would read as an item anybody can drop at the start.
            drops[at] = one.DropLevel;
            dropsText[at] = one.Unique ? string.Empty : one.DropLevel.ToString(CultureInfo.InvariantCulture);
            cells[at] = one.Width * one.Height;
            cellsText[at] = one.Unique
                ? string.Empty
                : string.Create(CultureInfo.InvariantCulture, $"{one.Width}x{one.Height}");
            models[at] = Tail(one.Model);

            text.Clear();
            text.Append(one.Path).Append(' ').Append(one.Name).Append(' ').Append(one.Class)
                .Append(' ').Append(kinds[at]).Append(' ').Append(one.Art)
                .Append(' ').Append(one.Ao).Append(' ').Append(one.Ao2);
            find[at] = text.ToString().ToLowerInvariant();
        }

        ColumnStore store = ColumnStore.Of(
            DataColumn.Words("name", names),
            DataColumn.Labels("class", classes),
            DataColumn.Labels("kind", kinds),
            DataColumn.Magnitudes("drop", string.Empty, drops, dropsText),
            DataColumn.Magnitudes("cells", string.Empty, cells, cellsText),
            DataColumn.Words("model", models));

        // EVERY COLUMN BECOMES A FIELD, the monster book's rule: words are matched, numbers compared.
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

        return new ItemBook(store, paths, unique, find, rows, held, numbers);
    }

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

    /// <summary>Which row a key is, or -1.</summary>
    public int Row(string? path) => path is { Length: > 0 } && _rows.TryGetValue(path, out int row) ? row : -1;

    private static string Tail(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash >= 0 && slash + 1 < path.Length ? path[(slash + 1)..] : path;
    }

    /// <summary>Every value a word column holds, lowercased once, and which rows hold each.</summary>
    private sealed record Held(string[] Lower, RowSet[] Rows)
    {
        public static Held Of(string[] text, int count)
        {
            var ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
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
                    id = lower.Count;
                    ids[value] = id;
                    lower.Add(value.ToLowerInvariant());
                    rows.Add(new RowSet(count));
                }

                rows[id].Add(row);
            }

            return new Held([.. lower], [.. rows]);
        }
    }
}
