using System.Text.Json.Serialization;

namespace PoEformance.Features;

/// <summary>
/// What was decided about the ground-type names on the map.
/// </summary>
/// <remarks>
/// THE FIRST VERSION OF THIS HAD ONE FILTER, on the reasoning that an area holds a HANDFUL of
/// ground types, so its regions must be few and large and a size threshold would do the whole
/// job. An Abyssal Depths screenshot killed that: two ground types, and maelstrom_abyss written
/// across the map roughly twenty times. FEW TYPES DOES NOT MEAN FEW REGIONS - one type winds
/// through an area in dozens of separate pieces, and every piece is a label carrying the same
/// word. Size cannot thin that, because the pieces are not small.
///
/// So the ground names need the same two filters the room names do, and for the same reason,
/// arrived at from the opposite direction: <see cref="MinTiles"/> drops the slivers where two
/// types meet, and <see cref="MaxPatches"/> stops one type being written twenty times.
/// </remarks>
public sealed record GroundSettings(
    [property: JsonPropertyName("show")] bool Show = false,
    [property: JsonPropertyName("minTiles")] int MinTiles = 4,
    [property: JsonPropertyName("maxPatches")] int MaxPatches = 3)
{
    /// <summary>
    /// Off, regions of four tiles and up, and each type written at most three times.
    /// </summary>
    /// <remarks>
    /// OFF for the reason the room names are: this writes a name on every block of ground in the
    /// area, which is a thing reached for while working out a layout rather than while playing.
    /// Four tiles because the regions that matter are enormous - an abyss, a fill, the walkable
    /// road - and anything under four is the ragged edge between two of them.
    ///
    /// THREE is a starting point rather than a measurement, and it is the honest word for it: the
    /// only figure in hand is the twenty that made a map unreadable. Three names the biggest
    /// pieces of a type - the regions come rarest-first and largest-first, so the ones kept are
    /// the ones somebody can see they are standing in - and leaves the slivers unanswered, which
    /// is the trade a crowded map has to make either way.
    /// </remarks>
    public static GroundSettings Default { get; } = new();

    /// <summary>The same values with anything out of range brought back in.</summary>
    public GroundSettings Normalised()
        => this with
        {
            // Capped rather than merely floored: a threshold above the biggest region hides
            // every label, which reads as the feature being broken rather than as a setting.
            MinTiles = Math.Clamp(MinTiles, 1, 4096),

            // At least one, or switching the layer on would draw nothing at all.
            MaxPatches = Math.Clamp(MaxPatches, 1, 64),
        };
}
