using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.World;

namespace PoEformance.Overlay;

/// <summary>
/// Draws the line a beam actually occupies, from where it starts to where it ends.
/// </summary>
/// <remarks>
/// The boss beam is the thing that made steering necessary, and until the Beam component was
/// decoded nothing here could say more about one than "there is an effect entity somewhere near
/// the middle of it". A ring on the entity's own position is a ring on ONE END of a line up to
/// eleven hundred world units long - which is worse than nothing, because it marks as dangerous
/// the one spot on the beam a player is already standing clear of.
///
/// BOTH ENDS COME OUT OF THE COMPONENT, so this is two projected points and a line between
/// them. The near end is the beam entity's own position exactly and the far end is what a
/// player has to be out of; see the Beam struct in the schema for the control that settled
/// which is which.
///
/// WHY IT IS DRAWN IN FULL rather than as a danger zone with a width: a width would be a guess.
/// The component has no thickness field that survived checking - the one candidate turned out
/// to be exceeded by the beam's own length on two thirds of readings - and a made-up radius
/// drawn confidently is the kind of thing this project has learned to leave out. A line is what
/// the data supports.
///
/// IT NEEDS NO PER-FRAME REREAD to stay correct: both ends are set when the beam is created and
/// never move. The line disappears because the entity leaves the list, which is the game saying
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

    /// <summary>How big a dot marks the far end, in pixels.</summary>
    private const float TargetDot = 5f;

    /// <summary>What to draw and in which colours. Shared with the tracker's other layers.</summary>
    public TrackerSettings Settings { get; set; } = TrackerSettings.Default;

    /// <summary>Draws every beam in the snapshot as the line it occupies.</summary>
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

        int drawn = 0;
        foreach (WorldEntity entity in snapshot.Entities)
        {
            // A remembered beam is a beam that has finished. Drawing one paints a danger line
            // across ground that is clear, which is the same mistake the ground rings guard
            // against and worse here, because a line covers so much more of the screen.
            if (entity.IsRemembered || entity.Beam is not { } beam)
            {
                continue;
            }

            ScreenPoint from = WorldToScreen.Project(
                snapshot.Matrix, beam.SourceX, beam.SourceY, beam.SourceZ, width, height);
            ScreenPoint to = WorldToScreen.Project(
                snapshot.Matrix, beam.TargetX, beam.TargetY, beam.TargetZ, width, height);

            // EITHER end may be off screen while the beam still crosses it - that is the normal
            // case for a long one fired from out of view, and it is exactly the case worth
            // drawing. ImGui clips the line itself, so both are handed over as they are and
            // only a beam with NEITHER end anywhere near the viewport is dropped.
            if (!from.OnScreen && !to.OnScreen)
            {
                continue;
            }

            draw.AddLine(
                new Vector2(from.X, from.Y),
                new Vector2(to.X, to.Y),
                colour,
                Settings.BeamThickness);

            // The far end marked, because that is the end that matters and a bare line does
            // not say which way it points.
            if (to.OnScreen)
            {
                draw.AddCircleFilled(new Vector2(to.X, to.Y), TargetDot, colour);
            }

            if (++drawn >= MostBeams)
            {
                return;
            }
        }
    }
}
