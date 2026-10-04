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
/// FEW COLUMNS SHOWN, MORE TO CHOOSE FROM. An item's interesting part here is its model, and the
/// model is in the pane beside the list rather than in a cell; what the starting columns carry is
/// what somebody narrows by - the class, base or unique, the drop level. The art id and the other
/// file names are there for the question "which file is this", and are switched on when it is asked.
/// </remarks>
public sealed class ItemBook : IQuerySource
{
    /// <summary>The headings the column chooser groups the columns under.</summary>
    private const string Named = "What it is";
    private const string Figures = "Figures";
    private const string Files = "Files";

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
        string[] groups,
        bool[] shown,
        Dictionary<string, Held> held,
        Dictionary<string, int> numbers)
    {
        Store = store;
        Paths = paths;
        Unique = unique;
        Groups = groups;
        Shown = shown;
        _find = find;
        _rows = rows;
        _held = held;
        _numbers = numbers;
    }

    /// <summary>A book with no items in it.</summary>
    public static ItemBook Empty { get; } = new(ColumnStore.Empty, [], [], [], [], [], [], [], []);

    /// <summary>The columns, in the order they are drawn.</summary>
    public ColumnStore Store { get; }

    /// <summary>Each row's key - see <see cref="ItemVisual.Path"/>.</summary>
    public string[] Paths { get; }

    /// <summary>Which rows are uniques, for the grid to ink.</summary>
    public bool[] Unique { get; }

    /// <summary>Which heading each column is offered under, for the chooser. One per column.</summary>
    public string[] Groups { get; }

    /// <summary>Which columns a table starts with, before anybody has chosen. One per column.</summary>
    public bool[] Shown { get; }

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
        var arts = new string[count];
        var drops = new double[count];
        var dropsText = new string[count];
        var cells = new double[count];
        var cellsText = new string[count];
        var widths = new double[count];
        var widthsText = new string[count];
        var heights = new double[count];
        var heightsText = new string[count];
        var models = new string[count];
        var seconds = new string[count];
        var icons = new string[count];

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
            arts[at] = one.Art;

            // A UNIQUE'S LEVEL AND SIZE ARE BLANK, NOT ZERO: the layout row it comes from carries
            // neither, and "level 0" would read as an item anybody can drop at the start.
            drops[at] = one.DropLevel;
            dropsText[at] = Blank(one.Unique, one.DropLevel);
            cells[at] = one.Width * one.Height;
            cellsText[at] = one.Unique
                ? string.Empty
                : string.Create(CultureInfo.InvariantCulture, $"{one.Width}x{one.Height}");
            widths[at] = one.Width;
            widthsText[at] = Blank(one.Unique, one.Width);
            heights[at] = one.Height;
            heightsText[at] = Blank(one.Unique, one.Height);

            models[at] = Tail(one.Model);
            seconds[at] = Tail(one.Ao2);
            icons[at] = Tail(one.Icon);

            text.Clear();
            text.Append(one.Path).Append(' ').Append(one.Name).Append(' ').Append(one.Class)
                .Append(' ').Append(kinds[at]).Append(' ').Append(one.Art)
                .Append(' ').Append(one.Ao).Append(' ').Append(one.Ao2);
            find[at] = text.ToString().ToLowerInvariant();
        }

        (DataColumn Column, string Group, bool Shown)[] laid =
        [
            (DataColumn.Words("name", names), Named, true),
            (DataColumn.Labels("class", classes), Named, true),
            (DataColumn.Labels("kind", kinds), Named, true),
            (DataColumn.Words("art", arts), Named, false),
            (DataColumn.Magnitudes("drop", string.Empty, drops, dropsText), Figures, true),
            (DataColumn.Magnitudes("cells", string.Empty, cells, cellsText), Figures, true),
            (DataColumn.Magnitudes("width", string.Empty, widths, widthsText), Figures, false),
            (DataColumn.Magnitudes("height", string.Empty, heights, heightsText), Figures, false),
            (DataColumn.Words("model", models), Files, true),
            (DataColumn.Words("ao2", seconds), Files, false),
            (DataColumn.Words("icon", icons), Files, false),
        ];

        ColumnStore store = ColumnStore.Of([.. laid.Select(one => one.Column)]);

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

        return new ItemBook(
            store,
            paths,
            unique,
            find,
            rows,
            [.. laid.Select(one => one.Group)],
            [.. laid.Select(one => one.Shown)],
            held,
            numbers);
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

    /// <summary>
    /// What a field holds within a set of rows, and how many rows each of its values covers.
    /// </summary>
    /// <remarks>
    /// THE MONSTER BOOK'S RULE: counted against the whole current filter, most first with ties on
    /// the name so the order does not shuffle as the filter moves, and a zero kept rather than
    /// dropped - "no bows left in this set" is an answer.
    /// </remarks>
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

    /// <summary>Which row a key is, or -1.</summary>
    public int Row(string? path) => path is { Length: > 0 } && _rows.TryGetValue(path, out int row) ? row : -1;

    private static string Blank(bool unique, int value)
        => unique ? string.Empty : value.ToString(CultureInfo.InvariantCulture);

    private static string Tail(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash >= 0 && slash + 1 < path.Length ? path[(slash + 1)..] : path;
    }

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
                    id = lower.Count;
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
