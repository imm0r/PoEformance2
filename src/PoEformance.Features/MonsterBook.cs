using System.Globalization;
using System.Text;
using PoEformance.Game.Components;
using PoEformance.Game.Entities;

namespace PoEformance.Features;

/// <summary>
/// The monster table worked out once, as the columns a grid draws and the text a search reads.
/// </summary>
/// <remarks>
/// OUT OF THE WINDOW AND INTO HERE, which is worth more than tidiness: nothing in this file
/// touches ImGui, so the whole of it runs on a machine with no game and no window. The shipped
/// export is 2733 real monsters committed to the repository, which makes every decision below -
/// what a column holds, what a search matches, which bar a value earns - a thing a test can
/// settle rather than a thing somebody has to squint at on screen.
///
/// THE SEARCHABLE TEXT IS BUILT ONCE PER TABLE, not once per keystroke. Joining tags, skills,
/// modifier ids and their wordings for every monster is real work; done behind a search box it is
/// where a tool starts to feel slow. What a keystroke costs is a scan of the strings that already
/// exist, which over this table is about 1.4 MB of Contains and does not show up at sixty frames
/// a second.
///
/// AND IT INCLUDES WHAT THE GAME SAYS A MODIFIER DOES, not only what it is called. Nobody searches
/// for "MonsterAttackBlock30Bypass15"; they search for "block", and that word is in the sentence
/// rather than in the id. The sentences arrive late - the install's own wordings land on a
/// background walk well after start-up - which is why <see cref="Of"/> takes them and why the
/// caller rebuilds when they change.
/// </remarks>
public sealed class MonsterBook : IQuerySource
{
    /// <summary>The headings the column chooser groups the columns under.</summary>
    private const string Named = "What it is";
    private const string Figures = "Figures";
    private const string Defence = "Defence";
    private const string Reach = "Reach";
    private const string Carries = "What it carries";

    private readonly string[] _find;
    private readonly Dictionary<string, int> _rows;
    private readonly Dictionary<string, Held> _held;
    private readonly Dictionary<string, int> _numbers;

    private MonsterBook(
        ColumnStore store,
        string[] paths,
        bool[] boss,
        string[] find,
        Dictionary<string, int> rows,
        string[] groups,
        bool[] shown,
        Dictionary<string, Held> held,
        Dictionary<string, int> numbers)
    {
        Store = store;
        Paths = paths;
        Boss = boss;
        Groups = groups;
        Shown = shown;
        _find = find;
        _rows = rows;
        _held = held;
        _numbers = numbers;

        Fields = [.. held.Keys.Order(StringComparer.Ordinal), .. numbers.Keys.Order(StringComparer.Ordinal)];
    }

    /// <summary>A book with no monsters in it.</summary>
    public static MonsterBook Empty { get; } = new(
        ColumnStore.Empty, [], [], [], [], [], [], [], []);

    /// <summary>
    /// Every field a query may name, for whatever offers them to somebody typing.
    /// </summary>
    /// <remarks>
    /// SPELT THE WAY A QUERY SPELLS THEM - lower case, no spaces - which is not always how the
    /// column header says it: a column called "atk spd" is asked for as "atkspd", because a space
    /// separates two terms and always will.
    /// </remarks>
    public IReadOnlyList<string> Fields { get; }

    /// <summary>The columns, in the order they are drawn.</summary>
    public ColumnStore Store { get; }

    /// <summary>Each row's metadata path - the table's own key, and what an entity carries.</summary>
    public string[] Paths { get; }

    /// <summary>Which rows the game gives the boss health bar to. True on 363 rows of the export.</summary>
    public bool[] Boss { get; }

    /// <summary>Which heading each column is offered under, for the chooser. One per column.</summary>
    public string[] Groups { get; }

    /// <summary>
    /// Which columns a table starts with, before anybody has chosen. One per column.
    /// </summary>
    /// <remarks>
    /// THE SIX THE WINDOW ALWAYS HAD. Twenty-nine columns are available and a table that opened
    /// with all of them would be unreadable and would answer nothing - the point of the chooser is
    /// that somebody adds the two they are asking about today, not that everything is on at once.
    /// </remarks>
    public bool[] Shown { get; }

