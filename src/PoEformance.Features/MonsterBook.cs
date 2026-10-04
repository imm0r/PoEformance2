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
public sealed class MonsterBook : ColumnBook
{
    /// <summary>The headings the column chooser groups the columns under.</summary>
    private const string Named = "What it is";
    private const string Figures = "Figures";
    private const string Defence = "Defence";
    private const string Reach = "Reach";
    private const string Carries = "What it carries";

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
        : base(store, paths, find, rows, groups, shown, held, numbers)
    {
        Boss = boss;
    }

    /// <summary>A book with no monsters in it.</summary>
    public static MonsterBook Empty { get; } = new(
        ColumnStore.Empty, [], [], [], [], [], [], [], []);

    /// <summary>Which rows the game gives the boss health bar to. True on 363 rows of the export.</summary>
    public bool[] Boss { get; }

    /// <summary>
    /// How the modifier lines in this table divide up between worded and not.
    /// </summary>
    /// <remarks>
    /// COUNTED OVER DISTINCT MODIFIER ROWS rather than per monster: one modifier sits on hundreds
    /// of them, and counting it once per monster would say more about how common a modifier is
    /// than about how much of the table can be read.
    ///
    /// THE POINT OF SPLITTING "NOT WORDED" IN TWO. A stat the game words only as part of a group
    /// could be worded here - a modifier holds every value in its own group, which is exactly what
    /// the single-stat rule assumes a caller does not have. A stat with no sentence anywhere is
    /// engine-internal and never had one. The first is work worth doing and the second is not, and
    /// one number for both says which is which to nobody.
    /// </remarks>
    /// <param name="Lines">Stat lines over every modifier row something points at.</param>
    /// <param name="Worded">Those the game has a sentence for.</param>
    /// <param name="Shared">Those the game words only as part of a multi-stat group.</param>
    /// <param name="Silent">Those with no sentence anywhere - the engine's own bookkeeping.</param>
    public readonly record struct Wordings(int Lines, int Worded, int Shared, int Silent);

    /// <summary>How much of this table's modifier text can be read - see <see cref="Wordings"/>.</summary>
    public Wordings Worded { get; private init; }

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

        // WHICH MODIFIER ROWS THE TABLE ACTUALLY USES, so the wording count below is over each of
        // them once. The same modifier sits on hundreds of monsters.
        var seen = new HashSet<int>();

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

            int carriedMods = Modifiers(table, one, said, text, byMod, at, seen);

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

        // EVERY COLUMN BECOMES A FIELD as well - words to match, numbers to compare - and the
        // three multi-valued ones above are kept as they are: see ColumnBook.Index.
        Index(store, held, numbers);

        return new MonsterBook(
            store,
            paths,
            boss,
            find,
            rows,
            [.. filling.Select(one => one.Group)],
            [.. filling.Select(one => one.Start)],
            held,
            numbers)
        {
            Worded = Counted(table, said, seen),
        };
    }

    /// <summary>
    /// The spelling a path is looked up in.
    /// </summary>
    /// <remarks>
    /// KEYED THE WAY THE TABLE IS KEYED, which is not a detail: MonsterVarieties normalises a path
    /// through <see cref="MonsterVarieties.Same"/> - backslashes to slashes, an @variant suffix
    /// dropped - and holds it case-insensitively. Looking up an entity's own path against anything
    /// stricter would miss rows the table plainly contains, and the symptom would read as a monster
    /// missing from the book rather than as a spelling this method refused.
    /// </remarks>
    protected override string Key(string? path) => MonsterVarieties.Same(path);

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
        int row,
        HashSet<int> seen)
        => Slot(table, one.Mods, said, text, into, row, seen)
            + Slot(table, one.Mods2, said, text, into, row, seen)
            + Slot(table, one.SpecialMods, said, text, into, row, seen);

    private static int Slot(
        MonsterVarieties table,
        IReadOnlyList<int>? rows,
        StatDescriptions? said,
        StringBuilder text,
        Gather into,
        int row,
        HashSet<int> seen)
    {
        if (rows is null)
        {
            return 0;
        }

        foreach (int at in rows)
        {
            seen.Add(at);

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

    /// <summary>
    /// Counts how much of the modifier text the game can actually word - see <see cref="Wordings"/>.
    /// </summary>
    private static Wordings Counted(MonsterVarieties table, StatDescriptions? said, HashSet<int> rows)
    {
        var lines = 0;
        var worded = 0;
        var shared = 0;

        foreach (int row in rows)
        {
            foreach (ModifierStat stat in table.Modifier(row)?.Stats ?? [])
            {
                lines++;

                if (stat.Worded(said) is { Length: > 0 })
                {
                    worded++;
                }
                else if (said?.Shared(stat.Stat) == true)
                {
                    shared++;
                }
            }
        }

        return new Wordings(lines, worded, shared, lines - worded - shared);
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
        ///
        /// AND THE NUMBER SAYS SO TOO. Written as 0 it sorted, binned and answered "armour=0" as if
        /// it were a measured nothing; as NaN the spread leaves it out and no range holds it. See
        /// ColumnSpread.Of.
        /// </remarks>
        public void Percent(int row, int? value)
        {
            _number[row] = value ?? double.NaN;
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
}
