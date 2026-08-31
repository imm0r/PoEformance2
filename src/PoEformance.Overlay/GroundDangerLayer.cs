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

        int drawn = DrawKnownGroundEffects(draw, snapshot, width, height);

        if (!Settings.ShowGroundDanger)
        {
            return;
        }

        IReadOnlyList<GroundDangerRule> rules = Settings.GroundDangerOrDefault;
        if (rules.Count == 0)
        {
            return;
        }

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

    /// <summary>
    /// Rings every entity the GAME calls a ground effect, and says how long it has left.
    /// </summary>
    /// <remarks>
    /// THE DIFFERENCE FROM THE RULES ABOVE is worth being precise about, because both draw
    /// circles on the floor and only one of them can be wrong about what it is drawing. A rule
    /// matches a metadata path somebody typed: it fires on whatever happens to start with that
    /// text, misses anything nobody thought of, and cannot say anything about the patch beyond
    /// "it matched". This asks the entity whether it carries a GroundEffect component, which is
    /// the game's own answer to the same question, and reads the countdown out of it.
    ///
    /// That is the shape of mistake this project has already paid for once - a whole feature
    /// built on the user describing something the game names itself. The rules stay because
    /// they still cover what the component does not: a Firewall or an ice crystal is a hostile
    /// effect wearing a monster's components, and carries no GroundEffect.
    ///
    /// THE TIMER READS 0.0 FOR A BEAT before the ring goes. That is not a rounding artefact -
    /// the game's countdown reaches zero a measured 0.38 s before it stops listing the entity.
    /// Left visible rather than hidden below some floor, because "about to be safe" is exactly
    /// the moment the number is worth reading.
    /// </remarks>
    private int DrawKnownGroundEffects(ImDrawListPtr draw, WorldSnapshot snapshot, int width, int height)
    {
        if (!Settings.ShowGroundEffects)
        {
            return 0;
        }

        uint colour = OverlaySettings.ParseColour(Settings.GroundEffectColour);
        if (colour == 0)
        {
            return 0;
        }

        int drawn = 0;
        foreach (WorldEntity entity in snapshot.Entities)
        {
            // Remembered sightings are ground that is safe again - the one mistake this
            // feature must not make. The component's own countdown cannot save a remembered
            // entity either: it is the last value read before the thing expired.
            if (entity.IsRemembered || entity.GroundSeconds is not { } seconds)
            {
                continue;
            }

            ScreenPoint at = WorldToScreen.Project(
                snapshot.Matrix, entity.WorldX, entity.WorldY, entity.WorldZ, width, height);
            if (!at.OnScreen)
            {
                continue;
            }

            var centre = new Vector2(at.X, at.Y);
            draw.AddCircle(centre, Settings.GroundEffectRadius, colour, Segments, 2f);

            if (Settings.ShowGroundEffectTimer)
            {
                string text = seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                Vector2 size = ImGui.CalcTextSize(text);
                draw.AddText(centre - (size / 2), colour, text);
            }

            if (++drawn >= MostRings)
            {
                break;
            }
        }

        return drawn;
    }
}
