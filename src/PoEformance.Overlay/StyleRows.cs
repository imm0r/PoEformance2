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

    /// <summary>
    /// The path being typed, while an icon popup is open.
    /// </summary>
    /// <remarks>
    /// One field for every row, which is safe because ImGui allows one popup at a time: opening
    /// another closes the first. The "which row is editing" flag this used to sit beside is
    /// gone with it - the popup's own open state is that flag, kept where ImGui keeps it.
    /// </remarks>
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

    /// <summary>
    /// Draws the rows as a table, and writes down whatever has settled.
    /// </summary>
    /// <remarks>
    /// A TABLE BECAUSE EVERY ROW IS THE SAME ROW. A tick, a swatch, a name, a size, an icon and
    /// a reset - forty times over on the markers page. Laid out with SameLine and a column stop
    /// they still drifted, because what precedes the size is a NAME and names are different
    /// lengths: "Route" and "Where a route starts" put their sliders in different places unless
    /// something holds a column, and the reset button only exists on rows that have been
    /// changed, so it moved everything after it on some rows and not others.
    ///
    /// Real columns hold. They also make the SHAPE of the list legible: a row with no size
    /// slider now has an empty cell where the sliders are, which reads as "this one has no
    /// size" instead of as a row that stops early.
    ///
    /// ONE TABLE FOR EVERY GROUP ON THE PAGE, not one per group. Column widths are measured per
    /// table, so a table per group would line each group up with itself and with nothing else -
    /// which is the same ragged edge one level up. The group names are rows inside the one
    /// table.
    /// </remarks>
    public void Draw()
    {
        // Sizes are per column and the name takes the slack, so the sliders sit at the same x
        // whatever the longest name in the list happens to be.
        if (!ImGui.BeginTable(
                "##style-rows",
                Columns,
                ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.NoSavedSettings))
        {
            Settle();
            return;
        }

        try
        {
            ImGui.TableSetupColumn("##on");
            ImGui.TableSetupColumn("##colour");
            ImGui.TableSetupColumn("##name", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("##size");
            ImGui.TableSetupColumn("##icon");
            ImGui.TableSetupColumn("##reset");

            foreach (string group in _groups)
            {
                IGrouping<string, StyleEntry>? entries =
                    StyleCatalogue.Grouped().FirstOrDefault(g => g.Key == group);
                if (entries is null)
                {
                    continue;
                }

                // A HEADING ROW rather than a fold. The groups are three or four rows each once
                // they are spread over four tabs, and a fold over three rows is a click to
                // reveal what would have fitted anyway. Only when there are several: one group
                // is named by the tab it is on.
                if (_groups.Length > 1)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TableNextColumn();
                    ImGui.TableNextColumn();
                    ImGui.TextColored(OverlayInk.Accent, group);
                }

                foreach (StyleEntry entry in entries)
                {
                    DrawRow(entry);
                }
            }
        }
        finally
        {
            ImGui.EndTable();
        }

        // After the content, so a drag that ended this frame is written down now rather
        // than waiting for whatever happens next - including the tab being switched away.
        Settle();
    }

    /// <summary>What a row is made of: on, colour, name, size, icon, reset.</summary>
    private const int Columns = 6;

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
        if (ImGui.SmallButton("Reset All") && changed > 0)
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

    /// <summary>One drawn thing, as one row of the table: on, colour, name, size, icon, reset.</summary>
    /// <remarks>
    /// EVERY CELL IS CLAIMED even when the entry has nothing to put in it, because a table lays
    /// out by cell and a skipped one shifts everything after it into the wrong column. An entry
    /// with no colour leaves an empty swatch cell rather than sliding its name left.
    /// </remarks>
    private void DrawRow(StyleEntry entry)
    {
        // ### rather than a plain label: ImGui derives a control's identity from its label, and
        // two entries sharing a word would collapse into one control.
        ImGui.PushID(entry.Key);

        LayerStyle style = _style[entry.Key];
        LayerStyle wanted = style;

        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        bool visible = style.Visible;
        if (ImGui.Checkbox("###show", ref visible))
        {
            wanted = wanted with { Hidden = !visible };
        }

        ImGui.TableNextColumn();
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
        }

        ImGui.TableNextColumn();

        // A marker's shape, at the size it is drawn on the large map, in the chosen colour, in
        // front of the name like an icon in a list. The row otherwise says "Breach" and a
        // swatch, which is the two things somebody reading it already knows.
        if (PreviewGlyph(entry.Key) is PoiGlyph glyph)
        {
            Preview(glyph, style.ColourOr(entry.Fallback), style.Sized(6f), style.WidthOr(0f));
            ImGui.SameLine();
        }

        ImGui.TextUnformatted(entry.Label);

        ImGui.TableNextColumn();
        wanted = DrawSizes(entry, wanted);

        ImGui.TableNextColumn();
        if (entry.Traits.HasFlag(StyleTraits.Icon))
        {
            wanted = DrawIcon(entry, wanted);
        }

        // ITS OWN COLUMN, so a row that has been changed does not push its neighbours' controls
        // sideways. Shown only when there is something to unset, so an untouched list is a list
        // of names rather than a wall of buttons - but the column is there either way, which is
        // what stops the rows disagreeing about where anything is.
        ImGui.TableNextColumn();
        if (!style.SaysNothing)
        {
            if (ImGui.SmallButton("Reset"))
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
    /// The size and line-width sliders, in the row's own size cell.
    /// </summary>
    /// <remarks>
    /// BOTH IN ONE CELL, because they are one question - how big is it drawn - asked two ways
    /// for two kinds of thing. A marker gets a scale, a line gets a width, and a few get both;
    /// giving each its own column would leave one of them empty on nearly every row.
    /// </remarks>
    private static LayerStyle DrawSizes(StyleEntry entry, LayerStyle wanted)
    {
        if (entry.Traits.HasFlag(StyleTraits.Scale))
        {
            // Starts at the ordinary size rather than at zero, so dragging it is an
            // adjustment from what is on screen rather than from nothing.
            float scale = wanted.Scale > 0f ? wanted.Scale : 1f;
            if (OverlayLayout.Narrow.Slider("###scale", ref scale, 0.3f, 4f, "x%.2f"))
            {
                wanted = wanted with { Scale = Math.Abs(scale - 1f) < 0.001f ? 0f : scale };
            }

            OverlayLayout.Hint("How big the marker is drawn, against its ordinary size.");

            if (entry.Traits.HasFlag(StyleTraits.Width))
            {
                ImGui.SameLine();
            }
        }

        if (entry.Traits.HasFlag(StyleTraits.Width))
        {
            float width = wanted.Width;

            // Zero is a real value here and it means "scale it with the marker", which is
            // what a line should do by default - so the format says so rather than showing a
            // meaningless 0.0.
            if (OverlayLayout.Narrow.Slider(
                    "###width", ref width, 0f, 8f, width <= 0f ? "as it comes" : "%.1f px"))
            {
                wanted = wanted with { Width = width };
            }

            OverlayLayout.Hint("How thick the line is. At zero it scales with the marker.");
        }

        return wanted;
    }


    /// <summary>The icon box: a path to a picture to draw instead of the built-in shape.</summary>
    /// <remarks>
    /// Behind a button rather than always on screen, because it is the least-used of these by
    /// a wide margin and a text field per row would triple the window for it.
    /// </remarks>
    private LayerStyle DrawIcon(StyleEntry entry, LayerStyle wanted)
    {
        bool has = !string.IsNullOrEmpty(wanted.Icon);

        // A POPUP, not a field that unfolds in place. In a table a cell has one column's width,
        // and a file path is longer than any column here would ever be - unfolded in the cell it
        // either squeezed to nothing or pushed the column wide for every other row in the list.
        // A popup floats over the table at whatever width it needs and takes none of it.
        if (ImGui.SmallButton(has ? "Icon *" : "Icon"))
        {
            _iconPath = wanted.Icon ?? string.Empty;
            ImGui.OpenPopup("icon");
        }

        OverlayLayout.Hint(
            has
                ? $"Drawn as {Path.GetFileName(wanted.Icon)} instead of the built-in shape."
                : "Draw a picture instead of the built-in shape.");

        if (!ImGui.BeginPopup("icon"))
        {
            return wanted;
        }

        try
        {
            // A width said out loud, because a popup sizes itself to its contents and a text
            // box asked to fill "what is left" inside one has nothing to measure against.
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 24f);
            ImGui.InputTextWithHint(
                "###iconpath", "a .png next to the tool, or a full path...", ref _iconPath, 512);

            OverlayLayout.Note("A .png next to the tool, or any full path. Missing files draw the shape.");

            int pressed = OverlayLayout.Actions("Use", "None");
            if (pressed == 0)
            {
                wanted = wanted with { Icon = _iconPath.Trim() };
                ImGui.CloseCurrentPopup();
            }
            else if (pressed == 1)
            {
                wanted = wanted with { Icon = string.Empty };
                ImGui.CloseCurrentPopup();
            }
        }
        finally
        {
            ImGui.EndPopup();
        }

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
