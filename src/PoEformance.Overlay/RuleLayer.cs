using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;

namespace PoEformance.Overlay;

/// <summary>
/// Paints what the rule engine decided to show.
/// </summary>
/// <remarks>
/// Draws only. Every decision about WHETHER something shows - the conditions, the priorities,
/// the linger, whether the game has focus - was made in <see cref="RuleEngine"/> and arrives
/// here as a finished list. That is what makes the whole feature testable without a game, and
/// it is where this differs most from the reference plugin, which evaluates its rules inside
/// its render callback and presses keys from the loop that draws text.
///
/// Straight onto the game rather than into a window, for the reason <see cref="AlertBanner"/>
/// gives: the point of a combat readout is being read WITHOUT looking away.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class RuleLayer
{
    /// <summary>How far the drop shadow sits from the text.</summary>
    private const float ShadowOffset = 1f;

    /// <summary>The shadow's colour: black, most of the way opaque.</summary>
    private const uint Shadow = 0xCC00_0000;

    /// <summary>Padding around a bar's label.</summary>
    private const float LabelGap = 4f;

    /// <summary>Draws one tick's worth of captions and bars.</summary>
    public void Draw(ImDrawListPtr draw, IReadOnlyList<RuleDrawing> drawings, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(drawings);

        foreach (RuleDrawing drawing in drawings)
        {
            RuleEffect effect = drawing.Effect;
            var at = new Vector2(width * effect.X, height * effect.Y);

            if (effect.Kind == RuleEffectKind.Bar)
            {
                Bar(draw, drawing, at, width, height);
                continue;
            }

            Caption(draw, drawing, at);
        }
    }

    private static void Caption(ImDrawListPtr draw, RuleDrawing drawing, Vector2 at)
    {
        RuleEffect effect = drawing.Effect;
        if (drawing.Text.Length == 0)
        {
            return;
        }

        float size = ImGui.GetFontSize() * effect.Scale;
        Vector2 measured = ImGui.CalcTextSize(drawing.Text) * effect.Scale;

        // Centred on the point, because that is what somebody placing a readout at 0.5 means
        // by the middle of the screen - and it keeps a caption whose text changes length from
        // sliding sideways as it updates.
        var corner = at - (measured / 2f);

        // A shadow rather than a plate: a caption may be up for the whole of a fight, where a
        // filled rectangle would be a permanent hole in the screen. One dark copy behind it is
        // enough to keep it readable over a bright wall.
        //
        // AddText on the draw list, never ImGui.Text - see ImGuiText: those are printf, and
        // this string comes from a user's caption, which is exactly where a stray percent sign
        // lives.
        draw.AddText(null, size, corner + new Vector2(ShadowOffset, ShadowOffset), Shadow, drawing.Text);
        draw.AddText(null, size, corner, RuleColours.Packed(effect.Colour, 0xFF40_FF33), drawing.Text);
    }

    private static void Bar(ImDrawListPtr draw, RuleDrawing drawing, Vector2 at, int width, int height)
    {
        RuleEffect effect = drawing.Effect;
        var size = new Vector2(width * effect.Width, height * effect.Height);
        var corner = at - (size / 2f);
        Vector2 far = corner + size;

        uint back = RuleColours.Packed(effect.BackgroundColour, 0xBF0F_0F0F);
        uint front = RuleColours.Packed(effect.Colour, 0xFF40_FF33);

        draw.AddRectFilled(corner, far, back, 3f);

        if (drawing.Fill is double fill)
        {
            draw.AddRectFilled(corner, new Vector2(corner.X + ((float)fill * size.X), far.Y), front, 3f);
        }
        else
        {
            // The number could not be read. An EMPTY bar would say the pool is at zero, which
            // on a life bar is the difference between "not loaded" and "about to die" - so an
            // unanswerable bar is outlined instead of filled, and reads as a bar with no
            // reading rather than as a reading of nothing.
            draw.AddRect(corner, far, front, 3f);
        }

        if (drawing.Text.Length == 0)
        {
            return;
        }

        float fontSize = ImGui.GetFontSize() * effect.Scale;
        Vector2 measured = ImGui.CalcTextSize(drawing.Text) * effect.Scale;

        // Above the bar rather than inside it: a label on top of a moving fill is legible at
        // one end of the bar and not at the other.
        var label = new Vector2(at.X - (measured.X / 2f), corner.Y - measured.Y - LabelGap);

        draw.AddText(null, fontSize, label + new Vector2(ShadowOffset, ShadowOffset), Shadow, drawing.Text);
        draw.AddText(null, fontSize, label, front, drawing.Text);
    }
}
