namespace PoEformance.Game.World;

/// <summary>
/// What KIND of ground is under each tile, in the game's own words.
/// </summary>
/// <remarks>
/// THREE FACTS MET, and none of them was a guess. The room probe found a vector of the area's
/// ground-type files at <c>TerrainMetadata+0x68</c>; the schema has recorded
/// <c>GridLandscapeData</c> since July as "static terrain-type nibbles 0-5", a value per cell;
/// and opening a <c>.gt</c> out of the bundles showed what one is - a NAMED ground type
/// (<c>bone_fill.gt</c> declares "BoneUpperFill", <c>bone_abyss.gt</c> declares "BoneAbyssFill").
/// A third recording then walked the vector's elements out: six of them for a Badlands area -
/// bone_fill, trims1, bone_abyss, badlands_noburrow, waypoint_ground, badlands - as eight-byte
/// pointers to file objects whose path sits at +0x08, the same shape a tile's TgtFilePtr has.
/// Six files against nibbles 0-5, arrived at from opposite directions.
///
/// So a nibble is an INDEX into that list, and the map can say "this is the abyss, that is the
/// waypoint" rather than naming the tile template that happens to draw it. That is what the
/// .arm route was after and could not reach: a room file names its ground types and never its
/// tiles, which killed the chain room-to-tile and handed over this one instead.
///
/// IT IS NOT TAKEN ON FAITH. <see cref="Note"/> carries two checks the reading has to survive,
/// and the layer that draws this refuses to draw when they fail - see <see cref="Trusted"/>.
/// A wrong offset here would produce a plausible-looking map of nonsense, which is the exact
/// failure this project has paid for before.
/// </remarks>
public sealed class TerrainGroundTypes
{
    /// <summary>Grid cells per tile, so a tile's own type can be taken from its cells.</summary>
    private const int Cells = TerrainGrid.CellsPerTile;

    /// <summary>A nibble holds 0-15, so no more types than that can be indexed.</summary>
    public const int MostTypes = 16;

    private readonly byte[] _cells;
    private readonly int _bytesPerRow;

    private TerrainGroundTypes(
        IReadOnlyList<string> types,
        byte[] cells,
        int bytesPerRow,
        int[] tileType,
        int outOfRange,
        IReadOnlyList<int> walkableCells,
        IReadOnlyList<int> totalCells,
        string note)
    {
        Types = types;
        _cells = cells;
        _bytesPerRow = bytesPerRow;
        TileType = tileType;
        OutOfRange = outOfRange;
        WalkableCells = walkableCells;
        TotalCells = totalCells;
        Note = note;
    }

    /// <summary>The area's ground-type files, in the order the nibbles index them.</summary>
    public IReadOnlyList<string> Types { get; }

    /// <summary>
    /// The type that covers most of each tile, row by row, or -1 where the tile has none.
    /// </summary>
    /// <remarks>
    /// PER TILE rather than per cell, and that is a deliberate loss of resolution. A cell is
    /// half a metre and there are millions of them; a tile is what the map already labels, what
    /// the rooms are grouped by, and the resolution a person can read a word at. It is also
    /// what a room file's own grid is measured in, which is where this goes next.
    /// </remarks>
    public int[] TileType { get; }

    /// <summary>
    /// Cells whose nibble names no type in the list. THE CHECK THAT MATTERS.
    /// </summary>
    /// <remarks>
    /// If the vector at +0x68 is not the list these nibbles index, this is where it shows: a
    /// wrong list is almost certainly a shorter or longer one, and every cell above its length
    /// lands here. Zero is the only passing answer, and a wrong offset cannot fake it for a
    /// whole area.
    /// </remarks>
    public int OutOfRange { get; }

    /// <summary>Walkable cells per type, and <see cref="TotalCells"/> beside it.</summary>
    /// <remarks>
    /// THE SECOND CHECK, and the one a wrong reading cannot pass by luck. If a nibble really
    /// names the ground, the types must SEPARATE on walkability: an abyss is walkable nowhere
    /// and a fill is walkable nearly everywhere. Noise would put every type at the area's
    /// average instead, because it would be sampling the same ground at random. So the spread
    /// between the types is the evidence, not any one number.
    /// </remarks>
    public IReadOnlyList<int> WalkableCells { get; }

    /// <summary>How many cells each type covers at all.</summary>
    public IReadOnlyList<int> TotalCells { get; }

    /// <summary>What the read found, and whether it can be believed. Never empty.</summary>
    public string Note { get; }

    /// <summary>
    /// True when both checks passed and this is safe to draw.
    /// </summary>
    /// <remarks>
    /// Not merely "it read something": a plausible map of nonsense is worse than no map,
    /// because nothing about it looks wrong. The spread test is the one with teeth - one type
    /// almost entirely walkable and another almost entirely not is a fact about the ground that
    /// a mis-indexed nibble has no way to produce.
    /// </remarks>
    public bool Trusted { get; private init; }

    /// <summary>The type under one grid CELL, or -1 outside the area.</summary>
    public int At(int x, int y)
    {
        if (x < 0 || y < 0)
        {
            return -1;
        }

        int index = (y * _bytesPerRow) + (x >> 1);
        if ((uint)index >= (uint)_cells.Length)
        {
            return -1;
        }

        byte packed = _cells[index];
        int nibble = (x & 1) == 0 ? packed & 0x0F : packed >> 4;
        return nibble < Types.Count ? nibble : -1;
    }

