using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using PoEformance.Features;
using PoEformance.Game.Files;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// The terrain tile's three files and the walk from a placed tile's definition to its triangles.
/// </summary>
/// <remarks>
/// WHAT THESE CAN AND CANNOT SETTLE. No real tile file is committed here, so what is checked is
/// that each reader implements the layout its references describe - zao's <c>tdt.cpp</c> and
/// annalithic's <c>Tdt.cs</c>, poe_data_tools' <c>tgt</c> and <c>tgm</c> parsers - and, more usefully,
/// that each one REFUSES the wrong answer a misread would produce: an offset into the middle of a
/// word, a template that is not a .tgt, a size no tile has, a walk that does not end at the end.
/// Whether the layouts match the game's files is settled by opening one in the tile book, where
/// every refusal is printed under the picture.
/// </remarks>
public class TileFilesTests
{
    /// <summary>A version 7 definition: a string table, then the references and the size.</summary>
    private static byte[] Tdt(string inherits = "", string template = "Art/Models/Terrain/Test/Arena.tgt",
        int width = 2, int height = 1, int version = 7, bool grounds = false, string edge = "edge")
    {
        // THE TABLE STARTS WITH AN EMPTY STRING, so offset zero means "nothing" the way the game's
        // own files use it, and every other offset lands where a string begins.
        string[] strings = ["", inherits, template, "arena_tag", edge, "Metadata/Terrain/Test/sand.gt", "Metadata/Terrain/Test/rock.gt"];
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
        write.Write(version);
        write.Write(table.Count / 2);
        write.Write(table.ToArray());
        if (version >= 7)
        {
            write.Write(inherits.Length > 0 ? starts[1] : starts[0]);
        }

        if (inherits.Length > 0)
        {
            write.Write((byte)0);
            return stream.ToArray();
        }

        if (version > 4)
        {
            write.Write(starts[2]);
        }

        write.Write(starts[3]);
        for (var side = 0; side < 4; side++)
        {
            write.Write(starts[4]);
        }

        write.Write((sbyte)width);
        write.Write((sbyte)height);
        if (grounds)
        {
            // Down-left, down-right, up-right, up-left - and the last one a step into a word, which
            // must read as nothing rather than as the tail of a path.
            write.Write(starts[5]);
            write.Write(starts[5]);
            write.Write(starts[6]);
            write.Write(starts[6] + 3);
            write.Write(new byte[48]);
            return stream.ToArray();
        }

        write.Write(new byte[64]);              // Ground types, offsets and the rest - not read.
        return stream.ToArray();
    }

    private const string Template = """
        version 3
        SourceScene "Art/Scenes/Arena.mb"
        Size 2 1
        TileMeshRoot "Art/Models/Terrain/Test/Arena"
        GroundMask "Art/Models/Terrain/Test/Arena_mask.dds"
        NormalMaterials 2
        	"Art/Textures/Wall.mat"
        	"Art/Textures/Floor.mat"
        SubTileMaterialIndices
        	2 0 1 1 1
        	1 1 2
        """;

    /// <summary>
    /// A DOLm block of quads: each quad one shape of two triangles, every vertex with a coordinate.
    /// An empty one has no levels at all, which is what a sub-tile with nothing in it writes.
    /// </summary>
    private static void Dolm(BinaryWriter write, int quads, Vector3 from = default)
    {
        const uint Format = 0x8;
        write.Write("DOLm"u8);
        write.Write((ushort)1);
        if (quads == 0)
        {
            write.Write((byte)0);
            write.Write((ushort)0);
            write.Write(Format);
            return;
        }

        write.Write((byte)1);
        write.Write((ushort)quads);
        write.Write(Format);
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

        for (var one = 0; one < quads; one++)
        {
            for (var corner = 0; corner < 4; corner++)
            {
                write.Write(from.X + (one * 10f) + (corner % 2 * 10f));
                write.Write(from.Y + (corner / 2 * 10f));
                write.Write(from.Z);
                write.Write(new byte[] { 0, 0, 127, 0, 127, 0, 0, 0 });
                write.Write(BitConverter.HalfToUInt16Bits((Half)0.5f));
                write.Write(BitConverter.HalfToUInt16Bits((Half)0.5f));
            }
        }
    }

    /// <summary>A sub-tile: props of some quads, a ground of some quads, and the records between.</summary>
    private static byte[] Tgm(int props, int ground, int version = 9, int tails = 0, int extra = 0)
    {
        using var stream = new MemoryStream();
        using var write = new BinaryWriter(stream);
        write.Write((byte)version);
        for (var one = 0; one < 6; one++)
        {
            write.Write(0f);
        }

        write.Write((ushort)props);
        write.Write((ushort)0);
        write.Write((byte)0);
        write.Write((byte)tails);

        Dolm(write, props);
        for (var one = 0; one < props; one++)
        {
            if (version == 9)
            {
                write.Write((ushort)one);
            }
            else
            {
                write.Write((uint)one);
            }

            if (version >= 12)
            {
                write.Write((ushort)0);
            }

            write.Write(new byte[24]);
        }

        Dolm(write, ground, new Vector3(0f, 0f, 5f));
        for (var one = 0; one < ground; one++)
        {
            write.Write((ushort)one);
            write.Write(new byte[24]);
        }

        write.Write(new byte[(tails * 87) + extra]);
        return stream.ToArray();
    }

    [Fact]
    public void ADEFINITIONNamesItsTemplateAndItsSize()
    {
        TileDefinition read = TileDefinition.Read(Tdt());

        Assert.True(read.Ready, read.Why);
        Assert.Equal(7, read.Version);
        Assert.Equal(["Art/Models/Terrain/Test/Arena.tgt"], read.Templates);
        Assert.Equal("arena_tag", read.Tag);
        Assert.Equal((2, 1), (read.Width, read.Height));
    }

    [Fact]
    public void ANDOneThatInheritsCarriesOnlyWhatItInherits()
    {
        TileDefinition read = TileDefinition.Read(Tdt(inherits: "Metadata/Terrain/Test/Base.tdt"));

        Assert.True(read.Ready, read.Why);
        Assert.Equal("Metadata/Terrain/Test/Base.tdt", read.Inherits);
        Assert.Empty(read.Templates);
    }