    /// <summary>How many monsters it holds.</summary>
    public int Count => Paths.Length;

    /// <summary>
    /// Works the table into columns. Never throws, and an empty table gives an empty book.
    /// </summary>
    /// <param name="table">What was read, whether from the install or from the export.</param>
    /// <param name="said">The game's own stat wordings, where they have been read yet.</param>
    public static MonsterBook Of(MonsterVarieties? table, StatDescriptions? said)
    {
        if (table is null || table.Count == 0)
        {
            return Empty;
        }

        int count = table.Count;
        var paths = new string[count];
        var boss = new bool[count];
        var find = new string[count];
        var rows = new Dictionary<string, int>(count, StringComparer.OrdinalIgnoreCase);

        var filling = new List<Filling>(32);
        Filling Column(string label, string group, ColumnShape shape = ColumnShape.Magnitude,
            string unit = "", bool shown = false)
        {
            var one = new Filling(label, group, shape, unit, count, shown);
            filling.Add(one);
            return one;
        }

        // THE UNITS ARE THE ONES 2733 ROWS SETTLE AND NO OTHERS. Life, damage, experience and model
        // size sit around 100 and are percentages of the base for the level. Attack speed, movement
        // speed, the ranges and poise plainly are not, and naming a unit this table cannot prove is
        // how a display ends up confidently wrong - MonsterVariety's own remarks settle each one.
        //
        // WHAT A FULL BAR MEANS, measured over the export rather than chosen: life and damage fill
        // at 250%, skills at 17, modifiers at 2 - each the column's own ninetieth percentile, with
        // 8% to 10% of rows above it and marked. See ColumnSpread for why that scale and not the
        // two that look more obvious.
        Filling name = Column("name", Named, ColumnShape.Text, shown: true);
        Filling kind = Column("type", Named, ColumnShape.Text, shown: true);
        Filling life = Column("life", Figures, unit: "%", shown: true);
        Filling damage = Column("dmg", Figures, unit: "%", shown: true);
        Filling skills = Column("skills", Carries, shown: true);
        Filling mods = Column("mods", Carries, shown: true);

        Filling xp = Column("xp", Figures, unit: "%");
        Filling model = Column("model", Figures, unit: "%");
        Filling poise = Column("poise", Figures);
        Filling size = Column("size", Figures);

        // CRIT IS A KIND AND NOT A CHANCE - 0, 1 or 2 across the whole table, 98% of them 0 - so it
        // is a column that sorts and prints and is never drawn with a bar. See ColumnShape.
        Filling crit = Column("crit", Figures, ColumnShape.Kind);

        // ARMOUR AND ITS THREE NEIGHBOURS LIVE ON THE TYPE, not on the monster: MonsterVarieties has
        // a MonsterArmour column of its own and it is filled on 16 rows of 2734. A monster whose
        // type row does not resolve gets a BLANK rather than a zero, because "the table cannot say"
        // and "no armour" are different answers and 56% of the types in use really do say zero.
        Filling armour = Column("armour", Defence, unit: "%");
        Filling evasion = Column("evasion", Defence, unit: "%");
        Filling shield = Column("es", Defence, unit: "%");
        Filling spread = Column("spread", Defence, unit: "%");
        Filling resists = Column("resists", Defence);
        Filling summoned = Column("summoned", Defence, ColumnShape.Label);

        Filling speed = Column("speed", Reach);
        Filling swing = Column("atk spd", Reach);
        Filling nearest = Column("atk min", Reach);
        Filling furthest = Column("atk max", Reach);
        Filling notices = Column("aggro min", Reach);
        Filling chases = Column("aggro max", Reach);

        Filling tags = Column("tags", Carries);
        Filling blood = Column("blood", Carries, ColumnShape.Label);

        Filling stance = Column("stance", Named, ColumnShape.Label);
        Filling bossy = Column("boss", Named, ColumnShape.Label);
        Filling built = Column("base", Named, ColumnShape.Text);
        Filling quest = Column("quest", Named, ColumnShape.Row);

        // THE MULTI-VALUED FIELDS, which are the ones a facet rail is worth having for: a monster
        // carries up to sixty-seven skills, ten tags and seven modifiers, and none of that fits in
        // a column. The single-valued ones are gathered off the finished columns below, so that
        // adding a column adds a field to the query without anybody wiring one up.
        var byTag = new Gather(count);
        var bySkill = new Gather(count);
        var byMod = new Gather(count);

        var text = new StringBuilder(512);
        var at = 0;

        foreach ((string path, MonsterVariety one) in table.All)
        {
            MonsterKind? type = table.Kind(one);
            string called = one.Name is { Length: > 0 } named ? named : Tail(path);
            string typed = type?.Id is { Length: > 0 } id ? id : Numbered(one.Type);

            text.Clear();
            text.Append(path).Append(' ').Append(called).Append(' ').Append(typed);

            // SEARCHED ACROSS EVERYTHING RESOLVED, not only the name: somebody looking up which
            // monsters cast a skill, or carry a tag, is asking the question this book exists for.
            int carried = Add(text, table.TagsOf(one), byTag, at);
            Add(text, table.Skills(one), bySkill, at);
            int profiles = Add(text, table.ResistancesOf(one), null, at);
            text.Append(' ').Append(table.BloodName(one));

            int carriedMods = Modifiers(table, one, said, text, byMod, at);

            // The two things the table says with a FLAG rather than with a column, so that typing
            // "boss" finds the bosses. There is no other way to ask this list for them.
            if (one.Boss)
            {
                text.Append(" boss");
            }

            if (type is { Summoned: true })
            {
                text.Append(" summoned");
            }

            paths[at] = path;
            boss[at] = one.Boss;
            find[at] = text.ToString().ToLowerInvariant();
            rows[path] = at;

            name.Words(at, called);
            kind.Words(at, typed);
            life.Percent(at, one.Life);
            damage.Percent(at, one.Damage);
            skills.Count(at, one.SkillCount);
            mods.Count(at, carriedMods);

            xp.Percent(at, one.Xp);
            model.Percent(at, one.ModelSize);
            poise.Number(at, one.Poise, one.Poise.ToString("0.##", CultureInfo.InvariantCulture));
            size.Count(at, one.Size);
            crit.Count(at, one.Crit);

            armour.Percent(at, type?.Armour);
            evasion.Percent(at, type?.Evasion);
            shield.Percent(at, type?.EnergyShield);
            spread.Percent(at, type?.Spread);
            resists.Count(at, profiles);
            summoned.Words(at, type is { Summoned: true } ? "summoned" : string.Empty);

            speed.Count(at, one.Speed);
            swing.Count(at, one.AttackSpeed);
            nearest.Count(at, one.MinAttack);
            furthest.Count(at, one.MaxAttack);
            notices.Count(at, one.MinAggro);
            chases.Count(at, one.MaxAggro);

            tags.Count(at, carried);
            blood.Words(at, table.BloodName(one));

            stance.Words(at, one.Stance);
            bossy.Words(at, one.Boss ? "boss" : string.Empty);
            built.Words(at, one.Base is { Length: > 0 } was ? Tail(was) : string.Empty);

            // GREATER THAN ZERO, not merely non-zero: the install route spells "no quest flag" as
            // -1 because zero is a row of QuestFlags like any other, and the export spells it as 0.
            quest.Number(at, one.Quest, one.Quest > 0 ? Numbered(one.Quest) : string.Empty);

            at++;
        }

        ColumnStore store = ColumnStore.Of([.. filling.Select(one => one.Done())]);

        var held = new Dictionary<string, Held>(StringComparer.Ordinal)
        {
            [ColumnQuery.Field("tag")] = byTag.Done(),
            [ColumnQuery.Field("skill")] = bySkill.Done(),
            [ColumnQuery.Field("mod")] = byMod.Done(),
        };

        var numbers = new Dictionary<string, int>(StringComparer.Ordinal);

        // EVERY COLUMN BECOMES A FIELD, off the finished store rather than out of a second list to
        // keep in step: a column of words is something to match, a column of numbers is something
        // to compare, and a column added next year is both without anybody remembering to say so.
        for (var column = 0; column < store.Columns.Length; column++)
        {
            DataColumn one = store.Columns[column];
            string key = ColumnQuery.Field(one.Name);

            if (one.Number.Length > 0)
            {
                numbers[key] = column;
                continue;
            }

            var gather = new Gather(count);
            for (var row = 0; row < count; row++)
            {
                gather.Add(row, one.Text[row]);
            }

            held[key] = gather.Done();
        }

        return new MonsterBook(
            store,
            paths,
            boss,
            find,
            rows,
            [.. filling.Select(one => one.Group)],
            [.. filling.Select(one => one.Start)],
            held,
            numbers);
    }

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
    /// MATCHED AS A SUBSTRING AND NOT EXACTLY, which is a decision about who is typing. The values
    /// in these fields are the game's own ids - MeleeAtAnimationSpeed, MonsterAttackBlock30Bypass15
    /// - and nobody knows them by heart; "skill:fire" finding every skill with fire in its name is
    /// the useful reading, and the facet rail is where an exact value gets clicked rather than
    /// typed. Several values matching is a union: they are all "this field holding that".
    /// </remarks>
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

