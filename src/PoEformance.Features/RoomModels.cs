using System.Globalization;
using System.Numerics;
using PoEformance.Game.Files;

namespace PoEformance.Features;

/// <summary>
/// A room's doodads, each drawn from its own .ao and put where the room file says.
/// </summary>
/// <remarks>
/// THE PART OF A ROOM THAT CAN BE DRAWN EXACTLY. Its tiles are chosen when the area is generated -
/// see RoomLayout - but its doodads are named outright: a model, a position, an angle, a scale. Each
/// model is the same .ao walk a monster or an item takes, read ONCE however many times the room
/// places it, and joined into one mesh at each place.
///
/// A POSITION COUNTS CELLS, 23 to a tile. The file does not say so in words; the reference does in
/// its types: poe_data_tools' <c>arm</c> parser reads a doodad's x and y as <c>u32</c> and a decal's
/// as <c>f32</c>, and a whole number is a place on the grid every .tdt's walkability blocks are
/// written in, not a length. The first real room agreed - read as world units its furniture piled
/// onto a fifth of one tile, read as cells it filled the room. <see cref="Spread"/> still reports how
/// far the doodads reach against the room's size in cells, so a room that breaks the rule says so.
///
/// NOTHING HERE IS ANIMATED - a doodad's rig, where it has one, stays in its rest pose - and there
/// are caps, because a room of three hundred trees is three hundred copies of a tree to rasterise.
/// </remarks>
public static class RoomModels
{
    /// <summary>
    /// Most doodads placed unless the tile book's slider says otherwise. Beyond it the rest are counted and left out.
    /// </summary>
    /// <remarks>
    /// A CHOICE AND NOT A CONSTANT, because what the cap protects - turning the picture without a
    /// stutter - depends on the machine, and what it costs depends on the room: the ship in Port's
    /// boss room places 596 doodads and the old fixed 400 left a third of it out.
    /// </remarks>
    public const int UsualDoodads = 500;

    /// <summary>The slider's ends.</summary>
    public const int LeastDoodads = 100;

    /// <summary>See <see cref="LeastDoodads"/>.</summary>
    public const int MostDoodads = 5000;

    /// <summary>
    /// Triangles allowed per doodad allowed. The renderer is on the processor; past the product, turning stutters.
    /// </summary>
    /// <remarks>
    /// The ratio the two fixed caps had - 400 doodads, 400,000 triangles - kept, so raising the
    /// doodads raises the triangles with them; a cap on one alone would stop the slider short.
    /// </remarks>
    public const int TrianglesPerDoodad = 1000;

    /// <summary>The object a room names beside a doodad the game places and never draws.</summary>
    public const string InvisibleStub = "Metadata/MiscellaneousObjects/DoodadInvisible";

    /// <summary>The folder of the level editor's own pieces - walk blockers, markers, pins.</summary>
    public const string ToolFolder = "Metadata/Terrain/Doodads/Tools/";

    /// <summary>
    /// Whether a doodad is one of the level editor's tools rather than part of the scene.
    /// </summary>
    /// <remarks>
    /// TWO WORDS, ONE THE GAME'S AND ONE A FOLDER'S. A room names an object beside each doodad, and
    /// the yellow box in the deserted 1open_01.arm - Blocker_Walk_3_1_01, its colour the vertex
    /// colours of VertexColourTransparentc.mat at a quarter of their alpha - is placed as
    /// <see cref="InvisibleStub"/>: the game says outright that it is not drawn. Its siblings in
    /// <see cref="ToolFolder"/> are not always placed that way - invisible_blocker.ao as a plain
    /// Doodad, marker4.ao as a BossArenaBlocker, the pins as league markers - but they are the same
    /// kind of thing: walk blockers in a flat vertex colour and markers wearing iconsc.mat, a
    /// camera-facing sheet of editor icons hung 350 units over the pin. That half is a rule on a
    /// folder's name, not the game's word, which is why it is a choice the book offers rather than
    /// a filter it applies.
    /// </remarks>
    public static bool IsTool(RoomDoodad doodad)
        => string.Equals(doodad.Stub, InvisibleStub, StringComparison.OrdinalIgnoreCase)
            || doodad.Ao.StartsWith(ToolFolder, StringComparison.OrdinalIgnoreCase);

    /// <summary>Cells to a tile - the grid a room's doodad positions and a tile's walkability are written in.</summary>
    public const int CellsPerTile = 23;

    /// <summary>World units in one cell.</summary>
    public static float CellSize => TileModels.Side / CellsPerTile;

    /// <summary>
    /// How far a room's doodads reach, and what that says about the unit.
    /// </summary>
    /// <param name="Most">The largest x and y any doodad has.</param>
    /// <param name="Cells">The room's size in cells - 23 to a tile.</param>
    /// <param name="NotCells">True when a doodad lies past the room's edge read as cells - a room the rule does not hold for.</param>
    public readonly record struct Spread(Vector2 Most, Vector2 Cells, bool NotCells)
    {
        /// <summary>The line the view prints.</summary>
        public string Say => string.Create(
                CultureInfo.InvariantCulture,
                $"doodads reach x {Most.X}, y {Most.Y}; the room is {Cells.X} x {Cells.Y} cells")
            + (NotCells ? " - past its edge, so this room's positions are not cells" : string.Empty);
    }

