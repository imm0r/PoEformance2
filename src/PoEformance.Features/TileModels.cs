using System.Globalization;
using System.Numerics;
using PoEformance.Game.Files;
using PoEformance.Game.World;

namespace PoEformance.Features;

/// <summary>
/// Walks a terrain tile from its definition to the triangles and textures that make it.
/// </summary>
/// <remarks>
/// THREE FILES AND A NAMING RULE:
///
///     .tdt                 the definition every placed tile names - may inherit another
///       -> .tgt            the template: how many sub-tiles, which materials, in which runs
///         -> .tgm          one per sub-tile, named {root}_c{x}r{y}.tgm - two DOLm blocks
///         -> .mat -> .dds  the same material walk a monster's skin takes
///
/// THE SUB-TILES ARE LAID OUT 250 UNITS APART, column x at (x - 1) * 250 and row y at
/// -(y - 1) * 250. Both references say so: zao's exporter offsets column and row index by 250 with
/// row numbers running down from the top, and annalithic's writes x + 250 per column and y - 250
/// per row. 250 is a terrain tile in world units - GameHelper2's TileToWorldConversion.
///
/// EACH SUB-TILE'S SHAPES TAKE THEIR MATERIALS IN RUNS - the template says "five shapes of material
/// 0, then three of material 1" - so a shape's material is its position in its own sub-tile, and the
/// joined mesh numbers every shape once so the dressing can find it again. THE GROUND IS LEFT
/// UNPAINTED: its material is the area's ground type, which a tile does not name, and painting it in
/// a wall's texture would be a confident wrong answer where a plain one is an honest gap.
///
/// NOT ANIMATED. A tile's geometry is static; anything in it that moves is a doodad, and doodads
/// belong to rooms - see the .arm view.
/// </remarks>
public static class TileModels
{
    /// <summary>How far an inheritance chain is followed.</summary>
    public const int MostHops = 8;

    /// <summary>A terrain tile's side in world units.</summary>
    public const float Side = 250f;

    /// <summary>
    /// Most sub-tiles read for one tile - a guard against a size read from the wrong place.
    /// </summary>
    /// <remarks>
    /// 256 WAS TOO FEW for a real tile: Port_Boss_01.tgt says 15x21, 315 sub-tiles, and is the whole
    /// harbour the boss fight stands in. A misread size is a garbage number in the thousands or
    /// millions, so the guard still catches what it is for at 1024.
    /// </remarks>
    public const int MostSubTiles = 1024;

    /// <summary>
    /// What the colour texture of a tile's black walls is called.
    /// </summary>
    /// <remarks>
    /// THE NAME IS THE ONLY MARK THERE IS: the walls are ordinary prop shapes in the .tgm, wearing
    /// an ordinary material whose colour map is <c>blacknofog.dds</c>. They hang down from a cliff's
    /// edge to close the gap under it, which the game's camera never looks into and this pane does.
    /// </remarks>
    public const string BlackWall = "blacknofog";

    /// <summary>
    /// Gathers the model for one tile, or says where the walk stopped. Never throws.
    /// </summary>
    /// <param name="read">How to get a file out of the install, by path.</param>
    /// <param name="path">The tile's <c>.tdt</c>, as the game or the install names it.</param>
    /// <param name="ground">
    /// Whether the ground block is drawn under the props. It is unpainted - see the remarks - and
    /// where it rises around a prop it hides the part of the tile somebody opened it to look at.
    /// </param>
    /// <param name="walls">
    /// Whether the shapes painted with <see cref="BlackWall"/> are drawn. They are geometry the game
    /// really has, and from any angle but the game's a black slab in front of the tile.
    /// </param>
    /// <param name="shaded">Whether each material's shader graphs are read - see MonsterModels.Shaded.</param>
    /// <param name="swaps">
    /// The materials a tileset swaps on the tiles it places - see TilesetIndex.Overrides - or null for
    /// the tile's own. EVERY DESERT TILESET placing <c>cliffcvm_stroma1</c> swaps all nine of its cliff
    /// and ledge materials, so the tile's own are what no area of the game shows.
    /// </param>
    /// <param name="tileset">The tileset the swaps are from, for the line under the picture.</param>
    /// <param name="laid">
    /// How the game laid the tile down in the current area - see <see cref="TileOrientation"/> - or
    /// unknown for the tile as its file holds it. THE WHOLE TILE TURNS AS ONE: every tile of a
    /// placed piece carries the same selector, and each sub-tile is turned about the same point, so
    /// a piece several tiles across keeps its shape. The point is the template's origin, which moves
    /// the picture but not the tile - the pane frames on the model's own box.
    /// </param>
    /// <param name="progress">Where the build says how far it has got, or null - see <see cref="ModelProgress"/>.</param>
    public static MonsterModel Of(
        Func<string, byte[]?>? read,
        string? path,
        bool ground = true,
        bool walls = true,
        bool shaded = false,
        IReadOnlyDictionary<string, string>? swaps = null,
        string tileset = "",
        TileOrientation laid = default,
        ModelProgress? progress = null)
    {
        if (read is null)
        {
            return MonsterModel.None with { Why = "no install to read" };
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return MonsterModel.None with { Why = "no tile to read" };
        }

        return Built(read, path, ground, walls, shaded, swaps, tileset, laid, new MonsterModels.Paints { Progress = progress }, apart: false).Model;
    }

