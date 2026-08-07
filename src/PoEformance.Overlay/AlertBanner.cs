using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;

namespace PoEformance.Overlay;

/// <summary>
/// Says one line across the top of the screen when something turned up.
/// </summary>
/// <remarks>
/// Painted straight onto the game rather than put in a window, because the whole point is
/// being read WITHOUT looking away from what you are doing. A window has a title bar, takes
/// focus, and sits wherever it was left; this is a line where the eye already is.
///
/// Near the top and centred, which is where the game itself puts its own notices, so it reads
/// as part of the same conversation rather than as something covering it. Deliberately not in
/// the middle: the middle is where the fight is.
///
/// It FADES rather than vanishing. A line that blinks out is one you are never sure you read,
/// and a fade also stops two alerts in quick succession from looking like one flicker.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class AlertBanner
{
    /// <summary>How long a line stays fully readable.</summary>
    public const long HoldMs = 3_000;

    /// <summary>How long it takes to fade out afterwards.</summary>
    public const long FadeMs = 900;

    /// <summary>Where the line sits, as a fraction of the screen height.</summary>
    private const float FromTop = 0.13f;

    private Alert? _showing;

    /// <summary>How every drawn thing looks. Shared with the overlay.</summary>
    public OverlayStyle Style { get; set; } = new();

    /// <summary>Takes a new alert to show, replacing whatever was up.</summary>
    /// <remarks>
    /// Replaces rather than queues. A queue would show something that happened five seconds
    /// ago in the middle of what is happening now, and the watcher has already decided this
    /// one is the most important thing it has to say.
    /// </remarks>
    public void Show(Alert alert) => _showing = alert;

    /// <summary>Clears whatever is up.</summary>
    public void Clear() => _showing = null;

    /// <summary>Draws the line, if there is one and it has not faded out.</summary>
    public void Draw(ImDrawListPtr draw, int width, int height, long nowMs)
    {
        if (_showing is not Alert alert || !Style.Visible(StyleCatalogue.Keys.Banner))
        {
            return;
        }

        long age = nowMs - alert.AtMs;
        if (age >= HoldMs + FadeMs)
        {
            _showing = null;
            return;
        }

        // A clock that went backwards - a restart, or a caller passing something else. Not
        // worth an exception in a drawing loop, and dropping the line is honest.
        if (age < 0)
        {
            _showing = null;
            return;
        }

        float alpha = age <= HoldMs ? 1f : 1f - ((age - HoldMs) / (float)FadeMs);

        string line = alert.Line;
        string away = alert.Distance > 0f ? $"{alert.Distance:F0} away" : string.Empty;

        Vector2 size = ImGui.CalcTextSize(line);
        var at = new Vector2((width - size.X) / 2f, height * FromTop);

        // A backing plate, because a bright line over a bright wall is unreadable and this one
        // gets exactly one chance to be read.
        draw.AddRectFilled(
            at - new Vector2(14f, 8f),
            at + size + new Vector2(14f, 8f),
            Fade(Style.Colour(StyleCatalogue.Keys.BannerBack), alpha),
            5f);

        draw.AddText(at, Fade(Style.Colour(StyleCatalogue.Keys.Banner), alpha), line);

        // How far away, under the line and quieter. It answers the question the line
        // provokes - "where" - without competing with what the thing actually is.
        if (away.Length > 0)
        {
            Vector2 awaySize = ImGui.CalcTextSize(away);
            draw.AddText(
                new Vector2((width - awaySize.X) / 2f, at.Y + size.Y + 3f),
                Fade(Style.Colour(StyleCatalogue.Keys.Banner), alpha * 0.65f),
                away);
        }
    }

    /// <summary>Scales a packed colour's alpha, leaving its colour alone.</summary>
    private static uint Fade(uint colour, float by)
    {
        uint alpha = (uint)Math.Clamp(((colour >> 24) & 0xFF) * by, 0f, 255f);
        return (colour & 0x00FFFFFF) | (alpha << 24);
    }
}