    /// <summary>
    /// The rows a query matches.
    /// </summary>
    /// <remarks>
    /// A SET PER CALL, which is 344 bytes over this table and is the simple thing: a keystroke makes
    /// one, counts the facets against it and drops it. What must not be made per call is one per
    /// facet VALUE, and that is what <see cref="RowSet.CountAnd"/> is for.
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

        // MOST FIRST, because a rail shows a dozen of the five thousand skills and the useful dozen
        // is the one the rows in front of somebody actually carry. Ties break on the name, so the
        // order does not shuffle about as the filter moves.
        into.Sort(static (left, right) => right.Count != left.Count
            ? right.Count.CompareTo(left.Count)
            : string.Compare(left.Value, right.Value, StringComparison.OrdinalIgnoreCase));

        if (most > 0 && into.Count > most)
        {
            into.RemoveRange(most, into.Count - most);
        }
    }

    /// <summary>
    /// Which row a path is, or -1 where the table does not hold it.
    /// </summary>
    /// <remarks>
    /// KEYED THE WAY THE TABLE IS KEYED, which is not a detail: MonsterVarieties normalises a path
    /// through <see cref="MonsterVarieties.Same"/> - backslashes to slashes, an @variant suffix
    /// dropped - and holds it case-insensitively. Looking up an entity's own path against anything
    /// stricter would miss rows the table plainly contains, and the symptom would read as a monster
    /// missing from the book rather than as a spelling this method refused.
    /// </remarks>
    public int Row(string? path)
        => MonsterVarieties.Same(path) is { Length: > 0 } key && _rows.TryGetValue(key, out int row)
            ? row
            : -1;

    /// <summary>
    /// Walks a monster's three modifier columns, counting every row and naming the ones it can.
    /// </summary>
    /// <remarks>
    /// EVERY ROW COUNTS, INCLUDING THE ONES THAT RESOLVE TO NOTHING. A book that counted only the
    /// modifiers it could name would show four where a monster has seven, and look complete while
    /// doing it - and seven is the number somebody came here to count. Against the shipped export
    /// all of them resolve; it is the install's own tables, where a table can simply be missing,
    /// that this is written for.
    /// </remarks>
    private static int Modifiers(
        MonsterVarieties table,
        MonsterVariety one,
        StatDescriptions? said,
        StringBuilder text,
        Gather into,
        int row)
        => Slot(table, one.Mods, said, text, into, row)
            + Slot(table, one.Mods2, said, text, into, row)
            + Slot(table, one.SpecialMods, said, text, into, row);

    private static int Slot(
        MonsterVarieties table,
        IReadOnlyList<int>? rows,
        StatDescriptions? said,
        StringBuilder text,
        Gather into,
        int row)
    {
        if (rows is null)
        {
            return 0;
        }

        foreach (int at in rows)
        {
            if (table.Modifier(at) is not { } mod)
            {
                // NAMED BY ITS NUMBER WHERE IT RESOLVES TO NOTHING, and indexed under that name -
                // so "which monsters carry a modifier this build cannot name" is a question the
                // query can answer, rather than a gap it hides.
                text.Append(' ').Append(Numbered(at));
                into.Add(row, Numbered(at));
                continue;
            }

            text.Append(' ').Append(mod.Id);
            into.Add(row, mod.Id);

            foreach (ModifierStat stat in mod.Stats ?? [])
            {
                text.Append(' ').Append(stat.Worded(said) ?? stat.Stat);
            }
        }

        return rows.Count;
    }

    /// <summary>Writes each of them into the searchable text and the index, and counts them.</summary>
    private static int Add(StringBuilder text, IEnumerable<string> said, Gather? into, int row)
    {
        var count = 0;
        foreach (string one in said)
        {
            text.Append(' ').Append(one);
            into?.Add(row, one);
            count++;
        }

        return count;
    }

    private static string Numbered(int row) => "#" + row.ToString(CultureInfo.InvariantCulture);

    /// <summary>Every value a field holds, and which rows hold each of them.</summary>
    /// <param name="Values">The values as the table spells them, for showing.</param>
    /// <param name="Lower">The same, lowercased once, for matching.</param>
    /// <param name="Rows">Which rows carry each value.</param>
    private sealed record Held(string[] Values, string[] Lower, RowSet[] Rows);

    /// <summary>Collects a field's values while the table is walked.</summary>
    private sealed class Gather(int rows)
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

    /// <summary>
    /// One column being filled in, so that the build loop reads as a list of facts.
    /// </summary>
    /// <remarks>
    /// TWENTY-NINE COLUMNS IS FIFTY-EIGHT ARRAYS otherwise, declared and passed and kept in step by
    /// hand - and the way that goes wrong is silent: one of them drifts by a row, and a monster is
    /// drawn with the next one's numbers. Here a column is one object, named once and written once
    /// per row, so a column that is declared and never filled is a null the first draw trips over
    /// rather than a plausible wrong answer.
    /// </remarks>
    private sealed class Filling(
        string label, string group, ColumnShape shape, string unit, int rows, bool shown)
    {
        private readonly double[] _number =
            shape is ColumnShape.Text or ColumnShape.Label ? [] : new double[rows];

        private readonly string[] _text = new string[rows];

        /// <summary>Which heading the chooser offers this column under.</summary>
        public string Group { get; } = group;

        /// <summary>Whether a table that nobody has configured starts with it.</summary>
        public bool Start { get; } = shown;

        public void Words(int row, string? text) => _text[row] = text ?? string.Empty;

        public void Count(int row, int value)
        {
            _number[row] = value;
            _text[row] = value.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// A percentage, or a blank where there is nothing to say.
        /// </summary>
        /// <remarks>
        /// A BLANK IS NOT A ZERO, and the defence columns are why: 56% of the types in use really do
        /// carry no armour, so zero is a real and common answer - while a monster whose type row
        /// does not resolve at all has no answer. Printed the same, the second would be read as the
        /// first on every row of a table that had lost its MonsterTypes.
        /// </remarks>
        public void Percent(int row, int? value)
        {
            _number[row] = value ?? 0;
            _text[row] = value is { } got
                ? got.ToString(CultureInfo.InvariantCulture) + "%"
                : string.Empty;
        }

        public void Number(int row, double value, string text)
        {
            _number[row] = value;
            _text[row] = text;
        }

        public DataColumn Done() => shape switch
        {
            ColumnShape.Text => DataColumn.Words(label, _text),
            ColumnShape.Label => DataColumn.Labels(label, _text),
            ColumnShape.Kind => DataColumn.Codes(label, _number, _text),
            ColumnShape.Row => DataColumn.RowNumbers(label, _number, _text),
            _ => DataColumn.Magnitudes(label, unit, _number, _text),
        };
    }

    /// <summary>The last part of a metadata path, for the many monsters the game never names.</summary>
    private static string Tail(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash >= 0 && slash + 1 < path.Length ? path[(slash + 1)..] : path;
    }
}
