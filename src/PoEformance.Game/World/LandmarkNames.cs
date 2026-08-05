using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoEformance.Game.World;

/// <summary>
/// Names for particular terrain tiles, by area.
/// </summary>
/// <remarks>
/// DATA, not code, on the same principle the offsets follow: which tile is "Beira of the
/// Rotten" is knowledge about the game that someone wrote down, and it belongs in a file that
/// can be corrected without a rebuild.
///
/// Ported from the AHK tool's own landmark data. It is worth being honest about its reach:
/// ninety-one areas and it covers FIVE endgame maps, against the hundreds that exist. That is
/// not a gap to be filled by writing more of it - random maps cannot be enumerated - which is
/// why the boss arenas are found by the shape of their tiles instead, and this supplies real
/// names where somebody happens to have written one.
///
/// Keyed the way the reference writes it: the tile's path, then the sub-ids of the particular
/// instance meant. Exact on purpose - a wall tile is reused all over an area, so a name
/// attached to its path alone would appear everywhere it was placed.
/// </remarks>
public sealed class LandmarkNames
{
    private readonly IReadOnlyDictionary<string, Dictionary<string, string>> _byArea;

    private LandmarkNames(IReadOnlyDictionary<string, Dictionary<string, string>> byArea) => _byArea = byArea;

    /// <summary>Nothing known - which is the normal case for an endgame map.</summary>
    public static LandmarkNames Empty { get; } = new(new Dictionary<string, Dictionary<string, string>>());

    /// <summary>How many areas are described.</summary>
    public int Areas => _byArea.Count;

    /// <summary>The names known for one area, or null when it is not described.</summary>
    /// <param name="areaId">The area's own id, as WorldAreas.dat gives it - "G1_2", "MapBluff".</param>
    public IReadOnlyDictionary<string, string>? For(string areaId)
    {
        ArgumentNullException.ThrowIfNull(areaId);
        return _byArea.TryGetValue(areaId, out Dictionary<string, string>? names) ? names : null;
    }

    /// <summary>
    /// Loads the file, or returns <see cref="Empty"/> when it is missing or unreadable.
    /// </summary>
    /// <remarks>
    /// Never throws. These are NAMES: without them a boss arena is still found and still
    /// routed to, it just reads "Boss Arena" instead of what lives in it. Failing the tool
    /// over a decoration would be the wrong trade.
    /// </remarks>
    public static LandmarkNames Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Empty;
            }

            Dictionary<string, Dictionary<string, string>>? read = JsonSerializer.Deserialize(
                File.ReadAllText(path), LandmarkJson.Default.DictionaryStringDictionaryStringString);

            if (read is null)
            {
                return Empty;
            }

            // BOTH levels ignore case, which is not tidiness - it is what makes the data match
            // at all. The tile paths come out of the game's memory and the keys were written
            // down by hand, so the two agree on the letters and not always on their case. The
            // reference lowercases both sides for exactly this reason; matching case-sensitively
            // would find nothing and look like a feature that simply had no data.
            var byArea = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach ((string area, Dictionary<string, string> names) in read)
            {
                byArea[area] = new Dictionary<string, string>(names, StringComparer.OrdinalIgnoreCase);
            }

            return new LandmarkNames(byArea);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return Empty;
        }
    }
}

/// <summary>Source-generated so the landmark file still loads under Native AOT.</summary>
[JsonSerializable(typeof(Dictionary<string, Dictionary<string, string>>))]
internal sealed partial class LandmarkJson : JsonSerializerContext;
