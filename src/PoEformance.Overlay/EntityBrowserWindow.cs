using System.Globalization;
using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
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
    private static readonly Vector4 DimText = new(0.62f, 0.65f, 0.72f, 1f);
    private static readonly Vector4 UnknownText = new(1f, 0.62f, 0.35f, 1f);
    private static readonly Vector4 KnownText = new(0.55f, 0.78f, 1f, 1f);
    private static readonly Vector4 PathText = new(0.85f, 0.78f, 0.45f, 1f);

    private readonly EntityInspector _inspector;
    private readonly Action<ulong, string, string> _dissect;

    private ulong _selected;
    private string _filter = string.Empty;
    private int _surveySequence;
    private bool _surveyPane;

    /// <param name="dissect">
    /// Opens an address in the dissector: where it is, what to call it, and a schema layout
    /// when one applies. A callback rather than the window itself - "show me this" is all
    /// either side needs to know about the other.
    /// </param>
    public EntityBrowserWindow(EntityInspector inspector, Action<ulong, string, string> dissect)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        ArgumentNullException.ThrowIfNull(dissect);
        _inspector = inspector;
        _dissect = dissect;
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
            ImGui.SameLine();
            DrawComponents(view);
        }

        _inspector.Request(new EntityRequest(
            Enabled: true,
            Address: _selected,
            Survey: [.. snapshot.Entities.Select(entity => entity.Address)],
            SurveySequence: _surveySequence));
    }

    /// <summary>While the tab is not in front, nothing is read for it.</summary>
    public void Idle() => _inspector.Request(EntityRequest.Idle);

    private void DrawControls(EntityView view, WorldSnapshot snapshot)
    {
        ImGui.SetNextItemWidth(240f);
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
                $"Counts every component across the {snapshot.Entities.Count} entities here.\n"
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
        EntityDuplicates duplicates = EntityDuplicates.Of(snapshot.Entities);
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
            ImGui.TextColored(
                DimText,
                $"{snapshot.Collapsed} repeat entities dropped this read - the game gives one"
                + " monster several entities over one set of components");
        }
    }

    private static readonly Vector4 WarnText = new(1f, 0.72f, 0.42f, 1f);

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

        return life + shield + chest;
    }

    private void DrawList(List<WorldEntity> listed, WorldEntity? player)
    {
        // BeginChild is paired with EndChild whatever it returns, and the finally is there
        // for the same reason the window's is.
        ImGui.BeginChild("entities", new Vector2(360f, 0f), ImGuiChildFlags.Borders);

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
                }
            }
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    private void DrawComponents(EntityView view)
    {
        ImGui.BeginChild("components", new Vector2(0f, 0f), ImGuiChildFlags.Borders);

        try
        {
            DrawComponentsInto(view);
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    private void DrawComponentsInto(EntityView view)
    {
        if (view.Address == 0)
        {
            ImGui.TextColored(DimText, "pick an entity");
            return;
        }

        ImGui.TextColored(PathText, view.Path);
        ImGui.TextColored(DimText, $"id {view.Id}  at 0x{view.Address:X}");

        if (ImGui.SmallButton("dissect the entity"))
        {
            _dissect(view.Address, view.Path, "Entity");
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

            int column = 0;
            foreach (EntityStat stat in view.Numbers)
            {
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
            if (ImGui.SmallButton($"dissect##{component.Address:X}"))
            {
                // The component's own layout when the schema has one. When it does not - the
                // whole reason to be here - the generic one still names the two rows every
                // component shares, so an unknown structure is never opened completely blind.
                _dissect(component.Address, $"{Short(view.Path)}.{component.Name}",
                    component.Described ? component.Name : "Component");
            }

            ImGui.SameLine();
            ImGui.TextColored(component.Described ? KnownText : UnknownText, component.Name);
            ImGui.SameLine();
            ImGui.TextColored(DimText, $"0x{component.Address:X}");
        }
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
        ImGui.TableSetupColumn("described", ImGuiTableColumnFlags.WidthStretch);
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
            ImGui.TextColored(DimText, entry.Described ? "yes" : "-");
        }
    }

    /// <summary>The frame's entities, filtered and nearest first.</summary>
    private List<WorldEntity> Listed(WorldSnapshot snapshot, WorldEntity? player)
    {
        IEnumerable<WorldEntity> entities = snapshot.Entities;

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
