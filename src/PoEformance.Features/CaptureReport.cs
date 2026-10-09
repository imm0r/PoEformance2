using System.Globalization;
using System.IO.Compression;
using System.Text;
using PoEformance.Game.Files;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Features;

/// <summary>A room standing near the player, and how many tiles off it is - nought for the room the player stands in.</summary>
/// <param name="Room">The room's file.</param>
/// <param name="Layout">The room as read, for the tiles it covers.</param>
/// <param name="Where">Where it stands.</param>
/// <param name="Tiles">Tiles from the player's tile to the nearest the room covers, counted the long way round a diagonal.</param>
public sealed record RoomNearby(string Room, RoomLayout Layout, RoomCandidate Where, int Tiles);

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

    /// <summary>The overlay's own reads for a few seconds and one pass reading what the ticked parts ask for - see CaptureMemory. Replayable with --replay.</summary>
    public const string MemoryFile = "memory.rec";

    /// <summary>Every room arranged by its ground and tiles - the Tile Book's line and hover.</summary>
    public const string ArrangedFile = "rooms-arranged.txt";

    /// <summary>The Tile Book's picked row against the rooms on the map, and where it parts with the area.</summary>
    public const string PickFile = "room-pick.txt";

    /// <summary>Every room file the area loaded, whole.</summary>
    public const string RoomFilesFile = "room-files.txt";

    /// <summary>The survey of the entity maps for the rooms' doodads, and every sighting.</summary>
    public const string DoodadsFile = "doodads.txt";

    /// <summary>Every room placed by its doodads - the Tile Book's line and hover.</summary>
    public const string PlacedFile = "rooms-placed.txt";

    /// <summary>The active routes, as the Routes page lists them.</summary>
    public const string RoutesFile = "routes.txt";

    /// <summary>The light hunt's last report.</summary>
    public const string SceneLightFile = "scene-light.txt";

    /// <summary>The model panes' probe lines.</summary>
    public const string ProbeFile = "model-probe.txt";

    /// <summary>The Memory Dissector's places, route and last search.</summary>
    public const string DissectorFile = "dissector.txt";

    /// <summary>The atlas check's report.</summary>
    public const string AtlasLogFile = "atlas-log.txt";

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
    /// Every room standing within <paramref name="reach"/> tiles of a tile, nearest first - the rooms the player stands in at nought.
    /// </summary>
    /// <remarks>
    /// By the tiles each room COVERS (RoomArrangement.Tiles), not its footprint's rectangle: an
    /// L-shaped room's notch is somebody else's floor, and standing there is not standing in it.
    /// Two rooms at nought is an ordinary answer where they share a rim, or where one stands inside
    /// another - The Assembly's Expedition encounter inside a workshop.
    /// </remarks>
    /// <param name="standing">Every room with where it stands - every place of a room laid more than once. See AreaRooms.Standing.</param>
    /// <param name="tilesX">The area's tiles across.</param>
    /// <param name="tilesY">The area's tiles down.</param>
    /// <param name="tileX">The player's tile.</param>
    /// <param name="tileY">The player's tile.</param>
    /// <param name="reach">Tiles either side of the player's own a room may be and still be listed.</param>
    public static List<RoomNearby> RoomsNear(
        IReadOnlyList<(string Room, RoomLayout Layout, RoomCandidate Where)> standing, int tilesX, int tilesY, int tileX, int tileY, int reach = RoomReach)
    {
        ArgumentNullException.ThrowIfNull(standing);
        var near = new List<RoomNearby>();
        foreach ((string room, RoomLayout layout, RoomCandidate where) in standing)
        {
            // The footprint first: a room whose rectangle is out of reach covers nothing in it.
            if (Gap(tileX, where.X, where.X + where.Width - 1) > reach || Gap(tileY, where.Y, where.Y + where.Height - 1) > reach)
            {
                continue;
            }

            int nearest = int.MaxValue;
            foreach (int tile in RoomArrangement.Tiles(layout, where, tilesX, tilesY))
            {
                int off = Math.Max(Math.Abs((tile % tilesX) - tileX), Math.Abs((tile / tilesX) - tileY));
                nearest = Math.Min(nearest, off);
            }

            if (nearest <= reach)
            {
                near.Add(new RoomNearby(room, layout, where, nearest));
            }
        }

        near.Sort((a, b) =>
        {
            int order = a.Tiles.CompareTo(b.Tiles);
            order = order != 0 ? order : string.CompareOrdinal(a.Room, b.Room);
            order = order != 0 ? order : a.Where.Y.CompareTo(b.Where.Y);
            return order != 0 ? order : a.Where.X.CompareTo(b.Where.X);
        });
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
        if (snapshot.Terrain is TerrainGrid terrain)
        {
            // The grid's size, so a placing can be run again from the capture on the same grid.
            said.Append("terrain  ").Append(Say(terrain.Width)).Append(" x ").Append(Say(terrain.Height)).Append(" cells, ")
                .Append(Say(terrain.TilesX)).Append(" x ").Append(Say(terrain.TilesY)).AppendLine(" tiles");
        }

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
        RoomCandidate where = room.Where;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(room.Tiles == 0 ? "here " : $"{room.Tiles} off")}  {TerrainRooms.NameFor(room.Room)}  at tile {where.X}, {where.Y}, {where.Width} x {where.Height}, {RoomFinder.Said(where.Turn)}  ({room.Room})");
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
            said.Append(ModelDump.DoodadLights(read, room.Room));
            if (room.Tiles == 0)
            {
                said.Append("=== ").Append(room.Room).AppendLine(" as the file has it");
                byte[]? file = read(room.Room);
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
    /// ASKED FOR because a capture is a dozen files or more and the place they are sent to takes five.
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

    /// <summary>
    /// Every room arranged by its ground and tiles, in a line and its detail: where each was laid, what disagrees there, how many of its tiles can be walked, and the rooms crowded out or not found.
    /// </summary>
    /// <remarks>
    /// THE TILE BOOK'S LINE AND ITS HOVER, built here so the capture writes the very text the window
    /// shows: a report that differs from the screen by a word is a report somebody has to reconcile.
    /// </remarks>
    public static (string Said, string Detail) Arranged(RoomArrangement arranged)
    {
        ArgumentNullException.ThrowIfNull(arranged);
        int moved = arranged.Laid.Count(one => one.Rank > 0);
        string said = string.Create(CultureInfo.InvariantCulture, $"rooms: {arranged.Laid.Count} drawn")
            + (moved > 0 ? string.Create(CultureInfo.InvariantCulture, $", {moved} off their first place") : string.Empty)
            + (arranged.Crowded.Count > 0 ? string.Create(CultureInfo.InvariantCulture, $", {arranged.Crowded.Count} with no free place") : string.Empty)
            + (arranged.Unfound.Count > 0 ? string.Create(CultureInfo.InvariantCulture, $", {arranged.Unfound.Count} not found") : string.Empty);
        var lines = new List<string>(arranged.Laid.Count + arranged.Crowded.Count + arranged.Unfound.Count);
        foreach (RoomLaid one in arranged.Laid)
        {
            // HOW MANY OF ITS TILES CAN BE WALKED, because the map draws the void and unexplored
            // ground the same black: a room outlined in the black stands on ground or it does not,
            // and this is where that is read rather than argued. See RoomLaid.Standing.
            RoomMisses misses = one.Misses;
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"{TerrainRooms.NameFor(one.Room)}: tile {one.Where.X}, {one.Where.Y}, {RoomFinder.Said(one.Where.Turn)}, {one.Beside}% beside the joins")
                + string.Create(CultureInfo.InvariantCulture,
                    $" - {misses.Corners.Count} corners and {misses.Tiles.Count} tiles disagree, {misses.Corners.Count + misses.Tiles.Count - misses.Elsewhere} of those at a join")
                + (one.Standing is { } standing
                    ? string.Create(CultureInfo.InvariantCulture, $" - stands on {standing} of its {one.Covers} tiles")
                    : string.Create(CultureInfo.InvariantCulture, $" - {one.Covers} tiles, walkable ground not read"))
                + (one.Rank > 0 ? string.Create(CultureInfo.InvariantCulture, $" - row {one.Rank + 1} of its list, the ones above it taken") : string.Empty));
        }

        lines.AddRange(arranged.Crowded.Select(one => TerrainRooms.NameFor(one) + ": every place on its list covers a tile a surer room holds"));
        lines.AddRange(arranged.Unfound.Select(one => TerrainRooms.NameFor(one) + ": its search found nothing"));
        return (said, string.Join('\n', lines));
    }

    /// <summary>
    /// Every room placed by its doodads, in a line and its detail: each room's places with their hits, lines, offset and tiles agreeing, what it yielded to whom, or why it has none - see RoomDoodadFinder.
    /// </summary>
    public static (string Said, string Detail) Placed(IReadOnlyList<(string Room, RoomLayout Layout, RoomDoodadPlaces Places)> placed)
    {
        ArgumentNullException.ThrowIfNull(placed);
        int places = 0, unplaced = 0;
        var lines = new List<string>(placed.Count);
        foreach ((string room, _, RoomDoodadPlaces found) in placed)
        {
            places += found.Places.Count;
            unplaced += found.Places.Count == 0 ? 1 : 0;
            string name = TerrainRooms.NameFor(room);
            var said = new List<string>(found.Places.Count + found.Yielded.Count);
            foreach (RoomDoodadPlace place in found.Places)
            {
                said.Add(string.Create(CultureInfo.InvariantCulture,
                    $"tile {place.Where.X}, {place.Where.Y}, {RoomFinder.Said(place.Where.Turn)}: {place.Hits} of {place.Lines} doodads within a tile ({place.PropHits} of {place.Props} props), off {place.MeanOff:0} on average")
                    + (place.TilesAgree >= 0 ? string.Create(CultureInfo.InvariantCulture, $", {place.TilesAgree} tiles agree") : ", not on the tile search's list"));
            }

            // WHAT IT GAVE UP, AND TO WHOM: a variant not laid votes for the laid one's tile - see RoomDoodadFinder.Settle.
            foreach ((RoomDoodadPlace place, string to) in found.Yielded)
            {
                said.Add(string.Create(CultureInfo.InvariantCulture,
                    $"yielded tile {place.Where.X}, {place.Where.Y}, {RoomFinder.Said(place.Where.Turn)} ({place.Hits} of {place.Lines}) to {TerrainRooms.NameFor(to)}"));
            }

            if (found.Places.Count == 0)
            {
                lines.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{name}: no place - {found.Why}; {found.Matchable} of its {found.Lines} doodad lines stand in the area, {found.Props} of them props, {found.ByModel} told by their model")
                    + (said.Count > 0 ? " - " + string.Join("; ", said) : string.Empty));
                continue;
            }

            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"{name}: {found.Places.Count} place{(found.Places.Count == 1 ? string.Empty : "s")}, {found.ByModel} of {found.Matchable} doodads told by their model - ")
                + string.Join("; ", said));
        }

        string line = string.Create(CultureInfo.InvariantCulture, $"rooms: {placed.Count - unplaced} placed by their doodads at {places} places")
            + (unplaced > 0 ? string.Create(CultureInfo.InvariantCulture, $", {unplaced} with no place") : string.Empty);
        return (line, string.Join('\n', lines));
    }

    /// <summary>
    /// A survey of the entity maps in a line and its detail: the three numbers, then each room's doodad lines against what was found, each path found and how many stand, and the paths found nowhere - see SleepingDoodads.
    /// </summary>
    /// <param name="done">The survey.</param>
    /// <param name="rooms">The rooms with their files read, or null while they are not.</param>
    public static (string Said, string Detail) Doodads(DoodadSurvey done, IReadOnlyList<(string Room, RoomLayout Layout)>? rooms)
    {
        ArgumentNullException.ThrowIfNull(done);
        if (done.Why.Length > 0)
        {
            return ("doodads: " + done.Why, string.Empty);
        }

        var byPath = new Dictionary<string, (int All, int Asleep)>(StringComparer.OrdinalIgnoreCase);
        foreach (DoodadSighting one in done.Found)
        {
            (int all, int asleep) = byPath.GetValueOrDefault(one.Path);
            byPath[one.Path] = (all + 1, asleep + (one.Asleep ? 1 : 0));
        }

        string said = string.Create(CultureInfo.InvariantCulture,
            $"doodads: {done.Found.Count} entities stand where the rooms name a doodad, on {byPath.Count} paths - sleeping map {done.SleepingNodes} of {done.SleepingSize} walked,"
            + $" awake {done.AwakeNodes}, {done.Named} with a path, read in {done.Milliseconds:0} ms");

        var lines = new List<string>();
        var unfound = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string room, RoomLayout layout) in rooms ?? [])
        {
            var stubs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int named = 0, found = 0, standing = 0;
            foreach (RoomDoodad doodad in layout.Doodads)
            {
                if (doodad.Stub.Length == 0)
                {
                    continue;
                }

                named++;
                bool known = byPath.TryGetValue(doodad.Stub, out (int All, int Asleep) count);
                found += known ? 1 : 0;
                if (stubs.Add(doodad.Stub))
                {
                    standing += count.All;
                    if (!known)
                    {
                        unfound.Add(doodad.Stub);
                    }
                }
            }

            lines.Add(named == 0
                ? TerrainRooms.NameFor(room) + ": its doodad lines name no stub"
                : string.Create(CultureInfo.InvariantCulture,
                    $"{TerrainRooms.NameFor(room)}: {found} of its {named} doodad lines name a path found in the area, {standing} such entities over {stubs.Count} paths"));
        }

        foreach ((string path, (int all, int asleep)) in byPath.OrderByDescending(one => one.Value.All).ThenBy(one => one.Key, StringComparer.OrdinalIgnoreCase))
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"{path}: {all} entities, {asleep} asleep"));
        }

        if (unfound.Count > 0)
        {
            lines.Add("found nowhere: " + string.Join(", ", unfound));
        }

        return (said, string.Join('\n', lines));
    }

    /// <summary>
    /// Every room file the area may have laid, whole, each under its path - the lines a placing is checked against, beside the sightings they are checked against.
    /// </summary>
    /// <remarks>
    /// WHAT THE FIRST ASSEMBLY CAPTURE LACKED: six rooms without a place, 844 sightings to hold them
    /// against, and not one of the rooms' doodad lines in the folder - rooms.txt prints a file only
    /// for the room the player stands in. The placing is pure (RoomDoodadFinder.Find takes the lines
    /// and the sightings and nothing else), so with the files here it runs again on any machine.
    /// THE WHOLE SET, not the loaded list's rooms alone - see AreaRoomSet for the ten of fifty-three.
    /// </remarks>
    /// <param name="read">How to get a file out of the install, by path.</param>
    /// <param name="loaded">The files the area loaded - the room files among them and every room of its room sets are printed.</param>
    public static string RoomFiles(Func<string, byte[]?> read, IReadOnlyList<string> loaded)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(loaded);
        var said = new StringBuilder();
        List<string> rooms = AreaRoomSet.Files(loaded, read);
        said.Append(Say(rooms.Count)).AppendLine(" room files the area may lay - the loaded list's and its room sets'");
        said.AppendLine();
        foreach (string path in rooms)
        {
            said.Append("##### ").AppendLine(path);
            byte[]? file = read(path);
            said.AppendLine(file is { Length: > 0 } ? StatDescriptionFiles.Decode(file).TrimEnd() : "(not in the install)");
            said.AppendLine();
        }

        return said.ToString();
    }

    /// <summary>
    /// The entities each place hit, by id - so a place in rooms-placed.txt can be held against the sightings in doodads.txt entity by entity.
    /// </summary>
    /// <param name="placed">Every room with its places, as settled.</param>
    /// <param name="survey">The survey the places were found in, whose sightings the places index.</param>
    public static string PlacedEntities(IReadOnlyList<(string Room, RoomLayout Layout, RoomDoodadPlaces Places)> placed, DoodadSurvey survey)
    {
        ArgumentNullException.ThrowIfNull(placed);
        ArgumentNullException.ThrowIfNull(survey);
        var said = new StringBuilder();
        said.AppendLine("=== the entities each place hit, by id, and the ones it yielded");
        foreach ((string room, _, RoomDoodadPlaces found) in placed)
        {
            foreach (RoomDoodadPlace place in found.Places)
            {
                Entities(said, TerrainRooms.NameFor(room), place, survey);
            }

            foreach ((RoomDoodadPlace place, _) in found.Yielded)
            {
                Entities(said, TerrainRooms.NameFor(room) + " (yielded)", place, survey);
            }
        }

        return said.ToString();
    }

    private static void Entities(StringBuilder said, string room, RoomDoodadPlace place, DoodadSurvey survey)
    {
        said.Append(room).Append(CultureInfo.InvariantCulture, $" tile {place.Where.X}, {place.Where.Y}, {RoomFinder.Said(place.Where.Turn)}: {place.Entities.Count} entities -");
        foreach (int one in place.Entities)
        {
            said.Append(' ').Append(one >= 0 && one < survey.Found.Count ? survey.Found[one].Id.ToString(CultureInfo.InvariantCulture) : "?");
        }

        said.AppendLine();
    }

    /// <summary>
    /// Every sighting of a survey as a table: id, path, model, where it stands, and which map it came out of - the finder's whole input, so a placing can be redone by hand.
    /// </summary>
    public static string Sightings(DoodadSurvey done)
    {
        ArgumentNullException.ThrowIfNull(done);
        var said = new StringBuilder();
        said.Append(Say(done.Found.Count)).AppendLine(" sightings; position in world units, z the game's way up");
        said.AppendLine("id\tpath\tmodel\tx\ty\tz\tmap");
        foreach (DoodadSighting one in done.Found)
        {
            said.Append(one.Id.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(one.Path).Append('\t').Append(one.Model)
                .Append('\t').Append(Num(one.X)).Append('\t').Append(Num(one.Y)).Append('\t').Append(Num(one.Z))
                .Append('\t').AppendLine(one.Asleep ? "sleeping" : "awake");
        }

        return said.ToString();
    }

    /// <summary>A line and its detail as one text, the way the hover reads it.</summary>
    public static string Lined((string Said, string Detail) text) => text.Detail.Length > 0 ? text.Said + '\n' + text.Detail : text.Said;

    private static string Say(int number) => number.ToString(CultureInfo.InvariantCulture);

    private static string Num(float number) => number.ToString("0.###", CultureInfo.InvariantCulture);
}
