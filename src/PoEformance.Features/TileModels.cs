using System.Globalization;
using System.Numerics;
using PoEformance.Game.Files;

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

    /// <summary>Most sub-tiles read for one tile - a guard against a size read from the wrong place.</summary>
    public const int MostSubTiles = 256;

    /// <summary>
    /// Gathers the model for one tile, or says where the walk stopped. Never throws.
    /// </summary>
    /// <param name="read">How to get a file out of the install, by path.</param>
    /// <param name="path">The tile's <c>.tdt</c>, as the game or the install names it.</param>
    /// <param name="ground">
    /// Whether the ground block is drawn under the props. It is unpainted - see the remarks - and
    /// where it rises around a prop it hides the part of the tile somebody opened it to look at.
    /// </param>
    public static MonsterModel Of(Func<string, byte[]?>? read, string? path, bool ground = true)
    {
        if (read is null)
        {
            return MonsterModel.None with { Why = "no install to read" };
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return MonsterModel.None with { Why = "no tile to read" };
        }

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
            return MonsterModel.None with
            {
                Why = why.Length > 0 ? why : $"the tile names no template: {path}",
                Bytes = bytes,
                Files = files,
            };
        }

        var joins = new List<MeshJoin>();
        var grounds = new List<MeshJoin>();
        var named = new List<(string Shape, string Material)>();
        var said = new List<string>();
        int inexact = 0;
        int subTiles = 0;

        foreach (string template in definition.Templates)
        {
            TileTemplate layout = TileTemplate.Read(Counted(template));
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

            for (var y = 1; y <= layout.Height; y++)
            {
                for (var x = 1; x <= layout.Width; x++)
                {
                    string mesh = layout.MeshOf(x, y);
                    TileMesh part = TileMesh.Read(Counted(mesh));
                    if (!part.Ready)
                    {
                        said.Add($"the sub-tile did not read: {mesh} - {part.Why}");
                        continue;
                    }

                    subTiles++;
                    inexact += part.Exact ? 0 : 1;
                    Matrix4x4 place = Matrix4x4.CreateTranslation((x - 1) * Side, -(y - 1) * Side, 0f);

                    if (part.Props.Ready)
                    {
                        joins.Add(new MeshJoin(part.Props, null, null, place));
                        Runs(layout, layout.RunsOf(x, y), part.Props.Shapes.Count, named);
                    }

                    if (ground && part.Ground.Ready)
                    {
                        grounds.Add(new MeshJoin(part.Ground, null, null, place));
                    }
                }
            }
        }

        // THE GROUND AFTER THE PROPS, so the props' shapes keep the numbers the runs gave them and
        // every ground shape comes after - unnamed, and so unpainted.
        joins.AddRange(grounds);
        SkinnedMesh joined = Numbered(SkinnedMesh.Joined(joins));
        if (!joined.Ready)
        {
            return MonsterModel.None with
            {
                Why = said.Count > 0 ? said[0] : $"the tile's sub-tiles hold no geometry: {path}",
                Bytes = bytes,
                Files = files,
            };
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
        return MonsterModels.Worn(Counted, joined, named, paintTheRest: false, path, move) with
        {
            Bytes = bytes,
            Files = files,
            Parts = subTiles,
            Kind = ModelKind.Tile,
        };
    }

    /// <summary>
    /// The definition that carries the templates, following inheritance - or why there is none.
    /// </summary>
    private static (TileDefinition Definition, string Why) Defined(Func<string, byte[]?> read, string path)
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
    /// </remarks>
    private static void Runs(
        TileTemplate layout, IReadOnlyList<TileRun> runs, int shapes, List<(string Shape, string Material)> into)
    {
        var given = 0;
        foreach (TileRun run in runs)
        {
            string material = run.Material >= 0 ? layout.Materials[run.Material] : string.Empty;
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

        return SkinnedMesh.Of(mesh.Positions, mesh.Normals, mesh.Indices, mesh.Least, mesh.Most, mesh.Coordinates, shapes);
    }

    private static string Name(int shape) => shape.ToString(CultureInfo.InvariantCulture);

    private static string Slashed(string path) => path.Replace('\\', '/').Trim();
}
