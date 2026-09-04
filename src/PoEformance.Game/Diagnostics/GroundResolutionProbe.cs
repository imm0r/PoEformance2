using PoEformance.Game.World;

namespace PoEformance.Game.Diagnostics;

/// <summary>
/// How much the ground map loses by reducing each tile's four corners to a majority.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS. <see cref="TerrainGroundTypes"/> holds one ground type per tile CORNER and
/// hands the layer one per TILE, taking whichever type most of the four corners agree on. That
/// reduction is this project's, not the game's: <c>annalithic/poeterrain</c>'s ArmImportComponent
/// builds a tile as a fan of triangles from the four corners to a midpoint and paints each QUARTER
/// with its own corner's type, so the game draws all four and never a majority.
///
/// WHAT THAT COULD COST, and it is one thing rather than a general loss of detail. The layer draws
/// a NAME AT A POINT per region - no fill, no outline - so a sharper boundary would move a label
/// by a few pixels and change nothing a person could see. What a majority can do is DELETE a type:
/// a thin diagonal feature that never holds three of any tile's four corners is absorbed into its
/// neighbour, the region never forms, and the name is simply absent from the map. That is the only
/// way the reduction changes what the map SAYS, and it is what this counts.
///
/// A DIAGNOSTIC, not a feature, and deliberately not a verdict. It reports the numbers at both
/// resolutions and leaves the decision to a person - the same stance <see cref="CornerProbe"/>
/// takes, and for the same reason: a measurement that also concludes is a measurement nobody
/// re-reads.
///
/// IT COMPARES AGAINST WHAT THE LAYER ACTUALLY DRAWS. The tile side runs the same flood fill over
/// the same filter the map uses - <see cref="TerrainGroundTypes.WorthNaming"/>, which is on the
/// ground object precisely so both callers share it. A probe that re-implemented the filter would
/// be comparing the map against a second opinion of the map, which is the class of check this
/// project has already been burned by.
/// </remarks>
public static class GroundResolutionProbe
{
    /// <summary>Quarters per tile - one per corner, which is how the game paints a tile.</summary>
    public const int PerTile = 4;

    /// <summary>
    /// Both resolutions side by side: agreement, what the majority overrules, and the regions.
    /// </summary>
    /// <param name="ground">The area's ground types. Untrusted ones are refused, as the map does.</param>
    /// <param name="tileWalkable">
    /// Whether a tile holds walkable ground, for parity with the map's own call. It decides only
    /// <see cref="TerrainRoom.WalkableTiles"/>, which nothing here reads - passed so the two fills
    /// differ in resolution and in nothing else.
    /// </param>
    /// <param name="minTiles">
    /// The layer's smallest named patch. The quarter side uses four times this, because a quarter
    /// is a quarter of a tile's AREA and comparing raw cell counts across two grids would make the
    /// finer one look like it found four times as much ground.
    /// </param>
    public static IReadOnlyList<string> Measure(
        TerrainGroundTypes? ground,
        int tilesX,
        int tilesY,
        Func<int, int, bool>? tileWalkable,
        int minTiles)
    {
        if (ground is null || !ground.Trusted)
        {
            return ["ground resolution: nothing trustworthy to measure"];
        }

        if (tilesX <= 0 || tilesY <= 0 || ground.TileType.Length != tilesX * tilesY)
        {
            return [$"ground resolution: {ground.TileType.Length} tile types is not {tilesX}x{tilesY}"];
        }

        if (minTiles < 1)
        {
            minTiles = 1;
        }

        // ── How the four corners of a tile split ──────────────────────────────
        long unanimous = 0;
        long threeOne = 0;
        long twoTwo = 0;
        long twoOneOne = 0;
        long allFour = 0;
        long overruled = 0;

        // The quarter grid, laid out as the renderer lays it out: quarter (2x+dx, 2y+dy) of tile
        // (x, y) takes corner (x+dx, y+dy). Filled in the same pass that counts the agreement,
        // because both want the same four reads per tile and an area is tens of thousands of them.
        int wideQuarters = tilesX * 2;
        var quarters = new int[wideQuarters * tilesY * 2];
        var tiles = new int[tilesX * tilesY];

        for (int tileY = 0; tileY < tilesY; tileY++)
        {
            for (int tileX = 0; tileX < tilesX; tileX++)
            {
                int sw = ground.At(tileX, tileY);
                int se = ground.At(tileX + 1, tileY);
                int nw = ground.At(tileX, tileY + 1);
                int ne = ground.At(tileX + 1, tileY + 1);

                int majority = ground.TileType[(tileY * tilesX) + tileX];
                int agreeing = (sw == majority ? 1 : 0) + (se == majority ? 1 : 0)
                    + (nw == majority ? 1 : 0) + (ne == majority ? 1 : 0);

                // WHAT THE REDUCTION THREW AWAY, in the unit the game paints in. A corner that
                // disagrees with its tile's majority is a quarter drawn as one type and reported
                // as another.
                overruled += PerTile - agreeing;

                switch (agreeing)
                {
                    case 4:
                        unanimous++;
                        break;
                    case 3:
                        threeOne++;
                        break;
                    case 2 when (sw == se && nw == ne)
                        || (sw == nw && se == ne)
                        || (sw == ne && se == nw):
                        // Two and two, so the majority is a coin toss between equals.
                        twoTwo++;
                        break;
                    case 2:
                        twoOneOne++;
                        break;
                    default:
                        allFour++;
                        break;
                }

                tiles[(tileY * tilesX) + tileX] = ground.WorthNaming(majority) ? majority : -1;

                int low = (tileY * 2 * wideQuarters) + (tileX * 2);
                quarters[low] = ground.WorthNaming(sw) ? sw : -1;
                quarters[low + 1] = ground.WorthNaming(se) ? se : -1;
                quarters[low + wideQuarters] = ground.WorthNaming(nw) ? nw : -1;
                quarters[low + wideQuarters + 1] = ground.WorthNaming(ne) ? ne : -1;
            }
        }

        // ── The regions each resolution produces ──────────────────────────────
        string[] types = [.. ground.Types];

        List<TerrainRoom> byTile = TerrainRooms.Find(types, tiles, tilesX, tilesY, tileWalkable);
        List<TerrainRoom> byQuarter = TerrainRooms.Find(
            types,
            quarters,
            wideQuarters,
            tilesY * 2,
            tileWalkable is null ? null : (x, y) => tileWalkable(x / 2, y / 2));

        long tileCells = (long)tilesX * tilesY;
        var lines = new List<string>(8)
        {
            $"ground resolution: {tileCells} tiles, {tileCells * PerTile} quarters,"
            + $" {types.Length} slots, smallest patch {minTiles} tiles",

            $"  corners per tile: {unanimous} agree, {threeOne} three-one, {twoTwo} two-two,"
            + $" {twoOneOne} two-one-one, {allFour} all four different",

            $"  the majority overrules {overruled} of {tileCells * PerTile} quarters",
        };

        // A TRUNCATED FILL WOULD BIAS THE COMPARISON, and silently: the quarter grid has four
        // times the cells, so it meets the cap first, and a capped side reports fewer regions
        // for a reason that has nothing to do with resolution.
        if (byTile.Count >= TerrainRooms.MaxRooms || byQuarter.Count >= TerrainRooms.MaxRooms)
        {
            lines.Add($"  CAPPED at {TerrainRooms.MaxRooms} regions - the counts below are floors");
        }

        lines.Add(
            $"  patches worth naming: {Kept(byTile, minTiles)} by tile,"
            + $" {Kept(byQuarter, minTiles * PerTile)} by quarter");

        lines.AddRange(PerType(types, byTile, byQuarter, minTiles));
        return lines;
    }

