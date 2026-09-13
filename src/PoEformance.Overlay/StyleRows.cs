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
///
/// THE ROW LEADS WITH WHAT THE THING LOOKS LIKE. The preview cell draws the marker as it is
/// actually drawn on the map - the chosen sheet cell if there is one, the built-in shape if
/// there is not, in the row's own colour and at its own size - so picking an icon is answered
/// on the spot instead of by closing the window and going to look. That is also why the icon
/// button carries the cell rather than the word "Icon": a row that has been given a picture
/// says so by showing it.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class StyleRows
{
    private static readonly Vector4 DimText = OverlayInk.Quiet;

    /// <summary>What a row is made of: on, colour, preview and name, icon, size, reset.</summary>
    private const int Columns = 6;

    /// <summary>The preview and icon cells' edge, in ems of the current font.</summary>
    /// <remarks>
    /// In ems rather than pixels so the rows keep their proportions when the overlay font is
    /// scaled, which it is - the tool is used at 1080p and at 4K.
    /// </remarks>
    private const float CellEms = 1.6f;

    /// <summary>How far one press of + or - moves a size, as a fraction of the ordinary one.</summary>
    /// <remarks>
    /// Five per cent: small enough that the steppers are how a size is TUNED rather than a
    /// coarse switch, and large enough that holding one gets somewhere. The slider is gone
    /// from this row because a drag in a table cell is a drag in a column two characters wide.
    /// </remarks>
    private const float SizeStep = 0.05f;

    private readonly OverlayStyle _style;
    private readonly Action _save;
    private readonly string[] _groups;
    private readonly Func<IconCache.Picture> _sheet;

    /// <summary>
    /// The path being typed, while a plate popup is open.
    /// </summary>
    /// <remarks>
    /// One field for every row, which is safe because ImGui allows one popup at a time: opening
    /// another closes the first. The "which row is editing" flag this used to sit beside is
    /// gone with it - the popup's own open state is that flag, kept where ImGui keeps it.
    /// </remarks>
    private string _platePath = string.Empty;

    /// <summary>The size the Apply button would set on every row of the page, as a percentage.</summary>
    private int _setAll = 100;

    // Something changed and has not been written down yet - see Settle.
    private bool _unsaved;

    /// <param name="save">Writes the style down. Called when a change has SETTLED, not per frame.</param>
    /// <param name="groups">The catalogue groups this block shows, in display order.</param>
    /// <param name="sheet">
    /// The icon sheet the rows draw their previews from. Asked per frame rather than held,
    /// because the cache can be told to forget everything and hand back new textures.
    /// </param>
    public StyleRows(OverlayStyle style, Action save, string[] groups, Func<IconCache.Picture> sheet)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(save);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(sheet);
        _style = style;
        _save = save;
        _groups = groups;
        _sheet = sheet;
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
    /// stepper now has an empty cell where the steppers are, which reads as "this one has no
    /// size" instead of as a row that stops early.
    ///
    /// ONE TABLE FOR EVERY GROUP ON THE PAGE, not one per group. Column widths are measured per
    /// table, so a table per group would line each group up with itself and with nothing else -
    /// which is the same ragged edge one level up. The group names are rows inside the one
    /// table.
    /// </remarks>
    public void Draw()
    {
        DrawSetAllLine();

        // Sizes are per column and the name takes the slack, so the steppers sit at the same x
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
            ImGui.TableSetupColumn("##icon");
            ImGui.TableSetupColumn("##size");
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

    /// <summary>
    /// The one control that sets every size on the page at once.
    /// </summary>
    /// <remarks>
    /// Because the answer to "these are all too small" is one decision and forty adjustments.
    /// It only touches the rows that HAVE a size - a line has a width and no scale, and setting
    /// its size to 130% would be setting a field nothing reads.
    /// </remarks>
    private void DrawSetAllLine()
    {
        StyleEntry[] sizeable = Sizeable();
        if (sizeable.Length == 0)
        {
            return;
        }

        ImGui.TextColored(DimText, "Set all sizes");
        ImGui.SameLine();

        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 5f);
        ImGui.InputInt("###setall", ref _setAll, 5);
        _setAll = Math.Clamp(_setAll, 30, 400);
        OverlayLayout.Hint("Per cent of each thing's ordinary size. 100 puts them back.");

        ImGui.SameLine();
        if (ImGui.SmallButton("Apply"))
        {
            float scale = _setAll / 100f;
            foreach (StyleEntry entry in sizeable)
            {
                // Exactly what the row's own stepper stores, zero included: at 100% this
                // CLEARS the scale rather than pinning it, so a default corrected in a later
                // release still reaches somebody who pressed Apply once.
                _style.Set(entry.Key, _style[entry.Key] with { Scale = Near(scale, 1f) ? 0f : scale });
            }

            Changed();
        }

        ImGui.Separator();
    }

    /// <summary>The entries on this page that have a size to set.</summary>
    private StyleEntry[] Sizeable()
        => [.. StyleCatalogue.Entries
            .Where(entry => _groups.Contains(entry.Group, StringComparer.Ordinal))
            .Where(entry => entry.Traits.HasFlag(StyleTraits.Scale))];

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

    /// <summary>One drawn thing, as one row: on, colour, preview and name, icon, size, reset.</summary>
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
        DrawPreview(entry, style);
        ImGui.SameLine();
        ImGui.TextUnformatted(entry.Label);

        ImGui.TableNextColumn();
        if (entry.Traits.HasFlag(StyleTraits.Icon))
        {
            wanted = DrawIcon(wanted);
        }
        else if (entry.Traits.HasFlag(StyleTraits.Plate))
        {
            wanted = DrawPlate(wanted);
        }

        ImGui.TableNextColumn();
        wanted = DrawSizes(entry, wanted);

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
    /// What the thing looks like on the map, in front of its name.
    /// </summary>
    /// <remarks>
    /// THE CHOSEN CELL WHEN THERE IS ONE, and the built-in shape when there is not - which is
    /// exactly what the map does, so the row and the map cannot disagree. This is the half of
    /// "show me what I picked" that matters: the icon button says WHICH cell, this says what
    /// the marker will actually look like once its colour and size are applied to it.
    ///
    /// Its own space is claimed on the line first, because ImGui lays out from what a widget
    /// SAYS it occupies rather than from what was painted - without the claim the shape paints
    /// over the next row's text.
    /// </remarks>
    private void DrawPreview(StyleEntry entry, LayerStyle style)
    {
        float box = ImGui.GetFontSize() * CellEms;
        Vector2 at = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(box, box));

        uint colour = style.ColourOr(entry.Fallback);
        ImDrawListPtr draw = ImGui.GetWindowDrawList();

        // Inset a little, so a square icon does not touch the row above and below.
        float pad = box * 0.1f;
        if (SheetIcon.Draw(
                draw,
                _sheet(),
                style,
                at + new Vector2(pad, pad),
                at + new Vector2(box - pad, box - pad),
                string.IsNullOrEmpty(style.Colour) ? 0xFFFFFFFF : colour))
        {
            return;
        }

        if (PreviewGlyph(entry.Key) is PoiGlyph glyph)
        {
            PoiGlyphPainter.Draw(
                draw,
                at + new Vector2(box / 2f, box / 2f),
                Math.Min(style.Sized(6f), box / 2f),
                colour,
                glyph,
                style.WidthOr(0f));
        }
    }

    /// <summary>
    /// The size steppers, in the row's own size cell.
    /// </summary>
    /// <remarks>
    /// A NUMBER WITH TWO BUTTONS rather than a slider, because a slider inside a table cell is
    /// a drag across a column two characters wide: the value jumps, and putting it back means
    /// another jump. Per cent of the ordinary size rather than pixels, because what the scale
    /// multiplies is NOT one number - a marker's base is a few pixels, the atlas entry's is
    /// derived from the node width, the flask figure's from the slot height - so an absolute
    /// pixel value here would mean something different on every row.
    ///
    /// A line's WIDTH keeps its slider, and shares this cell: it is a genuine pixel count with
    /// a small range, and it is on a handful of rows rather than all of them.
    /// </remarks>
    private static LayerStyle DrawSizes(StyleEntry entry, LayerStyle wanted)
    {
        if (entry.Traits.HasFlag(StyleTraits.Scale))
        {
            // Starts at the ordinary size rather than at zero, so stepping it is an
            // adjustment from what is on screen rather than from nothing.
            float scale = wanted.Scale > 0f ? wanted.Scale : 1f;

            if (ImGui.SmallButton("-"))
            {
                scale -= SizeStep;
            }

            ImGui.SameLine();

            // TextUnformatted rather than Text, so the per cent sign is a per cent sign: the
            // formatting calls hand the string to printf, where it would start a conversion
            // specifier - see ImGuiText. Unformatted takes no format string, so it needs no
            // doubling either, and a doubled one here would print two of them.
            ImGui.TextUnformatted($"{scale * 100f,3:0}%");
            ImGui.SameLine();

            if (ImGui.SmallButton("+"))
            {
                scale += SizeStep;
            }

            OverlayLayout.Hint("How big it is drawn, against its ordinary size.");

            scale = Math.Clamp(scale, 0.3f, 4f);

            // Stored as zero at the ordinary size rather than as 1.0, so the entry says
            // nothing and a default corrected in a later release still reaches you.
            float stored = Near(scale, 1f) ? 0f : scale;
            if (!Near(stored, wanted.Scale))
            {
                wanted = wanted with { Scale = stored };
            }

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

    /// <summary>Two sizes that are the same size, to within what a stepper can express.</summary>
    private static bool Near(float a, float b) => Math.Abs(a - b) < 0.001f;

    /// <summary>
    /// The icon button: the chosen cell of the sheet, and the picker behind it.
    /// </summary>
    /// <remarks>
    /// THE BUTTON IS THE ICON. A row that has been given a picture shows it here rather than
    /// saying "Icon *", which is the difference between reading the list and decoding it.
    ///
    /// A POPUP for the grid rather than something that unfolds in place: in a table a cell has
    /// one column's width, and the grid is fourteen cells across. Unfolded in the cell it would
    /// push that column wide for every other row in the list.
    /// </remarks>
    private LayerStyle DrawIcon(LayerStyle wanted)
    {
        IconCache.Picture sheet = _sheet();
        float box = ImGui.GetFontSize() * CellEms;

        bool pressed;
        if (wanted.HasIcon && sheet.Ready)
        {
            (Vector2 uv0, Vector2 uv1) = SheetIcon.Uv(wanted.IconIndex, sheet);
            pressed = ImGui.ImageButton(
                "###icon", sheet.Texture, new Vector2(box, box), uv0, uv1,
                new Vector4(0f, 0f, 0f, 0f), Vector4.One);
        }
        else
        {
            pressed = ImGui.SmallButton("Icon");
        }

        if (pressed)
        {
            ImGui.OpenPopup("icon");
        }

        OverlayLayout.Hint(
            wanted.HasIcon
                ? $"Drawn as cell {wanted.IconTile} of the icon sheet."
                : "Draw a picture from the icon sheet instead of the built-in shape.");

        if (!ImGui.BeginPopup("icon"))
        {
            return wanted;
        }

        try
        {
            int chosen = IconPicker.Draw(sheet, wanted.IconTile);
            if (chosen != IconPicker.Unchanged)
            {
                wanted = wanted with { IconTile = chosen };
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
    /// The plate box: a path to a picture to draw behind something drawn large.
    /// </summary>
    /// <remarks>
    /// STILL A PATH, where the markers are not, and the difference is the size it is drawn at.
    /// A plate is most of the screen wide with proportions of its own to keep; a cell off the
    /// 64-pixel sheet stretched that far is a smear. So this one keeps a file, and with it the
    /// one failure mode the markers no longer have - the file can be gone, which draws the
    /// plate that shipped.
    ///
    /// Behind a button rather than always on screen, because it is on exactly one row and a
    /// text field per row would widen the column for all of them.
    /// </remarks>
    private LayerStyle DrawPlate(LayerStyle wanted)
    {
        bool has = !string.IsNullOrEmpty(wanted.Plate);

        if (ImGui.SmallButton(has ? "Plate *" : "Plate"))
        {
            _platePath = wanted.Plate ?? string.Empty;
            ImGui.OpenPopup("plate");
        }

        OverlayLayout.Hint(
            has
                ? $"Drawn on {Path.GetFileName(wanted.Plate)} instead of the one that ships."
                : "Draw this on a picture of your own instead of the one that ships.");

        if (!ImGui.BeginPopup("plate"))
        {
            return wanted;
        }

        try
        {
            // A width said out loud, because a popup sizes itself to its contents and a text
            // box asked to fill "what is left" inside one has nothing to measure against.
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 24f);
            ImGui.InputTextWithHint(
                "###platepath", "a .png next to the tool, or a full path...", ref _platePath, 512);

            OverlayLayout.Note("A .png next to the tool, or any full path. Missing files draw the one that ships.");

            int pressed = OverlayLayout.Actions("Use", "None");
            if (pressed == 0)
            {
                wanted = wanted with { Plate = _platePath.Trim() };
                ImGui.CloseCurrentPopup();
            }
            else if (pressed == 1)
            {
                wanted = wanted with { Plate = string.Empty };
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
}
