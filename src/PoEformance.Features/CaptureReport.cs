using System.Globalization;
using System.IO.Compression;
using System.Text;
using PoEformance.Game.Files;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Features;

/// <summary>A room laid near the player, and how many tiles off it is - nought for the room the player stands in.</summary>
/// <param name="Laid">The room and where it was laid.</param>
/// <param name="Tiles">Tiles from the player's tile to the nearest the room covers, counted the long way round a diagonal.</param>
public sealed record RoomNearby(RoomLaid Laid, int Tiles);

/// <summary>
/// The text half of the capture key: what one press writes into its folder beside the two pictures and the recording.
/// </summary>
/// <remarks>
/// WHAT A CAPTURE IS FOR. The lighting work - and most work on this tool - needs the same handful
/// of things from one spot in the game: a picture of it, the files the rooms there are built from,
/// and what memory held. Asked for one at a time they arrive from different spots and different
/// builds; one key takes them together, from one place, at one moment.
///
/// THE ROOMS ARE THE ONES ON SCREEN, give or take: every room laid within
/// <see cref="RoomReach"/> tiles of the player's tile. A tile is 250 world units and the camera
/// sees a few of them either side, so that is what the pictures show - and each room's lights
/// are written so a lamp in the picture can be found in a file.
/// </remarks>
public static class CaptureReport
{
    /// <summary>Tiles either side of the player's own a room may be and still be written.</summary>
    public const int RoomReach = 3;

    /// <summary>Ticks of the reader the memory recording runs for at least - three seconds at its thirty a second; longer while the pass inside it runs (CaptureMemory).</summary>
    public const int MemoryFrames = 90;

    /// <summary>The game alone, the overlay hidden for the shot.</summary>
    public const string GameShot = "game.png";

    /// <summary>The game with the overlay over it, as it was when the key went down.</summary>
    public const string OverlayShot = "overlay.png";

    /// <summary>What was captured, where, and what could not be.</summary>
    public const string SummaryFile = "capture.txt";

    /// <summary>Every entity the snapshot held, nearest first.</summary>
    public const string EntitiesFile = "entities.txt";

    /// <summary>The files the area loaded.</summary>
    public const string LoadedFile = "loaded-files.txt";

    /// <summary>The environments of the rooms around the player, whole.</summary>
    public const string EnvironmentsFile = "environments.txt";

    /// <summary>The rooms around the player, each with its doodads' lights.</summary>
    public const string RoomsFile = "rooms.txt";

    /// <summary>The overlay's own reads for a few seconds and one pass reading everything again - see CaptureMemory. Replayable with --replay.</summary>
    public const string MemoryFile = "memory.rec";

    /// <summary>Where captures go: beside the tool, like its exports.</summary>
    public static string Root => Path.Combine(AppContext.BaseDirectory, "captures");

