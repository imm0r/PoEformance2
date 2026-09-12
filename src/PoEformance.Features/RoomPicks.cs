using System.Text.Json.Serialization;

namespace PoEformance.Features;

/// <summary>
/// What the user decided about the layout's room names, and which rooms they pinned.
/// </summary>
/// <remarks>
/// Its own object inside the overlay settings rather than four more fields at the top level,
/// for the reason the keep-out zones are one: a settings file stops being readable at the
/// point everything shares one level.
///
/// THE PICKS ARE KEPT PER AREA, by the area's own id - the same key the curated landmark data
/// is filed under. A pinned room is a statement about one layout ("the exit is in
/// exit_01"), and a global list would put Act 2's rooms on Act 3's map. Campaign layouts are
/// fixed, so a pick there is worth keeping for good; an endgame map is generated per instance,
/// so a pick in one is worth exactly as long as that instance - which is the honest limit of
/// what any file can offer.
/// </remarks>
public sealed record RoomSettings(
    [property: JsonPropertyName("show")] bool Show = false,
    [property: JsonPropertyName("minTiles")] int MinTiles = 2,
    [property: JsonPropertyName("filter")] string Filter = "",
    [property: JsonPropertyName("picked")]
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Picked = null,

    // How often one file may be placed in an area before its name stops being worth writing.
    // LAST in the list because it arrived last, and the positional order of a record is what a
    // caller passing arguments by position depends on.
    [property: JsonPropertyName("maxPlacements")] int MaxPlacements = 4)
{
    /// <summary>
    /// Off, rooms of two tiles and up, and files placed at most four times.
    /// </summary>
    /// <remarks>
    /// OFF because this writes a name on every room in the area, which is a great deal of text
    /// over the map and not what most people want most of the time - it is a thing reached for
    /// while working out a layout. Two tiles because a one-tile room is a piece of scenery: a
    /// rock, a rubble patch, a strip of wall, and there are hundreds of them.
    ///
    /// FOUR PLACEMENTS is the number that actually thins the map, and it is
    /// <see cref="Game.World.TerrainLandmarks"/>'s own: a file placed more often than that is a
    /// building block rather than a place. Size cannot do this job - an area is built from one
    /// module repeated, so the threshold in tiles is a cliff rather than a slider, with a map of
    /// solid text on one side of it and four labels on the other.
    /// </remarks>
    public static RoomSettings Default { get; } = new();

    /// <summary>Which rooms are pinned in one area, empty until somebody pins one.</summary>
    public IReadOnlyList<string> In(string areaId)
        => Picked is not null && areaId.Length > 0 && Picked.TryGetValue(areaId, out IReadOnlyList<string>? keys)
            ? keys
            : [];

    /// <summary>
    /// The same settings with one area's picks replaced.
    /// </summary>
    /// <remarks>
    /// An area whose picks are all gone loses its key rather than keeping an empty list, so
    /// visiting a map and unpinning what you pinned leaves the file as it was found.
    /// </remarks>
    public RoomSettings With(string areaId, IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        if (areaId.Length == 0)
        {
            return this;
        }

        var picked = Picked is null
            ? []
            : new Dictionary<string, IReadOnlyList<string>>(Picked, StringComparer.Ordinal);

        if (keys.Count > 0)
        {
            picked[areaId] = keys;
        }
        else
        {
            picked.Remove(areaId);
        }

        return this with { Picked = picked.Count > 0 ? picked : null };
    }

    /// <summary>Keeps the values inside the range the overlay understands.</summary>
    public RoomSettings Normalised()
        => this with
        {
            // Capped rather than merely floored: a threshold above the biggest room in the
            // area hides every label, which reads as the feature being broken.
            MinTiles = Math.Clamp(MinTiles, 1, 64),

            // Up to a thousand, which is "no limit" for any area that exists - a cap that
            // cannot be lifted would leave somebody working out a layout unable to see it.
            MaxPlacements = Math.Clamp(MaxPlacements, 1, 1000),
            Filter = Filter ?? string.Empty,
        };
}
