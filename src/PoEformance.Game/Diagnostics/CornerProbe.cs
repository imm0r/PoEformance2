using PoEformance.Core.Memory;

namespace PoEformance.Game.Diagnostics;

/// <summary>
/// What the three bytes at each TILE CORNER hold, and whether any of them names a ground type.
/// </summary>
/// <remarks>
/// WHY THIS CORNER AND NOT ANOTHER FIELD. A room file carries a ground type and a height per
/// CORNER of every slot - that is RePoE's parser talking, <c>RePoE/poe/file/arm.py</c>, not a
/// reading taken off the files by eye - and the terrain struct has an array at <c>+0x50</c> of
/// exactly <c>(tilesX+1) * (tilesY+1) * 3</c> bytes, three per tile corner. Two independent
/// measurements of the same shape. If one of those three bytes indexes the area's own
/// <c>GroundTypeFiles</c>, then a room's corner pattern is a stamp that could be searched for in
/// the area's - which is the one route to placing a room that has not been ruled out.
///
/// THE PREVIOUS ROUTE DIED HERE, and the manner of its death is the design of this. A landscape
/// nibble was taken for an index into that same list and was not one: 9190252 of 9212535 cells
/// carried a value beyond the list. What settled it was counting every value that occurs rather
/// than only the ones that fit, so that is what this does from the start - three lanes, every
/// value, and how many fall outside the list.
///
/// A DIAGNOSTIC, not a feature. It reads once per area under --debug, its lines go where the
/// room probe's go, and it draws nothing on the map. Nothing is concluded here: the numbers are
/// printed and a person decides.
/// </remarks>
public sealed class CornerProbe
{
    /// <summary>Bytes per tile corner, which is what makes the array's size recognisable.</summary>
    public const int BytesPerCorner = 3;

    /// <summary>
    /// The biggest array to read. A corner array is small - 21648 bytes over 87x81 tiles - and
    /// the cap is here so a drifted offset cannot ask for a gigabyte, not because a real one
    /// approaches it. It also keeps the read inside what a recording will hold, which is the
    /// whole point of running it under --debug.
    /// </summary>
    public const int MostBytes = 1 << 20;

    private readonly IMemoryReader _reader;

    public CornerProbe(IMemoryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
    }

    /// <summary>
    /// Reads the corner array and says what each of its three lanes holds.
    /// </summary>
    /// <param name="cornerVector">Address of the vector's begin/end pair - TerrainMetadata+0x50.</param>
    /// <param name="types">
    /// The area's ground-type files, in the order an index would name them. Blank entries are
    /// real: every area's list starts with one, and a value of zero means no type rather than
    /// the first.
    /// </param>
    public IReadOnlyList<string> Probe(
        ulong cornerVector, int tilesX, int tilesY, IReadOnlyList<string> types)
    {
        ArgumentNullException.ThrowIfNull(types);

        if (tilesX <= 0 || tilesY <= 0)
        {
            return ["corners: no tile count"];
        }

        ulong first = _reader.ReadPointer(cornerVector);
        ulong last = _reader.ReadPointer(cornerVector + 8);
        if (first == 0 || last <= first)
        {
            return ["corners: empty vector"];
        }

        long size = (long)(last - first);
        long corners = (long)(tilesX + 1) * (tilesY + 1);

        // THE SHAPE IS THE IDENTIFICATION. An array that is not three bytes per tile corner is
        // not the array this is about, and reading it anyway is how a wrong offset produces a
        // plausible histogram of nothing.
        if (size != corners * BytesPerCorner)
        {
            return [$"corners: {size} bytes, not {corners * BytesPerCorner}"
                + $" for {tilesX + 1}x{tilesY + 1} corners - not that array"];
        }

        if (size > MostBytes)
        {
            return [$"corners: {size} bytes is past the cap"];
        }

        var data = new byte[size];
        if (!_reader.TryRead(first, data))
        {
            return [$"corners: unreadable at {first:X} for {size} bytes"];
        }

        var lines = new List<string>(BytesPerCorner + 1)
        {
            $"corners: {corners} at {tilesX + 1}x{tilesY + 1}, {types.Count} ground-type slots",
        };

        for (int lane = 0; lane < BytesPerCorner; lane++)
        {
            lines.Add(Lane(data, corners, lane, types));
        }

        return lines;
    }

    /// <summary>
    /// One lane's distinct values, how much of the area each covers, and what the list calls it.
    /// </summary>
    /// <remarks>
    /// EVERY VALUE, including the ones no list entry can name. Folding those into a single
    /// "beyond the list" count is what left the landscape grid's verdict unactionable: it said
    /// the pairing was wrong and nothing about what the values were, and the values are what
    /// decides whether a lane is an index, a height, or a flag.
    ///
    /// Three values printed, because a lane that is an index into a list of seven does not need
    /// more than that to be recognisable, and a lane that is a height will show its spread in
    /// the distinct count alone.
    /// </remarks>
    private static string Lane(byte[] data, long corners, int lane, IReadOnlyList<string> types)
    {
        var counts = new long[256];
        for (long c = 0; c < corners; c++)
        {
            counts[data[(c * BytesPerCorner) + lane]]++;
        }

        int distinct = 0;
        long beyond = 0;
        for (int value = 0; value < counts.Length; value++)
        {
            if (counts[value] == 0)
            {
                continue;
            }

            distinct++;
            if (value >= types.Count)
            {
                beyond += counts[value];
            }
        }

        var top = new List<int>(distinct);
        for (int value = 0; value < counts.Length; value++)
        {
            if (counts[value] > 0)
            {
                top.Add(value);
            }
        }

        top.Sort((left, right) => counts[right].CompareTo(counts[left]));

        var said = new List<string>(3);
        foreach (int value in top.Take(3))
        {
            string name = value < types.Count
                ? (types[value].Length > 0 ? Name(types[value]) : "blank slot")
                : "beyond";
            said.Add($"{value}={counts[value]} {name}");
        }

        // "0 outside" is the shape an index has; anything else says this lane is not one. Kept
        // as a count rather than a verdict - see the class note on what this does not conclude.
        return $"  byte {lane}: {distinct} values, {beyond} outside the list"
            + (said.Count > 0 ? "  [" + string.Join(", ", said) + "]" : string.Empty);
    }

    /// <summary>The type's file stem, since a full path leaves no room for the numbers.</summary>
    private static string Name(string path)
    {
        int slash = path.LastIndexOf('/');
        string name = slash >= 0 && slash < path.Length - 1 ? path[(slash + 1)..] : path;
        int dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }
}
