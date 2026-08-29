using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.World;

namespace PoEformance.Overlay;

/// <summary>
/// Marks the ground where an incoming action is going to land.
/// </summary>
/// <remarks>
/// THE POINT IS THE POINT. Every other warning this tool draws is a direction - a ray out of a
/// monster saying where it is pointing - and a direction cannot say where a slam will fall. The
/// Actor's action carries a PLACE, settled against the game over 210 monster arrivals, so this
/// draws that place and nothing more.
///
/// WHAT IS NOT DRAWN, deliberately: the attack's AREA. Nothing here knows how wide a slam is -
/// the game's skill data has it and this tool does not read it - so a ring sized to look like
/// the danger zone would be an invention, and a confident-looking one. The marker is a fixed
/// screen size that says "here", and the colour says whether "here" is close enough to the
/// player to matter. Somebody reading the overlay can tell the difference between a mark and a
/// measurement; a made-up radius would take that away from them.
///
/// THE HEIGHT is the monster's own base, not the target's. Terrain height at another point is a
/// read this does not do, and over the few metres an action reaches the ground is near enough
/// flat that the marker lands where the eye expects it.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class EvasionLayer
{
    /// <summary>Most markers drawn in one frame - a cap on a count that a breach makes large.</summary>
    private const int MostMarkers = 48;

    /// <summary>Radius of the dot at the monster end of a line.</summary>
    private const float RootDot = 2.5f;

    /// <summary>How many segments a ring gets. 0 lets ImGui choose from the radius.</summary>
    private const int Segments = 0;

    /// <summary>
    /// What to draw and how, asked for afresh each frame.
    /// </summary>
    /// <remarks>
    /// A function rather than a stored copy, because the config window edits the PLANNER's
    /// settings from its own thread while this draws on the render thread. A copy here would go
    /// stale the moment somebody changed a colour or a floor, and the symptom - a switch that
    /// does nothing until the tool is restarted - is one this codebase has no way of showing.
    /// </remarks>
    public Func<EvasionSettings>? Settings { get; set; }

    /// <summary>
    /// The threats to draw, as the planner last decided them.
    /// </summary>
    /// <remarks>
    /// A function rather than a list, and it returns the LAST EVALUATED tick rather than a fresh
    /// one - the same arrangement the rule layer uses. The renderer redraws at VSync while the
    /// planner decides once per read, so evaluating here would both cost a re-decision per frame
    /// and let the dodge cooldown be consumed sixty times a second.
    /// </remarks>
    public Func<IReadOnlyList<Threat>>? Threats { get; set; }

    /// <summary>Draws a marker at every incoming action's landing spot.</summary>
    public void Draw(ImDrawListPtr draw, WorldSnapshot snapshot, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (Settings is null || Threats is null)
        {
            return;
        }

        EvasionSettings settings = Settings();
        if (!settings.WarnOrDefault.Enabled)
        {
            return;
        }

        uint marker = OverlaySettings.ParseColour(settings.MarkerColour);
        uint aimed = OverlaySettings.ParseColour(settings.AimedColour);
        if (marker == 0 && aimed == 0)
        {
            return;
        }

        int drawn = 0;
        foreach (Threat threat in Threats())
        {
            uint colour = threat.Aimed ? aimed : marker;
            if (colour == 0)
            {
                continue;
            }

            ScreenPoint at = WorldToScreen.Project(
                snapshot.Matrix, threat.TargetX, threat.TargetY, threat.TargetZ, width, height);

            if (!at.OnScreen)
            {
                continue;
            }

            var centre = new Vector2(at.X, at.Y);
            draw.AddCircle(centre, settings.MarkerRadius, colour, Segments, settings.Thickness);

            // A cross through it, so a marker is legible against a floor already covered in
            // effects - a ring alone disappears into a breach.
            float arm = settings.MarkerRadius * 0.6f;
            draw.AddLine(centre with { X = centre.X - arm }, centre with { X = centre.X + arm }, colour, settings.Thickness);
            draw.AddLine(centre with { Y = centre.Y - arm }, centre with { Y = centre.Y + arm }, colour, settings.Thickness);

            if (settings.ShowLine)
            {
                DrawLineFromMonster(draw, snapshot, threat, centre, colour, settings, width, height);
            }

            if (settings.ShowName)
            {
                string label = threat.Animation == PoEformance.Game.Components.AnimationKind.Unknown
                    ? threat.Name
                    : $"{threat.Name} ({threat.Animation})";
                draw.AddText(centre with { X = centre.X + settings.MarkerRadius + 4f, Y = centre.Y - 8f },
                    colour, ImGuiText.Escape(label));
            }

            if (++drawn >= MostMarkers)
            {
                return;
            }
        }
    }

    /// <summary>Joins the monster to the place its action lands, so a marker has an owner.</summary>
    /// <remarks>
    /// Both ends projected, never a screen-space angle from one of them: the camera is an
    /// isometric projection, so a world direction does not keep its angle on screen. The same
    /// trap the aim rays document.
    /// </remarks>
    private static void DrawLineFromMonster(
        ImDrawListPtr draw,
        WorldSnapshot snapshot,
        Threat threat,
        Vector2 target,
        uint colour,
        EvasionSettings settings,
        int width,
        int height)
    {
        ScreenPoint from = WorldToScreen.Project(
            snapshot.Matrix, threat.MonsterX, threat.MonsterY, threat.TargetZ, width, height);

        if (!from.OnScreen)
        {
            return;
        }

        var start = new Vector2(from.X, from.Y);
        draw.AddLine(start, target, colour, settings.Thickness);
        draw.AddCircleFilled(start, RootDot, colour, 8);
    }
}
