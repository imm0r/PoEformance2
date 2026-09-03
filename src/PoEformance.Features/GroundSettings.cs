using System.Text.Json.Serialization;

namespace PoEformance.Features;

/// <summary>
/// What was decided about the ground-type names on the map.
/// </summary>
/// <remarks>
/// Small on purpose, and smaller than <see cref="RoomSettings"/> for a reason worth writing
/// down: the room names need two filters because an area holds hundreds of room files placed
/// dozens of times each, and neither size nor repetition alone can thin them. An area holds a
/// HANDFUL of ground types, so its regions are few and large, and a size threshold does the
/// whole job - the only thing it drops is the one-tile sliver where two types meet.
/// </remarks>
public sealed record GroundSettings(
    [property: JsonPropertyName("show")] bool Show = false,
    [property: JsonPropertyName("minTiles")] int MinTiles = 4)
{
    /// <summary>
    /// Off, and regions of four tiles and up.
    /// </summary>
    /// <remarks>
    /// OFF for the reason the room names are: this writes a name on every block of ground in the
    /// area, which is a thing reached for while working out a layout rather than while playing.
    /// Four tiles because the regions that matter are enormous - an abyss, a fill, the walkable
    /// road - and anything under four is the ragged edge between two of them.
    /// </remarks>
    public static GroundSettings Default { get; } = new();

    /// <summary>The same values with anything out of range brought back in.</summary>
    public GroundSettings Normalised()
        => this with
        {
            // Capped rather than merely floored: a threshold above the biggest region hides
            // every label, which reads as the feature being broken rather than as a setting.
            MinTiles = Math.Clamp(MinTiles, 1, 4096),
        };
}
