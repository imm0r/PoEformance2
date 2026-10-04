using System.Globalization;
using System.Text;

namespace PoEformance.Features;

/// <summary>
/// The effect table worked out once, as the columns a grid draws and the text a search reads.
/// </summary>
/// <remarks>
/// THE ITEM BOOK'S SHAPE: the same grid, the same query grammar, the searchable text built once
/// per table. Its rows are projectiles, animated effects and ground effects, told apart by "kind";
/// a row's .ao files - its art variants, what it looks like stuck or bouncing - are offered by the
/// window rather than spread over columns.
/// </remarks>
public sealed class EffectBook : IQuerySource
{
    /// <summary>The headings the column chooser groups the columns under.</summary>
    private const string Named = "What it is";
    private const string Files = "Files";

    private readonly string[] _find;
    private readonly Dictionary<string, int> _rows;
    private readonly Dictionary<string, Held> _held;
    private readonly Dictionary<string, int> _numbers;

    private EffectBook(
        ColumnStore store,
        string[] paths,
        bool[] projectile,
        string[] find,
        Dictionary<string, int> rows,
        string[] groups,
        bool[] shown,
        Dictionary<string, Held> held,
        Dictionary<string, int> numbers)
    {
        Store = store;
        Paths = paths;
        Projectiles = projectile;
        Groups = groups;
        Shown = shown;
        _find = find;
        _rows = rows;
        _held = held;
        _numbers = numbers;
    }

    /// <summary>A book with no effects in it.</summary>
    public static EffectBook Empty { get; } = new(ColumnStore.Empty, [], [], [], [], [], [], [], []);

    /// <summary>The columns, in the order they are drawn.</summary>
    public ColumnStore Store { get; }

    /// <summary>Each row's key - see <see cref="EffectVisual.Path"/>.</summary>
    public string[] Paths { get; }

    /// <summary>Which rows are projectiles, for the grid to ink.</summary>
    public bool[] Projectiles { get; }

    /// <summary>Which heading each column is offered under, for the chooser. One per column.</summary>
    public string[] Groups { get; }

    /// <summary>Which columns a table starts with, before anybody has chosen. One per column.</summary>
    public bool[] Shown { get; }

    /// <summary>How many effects it holds.</summary>
    public int Count => Paths.Length;

    /// <summary>Works the table into columns. Never throws; an empty table gives an empty book.</summary>
    public static EffectBook Of(EffectVisuals? table)
    {
        if (table is null || table.Count == 0)
        {
            return Empty;
        }

        int count = table.Count;
        var paths = new string[count];
        var projectile = new bool[count];
        var find = new string[count];
        var rows = new Dictionary<string, int>(count, StringComparer.OrdinalIgnoreCase);

        var names = new string[count];
        var kinds = new string[count];
        var files = new double[count];
        var filesText = new string[count];
        var models = new string[count];
        var folders = new string[count];

        var text = new StringBuilder(256);
        for (var at = 0; at < count; at++)
        {
            EffectVisual one = table.All[at];
            paths[at] = one.Path;
            projectile[at] = one.Kind == EffectVisuals.Projectile;
            rows.TryAdd(one.Path, at);

            names[at] = one.Name;
            kinds[at] = one.Kind;
            files[at] = one.Files.Count;
            filesText[at] = one.Files.Count.ToString(CultureInfo.InvariantCulture);

            string first = one.Files.Count > 0 ? one.Files[0].Ao : string.Empty;
            models[at] = Tail(first);
            int slash = first.LastIndexOf('/');
            folders[at] = slash > 0 ? first[..slash] : string.Empty;

            text.Clear();
            text.Append(one.Path).Append(' ').Append(one.Name).Append(' ').Append(one.Kind);
            foreach (EffectFile file in one.Files)
            {
                text.Append(' ').Append(file.Label).Append(' ').Append(file.Ao);
            }

            find[at] = text.ToString().ToLowerInvariant();
        }

        (DataColumn Column, string Group, bool Shown)[] laid =
        [
            (DataColumn.Words("name", names), Named, true),
            (DataColumn.Labels("kind", kinds), Named, true),
            (DataColumn.Magnitudes("files", string.Empty, files, filesText), Files, true),
            (DataColumn.Words("model", models), Files, true),
            (DataColumn.Words("folder", folders), Files, false),
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

        return new EffectBook(
            store,
            paths,
            projectile,
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