    /// <summary>How far the doodads reach against the room's size. See <see cref="Spread"/>.</summary>
    public static Spread SpreadOf(RoomLayout room)
    {
        ArgumentNullException.ThrowIfNull(room);

        var most = Vector2.Zero;
        foreach (RoomDoodad one in room.Doodads)
        {
            most = Vector2.Max(most, new Vector2(one.X, one.Y));
        }

        var cells = new Vector2(room.Width * CellsPerTile, room.Height * CellsPerTile);
        return new Spread(most, cells, most.X > cells.X || most.Y > cells.Y);
    }

    /// <summary>
    /// The room's doodads as one model, or why there is none. Never throws.
    /// </summary>
    /// <param name="read">How to get a file out of the install, by path.</param>
    /// <param name="path">The room's <c>.arm</c>.</param>
    /// <param name="shaded">Whether each material's shader graphs are read - see MonsterModels.Shaded.</param>
    /// <param name="doodads">Most doodads placed - see <see cref="UsualDoodads"/>; zero or less is the usual.</param>
    /// <param name="tools">Whether the level editor's tools are placed too - see <see cref="IsTool"/>.</param>
    /// <param name="progress">Where the build says how far it has got, or null - see <see cref="ModelProgress"/>.</param>
    /// <param name="heights">How high a doodad whose line carries a height is set - see <see cref="DoodadHeight"/>.</param>
    public static MonsterModel Of(
        Func<string, byte[]?>? read,
        string? path,
        bool shaded = false,
        int doodads = UsualDoodads,
        bool tools = false,
        ModelProgress? progress = null,
        DoodadHeight heights = DoodadHeight.File)
    {
        if (read is null)
        {
            return MonsterModel.None with { Why = "no install to read" };
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return MonsterModel.None with { Why = "no room to read" };
        }

        // COUNTED, as a tile's and a monster's files are: a room is the heaviest thing the book
        // opens - every distinct doodad's .ao walk, materials and textures - and the line under
        // the picture should say so rather than read as nothing.
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

        RoomLayout room = RoomLayout.Read(Counted(path));
        if (!room.Ready)
        {
            return MonsterModel.None with { Why = $"the room did not read: {path} - {room.Why}", Bytes = bytes, Files = files };
        }

        if (room.Doodads.Count == 0)
        {
            return MonsterModel.None with { Why = $"the room places no doodads: {path}", Bytes = bytes, Files = files };
        }

        var paints = new MonsterModels.Paints { Progress = progress };
        var pile = new ModelPile();
        var lights = new RoomLights.Gathered(Counted);
        Doodads laid = Lay(room, Counted, paints, pile, doodads, tools, beyond: null, heights, lights);

        SkinnedMesh joined = SkinnedMesh.Joined(pile.Joins);
        if (!joined.Ready)
        {
            return MonsterModel.None with
            {
                Why = laid.FirstMissing.Length > 0
                    ? $"none of the room's doodads drew; the first: {laid.FirstMissing}"
                    : laid.Hidden > 0 && laid.Placed == 0
                        ? $"the room places only the level editor's tools, which are hidden: {path}"
                        : $"the room's doodads hold no geometry: {path}",
                Bytes = bytes,
                Files = files,
            };
        }

        var said = new List<string> { SpreadOf(room).Say };
        said.AddRange(laid.Said());
        MonsterModel model = Piled(joined, pile, path, laid.Models) with
        {
            Parts = laid.Placed,
            Bytes = bytes,
            Files = files,
            Move = string.Join("; ", said),
            Lights = lights.Lights,
            LightsSaid = lights.Said(),
        };

        // THE GRAPHS ONCE, OVER THE JOINED ROOM: every doodad's materials are in the one list, and a
        // material two doodads share is compiled once through the shared cache.
        return shaded ? MonsterModels.Shaded(Counted, model, paints) : model;
    }

    /// <summary>What laying a room's doodads came to.</summary>
    internal sealed record Doodads(
        int Placed, int Missing, int Capped, int Hidden, string FirstMissing, int Most, long MostTriangles, IReadOnlyCollection<MonsterModel> Models)
    {
        /// <summary>The lines under the picture about what did not draw, and why.</summary>
        public IEnumerable<string> Said()
        {
            if (Missing > 0)
            {
                yield return string.Create(CultureInfo.InvariantCulture, $"{Missing} doodads did not draw; the first: {FirstMissing}");
            }

            if (Hidden > 0)
            {
                yield return string.Create(CultureInfo.InvariantCulture,
                    $"{Hidden} of the level editor's tools hidden - walk blockers and markers the game does not draw; \"tools\" shows them");
            }

            if (Capped > 0)
            {
                yield return string.Create(CultureInfo.InvariantCulture,
                    $"{Capped} more left out to keep it turnable - {Most} doodads or {MostTriangles} triangles; the doodads slider raises it");
            }
        }
    }

