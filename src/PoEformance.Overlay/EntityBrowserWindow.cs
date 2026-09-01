using System.Globalization;
using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Core.Schema;
using PoEformance.Features;
using PoEformance.Game.Entities;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Overlay;

/// <summary>
/// Takes an entity apart, and shows what nothing in the tool understands yet.
/// </summary>
/// <remarks>
/// The shortest route to something new that this project has, and it needs no reverse
/// engineering to reach. The game names every component an entity carries and the reader
/// already lists them; about twenty have a decoder. The rest have been there the whole time -
/// named, addressed, and unread.
///
/// So the useful column is not the one saying what is understood. It is the one saying what
/// is not, and from there a click opens the thing in the dissector.
///
/// The SURVEY answers the question that cannot be asked one entity at a time: what exists in
/// this area that nothing understands? A component two entities carry is a much better lead
/// than one everything has, and rarity like that is invisible until they are counted together.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class EntityBrowserWindow
{
    private static readonly Vector4 DimText = OverlayInk.Quiet;
    private static readonly Vector4 UnknownText = OverlayInk.Warn;
    private static readonly Vector4 KnownText = OverlayInk.Reference;
    private static readonly Vector4 PathText = OverlayInk.Name;

    private readonly EntityInspector _inspector;
    private readonly EntityHiding _hiding;
    private readonly Action<ulong, string, string> _dissect;
    private readonly Action<ulong, string>? _compare;
    private readonly Action<ulong, float, float>? _route;
    private readonly Func<ulong, bool>? _routed;
    private readonly Func<MonsterVarieties>? _monsters;

    /// <summary>Components unfolded to their fields, by name, kept across selections.</summary>
    /// <remarks>
    /// By NAME rather than by address, on purpose: unfolding Life and then clicking through
    /// six monsters should keep showing Life. Addresses change with every selection, and
    /// keying on them would fold everything shut on each click.
    /// </remarks>
    private readonly HashSet<string> _open = new(StringComparer.Ordinal);

    private ulong _selected;
    private string _filter = string.Empty;

    /// <summary>Where the list ends and the components begin. Draggable; 0.4 was the old 360px.</summary>
    private readonly PaneSplit _split = new(0.4f);
    private int _surveySequence;
    private bool _surveyPane;

    /// <param name="dissect">
    /// Opens an address in the dissector: where it is, what to call it, and a schema layout
    /// when one applies. A callback rather than the window itself - "show me this" is all
    /// either side needs to know about the other.
    /// </param>
    /// <param name="route">
    /// Asks for a walkable way to something, given where it stands. Optional, because the
    /// browser is useful without a map open and must not require one.
    /// </param>
    /// <param name="routed">Whether a route to this address is already being drawn.</param>
    /// <param name="compare">
    /// Pins an address BESIDE whatever the dissector already has open, rather than replacing
    /// it. This is where a comparison comes from: two monsters of a kind, or the same
    /// component off two entities, picked out of a list that already knows where they are.
    /// Nobody assembles one by copying addresses out of here by hand.
    /// </param>
    /// <param name="hiding">What the list has been told to leave out. See <see cref="EntityHiding"/>.</param>
    /// <param name="monsters">
    /// The game's monster table, asked for each frame rather than held, so a table wired up
    /// after this window was built is still found. Optional: without it the browser shows what
    /// it always showed.
    /// </param>
    public EntityBrowserWindow(
        EntityInspector inspector,
        EntityHiding hiding,
        Action<ulong, string, string> dissect,
        Action<ulong, float, float>? route = null,
        Func<ulong, bool>? routed = null,
        Action<ulong, string>? compare = null,
        Func<MonsterVarieties>? monsters = null)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        ArgumentNullException.ThrowIfNull(hiding);
        ArgumentNullException.ThrowIfNull(dissect);
        _inspector = inspector;
        _hiding = hiding;
        _dissect = dissect;
        _route = route;
        _routed = routed;
        _compare = compare;
        _monsters = monsters;
    }

    /// <summary>Draws the tab's content and publishes what it wants read next.</summary>
    /// <param name="snapshot">
    /// The frame's entities. The LIST comes from here at no cost - it is already read and
    /// drawn - and only the selected entity is taken apart.
    /// </param>
    public void DrawTab(WorldSnapshot snapshot, WorldEntity? player)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        EntityView view = _inspector.View;
        List<WorldEntity> listed = Listed(snapshot, player);

        DrawControls(view, snapshot);
        ImGui.Separator();

        if (_surveyPane)
        {
            DrawSurvey(view);
        }
        else
        {
            DrawList(listed, player);
            _split.Bar();

            // From the whole snapshot rather than from the listed rows: hiding the selected
            // entity takes its row away, and the pane must still show what it is showing -
            // including the button that puts it back.
            DrawComponents(view, snapshot.Entities.FirstOrDefault(entity => entity.Address == _selected));
        }

        // Listed, not Entities: the survey walks every address's component table, and a
        // remembered entity's address belonged to an object the game may since have released.
        // Reading one would count components out of whatever now occupies that memory, which
        // is worse than missing it - a survey exists to be believed.
        _inspector.Request(new EntityRequest(
            Enabled: true,
            Address: _selected,
            Survey: [.. snapshot.Listed.Select(entity => entity.Address)],
            SurveySequence: _surveySequence,
            Expand: [.. _open]));
    }

    /// <summary>While the tab is not in front, nothing is read for it.</summary>
    public void Idle() => _inspector.Request(EntityRequest.Idle);

    private void DrawControls(EntityView view, WorldSnapshot snapshot)
    {
        // The label moves inside the box - "filter" beside a field is a word explaining a field
        // that explains itself the moment it says "filter by name" in its own grey text.
        //
        // Room reserved for BOTH buttons whether or not the second is drawn, and for the longer
        // of the two labels the first one wears - so the box is one width. Reserved for what is
        // on screen instead, it grew and shrank every time the survey was opened, which moves
        // the thing being typed into out from under the cursor that just clicked beside it.
        string survey = _surveyPane ? "Back to Entities" : "Survey This Area";
        OverlayLayout.Search(
            "##filter", "filter by name...", ref _filter, 64,
            OverlayLayout.ButtonRoom("Survey This Area", "Count Again"));

        ImGui.SameLine();
        if (ImGui.Button(survey))
        {
            _surveyPane = !_surveyPane;
            if (_surveyPane)
            {
                _surveySequence++;
            }
        }

        OverlayLayout.Hint(
            $"Counts every component across the {snapshot.Entities.Count - snapshot.Remembered}"
            + " entities here. The rare undescribed ones are the leads worth following.");

        if (_surveyPane)
        {
            ImGui.SameLine();
            if (ImGui.Button("Count Again"))
            {
                _surveySequence++;
            }
        }

        DrawFacts(view, snapshot);
        DrawHidden();
    }

    /// <summary>
    /// What the read is doing, on ONE line, with the sentences on hover.
    /// </summary>
    /// <remarks>
    /// FIVE PARAGRAPHS USED TO STAND HERE, between the filter and the panes, on the one tool in
    /// this window that is nothing but height: the read's status, a duplicate check, a corpse
    /// count, a note about dropped repeats, and a sentence saying what the number on each row
    /// means. Every one of them is worth having and not one is worth a permanent line of the
    /// list - which is what they cost, on every frame, whether or not anybody was reading them.
    /// The panes started six lines down the tab.
    ///
    /// A FACT IS SHORT; THE SENTENCE ABOUT IT IS WHAT WAS LONG. So the fact stays on screen and
    /// the sentence moves to a hover, which is where an explanation read once belongs. Nothing
    /// is dropped and nothing goes behind a click.
    ///
    /// What is still said in full is what changes the meaning of the list: a repeat the
    /// collapsing missed, and corpses that could not be read. Those are findings, and a finding
    /// somebody has to hover to see is a finding nobody sees.
    /// </remarks>
    private static void DrawFacts(EntityView view, WorldSnapshot snapshot)
    {
        ImGui.TextColored(DimText, ImGuiText.Escape(view.Status));

        // WHY THIS IS HERE AND NOT BEHIND A BUTTON: it answers "am I looking at twelve
        // monsters or at four of them three times", and that question arrives while looking
        // at the list, not before. It costs one pass over the snapshot's monsters on the
        // frames this tab is in front, which is nothing beside the tree the pane draws.
        //
        // It runs on the list AFTER the reader has collapsed repeats, so it should now find
        // nothing - and that is the point of leaving it in. It is the check on the collapsing
        // rather than a leftover: anything it still reports is a repeat the position key does
        // not catch, which is exactly what nobody would notice otherwise. So a FINDING is
        // spelled out where it cannot be missed, and a clean check is two words with the counts
        // it was made from one hover away.
        EntityDuplicates duplicates = EntityDuplicates.Of([.. snapshot.Listed]);
        Fact(
            duplicates.Any ? WarnText : DimText,
            duplicates.Any ? duplicates.Describe() : "no repeats");
        OverlayLayout.Hint(duplicates.Describe());

        if (snapshot.Collapsed > 0)
        {
            Fact(DimText, $"{snapshot.Collapsed} repeats dropped");

            // Says the MECHANISM, not the symptom. "On the same spot" was true but described
            // the position key this rule no longer uses, and a line that documents a rule
            // nobody applies any more is worse than no line: it is what the next person
            // reads instead of the code.
            OverlayLayout.Hint(
                "Dropped by this read: the game gives one monster several entities over one set"
                + " of components.");
        }

        // What the corpse check saw. Here because dots left on cleared ground are noticed
        // while looking at this list, and because the three ways it can fail look identical
        // on the map and want completely different fixes - see CorpseSigns.
        if (snapshot.Corpses.Seen > 0)
        {
            Fact(
                snapshot.Corpses.Unreadable > 0 ? WarnText : DimText,
                "corpses: " + snapshot.Corpses.Describe());
        }

        // What the bare number on each row is. It was read as an id, a count and an index
        // before somebody asked - which is what an unlabelled column earns. Said once, above
        // the list, rather than as a unit on every row: the list is read by scanning it, and
        // this change took the file names off the rows for exactly that reason.
        //
        // Only where there IS a player to measure from, because that is the only time the
        // number is on the rows at all.
        if (snapshot.Player is not null)
        {
            Fact(DimText, "nearest first");
            OverlayLayout.Hint("The number on each row is how far away it is, in grid squares.");
        }
    }

    /// <summary>One more fact on the line - <see cref="OverlayLayout.Fact"/>, under this name.</summary>
    /// <remarks>
    /// Written here first and lifted once the wealth page wanted the same line. Kept as a name
    /// because the calls below read as what they are.
    /// </remarks>
    private static void Fact(Vector4 ink, string text) => OverlayLayout.Fact(ink, text);

    private static readonly Vector4 WarnText = OverlayInk.Warn;

    /// <summary>The two ways to get a row out of the list, on the entity it is about.</summary>
    /// <remarks>
    /// HERE rather than on the row itself, which is where they were first drawn in the head:
    /// a list of hundreds of rows, each carrying two buttons, is a wall of buttons - and one
    /// of them is next to whatever the mouse is passing over on the way somewhere else. On
    /// the pane they are two buttons in total, they name what they will do, and reaching them
    /// takes the deliberate click that picking the entity already is.
    /// </remarks>
    private void DrawHideButtons(EntityView view, WorldEntity? chosen)
    {
        if (ImGui.SmallButton("Hide This Kind"))
        {
            _hiding.HideKind(view.Path);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Leaves every entity with this metadata path out of the list, in every area\n"
                + "and from now on. Undone under \"hidden\" below.");
        }

        // Only where the place is known. A remembered entity is a recording of somewhere it
        // no longer is, and hiding "the one standing there" on that would name a spot that
        // was true a minute ago.
        if (chosen is { IsRemembered: false } entity)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Hide Just This One"))
            {
                _hiding.HideOne(entity.Path, entity.WorldX, entity.WorldY);
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "Leaves out this one entity - its kind, on the spot it stands on. Kept by\n"
                    + "PLACE rather than by id, because the game hands ids out afresh per area:\n"
                    + "scenery stays hidden across sessions, and anything that walks away comes\n"
                    + "back to the list.");
            }
        }
    }

    /// <summary>What is being left out, and how to undo any of it.</summary>
    /// <remarks>
    /// A list that hides things without saying so is a list that lies. It sits behind a fold
    /// because on most days it is one line of count and nothing else, and it stays out of the
    /// pane so that undoing something does not need the entity that is no longer listed.
    /// </remarks>
    private void DrawHidden()
    {
        if (!_hiding.Any)
        {
            return;
        }

        if (!OverlayLayout.Subsection($"Hidden ({_hiding.Count})###hidden"))
        {
            return;
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Show Everything Again"))
        {
            _hiding.ShowEverything();
            return;
        }

        foreach (string kind in _hiding.Kinds)
        {
            ImGui.PushID(kind);
            try
            {
                if (ImGui.SmallButton("x"))
                {
                    _hiding.ShowKind(kind);

                    // The list just changed under the loop, so this frame stops drawing it.
                    // The finally pops the id - popping it here as well would unbalance the
                    // stack, which is the bug the quest rows once had in the other direction.
                    return;
                }
            }
            finally
            {
                ImGui.PopID();
            }

            ImGui.SameLine();
            ImGui.TextColored(PathText, ImGuiText.Escape(kind));
            ImGui.SameLine();
            ImGui.TextColored(DimText, "every one");
        }

        foreach (EntitySpot spot in _hiding.Spots)
        {
            ImGui.PushID($"{spot.Path}@{spot.X},{spot.Y}");
            try
            {
                if (ImGui.SmallButton("x"))
                {
                    _hiding.ShowOne(spot);
                    return;
                }
            }
            finally
            {
                ImGui.PopID();
            }

            ImGui.SameLine();
            ImGui.TextColored(DimText, ImGuiText.Escape(spot.Describe()));
        }
    }

    /// <summary>
    /// The reads that only some entities carry, written out where they can be watched.
    /// </summary>
    /// <remarks>
    /// An empty string for anything that has none, rather than a placeholder - a list where
    /// most rows carry "-  -  -" is harder to scan than one where the facts stand out.
    /// </remarks>
    private static string Facts(WorldEntity entity)
    {
        string life = entity.Life.IsValid
            ? $"  {entity.Life.Current}/{entity.Life.Max}"
            : string.Empty;

        string shield = entity.EnergyShield.IsValid && entity.EnergyShield.Max > 0
            ? $"  es {entity.EnergyShield.Current}/{entity.EnergyShield.Max}"
            : string.Empty;

        string chest = entity.Opened switch
        {
            true => "  opened",
            false => "  closed",
            _ => string.Empty,
        };

        // Said out loud, because this row's numbers are a RECORDING and its address no longer
        // points at anything: clicking it draws a route to where the thing was, which is
        // useful, while taking it apart in the dissector reads whatever now sits at that
        // address. Nothing else on the row would give that away.
        string remembered = entity.RememberedForMs is int since
            ? $"  remembered {since / 1000}s"
            : string.Empty;

        return life + shield + chest + remembered;
    }

    private void DrawList(List<WorldEntity> listed, WorldEntity? player)
    {
        // BeginChild is paired with EndChild whatever it returns, and the finally is there
        // for the same reason the window's is.
        ImGui.BeginChild("entities", new Vector2(_split.Left(), 0f), ImGuiChildFlags.Borders);

        try
        {
            foreach (WorldEntity entity in listed)
            {
                string away = player is null
                    ? string.Empty
                    : $"  {Distance(entity, player) / MapView.WorldToGrid:F0}";

                // What the newest reads say about this entity, where they say anything. This
                // is how they get CHECKED against the game: stand next to a monster and watch
                // the pool move as you hit it, open a chest and watch the flag turn over.
                // Without somewhere to see them, a read that quietly returns nonsense looks
                // exactly like a read that works.
                string facts = Facts(entity);

                // The name it SHOWS, and the file name only where it has no other. Both used to
                // ride every row - "Doryani (DoryaniEndgameTownAtlasTalk)" - which answered
                // "does this one carry a name at all" at the cost of doubling the width of a
                // list that is read by scanning it. The pane already prints the whole path for
                // whatever is selected, so the answer is one click away rather than gone.
                //
                // The FILTER still matches the path, which is what keeps that click reachable:
                // typing "Karui" finds the Well whose row now says only "Well".
                string called = entity.Name.Length > 0 ? entity.Name : entity.FileName;

                // The family where the path names one - "Hideout Object", "Sanctum Object" -
                // because two things that are both "Object" are told apart by the folder the
                // game files them under, and reading it here saves opening the pane to see it.
                string kind = WorldReader.DescribeKind(entity.Kind, entity.Path);

                // ###address, not ##: the label carries a live distance, and ImGui derives a
                // control's identity from its label - so without this the row would be a new
                // control every frame and the click would never land.
                if (ImGui.Selectable($"{kind}  {called}{away}{facts}###{entity.Address:X}",
                        entity.Address == _selected))
                {
                    _selected = entity.Address;
                    RouteTo(entity);
                }
            }
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    /// <summary>Draws the way to whatever was just picked.</summary>
    /// <remarks>
    /// Picking a thing and being shown how to reach it is one action, not two - the browser
    /// is where you find the entity that matters and the map is where you have to walk to it.
    ///
    /// ADDS, never toggles: selecting the same row twice would otherwise take the line away,
    /// and re-picking a row is what somebody does to confirm they picked the right one. The
    /// planner drops its oldest when full, so clicking down a list always draws the newest.
    ///
    /// The destination is where the thing stood when it was picked. For a chest or a portal
    /// that is the whole answer; for a monster on the move it goes stale, and the alternative
    /// - a route re-planned as it walks - costs a search per step.
    /// </remarks>
    private void RouteTo(WorldEntity entity)
    {
        if (_route is null || _routed?.Invoke(entity.Address) == true)
        {
            return;
        }

        _route(entity.Address, entity.WorldX, entity.WorldY);
    }

    /// <summary>
    /// What the game's own table says about the thing selected.
    /// </summary>
    /// <remarks>
    /// ABOVE THE COMPONENTS, because it answers a different question than they do. A component
    /// says what this ONE monster is doing right now; the table says what its KIND is - and that
    /// half was never readable here at all. Until now a monster was a path and a rarity, so
    /// "Metadata/Monsters/Zombies/Farmer/FarmerZombieMedium" was the whole of what the browser
    /// could say about it. It is a Risen Farmhand with one skill and 120% damage.
    ///
    /// Nothing is drawn for an entity the table has never heard of - a chest, an effect, a
    /// waypoint - so this costs a non-monster exactly one dictionary miss.
    /// </remarks>
    /// <summary>
    /// The tags worth putting on the first line, out of the 185 in use.
    /// </summary>
    /// <remarks>
    /// The same six GameHelper2 derives its MonsterCategory from, and for the same reason: they
    /// say what a thing IS. A monster may carry several - a werewolf is humanoid and beast at
    /// once - which is why this is a filter over the list rather than a lookup for one value.
    /// </remarks>
    private static readonly string[] Kinds =
        ["humanoid", "human", "undead", "construct", "beast", "demon", "eldritch", "golem"];

    private void DrawTable(string path)
    {
        MonsterVarieties table = _monsters?.Invoke() ?? MonsterVarieties.Empty;
        MonsterVariety? one = table.Find(path);
        if (one is null)
        {
            return;
        }

        if (one.Name is { Length: > 0 })
        {
            ImGuiText.Mono(KnownText, one.Boss ? $"{one.Name}  (boss)" : one.Name);
        }
        else if (one.Boss)
        {
            ImGuiText.Mono(KnownText, "(boss)");
        }

        // WHAT KIND OF THING IT IS, from the tags. These six are the ones GameHelper2 reads for
        // the same purpose, and they are the half of the tag list somebody actually wants at a
        // glance - the rest is movement speed, on-hit audio and attribute exclusions.
        string[] kinds =
        [
            .. table.TagsOf(one).Where(tag => Kinds.Contains(tag, StringComparer.Ordinal)),
        ];

        if (kinds.Length > 0)
        {
            ImGuiText.Mono(DimText, string.Join(", ", kinds));
        }

        // ARMOUR AND EVASION LIVE ON THE TYPE, not on the monster. The row's own MonsterArmour
        // column is filled on 16 of 2734 rows, so reading that one and concluding the game does
        // not store monster armour would be wrong twice over.
        MonsterKind? kind = table.Kind(one);
        if (kind is not null && (kind.Armour > 0 || kind.Evasion > 0 || kind.EnergyShield > 0))
        {
            var defence = new System.Text.StringBuilder();
            if (kind.Armour > 0)
            {
                defence.Append($"armour {kind.Armour}  ");
            }

            if (kind.Evasion > 0)
            {
                defence.Append($"evasion {kind.Evasion}  ");
            }

            if (kind.EnergyShield > 0)
            {
                defence.Append($"energy shield {kind.EnergyShield}");
            }

            ImGuiText.Mono(DimText, defence.ToString().TrimEnd());
        }

        // MULTIPLIERS ARE CALLED MULTIPLIERS. The table's Life/Damage/XP sit at a median of
        // about 110 with 100 as the baseline, so they are percentages of a base this row does
        // not carry - 120 is "a fifth more than its kind", not 120 life.
        ImGuiText.Mono(DimText, $"life {one.Life}%  damage {one.Damage}%  xp {one.Xp}%");

        // Deliberately unlabelled units - see MonsterVariety. Speed and attack speed are
        // plainly not percentages, and this table cannot say what they are.
        ImGuiText.Mono(
            DimText,
            $"speed {one.Speed}  attack {one.AttackSpeed}  reach {one.MinAttack}-{one.MaxAttack}"
            + $"  aggro {one.MinAggro}-{one.MaxAggro}");

        if (one.Stance is { Length: > 0 })
        {
            ImGui.SameLine();
            ImGuiText.Mono(DimText, $"  {one.Stance}");
        }

        // THE QUEST FLAG, on the 68 monsters that carry one - every named campaign boss. It
        // says this kill advances a quest, not where anything is.
        if (one.Quest > 0)
        {
            ImGuiText.Mono(UnknownText, $"quest step - QuestFlags row {one.Quest}");
        }

        DrawSkills(table, one);
        DrawModifiers(table, one);
        DrawRest(table, one);
    }

    /// <summary>
    /// The rest of the row: the shape of the thing, and the columns still holding row numbers.
    /// </summary>
    /// <remarks>
    /// FOLDED, because these are the fields somebody goes looking for rather than reads at a
    /// glance - and half of them are numbers pointing into tables this build does not carry.
    /// They are shown anyway, with a # to say so: a field that is loaded and never drawn is
    /// indistinguishable from one that was never carried, and the next person wanting
    /// MonsterType would go and export it again.
    /// </remarks>
    private static void DrawRest(MonsterVarieties table, MonsterVariety one)
    {
        if (!ImGui.TreeNode("the rest of the row"))
        {
            return;
        }

        try
        {
            ImGuiText.Mono(
                DimText,
                $"  size {one.Size}   model {one.ModelSize}%   poise "
                + one.Poise.ToString("0.###", CultureInfo.InvariantCulture));

            // AttackCrit holds 0, 1 or 2 - a kind, not a chance. Drawn as a bare number for
            // exactly that reason: a percent sign here would be an invented statistic.
            MonsterKind? kind = table.Kind(one);
            string blood = table.BloodName(one);
            ImGuiText.Mono(
                DimText,
                $"  crit kind {one.Crit}   blood "
                + (blood.Length > 0 ? blood : "#" + one.Blood.ToString(CultureInfo.InvariantCulture))
                + "   type "
                + (kind is null ? "#" + one.Type.ToString(CultureInfo.InvariantCulture) : kind.Id));

            if (kind is { Spread: > 0 })
            {
                ImGuiText.Mono(DimText, $"  damage spread {kind.Spread}");
            }

            // PROFILE NAMES, NOT PERCENTAGES - see MonsterVarieties.ResistancesOf. The rows
            // behind these carry 32 numeric columns whose tiers the export does not explain, and
            // "MajorFireResist" is what a reader wants anyway.
            string[] resists = [.. table.ResistancesOf(one)];
            if (resists.Length > 0)
            {
                ImGuiText.Mono(DimText, "  resistances  " + string.Join(", ", resists));
            }

            if (kind is { Summoned: true })
            {
                ImGuiText.Mono(DimText, "  summoned");
            }

            // THE FULL TAG LIST, not just the six on the first line. The rest is movement speed,
            // attribute exclusions and on-hit audio - rarely what somebody came for, but the
            // place they would look for it.
            string[] all = [.. table.TagsOf(one)];
            if (all.Length > 0)
            {
                ImGuiText.Wrapped(DimText, "  tags  " + string.Join(", ", all));
            }

            if (one.Base is { Length: > 0 })
            {
                ImGuiText.Mono(DimText, "  base  " + one.Base);
            }

            // The league bases live here - AbyssMonsterBase, SanctumMonsterBase - which is what
            // makes this column worth drawing despite pointing outside this table.
            foreach (string parent in one.Inherits ?? [])
            {
                ImGuiText.Mono(DimText, "  from  " + parent);
            }
        }
        finally
        {
            ImGui.TreePop();
        }
    }

    /// <summary>
    /// What the monster can do, by name.
    /// </summary>
    /// <remarks>
    /// FOLDED SHUT BY DEFAULT, because a boss carries sixty-seven of these and the browser's
    /// left pane is a list of everything in the area. The header carries the count, which is
    /// the part worth seeing without asking: one skill and sixty-seven are different animals.
    ///
    /// The skill names are the game's own internal ids rather than what a tooltip would say -
    /// GSYamaChaosCloud, not "Chaos Cloud". They read well enough to be worth showing, and the
    /// table that holds the pretty names is a different export again.
    /// </remarks>
    private static void DrawSkills(MonsterVarieties table, MonsterVariety one)
    {
        if (one.SkillCount == 0)
        {
            return;
        }

        string header = one.SkillCount == 1 ? "1 skill" : $"{one.SkillCount} skills";

        // Named or not is worth saying in the header rather than leaving somebody to wonder
        // why every line is a number: an unexported reference table and a monster whose skills
        // are genuinely unknown look identical from the list alone.
        if (table.NamedSkills == 0)
        {
            header += " (no names - the skill table was not exported)";
        }

        if (!ImGui.TreeNode(header))
        {
            return;
        }

        try
        {
            foreach (string skill in table.Skills(one))
            {
                ImGuiText.Mono(skill.StartsWith('#') ? UnknownText : DimText, "  " + skill);
            }
        }
        finally
        {
            ImGui.TreePop();
        }
    }

    /// <summary>
    /// The modifiers the game hangs on this kind, and the stats they set.
    /// </summary>
    /// <remarks>
    /// This is the half that answers "why is this one different": MonsterUniqueT2Boss sets
    /// monster_dropped_item_rarity_+% to 1600 and i_am_boss_of_tier to 2, and neither is
    /// visible anywhere else in this tool.
    ///
    /// The empty slots are already gone - Mods2 is a fixed-width array whose filler row is
    /// literally called "Nothing", and 29% of all modifier references pointed at it. They are
    /// dropped when the table is built rather than here, so nothing downstream has to know.
    /// </remarks>
    private static void DrawModifiers(MonsterVarieties table, MonsterVariety one)
    {
        ModifierMeaning[] carried = [.. table.Modifiers(one)];
        if (carried.Length == 0)
        {
            return;
        }

        if (!ImGui.TreeNode(carried.Length == 1 ? "1 modifier" : $"{carried.Length} modifiers"))
        {
            return;
        }

        try
        {
            foreach (ModifierMeaning meaning in carried)
            {
                ImGuiText.Mono(KnownText, "  " + meaning.Id);
                foreach (ModifierStat stat in meaning.Stats ?? [])
                {
                    ImGuiText.Mono(DimText, $"      {stat.Stat}  {stat.Range}");
                }
            }
        }
        finally
        {
            ImGui.TreePop();
        }
    }

    private void DrawComponents(EntityView view, WorldEntity? chosen)
    {
        ImGui.BeginChild("components", new Vector2(0f, 0f), ImGuiChildFlags.Borders);

        try
        {
            DrawComponentsInto(view, chosen);
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    /// <summary>
    /// The selected entity, in three folds.
    /// </summary>
    /// <remarks>
    /// THREE SECTIONS RATHER THAN ONE SCROLL, and the split is by where the answer COMES FROM
    /// rather than by what it is about. What the thing IS comes from the game's shipped tables
    /// and is the same for every one of its kind; what it is CARRYING is read out of this one
    /// entity's memory and changes while you watch; what it is MADE OF is the component layout,
    /// which is a reverse-engineering view and not something to read at a glance.
    ///
    /// Mixing them cost the pane its shape: a monster with 22 stats and 18 components pushed its
    /// own name off the top, so the half that identifies it was the half you had to scroll for.
    ///
    /// ImGui remembers each fold by id, so a section closed once stays closed across selections -
    /// which is the point. Somebody reading stats all afternoon closes the other two once.
    /// </remarks>
    private void DrawComponentsInto(EntityView view, WorldEntity? chosen)
    {
        if (view.Address == 0)
        {
            ImGui.TextColored(DimText, "pick an entity");
            return;
        }

        // ONE CALL PER SECTION, each owning its own fold. The shape matters: written as an
        // early return out of a closed section, the last one has to be invoked from two places,
        // and the next return added anywhere above it silently drops a third of the pane.
        if (ImGui.CollapsingHeader("what this is", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextColored(PathText, view.Path);
            ImGuiText.Mono(DimText, $"id {view.Id}  at 0x{view.Address:X}");
            DrawTable(view.Path);
        }

        DrawCarried(view);
        DrawInnards(view, chosen);
    }

    /// <summary>
    /// What this one entity has on it right now - read from its memory, not from a table.
    /// </summary>
    private void DrawCarried(EntityView view)
    {
        if (!ImGui.CollapsingHeader("what it is carrying", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        // What is on this entity with a clock running. Here rather than in the world read
        // because it is a vector walk per entity - affordable for the one under the cursor,
        // not for all of them - and here rather than nowhere because it answers a question
        // the component list cannot: whether a thing expires, and when. A flame wall carries
        // no DiesAfterTime component at all, so this is the only place its duration could
        // show up, and the only way to find out is to look at one.
        if (view.EffectsNote.Length > 0)
        {
            ImGui.TextColored(view.Timed.Count > 0 ? PathText : DimText, ImGuiText.Escape(view.EffectsNote));
            foreach (TimedEffect effect in view.Timed)
            {
                // A permanent effect is not a duration of zero, it is a duration of INFINITY -
                // or of NaN. Straight from the reference's buff bar, which tells the two apart
                // exactly this way; formatted naively it reads "8 of 8s" and looks like a
                // number somebody could act on.
                bool finite = float.IsFinite(effect.TimeLeft) && float.IsFinite(effect.TotalTime);
                string clock = finite && effect.TimeLeft > 0f
                    ? $"{effect.TimeLeft:F1} of {effect.TotalTime:F1}s left"
                    : "permanent";
                string charges = effect.Charges > 0 ? $"  x{effect.Charges}" : string.Empty;
                ImGui.TextColored(DimText, $"    {ImGuiText.Escape(effect.Name)}  {clock}{charges}");
            }

            ImGui.Separator();
        }

        // The entity's own numbers, as the game keeps them: a flat vector of (stat id, value).
        // Raw ids rather than names, because the names come from the game's Stats table and
        // that is 27,000 rows nobody needs in this repo to answer one question - 347 is
        // base_skill_effect_duration and 351 is skill_effect_duration, and a wall that carries
        // its own duration says so with a value in milliseconds against one of those.
        if (view.StatsNote.Length > 0)
        {
            ImGui.TextColored(view.Numbers.Count > 0 ? PathText : DimText, ImGuiText.Escape(view.StatsNote));

            // Named where the game's own table has a name, three across when it does not -
            // a hundred unnamed ids is a wall of numbers and a hundred named ones is a list
            // worth reading, so the two want different shapes.
            bool named = view.Numbers.Any(stat => stat.Name.Length > 0);
            int column = 0;
            string bag = string.Empty;
            foreach (EntityStat stat in view.Numbers)
            {
                // Grouped by which bag it came from, because the same stat is in both with
                // different values - the sheet's mana is in one and a smaller number is in
                // the other, and a list that runs them together makes every row ambiguous.
                if (stat.Source != bag)
                {
                    bag = stat.Source;
                    column = 0;
                    ImGui.TextColored(PathText, $"  from {ImGuiText.Escape(bag)}:");
                }

                if (named)
                {
                    ImGui.TextColored(DimText, stat.Name.Length > 0
                        ? $"  {ImGuiText.Escape(stat.Name)} = {stat.Value}"
                        : $"  {stat.Id} = {stat.Value}   (no name for this id)");
                    continue;
                }

                if (column % 3 != 0)
                {
                    ImGui.SameLine();
                }

                ImGui.TextColored(DimText, $"  {stat.Id,6} = {stat.Value,-11}");
                column++;
            }
        }
    }

    /// <summary>
    /// What the entity is made of, and the four things that can be done to it.
    /// </summary>
    /// <remarks>
    /// THE BUTTONS LIVE HERE, at the top, rather than under the identity block where they were.
    /// All four reach past what a thing IS and into the machinery - hiding a kind, opening it in
    /// the dissector, holding two side by side - so they belong with the component list they act
    /// on rather than between a monster's name and its stats.
    /// </remarks>
    private void DrawInnards(EntityView view, WorldEntity? chosen)
    {
        if (!ImGui.CollapsingHeader("what it is made of", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        DrawHideButtons(view, chosen);

        if (ImGui.SmallButton("Dissect the Entity"))
        {
            _dissect(view.Address, view.Path, "Entity");
        }

        if (_compare is not null)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Compare With##entity"))
            {
                _compare(view.Address, view.Path);
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "Holds this entity BESIDE the one already in the dissector instead of\n"
                    + "replacing it. Two of a kind read side by side separate what the species\n"
                    + "is from what this one is - and they walk their pointers together.");
            }
        }

        ImGui.Separator();

        if (view.Undescribed > 0)
        {
            ImGui.TextColored(UnknownText, $"{view.Undescribed} of {view.Components.Count} not described");
        }

        foreach (ComponentEntry component in view.Components)
        {
            // Open before dissect, and only where there is something to open. A component
            // nobody has written down has nothing to unfold, and a row that offers it anyway
            // would promise an answer this tool does not have.
            if (component.Described)
            {
                bool open = _open.Contains(component.Name);
                if (ImGui.SmallButton($"{(open ? "-" : "+")}##open{component.Name}"))
                {
                    if (!_open.Remove(component.Name))
                    {
                        _open.Add(component.Name);
                    }
                }

                ImGui.SameLine();
            }
            else
            {
                // Holds the column, so the dissect buttons stay in one line whether or not
                // the row above had something to unfold.
                ImGui.Dummy(new Vector2(17f, 0f));
                ImGui.SameLine();
            }

            if (ImGui.SmallButton($"Dissect##{component.Address:X}"))
            {
                // The component's own layout when the schema has one. When it does not - the
                // whole reason to be here - the generic one still names the two rows every
                // component shares, so an unknown structure is never opened completely blind.
                _dissect(component.Address, $"{Short(view.Path)}.{component.Name}",
                    component.Described ? component.Name : "Component");
            }

            if (_compare is not null)
            {
                // The same component off a second entity, which is the comparison that pays
                // best: one Life beside another has the same layout by construction, so every
                // row that differs is a quantity about that monster rather than about Life.
                ImGui.SameLine();
                if (ImGui.SmallButton($"+##compare{component.Address:X}"))
                {
                    _compare(component.Address, $"{Short(view.Path)}.{component.Name}");
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("hold beside what the dissector already has open");
                }
            }

            ImGui.SameLine();
            ImGui.TextColored(component.Described ? KnownText : UnknownText, component.Name);
            ImGui.SameLine();
            ImGuiText.Mono(DimText, $"0x{component.Address:X}");

            // The field count, because described is not binary: Monster has one field, and a
            // component in the game has dozens. "yes" hides the difference between the two.
            ImGui.SameLine();
            ImGui.TextColored(
                DimText,
                component.Described
                    ? $"{component.Fields} field{(component.Fields == 1 ? string.Empty : "s")}"
                    : "nothing written down");

            if (_open.Contains(component.Name))
            {
                DrawFields(component);
            }
        }
    }

    /// <summary>Lists what the schema says this component holds, with what it currently says.</summary>
    /// <remarks>
    /// The reason to look at an entity at all. A named field with a live value is what turns
    /// the schema from a claim into something the game can settle: hit a monster and watch
    /// Health move, open a chest and watch IsOpened turn over. A wrong offset shows up here
    /// as a number that will not move or a string that is not one.
    /// </remarks>
    private void DrawFields(ComponentEntry component)
    {
        IReadOnlyList<FieldReading>? values = component.Values;
        if (values is null || values.Count == 0)
        {
            // Open, and the reader has not come back with it yet. Saying so beats an empty
            // gap that reads as "this component holds nothing".
            ImGui.TextColored(DimText, "        reading...");
            return;
        }

        DrawFieldsInto(values, "        ");
    }

    /// <summary>One line per field, and the same again for whatever a field leads to.</summary>
    private static void DrawFieldsInto(IReadOnlyList<FieldReading> values, string indent)
    {
        foreach (FieldReading field in values)
        {
            ImGuiText.Mono(DimText, $"{indent}+0x{field.Offset:X3}");
            ImGui.SameLine();
            ImGui.TextColored(PathText, ImGuiText.Escape(field.Name));

            // The live reading, and the point of the whole pane: hit a monster and watch Health
            // move. Watching a number MOVE is a comparison between one frame and the next, so
            // the digits have to stand in the same place in both - in the body face a value
            // going from 999 to 1000 shifts everything after it and the eye loses the field it
            // was watching. No Escape here, unlike the name beside it: Mono is TextUnformatted
            // rather than printf, and escaping it as well would print the doubled signs.
            ImGui.SameLine();
            ImGuiText.Mono(KnownText, field.Text);

            // The WHY, where the schema has one. That text is the expensive part of this
            // project - drift history, what proved the offset, what it must not be confused
            // with - and a tooltip is the one place it costs nothing to carry.
            if (field.Comment.Length > 0 && ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    $"{field.Name}  ({field.Type.ToString().ToLowerInvariant()}) at +0x{field.Offset:X}\n\n"
                    + ImGuiText.Escape(Wrapped(field.Comment)));
            }

            // Always unfolded rather than behind another click. A field the schema bothered
            // to point at another struct IS that struct - Life.Health is a pool with a
            // maximum, not an address - and hiding it behind a second click would leave the
            // wall of hex that this exists to get rid of.
            if (field.Children is { Count: > 0 } children)
            {
                DrawFieldsInto(children, indent + "    ");
            }
        }
    }

    /// <summary>Breaks a schema comment into lines a tooltip can hold.</summary>
    private static string Wrapped(string text, int width = 90)
    {
        var built = new System.Text.StringBuilder(text.Length + 16);
        int since = 0;

        foreach (string word in text.Split(' '))
        {
            if (since > 0 && since + word.Length + 1 > width)
            {
                built.Append('\n');
                since = 0;
            }
            else if (since > 0)
            {
                built.Append(' ');
                since++;
            }

            built.Append(word);
            since += word.Length;
        }

        return built.ToString();
    }

    private void DrawSurvey(EntityView view)
    {
        if (view.Survey.Count == 0)
        {
            ImGui.TextColored(DimText, "nothing counted yet");
            return;
        }

        int unknown = view.Survey.Count(entry => !entry.Described);
        ImGui.TextColored(
            unknown > 0 ? UnknownText : DimText,
            $"{view.Survey.Count} kinds of component across {view.SurveyedEntities} entities, {unknown} not described");

        const ImGuiTableFlags Flags =
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit;

        if (!ImGui.BeginTable("survey", 3, Flags))
        {
            return;
        }

        try
        {
            DrawSurveyInto(view);
        }
        finally
        {
            ImGui.EndTable();
        }
    }

    private void DrawSurveyInto(EntityView view)
    {
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("component", ImGuiTableColumnFlags.WidthFixed, 280f);
        ImGui.TableSetupColumn("carried by", ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableSetupColumn("fields", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        foreach (ComponentTally entry in view.Survey)
        {
            if (_filter.Length > 0 && !entry.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextColored(entry.Described ? KnownText : UnknownText, entry.Name);

            ImGui.TableNextColumn();
            ImGui.Text(entry.Count.ToString(CultureInfo.InvariantCulture));

            ImGui.TableNextColumn();
            ImGui.TextColored(
                DimText,
                entry.Described ? entry.Fields.ToString(CultureInfo.InvariantCulture) : "-");
        }
    }

    /// <summary>The frame's entities, filtered and nearest first.</summary>
    private List<WorldEntity> Listed(WorldSnapshot snapshot, WorldEntity? player)
    {
        IEnumerable<WorldEntity> entities = snapshot.Entities;

        // Before the text filter, because the two say opposite things: a filter says what to
        // KEEP, and this says what is never worth listing. Applied first, the hidden rows are
        // gone whatever is typed - which is what makes the filter usable for finding one
        // doodad among the scenery rather than a way of naming everything else.
        if (_hiding.Any)
        {
            entities = entities.Where(entity => !_hiding.Hides(entity.Path, entity.WorldX, entity.WorldY));
        }

        if (_filter.Length > 0)
        {
            // The NAME as well as the path, because the rows show both and a filter that only
            // knew one of them would be searching for something other than what is on screen.
            // It matters more the moment the name stops being English: the path is the same
            // text on every client, the name is whatever this one calls it, and somebody
            // typing what they can see should find it either way.
            entities = entities.Where(entity =>
                entity.Path.Contains(_filter, StringComparison.OrdinalIgnoreCase)
                || entity.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase));
        }

        return player is null
            ? [.. entities]
            : [.. entities.OrderBy(entity => Distance(entity, player))];
    }

    private static float Distance(WorldEntity entity, WorldEntity player)
    {
        float dx = entity.WorldX - player.WorldX;
        float dy = entity.WorldY - player.WorldY;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>The last segment of a metadata path - enough to tell entities apart in a label.</summary>
    private static string Short(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash >= 0 && slash < path.Length - 1 ? path[(slash + 1)..] : path;
    }
}