    /// <summary>
    /// One tile as a room laid from the area's own tiles takes it: the props painted, the ground apart and unturned, and the template's size.
    /// </summary>
    /// <remarks>
    /// THE GROUND APART because the room needs it twice: drawn, and asked how high it is at a point,
    /// which is how the room sets each piece on the area's own ground. The props come back as
    /// MonsterModel.None where the tile is ground and nothing else - a floor tile - with its ground
    /// still given. One paint cache for every tile of a room, as a room's doodads share one.
    /// </remarks>
    internal static (MonsterModel Props, SkinnedMesh Ground, int Width, int Height) Apart(
        Func<string, byte[]?> read, string path, bool walls, MonsterModels.Paints paints)
    {
        (MonsterModel model, SkinnedMesh ground, int wide, int tall) = Built(read, path, true, walls, false, null, string.Empty, default, paints, apart: true);
        return (model, ground, wide, tall);
    }

    /// <summary>
    /// One tile as a stage for a monster: drawn whole, as <see cref="Of"/> draws it, with its ground given apart as well.
    /// </summary>
    /// <remarks>
    /// THE GROUND TWICE, in the model and on its own, because the stage needs it both ways: drawn under
    /// the monster, and asked how high it is where he stands - see StagedModels. The second copy is one
    /// join of the ground pieces, at load.
    /// </remarks>
    internal static (MonsterModel Model, SkinnedMesh Ground) Stage(
        Func<string, byte[]?> read,
        string path,
        bool walls,
        bool shaded,
        IReadOnlyDictionary<string, string>? swaps,
        string tileset,
        ModelProgress? progress)
    {
        (MonsterModel model, SkinnedMesh ground, _, _) = Built(
            read, path, true, walls, shaded, swaps, tileset, default, new MonsterModels.Paints { Progress = progress }, apart: false, groundToo: true);
        return (model, ground);
    }

