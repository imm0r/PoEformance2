using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.Components;
using PoEformance.Game.World;

namespace PoEformance.Overlay;

/// <summary>
/// Draws a line from the player to every monster worth walking to, coloured by rarity.
/// </summary>
/// <remarks>
/// PORTED FROM the GameHelper2 Tracker plugin's MonsterLineLogic, and the thing it does that a
/// dot cannot: a line has one end where the eye already is. A rare pack leader standing behind
/// its own minions is a dot among forty dots and an obvious line, and the same is true of the
/// unique nobody noticed spawning at the edge of a breach.
///
/// FROM FEET TO FEET, like the reference. Not from the health bar - the line's job is to point
/// at a place on the ground you are going to walk to, and lifting either end to the top of a
/// model makes it point above that place.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class MonsterLineLayer
{
    /// <summary>Most lines drawn in one frame. A backstop on a count that is unbounded.</summary>
    /// <remarks>
    /// Magic monsters are the reason this exists: they are common enough that switching their
    /// lines on in a dense area draws one to almost everything on screen, and the cap is what
    /// keeps that from being a frame nobody can afford rather than merely a busy screen.
    /// </remarks>
    private const int MostLines = 200;

    /// <summary>How thick the lines are drawn, in pixels.</summary>
    private const float Thickness = 1f;

    /// <summary>What to draw and in which colours. Shared with the tracker's other layers.</summary>
    public TrackerSettings Settings { get; set; } = TrackerSettings.Default;

    /// <summary>Draws a line to each monster whose rarity was asked for.</summary>
    public void Draw(ImDrawListPtr draw, WorldSnapshot snapshot, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        MonsterLineSettings lines = Settings.LinesOrDefault;
        if (!lines.Unique && !lines.Rare && !lines.Magic)
        {
            return;
        }

        if (snapshot.Player is not WorldEntity player)
        {
            return;
        }

        ScreenPoint from = WorldToScreen.Project(
            snapshot.Matrix, player.WorldX, player.WorldY, player.WorldZ, width, height);

        if (!from.OnScreen)
        {
            return;   // the camera is not showing the player - there is no end to draw from
        }

        var start = new Vector2(from.X, from.Y);
        int drawn = 0;

        foreach (WorldEntity monster in snapshot.Entities)
        {
            // Friendly excluded for the same reason the health bars exclude them: your own
            // minions and totems are numerous, and a line to each turns the screen into a fan
            // centred on your own character.
            if (monster.Kind != EntityKind.Monster || monster.IsFriendly || monster.IsEffect)
            {
                continue;
            }

            (bool on, string colour) = lines.For(monster.Rarity);
            if (!on)
            {
                continue;
            }

            uint packed = OverlaySettings.ParseColour(colour);
            if (packed == 0)
            {
                continue;   // never chosen, or unreadable - drawing it would be an invisible line
            }

            ScreenPoint to = WorldToScreen.Project(
                snapshot.Matrix, monster.WorldX, monster.WorldY, monster.WorldZ, width, height);

            if (!to.OnScreen)
            {
                continue;
            }

            draw.AddLine(start, new Vector2(to.X, to.Y), packed, Thickness);

            if (++drawn >= MostLines)
            {
                return;
            }
        }
    }
}
