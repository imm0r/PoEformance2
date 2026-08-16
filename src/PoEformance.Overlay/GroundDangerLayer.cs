using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.World;

namespace PoEformance.Overlay;

/// <summary>
/// Rings the ground effects a person has said they want to see coming.
/// </summary>
/// <remarks>
/// PORTED FROM the GameHelper2 Tracker plugin's GroundEffectLogic. Its value is the half of the
/// game that is hard to see: a patch of damaging ground under a fight is drawn as one more
/// bright thing among the spell effects, and the first sign of it is usually the health bar.
///
/// WHY IT CAN DRAW NOTHING WHILE BEING CONFIGURED CORRECTLY, which is the question this feature
/// generates: the entities it rings have to be in the snapshot, and the reader drops two classes
/// of them on purpose. A hostile thing that expires on its own and cannot be targeted is
/// reclassified and dropped unless <see cref="WorldReader.KeepEffects"/> is on - that rule is
/// what stops a Firewall build covering its own screen - and the noise filter refuses the
/// engine's <c>/fx/</c> and <c>/mat/</c> nodes before their components are read at all. The
/// tracker's tab says both, next to the switch.
///
/// THE RADIUS IS IN SCREEN PIXELS. It comes from the reference, whose figures are pixel
/// figures; a world-unit radius would need a second projected point and would shrink as the
/// camera pulls back, which is a different feature.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class GroundDangerLayer
{
    /// <summary>Most rings drawn in one frame. A backstop on a count that is unbounded.</summary>
    /// <remarks>
    /// A prefix typed one character short - <c>Metadata/Effects</c> rather than the whole path -
    /// matches every effect entity in the area, and there are hundreds. The cap turns that from
    /// a frame nobody can afford into a screen somebody can see is wrong.
    /// </remarks>
    private const int MostRings = 300;

    /// <summary>How many segments a ring is drawn with. 0 lets ImGui choose from the radius.</summary>
    private const int Segments = 0;

    /// <summary>What to ring, and how. Shared with the tracker's other layers.</summary>
    public TrackerSettings Settings { get; set; } = TrackerSettings.Default;

    /// <summary>Draws a ring on every entity a rule is watching for.</summary>
    public void Draw(ImDrawListPtr draw, WorldSnapshot snapshot, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!Settings.ShowGroundDanger)
        {
            return;
        }

        IReadOnlyList<GroundDangerRule> rules = Settings.GroundDangerOrDefault;
        if (rules.Count == 0)
        {
            return;
        }

        int drawn = 0;
        foreach (WorldEntity entity in snapshot.Entities)
        {
            // A SIGHTING has a position and no longer has the thing that was standing at it.
            // Ground effects expire, so a remembered one is a ring around ground that is safe
            // again - which is the one mistake this feature must not make.
            if (entity.IsRemembered)
            {
                continue;
            }

            GroundDangerRule? found = null;
            foreach (GroundDangerRule rule in rules)
            {
                if (rule.Matches(entity))
                {
                    found = rule;
                    break;
                }
            }

            if (found is not GroundDangerRule danger)
            {
                continue;
            }

            uint colour = OverlaySettings.ParseColour(danger.Colour);
            if (colour == 0)
            {
                continue;
            }

            // The entity's own base height: this is a ring drawn ON THE GROUND, and the ground
            // is where Render puts it.
            ScreenPoint at = WorldToScreen.Project(
                snapshot.Matrix, entity.WorldX, entity.WorldY, entity.WorldZ, width, height);

            if (!at.OnScreen)
            {
                continue;
            }

            var centre = new Vector2(at.X, at.Y);
            if (danger.Filled)
            {
                draw.AddCircleFilled(centre, danger.Radius, colour, Segments);
            }
            else
            {
                draw.AddCircle(centre, danger.Radius, colour, Segments, danger.Thickness);
            }

            if (++drawn >= MostRings)
            {
                return;
            }
        }
    }
}