    /// <summary>The walk behind <see cref="Of"/>, <see cref="Apart"/> and <see cref="Stage"/>.</summary>
    private static (MonsterModel Model, SkinnedMesh Ground, int Width, int Height) Built(
        Func<string, byte[]?> read,
        string path,
        bool ground,
        bool walls,
        bool shaded,
        IReadOnlyDictionary<string, string>? swaps,
        string tileset,
        TileOrientation laid,
        MonsterModels.Paints? paints,
        bool apart,
        bool groundToo = false)
    {
        long bytes = 0;
        var files = 0;
        byte[]? Counted(string one)
        {
            byte[]? got = read(one.Replace('\\', '/').Trim());
            if (got is not null)
            {
                bytes += got.Length;
                files++;
            }

            return got;
        }

        (TileDefinition definition, string why) = Defined(Counted, path);
        if (!definition.Ready || definition.Templates.Count == 0)
        {
            return (MonsterModel.None with
            {
                Why = why.Length > 0 ? why : $"the tile names no template: {path}",
                Bytes = bytes,
                Files = files,
            }, SkinnedMesh.None, 0, 0);
        }

        int wide = 0;
        int tall = 0;

        var joins = new List<MeshJoin>();
        var grounds = new List<MeshJoin>();
        var named = new List<(string Shape, string Material)>();
        var swapped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var said = new List<string>();
        int inexact = 0;
        int subTiles = 0;
        Matrix4x4? turn = Turned(laid);

        // THE TEMPLATES READ FIRST, so the bar knows how many sub-tiles there are before the first is read.
        (string Path, TileTemplate Layout)[] layouts = [.. definition.Templates.Select(one => (one, TileTemplate.Read(Counted(one))))];
        int expected = layouts.Sum(one => one.Layout.Ready && one.Layout.Width * one.Layout.Height <= MostSubTiles ? one.Layout.Width * one.Layout.Height : 0);
        ModelProgress.Step reading = ModelProgress.Begin(paints?.Progress, "reading the sub-tiles", expected);
        foreach ((string template, TileTemplate layout) in layouts)
        {
            if (!layout.Ready)
            {
                said.Add($"the template did not read: {template} - {layout.Why}");
                continue;
            }

            if (layout.Width * layout.Height > MostSubTiles)
            {
                said.Add($"the template says {layout.Width}x{layout.Height} sub-tiles: {template}");
                continue;
            }

            wide = Math.Max(wide, layout.Width);
            tall = Math.Max(tall, layout.Height);

            for (var y = 1; y <= layout.Height; y++)
            {
                for (var x = 1; x <= layout.Width; x++)
                {
                    string mesh = layout.MeshOf(x, y);
                    TileMesh part = TileMesh.Read(Counted(mesh));
                    reading.Advance();
                    if (!part.Ready)
                    {
                        said.Add($"the sub-tile did not read: {mesh} - {part.Why}");
                        continue;
                    }

                    subTiles++;
                    inexact += part.Exact ? 0 : 1;
                    Matrix4x4 place = Matrix4x4.CreateTranslation((x - 1) * Side, -(y - 1) * Side, 0f);
                    if (turn is { } turning)
                    {
                        place *= turning;
                    }

                    if (part.Props.Ready)
                    {
                        joins.Add(new MeshJoin(part.Props, null, null, place));
                        Runs(layout, layout.RunsOf(x, y), part.Props.Shapes.Count, named, swaps, swapped);
                    }

                    if (ground && part.Ground.Ready)
                    {
                        grounds.Add(new MeshJoin(part.Ground, null, null, place));
                    }
                }
            }
        }

        reading.Dispose();

        // THE GROUND AFTER THE PROPS, so the props' shapes keep the numbers the runs gave them and
        // every ground shape comes after - unnamed, and so unpainted. Or apart, where asked.
        SkinnedMesh groundApart = SkinnedMesh.None;
        if (apart || groundToo)
        {
            groundApart = SkinnedMesh.Joined(grounds);
        }

        if (!apart)
        {
            joins.AddRange(grounds);
        }

        SkinnedMesh joined = Numbered(SkinnedMesh.Joined(joins));
        if (!joined.Ready)
        {
            return (MonsterModel.None with
            {
                Why = apart && groundApart.Ready
                    ? $"the tile is ground and nothing else: {path}"
                    : said.Count > 0 ? said[0] : $"the tile's sub-tiles hold no geometry: {path}",
                Bytes = bytes,
                Files = files,
            }, groundApart, wide, tall);
        }

        // A WALK THAT DID NOT END AT THE END OF THE FILE IS SAID, not hidden: the props drew because
        // their block checked itself, but a sub-tile whose tail did not add up is a layout this has
        // not seen, and that is worth a line under the picture.
        string move = inexact == 0
            ? "a terrain tile is static - anything that moves in it is a doodad"
            : string.Create(CultureInfo.InvariantCulture,
                $"{inexact} of {subTiles} sub-tiles did not end where their file does - the layout after the props is not fully understood");

        // THROUGH THE SAME COUNTER AS EVERYTHING ABOVE IT, because the materials and the textures are
        // most of what a tile costs to open - and Bytes promises to include them. Handed the bare
        // reader, Worn's .mat and .dds reads went uncounted and the line under the picture said
        // a tile was a few kilobytes of geometry.
        paints ??= new MonsterModels.Paints();
        MonsterModel model = MonsterModels.Worn(Counted, joined, named, paintTheRest: false, path, move, paints) with
        {
            Bytes = bytes,
            Files = files,
            Parts = subTiles,
            Kind = ModelKind.Tile,
            Swapped = swapped.Count,
            Tileset = swaps is null ? string.Empty : tileset,
            Laid = laid.IsPlacement ? laid.ToString() : string.Empty,
        };

        // THE WALLS GO BEFORE THE GRAPHS ARE READ, so a material only the walls wear is not compiled
        // for nothing.
        model = walls ? model : Unwalled(model);
        return (shaded ? MonsterModels.Shaded(Counted, model, paints) : model, groundApart, wide, tall);
    }

