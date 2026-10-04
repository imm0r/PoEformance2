namespace PoEformance.Features;

/// <summary>
/// A table worked out once into the columns a grid draws and the text a search reads, and
/// everything a query can ask of it. What the four reference books share.
/// </summary>
/// <remarks>
/// ONE IMPLEMENTATION OF THE THREE QUESTIONS, where there were four. The monster, item, tile and
/// effect books each carried a word-for-word copy of the search, the field match, the number
/// range, the facet count and the value index - and a rule that lived in four places was a rule
/// that could be fixed in three. The facet rail's exact-match click (see <see cref="Value"/>) is
/// the change that made the copies indefensible: it had to land in all four at once.
///
/// WHAT A DERIVED BOOK STILL DECIDES is what its rows ARE: which columns, what each cell says,
/// what the searchable text is made of, and which fields hold several values per row (a monster
/// carries up to sixty-seven skills). Those are facts about a table, and they stay with it. How a
/// query reads them is a fact about queries, and that is here.
///
/// THE SEARCHABLE TEXT IS BUILT ONCE PER TABLE, not once per keystroke. What a keystroke costs is
/// a scan of strings that already exist, which over the monster table is about 1.4 MB of
/// Contains and does not show up at sixty frames a second.
///
/// NOTHING HERE TOUCHES ImGui, so the whole of every book runs on a machine with no game and no
/// window - which is what makes "does a click in the rail leave the rows the rail counted" a
/// thing a test settles rather than a thing somebody squints at on screen.
/// </remarks>
public abstract class ColumnBook : IQuerySource
{
    private readonly string[] _find;
    private readonly Dictionary<string, int> _rows;
    private readonly Dictionary<string, Held> _held;
    private readonly Dictionary<string, int> _numbers;

    /// <param name="store">The columns, in the order they are drawn.</param>
    /// <param name="paths">Each row's key.</param>
    /// <param name="find">Each row's searchable text, already lowercased.</param>
    /// <param name="rows">Row by key, in whatever spelling the derived book normalises to.</param>
    /// <param name="groups">Which heading each column is offered under. One per column.</param>
    /// <param name="shown">Which columns a table starts with. One per column.</param>
    /// <param name="held">The word fields: every value each holds and which rows hold it.</param>
    /// <param name="numbers">The number fields, each the store column it compares.</param>
    protected ColumnBook(
        ColumnStore store,
        string[] paths,
        string[] find,
        Dictionary<string, int> rows,
        string[] groups,
        bool[] shown,
        Dictionary<string, Held> held,
        Dictionary<string, int> numbers)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(find);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(numbers);

        Store = store;
        Paths = paths;
        Groups = groups;
        Shown = shown;
        _find = find;
        _rows = rows;
        _held = held;
        _numbers = numbers;

