using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;

namespace PoEformance.Overlay;

/// <summary>
/// What this area contains, kept in the corner for as long as you are in it.
/// </summary>
/// <remarks>
/// THE CARD ON THE WAY IN IS NOT ENOUGH, and the reason is what preload findings ARE. An
/// entity alert is an event: something appeared, you look, it is over. What an area loaded is
/// a property of the place - true before you walked in, true until you leave - and the moment
/// you most want it is not the entrance but the one where you are deciding whether to clear
/// the far half. A card that faded four minutes ago cannot answer that.
///
/// A REAL WINDOW rather than something painted on, and that is the one deliberate cost here.
/// Painted pixels cannot be clicked - this overlay only takes the mouse where ImGui has
/// something under it - so "close it when it is in the way" is impossible without a window.
/// The price is a small dead spot where the game will not receive clicks, which is also why it
/// is draggable: put it somewhere you do not click, and it stops mattering.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class PreloadPanel
{
    private const ImGuiWindowFlags Flags =
        ImGuiWindowFlags.NoTitleBar
        | ImGuiWindowFlags.AlwaysAutoResize
        | ImGuiWindowFlags.NoFocusOnAppearing
        | ImGuiWindowFlags.NoNav
        | ImGuiWindowFlags.NoScrollbar
        | ImGuiWindowFlags.NoCollapse;

    private uint _closed;

    /// <summary>How every drawn thing looks. Shared with the overlay.</summary>
    public OverlayStyle Style { get; set; } = new();

    /// <summary>Whether the list is wanted at all - the user's setting, not a style.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Draws the list for an area, when there is something in it.
    /// </summary>
    /// <param name="area">
    /// Which area these belong to. Closing is remembered against it, so dismissing the list
    /// here does not dismiss it for the next map - "in the way right now" and "I never want
    /// this" are different requests, and the second one is the setting.
    /// </param>
    /// <remarks>
    /// Everything found goes in, including the one-file findings the entry card leaves out.
    /// The count beside each name is the whole story: "Breach 43" is the mechanic being here
    /// and "Delirium 1" is a mention somewhere, and a reader tells those apart at a glance
    /// without the list having to decide for them.
    /// </remarks>
    public void Draw(uint area, IReadOnlyList<PreloadFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        if (!Enabled || findings.Count == 0 || (area != 0 && area == _closed))
        {
            return;
        }

        List<PreloadFinding> shown =
            [.. findings.Where(finding => Style.Visible(StyleCatalogue.ForWeight(finding.Weight)))];

        if (shown.Count == 0)
        {
            return;
        }

        Vector2 screen = ImGui.GetIO().DisplaySize;
        ImGui.SetNextWindowPos(new Vector2(screen.X * 0.015f, screen.Y * 0.22f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowBgAlpha(Alpha());

        if (!ImGui.Begin("What loaded###preload-corner", Flags))
        {
            ImGui.End();
            return;
        }

        // End in a finally: an exception between Begin and End leaves ImGui's stack unbalanced
        // and the assert that follows takes the process down.
        try
        {
            foreach (PreloadFinding finding in shown)
            {
                string key = StyleCatalogue.ForWeight(finding.Weight);
                ImGui.TextColored(Unpack(Style.Colour(key)), $"{finding.Name}  {finding.Files}");
            }

            // Under the list rather than in a corner of it: the window sizes itself to its
            // contents, so a button pinned to the right edge would move with the longest
            // name and be somewhere different in every area.
            if (ImGui.SmallButton("close for this map"))
            {
                _closed = area;
            }
        }
        finally
        {
            ImGui.End();
        }
    }

    /// <summary>How solid the window's backing is, from the style entry for it.</summary>
    /// <remarks>
    /// ImGui wants the background as an alpha on the window rather than as a colour in the
    /// draw list, so the catalogue entry's alpha is what is used and its hue is ignored. It is
    /// still one entry to change, which is what matters for it being findable.
    /// </remarks>
    private float Alpha()
    {
        uint back = Style.Colour(StyleCatalogue.Keys.PreloadListBack);
        return ((back >> 24) & 0xFF) / 255f;
    }

    /// <summary>An ImGui colour from a packed one.</summary>
    private static Vector4 Unpack(uint colour) => ImGui.ColorConvertU32ToFloat4(colour);
}