    /// <summary>A capture's folder name: when, and where - sorting by name sorts by time.</summary>
    public static string FolderName(DateTime local, AreaInfo area)
    {
        string where = Clean(area.Name.Length > 0 ? area.Name : area.Id);
        string when = local.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);
        return where.Length > 0 ? $"{when} {where}" : when;
    }

    /// <summary>
    /// A name made safe for a folder on Windows, wherever this runs.
    /// </summary>
    /// <remarks>
    /// The set is written out rather than asked of Path.GetInvalidFileNameChars, which answers for
    /// the machine running it: on Linux that is the slash and nothing else, and the tests run there.
    /// </remarks>
    public static string Clean(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var cleaned = new StringBuilder(name.Length);
        foreach (char one in name)
        {
            cleaned.Append(one is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' || char.IsControl(one) ? '_' : one);
        }

        return cleaned.ToString().Trim().TrimEnd('.');
    }

    /// <summary>The grid cell and tile a world position falls on.</summary>
    public static (int CellX, int CellY, int TileX, int TileY) Where(float worldX, float worldY)
    {
        int cellX = (int)MathF.Floor(worldX / MapView.WorldToGrid);
        int cellY = (int)MathF.Floor(worldY / MapView.WorldToGrid);
        return (cellX, cellY, FloorDiv(cellX, TerrainGrid.CellsPerTile), FloorDiv(cellY, TerrainGrid.CellsPerTile));
    }

    private static int FloorDiv(int value, int by) => (value - (((value % by) + by) % by)) / by;

    /// <summary>
    /// Every room laid within <paramref name="reach"/> tiles of a tile, nearest first - the rooms the player stands in at nought.
    /// </summary>
    /// <remarks>
    /// By the tiles each room COVERS (RoomArrangement.Tiles), not its footprint's rectangle: an
    /// L-shaped room's notch is somebody else's floor, and standing there is not standing in it.
    /// Two rooms at nought is an ordinary answer where they share a rim.
    /// </remarks>
    public static List<RoomNearby> RoomsNear(RoomArrangement arranged, int tileX, int tileY, int reach = RoomReach)
    {
        ArgumentNullException.ThrowIfNull(arranged);
        var near = new List<RoomNearby>();
        foreach (RoomLaid laid in arranged.Laid)
        {
            // The footprint first: a room whose rectangle is out of reach covers nothing in it.
            RoomCandidate where = laid.Where;
            if (Gap(tileX, where.X, where.X + where.Width - 1) > reach || Gap(tileY, where.Y, where.Y + where.Height - 1) > reach)
            {
                continue;
            }

            int nearest = int.MaxValue;
            foreach (int tile in RoomArrangement.Tiles(laid.Layout, where, arranged.TilesX, arranged.TilesY))
            {
                int off = Math.Max(Math.Abs((tile % arranged.TilesX) - tileX), Math.Abs((tile / arranged.TilesX) - tileY));
                nearest = Math.Min(nearest, off);
            }

            if (nearest <= reach)
            {
                near.Add(new RoomNearby(laid, nearest));
            }
        }

        near.Sort((a, b) => a.Tiles != b.Tiles ? a.Tiles.CompareTo(b.Tiles) : a.Laid.Rank.CompareTo(b.Laid.Rank));
        return near;
    }

    private static int Gap(int at, int from, int to) => at < from ? from - at : at > to ? at - to : 0;

    /// <summary>
    /// The summary: when, which build, where the player stood, the camera, and a line for every part of the capture saying what it holds or why it does not.
    /// </summary>
    /// <param name="snapshot">The world as it was when the key went down.</param>
    /// <param name="local">When the key went down.</param>
    /// <param name="version">The version and build, as the overlay's title bar says them - see ToolVersion.With.</param>
    /// <param name="client">The game's client area on the screen - what the pictures cover.</param>
    /// <param name="rooms">The rooms around the player, or null where none were arranged.</param>
    /// <param name="parts">One line per part of the capture.</param>
    public static string Summary(
        WorldSnapshot snapshot,
        DateTime local,
        string version,
        (int X, int Y, int Width, int Height) client,
        IReadOnlyList<RoomNearby>? rooms,
        IEnumerable<string> parts)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(parts);
        var said = new StringBuilder();
        said.Append("capture  ").Append(local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
            .Append(" local, PoEformance ").AppendLine(string.IsNullOrEmpty(version) ? ToolVersion.Said : version);
        AreaInfo area = snapshot.Area;
        said.Append("area     ").Append(area.Name.Length > 0 ? area.Name : "(unnamed)").Append(" (").Append(area.Id).Append(')')
            .Append(", act ").Append(Say(area.Act)).Append(", level ").Append(Say(snapshot.AreaLevel))
            .Append(", hash 0x").Append(snapshot.AreaHash.ToString("X8", CultureInfo.InvariantCulture))
            .Append(", state ").AppendLine(snapshot.State.ToString());
        said.Append("light    ").AppendLine(area.Environment.Length > 0 ? area.Environment : "(the area's environment did not resolve)");
        said.Append("screen   game client at ").Append(Say(client.X)).Append(", ").Append(Say(client.Y))
            .Append(", ").Append(Say(client.Width)).Append(" x ").AppendLine(Say(client.Height));

        if (snapshot.Player is { } player)
        {
            (int cellX, int cellY, int tileX, int tileY) = Where(player.WorldX, player.WorldY);
            said.Append("player   world ").Append(Num(player.WorldX)).Append(' ').Append(Num(player.WorldY)).Append(' ').Append(Num(player.WorldZ))
                .Append(", terrain height ").Append(Num(player.TerrainHeight))
                .Append(", level ").AppendLine(Say(snapshot.PlayerLevel));
            said.Append("         grid cell ").Append(Say(cellX)).Append(", ").Append(Say(cellY))
                .Append(", tile ").Append(Say(tileX)).Append(", ").AppendLine(Say(tileY));

            if (snapshot.Terrain is TerrainGrid grid)
            {
                said.Append("         the grid's height there ").Append(Num(grid.HeightAt(cellX, cellY)))
                    .Append(", the tile's level ").AppendLine(Num(grid.LevelAt(tileX, tileY)));
                if (grid.Tiles is { } tiles)
                {
                    (int subX, int subY) = tiles.SubAt(tileX, tileY);
                    string path = tiles.PathAt(tileX, tileY);
                    said.Append("tile     ").Append(path.Length > 0 ? path : "(none named)")
                        .Append(", laid ").Append(Say(tiles.PlacementAt(tileX, tileY)))
                        .Append(", piece ").Append(Say(subX)).Append(", ").AppendLine(Say(subY));
                }
            }
            else
            {
                said.AppendLine("tile     (no terrain read)");
            }
        }
        else
        {
            said.AppendLine("player   (not read)");
        }

        said.Append("camera  ");
        foreach (float one in snapshot.Matrix)
        {
            said.Append(' ').Append(Num(one));
        }

        said.AppendLine();
        said.AppendLine();
        said.AppendLine("=== rooms around the player");
        if (rooms is null)
        {
            said.AppendLine("(none arranged)");
        }
        else if (rooms.Count == 0)
        {
            said.Append("(none laid within ").Append(Say(RoomReach)).AppendLine(" tiles)");
        }
        else
        {
            foreach (RoomNearby room in rooms)
            {
                said.Append("  ").Append(Room(room)).AppendLine();
            }
        }

        said.AppendLine();
        said.AppendLine("=== what this capture holds");
        foreach (string part in parts)
        {
            said.Append("  ").AppendLine(part);
        }

        return said.ToString();
    }

    /// <summary>One room around the player, in a line.</summary>
    public static string Room(RoomNearby room)
    {
        ArgumentNullException.ThrowIfNull(room);
        RoomCandidate where = room.Laid.Where;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(room.Tiles == 0 ? "here " : $"{room.Tiles} off")}  {TerrainRooms.NameFor(room.Laid.Room)}  at tile {where.X}, {where.Y}, {where.Width} x {where.Height}, turn {where.Turn}, rank {room.Laid.Rank}, {room.Laid.Beside}% beside  ({room.Laid.Room})");
    }

    /// <summary>
    /// Every entity in the snapshot, nearest the player first: where it stands, what it is, and where in memory it was.
    /// </summary>
    /// <remarks>
    /// THE MEMORY SIDE OF A ROOM'S FILES. A doodad that is also an entity - a pot, a lamp, a door -
    /// stands at the height the game gave it, which is what the .arm's float was settled against;
    /// a light in a doodad's rig and a lamp entity at the same spot are the same lamp.
    /// </remarks>
    public static string Entities(WorldSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        float x = snapshot.Player?.WorldX ?? 0f;
        float y = snapshot.Player?.WorldY ?? 0f;
        var said = new StringBuilder();
        said.Append(Say(snapshot.Entities.Count)).Append(" entities, ").Append(Say(snapshot.Remembered))
            .AppendLine(" of them remembered rather than listed this read; distance and position in world units");
        said.AppendLine("distance\tkind\tid\taddress\tx\ty\tz\tterrain\tbounds z\tremembered\tpath\tname");
        foreach (WorldEntity one in snapshot.Entities.OrderBy(one => Distance(one, x, y)))
        {
            said.Append(Num(Distance(one, x, y))).Append('\t').Append(one.Kind).Append('\t').Append(one.Id.ToString(CultureInfo.InvariantCulture))
                .Append("\t0x").Append(one.Address.ToString("X", CultureInfo.InvariantCulture))
                .Append('\t').Append(Num(one.WorldX)).Append('\t').Append(Num(one.WorldY)).Append('\t').Append(Num(one.WorldZ))
                .Append('\t').Append(Num(one.TerrainHeight)).Append('\t').Append(Num(one.ModelBoundsZ))
                .Append('\t').Append(one.IsRemembered ? "yes" : "no")
                .Append('\t').Append(one.Path).Append('\t').AppendLine(one.Name);
        }

        return said.ToString();
    }

    private static float Distance(WorldEntity one, float x, float y) => MathF.Sqrt(((one.WorldX - x) * (one.WorldX - x)) + ((one.WorldY - y) * (one.WorldY - y)));

    /// <summary>
    /// The rooms around the player, each with its place and its doodads' lights - and, for the rooms the player stands in, the room's file whole, which says where each doodad stands.
    /// </summary>
    /// <param name="read">How to get a file out of the install, by path.</param>
    /// <param name="rooms">The rooms, nearest first.</param>
    public static string Rooms(Func<string, byte[]?> read, IReadOnlyList<RoomNearby> rooms)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(rooms);
        var said = new StringBuilder();
        foreach (RoomNearby room in rooms)
        {
            said.Append("##### ").AppendLine(Room(room));
            said.Append(ModelDump.DoodadLights(read, room.Laid.Room));
            if (room.Tiles == 0)
            {
                said.Append("=== ").Append(room.Laid.Room).AppendLine(" as the file has it");
                byte[]? file = read(room.Laid.Room);
                said.AppendLine(file is { Length: > 0 } ? StatDescriptionFiles.Decode(file).TrimEnd() : "(not in the install)");
            }

            said.AppendLine();
        }

        return said.ToString();
    }

    /// <summary>
    /// Every file of a capture packed into one zip beside its folder, named like it - what gets sent. Returns the zip's path and the files left out.
    /// </summary>
    /// <remarks>
    /// ASKED FOR because a capture is nine files and the place they are sent to takes five.
    ///
    /// THE PICTURES AND THE RECORDING ARE STORED, NOT COMPRESSED: a PNG and a Brotli stream are
    /// compressed already, and squeezing them again is seconds of work for nothing. The text files
    /// are compressed, and they are where it pays - the loaded-file list shrinks to a tenth.
    ///
    /// A file that cannot be opened is left out and named, not allowed to sink the rest: the only
    /// one that can be is a recording still being written, and the folder keeps it either way.
    /// </remarks>
    /// <param name="folder">The capture's folder.</param>
    public static (string Zip, List<string> LeftOut) Pack(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        string zip = Path.TrimEndingDirectorySeparator(folder) + ".zip";
        var leftOut = new List<string>();
        using FileStream file = File.Create(zip);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (string path in Directory.EnumerateFiles(folder).Order(StringComparer.OrdinalIgnoreCase))
        {
            string name = Path.GetFileName(path);
            bool packed = name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".rec", StringComparison.OrdinalIgnoreCase);
            try
            {
                archive.CreateEntryFromFile(path, name, packed ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
            }
            catch (IOException)
            {
                leftOut.Add(name);
            }
        }

        return (zip, leftOut);
    }

    /// <summary>The area's loaded files, one to a line, as the game listed them.</summary>
    public static string Loaded(IReadOnlyList<string> loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        var said = new StringBuilder();
        said.Append(Say(loaded.Count)).AppendLine(" files the area loaded");
        foreach (string one in loaded)
        {
            said.AppendLine(one);
        }

        return said.ToString();
    }

    private static string Say(int number) => number.ToString(CultureInfo.InvariantCulture);

    private static string Num(float number) => number.ToString("0.###", CultureInfo.InvariantCulture);
}
