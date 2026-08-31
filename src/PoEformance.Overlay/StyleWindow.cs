using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;

namespace PoEformance.Overlay;

/// <summary>
/// How the tool ITSELF looks: its text, its panels, its windows, its tabs.
/// </summary>
/// <remarks>
/// This page used to hold every style row the overlay had as well, and it had become a wall:
/// twelve groups of colour pickers, most about a feature configured on some other tab
/// entirely. Those rows now live with their features - see <see cref="StyleRows"/> and
/// <see cref="StyleCatalogue.Homes"/> - and what is left here is exactly the part that is
/// about no feature in particular: the interface's own size and solidity, which window is
/// pinned or click-through, and which tabs are on the bar.
///
/// It stays the page that can never be hidden, because two of its lists are the only way
/// back: a click-through window cannot offer its own menu, and a hidden tab cannot offer its
/// own checkbox.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class StyleWindow
{
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
    /// At the TOP of the page for a plain reason: somebody who cannot read the interface
    /// cannot go looking through it for the control that fixes that.
    /// </remarks>
    public InterfaceEditor? Interface { get; set; }

    // A change made and not yet written down - see Settle.
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

    /// <summary>Writes down a change once nothing is being dragged any more.</summary>
    private void Settle()
    {
        if (ImGui.IsAnyItemActive())
        {
            return;
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
        DrawInterface();

        if (Chrome is not null && OverlayLayout.Subsection("Windows", openByDefault: true))
        {
            Chrome.DrawList();
        }

        if (TabList is not null && OverlayLayout.Subsection("Tabs", openByDefault: true))
        {
            TabList();
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
            || !OverlayLayout.Subsection("Text and panels", openByDefault: true))
        {
            return;
        }

        InterfaceStyle now = editor.Now();

        OverlayLayout.Note(
            "How this tool's own windows are drawn. What it draws on the GAME is styled where"
            + " each feature lives - and on Markers.");
        ImGui.Spacing();

        int size = _draftTextSize > 0 ? _draftTextSize : now.TextSizeOr;
        if (OverlayLayout.Slider(
                "Text size", ref size, InterfaceStyle.MinTextSize, InterfaceStyle.MaxTextSize, "%d px"))
        {
            _draftTextSize = size;
        }

        // On release rather than on change - see the note on the draft. Deactivated-after-edit
        // covers the keyboard route too: ctrl-clicking a slider types into it, and that ends
        // the same way.
        //
        // BEFORE THE HINT, and that ordering is load-bearing rather than tidy: a tooltip is a
        // window, and drawing one leaves ImGui's "last item" pointing at the text inside it.
        // Asked afterwards, this would be asking whether a line of tooltip prose had just been
        // edited - which is never - so the text size would fail to commit in exactly the case
        // it matters, the one where the pointer is still over the slider that was just let go.
        if (ImGui.IsItemDeactivatedAfterEdit() && _draftTextSize > 0)
        {
            if (_draftTextSize != now.TextSizeOr)
            {
                editor.Chose(now with { TextSize = _draftTextSize });
                _unsavedInterface = true;
            }

            _draftTextSize = 0;
        }

        OverlayLayout.Hint(
            "Everything else follows this - the padding, the indents and the heading face are all"
            + " ratios of it, so the interface stays in proportion at any size.");

        // Whole percent, not a 0-to-1 fraction. Both read the same to the code and only one of
        // them reads as anything to a person - and a slider labelled in percent can be
        // ctrl-clicked and typed into, which one labelled "0.85" cannot usefully be.
        int panels = Percent(now.PanelOpacityOr);
        if (OverlayLayout.Slider("Tool panels", ref panels, Floor, 100, "%d%% solid"))
        {
            editor.Chose(now with { PanelOpacity = panels / 100f });
            _unsavedInterface = true;
        }

        OverlayLayout.Hint("Every page except the live readout.");

        int readout = Percent(now.ReadoutOpacityOr);
        if (OverlayLayout.Slider("Readout", ref readout, Floor, 100, "%d%% solid"))
        {
            editor.Chose(now with { ReadoutOpacity = readout / 100f });
            _unsavedInterface = true;
        }

        OverlayLayout.Hint(
            "The Status page - the one that sits in a corner while playing, and the one worth"
            + " being able to see the game through.");

        // ITS OWN LINE, not stuck to the end of the paragraph above it with SameLine. That is
        // what this button used to be, and it put the button wherever the prose happened to
        // wrap - so dragging the window moved it.
        if (now != InterfaceStyle.Default && OverlayLayout.Actions("Reset text and panels") == 0)
        {
            editor.Chose(InterfaceStyle.Default);
            _draftTextSize = 0;
            _unsavedInterface = true;
        }

        ImGui.Spacing();
    }

    /// <summary>An opacity as whole percent, for the sliders that show it that way.</summary>
    private static int Percent(float value) => (int)MathF.Round(value * 100f);

    /// <summary>The lowest the opacity sliders go, in the same whole percent.</summary>
    private static readonly int Floor = Percent(InterfaceStyle.MinOpacity);
}
