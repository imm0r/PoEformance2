using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.World;

namespace PoEformance.Overlay;

/// <summary>
/// Changes how anything the overlay draws looks.
/// </summary>
/// <remarks>
/// GENERATED FROM THE CATALOGUE, not written out row by row, and that is the whole point. A
/// hand-written settings page is how a tool ends up with fourteen configurable things and
/// three that are not, with no way to tell which is which and no way to find out but reading
/// the drawing code. Here a new drawn thing appears in this window the moment it gets a
/// catalogue entry, and it cannot be drawn without one.
///
/// Each row offers exactly what its entry says it can change, so a marker gets a size and a
/// line does not, and nothing pretends to be adjustable when nothing reads it.
///
/// Over the game rather than in the configuration window, because a colour is chosen by
/// LOOKING at it - on the map, among the other markers, at the size it will actually be. A
/// picker in another window is a picker you use once and then go and check.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class StyleWindow
{
    private static readonly Vector4 DimText = OverlayTheme.Quiet;

    private readonly OverlayStyle _style;
    private readonly Action _save;

    /// <summary>
    /// Which windows are pinned in place or handed to the mouse, if anybody said.
    /// </summary>
    /// <remarks>
    /// Here rather than in a settings page of its own because this is the tab about how the
    /// overlay LOOKS, and where a window sits is part of that. It is also the only place a
    /// click-through window can be got back, so it has to be somewhere findable.
    /// </remarks>
    public WindowChrome? Chrome { get; set; }

    /// <summary>Draws the tab list, when the tools window has offered it.</summary>
    /// <remarks>
    /// Beside the window list for the same reason that one is here: which tabs are on the
    /// bar is part of how the overlay looks, and this page is the one that can never hide
    /// itself - so the way back from any hiding is always reachable.
    /// </remarks>
    public Action? TabList { get; set; }

    /// <summary>
    /// How the tool's own windows are drawn, when somebody has offered it for editing.
    /// </summary>
    /// <remarks>
    /// Three callbacks rather than the settings themselves, because the three things happen at
    /// different moments: the value is READ every frame (the overlay owns it, and the
    /// configuration window can change it too), a change is APPLIED at once so it can be
    /// judged by looking at it, and it is WRITTEN DOWN only once the drag has finished. Handing
    /// this window the record instead would collapse the second and third into one, which is
    /// sixty file writes a second for one adjustment.
    /// </remarks>
    /// <param name="Now">What the interface looks like at this moment.</param>
    /// <param name="Chose">Applies a change immediately.</param>
    /// <param name="Settled">Writes it down, once nothing is being dragged.</param>
    public sealed record InterfaceEditor(Func<InterfaceStyle> Now, Action<InterfaceStyle> Chose, Action Settled);

    /// <summary>The interface's own size and solidity, if anybody offered them.</summary>
    /// <remarks>
    /// Here, beside the marker colours, because "how the tool looks" is one question however
    /// many files it is kept in. It sits at the TOP of the page for a plainer reason: somebody
    /// who cannot read the interface cannot go looking through it for the control that fixes
    /// that.
    /// </remarks>
    public InterfaceEditor? Interface { get; set; }

    /// <summary>Which key's icon box is open. Only one at a time, to keep the rows short.</summary>
    private string _editingIcon = string.Empty;
    private string _iconPath = string.Empty;

    // Something changed and has not been written down yet - see Settle.
    private bool _unsaved;

    // The same, for the interface settings - which live in a different file with a different
    // writer, so one flag could not say which of the two is waiting.
    private bool _unsavedInterface;

    /// <summary>
    /// A text size being dragged, before it is committed. 0 when nothing is being dragged.
    /// </summary>
    /// <remarks>
    /// The one setting here that is NOT applied while the slider moves, and the exception is
    /// paid for: a new size means building a new font atlas and uploading a new texture, and
    /// doing that on every frame of a drag across eighteen values is a visible stall. So the
    /// draft is what the slider shows, and the size only really changes when the mouse is let
    /// go.
    /// </remarks>
    private int _draftTextSize;

    /// <param name="save">Writes the style down. Called when a change has SETTLED, not per frame.</param>
    public StyleWindow(OverlayStyle style, Action save)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(save);
        _style = style;
        _save = save;
    }

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

        if (_unsavedInterface)
        {
            _unsavedInterface = false;
            Interface?.Settled();
        }
    }

    /// <summary>Draws the tab's content.</summary>
    public void DrawTab()
    {
        Draw();

        // After the content, so a drag that ended this frame is written down now rather
        // than waiting for whatever happens next - including the tab being switched away.
        Settle();
    }

    /// <summary>
    /// While the tab is not in front: a change made and left behind still lands.
    /// </summary>
    /// <remarks>
    /// The tab can be switched away from - or the whole window closed - with a change made
    /// and not yet written down, and "the last thing I did before leaving was the thing
    /// that got lost" is the worst way for a settings editor to behave.
    /// </remarks>
    public void Idle() => Settle();

    private void Draw()
    {
        int changed = _style.Changed.Count;
        ImGui.TextColored(
            DimText,
            changed == 0
                ? "everything as it comes - nothing changed yet"
                : $"{changed} changed. Everything else follows the defaults, so a corrected one reaches you.");

        ImGui.SameLine();
        if (ImGui.SmallButton("reset all") && changed > 0)
        {
            _style.ResetAll();
            Changed();
        }

        ImGui.Separator();

        // FIRST, above the colours, because these are the two things in here somebody arrives
        // looking for rather than browsing: an interface they cannot read, and a window that
        // has been made click-through - which cannot offer its own menu any more, so this
        // list is the only way back.
        DrawInterface();

        if (Chrome is not null && ImGui.CollapsingHeader("Windows", ImGuiTreeNodeFlags.DefaultOpen))
        {
            Chrome.DrawList();
        }

        if (TabList is not null && ImGui.CollapsingHeader("Tabs", ImGuiTreeNodeFlags.DefaultOpen))
        {
            TabList();
        }

        foreach (IGrouping<string, StyleEntry> group in StyleCatalogue.Grouped())
        {
            if (!ImGui.CollapsingHeader(group.Key, ImGuiTreeNodeFlags.DefaultOpen))
            {
                continue;
            }

            foreach (StyleEntry entry in group)
            {
                DrawRow(entry);
            }
        }
    }

    /// <summary>The interface's own text size and how solid its windows are.</summary>
    /// <remarks>
    /// THREE CONTROLS AND NO PALETTE. The colours of the tool's own windows are one decision
    /// made once (see <see cref="OverlayTheme"/>) and offering them here would be a theme
    /// editor - a large thing to build, maintain and get wrong, for a question almost nobody
    /// asks. What people actually cannot read is text that is too small and a panel with the
    /// game showing through it, and both of those depend on the screen rather than on taste,
    /// so they are the two that have to be adjustable.
    /// </remarks>
    private void DrawInterface()
    {
        if (Interface is not InterfaceEditor editor
            || !ImGui.CollapsingHeader("Text and panels", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        InterfaceStyle now = editor.Now();

        ImGui.TextColored(DimText, "How this tool's own windows are drawn. Everything below is");
        ImGui.TextColored(DimText, "about what it draws on the GAME.");
        ImGui.Spacing();

        int size = _draftTextSize > 0 ? _draftTextSize : now.TextSizeOr;
        ImGui.SetNextItemWidth(200f);
        if (ImGui.SliderInt("text size", ref size, InterfaceStyle.MinTextSize, InterfaceStyle.MaxTextSize, "%d px"))
        {
            _draftTextSize = size;
        }

        // On release rather than on change - see the note on the draft. Deactivated-after-edit
        // covers the keyboard route too: ctrl-clicking a slider types into it, and that ends
        // the same way.
        if (ImGui.IsItemDeactivatedAfterEdit() && _draftTextSize > 0)
        {
            if (_draftTextSize != now.TextSizeOr)
            {
                editor.Chose(now with { TextSize = _draftTextSize });
                _unsavedInterface = true;
            }

            _draftTextSize = 0;
        }

        // Whole percent, not a 0-to-1 fraction. Both read the same to the code and only one of
        // them reads as anything to a person - and a slider labelled in percent can be
        // ctrl-clicked and typed into, which one labelled "0.85" cannot usefully be.
        int panels = Percent(now.PanelOpacityOr);
        ImGui.SetNextItemWidth(200f);
        if (ImGui.SliderInt("tool panels", ref panels, Floor, 100, "%d%% solid"))
        {
            editor.Chose(now with { PanelOpacity = panels / 100f });
            _unsavedInterface = true;
        }

        int readout = Percent(now.ReadoutOpacityOr);
        ImGui.SetNextItemWidth(200f);
        if (ImGui.SliderInt("the readout", ref readout, Floor, 100, "%d%% solid"))
        {
            editor.Chose(now with { ReadoutOpacity = readout / 100f });
            _unsavedInterface = true;
        }

        ImGui.TextColored(DimText, "The readout is the first page - the one that sits in a corner");
        ImGui.TextColored(DimText, "while playing. The tools are every other page.");

        if (now != InterfaceStyle.Default)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("reset text and panels"))
            {
                editor.Chose(InterfaceStyle.Default);
                _draftTextSize = 0;
                _unsavedInterface = true;
            }
        }

        ImGui.Spacing();
    }

    /// <summary>An opacity as whole percent, for the sliders that show it that way.</summary>
    private static int Percent(float value) => (int)MathF.Round(value * 100f);

    /// <summary>The lowest the opacity sliders go, in the same whole percent.</summary>
    private static readonly int Floor = Percent(InterfaceStyle.MinOpacity);

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
            ImGui.SetNextItemWidth(120f);
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
            ImGui.SetNextItemWidth(120f);

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

        ImGui.SetNextItemWidth(300f);
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
