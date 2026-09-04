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
    private const ulong CornerData = 0x0000_0300_0200_0000;
    private const ulong TypeVector = 0x0000_0300_0300_0000;
    private const ulong FirstType = 0x0000_0300_0400_0000;
    private const ulong FirstText = 0x0000_0300_0500_0000;

    /// <summary>
    /// Twenty by ten tiles, which is twenty-one by eleven corners.
    /// </summary>
    /// <remarks>
    /// Big enough for the spread check to have something to measure: it ignores a type covering
    /// fewer than 64 corners, so the three-by-two area this fixture used while the types came
    /// from a per-CELL grid would now silence the check rather than exercise it.
    /// </remarks>
    private const int TilesX = 20;
    private const int TilesY = 10;

    private static readonly string[] Paths =
    [
        "Metadata/Terrain/Desert/Badlands/bone_fill.gt",
        "Metadata/Terrain/Desert/Badlands/bone_abyss.gt",
    ];

    /// <summary>
    /// An area whose LEFT half is walkable and one type, and whose right half is neither.
    /// </summary>
    /// <param name="cornerBytes">
    /// How long to make the corner vector. Defaults to three bytes per tile corner, which is the
    /// only length the reader accepts - passing a different one is how the check that identifies
    /// this array as that array gets exercised.
    /// </param>
    /// <param name="types">How many type pointers to place. Zero leaves the vector empty.</param>
    private static (FakeMemoryReader Reader, OffsetSchema Schema) Area(
        long? cornerBytes = null,
        int types = 2,
        bool namedFiles = true,
        bool leadingBlank = false,
        int? leftType = null,
        int? rightType = null,
        bool walkableEverywhere = false)
    {
        OffsetSchema schema = RealSessionTests.Schema();
        StructDef area = schema.Structs["AreaInstance"];
        StructDef terrain = schema.Structs["TerrainMetadata"];
        ulong terrainBase = AreaInstance + (ulong)area.OffsetOf("TerrainMetadata");

        int width = TilesX * TerrainGrid.CellsPerTile;
        int rows = TilesY * TerrainGrid.CellsPerTile;
        int stride = (width + 1) / 2;

        // With a blank first slot the two real types move up to 1 and 2, exactly as they do in
        // a real area - a corner indexes the list by position, so the blank shifts everything.
        // The halves can also be given explicit types, which is how a test puts the BLANK on
        // one side of the map and a named type on the other.
        int blank = leadingBlank ? 1 : 0;
        int left = leftType ?? blank;
        int right = rightType ?? (1 + blank);

        var walkable = new byte[stride * rows];
        int walkableTo = walkableEverywhere ? width : width / 2;
        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < walkableTo; x++)
            {
                int index = (y * stride) + (x >> 1);
                walkable[index] |= (byte)((x & 1) == 0 ? 1 : 1 << 4);
            }
        }

        int across = TilesX + 1;
        var corners = new byte[across * (TilesY + 1) * TerrainGroundTypes.BytesPerCorner];
        for (int cornerY = 0; cornerY <= TilesY; cornerY++)
        {
            for (int cornerX = 0; cornerX <= TilesX; cornerX++)
            {
                int at = ((cornerY * across) + cornerX) * TerrainGroundTypes.BytesPerCorner;
                corners[at] = (byte)(cornerX < TilesX / 2 ? left : right);

                // The other two bytes hold something the type list could never name, so a
                // reading that took the wrong lane would fail loudly rather than agree.
                corners[at + 1] = 0x7F;
                corners[at + 2] = 0x40;
            }
        }

        var fake = new FakeMemoryReader()
            .Place(GridData, walkable)
            .Place(CornerData, corners)
            .Place<uint>(AreaInstance + (ulong)area.OffsetOf("CurrentAreaHash"), 0x5150FACE)
            .Place<ulong>(terrainBase + (ulong)terrain.OffsetOf("GridWalkableData"), GridData)
            .Place<ulong>(terrainBase + (ulong)terrain.OffsetOf("GridWalkableData") + 8, GridData + (ulong)walkable.Length)
            .Place<ulong>(terrainBase + (ulong)terrain.OffsetOf("TileCornerData"), CornerData)
            .Place<ulong>(
                terrainBase + (ulong)terrain.OffsetOf("TileCornerData") + 8,
                CornerData + (ulong)(cornerBytes ?? corners.Length))
            .Place<int>(terrainBase + (ulong)terrain.OffsetOf("BytesPerRow"), stride)
            .Place<long>(terrainBase + (ulong)terrain.OffsetOf("TotalTilesX"), TilesX)
            .Place<long>(terrainBase + (ulong)terrain.OffsetOf("TotalTilesY"), TilesY);

        int typeFiles = terrain.OffsetOf("GroundTypeFiles");
        if (types > 0)
        {
            // EVERY REAL AREA STARTS ITS LIST WITH A NULL - three of them, in two recordings -
            // and a corner value of zero therefore means "no ground type here". The fixture can
            // lay the list out either way because that blank is what the reader used to reject.
            var pointers = new byte[(types + blank) * 8];
            for (int i = 0; i < types; i++)
            {
                ulong record = FirstType + ((ulong)i * 0x1000);
                BitConverter.GetBytes(record).CopyTo(pointers, (i + blank) * 8);

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
    public void ACornerArrayOfAnotherLengthIsRefusedAndSaysSo()
    {
        // THE GATE THAT LICENSES EVERYTHING AFTER IT. Three bytes per tile corner is the whole
        // identification of this array - it is what tied it to a room file's per-corner ground
        // types in the first place - so a different length means something else is being read,
        // and reinterpreting it would produce a plausible map of nonsense.
        (FakeMemoryReader fake, OffsetSchema schema) = Area(cornerBytes: 128);

        TerrainGrid grid = Read(fake, schema);

        Assert.Null(grid.Ground);
        Assert.Contains("not that array", grid.GroundNote, StringComparison.Ordinal);
        Assert.Contains("128 bytes against 693", grid.GroundNote, StringComparison.Ordinal);
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
        // EVERY element or none: a list with a hole in it would shift every value above the
        // hole onto the wrong name, which is the one failure a reader of an index table must
        // not have. Which element failed is what makes it findable.
        (FakeMemoryReader fake, OffsetSchema schema) = Area(namedFiles: false);

        TerrainGrid grid = Read(fake, schema);

        Assert.Null(grid.Ground);
        Assert.Contains("element 0 of 2 points at no readable file", grid.GroundNote, StringComparison.Ordinal);
    }

    [Fact]
    public void ABlankFirstSlotIsAPositionInTheListRatherThanAHoleInIt()
    {
        // THE BUG THIS IS THE REGRESSION FOR, and it kept the feature off the map entirely.
        // Every real area's list begins with a null - three of them across two recordings, the
        // Titan Grotto's among them - and the reader threw the whole list away over it,
        // reporting "element 0 of 5 is not a pointer". A corner value of zero means "no ground
        // type here"; the slot is DATA. Keeping it is also what keeps the values above it
        // pointing at the right names, since a corner indexes the list by position.
        (FakeMemoryReader fake, OffsetSchema schema) = Area(leadingBlank: true);

        TerrainGrid grid = Read(fake, schema);

        Assert.NotNull(grid.Ground);
        Assert.True(grid.Ground!.Trusted);
        Assert.Equal(3, grid.Ground.Types.Count);
        Assert.Equal(string.Empty, grid.Ground.Types[0]);
        Assert.False(grid.Ground.Names(0));
        Assert.Equal(Paths[0], grid.Ground.Types[1]);
        Assert.Contains("2 ground types", grid.GroundNote, StringComparison.Ordinal);

        // Two regions, not three: the blank names nothing, so there is nothing to write on it.
        Assert.Equal(2, grid.GroundRegions.Count);
        Assert.DoesNotContain(grid.GroundRegions, r => r.Path.Length == 0);
    }

    [Fact]
    public void WhichSlotHoldsTheWalkableGroundIsNotAssumedInAdvance()
    {
        // THE SECOND BUG THE BLANK SLOT CAUSED, and the opposite of the first. The spread check
        // used to EXCLUDE the blank, on the theory that it covers the void outside the playable
        // area and is walkable nowhere - so counting it would satisfy the "mostly not walkable"
        // half for free. A live Maelstrom area said otherwise: there the blank IS the floor, 635
        // of the area's 679 walkable corners, and the two NAMED types are a wall (0 of 2273
        // walkable) and an abyss (44 of 3561). A correct reading was rejected as a mis-read one.
        //
        // Here the named type holds the walkable half and the BLANK holds the unwalkable one -
        // the Maelstrom the other way up. Either arrangement partitions the walkable ground, and
        // the check has to believe both: what it tests is the spread, not which slot is which.
        (FakeMemoryReader fake, OffsetSchema schema) = Area(
            types: 1, leadingBlank: true, leftType: 1, rightType: 0);

        TerrainGrid grid = Read(fake, schema);

        Assert.NotNull(grid.Ground);
        Assert.True(grid.Ground!.Trusted);
        Assert.Equal(grid.Ground.TotalCorners[1], grid.Ground.WalkableCorners[1]);
        Assert.Equal(0, grid.Ground.WalkableCorners[0]);

        // One region, not two: the blank is real ground and counts for every measurement, but it
        // has no name to write, so nothing is labelled on it.
        TerrainRoom only = Assert.Single(grid.GroundRegions);
        Assert.Equal(Paths[0], only.Path);
    }

    [Fact]
    public void GroundThatDoesNotPartitionTheWalkableAreaIsNotBelieved()
    {
        // The check that has to keep working after the above: walkable EVERYWHERE puts both
        // values at the area's average, which is what a field that is not the ground type looks
        // like - it would sample the same ground at random and land every value in the same place.
        (FakeMemoryReader fake, OffsetSchema schema) = Area(walkableEverywhere: true);

        TerrainGrid grid = Read(fake, schema);

        Assert.NotNull(grid.Ground);
        Assert.False(grid.Ground!.Trusted);
        Assert.Contains("do not separate", grid.GroundNote, StringComparison.Ordinal);
        Assert.Empty(grid.GroundRegions);
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
