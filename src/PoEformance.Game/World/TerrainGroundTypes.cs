namespace PoEformance.Game.World;

/// <summary>
/// What KIND of ground is under each tile, in the game's own words.
/// </summary>
/// <remarks>
/// WHERE THIS LIVES, after a round of looking in the wrong place. The area lists its ground-type
/// files at <c>TerrainMetadata+0x68</c> and carries one index per TILE CORNER in the array at
/// <c>+0x50</c> - three bytes each, of which BYTE 0 is the type. That is measured, not assumed:
/// on two complete corner arrays (5472 of 5472 corners in one area, 3721 of 3721 in another) the
/// value range tracks the list length exactly, both ways, every slot the list holds is used,
/// nothing falls outside it, and the values drawn as characters form connected regions that read
/// as a map rather than as noise.
///
/// WHY IT IS THE CORNER ARRAY AND NOT SOMETHING ELSE THAT LOOKS LIKE ONE: a room file carries a
/// ground type and a height per CORNER of every slot - RePoE's <c>arm.py</c> talking, not a
/// reading taken by eye - and this array is exactly three bytes per tile corner. Two independent
/// measurements of the same shape, which is why the size check below is the whole identification.
///
/// GridLandscapeData WAS CREDITED WITH THIS AND DOES NOT HAVE IT. Its nibble is a fixed 0-5
/// constant unrelated to the per-area list: 9190252 of 9212535 cells named a type beyond the 5
/// the area listed. That theory was built, shipped and removed before a histogram said so, which
/// is why <see cref="Lines"/> counts every value that occurs rather than only the ones that fit.
///
/// PER CORNER, so a tile takes the type most of its four corners agree on. The corners outnumber
/// the tiles by one each way, which is what identifies the array in the first place.
///
/// IT IS STILL NOT TAKEN ON FAITH. <see cref="Note"/> carries two checks the reading has to
/// survive and the layer that draws this refuses to draw when they fail - see
/// <see cref="Trusted"/>. A wrong offset here would produce a plausible-looking map of nonsense,
/// which is the exact failure this project has already paid for twice.
/// </remarks>
public sealed class TerrainGroundTypes
{
    /// <summary>Bytes per tile corner. Byte 0 is the ground type; 1 and 2 are not read here.</summary>
    public const int BytesPerCorner = 3;

    /// <summary>Grid cells per tile, so a corner can be asked whether it can be stood on.</summary>
    private const int Cells = TerrainGrid.CellsPerTile;

    /// <summary>Values a byte can hold - the histogram counts all of them, not just the named.</summary>
    private const int MostValues = 256;

    /// <summary>
    /// Longest ground-type list worth believing, matching the schema's cap on the vector.
    /// </summary>
    /// <remarks>
    /// A guard against a drifted offset rather than a real bound: the areas measured so far list
    /// four to seven. It is NOT sixteen any more - that number came from the dead nibble theory,
    /// where the container was what limited the index. A byte is the container now.
    /// </remarks>
    public const int MostTypes = 64;

    private readonly byte[] _corners;
    private readonly int _tilesX;
    private readonly int _tilesY;

    private TerrainGroundTypes(
        IReadOnlyList<string> types,
        byte[] corners,
        int tilesX,
        int tilesY,
        int[] tileType,
        long outOfRange,
        IReadOnlyList<long> walkableCorners,
        IReadOnlyList<long> totalCorners,
        string note,
        IReadOnlyList<string> lines)
    {
        Types = types;
        _corners = corners;
        _tilesX = tilesX;
        _tilesY = tilesY;
        TileType = tileType;
        OutOfRange = outOfRange;
        WalkableCorners = walkableCorners;
        TotalCorners = totalCorners;
        Note = note;
        Lines = lines;
    }

    /// <summary>The area's ground-type files, in the order byte 0 indexes them.</summary>
    public IReadOnlyList<string> Types { get; }

    /// <summary>The type most of each tile's four corners agree on, row by row.</summary>
    /// <remarks>
    /// PER TILE rather than per corner, and the loss of resolution is deliberate. A tile is what
    /// the map already labels, what the rooms are grouped by, and the resolution a word can be
    /// read at. It is also what a room file's own grid is measured in, which is where this goes
    /// next: a room's corner pattern is a stamp to search the area's array for.
    ///
    /// Ties go to the first corner in sw, se, nw, ne order. A tile whose corners split two-two is
    /// a boundary tile and either answer is as true as the other.
    /// </remarks>
    public int[] TileType { get; }

