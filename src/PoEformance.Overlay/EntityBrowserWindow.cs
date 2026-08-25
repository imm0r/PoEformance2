using System.Globalization;
using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Core.Schema;
using PoEformance.Features;
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
    private static readonly Vector4 DimText = OverlayTheme.Quiet;
    private static readonly Vector4 UnknownText = new(1f, 0.62f, 0.35f, 1f);
    private static readonly Vector4 KnownText = new(0.55f, 0.78f, 1f, 1f);
    private static readonly Vector4 PathText = new(0.85f, 0.78f, 0.45f, 1f);

    private readonly EntityInspector _inspector;
    private readonly EntityHiding _hiding;
    private readonly Action<ulong, string, string> _dissect;
    private readonly Action<ulong, string>? _compare;
    private readonly Action<ulong, float, float>? _route;
    private readonly Func<ulong, bool>? _routed;

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
    public EntityBrowserWindow(
        EntityInspector inspector,
        EntityHiding hiding,
        Action<ulong, string, string> dissect,
        Action<ulong, float, float>? route = null,
        Func<ulong, bool>? routed = null,
        Action<ulong, string>? compare = null)
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
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 13.5f);
        ImGui.InputText("filter", ref _filter, 64);

        ImGui.SameLine();
        if (ImGui.Button(_surveyPane ? "back to entities" : "survey this area"))
        {
            _surveyPane = !_surveyPane;
            if (_surveyPane)
            {
                _surveySequence++;
            }
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                $"Counts every component across the {snapshot.Entities.Count - snapshot.Remembered} entities here.\n"
                + "The rare undescribed ones are the leads worth following.");
        }

        if (_surveyPane)
        {
            ImGui.SameLine();
            if (ImGui.Button("count again"))
            {
                _surveySequence++;
            }
        }

        ImGui.TextColored(DimText, view.Status);

        // WHY THIS IS HERE AND NOT BEHIND A BUTTON: it answers "am I looking at twelve
        // monsters or at four of them three times", and that question arrives while looking
        // at the list, not before. It costs one pass over the snapshot's monsters on the
        // frames this tab is in front, which is nothing beside the tree the pane draws.
        //
        // It runs on the list AFTER the reader has collapsed repeats, so it should now find
        // nothing - and that is the point of leaving it in. It is the check on the collapsing
        // rather than a leftover: anything it still reports is a repeat the position key does
        // not catch, which is exactly what nobody would notice otherwise.
        EntityDuplicates duplicates = EntityDuplicates.Of([.. snapshot.Listed]);
        ImGui.TextColored(
            duplicates.Any ? WarnText : DimText,
            ImGuiText.Escape(duplicates.Describe()));

        // What the corpse check saw. Here because dots left on cleared ground are noticed
        // while looking at this list, and because the three ways it can fail look identical
        // on the map and want completely different fixes - see CorpseSigns.
        if (snapshot.Corpses.Seen > 0)
        {
            ImGui.TextColored(
                snapshot.Corpses.Unreadable > 0 ? WarnText : DimText,
                ImGuiText.Escape("corpses: " + snapshot.Corpses.Describe()));
        }

        if (snapshot.Collapsed > 0)
        {
            // Says the MECHANISM, not the symptom. "On the same spot" was true but described
            // the position key this rule no longer uses, and a line that documents a rule
            // nobody applies any more is worse than no line: it is what the next person
            // reads instead of the code.
            ImGuiText.Wrapped(
                DimText,
                $"{snapshot.Collapsed} repeat entities dropped this read - the game gives one"
                + " monster several entities over one set of components");
        }

        DrawHidden();
    }

    private static readonly Vector4 WarnText = new(1f, 0.72f, 0.42f, 1f);

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
        if (ImGui.SmallButton("hide this kind"))
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
            if (ImGui.SmallButton("hide just this one"))
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

        if (!ImGui.CollapsingHeader($"hidden ({_hiding.Count})###hidden"))
        {
            return;
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("show everything again"))
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

                // BOTH names, where there are two. This window is where somebody finds out
                // which entities carry a name at all, and showing only the winner would hide
                // exactly that: "Elder Madox (ElderMadoxMapIntro)" answers the question, while
                // "Elder Madox" alone leaves you unsure which of the two you are reading.
                string called = entity.Name.Length > 0
                    ? $"{entity.Name}  ({entity.FileName})"
                    : entity.FileName;

                // ###address, not ##: the label carries a live distance, and ImGui derives a
                // control's identity from its label - so without this the row would be a new
                // control every frame and the click would never land.
                if (ImGui.Selectable($"{entity.Kind}  {called}{away}{facts}###{entity.Address:X}",
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

    private void DrawComponentsInto(EntityView view, WorldEntity? chosen)
    {
        if (view.Address == 0)
        {
            ImGui.TextColored(DimText, "pick an entity");
            return;
        }

        ImGui.TextColored(PathText, view.Path);
        ImGui.TextColored(DimText, $"id {view.Id}  at 0x{view.Address:X}");

        DrawHideButtons(view, chosen);

        if (ImGui.SmallButton("dissect the entity"))
        {
            _dissect(view.Address, view.Path, "Entity");
        }

        if (_compare is not null)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("compare with##entity"))
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

            ImGui.Separator();
        }

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

            if (ImGui.SmallButton($"dissect##{component.Address:X}"))
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
            ImGui.TextColored(DimText, $"0x{component.Address:X}");

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
            ImGui.TextColored(DimText, $"{indent}+0x{field.Offset:X3}");
            ImGui.SameLine();
            ImGui.TextColored(PathText, ImGuiText.Escape(field.Name));
            ImGui.SameLine();
            ImGui.TextColored(KnownText, ImGuiText.Escape(field.Text));

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
