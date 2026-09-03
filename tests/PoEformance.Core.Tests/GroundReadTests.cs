using PoEformance.Core.Schema;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Reading the ground types out of a synthetic process, against the SHIPPED schema.
/// </summary>
/// <remarks>
/// What these are really about is the note, not the reading. The first version of this feature
/// gave up in four places with a bare null, so an area that read nothing looked exactly like an
/// area with nothing to show - the very confusion the checks inside TerrainGroundTypes exist to
/// prevent, reintroduced one level above them. It took a screenshot of an empty map with no
/// explanation under it to notice. So every refusal here is asserted on its sentence.
/// </remarks>
public class GroundReadTests
{
    private const ulong AreaInstance = 0x0000_0300_0000_0000;
    private const ulong GridData = 0x0000_0300_0100_0000;
    private const ulong LandscapeData = 0x0000_0300_0200_0000;
    private const ulong TypeVector = 0x0000_0300_0300_0000;
    private const ulong FirstType = 0x0000_0300_0400_0000;
    private const ulong FirstText = 0x0000_0300_0500_0000;

    private const int TilesX = 3;
    private const int TilesY = 2;

    private static readonly string[] Paths =
    [
        "Metadata/Terrain/Desert/Badlands/bone_fill.gt",
        "Metadata/Terrain/Desert/Badlands/bone_abyss.gt",
    ];

