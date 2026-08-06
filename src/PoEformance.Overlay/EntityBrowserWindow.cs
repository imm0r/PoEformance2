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

    /// <summary>Whether the window is on screen. Nothing is read while it is not.</summary>
    public bool Visible { get; set; }

    /// <summary>Draws the window and publishes what it wants read next.</summary>
    /// <param name="snapshot">
    /// The frame's entities. The LIST comes from here at no cost - it is already read and
    /// drawn - and only the selected entity is taken apart.
    /// </param>
    public void Render(WorldSnapshot snapshot, WorldEntity? player)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!Visible)
        {
            _inspector.Request(EntityRequest.Idle);
            return;
        }

        EntityView view = _inspector.View;
        List<WorldEntity> listed = Listed(snapshot, player);

        ImGui.SetNextWindowSize(new Vector2(880f, 560f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(new Vector2(120f, 120f), ImGuiCond.FirstUseEver);

        bool open = Visible;
        bool expanded = ImGui.Begin("Entity browser", ref open, ImGuiWindowFlags.NoFocusOnAppearing);

        // End() in a finally. An exception between Begin and End leaves ImGui's window stack
        // unbalanced, and the assert that follows kills the process even though the overlay
        // catches the exception itself.
        try
        {
            if (expanded)
            {
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
            }
        }
        finally
        {
            ImGui.End();
        }
        Visible = open;

        _inspector.Request(new EntityRequest(
            Enabled: true,
            Address: _selected,
            Survey: [.. snapshot.Entities.Select(entity => entity.Address)],
            SurveySequence: _surveySequence));
    }

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

                // ###address, not ##: the label carries a live distance, and ImGui derives a
                // control's identity from its label - so without this the row would be a new
                // control every frame and the click would never land.
                if (ImGui.Selectable($"{entity.Kind}  {entity.ShortName}{away}###{entity.Address:X}",
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

        if (view.Undescribed > 0)
        {
            ImGui.TextColored(UnknownText, $"{view.Undescribed} of {view.Components.Count} not described");
        }

        foreach (ComponentEntry component in view.Components)
        {
            if (ImGui.SmallButton($"dissect##{component.Address:X}"))
            {
                // The schema's layout when there is one, so a known component names its own
                // rows - and nothing when there is not, which is the whole reason to be here.
                _dissect(component.Address, $"{Short(view.Path)}.{component.Name}",
                    component.Described ? component.Name : string.Empty);
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
            entities = entities.Where(entity =>
                entity.Path.Contains(_filter, StringComparison.OrdinalIgnoreCase));
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
