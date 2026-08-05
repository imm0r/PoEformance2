using System.Numerics;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Ground height: reading it, and the displacement that draws it without bending anything.
/// </summary>
/// <remarks>
/// The reported fault this answers: the outline sat a little off, and it moved whenever the
/// player walked up a staircase or a hill. The map transform measures height against the
/// player, so drawing the whole area at one height is only right while the ground is flat.
/// </remarks>
public class TerrainHeightTests
{
    /// <summary>A grid of open ground, sized in whole tiles, with the heights supplied.</summary>
    private static TerrainGrid Ground(int tilesX, int tilesY, float[]? heights = null)
    {
        int width = tilesX * TerrainGrid.CellsPerTile;
        int height = tilesY * TerrainGrid.CellsPerTile;
        int stride = (width + 1) / 2;

        var cells = new byte[stride * height];
        Array.Fill(cells, (byte)0x11);   // both nibbles set: every cell walkable

        return new TerrainGrid(cells, stride, height, tilesX, tilesY, heights);
    }

    [Fact]
    public void WithoutHeightsTheMapIsDrawnFlat()
    {
        // The read is allowed to fail - a missing pointer, a drifted offset - and when it
        // does the map has to keep working exactly as it did before heights existed.
        TerrainGrid grid = Ground(4, 3);

        Assert.False(grid.HasHeights);
        Assert.Equal(0f, grid.HeightAt(10, 10));
    }

    [Fact]
    public void HeightIsIndexedRowMajorByTileCount()
    {
        // Transposing this is the classic version of the bug and it is nearly invisible on a
        // square map, so the fixture is deliberately not square: 4 tiles across, 3 down, and
        // each tile's height is its own index.
        var heights = new float[12];
        for (int i = 0; i < heights.Length; i++)
        {
            heights[i] = i;
        }

        TerrainGrid grid = Ground(4, 3, heights);
        Assert.True(grid.HasHeights);

        int cells = TerrainGrid.CellsPerTile;

        Assert.Equal(0f, grid.HeightAt(0, 0));
        Assert.Equal(1f, grid.HeightAt(cells, 0));            // one tile right
        Assert.Equal(4f, grid.HeightAt(0, cells));            // one tile down = a full row on
        Assert.Equal(6f, grid.HeightAt(2 * cells, cells));    // row 1, column 2
    }

    [Fact]
    public void EveryCellOfATileSharesThatTilesHeight()
    {
        var heights = new float[] { 10f, 20f, 30f, 40f };
        TerrainGrid grid = Ground(2, 2, heights);

        int cells = TerrainGrid.CellsPerTile;
        Assert.Equal(10f, grid.HeightAt(0, 0));
        Assert.Equal(10f, grid.HeightAt(cells - 1, cells - 1));   // last cell still in tile 0
        Assert.Equal(20f, grid.HeightAt(cells, cells - 1));       // first cell of the next
    }

    [Fact]
    public void OutsideTheGridClampsToTheEdgeTile()
    {
        // Corners are placed AT the grid's edge, so the last one asks for a cell one past the
        // end; clamping keeps that on the edge tile instead of reading past the array.
        var heights = new float[] { 1f, 2f, 3f, 4f };
        TerrainGrid grid = Ground(2, 2, heights);

        Assert.Equal(1f, grid.HeightAt(-50, -50));
        Assert.Equal(4f, grid.HeightAt(grid.Width, grid.Height));
        Assert.Equal(4f, grid.HeightAt(100_000, 100_000));
    }

    /// <summary>The map as it is drawn on: a fixed view, so only the heights vary.</summary>
    private static MapView Map()
        => new(new Vector2(700, 450), 900f, 0.5f, IsLargeMap: true, Visible: true, 0, 0, 1400, 900);

