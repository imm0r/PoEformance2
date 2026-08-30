using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.Ui;

namespace PoEformance.Overlay;

/// <summary>
/// Shows what the map is keeping off, and lets somebody add to it by dragging boxes.
/// </summary>
/// <remarks>
/// MOSTLY A READOUT, and that is the shape it should have had from the start. The game's own
/// interface is measured, part by part, from the elements the game itself keeps - see
/// <see cref="HudReader"/> - so there is nothing here to describe by hand: the list names each
/// part the map is staying off and how big it came out, which is what turns a container that
/// over-claims from a mystery into a click.
///
/// THE BOXES ARE FOR WHAT MEASUREMENT CANNOT REACH - another overlay parked over the game, a
/// widget, an interface part whose element understates itself. There are none by default. What
/// they are is deliberately literal: the window you drag IS the region kept out, with nothing
/// scaled or interpreted in between, so a box that looks right cannot be wrong.
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

    /// <summary>What the map keeps off, as it stands. Replaced whenever anything here changes it.</summary>
    public MapKeepOut Zones { get; set; } = MapKeepOut.Default;

    /// <summary>
    /// The interface parts the last read measured, for the list. Published every frame.
    /// </summary>
    /// <remarks>
    /// NOT stored and not saved: these are a measurement of what is on screen right now, and a
    /// remembered one would be a list of where the orbs were the last time somebody looked at
    /// this page. What IS saved is which of them to ignore, by name.
    /// </remarks>
    public IReadOnlyList<HudPart> Parts { get; set; } = [];

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
    /// The rows that belong in the settings page: the switch, what was measured, and the boxes.
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
                + " them, so it stays off what the interface covers - measured from the game's"
                + " own HUD element, part by part.");
        }

        if (!on)
        {
            return;
        }

        // ### rather than ##: the label carries a live count, and ImGui derives a control's
        // identity from its label - so with ## the node would be a NEW node every time a part
        // appeared or went, and it would snap shut under the cursor. ### fixes the identity
        // and lets the label say whatever it likes.
        if (ImGui.TreeNode($"Interface parts ({Parts.Count} measured)###keepouthud"))
        {
            DrawPartList();
            ImGui.TreePop();
        }

        if (ImGui.TreeNode($"Extra boxes ({Zones.ZonesOrEmpty.Count})###keepoutzones"))
        {
            bool editing = Editing;
            if (ImGui.Checkbox("Show them, to drag them", ref editing))
            {
                Editing = editing;
            }

            DrawZoneList();
            ImGui.TreePop();
        }
    }

    /// <summary>
    /// What the interface measured as, and which of it to honour.
    /// </summary>
    /// <remarks>
    /// THE SIZES ARE THE POINT of showing this at all. Several of these parts are containers,
    /// and a container that reports a rectangle far larger than what it draws would quietly eat
    /// the map - the atlas panel has form here, stating an extent 733 pixels narrower than the
    /// screen it covers. A part that reads absurdly is obvious in a list of its neighbours and
    /// invisible in a screenshot of the map, so it is listed with the number it produced and a
    /// switch beside it.
    /// </remarks>
    private void DrawPartList()
    {
        bool hud = Zones.Hud;
        if (ImGui.Checkbox("Measure it##keepouthudon", ref hud))
        {
            Zones = Zones with { Hud = hud };
            Changed?.Invoke();
        }

        if (!hud)
        {
            ImGui.TextDisabled("off - the map will be drawn over the orbs and the bars");
            return;
        }

        if (Parts.Count == 0)
        {
            ImGui.TextDisabled("nothing measured - no HUD element resolved, or no game in front");
            return;
        }

        foreach (HudPart part in Parts)
        {
            bool honoured = Zones.Honours(part.Label);
            if (ImGui.Checkbox($"{part.Label}##keepoutpart{part.Address:X}", ref honoured))
            {
                Zones = Zones.Honouring(part.Label, honoured);
                Changed?.Invoke();
            }

            ImGui.SameLine();
            ImGui.TextDisabled(
                $"{part.Where.Width:F0}x{part.Where.Height:F0} at"
                + $" {part.Where.Left:F0},{part.Where.Top:F0}   {Extent(part.From)}");
        }
    }

    /// <summary>How a part's rectangle was arrived at, in a word.</summary>
    /// <remarks>
    /// Printed because the ways differ in how much they can be trusted: an element that states
    /// its own extent is a reading, and one measured from what its children cover is an
    /// inference about a container that claimed nothing.
    /// </remarks>
    private static string Extent(PanelExtent from) => from switch
    {
        PanelExtent.Element => "its own",
        PanelExtent.Children => "from its children",
        PanelExtent.Kind => "by kind",
        _ => "assumed",
    };

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
        if (ImGui.SmallButton("Remove all"))
        {
            // Only the boxes: the interface switch beside them is a different decision, and a
            // button labelled for one that quietly undoes the other is how a setting comes back
            // on for no reason anybody can trace.
            Zones = Zones with { Zones = [] };
            _place = true;
            Changed?.Invoke();
        }

        IReadOnlyList<MapKeepOutZone> zones = Zones.ZonesOrEmpty;
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
        IReadOnlyList<MapKeepOutZone> zones = Zones.ZonesOrEmpty;

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