    /// <summary>
    /// Corners whose byte names no slot in the list. THE CHECK THAT MATTERS.
    /// </summary>
    /// <remarks>
    /// If the vector at +0x68 is not the list this array indexes, this is where it shows: a wrong
    /// list is almost certainly a shorter or longer one, and every corner above its length lands
    /// here. Zero is the only passing answer, and a wrong offset cannot fake it for a whole area.
    /// </remarks>
    public long OutOfRange { get; }

    /// <summary>
    /// Every value that occurs, with how much ground it covers and how much can be stood on.
    /// </summary>
    /// <remarks>
    /// WHAT A ONE-LINE VERDICT CANNOT SAY. "9190252 cells name a type beyond the 5 the area
    /// lists" reports that a pairing is wrong and nothing whatever about the values - which is
    /// the only thing that decides what to do next. The walkable share is what gives an unnamed
    /// value meaning without any name at all: whatever is never walkable is the void or the abyss.
    ///
    /// NO PER-CENT SIGNS IN THESE STRINGS, and that is not a style choice. ImGui's text is printf
    /// underneath, so a stray '%' in a diagnostic line is read as a conversion: an earlier version
    /// of this histogram rendered "0x0.000fc2f6fe8cap-1022" where a share belonged and left the
    /// NEXT number standing under the wrong label. Every walkable figure it ever displayed was a
    /// different number wearing that name. Counts avoid the question entirely.
    /// </remarks>
    public IReadOnlyList<string> Lines { get; }

    /// <summary>Walkable corners per value, with <see cref="TotalCorners"/> beside it.</summary>
    /// <remarks>
    /// THE SECOND CHECK, and the one a wrong reading cannot pass by luck. If byte 0 really names
    /// the ground, the types must SEPARATE on walkability: an abyss is walkable nowhere and a
    /// fill is walkable nearly everywhere. Noise would put every type at the area's average
    /// instead, because it would be sampling the same ground at random. The SPREAD between the
    /// types is the evidence, not any one number.
    /// </remarks>
    public IReadOnlyList<long> WalkableCorners { get; }

    /// <summary>How many corners each value covers at all.</summary>
    public IReadOnlyList<long> TotalCorners { get; }

    /// <summary>What the read found, and whether it can be believed. Never empty.</summary>
    public string Note { get; }

    /// <summary>
    /// True when both checks passed and this is safe to draw.
    /// </summary>
    /// <remarks>
    /// Not merely "it read something": a plausible map of nonsense is worse than no map, because
    /// nothing about it looks wrong. The spread test is the one with teeth - one type almost
    /// entirely walkable and another almost entirely not is a fact about the ground that a
    /// mis-indexed byte has no way to produce.
    /// </remarks>
    public bool Trusted { get; private init; }

    /// <summary>The type at one tile CORNER, or -1 outside the array.</summary>
    public int At(int cornerX, int cornerY)
    {
        if (cornerX < 0 || cornerY < 0 || cornerX > _tilesX || cornerY > _tilesY)
        {
            return -1;
        }

        long index = ((((long)cornerY * (_tilesX + 1)) + cornerX) * BytesPerCorner);
        return index >= 0 && index < _corners.LongLength ? _corners[index] : -1;
    }