    /// <summary>
    /// An area whose LEFT half is walkable and type 0, and whose right half is neither.
    /// </summary>
    /// <param name="landscapeBytes">
    /// How long to make the landscape vector. Defaults to the walkable grid's own length, which
    /// is the only length the reader accepts - passing a different one is how the check that
    /// licenses reading the second buffer with the first's packing gets exercised.
    /// </param>
    /// <param name="types">How many type pointers to place. Zero leaves the vector empty.</param>
    private static (FakeMemoryReader Reader, OffsetSchema Schema) Area(
        long? landscapeBytes = null, int types = 2, bool namedFiles = true)
    {
        OffsetSchema schema = RealSessionTests.Schema();
        StructDef area = schema.Structs["AreaInstance"];
        StructDef terrain = schema.Structs["TerrainMetadata"];
        ulong terrainBase = AreaInstance + (ulong)area.OffsetOf("TerrainMetadata");

        int width = TilesX * TerrainGrid.CellsPerTile;
        int rows = TilesY * TerrainGrid.CellsPerTile;
        int stride = (width + 1) / 2;

        var walkable = new byte[stride * rows];
        var landscape = new byte[stride * rows];

        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = (y * stride) + (x >> 1);
                int type = x < width / 2 ? 0 : 1;
                landscape[index] |= (byte)((x & 1) == 0 ? type : type << 4);

                if (x < width / 2)
                {
                    walkable[index] |= (byte)((x & 1) == 0 ? 1 : 1 << 4);
                }
            }
        }

        var fake = new FakeMemoryReader()
            .Place(GridData, walkable)
            .Place(LandscapeData, landscape)
            .Place<uint>(AreaInstance + (ulong)area.OffsetOf("CurrentAreaHash"), 0x5150FACE)
            .Place<ulong>(terrainBase + (ulong)terrain.OffsetOf("GridWalkableData"), GridData)
            .Place<ulong>(terrainBase + (ulong)terrain.OffsetOf("GridWalkableData") + 8, GridData + (ulong)walkable.Length)
            .Place<ulong>(terrainBase + (ulong)terrain.OffsetOf("GridLandscapeData"), LandscapeData)
            .Place<ulong>(
                terrainBase + (ulong)terrain.OffsetOf("GridLandscapeData") + 8,
                LandscapeData + (ulong)(landscapeBytes ?? walkable.Length))
            .Place<int>(terrainBase + (ulong)terrain.OffsetOf("BytesPerRow"), stride)
            .Place<long>(terrainBase + (ulong)terrain.OffsetOf("TotalTilesX"), TilesX)
            .Place<long>(terrainBase + (ulong)terrain.OffsetOf("TotalTilesY"), TilesY);

        int typeFiles = terrain.OffsetOf("GroundTypeFiles");
        if (types > 0)
        {
            var pointers = new byte[types * 8];
            for (int i = 0; i < types; i++)
            {
                ulong record = FirstType + ((ulong)i * 0x1000);
                BitConverter.GetBytes(record).CopyTo(pointers, i * 8);

                // The path at +0x08, which is TgtFile's shape - the same one a tile uses, which
                // is why the reader can share its struct and its cache.
                fake.Place(record, new byte[0x40]);
                if (namedFiles)
                {
                    // A PAGE around the characters: the fake reader serves a read only when
                    // every byte was placed, and a bare string would make a long path read as a
                    // truncated one - a property of the fixture, not of the code.
                    ulong text = FirstText + ((ulong)i * 0x1000);
                    fake.Place(text, new byte[1024]);
                    fake.PlaceStdWString(record + 0x08, Paths[i % Paths.Length], text);
                }
            }

            fake.Place(TypeVector, pointers)
                .Place<ulong>(terrainBase + (ulong)typeFiles, TypeVector)
                .Place<ulong>(terrainBase + (ulong)typeFiles + 8, TypeVector + (ulong)pointers.Length);
        }
        else
        {
            fake.Place<ulong>(terrainBase + (ulong)typeFiles, 0UL)
                .Place<ulong>(terrainBase + (ulong)typeFiles + 8, 0UL);
        }

        return (fake, schema);
    }

    private static TerrainGrid Read(FakeMemoryReader fake, OffsetSchema schema)
        => Assert.IsType<TerrainGrid>(new TerrainReader(fake, schema).Read(AreaInstance, nowMs: 0));

    [Fact]
    public void TheGroundTypesComeBackNamedAndBelieved()
    {
        (FakeMemoryReader fake, OffsetSchema schema) = Area();

        TerrainGrid grid = Read(fake, schema);

        Assert.NotNull(grid.Ground);
        Assert.True(grid.Ground!.Trusted);
        Assert.Equal(Paths, grid.Ground.Types);
        Assert.Contains("2 ground types", grid.GroundNote, StringComparison.Ordinal);

        // And the regions the map would label, from the same flood fill the rooms use.
        Assert.Equal(2, grid.GroundRegions.Count);
    }

    [Fact]
    public void ALandscapeOfADifferentLengthIsRefusedAndSaysSo()
    {
        // THE GATE THAT LICENSES EVERYTHING AFTER IT. The nibbles are read with the walkable
        // grid's packing, and equal length is the whole reason that is allowed - so a different
        // length means something else is being read, and reinterpreting it would produce a
        // plausible map of nonsense.
        (FakeMemoryReader fake, OffsetSchema schema) = Area(landscapeBytes: 128);

        TerrainGrid grid = Read(fake, schema);

        Assert.Null(grid.Ground);
        Assert.Contains("not the same grid", grid.GroundNote, StringComparison.Ordinal);
        Assert.Contains("128 bytes against walkable", grid.GroundNote, StringComparison.Ordinal);
        Assert.Empty(grid.GroundRegions);
    }

    [Fact]
    public void AnEmptyTypeVectorSaysThatRatherThanNothing()
    {
        (FakeMemoryReader fake, OffsetSchema schema) = Area(types: 0);

        TerrainGrid grid = Read(fake, schema);

        Assert.Null(grid.Ground);
        Assert.Contains("no ground-type files", grid.GroundNote, StringComparison.Ordinal);
        Assert.Contains("empty vector", grid.GroundNote, StringComparison.Ordinal);
    }

    [Fact]
    public void ATypeThatNamesNoFileNamesTheElementItGaveUpOn()
    {
        // EVERY element or none: a list with a hole in it would shift every nibble above the
        // hole onto the wrong name, which is the one failure a reader of an index table must
        // not have. Which element failed is what makes it findable.
        (FakeMemoryReader fake, OffsetSchema schema) = Area(namedFiles: false);

        TerrainGrid grid = Read(fake, schema);

        Assert.Null(grid.Ground);
        Assert.Contains("element 0 of 2 names no file", grid.GroundNote, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNoteReachesTheReadoutWhetherOrNotThereIsAGround()
    {
        // Both ways round, because the readout is where this gets looked at first.
        (FakeMemoryReader good, OffsetSchema schema) = Area();
        Assert.Contains("ground: 2 ground types", Read(good, schema).Describe(), StringComparison.Ordinal);

        (FakeMemoryReader bad, OffsetSchema other) = Area(types: 0);
        Assert.Contains("ground: no ground-type files", Read(bad, other).Describe(), StringComparison.Ordinal);
    }
}
