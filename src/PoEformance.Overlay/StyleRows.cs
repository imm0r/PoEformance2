using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.World;

namespace PoEformance.Overlay;

/// <summary>
/// The style rows for one set of catalogue groups, drawn wherever their feature lives.
/// </summary>
/// <remarks>
/// GENERATED FROM THE CATALOGUE, not written out row by row, and that is the whole point. A
/// hand-written settings page is how a tool ends up with fourteen configurable things and
/// three that are not, with no way to tell which is which and no way to find out but reading
/// the drawing code. A new drawn thing appears in its editor the moment it gets a catalogue
/// entry, and it cannot be drawn without one.
///
/// ONE INSTANCE PER HOSTING PAGE rather than one editor for everything, which is what
/// replaced the single Appearance wall: the atlas styles sit on the atlas tab, the alert
/// styles on the alerts tab, and <see cref="StyleCatalogue.Homes"/> says which groups belong
/// where - with a test holding that every group is claimed by exactly one page. Each row
/// offers exactly what its entry says it can change, so a marker gets a size and a line does
/// not, and nothing pretends to be adjustable when nothing reads it.
///
/// Over the game rather than in the configuration window, because a colour is chosen by
/// LOOKING at it - on the map, among the other markers, at the size it will actually be. A
/// picker in another window is a picker you use once and then go and check.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class StyleRows
{
    private static readonly Vector4 DimText = OverlayInk.Quiet;

    private readonly OverlayStyle _style;
    private readonly Action _save;
    private readonly string[] _groups;

    /// <summary>Which key's icon box is open. Only one at a time, to keep the rows short.</summary>
    private string _editingIcon = string.Empty;
    private string _iconPath = string.Empty;

    // Something changed and has not been written down yet - see Settle.
    private bool _unsaved;

    /// <param name="save">Writes the style down. Called when a change has SETTLED, not per frame.</param>
    /// <param name="groups">The catalogue groups this block shows, in display order.</param>
    public StyleRows(OverlayStyle style, Action save, string[] groups)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(save);
        ArgumentNullException.ThrowIfNull(groups);
        _style = style;
        _save = save;
        _groups = groups;
    }

    /// <summary>Draws the rows, and writes down whatever has settled.</summary>
    /// <remarks>
    /// One group is drawn bare - its host's own header already says what it is. Several get
    /// a collapsing header each, CLOSED by default: this shape only occurs on the markers
    /// page, where eight groups open at once are the wall this arrangement replaced.
    /// </remarks>
    public void Draw()
    {
        foreach (string group in _groups)
        {
            IGrouping<string, StyleEntry>? entries =
                StyleCatalogue.Grouped().FirstOrDefault(g => g.Key == group);
            if (entries is null)
            {
                continue;
            }

            if (_groups.Length > 1 && !OverlayFonts.SectionHeader(group))
            {
                continue;
            }

            foreach (StyleEntry entry in entries)
            {
                DrawRow(entry);
            }
        }

        // After the content, so a drag that ended this frame is written down now rather
        // than waiting for whatever happens next - including the tab being switched away.
        Settle();
    }

    /// <summary>
    /// The global changed-count and its reset, for the one page that shows every leftover.
    /// </summary>
    /// <remarks>
    /// GLOBAL on purpose, and it says so: the count and the reset cover every drawn thing,
    /// the feature pages' styles included, because "put everything back how it came" is one
    /// decision, not five - and a reset that silently left the atlas colours standing would
    /// look like it had not worked.
    /// </remarks>
    public void DrawResetLine()
    {
        int changed = _style.Changed.Count;
        ImGuiText.Wrapped(
            DimText,
            changed == 0
                ? "everything as it comes - nothing changed yet"
                : $"{changed} changed, the feature pages' styles counted too. Everything else"
                  + " follows the defaults, so a corrected one reaches you.");

        ImGui.SameLine();
        if (ImGui.SmallButton("reset all") && changed > 0)
        {
            _style.ResetAll();
            Changed();
        }

        ImGui.Separator();
    }

    /// <summary>
    /// While the rows are not on screen: a change made and left behind still lands.
    /// </summary>
    /// <remarks>
    /// The tab can be switched away from - or the whole window closed - with a change made
    /// and not yet written down, and "the last thing I did before leaving was the thing
    /// that got lost" is the worst way for a settings editor to behave.
    /// </remarks>
    public void Idle() => Settle();

    /// <summary>Records a change, to be written down once the user has stopped making it.</summary>
    /// <remarks>
    /// The change itself lands immediately - the overlay reads the style every frame, which is
    /// the whole reason a colour can be chosen by looking at it. Only the SAVE waits.
    ///
    /// It has to. A slider drag or a colour wheel reports a new value on every frame it is
    /// held, so writing on each one is sixty file writes a second for one adjustment: a disk
    /// hammered for nothing, and a window in which the file is open when something goes wrong.
    /// </remarks>
    private void Changed() => _unsaved = true;

    /// <summary>Writes down a change once nothing is being dragged any more.</summary>
    private void Settle()
    {
        if (ImGui.IsAnyItemActive())
        {
            return;
        }

        if (_unsaved)
        {
            _unsaved = false;
            _save();
        }
    }

    /// <summary>One drawn thing: whether it is drawn, and everything its entry allows.</summary>
    private void DrawRow(StyleEntry entry)
    {
        // ### rather than a plain label: ImGui derives a control's identity from its label, and
        // two entries sharing a word would collapse into one control.
        ImGui.PushID(entry.Key);

        LayerStyle style = _style[entry.Key];
        LayerStyle wanted = style;

        bool visible = style.Visible;
        if (ImGui.Checkbox($"###show", ref visible))
        {
            wanted = wanted with { Hidden = !visible };
        }

        ImGui.SameLine();

        if (entry.Traits.HasFlag(StyleTraits.Colour))
        {
            Vector4 colour = ImGui.ColorConvertU32ToFloat4(style.ColourOr(entry.Fallback));
            if (ImGui.ColorEdit4(
                    "###colour",
                    ref colour,
                    ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel | ImGuiColorEditFlags.AlphaBar
                        | ImGuiColorEditFlags.AlphaPreviewHalf))
            {
                wanted = wanted with { Colour = OverlaySettings.FormatColour(ImGui.ColorConvertFloat4ToU32(colour)) };
            }

            ImGui.SameLine();
        }

        ImGui.Text(entry.Label);

        // A marker's shape, at the size it is drawn on the large map, in the chosen colour.
        // The row otherwise says "Breach" and a swatch, which is the two things somebody
        // reading it already knows.
        if (PreviewGlyph(entry.Key) is PoiGlyph glyph)
        {
            ImGui.SameLine();
            Preview(glyph, style.ColourOr(entry.Fallback), style.Sized(6f), style.WidthOr(0f));
        }

        wanted = DrawAdjustments(entry, wanted);

        // AT THE END OF THE ROW, after the adjustments rather than before them. It used to sit
        // straight after the name, which put a button in the middle of the row for the entries
        // that have one and left a gap there for the ones that do not - so the controls after it
        // started in a different place depending on whether anything had been changed yet.
        //
        // Shown only when there is something to unset, so an untouched list is a list of names
        // rather than a wall of buttons.
        if (!style.SaysNothing)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("reset"))
            {
                _style.Reset(entry.Key);
                Changed();
                ImGui.PopID();
                return;
            }
        }

        if (wanted != style)
        {
            _style.Set(entry.Key, wanted);
            Changed();
        }

        ImGui.PopID();
    }

    /// <summary>
    /// The sliders and the icon box, on the row's own line at a fixed column.
    /// </summary>
    /// <remarks>
    /// ON THE SAME LINE, which is what this list wanted all along. Every adjustment used to go
    /// on a SECOND line, indented under its row - so a marker with a size and an icon was three
    /// lines tall, and a list of a dozen of them was forty lines of mostly nothing. There is
    /// room on the line: what is on it is a tick, a swatch and a short name.
    ///
    /// AT A COLUMN rather than after the name, for the reason the atlas groups needed one. The
    /// names here run from "Route" to "Where a route starts", so hanging the controls off the
    /// end of each put "size" and "icon" wherever that row's name happened to stop - which is
    /// exactly the ragged edge that makes a list of settings unreadable.
    ///
    /// A wider column than the default: the row in front of it is a checkbox, a colour swatch,
    /// a name and sometimes a drawn preview of the marker, which is more than the tick and the
    /// name the default was measured for.
    /// </remarks>
    private LayerStyle DrawAdjustments(StyleEntry entry, LayerStyle wanted)
    {
        bool anySlider = entry.Traits.HasFlag(StyleTraits.Scale) || entry.Traits.HasFlag(StyleTraits.Width);
        if (!anySlider && !entry.Traits.HasFlag(StyleTraits.Icon))
        {
            return wanted;
        }

        OverlayLayout.ToColumn(ControlColumn);

        if (entry.Traits.HasFlag(StyleTraits.Scale))
        {
            // Starts at the ordinary size rather than at zero, so dragging it is an
            // adjustment from what is on screen rather than from nothing.
            float scale = wanted.Scale > 0f ? wanted.Scale : 1f;
            if (OverlayLayout.Narrow.Slider("size###scale", ref scale, 0.3f, 4f, "x%.2f"))
            {
                wanted = wanted with { Scale = Math.Abs(scale - 1f) < 0.001f ? 0f : scale };
            }

            if (entry.Traits.HasFlag(StyleTraits.Width))
            {
                OverlayLayout.Next();
            }
        }

        if (entry.Traits.HasFlag(StyleTraits.Width))
        {
            float width = wanted.Width;

            // Zero is a real value here and it means "scale it with the marker", which is
            // what a line should do by default - so the format says so rather than showing a
            // meaningless 0.0.
            if (OverlayLayout.Narrow.Slider(
                    "line###width", ref width, 0f, 8f, width <= 0f ? "as it comes" : "%.1f px"))
            {
                wanted = wanted with { Width = width };
            }
        }

        if (entry.Traits.HasFlag(StyleTraits.Icon))
        {
            if (anySlider)
            {
                OverlayLayout.Next();
            }

            wanted = DrawIcon(entry, wanted);
        }

        return wanted;
    }

    /// <summary>Where a row's adjustments start, in multiples of the text size.</summary>
    /// <remarks>
    /// Measured against the longest name in the catalogue plus what sits in front of it. Its own
    /// constant rather than the layout's default because the thing being cleared is wider here:
    /// a tick, a swatch, the name and a drawn preview, against the tick and a name elsewhere.
    /// </remarks>
    private const float ControlColumn = 18f;

    /// <summary>The icon box: a path to a picture to draw instead of the built-in shape.</summary>
    /// <remarks>
    /// Behind a button rather than always on screen, because it is the least-used of these by
    /// a wide margin and a text field per row would triple the window for it.
    /// </remarks>
    private LayerStyle DrawIcon(StyleEntry entry, LayerStyle wanted)
    {
        bool editing = _editingIcon == entry.Key;
        bool has = !string.IsNullOrEmpty(wanted.Icon);

        if (ImGui.SmallButton(editing ? "icon  v" : has ? "icon  *" : "icon"))
        {
            _editingIcon = editing ? string.Empty : entry.Key;
            _iconPath = wanted.Icon ?? string.Empty;
            editing = !editing;
        }

        if (has && !editing)
        {
            ImGui.SameLine();
            ImGui.TextColored(DimText, Path.GetFileName(wanted.Icon));
        }

        if (!editing)
        {
            return wanted;
        }

        // The one thing that gets a line of its own, and only while it is open: a path is as
        // long as a path, and the row it belongs to already holds a tick, a swatch, a name and
        // two sliders. Stepped in so it reads as belonging to the row above it.
        float step = OverlayLayout.Step();
        ImGui.Indent(step);
        try
        {
            OverlayLayout.Search(
                "###iconpath", "a .png next to the tool, or a full path...", ref _iconPath, 512,
                OverlayLayout.ButtonRoom("use", "none"));
        }
        finally
        {
            ImGui.Unindent(step);
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("use"))
        {
            wanted = wanted with { Icon = _iconPath.Trim() };
            _editingIcon = string.Empty;
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("none"))
        {
            wanted = wanted with { Icon = string.Empty };
            _editingIcon = string.Empty;
        }

        ImGuiText.Wrapped(DimText, "a .png next to the tool, or any full path. Missing files draw the shape.");
        return wanted;
    }

    /// <summary>
    /// The shape a key is drawn as, for the preview - nothing for the rest.
    /// </summary>
    /// <remarks>
    /// Only the places have shapes. Entity dots are circles and a preview of a circle beside
    /// the colour swatch says nothing the swatch has not already said.
    /// </remarks>
    private static PoiGlyph? PreviewGlyph(string key)
    {
        foreach (PoiGlyph glyph in Enum.GetValues<PoiGlyph>())
        {
            if (StyleCatalogue.ForGlyph(glyph) == key)
            {
                return glyph;
            }
        }

        return null;
    }

    /// <summary>Draws a marker where a row's text would go, at the size it is really drawn.</summary>
    private static void Preview(PoiGlyph glyph, uint colour, float radius, float width)
    {
        // Its own space claimed on the line, so the shape does not paint over the next row's
        // text - ImGui lays out from what a widget SAYS it occupies, not from what was drawn.
        Vector2 at = ImGui.GetCursorScreenPos();
        float box = Math.Max(16f, (radius * 2f) + 4f);
        ImGui.Dummy(new Vector2(box, box));

        PoiGlyphPainter.Draw(
            ImGui.GetWindowDrawList(), at + new Vector2(box / 2f, box / 2f), radius, colour, glyph, width);
    }
}
