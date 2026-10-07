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
/// back through the decode the heights use - with the file lying on the template any of the eight
/// ways a square can. The room's laying must find whichever way the area was built.
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
    private const string TallPath = "Metadata/Terrain/Test/tall.tdt";
    private const string TallTemplatePath = "Art/Models/Terrain/Test/Tall.tgt";

    /// <summary>The tile's ground, in its own frame.</summary>
    private static float Plane(float x, float y) => (0.2f * x) + (0.1f * y);

    /// <summary>
    /// Whichever of the eight ways the area's heights were written with the file lying, the ground says so - and the file is drawn lying that way.
    /// </summary>
    /// <remarks>
    /// THE PLANE TELLS ALL EIGHT APART because its two slopes differ, 0.2 across and 0.1 down: each
    /// way of lying puts the high corner somewhere else or the steeper slope along the other axis.
    /// That only holds with the heights counted the one way - turned half round and upside down, a
    /// plane is the same plane - which is why the sign is the game's and not a ninth question.
    /// </remarks>
    [Theory]
    [InlineData(3, "as filed (x, y)", 250f, 250f)]
    [InlineData(2, "(x, -y)", 250f, 0f)]
    [InlineData(1, "(-x, y)", 0f, 250f)]
    [InlineData(0, "(-x, -y)", 0f, 0f)]
    [InlineData(7, "(y, x)", 250f, 250f)]
    [InlineData(4, "(-y, -x)", 0f, 0f)]
    [InlineData(5, "(y, -x)", 250f, 0f)]
    [InlineData(6, "(-y, x)", 0f, 250f)]
    public void ATILELaidAsAuthoredLiesOnTheAreasGroundTheWayTheHeightsWereWritten(int lies, string said, float topX, float topY)
    {
        MonsterModel room = Laid(placement: 3, selector: 0, lies);

        Assert.True(room.Ready, room.Why);
        Assert.Contains("heights: drawn with the files ", room.Move, StringComparison.Ordinal);
        Assert.Contains(said + " before the game's own turn - 1/1 sloped pieces", room.Move, StringComparison.Ordinal);
        Assert.Contains("1 pieces of 1 tile files", room.Move, StringComparison.Ordinal);

        // Over the room's one tile, its corner at the picture's origin.
        Assert.Equal(0f, room.BodyLeast.X, 0.5f);
        Assert.Equal(0f, room.BodyLeast.Y, 0.5f);
        Assert.Equal(250f, room.BodyMost.X, 0.5f);
        Assert.Equal(250f, room.BodyMost.Y, 0.5f);

        // On the area's ground: the high corner where the area has it, as high as the plane says.
        Vector3 top = Highest(room);
        Assert.Equal((topX, topY), (top.X, top.Y));
        Assert.Equal(Plane(250f, 250f), top.Z, 8f);
    }

    [Fact]
    public void ATILETurnedAQuarterIsTurnedTheWayTheGameReadsItsHeights()
    {
        // Selector 1, turned 90 degrees - placement 6, X' = -Y and Y' = X about the middle.
        MonsterModel room = Laid(placement: 6, selector: 1, lies: 3);

        Assert.True(room.Ready, room.Why);
        Assert.Contains("heights: drawn with the files as filed (x, y)", room.Move, StringComparison.Ordinal);
        Vector3 top = Highest(room);
        Assert.Equal((0f, 250f), (top.X, top.Y));
    }

    /// <summary>
    /// The way the file lies is undone BEFORE the game's turn, not after: turned over and then a quarter round is not a quarter round and then turned over.
    /// </summary>
    /// <remarks>
    /// The two orders differ by a half turn wherever the turn is a quarter, so the wrong one would be
    /// found as the file lying (-x, y) rather than (x, -y) - which is what this pins.
    /// </remarks>
    [Fact]
    public void THEWayAFileLiesComesBeforeTheGamesTurn()
    {
        MonsterModel room = Laid(placement: 6, selector: 1, lies: 2);

        Assert.True(room.Ready, room.Why);
        Assert.Contains("heights: drawn with the files mirrored, turned 180° (x, -y) before the game's own turn - 1/1", room.Move, StringComparison.Ordinal);
        Vector3 top = Highest(room);
        Assert.Equal((250f, 250f), (top.X, top.Y));
    }

    /// <summary>
    /// A file whose ground sits below the area's is raised to it fitted and left where its tile's level puts it otherwise - and the line says by how much the two differ.
    /// </summary>
    /// <remarks>
    /// The area's heights are the plane over a tile level of nought; the file's ground is the same
    /// plane forty units further down. Fitted, the piece comes up forty to meet the area; at its level
    /// it stays down, and only the line's count says the area's ground is missed there.
    /// </remarks>
    [Fact]
    public void APIECEFittedMeetsTheAreasGroundAndAtItsLevelStaysWhereTheFileHasIt()
    {
        MonsterModel fitted = Laid(placement: 3, selector: 0, lies: 3, sunk: 40f);
        MonsterModel level = Laid(placement: 3, selector: 0, lies: 3, sunk: 40f, atLevel: true);

        Assert.True(fitted.Ready, fitted.Why);
        Assert.True(level.Ready, level.Why);
        Assert.Equal(Plane(250f, 250f), Highest(fitted).Z, 8f);
        Assert.Equal(Plane(250f, 250f) + 40f, Highest(level).Z, 8f);

        Assert.InRange(Above(fitted), 36, 44);
        Assert.Contains("within 8 units on 0/1 pieces at the tile's level, 1/1 fitted - drawn fitted", fitted.Move, StringComparison.Ordinal);
        Assert.Contains("- drawn at the tiles' levels", level.Move, StringComparison.Ordinal);
    }

    /// <summary>
    /// Where the file's ground has the area's own shape, counted from the tile's level, the two ways agree - on a tile raised well off nought.
    /// </summary>
    [Fact]
    public void ANDWhereTheFileCountsFromItsTilesLevelTheTwoAgree()
    {
        MonsterModel fitted = Laid(placement: 3, selector: 0, lies: 3, level: -100f);
        MonsterModel level = Laid(placement: 3, selector: 0, lies: 3, level: -100f, atLevel: true);

        Assert.Equal(Plane(250f, 250f) - 100f, Highest(fitted).Z, 8f);
        Assert.Equal(Plane(250f, 250f) - 100f, Highest(level).Z, 8f);
        Assert.InRange(Above(fitted), -4, 4);
        Assert.Contains("within 8 units on 1/1 pieces at the tile's level, 1/1 fitted", fitted.Move, StringComparison.Ordinal);
    }

    [Fact]
    public void WITHNoSlopeNothingIsSettledAndTheLineSaysSo()
    {
        MonsterModel room = Laid(placement: 3, selector: 0, lies: 3, flat: true);

        Assert.True(room.Ready, room.Why);
        Assert.Contains("no sloped piece", room.Move, StringComparison.Ordinal);

        // EVERY SHAPE SAYS WHAT IT CAME FROM: here the one piece's ground, by file and tile.
        Assert.Equal(room.Mesh.Shapes.Count, room.ShapeSources.Count);
        Assert.Equal(["ground of " + TilePath + " at tile 1, 1"], room.ShapeSources.Distinct());
    }

    [Fact]
    public void AROOMWithNoTilesReadFromTheAreaSaysWhy()
    {
        var grid = new TerrainGrid(new byte[((TilesX * Cells) + 1) / 2 * TilesY * Cells], ((TilesX * Cells) + 1) / 2, TilesY * Cells, TilesX, TilesY, heights: null);
        MonsterModel room = LaidRoomModels.Of(Files().GetValueOrDefault, RoomPath, grid, 1, 1, 0);

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

    /// <summary>
    /// A piece several tiles across is checked against the area's own index, and the line says which way of counting the file's rows the area agrees with.
    /// </summary>
    /// <remarks>
    /// One column, two rows, laid as authored, the area naming row 0 at the nearer area tile and row 1
    /// at the further. The files lay row 1 at the top (TileModels), and laid as filed the top goes to
    /// the further area tile - so the area's index agrees counting from the file's BOTTOM, and the line
    /// has to say exactly that, with nothing sloped to settle anything else.
    /// </remarks>
    [Fact]
    public void APIECESeveralTilesAcrossIsCheckedAgainstTheAreasOwnIndex()
    {
        var ids = new int[TilesX * TilesY];
        Array.Fill(ids, -1);
        var subY = new byte[ids.Length];
        var placements = new sbyte[ids.Length];
        Array.Fill(placements, (sbyte)-1);
        foreach ((int at, byte row) in new[] { ((1 * TilesX) + 1, (byte)0), ((2 * TilesX) + 1, (byte)1) })
        {
            ids[at] = 0;
            subY[at] = row;
            placements[at] = 3;
        }

        var tiles = new TerrainTiles([TallPath], ids, new byte[ids.Length], subY, placements, TilesX, TilesY);
        int stride = ((TilesX * Cells) + 1) / 2;
        var grid = new TerrainGrid(new byte[stride * TilesY * Cells], stride, TilesY * Cells, TilesX, TilesY, heights: null, tiles: tiles);

        MonsterModel room = LaidRoomModels.Of(Files().GetValueOrDefault, RoomPath, grid, 1, 1, 0);

        Assert.True(room.Ready, room.Why);
        Assert.Contains("1 pieces of 1 tile files", room.Move, StringComparison.Ordinal);
        Assert.Contains("sub-tiles: of 1 pieces several tiles across, 0 have every tile where the area's own index puts it counting the file's rows from the top, 1 counting them from the bottom", room.Move, StringComparison.Ordinal);
    }

    /// <summary>
    /// The room laid over an area whose one tile is laid the given way, its heights written with the file lying on the template as <paramref name="lies"/> says - TileOrientation's placement numbers.
    /// </summary>
    /// <param name="level">The tile's own level, which its sub-tile heights are written relative to.</param>
    /// <param name="sunk">How far below the area's ground the file's ground is written.</param>
    /// <param name="atLevel">Whether the piece is set at its tile's level rather than fitted.</param>
    private static MonsterModel Laid(
        int placement,
        byte selector,
        int lies,
        bool flat = false,
        float level = 0f,
        float sunk = 0f,
        bool atLevel = false,
        string? doodads = null,
        IReadOnlyList<WorldEntity>? entities = null,
        DoodadHeight doodadHeights = DoodadHeight.Ground)
    {
        var ids = new int[TilesX * TilesY];
        Array.Fill(ids, -1);
        var placements = new sbyte[ids.Length];
        Array.Fill(placements, (sbyte)-1);
        int at = (1 * TilesX) + 1;
        ids[at] = 0;
        placements[at] = (sbyte)placement;
        var tiles = new TerrainTiles([TilePath], ids, new byte[ids.Length], new byte[ids.Length], placements, TilesX, TilesY);

        // THE AREA'S SUB-TILE HEIGHTS, from the plane: template cell (tx, ty) is the file's point the
        // way of lying takes there - its inverse, a signed permutation's transpose, about the middle.
        (int xx, int xy, int yx, int yy) = TileOrientation.OfPlacement(lies).Turn;
        float cell = TileModels.Side / Cells;
        float half = TileModels.Side / 2f;
        var array = new byte[Cells * Cells];
        for (var ty = 0; ty < Cells; ty++)
        {
            for (var tx = 0; tx < Cells; tx++)
            {
                float cx = ((tx + 0.5f) * cell) - half;
                float cy = ((ty + 0.5f) * cell) - half;
                float fx = (xx * cx) + (yx * cy) + half;
                float fy = (xy * cx) + (yy * cy) + half;
                float height = flat ? 0f : Plane(fx, fy);
                array[(ty * Cells) + tx] = unchecked((byte)(sbyte)Math.Round(height / TerrainHeightField.HeightScale));
            }
        }

        var rotation = new byte[ids.Length];
        rotation[at] = selector;
        var which = new int[ids.Length];
        Array.Fill(which, -1);
        which[at] = 0;
        var levels = new float[ids.Length];
        levels[at] = level;
        TerrainHeightField heights = TerrainHeightField.WithSubTile(
            levels, TilesX, TilesY, rotation, which, [array], TileOrientationTests.GameSelectors, TileOrientationTests.GameHelper);

        int stride = ((TilesX * Cells) + 1) / 2;
        var grid = new TerrainGrid(new byte[stride * TilesY * Cells], stride, TilesY * Cells, TilesX, TilesY, heights, tiles: tiles);
        Dictionary<string, byte[]> files = Files(sunk);
        if (doodads is not null)
        {
            files[RoomPath] = Text(WithDoodads(doodads));
        }

        return LaidRoomModels.Of(files.GetValueOrDefault, RoomPath, grid, 1, 1, 0, atLevel: atLevel, heights: doodadHeights, entities: entities);
    }

    /// <summary>The test room with these doodad lines in place of its empty doodad group - after its grid, the second k line.</summary>
    private static string WithDoodads(string lines)
    {
        List<string> room = [.. RoomText.ReplaceLineEndings("\n").Split('\n')];
        int grid = room.FindLastIndex(line => line.StartsWith("k 1 1", StringComparison.Ordinal));
        room.InsertRange(grid + 1, lines.ReplaceLineEndings("\n").Split('\n'));
        return string.Join('\n', room);
    }

    /// <summary>
    /// The doodad heights line: a doodad found in memory by its stub and its place, its line's height beside its z and the area's ground - and the three readings counted.
    /// </summary>
    /// <remarks>
    /// The pot at cell 11, 11 carries -115 and its entity sits at -115 on ground that is not nought, so only
    /// "z is that height" holds. The second pot's entity is two thousand units away, somebody else's;
    /// the rock is a plain doodad, which the game makes no entity of.
    /// </remarks>
    [Fact]
    public void THEDOODADHeightsLineHoldsADoodadAgainstItsEntity()
    {
        const string doodads = """
            11 11 0 0 0 0 0 1 0 0 1 -115 1 "Metadata/Test/Pot.ao" "Metadata/Test/Pot" 0
            2 2 0 0 0 0 0 1 0 0 0 1 "Metadata/Test/Pot.ao" "Metadata/Test/Pot" 0
            5 5 0 0 0 0 0 1 0 0 0 1 "Metadata/Test/Rock.ao" "Metadata/MiscellaneousObjects/Doodad" 0
            """;
        float at = (1f + (11f / RoomModels.CellsPerTile)) * TileModels.Side;
        WorldEntity[] entities =
        [
            new(1, 1, "Metadata/Test/Pot", EntityKind.Unknown, at + 2f, at, -115f, TerrainHeight: -115f),
            new(2, 2, "Metadata/Test/Pot", EntityKind.Unknown, 2000f, 2000f, -7f),
            new(3, 3, "Metadata/Monsters/Rhoa", EntityKind.Monster, at, at, 0f),
        ];

        MonsterModel room = Laid(placement: 3, selector: 0, lies: 3, flat: true, level: 40f, doodads: doodads, entities: entities);

        Assert.Contains(
            "doodad heights: 1 of the room's doodads are in memory as entities, 1 with a height in their line - z is that height on 1, the ground plus it on 0, the ground on 0; Pot.ao: line -115, z -115, its terrain height -115, the area's ground ",
            room.Move,
            StringComparison.Ordinal);
        Assert.Contains(", 2.0 from its cell", room.Move, StringComparison.Ordinal);

        // WITHOUT THE AREA'S ENTITIES THERE IS NO LINE, and none of them found says so.
        Assert.DoesNotContain("doodad heights:", Laid(placement: 3, selector: 0, lies: 3, flat: true, doodads: doodads).Move, StringComparison.Ordinal);
        Assert.Contains(
            "doodad heights: none of the room's doodads is in memory as an entity - 1 entities read, 0 with a path the room names as a stub",
            Laid(placement: 3, selector: 0, lies: 3, flat: true, doodads: doodads, entities: [entities[2]]).Move,
            StringComparison.Ordinal);

        // AND WHY: the pot too far from any of its doodads, and one whose path spells the name another way.
        WorldEntity far = entities[1];
        WorldEntity spelt = new(4, 4, "Metadata/Other/Pot@2", EntityKind.Unknown, at, at, -115f);
        Assert.Contains(
            "doodad heights: none of the room's doodads is in memory as an entity - 2 entities read, 1 with a path the room names as a stub, the nearest ",
            Laid(placement: 3, selector: 0, lies: 3, flat: true, doodads: doodads, entities: [far, spelt]).Move,
            StringComparison.Ordinal);
        Assert.Contains(
            "; 1 with a stub's name in another path, such as Metadata/Other/Pot@2",
            Laid(placement: 3, selector: 0, lies: 3, flat: true, doodads: doodads, entities: [far, spelt]).Move,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// How far above its tile's level the line says a fitted piece sits - within a height step of the truth, the area's heights being whole steps of HeightScale.
    /// </summary>
    private static int Above(MonsterModel room)
    {
        const string said = "level: fitted to the area's ground, a piece sits ";
        int at = room.Move.IndexOf(said, StringComparison.Ordinal);
        Assert.True(at >= 0, room.Move);
        string rest = room.Move[(at + said.Length)..];
        return int.Parse(rest[..rest.IndexOf(' ', StringComparison.Ordinal)], System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The vertex with the greatest height.</summary>
    private static Vector3 Highest(MonsterModel room)
    {
        Vector3 top = room.Mesh.Positions.MaxBy(one => one.Z);
        return new Vector3(MathF.Round(top.X), MathF.Round(top.Y), top.Z);
    }

    /// <summary>The install: the room, the one by one tile's definition, template and mesh, and a one by two tile's.</summary>
    private static Dictionary<string, byte[]> Files(float sunk = 0f)
    {
        return new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [RoomPath] = Text(RoomText),
            [TilePath] = Tdt(TemplatePath),
            [TemplatePath] = Text("version 3\nSize 1 1\nTileMeshRoot \"Art/Models/Terrain/Test/Slope\"\n"),
            [MeshPath] = Tgm(10, sunk),
            [TallPath] = Tdt(TallTemplatePath),
            [TallTemplatePath] = Text("version 3\nSize 1 2\nTileMeshRoot \"Art/Models/Terrain/Test/Tall\"\n"),
            ["Art/Models/Terrain/Test/Tall_c1r1.tgm"] = Tgm(2),
            ["Art/Models/Terrain/Test/Tall_c1r2.tgm"] = Tgm(2),
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

    /// <summary>A sub-tile of no props and a ground of n by n quads over the tile, on the plane - <paramref name="sunk"/> further down.</summary>
    private static byte[] Tgm(int n, float sunk = 0f)
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
                    write.Write(Plane(x, y) + sunk);
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