    /// <summary>
    /// Works the per-tile types out of the raw corner array.
    /// </summary>
    /// <param name="types">
    /// The area's ground-type files, in list order. BLANK ENTRIES ARE POSITIONS, not holes: every
    /// area's list starts with one, so a value of zero means "no ground type here" rather than
    /// "the first type". Dropping the blank would shift every value above it onto another name.
    /// </param>
    /// <param name="corners">
    /// The raw array at TerrainMetadata+0x50, three bytes per tile corner.
    /// </param>
    /// <param name="walkable">
    /// The area's walkability, for the spread check. Optional: without it the check cannot run,
    /// and the result is reported untrusted rather than quietly assumed good.
    /// </param>
    public static TerrainGroundTypes? From(
        IReadOnlyList<string> types,
        byte[] corners,
        int tilesX,
        int tilesY,
        TerrainGrid? walkable = null)
    {
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(corners);

        long across = tilesX + 1L;
        long down = tilesY + 1L;

        // THE SIZE IS THE IDENTIFICATION, and it is the gate that licenses everything after it.
        // An array that is not three bytes per tile corner is not this array, and histogramming
        // it anyway is how a drifted offset draws a convincing map of something else.
        if (types.Count is 0 or > MostTypes
            || tilesX <= 0 || tilesY <= 0
            || corners.LongLength != across * down * BytesPerCorner)
        {
            return null;
        }

        // EVERY VALUE A BYTE CAN HOLD, not just the ones the list has room for. Folding the rest
        // into one number is what left the landscape grid's verdict unactionable - it said the
        // pairing was wrong and nothing about what the values were, which cost a second trip to
        // the game to find out.
        var walkableCorners = new long[MostValues];
        var totalCorners = new long[MostValues];
        long outOfRange = 0;

        for (long cornerY = 0; cornerY < down; cornerY++)
        {
            long row = cornerY * across * BytesPerCorner;

            // WALKABILITY AT THE CORNER'S OWN CELL. A corner sits where four tiles meet, so its
            // cell is the tile boundary - tile index times the cells in a tile. The LAST row and
            // column of corners have no tile beyond them, and their ground is the last cell of
            // the tile before: without that step back, two whole edges of the area would count
            // as unwalkable and drag every edge type's share down for no reason.
            int cellY = (int)(cornerY == tilesY ? (cornerY * Cells) - 1 : cornerY * Cells);

            for (long cornerX = 0; cornerX < across; cornerX++)
            {
                int value = corners[row + (cornerX * BytesPerCorner)];
                totalCorners[value]++;
                if (value >= types.Count)
                {
                    outOfRange++;
                }

                if (walkable is not null)
                {
                    int cellX = (int)(cornerX == tilesX ? (cornerX * Cells) - 1 : cornerX * Cells);
                    if (walkable.IsWalkable(cellX, cellY))
                    {
                        walkableCorners[value]++;
                    }
                }
            }
        }

        var tileType = new int[tilesX * tilesY];
        for (int tileY = 0; tileY < tilesY; tileY++)
        {
            long low = ((long)tileY * across) * BytesPerCorner;
            long high = low + (across * BytesPerCorner);

            for (int tileX = 0; tileX < tilesX; tileX++)
            {
                long at = (long)tileX * BytesPerCorner;

                // A TILE TAKES WHAT ITS FOUR CORNERS AGREE ON. Counted by comparison rather than
                // through a 256-entry tally cleared per tile: four values need six comparisons,
                // and an area is tens of thousands of tiles.
                int sw = corners[low + at];
                int se = corners[low + at + BytesPerCorner];
                int nw = corners[high + at];
                int ne = corners[high + at + BytesPerCorner];

                int best = sw;
                int most = 1 + (se == sw ? 1 : 0) + (nw == sw ? 1 : 0) + (ne == sw ? 1 : 0);

                int count = 1 + (sw == se ? 1 : 0) + (nw == se ? 1 : 0) + (ne == se ? 1 : 0);
                if (count > most) { best = se; most = count; }

                count = 1 + (sw == nw ? 1 : 0) + (se == nw ? 1 : 0) + (ne == nw ? 1 : 0);
                if (count > most) { best = nw; most = count; }

                count = 1 + (sw == ne ? 1 : 0) + (se == ne ? 1 : 0) + (nw == ne ? 1 : 0);
                if (count > most) { best = ne; }

                tileType[(tileY * tilesX) + tileX] = best;
            }
        }

        bool spread = Separates(types, walkableCorners, totalCorners);
        bool trusted = outOfRange == 0 && walkable is not null && spread;

        return new TerrainGroundTypes(
            types, corners, tilesX, tilesY, tileType, outOfRange, walkableCorners, totalCorners,
            Describe(types, outOfRange, walkableCorners, totalCorners, walkable is not null, spread),
            Histogram(types, walkableCorners, totalCorners, walkable is not null, tilesX, tilesY))
        {
            Trusted = trusted,
        };
    }

    /// <summary>True when this slot of the list names a file, rather than being a blank one.</summary>
    /// <remarks>
    /// Every area's list starts with a blank, and a corner value of zero therefore means "no
    /// ground type here". It is a position in the list rather than a hole in it, so it is kept -
    /// dropping it would shift every value above it onto another type's name.
    /// </remarks>
    public bool Names(int type) => (uint)type < (uint)Types.Count && Types[type].Length > 0;

