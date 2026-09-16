using System.Globalization;
using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.Components;
using PoEformance.Game.Entities;

namespace PoEformance.Overlay;

/// <summary>
/// The game's monster table as something a person can read, search and sort.
/// </summary>
/// <remarks>
/// A REFERENCE BOOK RATHER THAN A QUERY. Everything else that touches this table asks about the
/// one thing in front of the player; here nothing is in front of anybody and the table itself is
/// the subject - which is why this is a list beside a detail pane rather than a row per monster.
/// A monster carries three modifier lists, up to sixty-seven skills and ten tags, and none of that
/// fits in a cell.
///
/// EVERY ROW NUMBER IS RESOLVED AND THE NUMBER STAYS. Type, blood, tags, skills and the three
/// modifier columns are all row numbers into other tables, and MonsterVarieties already joins
/// them; what this adds is that a row which resolves to NOTHING is still shown, as "#4211". A
/// list that silently dropped what it could not name would look complete and be wrong - the same
/// trap <see cref="MonsterVarieties.Skills"/> names, and the reason the modifiers here are walked
/// row by row rather than through <see cref="MonsterVarieties.Modifiers"/>, which drops them.
/// Against the shipped export nothing is dropped either way; see <see cref="Mods"/> for why it
/// still matters.
///
/// EVERY NUMBER IT CAN NAME, IT NAMES - and the ones it cannot, it leaves bare. MonsterVariety's
/// own remarks settled the units across 2733 rows: Life, Damage, Xp and ModelSize sit around 100
/// and are PERCENTAGES of the base for the level; AttackSpeed, Speed and the aggro ranges are
/// plainly not, and naming a unit the table cannot prove is how a display ends up confidently
/// wrong. Crit is the trap in that set - it holds 0, 1 or 2 and is a KIND, not a chance - so it is
/// never drawn with a percent sign.
///
/// THE TEXT EVERY ROW IS SEARCHED BY, AND EVERY NUMBER IT PRINTS, IS BUILT ONCE PER TABLE. Joining
/// tags, skills and modifier ids for 2733 monsters is real work; done per frame it is work behind a
/// search box, which is where a tool starts to feel slow. So is formatting a cell: this window used
/// to submit fifteen hundred rows to show forty, which is some seven thousand strings built and
/// thrown away every frame. <see cref="MonsterBook"/> does both once, when the table arrives.
///
/// WHAT IS STILL MISSING, so that nobody has to rediscover it: six columns of a table holding some
/// two hundred thousand facts answer "what is this one" and nothing of the form "which of these".
/// docs/reading-big-tables.md is the design for the viewer that does - facets, a query grammar and
/// a comparison of pinned rows - and this window is its first stage.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class MonsterBookWindow(Func<MonsterVarieties> table, Func<StatDescriptions> sentences)
{
    /// <summary>How long a search string may be.</summary>
    private const uint SearchLength = 96;

    private readonly PaneSplit _split = new(0.46f);
    private readonly DataGrid _grid = new();

    /// <summary>The table the book was built from, to notice when a different one arrives.</summary>
    private MonsterVarieties _of = MonsterVarieties.Empty;

    /// <summary>And the sentences it was built with, which arrive later than the table does.</summary>
    private StatDescriptions _said = StatDescriptions.Empty;

    private MonsterBook _page = MonsterBook.Empty;

    /// <summary>Which rows the search leaves, as row numbers into the book.</summary>
    private readonly List<int> _shown = [];

    private string _search = string.Empty;
    private string _chosen = string.Empty;

    /// <summary>The selected row, or -1. Kept beside the path so the grid can mark it.</summary>
    private int _chosenRow = -1;

    /// <summary>The filter has to run again - the table changed, or somebody typed.</summary>
    private bool _refilter = true;

    /// <summary>What the grid asks about a row it is drawing. Made once - see <see cref="Grid"/>.</summary>
    private Func<int, Vector4?>? _ink;

    /// <summary>And what a tooltip on that row says.</summary>
    private Func<int, string>? _hover;

    /// <summary>Draws the tab.</summary>
    public void DrawTab()
    {
        MonsterVarieties all = table();
        StatDescriptions said = sentences();
        Read(all, said);

        if (_page.Count == 0)
        {
            ImGui.TextDisabled("No monster table loaded - see data/monster-varieties.json.");
            return;
        }

        Header(all, said);
        Filter();

        float left = _split.Left();
        if (ImGui.BeginChild("##monster-list", new Vector2(left, 0f), ImGuiChildFlags.Borders))
        {
            Grid();
        }

        ImGui.EndChild();

        _split.Bar();

        if (ImGui.BeginChild("##monster-detail", new Vector2(0f, 0f), ImGuiChildFlags.Borders))
        {
            Detail(all, said);
        }

        ImGui.EndChild();
    }

    /// <summary>
    /// Works the table into columns, once.
    /// </summary>
    /// <remarks>
    /// COMPARED BY REFERENCE and not by count: the table is loaded at start-up today and read from
    /// the install a moment later, and two different tables can perfectly well hold the same number
    /// of monsters. A count would keep showing the old one.
    ///
    /// THE SENTENCES ARE PART OF THAT KEY because they are part of the searchable text, and they
    /// arrive LATE - the install's own wordings land on a background walk well after start-up. A
    /// book keyed on the monster table alone would keep the text it built from the shipped export
    /// for the rest of the session, so searching for "Block" would find nothing on exactly the
    /// machines that can word it best.
    /// </remarks>
    private void Read(MonsterVarieties all, StatDescriptions said)
    {
        if (ReferenceEquals(_of, all) && ReferenceEquals(_said, said))
        {
            return;
        }

        _of = all;
        _said = said;
        _page = MonsterBook.Of(all, said);

        // THE SELECTION FOLLOWS THE PATH AND NOT THE ROW NUMBER. A new table is a different set of
        // rows in a different order, so the row somebody had chosen is very likely a different
        // monster in it - and the path is the one identity that survives a rebuild.
        _chosenRow = _page.Row(_chosen);

        // Filtered here rather than on the next frame, so the count in the header is the count of
        // what is in the list underneath it from the very first frame.
        _refilter = true;
        Filter();
    }

    private void Header(MonsterVarieties all, StatDescriptions said)
    {
        float room = OverlayLayout.ButtonRoom("Copy list");
        if (OverlayLayout.Search(
                "###monster-find",
                "name, path, type, tag, skill, modifier...",
                ref _search,
                SearchLength,
                room))
        {
            _refilter = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("Copy list"))
        {
            // WHAT IS ON SCREEN and not the whole table: a filtered list is the answer somebody
            // worked out, and pasting it into a conversation about it is what this is for. In the
            // order the grid has it, so that a sort by life copies out sorted by life.
            DataColumn[] columns = _page.Store.Columns;
            ImGui.SetClipboardText(string.Join(
                '\n',
                _shown.Select(row =>
                    $"{_page.Paths[row]}\t{columns[0].Text[row]}\t{columns[1].Text[row]}")));
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"The {_shown.Count} listed rows as path, name and type.");
        }

        ImGui.TextDisabled(
            $"{_shown.Count.ToString(CultureInfo.InvariantCulture)} of "
            + $"{all.Count.ToString(CultureInfo.InvariantCulture)} monsters"
            + $"  |  {all.NamedSkills.ToString(CultureInfo.InvariantCulture)} named skills"
            + $"  |  {all.NamedTags.ToString(CultureInfo.InvariantCulture)} named tags");

        // WHERE THE TABLE ITSELF CAME FROM, which is now two possible answers rather than one: the
        // shipped export writes the date it was built, and the install's own tables say so in
        // words. A stale export and a live read look identical on screen otherwise, and the way an
        // export fails is that a league's new monsters simply have no name.
        if (all.Generated is { Length: > 0 } made)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"  |  table: {ImGuiText.Escape(made)}");
        }

        // WHICH SENTENCES ARE IN FORCE, on hover rather than on the line, the same fact
        // AtlasWatch.ContentSource reports and for the same reason: the install's own wordings and
        // a six-month-old export look identical on screen right up to the handful GGG has reworded.
        ImGui.SameLine();
        ImGui.TextDisabled($"  |  {said.Count.ToString(CultureInfo.InvariantCulture)} stat wordings");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(ImGuiText.Escape(said.Source));
        }
    }

    private void Filter()
    {
        if (!_refilter)
        {
            return;
        }

        _refilter = false;
        _page.Filter(_search, _shown);

        // The grid sorts what it is given, and it has just been given a different list.
        _grid.Resort();
    }

    /// <summary>
    /// The list, drawn by a grid that knows nothing about monsters.
    /// </summary>
    /// <remarks>
    /// THE TWO THINGS THE GRID CANNOT WORK OUT FOR ITSELF are handed to it here: which rows are
    /// bosses - a flag the table carries that no column shows - and that hovering a row should
    /// print its path, which is this table's own key and the string somebody came to copy.
    ///
    /// KEPT IN FIELDS rather than written at the call, because a lambda written there is a new
    /// delegate every frame. They read <see cref="_page"/> when they are called rather than
    /// closing over the book that existed when they were made, so a table that arrives later is
    /// picked up without rebuilding them.
    /// </remarks>
    private void Grid()
    {
        _ink ??= row => _page.Boss[row] ? OverlayInk.Name : null;
        _hover ??= row => _page.Paths[row];

        int chosen = _grid.Draw("##monsters", _page.Store, _shown, _chosenRow, _ink, _hover);

        if (chosen == _chosenRow)
        {
            return;
        }

        _chosenRow = chosen;
        _chosen = chosen >= 0 && chosen < _page.Paths.Length ? _page.Paths[chosen] : string.Empty;
    }

    private void Detail(MonsterVarieties all, StatDescriptions said)
    {
        if (_chosen.Length == 0 || all.Find(_chosen) is not { } one)
        {
            ImGui.TextDisabled("Choose a monster on the left.");
            return;
        }

        Identity(all, one);
        ImGui.Separator();

        Figures(one);
        Type(all, one);
        Words("Tags", all.TagsOf(one));
        Words("Skills", all.Skills(one));
        Mods(all, one, said);
        Words("Built on", one.Inherits ?? []);
    }

    private void Identity(MonsterVarieties all, MonsterVariety one)
    {
        OverlayFonts.PushHeading();
        try
        {
            ImGui.TextUnformatted(one.Name is { Length: > 0 } named ? named : Tail(_chosen));
        }
        finally
        {
            OverlayFonts.PopHeading();
        }

        if (one.Name is not { Length: > 0 })
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(the game gives this one no name)");
        }

        OverlayFonts.PushMono();
        try
        {
            if (ImGui.Selectable($"{_chosen}###monster-path", false))
            {
                ImGui.SetClipboardText(_chosen);
            }
        }
        finally
        {
            OverlayFonts.PopMono();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("The path an entity carries - the table's own key. Click to copy.");
        }

        if (one.Boss)
        {
            ImGui.TextColored(OverlayInk.Name, "boss");
        }

        Pair("blood", Named(all.BloodName(one), one.Blood));

        if (one.Base is { Length: > 0 } built)
        {
            Pair("base", built);
        }

        // A ROW NUMBER WITH NO NAME HERE, on purpose. The column holds a QuestFlags row and this
        // table carries no copy of that one - the flag's own state is read from the game at
        // runtime, by QuestWatch - so naming it from here would mean inventing the name. What it
        // does say is worth showing: the 68 monsters that carry one are the campaign bosses.
        // GREATER THAN ZERO, not merely non-zero: the install route spells "no quest flag" as -1,
        // because zero is a row of QuestFlags like any other. The export spells it as zero.
        if (one.Quest > 0)
        {
            Pair("quest flag", $"row {one.Quest.ToString(CultureInfo.InvariantCulture)}");
        }
    }

    /// <summary>
    /// The plain figures, labelled only where the table settles what they mean.
    /// </summary>
    /// <remarks>
    /// See the class remarks: the percentages are percentages because 2733 rows say so, and the
    /// rest stay bare because nothing here can say what they are. Crit is shown as the kind it is.
    /// </remarks>
    private static void Figures(MonsterVariety one)
    {
        if (!OverlayLayout.Subsection("Figures", openByDefault: true))
        {
            return;
        }

        ImGui.Indent();
        try
        {
            Pair("life", Percent(one.Life));
            Pair("damage", Percent(one.Damage));
            Pair("experience", Percent(one.Xp));
            Pair("model size", Percent(one.ModelSize));

            // NO UNIT, and that is the whole point of these four sitting apart from the four
            // above. Nothing measured says what they are counted in.
            Pair("attack speed", Count(one.AttackSpeed));
            Pair("movement speed", Count(one.Speed));
            Pair("size", Count(one.Size));
            Pair("poise", one.Poise.ToString("0.##", CultureInfo.InvariantCulture));
            Pair("attack range", $"{Count(one.MinAttack)} - {Count(one.MaxAttack)}");
            Pair("aggro range", $"{Count(one.MinAggro)} - {Count(one.MaxAggro)}");

            // NOT A PERCENTAGE AND NOT A CHANCE. AttackCrit holds 0, 1 or 2 across the whole
            // table - it names a kind - so it is shown as the number it is, with no unit
            // invented for it and no label that would read as one.
            Pair("crit kind", Count(one.Crit));

            if (one.Stance is { Length: > 0 } stance)
            {
                Pair("stance", stance);
            }
        }
        finally
        {
            ImGui.Unindent();
        }
    }

    private static void Type(MonsterVarieties all, MonsterVariety one)
    {
        MonsterKind? kind = all.Kind(one);
        string called = kind?.Id is { Length: > 0 } named ? named : Row(one.Type);

        if (!OverlayLayout.Subsection($"Type - {called}", openByDefault: true))
        {
            return;
        }

        ImGui.Indent();
        try
        {
            if (kind is null)
            {
                ImGui.TextDisabled("This type row is not in the exported table.");
                return;
            }

            // THIS IS WHERE ARMOUR ACTUALLY LIVES - MonsterVarieties' own remark. The monster's
            // MonsterArmour column is filled on 16 rows of 2734; the real values are one join
            // away, on the type.
            Pair("armour", Percent(kind.Armour));
            Pair("evasion", Percent(kind.Evasion));
            Pair("energy shield", $"{Percent(kind.EnergyShield)} of life");
            Pair("damage spread", Percent(kind.Spread));
            Pair("summoned", kind.Summoned ? "yes" : "no");

            string[] resists = [.. all.ResistancesOf(one)];
            if (resists.Length > 0)
            {
                // PROFILE NAMES AND NOT PERCENTAGES: each profile holds 32 numeric columns of
                // tiers that the export does not settle the meaning of, so the set is named
                // rather than valued. MonsterKind says so at length.
                Pair("resistances", string.Join(", ", resists));
            }
        }
        finally
        {
            ImGui.Unindent();
        }
    }

    private static void Words(string what, IEnumerable<string> said)
    {
        string[] all = [.. said];
        if (all.Length == 0)
        {
            return;
        }

        if (!OverlayLayout.Subsection($"{what} ({all.Length.ToString(CultureInfo.InvariantCulture)})"))
        {
            return;
        }

        ImGui.Indent();
        try
        {
            foreach (string one in all)
            {
                ImGui.BulletText(ImGuiText.Escape(one));
            }
        }
        finally
        {
            ImGui.Unindent();
        }
    }

    /// <summary>
    /// Every modifier the monster carries, named where it can be and numbered where it cannot.
    /// </summary>
    /// <remarks>
    /// WALKED ROW BY ROW rather than through <see cref="MonsterVarieties.Modifiers"/>, which
    /// yields only the rows that resolved. Against the shipped table that is a distinction with
    /// no difference - all 2530 modifier rows on all 2733 monsters resolve, and the same holds
    /// for the skills, the tags, the types and the blood. It is the NEXT table this is for: the
    /// export is about to be replaced by a read of the install's own files, where a table can
    /// simply be missing, and a book that then showed four of a monster's seven modifiers would
    /// look complete and be wrong. The seven is what somebody comes here to count.
    /// </remarks>
    private static void Mods(MonsterVarieties all, MonsterVariety one, StatDescriptions said)
    {
        int[] rows = [.. Rows(one)];
        if (rows.Length == 0)
        {
            return;
        }

        if (!OverlayLayout.Subsection($"Modifiers ({rows.Length.ToString(CultureInfo.InvariantCulture)})"))
        {
            return;
        }

        ImGui.Indent();
        try
        {
            foreach (int row in rows)
            {
                if (all.Modifier(row) is not { } mod)
                {
                    ImGui.BulletText(Row(row));
                    ImGui.SameLine();
                    ImGui.TextDisabled("(not in the exported table)");
                    continue;
                }

                ImGui.BulletText(ImGuiText.Escape(mod.Id));

                ImGui.Indent();
                try
                {
                    foreach (ModifierStat stat in mod.Stats ?? [])
                    {
                        Stat(stat, said);
                    }
                }
                finally
                {
                    ImGui.Unindent();
                }
            }
        }
        finally
        {
            ImGui.Unindent();
        }
    }

    /// <summary>
    /// One stat a modifier sets: the sentence the game words it with, or the raw fact.
    /// </summary>
    /// <remarks>
    /// THE RAW FACT IS NEVER LOST, it moves to the tooltip. A sentence is what somebody reads and
    /// the id is what they search for, cite and check an export against - "30  monster_base_block_%"
    /// is the thing that can be looked up, and dropping it the moment a wording exists would make
    /// this book less useful to the person most likely to open it.
    ///
    /// MONO AND TextUnformatted FOR THE RAW LINE, and that is not a style choice: 207 of these stat
    /// ids carry a PERCENT SIGN - maim_on_hit_%, monster_damage_+%_final_vs_monsters - and ImGui's
    /// Text calls are printf. Drawn with one of those, "%_f" is a conversion that reads an argument
    /// nobody passed and eats the characters behind it. See ImGuiText.
    /// </remarks>
    private static void Stat(ModifierStat stat, StatDescriptions said)
    {
        string raw = $"{stat.Range,10}  {stat.Stat}";

        if (stat.Worded(said) is not { Length: > 0 } sentence)
        {
            ImGuiText.Mono(raw);
            return;
        }

        ImGui.TextUnformatted(sentence);
        if (ImGui.IsItemHovered())
        {
            ImGuiText.MonoTooltip(raw);
        }
    }

    /// <summary>The three modifier columns as one run of rows, in the order the table holds them.</summary>
    private static IEnumerable<int> Rows(MonsterVariety one)
        => (one.Mods ?? []).Concat(one.Mods2 ?? []).Concat(one.SpecialMods ?? []);

    private static void Pair(string what, string said)
    {
        if (said.Length == 0)
        {
            return;
        }

        ImGui.TextDisabled(what);
        OverlayLayout.ToColumn();
        ImGui.TextUnformatted(said);
    }

    /// <summary>A name with its row number kept beside it, or the bare row when there is no name.</summary>
    /// <remarks>
    /// EVERY FIELD HERE THAT IS A ROW NUMBER STAYS ONE - MonsterVarieties' own rule, and the
    /// reason is that the number is what a capture, a dump or the next export can be checked
    /// against. The name is the part that is easy to lose and easy to re-derive; the number is
    /// the part that is not.
    /// </remarks>
    private static string Named(string name, int row)
        => name.Length > 0
            ? $"{name}  ({Row(row)})"
            : Row(row);

    private static string Row(int row)
        => "#" + row.ToString(CultureInfo.InvariantCulture);

    private static string Percent(int value)
        => value.ToString(CultureInfo.InvariantCulture) + "%";

    private static string Count(int value)
        => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The last part of a metadata path, for the many monsters the game never names.</summary>
    private static string Tail(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash >= 0 && slash + 1 < path.Length ? path[(slash + 1)..] : path;
    }
}