    /// <summary>
    /// The matrix that lays a tile's points down the way <paramref name="laid"/> says, or null where it leaves them as they are.
    /// </summary>
    /// <remarks>
    /// Rows, as System.Numerics multiplies: X' = X * M11 + Y * M21 and Y' = X * M12 + Y * M22, with
    /// the height left alone. A MIRROR TURNS EVERY TRIANGLE'S WINDING over, which costs nothing here:
    /// the picture neither culls nor lights one side only - see MeshPicture.
    /// </remarks>
    public static Matrix4x4? Turned(TileOrientation laid)
    {
        if (!laid.IsPlacement || (laid.Degrees == 0 && !laid.Mirrored))
        {
            return null;
        }

        (int xx, int xy, int yx, int yy) = laid.Turn;
        return new Matrix4x4(
            xx, yx, 0f, 0f,
            xy, yy, 0f, 0f,
            0f, 0f, 1f, 0f,
            0f, 0f, 0f, 1f);
    }

    /// <summary>
    /// The model without the shapes whose colour texture is <see cref="BlackWall"/>.
    /// </summary>
    /// <remarks>
    /// AFTER THE DRESSING, because the texture is what marks a wall and only the dressing reads it.
    /// The vertices stay where they are - a few unused ones cost nothing - and the box is worked out
    /// again from the triangles that remain, because the walls hang furthest down and the floor and
    /// the framing are placed from the box.
    /// </remarks>
    private static MonsterModel Unwalled(MonsterModel model)
    {
        SkinnedMesh mesh = model.Mesh;
        IReadOnlyList<string> textures = model.ShapeTextures;
        int count = mesh.Shapes.Count;
        if (textures.Count != count || model.Skins.Count != count || model.Modes.Count != count)
        {
            return model;
        }

        var keep = new bool[count];
        int kept = 0;
        int indices = 0;
        for (var shape = 0; shape < count; shape++)
        {
            keep[shape] = !textures[shape].Contains(BlackWall, StringComparison.OrdinalIgnoreCase);
            if (keep[shape])
            {
                kept++;
                indices += mesh.Shapes[shape].Count;
            }
        }

        if (kept == count || kept == 0)
        {
            return model;
        }

        var into = new int[indices];
        var shapes = new MeshShape[kept];
        var skins = new Mipmaps?[kept];
        var modes = new string[kept];
        var materials = new string[kept];
        var painted = new string[kept];
        Vector3[] positions = mesh.Positions;
        var least = new Vector3(float.MaxValue);
        var most = new Vector3(float.MinValue);
        int at = 0;
        int next = 0;
        for (var shape = 0; shape < count; shape++)
        {
            if (!keep[shape])
            {
                continue;
            }

            MeshShape part = mesh.Shapes[shape];
            int from = Math.Clamp(part.From, 0, mesh.Indices.Length);
            int length = Math.Clamp(part.Count, 0, mesh.Indices.Length - from);
            Array.Copy(mesh.Indices, from, into, at, length);
            for (int one = at; one < at + length; one++)
            {
                Vector3 point = positions[into[one]];
                least = Vector3.Min(least, point);
                most = Vector3.Max(most, point);
            }

            shapes[next] = part with { From = at, Count = length };
            skins[next] = model.Skins[shape];
            modes[next] = model.Modes[shape];
            materials[next] = shape < model.ShapeMaterials.Count ? model.ShapeMaterials[shape] : string.Empty;
            painted[next] = textures[shape];
            at += length;
            next++;
        }

        if (at == 0)
        {
            return model;
        }

        SkinnedMesh unwalled = SkinnedMesh.Of(
            positions, mesh.Normals, at == into.Length ? into : into[..at], least, most, mesh.Coordinates, shapes,
            mesh.Facts, mesh.Colours, mesh.Coloured);

        // A FRESH RECORD RATHER THAN with, because MonsterModel keeps its blends worked out
        // from Modes in a field of its own, and with copies the field along with everything else.
        return new MonsterModel(unwalled, model.Skin, model.Mesh_, model.Material, model.Why, model.Paint)
        {
            Skins = skins,
            Modes = modes,
            Materials = model.Materials,
            BodyLeast = least,
            BodyMost = most,
            NamedInAo = model.NamedInAo,
            Runs = model.Runs,
            Textures = model.Textures,
            Guessed = model.Guessed,
            Move = model.Move,
            Bytes = model.Bytes,
            Files = model.Files,
            Parts = model.Parts,
            Shaders = model.Shaders,
            Kind = model.Kind,
            ShapeMaterials = materials,
            ShapeTextures = painted,
            Walls = count - kept,
            Swapped = model.Swapped,
            Tileset = model.Tileset,
        };
    }

