using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.World;

namespace PoEformance.Overlay;

/// <summary>
/// Draws the path a beam occupies as a banded arrow, from where it starts to where it ends.
/// </summary>
/// <remarks>
/// The boss beam is the thing that made steering necessary, and until the Beam component was
/// decoded nothing here could say more about one than "there is an effect entity somewhere near
/// the middle of it". A ring on the entity's own position marks ONE END of a line up to eleven
/// hundred world units long - which is worse than nothing, because it flags as dangerous the one
/// spot a player is already standing clear of.
///
/// THE SHAPE IS THE GAME'S OWN IDIOM, the wide translucent band with chevrons running along it
/// that Path of Exile uses to point somewhere. It is the right pick for two reasons beyond
/// looking familiar: a band survives being glanced at during a fight where a hairline does not,
/// and the chevrons carry the one thing a plain line cannot - WHICH WAY IT POINTS. Both ends of
/// this beam come out of the component, so the direction is known, and the far end is the half
/// that matters.
///
/// WHAT THE WIDTH IS NOT. The band's width is a display choice and carries no claim about how
/// far the beam actually reaches sideways. The component has no thickness field that survived
/// checking - the one candidate is exceeded by the beam's own length on two thirds of readings -
/// so a band drawn to look like a danger zone would be an invention wearing the authority of a
/// measurement. The LINE is measured; the WIDTH is decoration, and the settings row beside the
/// slider says so.
///
/// IT NEEDS NO PER-FRAME REREAD to stay correct: both ends are set when the beam is created and
/// never move. The band disappears because the entity leaves the list, which is the game saying
/// the beam is over.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class BeamLayer
{
    /// <summary>Most beams drawn in one frame. Measured maximum alive at once is five.</summary>
    /// <remarks>
    /// Generous against the measurement rather than tight to it: the cap is a backstop against
    /// a future encounter nobody has recorded, not a budget. If it is ever reached, the screen
    /// is already telling the player something has gone very wrong.
    /// </remarks>
    private const int MostBeams = 64;

    /// <summary>Most chevrons on one band, however long it is on screen.</summary>
    /// <remarks>
    /// A beam fired from off screen can project to a band thousands of pixels long, and spacing
    /// alone would then put hundreds of triangles on it. The cap turns that into a band whose
    /// arrows thin out rather than a frame spent drawing them.
    /// </remarks>
    private const int MostChevrons = 40;

    /// <summary>Chevron spacing along the band, as a multiple of its full width.</summary>
    /// <remarks>
    /// These three proportions were measured off the game's own way-to-the-waypoint band rather
    /// than picked: a chevron base about six tenths of the band's width, spacing a little under
    /// one width, and SQUAT - wider across than it is long. That last one is what makes the
    /// arrow read as an arrow at a glance; the first attempt had them longer than they were
    /// wide and they looked like darts strung on a wire.
    /// </remarks>
    private const float ChevronSpacing = 0.85f;

    /// <summary>Half a chevron's base, as a fraction of the band's half-width.</summary>
    private const float ChevronSpan = 0.60f;

    /// <summary>Half a chevron's length along the band, as a fraction of the half-width.</summary>
    private const float ChevronLength = 0.50f;

    /// <summary>How far the outer glow reaches past the band edge.</summary>
    private const float GlowSpread = 1.28f;

    /// <summary>How fast the chevrons travel along the band, in widths per second.</summary>
    /// <remarks>
    /// They move for the same reason the game's do: a static arrow says which way the line
    /// points, and a moving one says it without being read. Slow enough not to draw the eye off
    /// the fight - this is a warning, not an ornament.
    /// </remarks>
    private const float ChevronSpeed = 0.55f;

    /// <summary>What to draw and in which colours. Shared with the tracker's other layers.</summary>
    public TrackerSettings Settings { get; set; } = TrackerSettings.Default;

    /// <summary>Draws every beam in the snapshot as the band it occupies.</summary>
    public void Draw(ImDrawListPtr draw, WorldSnapshot snapshot, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!Settings.ShowBeams)
        {
            return;
        }

        uint colour = OverlaySettings.ParseColour(Settings.BeamColour);
        if (colour == 0)
        {
            return;
        }

        float halfWidth = Math.Max(1f, Settings.BeamThickness) / 2f;
        double time = ImGui.GetTime();

        int drawn = 0;
        foreach (WorldEntity entity in snapshot.Entities)
        {
            // A remembered beam is a beam that has finished. Drawing one paints a danger band
            // across ground that is clear, which is the same mistake the ground rings guard
            // against and worse here, because a band covers so much more of the screen.
            if (entity.IsRemembered || entity.Beam is not { } beam)
            {
                continue;
            }

            ScreenPoint from = WorldToScreen.Project(
                snapshot.Matrix, beam.SourceX, beam.SourceY, beam.SourceZ, width, height);
            ScreenPoint to = WorldToScreen.Project(
                snapshot.Matrix, beam.TargetX, beam.TargetY, beam.TargetZ, width, height);

            // EITHER end may be off screen while the beam still crosses it - the normal case for
            // a long one fired from out of view, and exactly the case worth drawing. ImGui clips
            // the geometry itself, so both are handed over as they are and only a beam with
            // NEITHER end anywhere near the viewport is dropped.
            if (!from.OnScreen && !to.OnScreen)
            {
                continue;
            }

            var a = new Vector2(from.X, from.Y);
            var b = new Vector2(to.X, to.Y);
            Vector2 along = b - a;
            float length = along.Length();
            if (length < 1f)
            {
                continue;
            }

            DrawBand(draw, a, b, along / length, length, halfWidth, colour, time);

            if (++drawn >= MostBeams)
            {
                return;
            }
        }
    }

    /// <summary>One beam: the translucent band, its edges, and the chevrons running along it.</summary>
    private static void DrawBand(
        ImDrawListPtr draw,
        Vector2 a,
        Vector2 b,
        Vector2 direction,
        float length,
        float halfWidth,
        uint colour,
        double time)
    {
        var normal = new Vector2(-direction.Y, direction.X);
        Vector2 across = normal * halfWidth;
        Vector2 glow = normal * (halfWidth * GlowSpread);
        uint edge = WithAlpha(colour, 1f);

        // Two nested quads rather than one flat fill: the wider, fainter one is what keeps the
        // band from ending in a hard line against a dark floor, and it is the cheapest stand-in
        // for the game's own soft edge that a draw list without shaders can manage.
        //
        // HOW SEE-THROUGH THAT LEAVES THE MIDDLE, worked out rather than eyeballed, because the
        // preview render used to check the shape does not composite alpha and made this look
        // solid: the two passes overlap, so the interior is 1 - (1 - 0.10a)(1 - 0.20a) of the
        // configured colour's alpha a. At the default a = 0xE6 that is 25% opaque, and even at a
        // fully opaque colour it is 28% - the band never stops being something you can see the
        // fight through, whatever colour is picked for it.
        draw.AddQuadFilled(a + glow, b + glow, b - glow, a - glow, WithAlpha(colour, 0.10f));

        // The body stays well under the chosen alpha: this sits over the fight for as long as
        // the beam lasts, and a band solid enough to read comfortably is a band you cannot see
        // the monster through.
        draw.AddQuadFilled(a + across, b + across, b - across, a - across, WithAlpha(colour, 0.20f));

        // Edges at full strength, which is what makes the band read as a defined lane rather
        // than as a smear - the same trick the game's own version uses.
        draw.AddLine(a + across, b + across, edge, 2f);
        draw.AddLine(a - across, b - across, edge, 2f);

        // Chevrons, marching towards the far end. The phase is wrapped into one spacing so the
        // pattern scrolls forever without the triangles drifting off the end of the band.
        float spacing = Math.Max(8f, halfWidth * 2f * ChevronSpacing);
        float phase = (float)(time * ChevronSpeed * spacing % spacing);
        float span = halfWidth * ChevronSpan;
        float reach = halfWidth * ChevronLength;
        Vector2 wing = normal * span;
        uint wash = WithAlpha(colour, 0.24f);

        int count = 0;
        for (float t = phase; t < length - reach && count < MostChevrons; t += spacing, count++)
        {
            Vector2 centre = a + (direction * t);
            Vector2 tip = centre + (direction * reach);
            Vector2 back = centre - (direction * reach);
            Vector2 left = back + wing;
            Vector2 right = back - wing;

            // Barely filled and firmly outlined, so the band stays see-through where it matters
            // and the arrows still read against a bright spell underneath.
            draw.AddTriangleFilled(tip, left, right, wash);
            draw.AddTriangle(tip, left, right, edge, 2f);
        }

        // The far end marked, because that is the end that matters and a band alone does not
        // say where it stops.
        draw.AddCircleFilled(b, halfWidth * 0.5f, edge);
    }

    /// <summary>The same colour at a different alpha. ImGui packs alpha in the top byte.</summary>
    private static uint WithAlpha(uint colour, float scale)
    {
        uint alpha = (colour >> 24) & 0xFF;
        uint scaled = (uint)Math.Clamp(alpha * scale, 0, 255);
        return (colour & 0x00FFFFFFu) | (scaled << 24);
    }
}