    /// <summary>Regions the layer would keep, which is the only count worth comparing.</summary>
    private static int Kept(List<TerrainRoom> regions, int least)
    {
        int kept = 0;
        foreach (TerrainRoom region in regions)
        {
            if (region.Tiles >= least)
            {
                kept++;
            }
        }

        return kept;
    }

    /// <summary>
    /// The types whose count changes, and the ones the majority erases from the map entirely.
    /// </summary>
    /// <remarks>
    /// ONLY THE ONES THAT DIFFER, because a type with the same count at both resolutions is
    /// exactly the case where the question does not arise, and a full table would bury the
    /// handful of rows that answer it.
    ///
    /// "NAMED NOWHERE TODAY" is the row that decides the whole question. A type with no patch big
    /// enough at tile resolution and one at quarter resolution is a word that is missing from the
    /// map right now - not a boundary drawn more finely, an absent label. If no area produces one,
    /// the reduction costs nothing a person could notice and there is nothing to change.
    /// </remarks>
    private static IReadOnlyList<string> PerType(
        string[] types, List<TerrainRoom> byTile, List<TerrainRoom> byQuarter, int minTiles)
    {
        var tileCounts = new int[types.Length];
        var quarterCounts = new int[types.Length];

        Tally(types, byTile, minTiles, tileCounts);
        Tally(types, byQuarter, minTiles * PerTile, quarterCounts);

        var lines = new List<string>();
        for (int type = 0; type < types.Length; type++)
        {
            if (tileCounts[type] == quarterCounts[type])
            {
                continue;
            }

            string name = types[type].Length > 0
                ? TerrainGroundTypes.NameFor(types[type])
                : "(blank slot)";

            lines.Add($"  {name,-28} {tileCounts[type],4} by tile  {quarterCounts[type],4} by quarter"
                + (tileCounts[type] == 0 ? "   NAMED NOWHERE TODAY" : string.Empty));
        }

        return lines;
    }

    /// <summary>
    /// Counts the kept regions per type, matched on the path the region carries.
    /// </summary>
    /// <remarks>
    /// By PATH rather than by index, because <see cref="TerrainRooms.Find"/> reports the string it
    /// was given and not the id it was indexed by. A dictionary would be the obvious answer and is
    /// the wrong one here: a list of ground types is a handful of entries, so a scan over it beats
    /// hashing a path per region.
    /// </remarks>
    private static void Tally(string[] types, List<TerrainRoom> regions, int least, int[] counts)
    {
        foreach (TerrainRoom region in regions)
        {
            if (region.Tiles < least)
            {
                continue;
            }

            for (int type = 0; type < types.Length; type++)
            {
                if (string.Equals(types[type], region.Path, StringComparison.Ordinal))
                {
                    counts[type]++;
                    break;
                }
            }
        }
    }
}