    [Fact]
    public void AWrongReferenceHeightMovesTHE_WHOLE_OutlineTogether()
    {
        // Half of the rule a recording of the map was read with, and worth keeping because
        // the next height fault will be read the same way.
        //
        // The reference is what EVERY point is measured against, so getting it wrong shifts
        // all of them by the same amount - and since it is the PLAYER's height, that shift
        // changes as they walk. A recording showing a uniform offset that grew from nothing
        // on the flat to sixteen pixels on a hill is therefore a statement about the
        // reference, not about the terrain.
        MapView map = Map();
        float[] groundHeights = [0f, 120f, -260f, 45f];

        const float trueReference = 300f;
        const float wrongReference = 300f - 82f;   // the player's sub-tile term, unaccounted

        var shifts = new List<float>();
        for (int i = 0; i < groundHeights.Length; i++)
        {
            float worldX = 1000f + (i * 700f);
            float worldY = 800f - (i * 400f);

            Vector2 right = map.Project(worldX, worldY, groundHeights[i], 500f, 500f, trueReference);
            Vector2 wrong = map.Project(worldX, worldY, groundHeights[i], 500f, 500f, wrongReference);

            Assert.Equal(right.X, wrong.X, 3);   // height never moves a point sideways
            shifts.Add(wrong.Y - right.Y);
        }

        // Every point moved by the SAME amount - which is what "uniform across the frame"
        // in the measurement means, and why it points at the reference.
        Assert.All(shifts, shift => Assert.Equal(shifts[0], shift, 3));
        Assert.NotEqual(0f, shifts[0]);
    }

    [Fact]
    public void AWrongGroundHeightMovesOnlyTheWallThatHasIt()
    {
        // The other half. An error in the per-wall heights - the sub-tile term this does not
        // read - varies from wall to wall by construction, so it shows up as parts of the map
        // disagreeing with each other rather than as a shift of all of it. That is what
        // separates "the reference is wrong" from "the terrain heights are wrong" in a
        // screenshot, without needing to know either value.
        MapView map = Map();

        Vector2 wallA = map.Project(1000f, 800f, 0f, 500f, 500f, 300f);
        Vector2 wallB = map.Project(2000f, 400f, 0f, 500f, 500f, 300f);

        Vector2 wallAOff = map.Project(1000f, 800f, 60f, 500f, 500f, 300f);
        Vector2 wallBSame = map.Project(2000f, 400f, 0f, 500f, 500f, 300f);

        Assert.NotEqual(wallA.Y, wallAOff.Y, 3);   // the wall with the wrong height moved
        Assert.Equal(wallB.Y, wallBSame.Y, 3);     // its neighbour did not
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-242f)]    // the top of the hill the owner measured
    [InlineData(-110f)]
    [InlineData(87f)]      // ground BELOW the player, to pin the sign
    public void MovingACellDiagonallyIsTheSameAsGivingItAHeight(float groundHeight)
    {
        // The identity the whole drawing rests on, and the reason the map needs no mesh.
        //
        //     screen = ((dx - dy) * cos,  (dz - (dx + dy)) * sin)
        //
        // Move a point the same distance along BOTH grid axes and the x term cancels while
        // the y term counts it twice - so a height is exactly a displacement of half that
        // height along the diagonal. Bake it into the picture and the picture can be drawn
        // perfectly flat, exact for every cell rather than for a mesh's corners.
        MapView map = Map();

        const float playerX = 5000f;
        const float playerY = 4000f;
        const float playerGround = -110f;
        const float worldX = 7000f;
        const float worldY = 3000f;

        // What the game does: the point at its real height.
        Vector2 withHeight = map.Project(worldX, worldY, groundHeight, playerX, playerY, playerGround);

        // What this does: the point moved by half its height along the diagonal, drawn flat
        // against the player's own ground.
        float shift = groundHeight / (2f * MapView.HeightToGrid);
        Vector2 displaced = map.Project(
            worldX - (shift * MapView.WorldToGrid),
            worldY - (shift * MapView.WorldToGrid),
            0f, playerX, playerY, playerGround);

        Assert.Equal(withHeight.X, displaced.X, 2);
        Assert.Equal(withHeight.Y, displaced.Y, 2);
    }

    [Fact]
    public void TheDisplacementIsWholeCellsAndCarriesTheGamesSign()
    {
        // Whole cells because the picture has no finer resolution to put it at, and the sign
        // matters: this game counts ground height DOWNWARD, so higher ground is a more
        // negative number - which has to end up drawn HIGHER on the map, not lower.
        TerrainGrid uphill = Ground(2, 2, [-242f, -242f, -242f, -242f]);
        TerrainGrid flat = Ground(2, 2, [0f, 0f, 0f, 0f]);

        int shift = uphill.IsoHeightShift(5, 5);
        Assert.Equal((int)(-242f / (2f * MapView.HeightToGrid)), shift);
        Assert.True(shift < 0, "raised ground displaces the other way");
        Assert.Equal(0, flat.IsoHeightShift(5, 5));

        // ...and with no heights at all, nothing moves.
        Assert.Equal(0, Ground(2, 2).IsoHeightShift(5, 5));
    }
}
