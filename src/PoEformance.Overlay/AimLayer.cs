using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.Components;
using PoEformance.Game.World;

namespace PoEformance.Overlay;

/// <summary>
/// Draws a line out of every monster showing where it is pointing.
/// </summary>
/// <remarks>
/// THE GAME AIMS BY TURNING, which is why this is the answer to "where is that monster aiming"
/// rather than an approximation of it. Nothing in the game's memory holds a target entity; what
/// it has is a facing and a tolerance - its own stat table calls them
/// <c>action_required_target_facing_angle_tolerance_degrees</c> and
/// <c>active_skill_facing_angle_turn_duration_ms</c> - so a skill fires when the actor has
/// turned far enough. The ray is that angle, drawn.
///
/// WHAT IT DOES NOT PROMISE, and the difference matters when standing in front of one: many
/// attacks lock their direction the moment they start, so a monster can point at you, commit,
/// and be facing elsewhere by the time it lands. Homing projectiles ignore facing entirely. The
/// line says where the monster is pointing NOW - which is exactly what the game knows, and all
/// that the fields can support.
///
/// TWO LINES, when it is mid-turn: where it points, and where it is turning to. The second one
/// is the aim proper, and it is on screen only for the length of a turn - a median of 94 ms in
/// the recordings, longest 610. Blink and it is the same line again.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class AimLayer
{
    /// <summary>Most rays drawn in one frame. A backstop on a count that is unbounded.</summary>
    private const int MostRays = 200;

    /// <summary>Radius of the dot at the near end, which says which monster a ray belongs to.</summary>
    private const float RootDot = 2.5f;

    /// <summary>How long the arrow head at the far end is, as a share of the ray.</summary>
    private const float HeadShare = 0.18f;

    /// <summary>What to draw and how. Shared with the tracker's other layers.</summary>
    public TrackerSettings Settings { get; set; } = TrackerSettings.Default;

    /// <summary>The animation table, for deciding whether a monster is doing anything.</summary>
    public AnimationNames Animations { get; set; } = AnimationNames.Empty;

    /// <summary>Draws a ray from every entity whose aim was read.</summary>
    public void Draw(ImDrawListPtr draw, WorldSnapshot snapshot, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        AimSettings settings = Settings.AimOrDefault;
        if (!settings.Enabled)
        {
            return;
        }

        uint colour = OverlaySettings.ParseColour(settings.Colour);
        uint turnColour = OverlaySettings.ParseColour(settings.TurnColour);
        if (colour == 0)
        {
            return;
        }

        int drawn = 0;
        foreach (WorldEntity entity in snapshot.Entities)
        {
            if (entity.Aim is not Aim aim)
            {
                continue; // not read for this one - see WorldReader.ReadAim
            }

            if (entity.Kind == EntityKind.Player && !settings.ShowPlayer)
            {
                continue;
            }

            // "Not quiet" rather than "dangerous": an animation the table has no name for
            // counts as something happening, because a marker that vanishes on an unrecognised
            // id reads as nothing being there. See AnimationNames.IsQuiet.
            if (settings.OnlyWhileActing && aim.Animation >= 0 && Animations.IsQuiet(aim.Animation))
            {
                continue;
            }

            if (!DrawRay(draw, snapshot, entity, aim.Angle, colour, settings, width, height))
            {
                continue;
            }

            // The turn, when there is one, in its own colour. Drawn AFTER so it lands on top of
            // the pose it is leaving - it is the half worth reading.
            if (settings.ShowTurn && aim.IsTurning && turnColour != 0)
            {
                DrawRay(draw, snapshot, entity, aim.Turning, turnColour, settings, width, height);
            }

            if (settings.ShowAction && aim.Animation >= 0)
            {
                DrawAction(draw, snapshot, entity, aim, colour, width, height);
            }

            if (++drawn >= MostRays)
            {
                return;
            }
        }
    }

    /// <summary>
    /// One ray, from the entity's feet along an angle. False when it cannot be placed.
    /// </summary>
    /// <remarks>
    /// BOTH ENDS ARE PROJECTED rather than one end plus a screen-space angle, and that is the
    /// whole reason this looks right: the camera is an isometric projection, so a world
    /// direction does not keep its angle on screen. A ray drawn at the world angle from a
    /// projected origin points somewhere the monster is not looking - and it would look
    /// plausible, which is worse.
    /// </remarks>
    private static bool DrawRay(
        ImDrawListPtr draw,
        WorldSnapshot snapshot,
        WorldEntity entity,
        float angle,
        uint colour,
        AimSettings settings,
        int width,
        int height)
    {
        ScreenPoint from = WorldToScreen.Project(
            snapshot.Matrix, entity.WorldX, entity.WorldY, entity.WorldZ, width, height);

        if (!from.OnScreen)
        {
            return false;
        }

        (float aheadX, float aheadY) =
            Facing.Ahead(entity.WorldX, entity.WorldY, angle, settings.Length);

        ScreenPoint to = WorldToScreen.Project(
            snapshot.Matrix, aheadX, aheadY, entity.WorldZ, width, height);

        if (!to.OnScreen)
        {
            return false;
        }

        var start = new Vector2(from.X, from.Y);
        var end = new Vector2(to.X, to.Y);
        draw.AddLine(start, end, colour, settings.Thickness);
        draw.AddCircleFilled(start, RootDot, colour, 8);

        // An arrow head, so a ray says which way along itself it points. Two short lines back
        // from the tip, rotated in SCREEN space - by this stage the direction is a screen
        // direction and no longer a world one.
        Vector2 along = end - start;
        float length = along.Length();
        if (length > 1f)
        {
            Vector2 unit = along / length;
            var side = new Vector2(-unit.Y, unit.X);
            float head = Math.Min(length * HeadShare, 12f);
            draw.AddLine(end, end - (unit * head) + (side * head * 0.5f), colour, settings.Thickness);
            draw.AddLine(end, end - (unit * head) - (side * head * 0.5f), colour, settings.Thickness);
        }

        return true;
    }

    /// <summary>Writes what the entity is doing, beside where it is pointing.</summary>
    /// <remarks>
    /// The ID travels with the name on purpose. The name table came from the player's own
    /// animations and its use on MONSTERS rests on the ids being the game's global CastType
    /// rather than a per-monster list - which is reasonable and unproven. A number beside every
    /// name is what would make a table that does not apply obvious at a glance.
    /// </remarks>
    private void DrawAction(
        ImDrawListPtr draw,
        WorldSnapshot snapshot,
        WorldEntity entity,
        Aim aim,
        uint colour,
        int width,
        int height)
    {
        ScreenPoint at = WorldToScreen.Project(
            snapshot.Matrix, entity.WorldX, entity.WorldY, entity.HealthBarZ, width, height);

        if (!at.OnScreen)
        {
            return;
        }

        string label = $"{Animations.Label(aim.Animation)} ({aim.Animation})";
        draw.AddText(new Vector2(at.X + 8f, at.Y - 18f), colour, ImGuiText.Escape(label));
    }
}