    [Fact]
    public void ANDATemplateThatIsNotATgtIsRefusedRatherThanFollowed()
    {
        // The read that lands one field out reads the TAG where the template should be - a word,
        // not a path - and that is what the check exists to catch.
        TileDefinition read = TileDefinition.Read(Tdt(template: "arena_tag"));

        Assert.False(read.Ready);
        Assert.Contains("not a .tgt", read.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void ANDASizeNoTileHasIsRefused()
    {
        Assert.False(TileDefinition.Read(Tdt(width: 0)).Ready);
        Assert.False(TileDefinition.Read(Tdt(width: 120)).Ready);
        Assert.False(TileDefinition.Read([1, 2, 3]).Ready);
    }

    [Fact]
    public void ATEMPLATENamesEachSubTilesMeshAndItsMaterialRuns()
    {
        TileTemplate read = TileTemplate.Parse(Template);

        Assert.True(read.Ready, read.Why);
        Assert.Equal((2, 1), (read.Width, read.Height));
        Assert.Equal(["Art/Textures/Wall.mat", "Art/Textures/Floor.mat"], read.Materials);
        Assert.Equal("Art/Models/Terrain/Test/Arena_c1r1.tgm", read.MeshOf(1, 1));
        Assert.Equal("Art/Models/Terrain/Test/Arena_c2r1.tgm", read.MeshOf(2, 1));

        Assert.Equal([new TileRun(0, 1), new TileRun(1, 1)], read.RunsOf(1, 1));
        Assert.Equal([new TileRun(1, 2)], read.RunsOf(2, 1));
    }

    [Fact]
    public void ANDTheFirstLineIsTheTopRowAndAOneByOneTileHasNoSuffix()
    {
        TileTemplate tall = TileTemplate.Parse("""
            version 3
            Size 1 2
            TileMeshRoot "Art/Tall"
            NormalMaterials 2
            	"a.mat"
            	"b.mat"
            SubTileMaterialIndices
            	1 0 1
            	1 1 1
            """);

        // Rows count up from the bottom: the first line belongs to the top row, r2.
        Assert.Equal([new TileRun(0, 1)], tall.RunsOf(1, 2));
        Assert.Equal([new TileRun(1, 1)], tall.RunsOf(1, 1));

        TileTemplate single = TileTemplate.Parse("version 3\nSize 1 1\nTileMeshRoot \"Art/One\"\nNormalMaterials 0\n");
        Assert.Equal("Art/One.tgm", single.MeshOf(1, 1));
    }

    [Fact]
    public void ANDATripleIsReadByItsLastTwoNumbers()
    {
        TileTemplate read = TileTemplate.Parse("""
            version 3
            Size 1 1
            TileMeshRoot "Art/One"
            NormalMaterials 2
            	"a.mat"
            	"b.mat"
            SubTileMaterialIndices
            	2 7 1 3 7 0 2
            """);

        Assert.Equal([new TileRun(1, 3), new TileRun(0, 2)], read.RunsOf(1, 1));
    }

    [Fact]
    public void ANDAnOlderTemplateIsRefusedByName()
    {
        TileTemplate read = TileTemplate.Parse("version 1\nTileMesh \"Art/Old.tgm\"\nNormalMaterials 0\n");

        Assert.False(read.Ready);
        Assert.Contains("version 1", read.Why, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(12)]
    public void ASUBTILEReadsItsPropsAndItsGroundAndEndsWhereTheFileDoes(int version)
    {
        TileMesh read = TileMesh.Read(Tgm(props: 2, ground: 1, version: version, tails: 1));

        Assert.True(read.Ready, read.Why);
        Assert.True(read.Exact);
        Assert.Equal(4, read.Props.Triangles);
        Assert.Equal(2, read.Props.Shapes.Count);
        Assert.Equal(2, read.Ground.Triangles);
    }

    [Fact]
    public void ANDEitherHalfMayBeEmpty()
    {
        TileMesh onlyGround = TileMesh.Read(Tgm(props: 0, ground: 1));
        Assert.True(onlyGround.Ready, onlyGround.Why);
        Assert.True(onlyGround.Exact);
        Assert.False(onlyGround.Props.Ready);
        Assert.True(onlyGround.Ground.Ready);

        TileMesh onlyProps = TileMesh.Read(Tgm(props: 1, ground: 0));
        Assert.True(onlyProps.Exact);
        Assert.True(onlyProps.Props.Ready);
        Assert.False(onlyProps.Ground.Ready);
    }

    [Fact]
    public void ANDAWalkThatDoesNotEndAtTheEndSaysSo()
    {
        // Bytes past what the layout accounts for are exactly what a wrong step leaves - and the
        // props still read, because their block checked itself before anything after it.
        TileMesh read = TileMesh.Read(Tgm(props: 1, ground: 1, extra: 3));

        Assert.True(read.Ready);
        Assert.False(read.Exact);
        Assert.True(read.Props.Ready);
    }

    [Fact]
    public void ANDAnOlderMeshIsRefusedByName()
    {
        TileMesh read = TileMesh.Read(Tgm(props: 1, ground: 1, version: 8));

        Assert.False(read.Ready);
        Assert.Contains("version 8", read.Why, StringComparison.Ordinal);
    }

    private static Dictionary<string, byte[]> Install()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Metadata/Terrain/Test/Arena.tdt"] = Tdt(),
            ["Metadata/Terrain/Test/Child.tdt"] = Tdt(inherits: "Metadata/Terrain/Test/Arena.tdt"),
            ["Art/Models/Terrain/Test/Arena.tgt"] = Encoding.Unicode.GetBytes(Template),
            ["Art/Models/Terrain/Test/Arena_c1r1.tgm"] = Tgm(props: 2, ground: 1),
            ["Art/Models/Terrain/Test/Arena_c2r1.tgm"] = Tgm(props: 2, ground: 1),
            ["Art/Textures/Wall.mat"] = Mat("Art/Textures/wall.dds"),
            ["Art/Textures/Floor.mat"] = Mat("Art/Textures/floor.dds"),
            ["Art/Textures/wall.dds"] = Dds(),
            ["Art/Textures/floor.dds"] = Dds(),
        };
        return files;
    }

    [Fact]
    public void ATILEIsItsSubTilesSideBySideWithEachShapeInItsRunsMaterial()
    {
        Dictionary<string, byte[]> files = Install();
        MonsterModel model = TileModels.Of(path => files.GetValueOrDefault(path), "Metadata/Terrain/Test/Arena.tdt");

        Assert.True(model.Ready, model.Why);
        Assert.Equal(2, model.Parts);

        // Two sub-tiles of two prop quads and one ground quad: twelve triangles.
        Assert.Equal(12, model.Mesh.Triangles);

        // THE SECOND SUB-TILE STANDS 250 ALONG x, which is where its quads begin.
        Assert.Equal(0f, model.Mesh.Least.X);
        Assert.Equal(250f + 20f, model.Mesh.Most.X);

        // c1r1 is "wall, floor" and c2r1 "floor, floor"; the two grounds come last and are unpainted.
        Assert.Equal(6, model.Skins.Count);
        Assert.NotNull(model.Skins[0]);
        Assert.NotNull(model.Skins[1]);
        Assert.NotSame(model.Skins[0], model.Skins[1]);
        Assert.Same(model.Skins[1], model.Skins[2]);
        Assert.Same(model.Skins[1], model.Skins[3]);
        Assert.Null(model.Skins[4]);
        Assert.Null(model.Skins[5]);
        Assert.False(model.Moves);

    }

    /// <summary>A tile laid turned is the same tile, every point turned the same way about one point.</summary>
    [Fact]
    public void ATileLaidAsTheAreaLaidItTurnsAsOnePiece()
    {
        Dictionary<string, byte[]> files = Install();
        MonsterModel own = TileModels.Of(path => files.GetValueOrDefault(path), "Metadata/Terrain/Test/Arena.tdt");

        // Selector 1 of the game's own tables: X' = -Y, Y' = X.
        PoEformance.Game.World.TileOrientation quarter =
            PoEformance.Game.World.TileOrientation.Table(TileOrientationTests.GameSelectors, TileOrientationTests.GameHelper)[1];
        MonsterModel turned = TileModels.Of(path => files.GetValueOrDefault(path), "Metadata/Terrain/Test/Arena.tdt", laid: quarter);

        Assert.True(turned.Ready, turned.Why);
        Assert.Equal("turned 90°", turned.Laid);
        Assert.Equal(own.Mesh.Positions.Length, turned.Mesh.Positions.Length);
        for (var one = 0; one < own.Mesh.Positions.Length; one++)
        {
            Vector3 was = own.Mesh.Positions[one];
            Assert.Equal(new Vector3(-was.Y, was.X, was.Z), turned.Mesh.Positions[one]);
        }

        // As the file holds it, nothing is said and nothing is moved.
        MonsterModel authored = TileModels.Of(path => files.GetValueOrDefault(path), "Metadata/Terrain/Test/Arena.tdt",
            laid: PoEformance.Game.World.TileOrientation.OfPlacement(3));
        Assert.Equal("as authored", authored.Laid);
        Assert.Equal(own.Mesh.Positions, authored.Mesh.Positions);
        Assert.Equal(string.Empty, own.Laid);
    }

    /// <summary>Without its ground a tile is its props alone - every shape painted, and still a tile.</summary>
    [Fact]
    public void ANDATileDrawnWithoutItsGroundIsItsPropsAlone()
    {
        Dictionary<string, byte[]> files = Install();
        MonsterModel model = TileModels.Of(path => files.GetValueOrDefault(path), "Metadata/Terrain/Test/Arena.tdt", ground: false);

        Assert.True(model.Ready, model.Why);
        Assert.Equal(ModelKind.Tile, model.Kind);
        Assert.Equal(2, model.Parts);
        Assert.Equal(8, model.Mesh.Triangles);
        Assert.Equal(4, model.Skins.Count);
        Assert.All(model.Skins, one => Assert.NotNull(one));
        Assert.Equal(model.Mesh.Shapes.Count, model.NamedInAo);
    }

    /// <summary>
    /// The shapes painted black-no-fog go when asked, and the rest keep their own textures and order.
    /// </summary>
    /// <remarks>
    /// c1r1 wears "wall, floor" and c2r1 "floor, floor"; with the wall's texture renamed blacknofog
    /// the first shape is the one wall, and the dump's per-shape lines say which file each wears.
    /// </remarks>
    [Fact]
    public void ANDATilesBlackWallsGoWhenAskedAndTheRestStay()
    {
        Dictionary<string, byte[]> files = Install();
        files["Art/Textures/Wall.mat"] = Mat("Art/Textures/blacknofog.dds");
        files["Art/Textures/blacknofog.dds"] = Dds();

        MonsterModel all = TileModels.Of(path => files.GetValueOrDefault(path), "Metadata/Terrain/Test/Arena.tdt", ground: false);
        Assert.True(all.Ready, all.Why);
        Assert.Equal(4, all.Mesh.Shapes.Count);
        Assert.Equal("Art/Textures/blacknofog.dds", all.ShapeTextures[0]);
        Assert.Equal(0, all.Walls);

        MonsterModel bare = TileModels.Of(path => files.GetValueOrDefault(path), "Metadata/Terrain/Test/Arena.tdt", ground: false, walls: false);
        Assert.True(bare.Ready, bare.Why);
        Assert.Equal(1, bare.Walls);
        Assert.Equal(3, bare.Mesh.Shapes.Count);
        Assert.Equal(6, bare.Mesh.Triangles);
        Assert.Equal(3, bare.Skins.Count);
        Assert.Equal(3, bare.Modes.Count);
        Assert.All(bare.ShapeTextures, one => Assert.Equal("Art/Textures/floor.dds", one));
        Assert.Equal(0, bare.Mesh.Shapes[0].From);
        Assert.Equal(bare.Mesh.Indices.Length, bare.Mesh.Shapes[^1].From + bare.Mesh.Shapes[^1].Count);

        string dump = ModelDump.OfTile(path => files.GetValueOrDefault(path), "Metadata/Terrain/Test/Arena.tdt", all);
        Assert.Contains("=== .mat Art/Textures/Wall.mat", dump, StringComparison.Ordinal);
        Assert.Contains("=== texture Art/Textures/blacknofog.dds", dump, StringComparison.Ordinal);
        Assert.Contains("tex Art/Textures/blacknofog.dds", dump, StringComparison.Ordinal);
    }

    /// <summary>
    /// A definition's four side edge types are read, down to left, and a string that is not an .et reads as none.
    /// </summary>
    [Fact]
    public void ADEFINITIONSEdgeTypesAreReadAndAnythingButAnEtIsNone()
    {
        TileDefinition walled = TileDefinition.Read(Tdt(edge: "Metadata/Terrain/Test/wall.et", grounds: true));
        Assert.True(walled.Ready, walled.Why);
        Assert.Equal(["Metadata/Terrain/Test/wall.et", "Metadata/Terrain/Test/wall.et", "Metadata/Terrain/Test/wall.et", "Metadata/Terrain/Test/wall.et"], walled.Edges);
        Assert.Equal(2, walled.Width);

        // THE SAME FILE WITH A WORD WHERE THE EDGE GOES, which is not a type.
        Assert.All(TileDefinition.Read(Tdt(grounds: true)).Edges, one => Assert.Equal(string.Empty, one));

        // AND THE IDENTITY A ROOM'S SLOT IS CHECKED AGAINST.
        TileIdentity? identity = TileIdentity.Of(walled);
        Assert.NotNull(identity);
        Assert.Equal((2, 1, "arena_tag"), (identity.Width, identity.Height, identity.Tag));
        Assert.Null(TileIdentity.Of(TileDefinition.Read(Tdt(inherits: "Metadata/Terrain/Test/Arena.tdt"))));
    }

    /// <summary>
    /// A tile whose sub-tiles hold nothing is dumped file by file: the definitions it inherits through, its template, and every sub-tile's mesh.
    /// </summary>
    [Fact]
    public void ATILEThatDrewNothingIsDumpedFileByFile()
    {
        Dictionary<string, byte[]> files = Install();
        files["Art/Models/Terrain/Test/Arena_c1r1.tgm"] = Tgm(props: 0, ground: 0);
        files["Art/Models/Terrain/Test/Arena_c2r1.tgm"] = Tgm(props: 0, ground: 0);
        Func<string, byte[]?> read = path => files.GetValueOrDefault(path);

        MonsterModel model = TileModels.Of(read, "Metadata/Terrain/Test/Child.tdt");
        Assert.False(model.Ready);
        string dump = ModelDump.OfTile(read, "Metadata/Terrain/Test/Child.tdt", model);

        Assert.Contains("did not load: the tile's sub-tiles hold no geometry", dump, StringComparison.Ordinal);
        int child = dump.IndexOf("=== .tdt Metadata/Terrain/Test/Child.tdt", StringComparison.Ordinal);
        int parent = dump.IndexOf("=== .tdt Metadata/Terrain/Test/Arena.tdt", StringComparison.Ordinal);
        int template = dump.IndexOf("=== .tgt Art/Models/Terrain/Test/Arena.tgt", StringComparison.Ordinal);
        Assert.True(child >= 0 && parent > child && template > parent, dump);
        Assert.Contains("=== sub-tiles of Art/Models/Terrain/Test/Arena.tgt (2x1)", dump, StringComparison.Ordinal);
        Assert.Contains("  c2r1 Art/Models/Terrain/Test/Arena_c2r1.tgm", dump, StringComparison.Ordinal);
        Assert.Contains("    props: none", dump, StringComparison.Ordinal);
        Assert.Contains("    ground: no ground", dump, StringComparison.Ordinal);
    }

    /// <summary>The dump prints the graphs a tile's materials name, once each, after the materials.</summary>
    [Fact]
    public void ANDTheDumpPrintsTheGraphsTheMaterialsName()
    {
        Dictionary<string, byte[]> files = Install();
        const string Graph = "Metadata/Materials/Environment/Ledge_Blend.fxgraph";
        files["Art/Textures/Floor.mat"] = Encoding.UTF8.GetBytes(
            $$"""{"graphinstances":[{"parent":"{{Graph}}","custom_parameters":[{"name":"AlbedoTransparency_TEX","parameters":[{"path":"Art/Textures/floor.dds"}]}]}]}""");
        files[Graph] = Encoding.UTF8.GetBytes("{\"nodes\":[\"mask\"]}");

        Func<string, byte[]?> read = path => files.GetValueOrDefault(path);
        string dump = ModelDump.OfTile(read, "Metadata/Terrain/Test/Arena.tdt", TileModels.Of(read, "Metadata/Terrain/Test/Arena.tdt"));

        int at = dump.IndexOf("=== graph " + Graph, StringComparison.Ordinal);
        Assert.True(at > dump.IndexOf("=== .mat Art/Textures/Floor.mat", StringComparison.Ordinal), dump);
        Assert.Equal(at, dump.LastIndexOf("=== graph " + Graph, StringComparison.Ordinal));
        Assert.Contains("{\"nodes\":[\"mask\"]}", dump, StringComparison.Ordinal);
    }

    /// <summary>
    /// A tile asked for its graphs colours the shapes whose graphs say more than a texture, and names what it left out.
    /// </summary>
    /// <remarks>
    /// THE FLOOR IS GIVEN THE LEDGE'S SHAPE OF MATERIAL: a graph that mixes by a mask, and after it
    /// one whose colour needs a node nobody has read. The mix stands, the other is named, and the
    /// wall - an ordinary colour map - keeps its plain skin.
    /// </remarks>
    [Fact]
    public void ANDATileAskedForItsGraphsIsColouredByThemAndSaysWhatItLeftOut()
    {
        Dictionary<string, byte[]> files = Install();
        files["Art/Textures/Floor.mat"] = Encoding.UTF8.GetBytes(
            """
            {"graphinstances":[
              {"parent":"Metadata/Ledge.fxgraph","custom_parameters":[{"name":"Meshmap","parameters":[{"path":"Art/Textures/floor.dds","srgb":false}]}]},
              {"parent":"Metadata/Effects/Graphs/General/Breach.fxgraph"}]}
            """);
        files["Metadata/Ledge.fxgraph"] = Encoding.UTF8.GetBytes(
            """
            {"nodes":[
              {"type":"InputUV","index":0,"stage":"Texturing"},
              {"type":"SampleTexture","index":6,"parameters":[{"format":"BC1","srgb":false},{}],"custom_parameter":"Meshmap"},
              {"type":"SampleTexture","index":0,"parameters":[{"path":"Art/Textures/wall.dds","srgb":true},{}]},
              {"type":"Lerp3","index":0},
              {"type":"AlbedoColor","index":0,"stage":"Texturing"}],
             "links":[
              {"src":{"type":"InputUV","index":0,"stage":"Texturing","variable":"output"},"dst":{"type":"SampleTexture","index":6,"variable":"uv"}},
              {"src":{"type":"InputUV","index":0,"stage":"Texturing","variable":"output"},"dst":{"type":"SampleTexture","index":0,"variable":"uv"}},
              {"src":{"type":"SampleTexture","index":6,"variable":"rgba"},"dst":{"type":"Lerp3","index":0,"variable":"a"}},
              {"src":{"type":"SampleTexture","index":0,"variable":"rgba"},"dst":{"type":"Lerp3","index":0,"variable":"b"}},
              {"src":{"type":"SampleTexture","index":6,"variable":"rgba","swizzle":"y"},"dst":{"type":"Lerp3","index":0,"variable":"alpha"}},
              {"src":{"type":"Lerp3","index":0,"variable":"output"},"dst":{"type":"AlbedoColor","index":0,"stage":"Texturing","variable":"input"}}]}
            """);
        files["Metadata/Effects/Graphs/General/Breach.fxgraph"] = Encoding.UTF8.GetBytes(
            """
            {"nodes":[
              {"type":"InputAlbedoColor","index":0,"stage":"PreLighting"},
              {"type":"BreachMinSphereDist","index":0},
              {"type":"AlbedoColor","index":0,"stage":"PreLighting"}],
             "links":[
              {"src":{"type":"InputAlbedoColor","index":0,"stage":"PreLighting","variable":"output"},"dst":{"type":"BreachMinSphereDist","index":0,"variable":"world_pos"}},
              {"src":{"type":"BreachMinSphereDist","index":0,"variable":"dist"},"dst":{"type":"AlbedoColor","index":0,"stage":"PreLighting","variable":"input"}}]}
            """);

        MonsterModel model = TileModels.Of(path => files.GetValueOrDefault(path), "Metadata/Terrain/Test/Arena.tdt", ground: false, shaded: true);

        Assert.True(model.Ready, model.Why);
        Assert.Equal(1, model.ShadedBy);
        Assert.Equal(["BreachMinSphereDist in Breach"], model.Unshaded);

        // c1r1 is "wall, floor" and c2r1 "floor, floor": the floor's three shapes run the program.
        Assert.Null(model.Shades[0]);
        Assert.All([1, 2, 3], shape => Assert.NotNull(model.Shades[shape]));
        Assert.Same(model.Shades[1], model.Shades[3]);
        Assert.NotNull(model.Skins[0]);

        // THE DUMP NAMES WHAT THE PROGRAM READS, how, and what the decoder made of every channel:
        // the one texel is blue 10, green 20, red 30 and alpha 255, written blue first.
        string dump = ModelDump.OfTile(path => files.GetValueOrDefault(path), "Metadata/Terrain/Test/Arena.tdt", model);
        Assert.Contains("=== program texture Art/Textures/floor.dds (read as linear)", dump, StringComparison.Ordinal);
        Assert.Contains("=== program texture Art/Textures/wall.dds (read as sRGB)", dump, StringComparison.Ordinal);
        Assert.Contains("  r: 30 30 30 30 30 · 30.0", dump, StringComparison.Ordinal);
        Assert.Contains("  a: 255 255 255 255 255 · 255.0", dump, StringComparison.Ordinal);
        Assert.Contains("graphs program", dump, StringComparison.Ordinal);

        // AND THE AREA'S NEEDS SAY THE SAME FOR THE TILE, by path, as the pane's line would - and a
        // path that will not load says so rather than nothing.
        IReadOnlyDictionary<string, string> needs = AreaNeeds.Of(
            path => files.GetValueOrDefault(path), ["Metadata/Terrain/Test/Arena.tdt", "Metadata/Terrain/Test/Gone.tdt"], string.Empty, null).Needs;
        Assert.Equal("BreachMinSphereDist in Breach", needs["Metadata/Terrain/Test/Arena.tdt"]);
        Assert.StartsWith("did not load:", needs["Metadata/Terrain/Test/Gone.tdt"], StringComparison.Ordinal);

        // SEARCHABLE AS A COLUMN: needs:BreachMinSphereDist finds the tile, and a tile the needs do not name is not found.
        TileBook book = TileBook.Of(["Metadata/Terrain/Test/Arena.tdt", "Metadata/Terrain/Test/Other.tdt"], new Dictionary<string, int> { ["Metadata/Terrain/Test/Arena.tdt"] = 1 }, needs);
        RowSet? found = book.Matching(ColumnQuery.Parse("needs:BreachMinSphereDist").Term, out string why);
        Assert.True(found is not null, why);
        var rows = new List<int>();
        found!.CopyTo(rows);
        Assert.Equal([book.Row("Metadata/Terrain/Test/Arena.tdt")], rows);
    }

    /// <summary>A tile whose material reads the clock says so, and the book finds it with clock:yes.</summary>
    [Fact]
    public void ATileWhoseGraphsReadTheClockIsNamedAndFoundByClockYes()
    {
        Dictionary<string, byte[]> files = Install();
        files["Art/Textures/Floor.mat"] = Encoding.UTF8.GetBytes(
            """
            {"graphinstances":[{"parent":"Metadata/Flowing.fxgraph"}]}
            """);
        files["Metadata/Flowing.fxgraph"] = Encoding.UTF8.GetBytes(
            """
            {"nodes":[
              {"type":"Time","index":0},
              {"type":"AlbedoColor","index":0,"stage":"Texturing"}],
             "links":[
              {"src":{"type":"Time","index":0,"variable":"output"},"dst":{"type":"AlbedoColor","index":0,"stage":"Texturing","variable":"input"}}]}
            """);

        MonsterModel model = TileModels.Of(path => files.GetValueOrDefault(path), "Metadata/Terrain/Test/Arena.tdt", ground: false, shaded: true);
        Assert.True(model.Ready, model.Why);
        Assert.Equal(["Art/Textures/Floor.mat"], model.Clocked);

        AreaReading reading = AreaNeeds.Of(
            path => files.GetValueOrDefault(path), ["Metadata/Terrain/Test/Arena.tdt", "Metadata/Terrain/Test/Gone.tdt"], string.Empty, null);
        Assert.Equal("Art/Textures/Floor.mat", reading.Clocked["Metadata/Terrain/Test/Arena.tdt"]);
        Assert.False(reading.Clocked.ContainsKey("Metadata/Terrain/Test/Gone.tdt"));

        TileBook book = TileBook.Of(
            ["Metadata/Terrain/Test/Arena.tdt", "Metadata/Terrain/Test/Other.tdt"],
            new Dictionary<string, int> { ["Metadata/Terrain/Test/Arena.tdt"] = 1, ["Metadata/Terrain/Test/Other.tdt"] = 1 },
            reading.Needs,
            reading.Clocked);
        RowSet? found = book.Matching(ColumnQuery.Parse("clock:yes").Term, out string why);
        Assert.True(found is not null, why);
        var rows = new List<int>();
        found!.CopyTo(rows);
        Assert.Equal([book.Row("Metadata/Terrain/Test/Arena.tdt")], rows);

        // AND ACROSS THE WHOLE INSTALL, from the definitions, templates and graphs alone - no area, no
        // geometry. A tile not placed anywhere is found; a room is passed over.
        var walked = 0;
        IReadOnlySet<string> anywhere = TileClocks.Of(
            path => files.GetValueOrDefault(path),
            ["Metadata/Terrain/Test/Arena.tdt", "Metadata/Terrain/Test/Gone.tdt", "Metadata/Terrain/Test/Rooms/r.arm"],
            () => walked++);
        Assert.Equal(["Metadata/Terrain/Test/Arena.tdt"], anywhere);
        Assert.Equal(3, walked);

        TileBook everywhere = TileBook.Of(["Metadata/Terrain/Test/Arena.tdt", "Metadata/Terrain/Test/Other.tdt"], null, clockedAnywhere: anywhere);
        RowSet? far = everywhere.Matching(ColumnQuery.Parse("clock:yes").Term, out why);
        Assert.True(far is not null, why);
        rows.Clear();
        far!.CopyTo(rows);
        Assert.Equal([everywhere.Row("Metadata/Terrain/Test/Arena.tdt")], rows);
    }

    /// <summary>A definition's four corner ground types are read where annalithic reads them, and only whole.</summary>
    [Fact]
    public void ADEFINITIONReadsItsCornerGroundTypesAndRefusesAHalfWord()
    {
        TileDefinition plain = TileDefinition.Read(Tdt());
        Assert.True(plain.Ready, plain.Why);
        Assert.Equal(["", "", "", ""], plain.Grounds);

        TileDefinition grounded = TileDefinition.Read(Tdt(grounds: true));
        Assert.True(grounded.Ready, grounded.Why);
        Assert.Equal(["Metadata/Terrain/Test/sand.gt", "Metadata/Terrain/Test/sand.gt", "Metadata/Terrain/Test/rock.gt", ""], grounded.Grounds);
    }

    /// <summary>
    /// The dump's ground section: corners, their types, the mask, and what the tilesets using the tile draw its ground with.
    /// </summary>
    /// <remarks>
    /// Three tilesets: one names the tile, one does not, and one names a tile of the same FILE name in
    /// another folder - listed and not opened, since its ground is not this tile's. Of the opened
    /// one's MaterialsList only the group the corner type selects and the unnamed group are printed,
    /// of its overrides only the line touching this tile, and every ground material they offer is
    /// printed with the Input its graph takes its coordinates from.
    /// </remarks>
    [Fact]
    public void THEDUMPPrintsTheGroundChainAndWhatTheTilesetsUsingTheTileDrawItsGroundWith()
    {
        Dictionary<string, byte[]> files = Install();
        files["Metadata/Terrain/Test/Arena.tdt"] = Tdt(grounds: true);
        files["Metadata/Terrain/Test/sand.gt"] = Encoding.Unicode.GetBytes("SandFill\r\n1 1 0 0\r\n");
        files["Art/Models/Terrain/Test/Arena_mask.dds"] = Dds();
        files["Metadata/Terrain/Test/desert.tsi"] = Encoding.Unicode.GetBytes(
            "version 3\r\nTileSet \"desert.tst\"\r\nMaterialsList \"desert.mtd\"\r\nTileMaterialOverrides \"desert.tmo\"\r\n");
        files["Metadata/Terrain/Test/desert.tst"] = Encoding.Unicode.GetBytes("\"Metadata/Terrain/Test/Arena.tdt\" 1\r\n");
        files["Metadata/Terrain/Test/desert.mtd"] = Encoding.Unicode.GetBytes(
            """
            version 5
            1 0
            "Art/Textures/Ground/Dirt.mat"
            // SAND
            "SandFill" 2 0
            "Art\Textures\Ground\Sand.mat"
            "Art/Textures/Ground/Dune.mat"
            60 40 16
            "RockFill" 1 0
            "Art/Textures/Ground/Rock.mat"
            """);
        files["Metadata/Terrain/Test/desert.tmo"] = Encoding.Unicode.GetBytes(
            "version 1\r\n\"Art/Textures/Wall.mat\" \"Art/Textures/Prism.mat\"\r\n\"Art/Textures/Elsewhere.mat\" \"Art/Textures/Prism.mat\"\r\n");
        files["Art/Textures/Ground/Sand.mat"] = Encoding.UTF8.GetBytes(
            """{"graphinstances":[{"parent":"Metadata/Ground.fxgraph","custom_parameters":[{"name":"Colour","parameters":[{"path":"Art/Textures/floor.dds","srgb":true}]}]}]}""");
        files["Metadata/Ground.fxgraph"] = Encoding.UTF8.GetBytes(
            """
            {"nodes":[
              {"type":"InputWorldPos","index":0,"stage":"Texturing"},
              {"type":"SampleTexture","index":0,"parameters":[{"srgb":true},{}],"custom_parameter":"Colour"},
              {"type":"AlbedoColor","index":0,"stage":"Texturing"}],
             "links":[
              {"src":{"type":"InputWorldPos","index":0,"stage":"Texturing","variable":"output","swizzle":"xy"},"dst":{"type":"SampleTexture","index":0,"variable":"uv"}},
              {"src":{"type":"SampleTexture","index":0,"variable":"rgba"},"dst":{"type":"AlbedoColor","index":0,"stage":"Texturing","variable":"input"}}]}
            """);
        files["Metadata/Terrain/Woods/woods.tsi"] = Encoding.Unicode.GetBytes("version 3\r\nTileSet \"woods.tst\"\r\n");
        files["Metadata/Terrain/Woods/woods.tst"] = Encoding.Unicode.GetBytes("\"Metadata/Terrain/Woods/Tree.tdt\" 1\r\n");
        files["Metadata/Terrain/Other/other.tsi"] = Encoding.Unicode.GetBytes("version 3\r\nTileSet \"other.tst\"\r\n");
        files["Metadata/Terrain/Other/other.tst"] = Encoding.Unicode.GetBytes("\"Metadata/Terrain/Other/Arena.tdt\" 1\r\n");

        Func<string, byte[]?> read = path => files.GetValueOrDefault(path);
        MonsterModel model = TileModels.Of(read, "Metadata/Terrain/Test/Arena.tdt");
        string dump = ModelDump.OfTile(
            read, "Metadata/Terrain/Test/Arena.tdt", model,
            TilesetIndex.Build(read, ["Metadata/Terrain/Other/other.tsi", "Metadata/Terrain/Woods/woods.tsi", "Metadata/Terrain/Test/desert.tsi"]));

        Assert.Contains("  down-left: Metadata/Terrain/Test/sand.gt", dump, StringComparison.Ordinal);
        Assert.Contains("  up-left: -", dump, StringComparison.Ordinal);
        Assert.Contains("=== ground mask Art/Models/Terrain/Test/Arena_mask.dds", dump, StringComparison.Ordinal);
        Assert.Contains("=== ground mesh of Art/Models/Terrain/Test/Arena.tgt", dump, StringComparison.Ordinal);

        // EXACT FIRST, the same file name elsewhere listed and left shut.
        Assert.Contains("searched 3 tilesets; 1 place this tile, 1 a tile of the same file name in another folder", dump, StringComparison.Ordinal);
        Assert.Contains("  Metadata/Terrain/Other/other.tsi (places metadata/terrain/other/arena.tdt - another tile, not opened)", dump, StringComparison.Ordinal);
        Assert.Contains("=== .tsi Metadata/Terrain/Test/desert.tsi", dump, StringComparison.Ordinal);
        Assert.DoesNotContain("=== .tsi Metadata/Terrain/Other/other.tsi", dump, StringComparison.Ordinal);
        Assert.DoesNotContain("=== .tsi Metadata/Terrain/Woods/woods.tsi", dump, StringComparison.Ordinal);

        // THE CORNER TYPE'S GROUP BY ITS NAME, and the unnamed one - not the group no corner selects.
        Assert.Contains("=== MaterialsList Metadata/Terrain/Test/desert.mtd", dump, StringComparison.Ordinal);
        Assert.Contains("version 5 · 3 groups: (unnamed), \"SandFill\", \"RockFill\"", dump, StringComparison.Ordinal);
        Assert.Contains("  \"SandFill\" at 2 corners: 2 choices · weights 60 40 · then 16", dump, StringComparison.Ordinal);
        Assert.Contains("      Art/Textures/Ground/Sand.mat", dump, StringComparison.Ordinal);
        Assert.Contains("  the unnamed group: 1 choice", dump, StringComparison.Ordinal);
        Assert.DoesNotContain("Rock.mat", dump, StringComparison.Ordinal);

        Assert.Contains("2 overrides, 1 of this tile's materials or its ground's:", dump, StringComparison.Ordinal);
        Assert.Contains("  Art/Textures/Wall.mat -> Art/Textures/Prism.mat", dump, StringComparison.Ordinal);
        Assert.DoesNotContain("Elsewhere.mat", dump, StringComparison.Ordinal);

        // AND THE GROUND MATERIALS, once each, with where their coordinates come from.
        Assert.Contains("=== ground materials (3)", dump, StringComparison.Ordinal);
        Assert.Contains("=== ground .mat Art/Textures/Ground/Sand.mat", dump, StringComparison.Ordinal);
        Assert.Contains("offered by: Test \"SandFill\"", dump, StringComparison.Ordinal);
        Assert.Contains("offered by: Test (unnamed)", dump, StringComparison.Ordinal);
        Assert.Contains("graphs name: InputWorldPos", dump, StringComparison.Ordinal);
        Assert.Contains("=== graph Metadata/Ground.fxgraph", dump, StringComparison.Ordinal);
        Assert.Contains("=== ground texture Art/Textures/floor.dds (read as sRGB)", dump, StringComparison.Ordinal);
    }

    /// <summary>
    /// A tile drawn as a tileset wears the materials it swaps in, and says how many - the dump first of all.
    /// </summary>
    /// <remarks>
    /// The wall's material is swapped for a prism, the floor's is left: the swapped shape is painted
    /// from the prism's texture, the count says one, and the dump says the materials it prints are
    /// the swapped-in ones before printing them.
    /// </remarks>
    [Fact]
    public void ATILEDrawnAsATilesetWearsTheMaterialsItSwapsIn()
    {
        Dictionary<string, byte[]> files = Install();
        files["Art/Textures/Prism.mat"] = Mat("Art/Textures/prism.dds");
        files["Art/Textures/prism.dds"] = Dds();
        Func<string, byte[]?> read = path => files.GetValueOrDefault(path);
        var swaps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["art/textures/wall.mat"] = "Art/Textures/Prism.mat" };

        MonsterModel own = TileModels.Of(read, "Metadata/Terrain/Test/Arena.tdt");
        MonsterModel drawn = TileModels.Of(read, "Metadata/Terrain/Test/Arena.tdt", swaps: swaps, tileset: "maps/desert");

        Assert.True(drawn.Ready, drawn.Why);
        Assert.Equal(0, own.Swapped);
        Assert.Equal(string.Empty, own.Tileset);
        Assert.Equal("Art/Textures/Wall.mat", own.ShapeMaterials[0]);

        Assert.Equal(1, drawn.Swapped);
        Assert.Equal("maps/desert", drawn.Tileset);
        Assert.Equal("Art/Textures/Prism.mat", drawn.ShapeMaterials[0]);
        Assert.Equal("Art/Textures/prism.dds", drawn.ShapeTextures[0]);
        Assert.Equal("Art/Textures/Floor.mat", drawn.ShapeMaterials[1]);

        string dump = ModelDump.OfTile(read, "Metadata/Terrain/Test/Arena.tdt", drawn);
        Assert.StartsWith(
            "tile: Metadata/Terrain/Test/Arena.tdt" + Environment.NewLine
                + "drawn as tileset maps/desert: 1 of the tile's materials swapped",
            dump, StringComparison.Ordinal);
        Assert.Contains("=== .mat Art/Textures/Prism.mat", dump, StringComparison.Ordinal);
    }

