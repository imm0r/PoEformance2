using System.Globalization;
using System.Numerics;
using System.Runtime.Versioning;
using System.Text;
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
/// THE TEXT EVERY ROW IS SEARCHED BY IS BUILT ONCE PER TABLE, not once per keystroke. Joining
/// tags, skills and modifier ids for 2733 monsters is real work; done per frame it is work behind
/// a search box, which is where a tool starts to feel slow. It is built when the table arrives,
/// filtered when the text changes, and re-sorted only when ImGui says its columns are dirty.
///
/// WHAT THIS WINDOW CANNOT DO, and what would replace it: six columns of a table holding some two
/// hundred thousand facts answer "what is this one" and nothing of the form "which of these".
/// docs/reading-big-tables.md is the design for the viewer that does - and for why the row cap
/// below, the per-frame formatting and the substring search all go with it.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class MonsterBookWindow(Func<MonsterVarieties> table, Func<StatDescriptions> sentences)
{
    /// <summary>How long a search string may be.</summary>
    private const uint SearchLength = 96;

    /// <summary>
    /// How many rows are drawn at once.
    /// </summary>
    /// <remarks>
    /// CAPPED rather than clipped with ImGuiListClipper, the same call AtlasLogWindow and
    /// IconPicker made: these bindings reach the clipper through a raw pointer and a matching
    /// Destroy, which is a lifetime to get right in code that cannot be run on this machine.
    /// Fifteen hundred rows of a filtered list is already more than anybody scrolls, and the
    /// search is the real navigation here.
    /// </remarks>
    private const int MostRows = 1500;

    private readonly PaneSplit _split = new(0.46f);

    /// <summary>The table the book was built from, to notice when a different one arrives.</summary>
    private MonsterVarieties _of = MonsterVarieties.Empty;

    /// <summary>And the sentences it was built with, which arrive later than the table does.</summary>
    private StatDescriptions _said = StatDescriptions.Empty;

    private List<Entry> _book = [];
    private List<Entry> _shown = [];
    private string _search = string.Empty;
    private string _chosen = string.Empty;

    /// <summary>The filter has to run again - the table changed, or somebody typed.</summary>
    private bool _refilter = true;

    /// <summary>The list has to be put back in order - it was just rebuilt.</summary>
    private bool _resort = true;

    /// <summary>One monster as the list shows it, with everything it is searched by worked out.</summary>
    private readonly record struct Entry(
        string Path,
        MonsterVariety One,
        string Name,
        string Kind,
        int Skills,
        int Mods,
        string Find);

    /// <summary>Draws the tab.</summary>
    public void DrawTab()
    {
        MonsterVarieties all = table();
        StatDescriptions said = sentences();
        Read(all, said);

        if (_book.Count == 0)
        {
            ImGui.TextDisabled("No monster table loaded - see data/monster-varieties.json.");
            return;
        }

        Header(all, said);
        Filter();

        float left = _split.Left();
        if (ImGui.BeginChild("##monster-list", new Vector2(left, 0f), ImGuiChildFlags.Borders))
        {
            List();
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
    /// Turns the table into rows, once.
    /// </summary>
    /// <remarks>
    /// COMPARED BY REFERENCE and not by count: the table is loaded at start-up today and will be
    /// read from the install later, and two different tables can perfectly well hold the same
    /// number of monsters. A count would keep showing the old one.
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

        var book = new List<Entry>(all.Count);
        var text = new StringBuilder(512);

        foreach ((string path, MonsterVariety one) in all.All)
        {
            string name = one.Name is { Length: > 0 } named ? named : Tail(path);
            MonsterKind? kind = all.Kind(one);
            string type = kind?.Id is { Length: > 0 } called ? called : Row(one.Type);

            text.Clear();
            text.Append(path).Append(' ').Append(name).Append(' ').Append(type);

            // SEARCHED ACROSS EVERYTHING RESOLVED, not only the name: somebody looking up which
            // monsters cast a skill, or carry a tag, is asking the question this book exists for.
            Add(text, all.TagsOf(one));
            Add(text, all.Skills(one));
            Add(text, all.ResistancesOf(one));
            text.Append(' ').Append(all.BloodName(one));

            int mods = 0;
            foreach (int row in Rows(one))
            {
                mods++;
                if (all.Modifier(row) is not { } mod)
                {
                    text.Append(' ').Append(Row(row));
                    continue;
                }

                text.Append(' ').Append(mod.Id);

                // AND WHAT THE GAME SAYS THE MODIFIER DOES, not only what it is called. Nobody
                // searches for "MonsterAttackBlock30Bypass15"; they search for "block", and that
                // word is in the sentence rather than in the id.
                foreach (ModifierStat stat in mod.Stats ?? [])
                {
                    text.Append(' ').Append(stat.Worded(said) ?? stat.Stat);
                }
            }

            // The words the table says with a FLAG rather than with a column, so that typing
            // "boss" finds the bosses. There is no other way to ask this list for them.
            if (one.Boss)
            {
                text.Append(" boss");
            }

            if (kind is { Summoned: true })
            {
                text.Append(" summoned");
            }

            book.Add(new Entry(
                path, one, name, type, one.SkillCount, mods, text.ToString().ToLowerInvariant()));
        }

        // BY NAME BEFORE ANYBODY HAS CLICKED A HEADER. The dictionary hands these out in its own
        // order, and ImGui has no sort specs to offer until the header row has been drawn once -
        // so without this the first frame of a freshly loaded table is 2733 monsters in hash
        // order, which reads as a broken list rather than as an unsorted one.
        book.Sort(static (left, right) =>
            string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase) is var said and not 0
                ? said
                : string.CompareOrdinal(left.Path, right.Path));

        _book = book;

        // Filtered here rather than on the next frame, so the count in the header is the count
        // of what is in the list underneath it from the very first frame.
        _refilter = true;
        Filter();
    }

    private static void Add(StringBuilder text, IEnumerable<string> said)
    {
        foreach (string one in said)
        {
            text.Append(' ').Append(one);
        }
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
            // worked out, and pasting it into a conversation about it is what this is for.
            ImGui.SetClipboardText(string.Join(
                '\n', _shown.Select(row => $"{row.Path}\t{row.Name}\t{row.Kind}")));
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

        string looking = _search.Trim().ToLowerInvariant();
        var shown = new List<Entry>(_book.Count);

        foreach (Entry row in _book)
        {
            if (looking.Length == 0 || row.Find.Contains(looking, StringComparison.Ordinal))
            {
                shown.Add(row);
            }
        }

        _shown = shown;
        _resort = true;
    }

    private void List()
    {
        if (!ImGui.BeginTable(
                "##monsters",
                6,
                ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY
                    | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable
                    | ImGuiTableFlags.Sortable))
        {
            return;
        }

        try
        {
            ImGui.TableSetupColumn(
                "name", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultSort);
            ImGui.TableSetupColumn("type", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("life");
            ImGui.TableSetupColumn("dmg");
            ImGui.TableSetupColumn("skills");
            ImGui.TableSetupColumn("mods");
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            Sort();

            foreach (Entry row in _shown.Take(MostRows))
            {
                Draw(row);
            }
        }
        finally
        {
            // In a finally and unconditionally: EndTable pairs with BeginTable whatever it
            // returned, and an exception between the two leaves ImGui's stack unbalanced.
            ImGui.EndTable();
        }

        if (_shown.Count > MostRows)
        {
            string more = (_shown.Count - MostRows).ToString(CultureInfo.InvariantCulture);
            ImGui.TextColored(OverlayInk.Warn, $"...and {more} more - narrow the search");
        }
    }

    private void Draw(Entry row)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();

        // ###path rather than a bare label: two monsters may well share a name - the table has
        // nineteen called "Skeletal Warrior" - and an id built from the label would make them
        // one selectable as far as ImGui is concerned.
        if (row.One.Boss)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, OverlayInk.Name);
        }

        if (ImGui.Selectable(
                $"{row.Name}###monster-{row.Path}",
                string.Equals(row.Path, _chosen, StringComparison.Ordinal),
                ImGuiSelectableFlags.SpanAllColumns))
        {
            _chosen = row.Path;
        }

        if (row.One.Boss)
        {
            ImGui.PopStyleColor();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(ImGuiText.Escape(row.Path));
        }

        ImGui.TableNextColumn();
        ImGui.TextDisabled(ImGuiText.Escape(row.Kind));
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(Percent(row.One.Life));
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(Percent(row.One.Damage));
        ImGui.TableNextColumn();
        ImGui.TextDisabled(row.Skills.ToString(CultureInfo.InvariantCulture));
        ImGui.TableNextColumn();
        ImGui.TextDisabled(row.Mods.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Re-orders the shown rows when a column header has been clicked.
    /// </summary>
    /// <remarks>
    /// ONLY WHEN ImGui SAYS SO, or when the filter has just produced a new list. Sorting a list
    /// that is already sorted, sixty times a second, is the kind of cost that never shows up in
    /// a profile as one thing and is free to avoid.
    /// </remarks>
    private unsafe void Sort()
    {
        // ImGui hands back a null pointer until the header row has been drawn and somebody has
        // chosen a column, so the wrapper cannot be trusted without checking the pointer it wraps.
        ImGuiTableSortSpecsPtr specs = ImGui.TableGetSortSpecs();
        if (specs.NativePtr == null || specs.SpecsCount == 0)
        {
            return;
        }

        if (!specs.SpecsDirty && !_resort)
        {
            return;
        }

        specs.SpecsDirty = false;
        _resort = false;

        ImGuiTableColumnSortSpecsPtr by = specs.Specs;
        bool up = by.SortDirection == ImGuiSortDirection.Ascending;
        int column = by.ColumnIndex;

        _shown.Sort((left, right) =>
        {
            int said = column switch
            {
                1 => string.Compare(left.Kind, right.Kind, StringComparison.OrdinalIgnoreCase),
                2 => left.One.Life.CompareTo(right.One.Life),
                3 => left.One.Damage.CompareTo(right.One.Damage),
                4 => left.Skills.CompareTo(right.Skills),
                5 => left.Mods.CompareTo(right.Mods),
                _ => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase),
            };

            // Ties break on the path, which is unique - so a sort by type puts each type's
            // monsters in one fixed order rather than in whatever order the comparison left them.
            return (up ? said : -said) is var order and not 0
                ? order
                : string.CompareOrdinal(left.Path, right.Path);
        });
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
