using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Game.World;

namespace PoEformance.Overlay;

/// <summary>
/// Draws a marker's shape.
/// </summary>
/// <remarks>
/// SHAPES RATHER THAN PICTURES, and the reason is size. A marker on the in-game map is five to
/// ten pixels across; a detailed sprite at that size is a smudge, and every smudge looks like
/// every other one. What survives is the SILHOUETTE class - round, square, spiky, hollow,
/// pointed - so that is what these are built from, and colour is left to do a second job
/// rather than the only one.
///
/// The alternative was the reference's: ship the game's own icon sheet - a 5 MB grid of a
/// thousand 64-pixel tiles pulled out of the game files - and blit a cell per marker. It
/// looks exactly right, at full size. Against it: the art is the game's to redistribute, not
/// ours; each entity type needs a cell chosen by hand; and the thing it buys, fidelity, is
/// what the scale throws away. Drawn shapes need no asset, no licence, no load path, scale to
/// any size, and take a colour.
///
/// This does not close that door. The mapping from an entity to WHICH marker is the hard part
/// and it is already solved - the game names its own icons and we read the name - so swapping
/// how one is painted stays a change to this file alone.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class PoiGlyphPainter
{
    /// <summary>Line thickness for the outlined shapes, before scaling.</summary>
    private const float Stroke = 1.6f;

    /// <summary>Draws one marker, centred, sized to roughly <paramref name="radius"/>.</summary>
    public static void Draw(ImDrawListPtr draw, Vector2 at, float radius, uint colour, PoiGlyph glyph)
    {
        float line = Math.Max(1f, radius * Stroke / 5f);

        switch (glyph)
        {
            case PoiGlyph.Portal: Portal(draw, at, radius, colour); break;
            case PoiGlyph.Waypoint: Waypoint(draw, at, radius, colour, line); break;
            case PoiGlyph.Checkpoint: Checkpoint(draw, at, radius, colour, line); break;
            case PoiGlyph.Chest: Chest(draw, at, radius, colour, line); break;
            case PoiGlyph.Strongbox: Strongbox(draw, at, radius, colour, line); break;
            case PoiGlyph.Shrine: Shrine(draw, at, radius, colour, line); break;
            case PoiGlyph.Breach: Breach(draw, at, radius, colour, line); break;
            case PoiGlyph.Ritual: Ritual(draw, at, radius, colour, line); break;
            case PoiGlyph.Expedition: draw.AddNgonFilled(at, radius, colour, 5); break;
            case PoiGlyph.Essence: draw.AddNgon(at, radius, colour, 4, line); break;
            case PoiGlyph.Delirium: Delirium(draw, at, radius, colour); break;
            case PoiGlyph.Npc: Npc(draw, at, radius, colour); break;
            case PoiGlyph.Quest: Quest(draw, at, radius, colour, line); break;
            case PoiGlyph.Boss: Boss(draw, at, radius, colour, line); break;
            default: Diamond(draw, at, radius, colour); break;
        }
    }

    /// <summary>A diamond: the shape for a place the tool cannot name.</summary>
    private static void Diamond(ImDrawListPtr draw, Vector2 at, float r, uint colour)
    {
        draw.AddQuadFilled(
            at with { Y = at.Y - r },
            at with { X = at.X + r },
            at with { Y = at.Y + r },
            at with { X = at.X - r },
            colour);
    }

    /// <summary>An arch - a way out.</summary>
    private static void Portal(ImDrawListPtr draw, Vector2 at, float r, uint colour)
    {
        draw.PathLineTo(new Vector2(at.X - r, at.Y + r));
        draw.PathLineTo(new Vector2(at.X - r, at.Y));
        draw.PathArcTo(at, r, MathF.PI, MathF.Tau, 12);
        draw.PathLineTo(new Vector2(at.X + r, at.Y + r));
        draw.PathFillConvex(colour);
    }

    /// <summary>A ring with a centre: somewhere you can travel to.</summary>
    private static void Waypoint(ImDrawListPtr draw, Vector2 at, float r, uint colour, float line)
    {
        draw.AddCircle(at, r, colour, 14, line);
        draw.AddCircleFilled(at, r * 0.35f, colour, 8);
    }

    /// <summary>A flag.</summary>
    private static void Checkpoint(ImDrawListPtr draw, Vector2 at, float r, uint colour, float line)
    {
        var foot = new Vector2(at.X - r * 0.5f, at.Y + r);
        var head = new Vector2(at.X - r * 0.5f, at.Y - r);
        draw.AddLine(foot, head, colour, line);
        draw.AddTriangleFilled(head, new Vector2(at.X + r, at.Y - r * 0.45f), head with { Y = at.Y + r * 0.1f }, colour);
    }

    /// <summary>A box with a lid.</summary>
    private static void Chest(ImDrawListPtr draw, Vector2 at, float r, uint colour, float line)
    {
        draw.AddRectFilled(new Vector2(at.X - r, at.Y - r * 0.7f), new Vector2(at.X + r, at.Y + r * 0.8f), colour);
        draw.AddLine(
            new Vector2(at.X - r, at.Y - r * 0.1f),
            new Vector2(at.X + r, at.Y - r * 0.1f),
            0xFF000000,
            line);
    }

    /// <summary>A box that is locked.</summary>
    private static void Strongbox(ImDrawListPtr draw, Vector2 at, float r, uint colour, float line)
    {
        draw.AddRect(new Vector2(at.X - r, at.Y - r * 0.8f), new Vector2(at.X + r, at.Y + r * 0.8f), colour, 0f, 0, line * 1.4f);
        draw.AddCircleFilled(at, r * 0.3f, colour, 8);
    }

    /// <summary>A burst - it radiates while you stand in it.</summary>
    private static void Shrine(ImDrawListPtr draw, Vector2 at, float r, uint colour, float line)
    {
        draw.AddCircleFilled(at, r * 0.45f, colour, 10);
        for (int i = 0; i < 4; i++)
        {
            float angle = (MathF.PI / 2f * i) + (MathF.PI / 4f);
            var dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            draw.AddLine(at + (dir * r * 0.7f), at + (dir * r * 1.35f), colour, line);
        }
    }

    /// <summary>A hole with cracks running out of it.</summary>
    private static void Breach(ImDrawListPtr draw, Vector2 at, float r, uint colour, float line)
    {
        draw.AddNgon(at, r, colour, 6, line);
        for (int i = 0; i < 3; i++)
        {
            float angle = MathF.Tau / 3f * i;
            var dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            draw.AddLine(at, at + (dir * r * 0.85f), colour, line);
        }
    }

    /// <summary>Concentric circles - an altar to fight around.</summary>
    private static void Ritual(ImDrawListPtr draw, Vector2 at, float r, uint colour, float line)
    {
        draw.AddCircle(at, r, colour, 16, line);
        draw.AddCircle(at, r * 0.5f, colour, 12, line);
    }

    /// <summary>Overlapping circles - the fog.</summary>
    private static void Delirium(ImDrawListPtr draw, Vector2 at, float r, uint colour)
    {
        draw.AddCircleFilled(at with { X = at.X - r * 0.45f }, r * 0.55f, colour, 10);
        draw.AddCircleFilled(at with { X = at.X + r * 0.45f }, r * 0.55f, colour, 10);
        draw.AddCircleFilled(at with { Y = at.Y - r * 0.35f }, r * 0.6f, colour, 10);
    }

    /// <summary>A figure.</summary>
    private static void Npc(ImDrawListPtr draw, Vector2 at, float r, uint colour)
    {
        draw.AddCircleFilled(at with { Y = at.Y - r * 0.5f }, r * 0.42f, colour, 10);
        draw.AddTriangleFilled(
            new Vector2(at.X, at.Y - r * 0.1f),
            new Vector2(at.X + r * 0.8f, at.Y + r),
            new Vector2(at.X - r * 0.8f, at.Y + r),
            colour);
    }

    /// <summary>A bar over a dot - the way the game asks for attention.</summary>
    private static void Quest(ImDrawListPtr draw, Vector2 at, float r, uint colour, float line)
    {
        draw.AddLine(at with { Y = at.Y - r }, at with { Y = at.Y + r * 0.2f }, colour, line * 1.6f);
        draw.AddCircleFilled(at with { Y = at.Y + r * 0.75f }, line, colour, 6);
    }

    /// <summary>A spiked star - something large lives here.</summary>
    private static void Boss(ImDrawListPtr draw, Vector2 at, float r, uint colour, float line)
    {
        draw.AddCircleFilled(at, r * 0.4f, colour, 10);
        for (int i = 0; i < 8; i++)
        {
            float angle = MathF.Tau / 8f * i;
            var dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            draw.AddLine(at + (dir * r * 0.5f), at + (dir * r * 1.3f), colour, line);
        }
    }
}