    /// <summary>
    /// Whether the types disagree about walkability enough to be real.
    /// </summary>
    /// <remarks>
    /// The bar is one type mostly walkable and another mostly not, among types big enough to mean
    /// anything. A mis-read array samples the same ground for every type and lands them all on
    /// the area's average, which fails this; a correct one has the abyss at nearly nought and the
    /// fill at nearly one. Deliberately loose - a check against noise, not a measurement.
    ///
    /// THE BLANK SLOT IS EXCLUDED, and leaving it in would quietly gut this. It covers the void
    /// outside the playable area, which is walkable nowhere - so it satisfies the "mostly not"
    /// half for free, and the gate would then be asking only whether ANY type is walkable. A
    /// check half of which passes by construction is most of the way to no check.
    /// </remarks>
    private static bool Separates(
        IReadOnlyList<string> types, IReadOnlyList<long> walkable, IReadOnlyList<long> total)
    {
        // Corners, not cells: an area is thousands of corners rather than millions of cells, so
        // the old cell-era threshold of 1024 would have silenced every type in a small area.
        // Sixty-four corners is about an eight-by-eight block of tiles - enough to mean something.
        const int Enough = 64;
        bool high = false;
        bool low = false;

        for (int type = 0; type < total.Count; type++)
        {
            if (total[type] < Enough || type >= types.Count || types[type].Length == 0)
            {
                continue;
            }

            double share = walkable[type] / (double)total[type];
            high |= share > 0.8;
            low |= share < 0.2;
        }

        return high && low;
    }

    private static string Describe(
        IReadOnlyList<string> types,
        long outOfRange,
        IReadOnlyList<long> walkable,
        IReadOnlyList<long> total,
        bool haveWalkable,
        bool spread)
    {
        int named = 0;
        foreach (string type in types)
        {
            if (type.Length > 0)
            {
                named++;
            }
        }

        if (outOfRange > 0)
        {
            return $"{outOfRange} corners name a type beyond the {types.Count} the area lists"
                + " - the list and the array do not belong together";
        }

        if (!haveWalkable)
        {
            return $"{named} ground types, unchecked - no walkable grid to compare against";
        }

        if (!spread)
        {
            return $"{named} ground types, but they do not separate on walkability"
                + " - which is what a mis-read array looks like";
        }

        int walkableTypes = 0;
        for (int type = 0; type < total.Count; type++)
        {
            if (type < types.Count && types[type].Length > 0
                && total[type] > 0 && walkable[type] > total[type] / 2)
            {
                walkableTypes++;
            }
        }

        return $"{named} ground types, {walkableTypes} of them ground you can stand on";
    }

    /// <summary>
    /// One line per value that occurs: how much ground, how much of it walkable, and its name.
    /// </summary>
    /// <remarks>
    /// Sorted by coverage, because the question this answers is what the array IS - and the
    /// values covering a hundred corners between them are noise beside the one covering seven
    /// thousand.
    ///
    /// THE AREA'S OWN WALKABLE COUNT LEADS, without which no row below it means anything. A value
    /// that is walkable nearly everywhere says nothing until you know whether the area is - the
    /// first version printed the rows without it and left exactly that unanswerable.
    ///
    /// SLOTS rather than "named types", which the first version called them: the list has a blank
    /// first entry, so the two numbers differ and printing one under the other's name is how a
    /// person reads five names into a list of four.
    /// </remarks>
    private static IReadOnlyList<string> Histogram(
        IReadOnlyList<string> types,
        IReadOnlyList<long> walkable,
        IReadOnlyList<long> total,
        bool haveWalkable,
        int tilesX,
        int tilesY)
    {
        long everything = 0;
        long stood = 0;
        for (int value = 0; value < total.Count; value++)
        {
            everything += total[value];
            stood += walkable[value];
        }

        if (everything == 0)
        {
            return [];
        }

        var order = new List<int>(16);
        for (int value = 0; value < total.Count; value++)
        {
            if (total[value] > 0)
            {
                order.Add(value);
            }
        }

        order.Sort((left, right) => total[right].CompareTo(total[left]));

        int named = 0;
        foreach (string type in types)
        {
            if (type.Length > 0)
            {
                named++;
            }
        }

        var lines = new List<string>(order.Count + 1)
        {
            $"{everything} corners over {tilesX}x{tilesY} tiles, "
            + (haveWalkable ? $"{stood} of them walkable, " : string.Empty)
            + $"{types.Count} slots ({named} named)",
        };

        foreach (int value in order)
        {
            string name = value < types.Count
                ? (types[value].Length > 0 ? NameFor(types[value]) : "(blank slot)")
                : "(beyond the list)";

            string stand = haveWalkable
                ? $"{walkable[value],7} walkable"
                : "walkability unknown";

            lines.Add($"  {value,2}  {total[value],8} corners  {stand}   {name}");
        }

        return lines;
    }

    /// <summary>The type's short name - its file stem, which is what a label has room for.</summary>
    public static string NameFor(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        int slash = path.LastIndexOf('/');
        string name = slash >= 0 && slash < path.Length - 1 ? path[(slash + 1)..] : path;
        int dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }
}
