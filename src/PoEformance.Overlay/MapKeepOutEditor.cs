using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.Ui;

namespace PoEformance.Overlay;

/// <summary>
/// Lets somebody say where the game's interface is, by dragging boxes over it.
/// </summary>
/// <remarks>
/// BY DRAGGING RATHER THAN BY TYPING NUMBERS, and that is the whole reason this is a class
/// instead of four sliders. What is being described is "that orb, there" - a thing on the
/// screen the person is looking at - and the only way to check a number for it is to type one,
/// look, and type another. The reference tool solves the same problem the same way: GameHelper2's
/// Radar has a "culling window" the user drags over the game once and it remembers.
///
/// WHAT A BOX IS is deliberately literal: the window you drag IS the region kept out. Nothing
/// is scaled, offset or interpreted between what is dragged and what is stored, so a box that
/// looks right cannot be wrong - which matters because everything else about these zones is a
/// description rather than a measurement (see <see cref="MapKeepOut"/>).
///
/// SAVED AS FRACTIONS of the window, so the zones survive a resolution change; the conversion
/// happens here, on the way in and out, and nothing downstream sees pixels it did not ask for.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class MapKeepOutEditor
{
    /// <summary>How see-through a zone box is while it is being dragged.</summary>
    /// <remarks>
    /// Enough to place it against the thing it is covering - a solid box would hide the orb it
    /// is supposed to line up with, which is the one moment the picture underneath matters.
    /// </remarks>
    private static readonly Vector4 BoxColour = new(0.85f, 0.35f, 0.25f, 0.28f);

    private static readonly Vector4 BoxEdge = new(0.95f, 0.45f, 0.30f, 0.90f);

    /// <summary>
    /// Smallest box worth having: below this the title bar cannot be grabbed.
    /// </summary>
    /// <remarks>
    /// Small on purpose, and it is why the buttons live in a window of their own rather than
    /// inside each box. The experience strip is a few dozen pixels tall; a box that had to be
    /// large enough to hold a row of controls could not describe it.
    /// </remarks>
    private static readonly Vector2 SmallestBox = new(80f, 38f);

    private bool _editing;

    // The zone boxes are placed by us on the frame this turns true and dragged by the user
    // after it - so the position is pushed once and read back from then on. Placing them every
    // frame would pin them and they could not be moved at all.
    private bool _place;

    private (int Width, int Height) _placedFor;

    /// <summary>Where the interface is, as it stands. Replaced whenever a box moves.</summary>
    public MapKeepOut Zones { get; set; } = MapKeepOut.Default;

    /// <summary>Called after anything here changes it, so the settings are written down.</summary>
    public Action? Changed { get; set; }

    /// <summary>Whether the boxes are on screen to be dragged.</summary>
    public bool Editing
    {
        get => _editing;
        set
        {
            if (_editing != value)
            {
                _editing = value;
                _place = value; // put them where the settings say, once, on the way in
            }
        }
    }

    /// <summary>
    /// The rows that belong in the settings page: the switch, the list, and the way in.
    /// </summary>
    public void DrawControls()
    {
        bool on = Zones.On;
        if (ImGui.Checkbox("Keep the map off the game's interface", ref on))
        {
            Zones = Zones with { On = on };
            Changed?.Invoke();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "The game draws the large map across the WHOLE window and paints the orbs, the"
                + " bars and any open panel on top of it. This overlay cannot get underneath"
                + " them, so it stays off the parts of the screen listed below.");
        }

        if (!on)
        {
            return;
        }

        bool editing = Editing;
        if (ImGui.Checkbox("Show the zones, to drag them", ref editing))
        {
            Editing = editing;
        }

        DrawZoneList();
    }

    /// <summary>
    /// Add, reset, and one row per zone. Shared by the settings page and the floating controls.
    /// </summary>
    /// <remarks>
    /// ONE list drawn in two places rather than two lists. A second copy of this would be a
    /// second place to add a zone and one place to forget, and the two would disagree about
    /// what a zone is called the first time either is edited.
    /// </remarks>
    private void DrawZoneList()
    {
        if (ImGui.SmallButton("Add a zone"))
        {
            Zones = Zones.Plus();
            _place = true;
            Editing = true;
            Changed?.Invoke();
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Reset"))
        {
            Zones = MapKeepOut.Default;
            _place = true;
            Changed?.Invoke();
        }

        IReadOnlyList<MapKeepOutZone> zones = Zones.ZonesOrDefault;
        int removing = -1;

        for (int i = 0; i < zones.Count; i++)
        {
            MapKeepOutZone zone = zones[i];
            bool zoneOn = zone.On;

            // The ##index suffix is not decoration: ImGui derives a control's identity from
            // its label, so two zones named the same would be ONE checkbox and clicking either
            // would move the first.
            if (ImGui.Checkbox($"{zone.Name}##keepout{i}", ref zoneOn))
            {
                Zones = Zones.With(i, zone with { On = zoneOn });
                Changed?.Invoke();
            }

            ImGui.SameLine();
            ImGui.TextDisabled(
                $"{zone.Left * 100f:F0}-{zone.Right * 100f:F0}%"
                + $" x {zone.Top * 100f:F0}-{zone.Bottom * 100f:F0}%");

            ImGui.SameLine();
            if (ImGui.SmallButton($"Remove##keepout{i}"))
            {
                removing = i;
            }
        }

        if (removing >= 0)
        {
            Zones = Zones.Less(removing);
            _place = true;
            Changed?.Invoke();
        }
    }

    /// <summary>Draws the draggable boxes, while they are being edited.</summary>
    /// <param name="width">The game window's width in pixels.</param>
    /// <param name="height">Its height.</param>
    public void Draw(int width, int height)
    {
        if (!_editing || !Zones.On || width < 1 || height < 1)
        {
            return;
        }

        // A resized window moves every zone, because they are stored proportionally: the boxes
        // have to be put back where the settings now say rather than left at their old pixels.
        if (_placedFor != (width, height))
        {
            _placedFor = (width, height);
            _place = true;
        }

        DrawBoxes(width, height);
        _place = false;

        DrawFloatingControls(width, height);
    }

    /// <summary>One draggable box per zone, each one exactly the region it stands for.</summary>
    private void DrawBoxes(int width, int height)
    {
        IReadOnlyList<MapKeepOutZone> zones = Zones.ZonesOrDefault;

        ImGui.PushStyleColor(ImGuiCol.WindowBg, BoxColour);
        ImGui.PushStyleColor(ImGuiCol.Border, BoxEdge);
        ImGui.PushStyleColor(ImGuiCol.TitleBg, BoxColour);
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, BoxEdge);

        for (int i = 0; i < zones.Count; i++)
        {
            MapKeepOutZone zone = zones[i];
            ScreenRect at = zone.Placed(width, height);

            if (_place)
            {
                ImGui.SetNextWindowPos(at.TopLeft, ImGuiCond.Always);
                ImGui.SetNextWindowSize(new Vector2(at.Width, at.Height), ImGuiCond.Always);
            }

            ImGui.SetNextWindowSizeConstraints(SmallestBox, new Vector2(width, height));

            // NoSavedSettings so ImGui's own ini never becomes a second, disagreeing record of
            // where these are: the settings file is the one that decides.
            if (ImGui.Begin(
                    $"{zone.Name}##keepoutbox{i}",
                    ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar
                    | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoSavedSettings
                    | ImGuiWindowFlags.NoDocking))
            {
                ImGui.TextDisabled("drag / resize me");
            }

            Vector2 position = ImGui.GetWindowPos();
            Vector2 size = ImGui.GetWindowSize();
            ImGui.End();

            var dragged = new ScreenRect(
                position.X, position.Y, position.X + size.X, position.Y + size.Y);

            // Written back only once the box is LET GO, and only when it really moved. Two
            // reasons, and the tool's own windows settle on the same rule: a settings file
            // rewritten on every frame of a drag is a file written sixty times a second, and
            // mid-drag the person is the authority on where the box is - reading it back and
            // pushing it again is what a jittering drag feels like.
            if (!_place && !ImGui.IsMouseDown(ImGuiMouseButton.Left) && Moved(at, dragged))
            {
                Zones = Zones.With(i, zone.MovedTo(dragged, width, height));
                Changed?.Invoke();
            }
        }

        ImGui.PopStyleColor(4);
    }

    /// <summary>The small window holding the way out, and the list of zones.</summary>
    /// <remarks>
    /// ITS OWN WINDOW rather than only the settings page, because the boxes are drawn whether
    /// the tools window is open or not: somebody who switched these on and then closed the tools
    /// would be left with boxes over their game and nothing to dismiss them with.
    /// </remarks>
    private void DrawFloatingControls(int width, int height)
    {
        ImGui.SetNextWindowPos(
            new Vector2(width * 0.5f, height * 0.08f), ImGuiCond.Appearing, new Vector2(0.5f, 0f));
        ImGui.SetNextWindowBgAlpha(0.9f);

        if (ImGui.Begin(
                "Where the game's interface is##keepoutcontrols",
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings
                | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking))
        {
            ImGui.TextDisabled("Cover the orbs, the bars and anything else the map must not");
            ImGui.TextDisabled("be drawn over. Boxes are saved as a share of the window.");
            ImGui.Separator();

            DrawZoneList();

            ImGui.Separator();
            if (ImGui.Button("Done"))
            {
                Editing = false;
            }
        }

        ImGui.End();
    }

    /// <summary>Whether a box was dragged, rather than merely re-reported.</summary>
    private static bool Moved(ScreenRect was, ScreenRect now)
        => Math.Abs(was.Left - now.Left) > 0.5f
           || Math.Abs(was.Top - now.Top) > 0.5f
           || Math.Abs(was.Right - now.Right) > 0.5f
           || Math.Abs(was.Bottom - now.Bottom) > 0.5f;
}