    /// <summary>
    /// Works the per-tile types out of the raw landscape bytes.
    /// </summary>
    /// <param name="landscape">
    /// The landscape nibbles, packed exactly as the walkable grid is - two cells per byte, even
    /// x in the low nibble. THE CALLER HAS ESTABLISHED THAT, by requiring this buffer to be the
    /// same length as the walkable one; the two grids cover the same ground with the same row
    /// stride, so a different length means a different thing is being read and the whole reading
    /// is abandoned rather than reinterpreted.
    /// </param>
    /// <param name="walkable">
    /// The area's walkability, for the spread check. Optional: without it the check cannot run,
    /// and the result is reported as untrusted rather than quietly assumed good.
    /// </param>
    public static TerrainGroundTypes? From(
        IReadOnlyList<string> types,
        byte[] landscape,
        int bytesPerRow,
        int tilesX,
        int tilesY,
        TerrainGrid? walkable = null)
    {
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(landscape);

        if (types.Count is 0 or > MostTypes || tilesX <= 0 || tilesY <= 0 || bytesPerRow <= 0)
        {
            return null;
        }

        var counts = new int[types.Count];
        var walkableCells = new int[types.Count];
        var totalCells = new int[types.Count];
        var tileType = new int[tilesX * tilesY];
        int outOfRange = 0;

        for (int tileY = 0; tileY < tilesY; tileY++)
        {
            for (int tileX = 0; tileX < tilesX; tileX++)
            {
                Array.Clear(counts);

                int fromX = tileX * Cells;
                int fromY = tileY * Cells;

                for (int y = fromY; y < fromY + Cells; y++)
                {
                    for (int x = fromX; x < fromX + Cells; x++)
                    {
                        int index = (y * bytesPerRow) + (x >> 1);
                        if ((uint)index >= (uint)landscape.Length)
                        {
                            continue;
                        }

                        byte packed = landscape[index];
                        int nibble = (x & 1) == 0 ? packed & 0x0F : packed >> 4;
                        if (nibble >= types.Count)
                        {
                            outOfRange++;
                            continue;
                        }

                        counts[nibble]++;
                        totalCells[nibble]++;
                        if (walkable is not null && walkable.IsWalkable(x, y))
                        {
                            walkableCells[nibble]++;
                        }
                    }
                }

                int best = -1;
                int most = 0;
                for (int type = 0; type < counts.Length; type++)
                {
                    if (counts[type] > most)
                    {
                        most = counts[type];
                        best = type;
                    }
                }

                tileType[(tileY * tilesX) + tileX] = best;
            }
        }

        bool spread = Separates(types, walkableCells, totalCells);
        bool trusted = outOfRange == 0 && walkable is not null && spread;

        return new TerrainGroundTypes(
            types, landscape, bytesPerRow, tileType, outOfRange, walkableCells, totalCells,
            Describe(types, outOfRange, walkableCells, totalCells, walkable is not null, spread))
        {
            Trusted = trusted,
        };
    }

    /// <summary>True when this slot of the list names a file, rather than being a blank one.</summary>
    /// <remarks>
    /// Every area's list starts with a blank, and a landscape nibble of zero therefore means
    /// "no ground type here". It is a position in the list rather than a hole in it, so it is
    /// kept - dropping it would shift every nibble above it onto another type's name.
    /// </remarks>
    public bool Names(int type) => (uint)type < (uint)Types.Count && Types[type].Length > 0;

    /// <summary>
    /// Whether the types disagree about walkability enough to be real.
    /// </summary>
    /// <remarks>
    /// The bar is one type mostly walkable and another mostly not, among types big enough to
    /// mean anything. A mis-read grid samples the same ground for every type and lands them all
    /// on the area's average, which fails this; a correct one has the abyss at nearly nought
    /// and the fill at nearly one. Deliberately loose - it is a check against noise, not a
    /// measurement.
    ///
    /// THE BLANK SLOT IS EXCLUDED, and leaving it in would have quietly gutted this. It covers
    /// the void outside the playable area, which is walkable nowhere - so it satisfies the
    /// "mostly not" half for free, and the gate would then be asking only whether ANY type is
    /// walkable. A check half of which passes by construction is most of the way to no check.
    /// </remarks>
    private static bool Separates(
        IReadOnlyList<string> types, IReadOnlyList<int> walkable, IReadOnlyList<int> total)
    {
        const int Enough = 1024;   // a type covering less than two tiles says nothing either way
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
        int outOfRange,
        IReadOnlyList<int> walkable,
        IReadOnlyList<int> total,
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
            return $"{outOfRange} cells name a type beyond the {types.Count} the area lists"
                + " - the list and the grid do not belong together";
        }

        if (!haveWalkable)
        {
            return $"{named} ground types, unchecked - no walkable grid to compare against";
        }

        if (!spread)
        {
            return $"{named} ground types, but they do not separate on walkability"
                + " - which is what a mis-read grid looks like";
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
