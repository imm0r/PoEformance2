using System.Globalization;
using System.Text;
using PoEformance.Core.Memory;
using PoEformance.Features;
using PoEformance.Game.Files;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// The placing and the ground-and-tile search run again from a capture, without the game: POEF_CAPTURE names the capture's folder; what the tool would write to rooms-placed.txt from its room-files.txt and doodads.txt goes to placing-replay.txt beside them, and the search over the recording's terrain to search-replay.txt.
/// </summary>
/// <remarks>
/// A DEV TOOL IN THE SHAPE OF A TEST, because the test project is where the finder and the capture's
/// texts are already built and referenced. With the variable unset it does nothing, so CI never
/// touches it; set, it is how a change to RoomDoodadFinder is held against a real area before it goes
/// near the game - the capture key's reason for writing every room file (CaptureReport.RoomFiles).
/// Run it with <c>POEF_CAPTURE=&lt;folder&gt; dotnet test --filter CaptureReplay</c>.
///
/// THE SEARCH REPLAY ANSWERS ONE QUESTION: whether the tiles alone could place a room its doodads
/// cannot - a corridor with no doodad line, of which The Stone Citadel has several, or a room of big
/// slots alone, which is every checkpoint and the boss of Sinter Rift. The recording holds the terrain
/// whole (the capture's world pass reads it), the room files are beside it, and tile-identities.txt
/// says what each laid tile's file asks - a capture from 0.1.155 on; without it the search scores
/// corners alone and says so. Three things are printed so both halves of every comparison are on the
/// page: how often each tile file was laid (a file laid once pins a room by itself), what each kind of
/// slot in a room asks and which laid files answer it by RoomPlacements' own rule, and the search's best
/// places beside each place the doodads found, scored by the same yardstick and ranked.
/// </remarks>
public class CaptureReplayTests
{
    /// <summary>How many of the rarest laid files are listed.</summary>
    private const int RarestListed = 40;

    /// <summary>The capture's folder, from POEF_CAPTURE - or null, and the replay does nothing.</summary>
    private static string? Folder => Environment.GetEnvironmentVariable("POEF_CAPTURE");

    /// <summary>Places every room of the capture by its doodads and writes the result beside the capture's files.</summary>
    [Fact]
    public void THEPLACINGRunsAgainFromACapture()
    {
        if (Folder is not { Length: > 0 } folder)
        {
            return;
        }

        string replay = Replay(folder);
        File.WriteAllText(Path.Combine(folder, "placing-replay.txt"), replay);
        Assert.StartsWith("grid ", replay, StringComparison.Ordinal);
        Assert.Contains("\nrooms: ", replay, StringComparison.Ordinal);
    }

    /// <summary>Searches every room of the capture over its recording's terrain and writes the result, with each doodad place's rank, beside the capture's files.</summary>
    [Fact]
    public void THESEARCHRunsAgainFromACapture()
    {
        if (Folder is not { Length: > 0 } folder)
        {
            return;
        }

        string replay = SearchReplay(folder);
        File.WriteAllText(Path.Combine(folder, "search-replay.txt"), replay);
        Assert.StartsWith("terrain ", replay, StringComparison.Ordinal);
    }

    /// <summary>rooms-placed.txt as the tool would write it from the capture's room files and sightings.</summary>
    public static string Replay(string folder)
    {
        Placing placing = Place(folder);
        return string.Create(CultureInfo.InvariantCulture, $"grid {placing.TilesX} x {placing.TilesY} tiles, {placing.Survey.Found.Count} sightings, {placing.Rooms.Count} rooms\n")
            + CaptureReport.Lined(CaptureReport.Placed(placing.Placed)) + "\n\n" + CaptureReport.PlacedEntities(placing.Placed, placing.Survey)
            + "\n" + CaptureReport.Lined(CaptureReport.Doodads(placing.Survey, placing.Rooms));
    }

