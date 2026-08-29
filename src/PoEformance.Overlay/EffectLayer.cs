using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.World;

namespace PoEformance.Overlay;

/// <summary>
/// Draws the ground effects and effect nodes out in the world. A DEBUGGING TOOL.
/// </summary>
/// <remarks>
/// Everything here is normally thrown away on purpose, twice over: the noise filter refuses
/// the engine's own <c>/fx/</c> and <c>/mat/</c> nodes before their components are read, and
/// the reader drops hostile effects so a Firewall build does not cover its own screen in enemy
/// markers. Both of those are right for playing and useless for looking.
///
/// WHAT IT CAN AND CANNOT SHOW, because the name invites the wrong expectation: the game's
/// actual particles are a rendering matter and are not entities at all - nothing in memory
/// lists a spark. What IS listed is the effect ENTITIES: the ground effects that carry Life and
/// a position, and the engine's asset nodes. A screen of fire is one entity here, not a
/// thousand, and that entity is what gets a mark.
///
/// IN WORLD SPACE rather than on the map, unlike the other debug layers. Where a damaging patch
/// of ground is matters within a few feet, and the map cannot answer at that scale.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class EffectLayer
{
    /// <summary>Most marks drawn in one frame. A backstop on a count that is unbounded.</summary>
    private const int MostMarks = 2_000;

    private static readonly Vector4 HostileMark = new(1f, 0.45f, 0.25f, 0.9f);
    private static readonly Vector4 FriendlyMark = new(0.45f, 0.85f, 1f, 0.9f);
    private static readonly Vector4 NodeMark = new(0.7f, 0.6f, 0.9f, 0.75f);

    /// <summary>Whether to draw at all. Off, because this is for looking rather than playing.</summary>
    public bool Enabled { get; set; }

    /// <summary>Write the metadata path beside each mark. The whole point, and very busy.</summary>
    public bool ShowPaths { get; set; } = true;

    /// <summary>
    /// Whether the READER should keep the hostile ground effects it otherwise drops.
    /// </summary>
    /// <remarks>
    /// NOT a drawing flag, and it sits here anyway - beside the two that are - for the same
    /// reason the projectile layer owns the visual-entity read: the settings are applied before
    /// the reader can be reached, so a value pushed at the reader during Apply would be pushed
    /// at nothing and silently lost. Held here it survives, and <c>EntityOverlay</c> syncs it
    /// every frame, which is also what keeps it right when the switch is moved from the tab.
    ///
    /// Off, and it costs what the tab says: the rule it undoes is what stops a Firewall build
    /// covering its own screen in enemy markers.
    /// </remarks>
    public bool KeepHostile { get; set; }

    /// <summary>Draws a mark on every effect the snapshot carries.</summary>
    /// <param name="project">
    /// The overlay's own world-to-screen, handed over rather than repeated: it is the one piece
    /// of this project that took real work to get right, and a second copy of it would be a
    /// second thing to get wrong.
    /// </param>
    public void Draw(
        ImDrawListPtr draw,
        WorldSnapshot snapshot,
        Func<WorldEntity, (bool OnScreen, float X, float Y)> project)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(project);

        if (!Enabled)
        {
            return;
        }

        int drawn = 0;
        foreach (WorldEntity entity in snapshot.Entities)
        {
            if (!EffectCensus.Is(entity))
            {
                continue;
            }

            (bool onScreen, float x, float y) = project(entity);
            if (!onScreen)
            {
                continue;
            }

            Vector4 colour = ColourOf(entity);
            uint packed = ImGui.ColorConvertFloat4ToU32(colour);
            var at = new Vector2(x, y);

            // A RING rather than a filled dot, so a monster marker underneath one stays
            // readable. These are the most numerous things on the screen when this is on, and
            // a solid mark each would bury everything the overlay is actually for.
            draw.AddCircle(at, 7f, packed, 12, 1.5f);
            draw.AddLine(at - new Vector2(3f, 0f), at + new Vector2(3f, 0f), packed, 1f);
            draw.AddLine(at - new Vector2(0f, 3f), at + new Vector2(0f, 3f), packed, 1f);

            if (ShowPaths)
            {
                draw.AddText(at + new Vector2(9f, -6f), packed, ImGuiText.Tail(entity.Path));
            }

            if (++drawn >= MostMarks)
            {
                return;
            }
        }
    }

    private static Vector4 ColourOf(WorldEntity entity)
        => entity.Kind == EntityKind.Unknown ? NodeMark
            : entity.IsFriendly ? FriendlyMark
            : HostileMark;
}
