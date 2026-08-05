namespace PoEformance.Game.World;

/// <summary>
/// The patch grid the area's outline is drawn as, and where each patch's edges fall.
/// </summary>
/// <remarks>
/// The map transform is affine at a FIXED ground height, so flat ground is exactly one quad
/// and there is nothing here to compute. Height is what breaks that: the transform measures
/// it against the player, so a single height draws the whole area at the player's own
/// elevation and the outline slides against the game's map as they walk up a staircase.
///
/// Splitting the surface into patches lets the height vary between them while each patch
/// stays affine. This is only the arithmetic of that split - which patches exist and where
/// their edges land - kept apart from the drawing so it can be tested without a GPU.
/// </remarks>
/// <param name="PatchesX">Patches across. 1 when there is no height to vary.</param>
/// <param name="PatchesY">Patches down.</param>
/// <param name="ExtentX">Grid cells the mesh spans across - what the texture covers.</param>
/// <param name="ExtentY">Grid cells the mesh spans down.</param>
public readonly record struct TerrainMesh(int PatchesX, int PatchesY, int ExtentX, int ExtentY)
{
    /// <summary>
    /// Splits an area into patches, one per terrain tile, capped.
    /// </summary>
    /// <param name="maxPatches">
    /// Most patches along either axis. A cap rather than one per tile throughout: a large
    /// area is a few hundred tiles across, and past this the extra corners move the outline
    /// by less than a pixel while multiplying the projections that place them.
    /// </param>
    /// <param name="extentX">
    /// How much of the grid the texture actually covers. Thinning floors the pixel count, so
    /// this can be slightly less than the grid's width - and using the grid's width here
    /// instead would stretch the image over ground it has no pixels for.
    /// </param>
    public static TerrainMesh For(TerrainGrid grid, int maxPatches, int extentX, int extentY)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPatches, 1);

        extentX = Math.Max(1, extentX);
        extentY = Math.Max(1, extentY);

        // No heights means flat, and flat is one exact quad - the drawing this replaced.
        if (!grid.HasHeights)
        {
            return new TerrainMesh(1, 1, extentX, extentY);
        }

        int cells = TerrainGrid.CellsPerTile;
        return new TerrainMesh(
            Math.Clamp((extentX + cells - 1) / cells, 1, maxPatches),
            Math.Clamp((extentY + cells - 1) / cells, 1, maxPatches),
            extentX,
            extentY);
    }

    /// <summary>Corner columns - one more than the patches they bound.</summary>
    public int CornersX => PatchesX + 1;

    public int CornersY => PatchesY + 1;

    /// <summary>
    /// Grid column of a patch edge, from 0 at the first to the full extent at the last.
    /// </summary>
    /// <remarks>
    /// Divided out each time rather than stepped by a fixed width. A step is the extent
    /// divided DOWN, so the last edge stops short of the area by up to one patch - a strip of
    /// map that is never drawn, invisible in the middle and exactly at the edges, which is
    /// where the outline is being compared against the game's own.
    /// </remarks>
    public int EdgeX(int index) => (int)((long)Math.Clamp(index, 0, PatchesX) * ExtentX / PatchesX);

    public int EdgeY(int index) => (int)((long)Math.Clamp(index, 0, PatchesY) * ExtentY / PatchesY);

    /// <summary>Texture coordinate of a patch edge - 0 at the first, 1 at the last.</summary>
    public float U(int index) => EdgeX(index) / (float)ExtentX;

    public float V(int index) => EdgeY(index) / (float)ExtentY;
}
