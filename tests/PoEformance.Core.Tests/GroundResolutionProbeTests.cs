using PoEformance.Game.Diagnostics;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Measuring what the per-tile majority costs, on an area built so the answer is known.
/// </summary>
/// <remarks>
/// THE QUESTION. The ground layer reduces each tile's four corners to whichever type most of them
/// agree on, while the game paints each QUARTER with its own corner's type. The reduction cannot
/// blur a boundary the overlay never draws - the layer writes a name at a point - so the only way
/// it changes the map is by DELETING a type: one that never holds a majority anywhere is a word
/// simply absent from the map.
///
/// So the fixture below contains exactly that: a vein of scattered single corners, each surrounded
/// by floor, which wins no tile and would win nine patches at corner resolution. A probe that did
/// not find it would be reporting that the reduction is free when it is not, and a probe that
/// found it in the PLAIN area would be inventing a reason to change working code. Both directions
/// are tested, because only the pair of them says the measurement means anything.
/// </remarks>
public class GroundResolutionProbeTests
{
    private const int Cells = TerrainGrid.CellsPerTile;
    private const int Corner = TerrainGroundTypes.BytesPerCorner;

    private const int TilesX = 20;
    private const int TilesY = 10;

    /// <summary>Where the floor gives way to wall, in corners. Also the walkable boundary.</summary>
    private const int Wall = 14;

    private static readonly string[] Types =
    [
        "Metadata/Terrain/Desert/Badlands/floor.gt",
        "Metadata/Terrain/Desert/Badlands/wall.gt",
        "Metadata/Terrain/Desert/Badlands/vein.gt",
    ];

    /// <summary>True where the vein sits: isolated corners, three apart, well inside the floor.</summary>
    /// <remarks>
    /// THREE APART SO NO TILE EVER HOLDS TWO. A tile's four corners span two columns and two rows,
    /// so a spacing of three guarantees each vein corner is alone in every tile it touches - one
    /// corner against three of floor, which the majority always overrules. That is the property
    /// the whole fixture exists to have, and a spacing of two would quietly destroy it.
    ///
    /// Kept off the area's edge as well: a corner on the border owns fewer than four quarters, so
    /// an edge vein would test the region sizes against a different number for a reason that has
    /// nothing to do with the question.
    /// </remarks>
    private static bool Vein(int cornerX, int cornerY)
        => cornerX % 3 == 0 && cornerY % 3 == 0
           && cornerX is >= 3 and <= 9 && cornerY is >= 3 and <= 9;

    private static byte[] Corners(bool withVein)
    {
        int across = TilesX + 1;
        var corners = new byte[across * (TilesY + 1) * Corner];

        for (int cornerY = 0; cornerY <= TilesY; cornerY++)
        {
            for (int cornerX = 0; cornerX <= TilesX; cornerX++)
            {
                int at = ((cornerY * across) + cornerX) * Corner;
                corners[at] = withVein && Vein(cornerX, cornerY)
                    ? (byte)2
                    : (byte)(cornerX < Wall ? 0 : 1);

                // The other two lanes carry something that is NOT the type, so a probe reading
                // the wrong one comes back with a different answer rather than the same one.
                corners[at + 1] = 0x7F;
                corners[at + 2] = 0x40;
            }
        }

        return corners;
    }

    /// <summary>Walkable exactly where the floor is, so the two types separate cleanly.</summary>
    private static byte[] WalkableCells(out int stride)
    {
        int width = TilesX * Cells;
        stride = (width + 1) / 2;
        var cells = new byte[stride * TilesY * Cells];

        for (int y = 0; y < TilesY * Cells; y++)
        {
            for (int x = 0; x < Wall * Cells; x++)
            {
                cells[(y * stride) + (x >> 1)] |= (byte)((x & 1) == 0 ? 1 : 1 << 4);
            }
        }

        return cells;
    }

    private static TerrainGrid Walkable(TerrainGroundTypes? ground = null)
    {
        byte[] cells = WalkableCells(out int stride);
        return new TerrainGrid(
            cells, stride, TilesY * Cells, TilesX, TilesY,
            heights: null, heightNote: string.Empty, landmarks: null, rooms: null,
            roomProbe: null, ground: ground);
    }

    private static TerrainGroundTypes Read(bool withVein)
        => Assert.IsType<TerrainGroundTypes>(
            TerrainGroundTypes.From(Types, Corners(withVein), TilesX, TilesY, Walkable()));

    private static IReadOnlyList<string> Measure(bool withVein, int minTiles = 1)
    {
        TerrainGrid walkable = Walkable();
        bool[] mask = walkable.WalkableTileMask();
        return GroundResolutionProbe.Measure(
            Read(withVein), TilesX, TilesY, (x, y) => mask[(y * TilesX) + x], minTiles);
    }

    private static string Line(IReadOnlyList<string> lines, string holding)
        => Assert.Single(lines, l => l.Contains(holding, StringComparison.Ordinal));

    [Fact]
    public void TheFixtureItselfHidesTheVeinFromEveryTile()
    {
        // THE FIXTURE'S OWN PREMISE, asserted before anything is measured against it. If a vein
        // corner ever won a tile the probe below would be measuring a different question and
        // would still pass, which is the shape of a check that proves nothing.
        TerrainGroundTypes ground = Read(withVein: true);

        Assert.DoesNotContain(2, ground.TileType);
        Assert.True(ground.Trusted);
        Assert.True(ground.WorthNaming(2), "the vein must survive the layer's own name filter");
    }