    /// <summary>
    /// The dump counts the shader sources by folder; the export writes every one whole, in path order.
    /// </summary>
    [Fact]
    public void THEDUMPCountsTheShaderSourcesAndTheExportWritesThemWhole()
    {
        Dictionary<string, byte[]> files = Install();
        files["Shaders/Renderer/Ground.ffx"] = Encoding.UTF8.GetBytes("fragment ground\nfloat2 uv = world_pos.xy * scale;\n");
        files["Shaders/Common/Util.hlsl"] = Encoding.Unicode.GetBytes("\uFEFFfloat a;\r\n// Terrain coordinates\r\nfloat b;\r\n");
        Func<string, byte[]?> read = path => files.GetValueOrDefault(path);
        MonsterModel model = TileModels.Of(read, "Metadata/Terrain/Test/Arena.tdt");
        string[] shaders = ["Shaders/Renderer/Ground.ffx", "Shaders/Common/Util.hlsl", "Shaders/Common/Missing.hlsl"];

        string dump = ModelDump.OfTile(read, "Metadata/Terrain/Test/Arena.tdt", model, null, shaders);
        Assert.Contains("3 files, by folder:", dump, StringComparison.Ordinal);
        Assert.Contains("  Shaders/Common  2", dump, StringComparison.Ordinal);
        Assert.Contains("  Shaders/Renderer  1", dump, StringComparison.Ordinal);
        Assert.Contains("every one is written whole beside this dump, to " + ModelDump.ShaderFile, dump, StringComparison.Ordinal);
        Assert.DoesNotContain("world_pos.xy", dump, StringComparison.Ordinal);

        string sources = ModelDump.ShaderSources(read, shaders);
        int missing = sources.IndexOf("=== shader source Shaders/Common/Missing.hlsl", StringComparison.Ordinal);
        int util = sources.IndexOf("=== shader source Shaders/Common/Util.hlsl", StringComparison.Ordinal);
        int ground = sources.IndexOf("=== shader source Shaders/Renderer/Ground.ffx", StringComparison.Ordinal);
        Assert.True(missing >= 0 && missing < util && util < ground, "in path order");
        Assert.Contains("float2 uv = world_pos.xy * scale;", sources, StringComparison.Ordinal);
        Assert.Contains("// Terrain coordinates", sources, StringComparison.Ordinal);
        Assert.Contains("(not in the install)", sources, StringComparison.Ordinal);

        string none = ModelDump.OfTile(read, "Metadata/Terrain/Test/Arena.tdt", model);
        Assert.Contains("(the install walk found none:", none, StringComparison.Ordinal);
    }

