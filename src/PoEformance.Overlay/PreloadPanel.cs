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

    /// <summary>The id this window's lock and click-through are filed under.</summary>
    public const string ChromeId = "preload";

    /// <summary>Whether this window is pinned in place or handed to the mouse.</summary>
    public WindowChrome Chrome { get; set; } = new();

    /// <summary>Whether the list is wanted at all - the user's setting, not a style.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Whether an empty window is taken away rather than left as a bare timer.</summary>
    public bool HideWhenEmpty { get; set; }

    /// <summary>
    /// Draws the list for an area, when there is something in it.
    /// </summary>
    /// <param name="area">
    /// Which area these belong to. Closing is remembered against it, so dismissing the list
    /// here does not dismiss it for the next map - "in the way right now" and "I never want
    /// this" are different requests, and the second one is the setting.
    /// </param>
    /// <param name="found">What this area turned out to hold, in the list's own order.</param>
    /// <param name="sinceMs">
    /// How long since the last area loaded, or negative to leave the timer out. It is the one
    /// thing here that is worth reading when nothing was found: a map that has been open eleven
    /// minutes is a different decision from one opened just now.
    /// </param>
    /// <param name="stale">
    /// Whether the file list belongs somewhere other than where you are standing - town and
    /// hideout, where it is not refreshed. Said rather than hidden, because a window that
    /// silently shows the LAST area's contents is worse than one that admits it.
    /// </param>
    public void Draw(uint area, IReadOnlyList<PreloadAlertEntry> found, long sinceMs = -1, bool stale = false)
    {
        ArgumentNullException.ThrowIfNull(found);

        if (!Enabled || (area != 0 && area == _closed))
        {
            return;
        }

        // Nothing to say and nothing to say it with: no entries, no timer, no warning. The
        // window would be an empty box, which is worse than not being there.
        bool empty = found.Count == 0 && !stale;
        if (empty && (HideWhenEmpty || sinceMs < 0))
        {
            return;
        }

        // Out of the way while one of the game's panels is underneath it - see
        // WindowChrome.Covered. Not remembered as closed: the panel shutting brings it back.
        if (Chrome.Covered(ChromeId))
        {
            return;
        }

        Vector2 screen = ImGui.GetIO().DisplaySize;
        ImGui.SetNextWindowPos(new Vector2(screen.X * 0.015f, screen.Y * 0.22f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowBgAlpha(Alpha());

        bool expanded = ImGui.Begin("What loaded###preload-corner", Chrome.Flags(ChromeId, Flags));

        // Before the early return, not after it: a collapsed window is still on screen, and
        // where it sits is what decides next frame whether it is over a panel.
        Chrome.Measure(ChromeId);

        if (!expanded)
        {
            ImGui.End();
            return;
        }

        // End in a finally: an exception between Begin and End leaves ImGui's stack unbalanced
        // and the assert that follows takes the process down.
        try
        {
            if (sinceMs >= 0)
            {
                ImGui.TextDisabled($"{sinceMs / 1000f:00.0}s");
                ImGui.Separator();
            }

            if (stale)
            {
                // The reference says the same thing in the same place, and for the same reason:
                // the game does not reload the file table in town, so what is held is the last
                // real area. Naming that is the difference between a stale window and a lying one.
                ImGui.TextDisabled("not updated here");
            }

            foreach (PreloadAlertEntry entry in found)
            {
                ImGui.TextColored(Unpack(entry.Colour), entry.Shown);
            }

            // Under the list rather than in a corner of it: the window sizes itself to its
            // contents, so a button pinned to the right edge would move with the longest
            // name and be somewhere different in every area.
            if (ImGui.SmallButton("close for this map"))
            {
                _closed = area;
            }

            // LAST, after the contents. The menu declines to open over a control, and what is
            // under the cursor is only known once the controls have been submitted.
            Chrome.Menu(ChromeId);
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
