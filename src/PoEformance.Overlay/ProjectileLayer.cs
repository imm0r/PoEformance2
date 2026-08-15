using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.World;

namespace PoEformance.Overlay;

/// <summary>
/// Draws the projectiles in flight, each with the line it came along.
/// </summary>
/// <remarks>
/// IN WORLD SPACE and not on the map, like the effects and unlike the dots. Where a
/// projectile is matters within a few feet and for about half a second; the map is neither
/// close enough nor still enough to answer that.
///
/// WHAT IT CAN SHOW, because the word invites the wrong expectation twice over. The game's
/// particles are rendering and are not entities - nothing in memory lists a spark, so this is
/// not an outline of the fire you can see. What IS listed is the projectile ENTITY the skill
/// spawns, one per projectile, and that entity is what gets a mark.
///
/// WHOSE IT IS comes from one byte - Positioned.Reaction, the same one that separates your
/// minions from the monsters - and that is the only thing on the entity that answers it. Both
/// sides are drawn by default for that reason: if the byte turns out not to be set on the
/// projectiles a particular skill spawns, showing only the friendly ones would be an overlay
/// that draws nothing and offers no clue why. Two colours and a count on the tab make the same
/// question answerable by looking, and the switch is there for whoever has looked.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ProjectileLayer
{
    /// <summary>Radius in pixels of the mark on the projectile itself, before the chosen scale.</summary>
    private const float Radius = 4f;

    /// <summary>Most projectiles drawn in one frame. A backstop on a count that is unbounded.</summary>
    /// <remarks>
    /// A volley skill with enough projectile support can put a lot of these in the air at
    /// once, and every one of them is a trail of line segments. The cap is high enough that
    /// nothing normal reaches it and low enough that nothing pathological costs a frame.
    /// </remarks>
    private const int MostDrawn = 400;

    /// <summary>The faintest a trail's oldest end is drawn, as a share of its head.</summary>
    private const float TailStrength = 0.15f;

    /// <summary>Whether to draw at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Draw only the projectiles the game says are on the player's side.</summary>
    /// <remarks>
    /// Off by default, and the type's remarks say why: the rule behind "mine" rests on a byte
    /// nothing here has been able to verify on a projectile, and a filter built on an
    /// unverified rule should not be the thing standing between the user and an empty screen.
    /// </remarks>
    public bool MineOnly { get; set; }

    /// <summary>Draw the line each projectile came along, not just where it is.</summary>
    public bool ShowTrails { get; set; } = true;

    /// <summary>Write the metadata path beside each mark. For finding out what a skill spawns.</summary>
    public bool ShowPaths { get; set; }

    /// <summary>How every drawn thing looks. Shared with the overlay.</summary>
    public OverlayStyle Style { get; set; } = new();

    /// <summary>Draws every projectile the projection can place.</summary>
    /// <param name="watch">
    /// Where each one has been. Handed over rather than kept here: the trail is state that
    /// spans snapshots, this class is a drawing loop, and only the first of those is testable
    /// off Windows.
    /// </param>
    public void Draw(
        ImDrawListPtr draw, WorldSnapshot snapshot, ProjectileWatch watch, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(watch);

        if (!Enabled)
        {
            return;
        }

        uint outline = Style.Colour(StyleCatalogue.Keys.DotOutline);
        uint label = Style.Colour(StyleCatalogue.Keys.DotLabel);
        LayerStyle trailStyle = Style[StyleCatalogue.Keys.ProjectileTrail];
        float trailWidth = trailStyle.WidthOr(1.6f);
        float trailStrength = ((Style.Colour(StyleCatalogue.Keys.ProjectileTrail) >> 24) & 0xFF) / 255f;

        int drawn = 0;
        foreach (WorldEntity projectile in snapshot.Entities)
        {
            if (!ProjectileWatch.Is(projectile) || (MineOnly && !projectile.IsFriendly))
            {
                continue;
            }

            string key = projectile.IsFriendly
                ? StyleCatalogue.Keys.ProjectileMine
                : StyleCatalogue.Keys.ProjectileOther;

            if (!Style.Visible(key))
            {
                continue;
            }

            // The entity's OWN position, with nothing added to it. A monster's mark is lifted
            // to the top of its model because that is where the game floats its health bar and
            // the feet are what Render reports - but a projectile in the air IS where Render
            // says it is, and lifting it would put the mark above the thing it marks.
            ScreenPoint head = WorldToScreen.Project(
                snapshot.Matrix, projectile.WorldX, projectile.WorldY, projectile.WorldZ, width, height);

            if (!head.OnScreen)
            {
                continue;
            }

            uint colour = Style.Colour(key);

            // Sized from the entity's OWN entry rather than from one of the two. They are
            // separate settings precisely so one side can be made to shout, and a scale read
            // from the wrong key is a slider that does nothing.
            float scale = Style.Sized(key, 1f);
            var at = new Vector2(head.X, head.Y);

            if (ShowTrails && trailStyle.Visible && trailStrength > 0f)
            {
                // The trail says WHAT it is while the mark says whose it is, so the two
                // together answer both at once: a cyan dot on a red trail is your fireball.
                // An unrecognised name falls back to the mark's colour rather than to a
                // colour of its own - see ProjectileElements for why that is a guess.
                string element = StyleCatalogue.ForElement(ProjectileElements.Of(projectile.Path));
                uint trailColour = element.Length > 0 && Style.Visible(element)
                    ? Style.Colour(element)
                    : colour;

                DrawTrail(
                    draw, snapshot, watch.TrailOf(projectile), at, trailColour, trailStrength,
                    trailWidth, width, height);
            }

            // Filled, unlike the effects' rings. A projectile is small, fast and usually over
            // something bright - it is its own explosion - so the mark has to survive being
            // drawn on top of fire, and an outlined dot is what does that.
            draw.AddCircleFilled(at, Radius * scale, colour, 10);
            draw.AddCircle(at, Radius * scale, outline, 10, 1f);

            if (ShowPaths)
            {
                draw.AddText(at + new Vector2((Radius * scale) + 4f, -6f), label, ImGuiText.Tail(projectile.Path));
            }

            if (++drawn >= MostDrawn)
            {
                return;
            }
        }
    }

    /// <summary>Draws one projectile's trail, fading towards the end it came from.</summary>
    /// <remarks>
    /// Segment by segment rather than as one polyline, because the fade is the point: a line
    /// of even strength says where a projectile has been and not which way it is going, and
    /// direction is half of what the trail was drawn for. ImGui's polyline takes one colour.
    /// </remarks>
    private static void DrawTrail(
        ImDrawListPtr draw,
        WorldSnapshot snapshot,
        IReadOnlyList<TrailPoint> trail,
        Vector2 head,
        uint colour,
        float strength,
        float thickness,
        int width,
        int height)
    {
        if (trail.Count < 2)
        {
            return;
        }

        Vector2 previous = default;
        bool have = false;
        for (int i = 0; i < trail.Count; i++)
        {
            TrailPoint point = trail[i];
            ScreenPoint on = WorldToScreen.Project(
                snapshot.Matrix, point.X, point.Y, point.Z, width, height);

            if (!on.OnScreen)
            {
                // A trail that leaves the screen is broken rather than bridged: joining across
                // the gap would draw a straight line through wherever the projection folded,
                // which is a path the projectile never took.
                have = false;
                continue;
            }

            var here = new Vector2(on.X, on.Y);
            if (have)
            {
                // Ramped along the trail's own length, so the oldest end is faintest whatever
                // the trail happens to be holding - a projectile just spawned has two points
                // and should not look like a stub of a full one.
                float along = (float)i / (trail.Count - 1);
                float fade = strength * (TailStrength + ((1f - TailStrength) * along));
                draw.AddLine(previous, here, OverlaySettings.Fade(colour, fade), thickness);
            }

            previous = here;
            have = true;
        }

        // The last recorded position to where it is NOW. Without this the trail stops a frame
        // behind the mark, which reads as the mark having jumped ahead of its own line.
        if (have)
        {
            draw.AddLine(previous, head, OverlaySettings.Fade(colour, strength), thickness);
        }
    }
}