    [Fact]
    public void ThePlainAreaLosesNothingToTheMajority()
    {
        // The control, and the direction that matters most: an area whose types meet along one
        // straight edge is exactly where the reduction is free, and a probe that reported a
        // finding here would be an argument for changing code that is working.
        IReadOnlyList<string> lines = Measure(withVein: false);

        Assert.DoesNotContain(lines, l => l.Contains("NAMED NOWHERE TODAY", StringComparison.Ordinal));

        // 20x10 tiles: 19 unanimous per row and one two-two where the floor meets the wall.
        Assert.Contains("190 agree", Line(lines, "corners per tile"));
        Assert.Contains("10 two-two", Line(lines, "corners per tile"));
        Assert.Contains("0 three-one", Line(lines, "corners per tile"));

        // Only the boundary tiles lose anything, and they lose two quarters each.
        Assert.Contains("overrules 20 of 800", Line(lines, "overrules"));
    }

    [Fact]
    public void AVeinTheMajorityErasesIsReportedAsNamedNowhere()
    {
        // THE FINDING THE WHOLE PROBE IS FOR. Nine isolated corners, each owning the four
        // quarters around it, none of them ever a tile's majority: absent from the map today and
        // nine patches at corner resolution.
        IReadOnlyList<string> lines = Measure(withVein: true);
        string vein = Line(lines, "vein");

        Assert.Contains("NAMED NOWHERE TODAY", vein, StringComparison.Ordinal);
        Assert.Contains("0 by tile", vein, StringComparison.Ordinal);
        Assert.Contains("9 by quarter", vein, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAgreementCountsSplitTheTilesExactly()
    {
        // Each of the 9 vein corners touches 4 tiles, and no tile touches two, so 36 tiles are
        // three-one. The floor-to-wall edge is 10 two-two tiles as before, and nothing overlaps.
        IReadOnlyList<string> lines = Measure(withVein: true);
        string split = Line(lines, "corners per tile");

        Assert.Contains("154 agree", split);
        Assert.Contains("36 three-one", split);
        Assert.Contains("10 two-two", split);
        Assert.Contains("0 two-one-one", split);
        Assert.Contains("0 all four different", split);

        // One quarter per three-one tile, two per two-two tile.
        Assert.Contains("overrules 56 of 800", Line(lines, "overrules"));
    }

    [Fact]
    public void TheQuarterThresholdIsScaledByAreaAndNotByCellCount()
    {
        // A quarter is a quarter of a tile's AREA, so the finer grid has to clear four times the
        // cell count to be kept. Without that scaling every patch on the quarter side would look
        // four times bigger than it is, and the comparison would find a gain in every area.
        //
        // Each vein patch is exactly one tile of ground, so it survives a one-tile floor and is
        // dropped by a two-tile one.
        Assert.Contains("9 by quarter", Line(Measure(withVein: true, minTiles: 1), "vein"));
        Assert.DoesNotContain(
            Measure(withVein: true, minTiles: 2),
            l => l.Contains("vein", StringComparison.Ordinal));
    }

    [Fact]
    public void OnlyTheTypesWhoseCountChangesAreListed()
    {
        // The floor and the wall are unaffected by the reduction, so a row for either would be
        // noise in front of the one row that answers the question.
        IReadOnlyList<string> lines = Measure(withVein: true);

        Assert.DoesNotContain(lines, l => l.Contains("floor ", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("wall ", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUntrustedReadIsRefusedRatherThanMeasured()
    {
        // The same refusal the map makes, and for the same reason: a measurement taken off an
        // array that was not believed would be a number about nothing, and printed beside real
        // ones it would read as one of them.
        Assert.Contains(
            "nothing trustworthy",
            Assert.Single(GroundResolutionProbe.Measure(null, TilesX, TilesY, null, 1)));

        // Untrusted because no walkable grid was supplied, so neither check could run.
        TerrainGroundTypes? unverified = TerrainGroundTypes.From(
            Types, Corners(withVein: true), TilesX, TilesY);

        Assert.NotNull(unverified);
        Assert.False(unverified.Trusted);
        Assert.Contains(
            "nothing trustworthy",
            Assert.Single(GroundResolutionProbe.Measure(unverified, TilesX, TilesY, null, 1)));
    }

    [Fact]
    public void AMismatchedTileCountIsSaidRatherThanIndexed()
    {
        // A probe reading past the end of an array is how a drifted offset becomes a crash
        // instead of a report. The sizes have to agree or nothing is measured.
        Assert.Contains(
            "is not",
            Assert.Single(GroundResolutionProbe.Measure(Read(withVein: true), TilesX + 1, TilesY, null, 1)));
    }

    [Fact]
    public void TheMapAndTheProbeApplyTheSameNameFilter()
    {
        // WHAT KEEPS THE COMPARISON HONEST. The probe compares the map against a finer version of
        // itself, which only means anything while both sides decide "worth naming" the same way.
        // The rule lives on TerrainGroundTypes for that reason; this pins that it is the rule the
        // map's own regions are built from.
        TerrainGroundTypes ground = Read(withVein: true);
        TerrainGrid grid = Walkable(ground);

        Assert.True(ground.AnyStandableNamed);
        Assert.False(grid.NamingUnstandableGround);

        // The floor is standable and named, so it is what the map draws; the vein is standable
        // too and draws nowhere only because no TILE carries it.
        Assert.True(ground.WorthNaming(0));
        Assert.False(ground.WorthNaming(1));
        Assert.Contains(grid.GroundRegions, r => r.Path == Types[0]);
        Assert.DoesNotContain(grid.GroundRegions, r => r.Path == Types[2]);
    }
}