    /// <summary>
    /// search-replay.txt: the laid tile files rarest first, then every room - what each kind of its slots asks and which laid files answer, the tiles under each place its doodads found, and its search by ground and tiles with the doodad places ranked by the same yardstick.
    /// </summary>
    public static string SearchReplay(string folder)
    {
        var said = new StringBuilder();
        TerrainGrid? grid = Terrain(folder, out string how);
        said.AppendLine(how);
        if (grid is not { Ground: { } ground })
        {
            said.AppendLine(grid is null ? "nothing to search without the terrain" : "nothing to search without the ground types");
            return said.ToString();
        }

        Placing placing = Place(folder);
        Dictionary<string, TileIdentity>? identities = Identities(folder);
        Func<string, TileIdentity?>? identity = identities is null ? null : file => identities.GetValueOrDefault(file);
        said.AppendLine(identities is null
            ? $"no {CaptureReport.TileIdentitiesFile} in the capture - the tiles are not checked, the corners alone are"
            : string.Create(CultureInfo.InvariantCulture, $"{identities.Count} tile identities from {CaptureReport.TileIdentitiesFile}"));
        if (grid.TilesX != placing.TilesX || grid.TilesY != placing.TilesY)
        {
            said.AppendLine(CultureInfo.InvariantCulture, $"the recording's grid is {grid.TilesX} x {grid.TilesY} tiles where capture.txt says {placing.TilesX} x {placing.TilesY} - the recording's is searched");
        }

        // HOW ALONE EACH LAID FILE IS: a file laid once pins whatever room asks for it.
        Dictionary<string, int> cells = Cells(grid.Tiles);
        if (identities is not null && cells.Count > 0)
        {
            said.AppendLine();
            said.AppendLine(CultureInfo.InvariantCulture, $"=== the {cells.Count} tile files laid, rarest first: cells, and how often each was laid (cells over its size)");
            foreach ((string path, int count) in cells.OrderBy(one => Instances(one.Key, one.Value, identities)).ThenBy(one => one.Key, StringComparer.OrdinalIgnoreCase).Take(RarestListed))
            {
                said.Append("  ").AppendLine(LaidSaid(path, count, identities, grid.Tiles));
            }

            if (cells.Count > RarestListed)
            {
                said.AppendLine(CultureInfo.InvariantCulture, $"  ... and {cells.Count - RarestListed} more");
            }
        }

        bool[] walkable = grid.WalkableTileMask();
        for (var one = 0; one < placing.Rooms.Count; one++)
        {
            (string room, RoomLayout layout) = placing.Rooms[one];
            said.AppendLine();
            said.Append("=== ").Append(Path.GetFileNameWithoutExtension(room)).Append(CultureInfo.InvariantCulture, $", {layout.Width} x {layout.Height}").AppendLine();

            // THE ROOM'S ASKS, each kind of slot once, and the laid files alike by RoomPlacements' own
            // rule: the size as an unordered pair, the tag where the slot names one, the edge and ground
            // types as sets. A kind with one file alike, laid once, is the room's own fingerprint.
            if (identities is not null)
            {
                foreach (Ask ask in Asks(layout))
                {
                    List<string> alike = [.. identities.Where(laid => Alike(ask, laid.Value)).Select(laid => laid.Key).OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
                    said.Append("  slot ").Append(ask.Said).Append(CultureInfo.InvariantCulture, $": {alike.Count} laid files alike");
                    if (alike.Count is > 0 and <= 4)
                    {
                        said.Append(" - ").Append(string.Join("; ", alike.Select(path => LaidSaid(path, cells.GetValueOrDefault(path), identities, grid.Tiles))));
                    }

                    said.AppendLine();
                }
            }

            // THE OTHER HALF, FIRST: what lies under each place the doodads found, whether or not the search
            // can run - and for a place laid as written, every big slot against the tile on its own cell, by
            // the strict rule and with an unnamed type as any, so the rule can be held against a known place.
            RoomDoodadPlaces places = placing.Placed[one].Places;
            foreach (RoomDoodadPlace place in places.Places)
            {
                RoomCandidate at = place.Where;
                said.AppendLine(CultureInfo.InvariantCulture, $"  doodads at tile {at.X}, {at.Y}, {RoomFinder.Said(at.Turn)}, {at.Width} x {at.Height} on the grid - laid under it: {Under(grid.Tiles, at)}");
                if (at.Turn != 0 || identities is null || grid.Tiles is not { } laidTiles)
                {
                    continue;
                }

                for (var line = 0; line < layout.Height; line++)
                {
                    for (var column = 0; column < layout.Width; column++)
                    {
                        RoomSlot slot = layout.SlotAt(column, line);
                        if (!slot.IsTile || (slot.Width == 1 && slot.Height == 1))
                        {
                            continue;
                        }

                        Ask ask = AskOf(layout, slot);
                        string path = laidTiles.PathAt(at.X + column, at.Y + line);
                        TileIdentity? laid = identities.GetValueOrDefault(path);
                        string verdict = laid is null ? "no identity" : Alike(ask, laid) ? "alike" : Loosely(ask, laid) ? "alike with an unnamed type as any" : "unlike";

                        // AND WHICH PIECE of its file the tile on the slot's own cell is: (0, 0) there says the
                        // slot's corner is the tile's corner, which is what pins a placement to the tile and not
                        // merely into it. The placement is -1 without the rotation tables, which a replay has not.
                        (int pieceX, int pieceY) = laidTiles.SubAt(at.X + column, at.Y + line);
                        said.AppendLine(CultureInfo.InvariantCulture, $"    slot {column}, {line} {ask.Said} - laid {(laid is null ? Short(path) : LaidSaid(path, 0, identities, null))} piece {pieceX}, {pieceY} placement {laidTiles.PlacementAt(at.X + column, at.Y + line)}: {verdict}");
                    }
                }
            }

            RoomSearch search = RoomFinder.Find(layout, ground, grid.TilesX, grid.TilesY, grid.Tiles, identity, walkable: walkable);
            if (search.Why.Length > 0)
            {
                said.Append("  no search - ").AppendLine(search.Why);
                continue;
            }

            said.Append(CultureInfo.InvariantCulture, $"  search: {search.Fits} places fit every corner, {search.Candidates.Count} listed by their {(search.TileChecked ? "tiles, then corners" : "corners")} - {search.Corners} corners in the stamp, {search.Free} free, {search.Left} big slots left out").AppendLine();

            // HOW ALONE THE BEST PLACE IS: every listed place scoring exactly as the first does.
            var ties = 0;
            if (search.Candidates.Count > 0)
            {
                RoomCandidate top = search.Candidates[0];
                while (ties < search.Candidates.Count && search.Candidates[ties].TilesAgree == top.TilesAgree && search.Candidates[ties].Matched == top.Matched)
                {
                    ties++;
                }
            }

            for (var rank = 0; rank < Math.Min(5, search.Candidates.Count); rank++)
            {
                said.Append("  ").AppendLine(Said(rank + 1, search.Candidates[rank], search.Misses[rank]));
            }

            if (ties > 1)
            {
                said.AppendLine(CultureInfo.InvariantCulture, $"  {ties} places tie at the top");
            }

            // WHERE THE BEST PLACE PARTS WITH THE AREA, slot by slot: which asks the tiles there do not answer.
            if (search.Candidates.Count > 0)
            {
                foreach (RoomPart part in search.Parts(search.Candidates[0]).Where(part => !part.IsCorner).Take(10))
                {
                    said.AppendLine(CultureInfo.InvariantCulture, $"    slot {part.RoomU}, {part.RoomV} at tile {part.X}, {part.Y} ({(part.Sides.Length > 0 ? part.Sides : "inside")}, {part.Join}): wants {part.Wanted} - laid {part.Laid}");
                }
            }

            if (places.Places.Count == 0)
            {
                continue;
            }

            // THE DOODAD PLACES BY THE SEARCH'S OWN YARDSTICK, ranked against its list.
            (RoomScorer? scorer, string why) = RoomFinder.Scorer(layout, ground, grid.TilesX, grid.TilesY, grid.Tiles, identity, walkable);
            foreach (RoomDoodadPlace place in places.Places)
            {
                RoomCandidate at = place.Where;
                RoomPlace? scored = scorer?.Score(at.X, at.Y, at.Turn);
                if (scored is null)
                {
                    said.AppendLine(CultureInfo.InvariantCulture, $"  doodads at tile {at.X}, {at.Y}, {RoomFinder.Said(at.Turn)}: not scored - {why}");
                    continue;
                }

                int above = 0, rank = -1;
                for (var listed = 0; listed < search.Candidates.Count; listed++)
                {
                    RoomCandidate candidate = search.Candidates[listed];
                    if (candidate.X == at.X && candidate.Y == at.Y && candidate.Turn == at.Turn)
                    {
                        rank = listed;
                    }

                    if (candidate.TilesAgree > scored.Where.TilesAgree || (candidate.TilesAgree == scored.Where.TilesAgree && candidate.Matched > scored.Where.Matched))
                    {
                        above++;
                    }
                }

                said.Append("  doodads at ").Append(Said(0, scored.Where, scored.Misses));
                if (rank >= 0)
                {
                    said.Append(CultureInfo.InvariantCulture, $" - rank {rank + 1}");
                }
                else
                {
                    said.Append(" - not among the listed");
                }

                said.AppendLine(CultureInfo.InvariantCulture, $", {above} listed places rank above it");
            }
        }

        return said.ToString();
    }

    /// <summary>One place in a line: where, how it was laid, and both halves of what agrees there.</summary>
    private static string Said(int rank, RoomCandidate where, RoomMisses misses)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{(rank > 0 ? rank + ". " : string.Empty)}tile {where.X}, {where.Y}, {RoomFinder.Said(where.Turn)}: {where.TilesAgree} of {where.Tiles} tiles agree, {where.Matched} of {where.Corners} corners, {RoomArrangement.Beside(where, misses)}% beside, {misses.Elsewhere} misses at no join");

    /// <summary>What a kind of slot asks of the tile under it, in the terms RoomPlacements compares: the size, the tag, the edge and ground types sorted.</summary>
    private sealed record Ask(int Width, int Height, string Tag, string[] Edges, string[] Grounds)
    {
        public string Said => string.Create(
            CultureInfo.InvariantCulture,
            $"{Width}x{Height}{(Tag.Length > 0 ? " \"" + Tag + "\"" : string.Empty)} edges [{string.Join(", ", Edges.Select(Short))}] grounds [{string.Join(", ", Grounds.Select(Short))}]");
    }

