using System.Numerics;
using System.Text;
using PoEformance.Features;
using PoEformance.Game.Files;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// A room laid from the tiles the area put down, each set on the area's own ground - and the ground deciding how the files' heights count.
/// </summary>
/// <remarks>
/// THE AREA IS BUILT SO THE ANSWER IS KNOWN. One tile file, its ground a plane that rises 0.2 per
/// unit across and 0.1 down its own frame, laid at area tile (1, 1); and the area's sub-tile heights
/// written from that same plane - through the game's own selector tables, so a turned tile is read
/// back through the decode the heights use - with the file's Y either counting the way the template
/// index does or the other way. The room's laying must find whichever way the area was built.
/// </remarks>
public class LaidRoomModelsTests
{
    private const int TilesX = 4;
    private const int TilesY = 3;
    private const int Cells = TerrainGrid.CellsPerTile;
    private const string RoomPath = "Metadata/Terrain/Test/room.arm";
    private const string TilePath = "Metadata/Terrain/Test/slope.tdt";
    private const string TemplatePath = "Art/Models/Terrain/Test/Slope.tgt";
    private const string MeshPath = "Art/Models/Terrain/Test/Slope.tgm";

    /// <summary>The tile's ground, in its own frame.</summary>
    private static float Plane(float x, float y) => (0.2f * x) + (0.1f * y);

    [Fact]
    public void ATILELaidAsAuthoredIsSetOnTheAreasGroundAndTheGroundSaysTheFilesYCountsAsTheIndexDoes()
    {
        MonsterModel room = Laid(placement: 3, selector: 0, overturned: false);

        Assert.True(room.Ready, room.Why);
        Assert.Contains("heights: drawn Y as filed, heights as the area's", room.Move, StringComparison.Ordinal);
        Assert.Contains("1 pieces of 1 tile files", room.Move, StringComparison.Ordinal);

        // Over the room's one tile, its corner at the picture's origin.
        Assert.Equal(0f, room.BodyLeast.X, 0.5f);
        Assert.Equal(0f, room.BodyLeast.Y, 0.5f);
        Assert.Equal(250f, room.BodyMost.X, 0.5f);
        Assert.Equal(250f, room.BodyMost.Y, 0.5f);

        // On the area's ground: the high corner as high as the plane says, within a height step.
        Vector3 top = Highest(room);
        Assert.Equal((250f, 250f), (top.X, top.Y));
        Assert.Equal(Plane(250f, 250f), top.Z, 8f);
    }

    [Fact]
    public void ANDWhereTheAreaCountsTheFilesYTheOtherWayTheGroundSaysSo()
    {
        MonsterModel room = Laid(placement: 3, selector: 0, overturned: true);

        Assert.True(room.Ready, room.Why);
        Assert.Contains("heights: drawn Y turned over, heights as the area's", room.Move, StringComparison.Ordinal);

        // Turned over about its middle: the file's high corner at the area's (250, 0).
        Vector3 top = Highest(room);
        Assert.Equal((250f, 0f), (top.X, top.Y));
    }

    [Fact]
    public void ATILETurnedAQuarterIsTurnedTheWayTheGameReadsItsHeights()
    {
        // Selector 1, turned 90 degrees - placement 6, X' = -Y and Y' = X about the middle.
        MonsterModel room = Laid(placement: 6, selector: 1, overturned: false);

        Assert.True(room.Ready, room.Why);
        Assert.Contains("heights: drawn Y as filed, heights as the area's", room.Move, StringComparison.Ordinal);
        Vector3 top = Highest(room);
        Assert.Equal((0f, 250f), (top.X, top.Y));
    }

    [Fact]
    public void WITHNoSlopeNothingIsSettledAndTheLineSaysSo()
    {
        MonsterModel room = Laid(placement: 3, selector: 0, overturned: false, flat: true);

        Assert.True(room.Ready, room.Why);
        Assert.Contains("no sloped piece", room.Move, StringComparison.Ordinal);
    }

    [Fact]
    public void AROOMWithNoTilesReadFromTheAreaSaysWhy()
    {
        var grid = new TerrainGrid(new byte[((TilesX * Cells) + 1) / 2 * TilesY * Cells], ((TilesX * Cells) + 1) / 2, TilesY * Cells, TilesX, TilesY, heights: null);
        MonsterModel room = LaidRoomModels.Of(Files(overturned: false).GetValueOrDefault, RoomPath, grid, 1, 1, 0);

        Assert.False(room.Ready);
        Assert.Contains("tiles are not read", room.Why, StringComparison.Ordinal);
    }

