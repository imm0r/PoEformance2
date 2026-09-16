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
public sealed class MonsterBook
{
    private readonly string[] _find;
    private readonly Dictionary<string, int> _rows;

    private MonsterBook(
        ColumnStore store,
        string[] paths,
        bool[] boss,
        string[] find,
        Dictionary<string, int> rows)
    {
        Store = store;
        Paths = paths;
        Boss = boss;
        _find = find;
        _rows = rows;
    }

    /// <summary>A book with no monsters in it.</summary>
    public static MonsterBook Empty { get; } = new(ColumnStore.Empty, [], [], [], []);

    /// <summary>The columns, in the order they are drawn.</summary>
    public ColumnStore Store { get; }

    /// <summary>Each row's metadata path - the table's own key, and what an entity carries.</summary>
    public string[] Paths { get; }

    /// <summary>Which rows the game gives the boss health bar to. True on 363 rows of the export.</summary>
    public bool[] Boss { get; }

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

        var name = new string[count];
        var kind = new string[count];
        var life = new double[count];
        var lifeText = new string[count];
        var damage = new double[count];
        var damageText = new string[count];
        var skills = new double[count];
        var skillsText = new string[count];
        var mods = new double[count];
        var modsText = new string[count];

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
            Add(text, table.TagsOf(one));
            Add(text, table.Skills(one));
            Add(text, table.ResistancesOf(one));
            text.Append(' ').Append(table.BloodName(one));

            int carried = Modifiers(table, one, said, text);

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

            name[at] = called;
            kind[at] = typed;
            life[at] = one.Life;
            lifeText[at] = Percent(one.Life);
            damage[at] = one.Damage;
            damageText[at] = Percent(one.Damage);
            skills[at] = one.SkillCount;
            skillsText[at] = Plain(one.SkillCount);
            mods[at] = carried;
            modsText[at] = Plain(carried);

            at++;
        }

        // THE UNITS ARE THE ONES 2733 ROWS SETTLE AND NO OTHERS. Life and damage sit around 100
        // and are percentages of the base for the level; a count of skills or modifiers is a
        // count. Every other figure this table carries is left to the detail pane, where it can
        // be labelled honestly rather than squeezed into a header.
        //
        // WHAT A FULL BAR MEANS, measured over the export rather than chosen: life and damage fill
        // at 250%, skills at 17, modifiers at 2 - each the column's own ninetieth percentile, with
        // 8% to 10% of rows above it and marked. See ColumnSpread for why that scale and not the
        // two that look more obvious.
        ColumnStore store = ColumnStore.Of(
            DataColumn.Words("name", name),
            DataColumn.Words("type", kind),
            DataColumn.Magnitudes("life", "%", life, lifeText),
            DataColumn.Magnitudes("dmg", "%", damage, damageText),
            DataColumn.Magnitudes("skills", string.Empty, skills, skillsText),
            DataColumn.Magnitudes("mods", string.Empty, mods, modsText));

        return new MonsterBook(store, paths, boss, find, rows);
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
    /// Fills <paramref name="into"/> with the rows that match, in table order.
    /// </summary>
    /// <remarks>
    /// INTO A LIST THE CALLER KEEPS, so that typing does not allocate a new list per keystroke -
    /// after the first few it is holding the capacity it needs and the loop only writes.
    /// </remarks>
    public void Filter(string? search, List<int> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        into.Clear();

        string looking = (search ?? string.Empty).Trim().ToLowerInvariant();

        for (var row = 0; row < _find.Length; row++)
        {
            if (looking.Length == 0 || _find[row].Contains(looking, StringComparison.Ordinal))
            {
                into.Add(row);
            }
        }
    }

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
        MonsterVarieties table, MonsterVariety one, StatDescriptions? said, StringBuilder text)
        => Slot(table, one.Mods, said, text)
            + Slot(table, one.Mods2, said, text)
            + Slot(table, one.SpecialMods, said, text);

    private static int Slot(
        MonsterVarieties table, IReadOnlyList<int>? rows, StatDescriptions? said, StringBuilder text)
    {
        if (rows is null)
        {
            return 0;
        }

        foreach (int row in rows)
        {
            if (table.Modifier(row) is not { } mod)
            {
                text.Append(' ').Append(Numbered(row));
                continue;
            }

            text.Append(' ').Append(mod.Id);

            foreach (ModifierStat stat in mod.Stats ?? [])
            {
                text.Append(' ').Append(stat.Worded(said) ?? stat.Stat);
            }
        }

        return rows.Count;
    }

    private static void Add(StringBuilder text, IEnumerable<string> said)
    {
        foreach (string one in said)
        {
            text.Append(' ').Append(one);
        }
    }

    private static string Percent(int value)
        => value.ToString(CultureInfo.InvariantCulture) + "%";

    private static string Plain(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Numbered(int row) => "#" + row.ToString(CultureInfo.InvariantCulture);

    /// <summary>The last part of a metadata path, for the many monsters the game never names.</summary>
    private static string Tail(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash >= 0 && slash + 1 < path.Length ? path[(slash + 1)..] : path;
    }
}