    /// <summary>Each kind of slot in a room once, in the order its first slot comes.</summary>
    private static List<Ask> Asks(RoomLayout room)
    {
        var asks = new List<Ask>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var line = 0; line < room.Height; line++)
        {
            for (var column = 0; column < room.Width; column++)
            {
                RoomSlot slot = room.SlotAt(column, line);
                if (!slot.IsTile)
                {
                    continue;
                }

                Ask ask = AskOf(room, slot);
                if (seen.Add(ask.Said))
                {
                    asks.Add(ask);
                }
            }
        }

        return asks;
    }

    /// <summary>What one slot asks, in RoomPlacements' terms.</summary>
    private static Ask AskOf(RoomLayout room, RoomSlot slot)
        => new(
            slot.Width,
            slot.Height,
            room.Named(slot.Tag),
            Sorted([room.Named(slot.Edge(0)), room.Named(slot.Edge(1)), room.Named(slot.Edge(2)), room.Named(slot.Edge(3))]),
            Sorted([room.Named(slot.Ground(0)), room.Named(slot.Ground(1)), room.Named(slot.Ground(2)), room.Named(slot.Ground(3))]));

    /// <summary>Whether a laid file answers a slot's ask - RoomPlacements.Compare's rule, kept in step with it by hand.</summary>
    private static bool Alike(Ask ask, TileIdentity laid)
        => SameSize(ask, laid)
            && (ask.Tag.Length == 0 || string.Equals(ask.Tag, laid.Tag, StringComparison.OrdinalIgnoreCase))
            && Sorted(laid.Edges).AsSpan().SequenceEqual(ask.Edges, StringComparer.OrdinalIgnoreCase)
            && Sorted(laid.Grounds).AsSpan().SequenceEqual(ask.Grounds, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The same, with an unnamed type in the ask taken as any - the reading the ground stamp gives nought (RoomFinder: a corner type nought names nothing). A hypothesis to hold against a known place, not the tool's rule.
    /// </summary>
    private static bool Loosely(Ask ask, TileIdentity laid)
        => SameSize(ask, laid)
            && (ask.Tag.Length == 0 || string.Equals(ask.Tag, laid.Tag, StringComparison.OrdinalIgnoreCase))
            && Covers(laid.Edges, ask.Edges)
            && Covers(laid.Grounds, ask.Grounds);

    private static bool SameSize(Ask ask, TileIdentity laid)
        => (laid.Width == ask.Width && laid.Height == ask.Height) || (laid.Width == ask.Height && laid.Height == ask.Width);

    /// <summary>Whether every named type the ask lists is among the laid ones, each laid one answering at most one ask.</summary>
    private static bool Covers(IReadOnlyList<string> laid, string[] asks)
    {
        List<string> pool = [.. laid.Select(one => one.Replace('\\', '/'))];
        foreach (string ask in asks)
        {
            if (ask.Length == 0)
            {
                continue;
            }

            int found = pool.FindIndex(one => string.Equals(one, ask, StringComparison.OrdinalIgnoreCase));
            if (found < 0)
            {
                return false;
            }

            pool.RemoveAt(found);
        }

        return true;
    }

    private static string[] Sorted(IEnumerable<string> four)
    {
        string[] sorted = [.. four.Select(one => one.Replace('\\', '/'))];
        Array.Sort(sorted, StringComparer.OrdinalIgnoreCase);
        return sorted;
    }

    /// <summary>A path's last name without its extension, or a dash for none.</summary>
    private static string Short(string path) => path.Length == 0 ? "-" : Path.GetFileNameWithoutExtension(path);

    /// <summary>How many cells each tile file covers in the area.</summary>
    private static Dictionary<string, int> Cells(TerrainTiles? tiles)
    {
        var cells = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (tiles is null)
        {
            return cells;
        }

        for (var y = 0; y < tiles.Height; y++)
        {
            for (var x = 0; x < tiles.Width; x++)
            {
                string path = tiles.PathAt(x, y);
                if (path.Length > 0)
                {
                    cells[path] = cells.GetValueOrDefault(path) + 1;
                }
            }
        }

        return cells;
    }

    /// <summary>How often a file was laid: its cells over its size, rounded up - one where its size is not known.</summary>
    private static int Instances(string path, int cells, Dictionary<string, TileIdentity> identities)
    {
        int size = identities.TryGetValue(path, out TileIdentity? identity) ? Math.Max(1, identity.Width * identity.Height) : cells;
        return (cells + size - 1) / Math.Max(1, size);
    }

    /// <summary>A laid file in a line: its path under Metadata/Terrain, size, tag, edges and grounds, cells and times laid - and where, for a file laid once.</summary>
    private static string LaidSaid(string path, int cells, Dictionary<string, TileIdentity> identities, TerrainTiles? tiles)
    {
        string name = path.StartsWith("Metadata/Terrain/", StringComparison.OrdinalIgnoreCase) ? path["Metadata/Terrain/".Length..] : path;
        if (!identities.TryGetValue(path, out TileIdentity? identity))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{name}  (no identity)  {cells} cells");
        }

        var said = new StringBuilder(name);
        said.Append(CultureInfo.InvariantCulture, $"  {identity.Width}x{identity.Height}{(identity.Tag.Length > 0 ? " \"" + identity.Tag + "\"" : string.Empty)}");
        said.Append(CultureInfo.InvariantCulture, $" edges [{string.Join(", ", identity.Edges.Select(Short))}] grounds [{string.Join(", ", identity.Grounds.Select(Short))}]");
        if (cells > 0)
        {
            int laid = Instances(path, cells, identities);
            said.Append(CultureInfo.InvariantCulture, $"  {cells} cells = laid {laid}x");
            if (laid == 1 && tiles is not null && Bounds(tiles, path) is var (x0, y0, x1, y1))
            {
                said.Append(CultureInfo.InvariantCulture, $" at tiles {x0}..{x1}, {y0}..{y1}");
            }
        }

        return said.ToString();
    }

    /// <summary>The tiles a file covers, as the corners of their bounding box - or null where it covers none.</summary>
    private static (int X0, int Y0, int X1, int Y1)? Bounds(TerrainTiles tiles, string path)
    {
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        for (var y = 0; y < tiles.Height; y++)
        {
            for (var x = 0; x < tiles.Width; x++)
            {
                if (string.Equals(tiles.PathAt(x, y), path, StringComparison.OrdinalIgnoreCase))
                {
                    x0 = Math.Min(x0, x);
                    y0 = Math.Min(y0, y);
                    x1 = Math.Max(x1, x);
                    y1 = Math.Max(y1, y);
                }
            }
        }

        return x1 < 0 ? null : (x0, y0, x1, y1);
    }

    /// <summary>The distinct files laid under a footprint, most cells first.</summary>
    private static string Under(TerrainTiles? tiles, RoomCandidate at)
    {
        if (tiles is null)
        {
            return "(tiles not read)";
        }

        var under = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var y = at.Y; y < at.Y + at.Height; y++)
        {
            for (var x = at.X; x < at.X + at.Width; x++)
            {
                string path = tiles.PathAt(x, y);
                under[path.Length > 0 ? path : "(none)"] = under.GetValueOrDefault(path.Length > 0 ? path : "(none)") + 1;
            }
        }

        return string.Join(", ", under.OrderByDescending(one => one.Value).ThenBy(one => one.Key, StringComparer.OrdinalIgnoreCase)
            .Select(one => string.Create(CultureInfo.InvariantCulture, $"{Short(one.Key)} x{one.Value}")));
    }

    /// <summary>A capture's rooms placed by their doodads, as the tool would place them.</summary>
    /// <param name="Rooms">Every room of room-files.txt, read.</param>
    /// <param name="Survey">Every sighting of doodads.txt.</param>
    /// <param name="TilesX">The grid's tiles across, as capture.txt says.</param>
    /// <param name="TilesY">The grid's tiles down.</param>
    /// <param name="Placed">Every room with its places, settled.</param>
    private sealed record Placing(
        List<(string Room, RoomLayout Layout)> Rooms,
        DoodadSurvey Survey,
        int TilesX,
        int TilesY,
        List<(string Room, RoomLayout Layout, RoomDoodadPlaces Places)> Placed);

    /// <summary>Every room of the capture placed by its doodads over the capture's sightings.</summary>
    private static Placing Place(string folder)
    {
        List<(string Room, RoomLayout Layout)> rooms = Rooms(File.ReadAllText(Path.Combine(folder, CaptureReport.RoomFilesFile)));
        DoodadSurvey survey = Survey(File.ReadAllText(Path.Combine(folder, CaptureReport.DoodadsFile)));
        (int tilesX, int tilesY) = Tiles(File.ReadAllText(Path.Combine(folder, CaptureReport.SummaryFile)), survey);

        var found = new List<(string Room, RoomDoodadPlaces Places)>(rooms.Count);
        foreach ((string room, RoomLayout layout) in rooms)
        {
            found.Add((room, RoomDoodadFinder.Find(layout.Doodads, layout.Width, layout.Height, survey.Found, tilesX, tilesY)));
        }

        List<(string Room, RoomDoodadPlaces Places)> settled = RoomDoodadFinder.Settle(found, tilesX, tilesY);
        var placed = new List<(string Room, RoomLayout Layout, RoomDoodadPlaces Places)>(rooms.Count);
        for (var one = 0; one < rooms.Count; one++)
        {
            placed.Add((rooms[one].Room, rooms[one].Layout, settled[one].Places));
        }

        return new Placing(rooms, survey, tilesX, tilesY, placed);
    }

    /// <summary>
    /// The terrain as the capture's world pass read it, out of memory.rec - or null and why. The replay serves the newest read at or before its last frame, which is the pass's.
    /// </summary>
    private static TerrainGrid? Terrain(string folder, out string how)
    {
        string path = Path.Combine(folder, CaptureReport.MemoryFile);
        if (!File.Exists(path))
        {
            how = $"terrain not read: no {CaptureReport.MemoryFile} in the capture";
            return null;
        }

        using var replay = ReplayMemoryReader.Load(File.OpenRead(path));
        if (!replay.ResolvedStatics.TryGetValue("GameStates", out ulong gameStates))
        {
            how = "terrain not read: the recording names no GameStates static";
            return null;
        }

        // THE LIVE SCHEMA, not the one the committed recordings replay against: a capture is of the client as it is.
        var world = new WorldReader(replay, RealSessionTests.LiveSchema());
        TerrainGrid? grid = world.Read(gameStates, CaptureMemory.MostEntities).Terrain;
        if (grid is null)
        {
            how = "terrain not read from the recording";
            return null;
        }

        string groundSaid = grid.Ground is null ? "not read" : string.Create(CultureInfo.InvariantCulture, $"{grid.Ground.Types.Count} named");
        string tilesSaid = grid.Tiles is null ? "not read" : string.Create(CultureInfo.InvariantCulture, $"{grid.Tiles.Paths.Count} files laid");
        how = string.Create(CultureInfo.InvariantCulture, $"terrain {grid.TilesX} x {grid.TilesY} tiles from the recording, ground types {groundSaid}, tiles {tilesSaid}");
        return grid;
    }

    /// <summary>
    /// Each laid tile file's identity from tile-identities.txt, by path - or null where the capture has none (before 0.1.155), and the search scores corners alone.
    /// </summary>
    private static Dictionary<string, TileIdentity>? Identities(string folder)
    {
        string path = Path.Combine(folder, CaptureReport.TileIdentitiesFile);
        if (!File.Exists(path))
        {
            return null;
        }

        var known = new Dictionary<string, TileIdentity>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in File.ReadLines(path))
        {
            string[] cells = raw.TrimEnd('\r').Split('\t');
            if (cells.Length < 12
                || !int.TryParse(cells[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int width)
                || !int.TryParse(cells[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int height))
            {
                continue;
            }

            known[cells[0]] = new TileIdentity(width, height, cells[3], cells[4..8], cells[8..12]);
        }

        return known;
    }

    /// <summary>Every room of room-files.txt, each block under its "##### path" header parsed as the file.</summary>
    private static List<(string Room, RoomLayout Layout)> Rooms(string text)
    {
        var rooms = new List<(string Room, RoomLayout Layout)>();
        string? room = null;
        var block = new List<string>();
        void Close()
        {
            if (room is not null)
            {
                rooms.Add((room, RoomLayout.Parse(string.Join('\n', block))));
            }

            block.Clear();
        }

        foreach (string line in text.Split('\n'))
        {
            if (line.StartsWith("##### ", StringComparison.Ordinal))
            {
                Close();
                room = line[6..].Trim();
            }
            else
            {
                block.Add(line.TrimEnd('\r'));
            }
        }

        Close();
        return rooms;
    }

    /// <summary>Every sighting of doodads.txt's table.</summary>
    private static DoodadSurvey Survey(string text)
    {
        var found = new List<DoodadSighting>();
        var inTable = false;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (!inTable)
            {
                inTable = line.StartsWith("id\tpath\tmodel", StringComparison.Ordinal);
                continue;
            }

            string[] cells = line.Split('\t');
            if (cells.Length < 7)
            {
                continue;
            }

            found.Add(new DoodadSighting(
                uint.Parse(cells[0], CultureInfo.InvariantCulture), cells[1], cells[2],
                float.Parse(cells[3], CultureInfo.InvariantCulture), float.Parse(cells[4], CultureInfo.InvariantCulture), float.Parse(cells[5], CultureInfo.InvariantCulture),
                cells[6] == "sleeping") { Remembered = cells[6] == "remembered" });
        }

        return new DoodadSurvey(0, 0, 0, 0, found, 0d, string.Empty);
    }

    /// <summary>The grid's size from capture.txt's terrain line, or past the farthest sighting where a capture predates the line.</summary>
    private static (int TilesX, int TilesY) Tiles(string summary, DoodadSurvey survey)
    {
        // WITHOUT THE CARRIAGE RETURN, or the line's last word is "tiles\r" and every capture written on
        // Windows fell through to the guess below - 46 x 81 for a grid of 45 x 93.
        foreach (string raw in summary.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("terrain  ", StringComparison.Ordinal))
            {
                string[] words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                int tiles = Array.IndexOf(words, "tiles");
                if (tiles >= 3 && int.TryParse(words[tiles - 3], CultureInfo.InvariantCulture, out int x) && int.TryParse(words[tiles - 1], CultureInfo.InvariantCulture, out int y))
                {
                    return (x, y);
                }
            }
        }

        float farX = 0f, farY = 0f;
        foreach (DoodadSighting one in survey.Found)
        {
            farX = Math.Max(farX, one.X);
            farY = Math.Max(farY, one.Y);
        }

        return ((int)(farX / RoomDoodadFinder.WithinUnits) + 10, (int)(farY / RoomDoodadFinder.WithinUnits) + 10);
    }
}