    /// <summary>
    /// The pieces over a block: a file several tiles across counted once, by its sub-tiles, whatever frame they are counted in - and one that is not whole said so.
    /// </summary>
    /// <remarks>
    /// Two pieces of one two by one file side by side, laid as authored; one laid a quarter round,
    /// standing one by two; a one by one; and a two by one file with only one of its tiles laid.
    /// </remarks>
    [Fact]
    public void APIECESeveralTilesAcrossIsCountedOnceByItsSubTiles()
    {
        const int across = 6;
        const int down = 3;
        var ids = new int[across * down];
        Array.Fill(ids, -1);
        var subX = new byte[ids.Length];
        var subY = new byte[ids.Length];
        var placements = new sbyte[ids.Length];
        Array.Fill(placements, (sbyte)3);
        void Lay(int x, int y, int id, byte sx, byte sy, sbyte placement = 3)
        {
            ids[(y * across) + x] = id;
            subX[(y * across) + x] = sx;
            subY[(y * across) + x] = sy;
            placements[(y * across) + x] = placement;
        }

        // Row 0: two two-by-ones side by side, then a one-by-one, then half of a two-by-one.
        Lay(0, 0, 0, 0, 0);
        Lay(1, 0, 0, 1, 0);
        Lay(2, 0, 0, 0, 0);
        Lay(3, 0, 0, 1, 0);
        Lay(4, 0, 1, 0, 0);
        Lay(5, 0, 2, 0, 0);

        // Rows 1-2: a two-by-one laid a quarter round, standing one across by two down.
        Lay(0, 1, 0, 0, 0, 6);
        Lay(0, 2, 0, 1, 0, 6);
        var tiles = new TerrainTiles(["a.tdt", "b.tdt", "c.tdt"], ids, subX, subY, placements, across, down);

        List<TilePiece> pieces = tiles.Pieces(0, 0, across, down, id => id == 1 ? (1, 1) : (2, 1));

        Assert.Equal(5, pieces.Count);
        Assert.Contains(new TilePiece(0, 3, 0, 0, 1, 0, Whole: true), pieces);
        Assert.Contains(new TilePiece(0, 3, 2, 0, 3, 0, Whole: true), pieces);
        Assert.Contains(new TilePiece(1, 3, 4, 0, 4, 0, Whole: true), pieces);
        Assert.Contains(new TilePiece(2, 3, 5, 0, 5, 0, Whole: false), pieces);
        Assert.Contains(new TilePiece(0, 6, 0, 1, 0, 2, Whole: true), pieces);

        // A block that holds only part of a piece still finds all of it.
        Assert.Equal([new TilePiece(0, 3, 2, 0, 3, 0, Whole: true)], tiles.Pieces(3, 0, 1, 1, _ => (2, 1)));
    }

    /// <summary>The room laid over an area whose one tile is laid the given way, its heights written with the file's Y as the index counts it or turned over.</summary>
    private static MonsterModel Laid(int placement, byte selector, bool overturned, bool flat = false)
    {
        var ids = new int[TilesX * TilesY];
        Array.Fill(ids, -1);
        var placements = new sbyte[ids.Length];
        Array.Fill(placements, (sbyte)-1);
        int at = (1 * TilesX) + 1;
        ids[at] = 0;
        placements[at] = (sbyte)placement;
        var tiles = new TerrainTiles([TilePath], ids, new byte[ids.Length], new byte[ids.Length], placements, TilesX, TilesY);

        // THE AREA'S SUB-TILE HEIGHTS, from the plane: template cell (tx, ty) is the file's point at
        // its middle, its Y counted down from the far edge where the area counts it the other way.
        float cell = TileModels.Side / Cells;
        var array = new byte[Cells * Cells];
        for (var ty = 0; ty < Cells; ty++)
        {
            for (var tx = 0; tx < Cells; tx++)
            {
                float fx = (tx + 0.5f) * cell;
                float fy = overturned ? TileModels.Side - ((ty + 0.5f) * cell) : (ty + 0.5f) * cell;
                float height = flat ? 0f : Plane(fx, fy);
                array[(ty * Cells) + tx] = unchecked((byte)(sbyte)Math.Round(height / TerrainHeightField.HeightScale));
            }
        }

        var rotation = new byte[ids.Length];
        rotation[at] = selector;
        var which = new int[ids.Length];
        Array.Fill(which, -1);
        which[at] = 0;
        TerrainHeightField heights = TerrainHeightField.WithSubTile(
            new float[ids.Length], TilesX, TilesY, rotation, which, [array], TileOrientationTests.GameSelectors, TileOrientationTests.GameHelper);

        int stride = ((TilesX * Cells) + 1) / 2;
        var grid = new TerrainGrid(new byte[stride * TilesY * Cells], stride, TilesY * Cells, TilesX, TilesY, heights, tiles: tiles);
        return LaidRoomModels.Of(Files(overturned).GetValueOrDefault, RoomPath, grid, 1, 1, 0);
    }

