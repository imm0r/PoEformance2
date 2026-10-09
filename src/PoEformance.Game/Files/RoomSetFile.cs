using System.Globalization;

namespace PoEformance.Game.Files;

/// <summary>One room a room set offers the generator: the file, its weight in the draw, and the ways round it may be laid.</summary>
/// <param name="Weight">The room's weight, or null where the line writes none.</param>
/// <param name="Arm">The room's <c>.arm</c>, with forward slashes.</param>
/// <param name="Rotations">The laying tokens after it - <c>I</c>, <c>FI</c>, <c>R180</c>, <c>FR180</c> and the like - or none.</param>
public readonly record struct RoomSetEntry(int? Weight, string Arm, IReadOnlyList<string> Rotations);

/// <summary>
/// A room set (<c>.rs</c>): every room an area's generator may lay - <c>generate.rs</c> beside the area's <c>master.tsi</c>.
/// </summary>
/// <remarks>
/// WHY THIS IS READ. The rooms the tool placed were the <c>.arm</c> files in the area's loaded-file
/// list, and in The Assembly that list named ten of the set's fifty-three. The list counts files
/// loaded since the area change; a room already in memory from the last instance of the same map -
/// the boss room, the entrance - is not loaded again and is not listed. The set is the area's own
/// statement of what may stand in it, and it IS in the list, being read anew for every area.
///
/// THE FORMAT, as poe_data_tools reads it (<c>file_parsers/rs</c>): a <c>version</c> line, then one
/// room a line - an optional weight, the quoted <c>.arm</c>, and the laying tokens after it - with
/// blank lines and <c>//</c> comments skipped. RePoE's world_areas parser reads the same file by
/// another road - WorldAreas.dat → Topologies.dat → the <c>.dgr</c> → its master <c>.tsi</c> → the
/// master's RoomSet - into its graph JSON's room_set: fifty-three rooms for VaalFactory, the
/// boss room and the entrance among them. That road needs no loaded-file list at all; this one
/// needs the list to name the set, which it did in both captures of The Assembly.
/// </remarks>
public sealed class RoomSetFile
{
    /// <summary>Nothing read.</summary>
    public static RoomSetFile None { get; } = new() { Why = "nothing to read" };

    /// <summary>The file's version line.</summary>
    public int Version { get; private init; }

    /// <summary>The rooms, in the file's order.</summary>
    public IReadOnlyList<RoomSetEntry> Rooms { get; private init; } = [];

    /// <summary>Why the file did not read, or empty.</summary>
    public string Why { get; private init; } = string.Empty;

    /// <summary>Whether the file read.</summary>
    public bool Ready => Why.Length == 0;

    /// <summary>Reads the file's bytes. Never throws.</summary>
    public static RoomSetFile Read(byte[]? content)
        => content is not { Length: > 0 } ? None : Parse(StatDescriptionFiles.Decode(content));

    /// <summary>Reads the text. Public so the format can be tested without an install. Never throws.</summary>
    public static RoomSetFile Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return None;
        }

        int version = -1;
        var rooms = new List<RoomSetEntry>();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (version < 0)
            {
                if (!line.StartsWith("version ", StringComparison.Ordinal) || !int.TryParse(line.AsSpan(8).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out version))
                {
                    return new RoomSetFile { Why = $"no version line first: \"{line}\"" };
                }

                continue;
            }

            int open = line.IndexOf('"');
            int close = open >= 0 ? line.IndexOf('"', open + 1) : -1;
            if (close < 0)
            {
                return new RoomSetFile { Version = version, Why = $"a room line with no quoted file: \"{line}\"" };
            }

            string arm = line[(open + 1)..close].Replace('\\', '/');
            if (!arm.EndsWith(".arm", StringComparison.OrdinalIgnoreCase))
            {
                return new RoomSetFile { Version = version, Why = $"a room line naming no .arm: \"{line}\"" };
            }

            int? weight = null;
            string before = line[..open].Trim();
            if (before.Length > 0)
            {
                if (!int.TryParse(before, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
                {
                    return new RoomSetFile { Version = version, Why = $"a room line whose weight is not a number: \"{line}\"" };
                }

                weight = parsed;
            }

            string[] rotations = line[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            rooms.Add(new RoomSetEntry(weight, arm, rotations));
        }

        return version < 0 ? new RoomSetFile { Why = "no version line" } : new RoomSetFile { Version = version, Rooms = rooms };
    }
}
