using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.World;

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
/// Straight onto the game rather than into a window, for the reason the preload card
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

    /// <summary>How many points a world-space circle is drawn from.</summary>
    /// <remarks>
    /// Enough that a ring on the ground reads as a circle rather than as a polygon at the
    /// radii these rules use. It is projected point by point on purpose: the ground circle is
    /// an ellipse on screen, and drawing an actual ellipse would need the camera's tilt, which
    /// this layer has no business knowing.
    /// </remarks>
    private const int RingPoints = 48;

    /// <summary>The colour of a range whose condition is satisfied, and of one that is not.</summary>
    private const uint RingHolds = 0xC050_FF50;
    private const uint RingWaits = 0xC060_A0FF;

    /// <summary>
    /// Draws the ranges a rule measures, so a radius can be checked against the ground.
    /// </summary>
    /// <remarks>
    /// The debug view for building rules, and the reason it exists is the project's own
    /// working rule: a radius is a number in a text field, and the honest way to know whether
    /// 30 is right is to see the circle with the monsters it counts inside it.
    ///
    /// BOTH kinds are world circles on the ground, differing only in where they are centred.
    /// The cursor one was a circle of screen PIXELS around the mouse, and that was wrong twice
    /// over: a circle on the screen is an ELLIPSE on the ground - stretched away from the
    /// camera by the tilt - so it counted monsters in a region no skill has, and its radius
    /// moved with the resolution and the zoom besides. Drawn as a world circle, it comes out
    /// as an ellipse on screen because the projection puts it there, which is the shape the
    /// measurement actually has.
    /// </remarks>
    /// <summary>A leaf whose number could not be read - its own colour, not a failure's.</summary>
    private const uint FactUnknown = 0xC0B0_B0B0;

    public void DrawRanges(
        ImDrawListPtr draw,
        IReadOnlyList<PreviewRing> rings,
        WorldSnapshot snapshot,
        (float X, float Y)? cursor,
        int width,
        int height,
        IReadOnlyList<PreviewFact>? facts = null)
    {
        ArgumentNullException.ThrowIfNull(rings);
        ArgumentNullException.ThrowIfNull(snapshot);

        if (width <= 0 || height <= 0)
        {
            return;
        }

        // The whole rule as a checklist, before the rings and independent of them: the rings
        // carry the two range counters, and somebody watching a rule that will not fire was
        // left guessing which of the OTHER conditions was the one saying no. Left edge, above
        // half height - clear of the quest tracker on the right and the bars below.
        if (facts is { Count: > 0 })
        {
            var at = new Vector2(12f, height * 0.28f);
            float line = ImGui.GetFontSize() + 3f;
            foreach (PreviewFact fact in facts)
            {
                uint colour = fact.Holds ? RingHolds : fact.Known ? RingWaits : FactUnknown;
                string text = (fact.Holds ? "+ " : fact.Known ? "- " : "? ") + fact.Label;
                draw.AddText(at + new Vector2(ShadowOffset, ShadowOffset), Shadow, text);
                draw.AddText(at, colour, text);
                at.Y += line;
            }
        }

        if (rings.Count == 0 || snapshot.Player is not WorldEntity player)
        {
            return;
        }

        // Where the pointer is on the ground, computed here rather than taken from the tick:
        // the renderer redraws far more often than a snapshot arrives, so the cursor ring
        // follows the mouse instead of stepping after it. Same function the rules use, so the
        // two cannot disagree about where "there" is.
        (float X, float Y)? aimed = cursor is (float sx, float sy)
            ? WorldToScreen.OnGround(snapshot.Matrix, sx, sy, player.WorldZ, width, height)
            : null;

        foreach (PreviewRing ring in rings)
        {
            uint colour = ring.Holds ? RingHolds : RingWaits;

            if (ring.AtCursor)
            {
                if (aimed is (float ax, float ay))
                {
                    WorldRing(draw, snapshot, ax, ay, player.WorldZ, ring, colour, width, height);
                }

                continue;
            }

            WorldRing(draw, snapshot, player.WorldX, player.WorldY, player.WorldZ, ring, colour, width, height);
        }
    }

    private static void WorldRing(
        ImDrawListPtr draw,
        WorldSnapshot snapshot,
        float centreX,
        float centreY,
        float groundZ,
        PreviewRing ring,
        uint colour,
        int width,
        int height)
    {
        Vector2 previous = default;
        bool hadPrevious = false;
        Vector2 lowest = default;
        bool hadLowest = false;

        for (int step = 0; step <= RingPoints; step++)
        {
            float angle = step / (float)RingPoints * MathF.Tau;
            float x = centreX + ((float)ring.Radius * MathF.Cos(angle));
            float y = centreY + ((float)ring.Radius * MathF.Sin(angle));

            // At one height rather than at the terrain under each point. The ring is a distance
            // in the XY plane - which is what the rule measures - so following the ground would
            // draw a different shape from the one being explained.
            ScreenPoint point = WorldToScreen.Project(
                snapshot.Matrix, x, y, groundZ, width, height);

            if (!point.OnScreen)
            {
                // A gap rather than a chord across the screen. A point behind the camera
                // projects to a plausible pixel, and joining to it draws a line through the
                // middle of everything - the same trap the atlas routes record.
                hadPrevious = false;
                continue;
            }

            var here = new Vector2(point.X, point.Y);
            if (hadPrevious)
            {
                draw.AddLine(previous, here, colour, 2f);
            }

            previous = here;
            hadPrevious = true;

            if (!hadLowest || here.Y > lowest.Y)
            {
                lowest = here;
                hadLowest = true;
            }
        }

        if (hadLowest)
        {
            // At the ring's lowest point on screen, which is the near edge - so the label sits
            // between the player and the camera rather than behind the character.
            Label(draw, ring.Label, lowest + new Vector2(0, 4f), colour);
        }
    }

    private static void Label(ImDrawListPtr draw, string text, Vector2 at, uint colour)
    {
        Vector2 size = ImGui.CalcTextSize(text);
        var corner = new Vector2(at.X - (size.X / 2f), at.Y);

        draw.AddText(corner + new Vector2(ShadowOffset, ShadowOffset), Shadow, text);
        draw.AddText(corner, colour, text);
    }

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