    /// <summary>The vertex with the greatest height.</summary>
    private static Vector3 Highest(MonsterModel room)
    {
        Vector3 top = room.Mesh.Positions.MaxBy(one => one.Z);
        return new Vector3(MathF.Round(top.X), MathF.Round(top.Y), top.Z);
    }

    /// <summary>The install: the room, and the tile's definition, template and mesh.</summary>
    private static Dictionary<string, byte[]> Files(bool overturned)
    {
        _ = overturned;
        return new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [RoomPath] = Text(RoomText),
            [TilePath] = Tdt(TemplatePath),
            [TemplatePath] = Text("version 3\nSize 1 1\nTileMeshRoot \"Art/Models/Terrain/Test/Slope\"\n"),
            [MeshPath] = Tgm(10),
        };
    }

    private const string RoomText = """
        version 36
        1
        "Metadata/Terrain/Test/floor.gt"
        5 3
        0
        "roomtag"
        0
        k 1 1 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0
        -1
        -1
        -1
        -1
        -1
        -1
        ""
        k 1 1 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0
        -1
        -1
        -1
        0
        -1
        0
        """;

    /// <summary>Text as the game writes it: UTF-16 with its mark.</summary>
    private static byte[] Text(string text) => [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)];

    /// <summary>A version 7 definition, one tile by one, naming its template.</summary>
    private static byte[] Tdt(string template)
    {
        string[] strings = ["", template, "slope_tag", "edge"];
        var starts = new uint[strings.Length];
        var table = new List<byte>();
        for (var one = 0; one < strings.Length; one++)
        {
            starts[one] = (uint)(table.Count / 2);
            table.AddRange(Encoding.Unicode.GetBytes(strings[one]));
            table.AddRange([0, 0]);
        }

        using var stream = new MemoryStream();
        using var write = new BinaryWriter(stream);
        write.Write(7);
        write.Write(table.Count / 2);
        write.Write(table.ToArray());
        write.Write(starts[0]);
        write.Write(starts[1]);
        write.Write(starts[2]);
        for (var side = 0; side < 4; side++)
        {
            write.Write(starts[3]);
        }

        write.Write((sbyte)1);
        write.Write((sbyte)1);
        write.Write(new byte[64]);
        return stream.ToArray();
    }

    /// <summary>A sub-tile of no props and a ground of n by n quads over the tile, on the plane.</summary>
    private static byte[] Tgm(int n)
    {
        using var stream = new MemoryStream();
        using var write = new BinaryWriter(stream);
        write.Write((byte)9);
        for (var one = 0; one < 6; one++)
        {
            write.Write(0f);
        }

        write.Write((ushort)0);
        write.Write((ushort)0);
        write.Write((byte)0);
        write.Write((byte)0);

        // The props: an empty block.
        write.Write("DOLm"u8);
        write.Write((ushort)1);
        write.Write((byte)0);
        write.Write((ushort)0);
        write.Write(0x8u);

        // The ground: n by n quads, each its own shape.
        int quads = n * n;
        write.Write("DOLm"u8);
        write.Write((ushort)1);
        write.Write((byte)1);
        write.Write((ushort)quads);
        write.Write(0x8u);
        write.Write((uint)(quads * 2));
        write.Write((uint)(quads * 4));
        for (var one = 0; one < quads; one++)
        {
            write.Write((uint)(one * 6));
            write.Write(6u);
        }

        for (var one = 0; one < quads; one++)
        {
            foreach (int corner in new[] { 0, 1, 2, 1, 3, 2 })
            {
                write.Write((ushort)((one * 4) + corner));
            }
        }

        float step = TileModels.Side / n;
        for (var row = 0; row < n; row++)
        {
            for (var column = 0; column < n; column++)
            {
                for (var corner = 0; corner < 4; corner++)
                {
                    float x = (column + (corner % 2)) * step;
                    float y = (row + (corner / 2)) * step;
                    write.Write(x);
                    write.Write(y);
                    write.Write(Plane(x, y));
                    write.Write(new byte[] { 0, 0, 127, 0, 127, 0, 0, 0 });
                    write.Write(BitConverter.HalfToUInt16Bits((Half)0.5f));
                    write.Write(BitConverter.HalfToUInt16Bits((Half)0.5f));
                }
            }
        }

        for (var one = 0; one < quads; one++)
        {
            write.Write((ushort)one);
            write.Write(new byte[24]);
        }

        return stream.ToArray();
    }
}
