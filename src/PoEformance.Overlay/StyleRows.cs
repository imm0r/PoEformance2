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
    private static readonly Vector4 DimText = OverlayTheme.Quiet;

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

            if (_groups.Length > 1 && !ImGui.CollapsingHeader(group))
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
        ImGui.TextColored(
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

        // What is set, and how to unset it. Shown only when there is something to unset, so
        // an untouched list is a list of names rather than a wall of buttons.
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

        wanted = DrawAdjustments(entry, wanted);

        if (wanted != style)
        {
            _style.Set(entry.Key, wanted);
            Changed();
        }

        ImGui.PopID();
    }

    /// <summary>The sliders and the icon box, indented under the row they belong to.</summary>
    private LayerStyle DrawAdjustments(StyleEntry entry, LayerStyle wanted)
    {
        bool anySlider = entry.Traits.HasFlag(StyleTraits.Scale) || entry.Traits.HasFlag(StyleTraits.Width);
        if (!anySlider && !entry.Traits.HasFlag(StyleTraits.Icon))
        {
            return wanted;
        }

        ImGui.Indent(26f);

        if (entry.Traits.HasFlag(StyleTraits.Scale))
        {
            // Starts at the ordinary size rather than at zero, so dragging it is an
            // adjustment from what is on screen rather than from nothing.
            float scale = wanted.Scale > 0f ? wanted.Scale : 1f;
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 6.5f);
            if (ImGui.SliderFloat("size###scale", ref scale, 0.3f, 4f, "x%.2f"))
            {
                wanted = wanted with { Scale = Math.Abs(scale - 1f) < 0.001f ? 0f : scale };
            }

            if (entry.Traits.HasFlag(StyleTraits.Width))
            {
                ImGui.SameLine();
            }
        }

        if (entry.Traits.HasFlag(StyleTraits.Width))
        {
            float width = wanted.Width;
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 6.5f);

            // Zero is a real value here and it means "scale it with the marker", which is
            // what a line should do by default - so the format says so rather than showing a
            // meaningless 0.0.
            if (ImGui.SliderFloat("line###width", ref width, 0f, 8f, width <= 0f ? "as it comes" : "%.1f px"))
            {
                wanted = wanted with { Width = width };
            }
        }

        if (entry.Traits.HasFlag(StyleTraits.Icon))
        {
            wanted = DrawIcon(entry, wanted);
        }

        ImGui.Unindent(26f);
        return wanted;
    }

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

        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 16.5f);
        ImGui.InputText("###iconpath", ref _iconPath, 512);

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

        ImGui.TextColored(DimText, "a .png next to the tool, or any full path. Missing files draw the shape.");
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
