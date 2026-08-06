using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;

namespace PoEformance.Overlay;

/// <summary>
/// What this area contains, kept in the corner for as long as you are in it.
/// </summary>
/// <remarks>
/// THE BANNER IS NOT ENOUGH, and the reason is what preload findings ARE. An entity alert is
/// an event - something appeared, you look, it is over. What an area loaded is a property of
/// the place: it was true before you walked in and stays true until you leave, and the moment
/// you most want it is not the moment you entered but the one where you are deciding whether
/// to clear the far half. A line that faded out four minutes ago cannot answer that.
///
/// So the banner says it once, loudly, and this says it quietly for as long as it is true.
/// Both are switchable on their own, because which of the two somebody wants is taste rather
/// than a fact about the feature.
///
/// Drawn rather than put in a window, for the same reason the banner is: a window has a title
/// bar, takes focus, and would have to be dragged out of the way of the fight.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class PreloadPanel
{
    /// <summary>Where the list starts, as a fraction of the screen.</summary>
    /// <remarks>
    /// Left edge and a fifth of the way down: clear of the banner above it, clear of the
    /// game's own flask and life furniture at the bottom, and on the side the in-game map is
    /// not on. It is not adjustable yet - if it lands badly on an ultrawide, that is the first
    /// thing to make a setting.
    /// </remarks>
    private const float FromLeft = 0.015f;
    private const float FromTop = 0.22f;

    private const float RowHeight = 18f;
    private const float PadX = 10f;
    private const float PadY = 7f;

    /// <summary>How every drawn thing looks. Shared with the overlay.</summary>
    public OverlayStyle Style { get; set; } = new();

    /// <summary>Whether the list is wanted at all - the user's switch, not a style.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Draws the list, when there is something in it.
    /// </summary>
    /// <param name="findings">What the area turned out to contain, strongest first.</param>
    /// <remarks>
    /// Everything found goes in, including the one-file findings the banner leaves out. The
    /// count is beside each name and is the whole story: "Breach 43" is the mechanic being
    /// here and "Delirium 1" is a mention somewhere, and a reader can tell those apart at a
    /// glance without the list having to decide for them.
    /// </remarks>
    public void Draw(ImDrawListPtr draw, int width, int height, IReadOnlyList<PreloadFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        if (!Enabled || findings.Count == 0)
        {
            return;
        }

        // Hidden entries are dropped before anything is measured, so the backing plate fits
        // what is actually drawn rather than leaving a gap where a hidden weight would be.
        List<PreloadFinding> shown =
            [.. findings.Where(finding => Style.Visible(StyleCatalogue.ForWeight(finding.Weight)))];

        if (shown.Count == 0)
        {
            return;
        }

        var at = new Vector2(width * FromLeft, height * FromTop);

        // Measured with the same scale the drawing uses, and summed the same way it advances.
        // A plate sized from the row COUNT is right until somebody scales a weight up, and
        // then it clips the thing they made bigger precisely because it mattered.
        float widest = 0f;
        float rows = 0f;
        foreach (PreloadFinding finding in shown)
        {
            float scale = Style.Sized(StyleCatalogue.ForWeight(finding.Weight), 1f);
            widest = Math.Max(widest, ImGui.CalcTextSize(Line(finding)).X * scale);
            rows += RowHeight * scale;
        }

        draw.AddRectFilled(
            at - new Vector2(PadX, PadY),
            at + new Vector2(widest + PadX, rows + PadY),
            Style.Colour(StyleCatalogue.Keys.PreloadListBack),
            4f);

        float y = at.Y;
        foreach (PreloadFinding finding in shown)
        {
            string key = StyleCatalogue.ForWeight(finding.Weight);
            float scale = Style.Sized(key, 1f);

            // The scaling overload rather than the plain one: the whole reason the weights
            // carry a Scale trait is that "dangerous" should be able to be BIGGER, and a list
            // that only changes colour cannot say that on a busy screen.
            draw.AddText(
                ImGui.GetFont(),
                ImGui.GetFontSize() * scale,
                new Vector2(at.X, y),
                Style.Colour(key),
                Line(finding));

            y += RowHeight * scale;
        }
    }

    /// <summary>One row: what it is, and how much of it there was.</summary>
    private static string Line(PreloadFinding finding) => $"{finding.Name}  {finding.Files}";
}