    /// <summary>
    /// The definition that carries the templates, following inheritance - or why there is none.
    /// </summary>
    public static (TileDefinition Definition, string Why) Defined(Func<string, byte[]?> read, string path)
    {
        // IN THE SPELLING THE READ USES, so a loop written with backslashes is caught on its
        // second hop rather than read eight more times before the hop count stops it.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string at = Slashed(path);
        for (var hop = 0; hop <= MostHops; hop++)
        {
            if (!seen.Add(at))
            {
                return (TileDefinition.None, $"the tile inherits itself: {at}");
            }

            TileDefinition one = TileDefinition.Read(read(at));
            if (!one.Ready)
            {
                return (one, $"the tile definition did not read: {at} - {one.Why}");
            }

            if (one.Inherits.Length == 0)
            {
                return (one, string.Empty);
            }

            at = Slashed(one.Inherits);
        }

        return (TileDefinition.None, $"the tile's inheritance runs past {MostHops} files: {path}");
    }

    /// <summary>
    /// The materials of one sub-tile's shapes, in order, from the template's runs.
    /// </summary>
    /// <remarks>
    /// SHAPES THE RUNS DO NOT REACH GET NO MATERIAL rather than the last one: a count that came up
    /// short is something to see, and stretching the last run over it would hide it.
    ///
    /// A TILESET'S SWAP HAPPENS HERE, before anything reads the material, so the dressing, the
    /// graphs and the dump all see the material the area draws - and the tile's own is named only
    /// in the count of what was swapped.
    /// </remarks>
    private static void Runs(
        TileTemplate layout,
        IReadOnlyList<TileRun> runs,
        int shapes,
        List<(string Shape, string Material)> into,
        IReadOnlyDictionary<string, string>? swaps,
        HashSet<string> swapped)
    {
        var given = 0;
        foreach (TileRun run in runs)
        {
            string material = run.Material >= 0 ? layout.Materials[run.Material] : string.Empty;
            if (swaps is not null && material.Length > 0 && swaps.TryGetValue(Slashed(MaterialFile.Bare(material)), out string? swap))
            {
                swapped.Add(material);
                material = swap;
            }

            for (var one = 0; one < run.Shapes && given < shapes; one++)
            {
                into.Add((Name(into.Count), material));
                given++;
            }
        }

        for (; given < shapes; given++)
        {
            into.Add((Name(into.Count), string.Empty));
        }
    }

    /// <summary>The joined mesh with every shape named by its position - what the material list is keyed by.</summary>
    private static SkinnedMesh Numbered(SkinnedMesh mesh)
    {
        if (!mesh.Ready)
        {
            return mesh;
        }

        var shapes = new MeshShape[mesh.Shapes.Count];
        for (var one = 0; one < shapes.Length; one++)
        {
            shapes[one] = mesh.Shapes[one] with { Name = Name(one) };
        }

        return SkinnedMesh.Of(mesh.Positions, mesh.Normals, mesh.Indices, mesh.Least, mesh.Most, mesh.Coordinates, shapes, mesh.Facts, mesh.Colours, mesh.Coloured);
    }

    private static string Name(int shape) => shape.ToString(CultureInfo.InvariantCulture);

    private static string Slashed(string path) => path.Replace('\\', '/').Trim();
}