    [Fact]
    public void ANDATILECostsWhatItReadMaterialsAndTexturesIncluded()
    {
        // COUNTED AT THE READER. The .mat and .dds reads happen inside the dressing, which used to be
        // handed the bare reader - so the line under the picture said a tile was a few kilobytes of
        // geometry, while Bytes promises the material and the texture are in it.
        Dictionary<string, byte[]> files = Install();
        var handed = new List<string>();
        long bytes = 0;
        byte[]? Read(string path)
        {
            byte[]? got = files.GetValueOrDefault(path);
            if (got is not null)
            {
                handed.Add(path);
                bytes += got.Length;
            }

            return got;
        }

        MonsterModel model = TileModels.Of(Read, "Metadata/Terrain/Test/Arena.tdt");

        Assert.True(model.Ready, model.Why);
        Assert.Equal(handed.Count, model.Files);
        Assert.Equal(bytes, model.Bytes);
        Assert.Contains("Art/Textures/Wall.mat", handed);
        Assert.Contains("Art/Textures/Floor.mat", handed);
        Assert.Contains("Art/Textures/wall.dds", handed);
        Assert.Contains("Art/Textures/floor.dds", handed);
    }

    [Fact]
    public void ANDAnInheritingTileIsDrawnFromWhatItInherits()
    {
        Dictionary<string, byte[]> files = Install();
        MonsterModel model = TileModels.Of(path => files.GetValueOrDefault(path), "Metadata/Terrain/Test/Child.tdt");

        Assert.True(model.Ready, model.Why);
        Assert.Equal(12, model.Mesh.Triangles);
    }