    /// <summary>
    /// A room's doodads laid into a pile, each where the room puts it and then by <paramref name="beyond"/> - the room as laid in an area - or nowhere further.
    /// </summary>
    /// <remarks>
    /// ONE LOAD PER MODEL, keyed by its file: a room places the same tree or rock dozens of times. AND
    /// ONE TEXTURE CACHE FOR ALL OF THEM, because two different doodads sharing a material - the rock
    /// and the stump on one sheet - are one decode and one upload, not one each.
    /// </remarks>
    internal static Doodads Lay(
        RoomLayout room,
        Func<string, byte[]?> counted,
        MonsterModels.Paints paints,
        ModelPile pile,
        int doodads,
        bool tools,
        Func<RoomDoodad, Matrix4x4>? beyond,
        DoodadHeight heights = DoodadHeight.File,
        RoomLights.Gathered? lights = null)
    {
        var models = new Dictionary<string, MonsterModel>(StringComparer.OrdinalIgnoreCase);
        float size = CellSize;
        long triangles = 0;
        int placed = 0, missing = 0, capped = 0, hidden = 0;
        int mostDoodads = doodads > 0 ? doodads : UsualDoodads;
        long mostTriangles = (long)mostDoodads * TrianglesPerDoodad;
        string firstMissing = string.Empty;

        using ModelProgress.Step step = ModelProgress.Begin(paints.Progress, "laying the doodads", room.Doodads.Count);
        foreach (RoomDoodad one in room.Doodads)
        {
            step.Advance();
            if (one.Ao.Length == 0)
            {
                continue;
            }

            if (!tools && IsTool(one))
            {
                hidden++;
                continue;
            }

            float scale = one.Scale > 0f ? one.Scale : 1f;
            Matrix4x4 place = Matrix4x4.CreateScale(scale)
                * Matrix4x4.CreateRotationZ(one.Turn)
                * Matrix4x4.CreateTranslation(one.X * size, one.Y * size, 0f);
            if (beyond is not null)
            {
                // THE ROOM AS LAID SETS ITS OWN HEIGHT, from the area's ground under the doodad.
                place *= beyond(one);
            }
            else if (heights != DoodadHeight.Ground && one.Height is { } height)
            {
                // ON ITS OWN THE ROOM'S GROUND IS NOUGHT, so the height and the ground plus it are one.
                place *= Matrix4x4.CreateTranslation(0f, 0f, height);
            }

            // THE LIGHTS WHETHER OR NOT THE DOODAD DRAWS: a light can be all a doodad is -
            // grimtangle_ambientlight.ao has no mesh - and a doodad left out to keep the picture
            // turnable still lights the room in the game.
            lights?.Add(one.Ao, place);

            if (!models.TryGetValue(one.Ao, out MonsterModel? model))
            {
                model = MonsterModels.OfFiles(counted, [one.Ao], wearing: true, paints);
                models[one.Ao] = model;
            }

            if (!model.Ready)
            {
                missing++;
                if (firstMissing.Length == 0)
                {
                    firstMissing = $"{one.Ao} - {model.Why}";
                }

                continue;
            }

            if (placed >= mostDoodads || triangles + model.Mesh.Triangles > mostTriangles)
            {
                capped++;
                continue;
            }

            pile.Add(model, place, one.Ao);
            triangles += model.Mesh.Triangles;
            placed++;
        }

        return new Doodads(placed, missing, capped, hidden, firstMissing, mostDoodads, mostTriangles, models.Values);
    }

    /// <summary>A pile joined into a room's model, with the shaders and meshes of the models in it.</summary>
    internal static MonsterModel Piled(SkinnedMesh joined, ModelPile pile, string path, IEnumerable<MonsterModel> models)
    {
        MonsterModel[] all = [.. models];
        return new MonsterModel(joined, pile.Skins.FirstOrDefault(one => one is not null), path, string.Empty, string.Empty)
        {
            Skins = pile.Skins,
            Modes = pile.Modes,
            ShapeMaterials = pile.Wearing,

            // CARRIED LIKE THE MATERIALS, so the dump's "tex" column names each doodad's colour map;
            // left out, every room shape read "tex -" whether it was textured or not.
            ShapeTextures = pile.Textures,
            ShapeSources = pile.Sources,
            BodyLeast = joined.Least,
            BodyMost = joined.Most,
            Kind = ModelKind.Room,
            Shaders = [.. all.SelectMany(one => one.Shaders).Distinct(StringComparer.OrdinalIgnoreCase)],
            Meshes = [.. all.Where(one => one.Ready).Select(one => new MeshNamed(one.Mesh_, one.BodyFacts)).Distinct()],
        };
    }
}
