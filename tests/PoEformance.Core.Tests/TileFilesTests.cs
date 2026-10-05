using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using PoEformance.Features;
using PoEformance.Game.Files;

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
        int width = 2, int height = 1, int version = 7)
    {
        // THE TABLE STARTS WITH AN EMPTY STRING, so offset zero means "nothing" the way the game's
        // own files use it, and every other offset lands where a string begins.
        string[] strings = ["", inherits, template, "arena_tag", "edge"];
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