    [Fact]
    public void ANDAMissingSubTileIsNamedRatherThanSilentlyLeftOut()
    {
        Dictionary<string, byte[]> files = Install();
        files.Remove("Art/Models/Terrain/Test/Arena_c1r1.tgm");
        files.Remove("Art/Models/Terrain/Test/Arena_c2r1.tgm");

        MonsterModel model = TileModels.Of(path => files.GetValueOrDefault(path), "Metadata/Terrain/Test/Arena.tdt");

        Assert.False(model.Ready);
        Assert.Contains("Arena_c1r1.tgm", model.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void THEBOOKListsTheInstallAndTheAreaAndFindsTheAreasTiles()
    {
        TileBook book = TileBook.Of(
            ["Metadata/Terrain/Woods/Slash/HagWitchArena_01.tdt", "Metadata/Terrain/Maps/Port/Tiles/Pier_01.tdt"],
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["metadata/terrain/woods/slash/hagwitcharena_01.tdt"] = 1,
                ["Metadata/Terrain/Woods/Only/InMemory.tdt"] = 3,
            });

        Assert.Equal(3, book.Count);

        RowSet? here = book.Matching(ColumnQuery.Parse("here:yes").Term, out string why);
        Assert.True(here is not null, why);
        var rows = new List<int>();
        here!.CopyTo(rows);
        Assert.Equal(2, rows.Count);
        Assert.Equal(3, book.Placed[book.Row("Metadata/Terrain/Woods/Only/InMemory.tdt")]);

        Assert.Equal(("Woods", "Slash", "HagWitchArena_01"), TileBook.Split("Metadata/Terrain/Woods/Slash/HagWitchArena_01.tdt"));

        var sets = new List<Facet>();
        RowSet? all = book.Matching(ColumnQuery.Parse(string.Empty).Term, out _);
        book.Facets(all!, "set", sets);
        Assert.Equal(new Facet("Woods", 2), sets[0]);
    }

    internal static byte[] Mat(string texture)
        => Encoding.UTF8.GetBytes(
            $$"""{"graphinstances":[{"custom_parameters":[{"name":"AlbedoTransparency_TEX","parameters":[{"path":"{{texture}}"}]}]}]}""");

    /// <summary>A one-pixel uncompressed DDS - GameArtTests' layout.</summary>
    internal static byte[] Dds()
    {
        using var stream = new MemoryStream();
        using var write = new BinaryWriter(stream);
        write.Write(Encoding.ASCII.GetBytes("DDS "));
        write.Write(124);
        write.Write(0x1007);
        write.Write(1);
        write.Write(1);
        write.Write(4);
        write.Write(0);
        write.Write(0);
        write.Write(new byte[44]);
        write.Write(32);
        write.Write(0x41);
        write.Write(0);
        write.Write(32);
        write.Write(0x00FF0000);
        write.Write(0x0000FF00);
        write.Write(0x000000FF);
        write.Write(unchecked((int)0xFF000000));
        write.Write(0x1000);
        write.Write(new byte[16]);
        write.Write(new byte[] { 10, 20, 30, 255 });
        return stream.ToArray();
    }
}