        Fields = [.. held.Keys.Order(StringComparer.Ordinal), .. numbers.Keys.Order(StringComparer.Ordinal)];
    }

    /// <summary>The columns, in the order they are drawn.</summary>
    public ColumnStore Store { get; }

    /// <summary>Each row's key - the table's own, and what the window selects by.</summary>
    public string[] Paths { get; }

    /// <summary>Which heading each column is offered under, for the chooser. One per column.</summary>
    public string[] Groups { get; }

    /// <summary>Which columns a table starts with, before anybody has chosen. One per column.</summary>
    public bool[] Shown { get; }

    /// <summary>
    /// Every field a query may name, for whatever offers them to somebody typing.
    /// </summary>
    /// <remarks>
    /// SPELT THE WAY A QUERY SPELLS THEM - lower case, no spaces - which is not always how the
    /// column header says it: a column called "atk spd" is asked for as "atkspd", because a space
    /// separates two terms and always will.
    /// </remarks>
    public IReadOnlyList<string> Fields { get; }

    /// <summary>How many rows it holds.</summary>
    public int Count => Paths.Length;

    /// <summary>How many rows there are. What <see cref="IQuerySource"/> means by it.</summary>
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

    /// <summary>
    /// The rows where a field holds a value, or false where there is no such field.
    /// </summary>
    /// <remarks>
    /// TWO READINGS, AND THE TEXT SAYS WHICH. Typed bare - <c>skill:fire</c> - a value is a
    /// substring, because the values in these fields are the game's own ids
    /// (MeleeAtAnimationSpeed, MonsterAttackBlock30Bypass15) and nobody knows them by heart;
    /// finding every skill with "fire" in its name is the useful reading. Quoted -
    /// <c>tag:"beast"</c> - it is the whole value and nothing else, which is what a click in the
    /// facet rail writes.
    ///
    /// THE QUOTED FORM EXISTS BECAUSE THE RAIL'S COUNT HAS TO MEAN SOMETHING. The rail counts the
    /// rows carrying exactly "beast"; a click that then wrote the bare form would also pick up
    /// amphibian_beast, avian_beast and beast_onhit_audio, and the list would be longer than the
    /// number beside the word that was clicked. Over the shipped export that is 18 of 185 tags,
    /// 580 of 5362 skills and 280 of 1214 types - and every item class with a space in its name,
    /// which the bare form cannot even hold. Several values matching is a union: they are all
    /// "this field holding that".
    /// </remarks>
    public bool Value(string field, string value, bool exact, RowSet into)
    {
        ArgumentNullException.ThrowIfNull(into);

        if (!_held.TryGetValue(ColumnQuery.Field(field ?? string.Empty), out Held? held))
        {
            return false;
        }

        string looking = (value ?? string.Empty).ToLowerInvariant();

        for (var at = 0; at < held.Lower.Length; at++)
        {
            bool hit = exact
                ? string.Equals(held.Lower[at], looking, StringComparison.Ordinal)
                : held.Lower[at].Contains(looking, StringComparison.Ordinal);

            if (hit)
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

    /// <summary>
    /// The rows a query matches.
    /// </summary>
    /// <remarks>
    /// A SET PER CALL, which is 344 bytes over the monster table and is the simple thing: a
    /// keystroke makes one, counts the facets against it and drops it. What must not be made per
    /// call is one per facet VALUE, and that is what <see cref="RowSet.CountAnd"/> is for.
    /// </remarks>
    /// <returns>Null where the query names something this table has not got, and says what.</returns>
    public RowSet? Matching(QueryTerm? query, out string error)
    {
        var rows = new RowSet(Count);
        return ColumnQuery.Run(query, this, rows, out error) ? rows : null;
    }

    /// <summary>
    /// What a field holds within a set of rows, and how many rows each of its values covers.
    /// </summary>
    /// <remarks>
    /// COUNTED AGAINST THE WHOLE CURRENT FILTER rather than against everything except this field's
    /// own part of it. The two readings differ: the other one lets somebody pick several values of
    /// one field as alternatives, and this one reads every click as a further narrowing. This is the
    /// one that matches the grammar - two terms side by side mean BOTH - and a rail that narrowed
    /// while the text it writes widened would be two filters wearing one coat.
    ///
    /// SO A ZERO IS WORTH SHOWING, dim. "There are no casters left in this set" is an answer, and a
    /// rail that hides what it cannot offer makes it look like the field does not exist.
    ///
    /// MOST FIRST, because a rail shows a dozen of the five thousand skills and the useful dozen
    /// is the one the rows in front of somebody actually carry. Ties break on the name, so the
    /// order does not shuffle about as the filter moves.
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

    /// <summary>
    /// Which row a key is, or -1 where the table does not hold it.
    /// </summary>
    /// <remarks>
    /// LOOKED UP IN THE SPELLING <see cref="Key"/> SETTLES, which a derived book overrides where
    /// its table normalises - the monster table drops an @variant suffix and turns backslashes
    /// round, and looking an entity's own path up against anything stricter would miss rows the
    /// table plainly contains.
    /// </remarks>
    public int Row(string? path)
        => Key(path) is { Length: > 0 } key && _rows.TryGetValue(key, out int row) ? row : -1;

    /// <summary>The spelling a key is looked up in. The path itself unless a book says otherwise.</summary>
    protected virtual string Key(string? path) => path ?? string.Empty;

    /// <summary>
    /// Makes every column a field: words to match, numbers to compare.
    /// </summary>
    /// <remarks>
    /// OFF THE FINISHED STORE rather than out of a second list to keep in step: a column of words
    /// is something to match, a column of numbers is something to compare, and a column added
    /// next year is both without anybody remembering to say so. A field a derived book has
    /// already put into <paramref name="held"/> - the monster book's multi-valued ones - is left
    /// alone, so a column may not quietly replace it.
    /// </remarks>
    protected static void Index(ColumnStore store, Dictionary<string, Held> held, Dictionary<string, int> numbers)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(numbers);

        for (var column = 0; column < store.Columns.Length; column++)
        {
            DataColumn one = store.Columns[column];
            string key = ColumnQuery.Field(one.Name);

            if (one.Number.Length > 0)
            {
                numbers.TryAdd(key, column);
                continue;
            }

            held.TryAdd(key, Held.Of(one.Text, store.Rows));
        }
    }

    /// <summary>The last part of a path, for the many things the game never names.</summary>
    protected static string Tail(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        int slash = path.LastIndexOf('/');
        return slash >= 0 && slash + 1 < path.Length ? path[(slash + 1)..] : path;
    }

    /// <summary>Every value a field holds, and which rows hold each of them.</summary>
    /// <param name="Values">The values as the table spells them, for showing.</param>
    /// <param name="Lower">The same, lowercased once, for matching.</param>
    /// <param name="Rows">Which rows carry each value.</param>
    protected sealed record Held(string[] Values, string[] Lower, RowSet[] Rows)
    {
        /// <summary>The index of a column holding one value per row. An empty cell holds nothing.</summary>
        public static Held Of(string[] text, int count)
        {
            ArgumentNullException.ThrowIfNull(text);

            var gather = new Gather(count);
            for (var row = 0; row < text.Length; row++)
            {
                gather.Add(row, text[row]);
            }

            return gather.Done();
        }
    }

    /// <summary>
    /// Collects a field's values while a table is walked - several per row where a row has several.
    /// </summary>
    /// <remarks>
    /// CASE-INSENSITIVE ON THE WAY IN, so "Undead" and "undead" are one value spelt the first way
    /// the table spelt it, and the rail does not offer the same word twice.
    /// </remarks>
    protected sealed class Gather(int rows)
    {
        private readonly Dictionary<string, int> _ids = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _values = [];
        private readonly List<RowSet> _rows = [];

        public void Add(int row, string? value)
        {
            if (value is not { Length: > 0 })
            {
                return;
            }

            if (!_ids.TryGetValue(value, out int id))
            {
                id = _values.Count;
                _ids[value] = id;
                _values.Add(value);
                _rows.Add(new RowSet(rows));
            }

            _rows[id].Add(row);
        }

        public Held Done()
            => new(
                [.. _values],
                [.. _values.Select(one => one.ToLowerInvariant())],
                [.. _rows]);
    }
}
