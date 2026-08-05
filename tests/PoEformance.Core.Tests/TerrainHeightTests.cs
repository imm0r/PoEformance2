using System.Numerics;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Ground height per terrain tile, and the patch mesh that exists to make use of it.
/// </summary>
/// <remarks>
/// The reported fault this answers: the outline sat a little off at the map's edges, and it
/// moved whenever the player walked up a staircase or a hill. The map transform measures
/// height against the player, so drawing the whole area at one height is only right while the
/// ground is flat.
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

        // ...which is what makes the mesh collapse back to the single quad it used to be.
        TerrainMesh mesh = TerrainMesh.For(grid, maxPatches: 96, grid.Width, grid.Height);
        Assert.Equal(1, mesh.PatchesX);
        Assert.Equal(1, mesh.PatchesY);
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

    [Fact]
    public void TheMeshSpansTheWholeArea()
    {
        // The one that matters. Stepping by a rounded-down patch width leaves the right and
        // bottom of the area undrawn - up to a whole patch of it - which is invisible in the
        // middle of a map and lands exactly where the edges are, the place the outline is
        // being compared against the game's own.
        TerrainGrid grid = Ground(7, 5, new float[35]);
        TerrainMesh mesh = TerrainMesh.For(grid, maxPatches: 96, grid.Width, grid.Height);

        Assert.Equal(0, mesh.EdgeX(0));
        Assert.Equal(0, mesh.EdgeY(0));
        Assert.Equal(grid.Width, mesh.EdgeX(mesh.PatchesX));
        Assert.Equal(grid.Height, mesh.EdgeY(mesh.PatchesY));

        // ...and the texture is stretched over exactly that span, corner to corner.
        Assert.Equal(0f, mesh.U(0));
        Assert.Equal(1f, mesh.U(mesh.PatchesX));
        Assert.Equal(1f, mesh.V(mesh.PatchesY));
    }

    [Fact]
    public void PatchEdgesNeverGoBackwards()
    {
        // A patch whose right edge is left of its left edge is a quad turned inside out, and
        // it draws as a torn triangle rather than as nothing at all.
        TerrainGrid grid = Ground(37, 11, new float[37 * 11]);
        TerrainMesh mesh = TerrainMesh.For(grid, maxPatches: 96, grid.Width, grid.Height);

        for (int i = 0; i < mesh.PatchesX; i++)
        {
            Assert.True(mesh.EdgeX(i) < mesh.EdgeX(i + 1), $"patch {i} is empty or reversed");
        }

        for (int i = 0; i < mesh.PatchesY; i++)
        {
            Assert.True(mesh.EdgeY(i) < mesh.EdgeY(i + 1), $"row {i} is empty or reversed");
        }
    }

    [Fact]
    public void OnePatchPerTile_UntilTheCap()
    {
        // Tile granularity is the point: that is the resolution the heights arrive at.
        TerrainGrid small = Ground(9, 4, new float[36]);
        TerrainMesh mesh = TerrainMesh.For(small, maxPatches: 96, small.Width, small.Height);
        Assert.Equal(9, mesh.PatchesX);
        Assert.Equal(4, mesh.PatchesY);

        // A large area would otherwise cost tens of thousands of quads for a correction
        // measured in pixels, so the count is capped - and the span still reaches the edge.
        TerrainGrid huge = Ground(300, 200, new float[60_000]);
        TerrainMesh capped = TerrainMesh.For(huge, maxPatches: 96, huge.Width, huge.Height);
        Assert.Equal(96, capped.PatchesX);
        Assert.Equal(96, capped.PatchesY);
        Assert.Equal(huge.Width, capped.EdgeX(capped.PatchesX));
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

    [Fact]
    public void TheMeshSpansWhatTheTextureCovers_NotTheGrid()
    {
        // Thinning floors the pixel count, so a big grid's texture stops a cell or two short
        // of it. Spanning the grid's full width instead stretches the image over ground it
        // has no pixels for, which slides the outline by that difference.
        TerrainGrid grid = Ground(200, 200, new float[40_000]);
        OutlineMask mask = TerrainOutline.Build(grid, maxEdge: 2048);

        int coverX = Math.Min(grid.Width, mask.Width * mask.Step);
        Assert.True(coverX < grid.Width, "fixture is too small to exercise thinning");

        TerrainMesh mesh = TerrainMesh.For(grid, maxPatches: 96, coverX, coverX);
        Assert.Equal(coverX, mesh.EdgeX(mesh.PatchesX));
    }
}
