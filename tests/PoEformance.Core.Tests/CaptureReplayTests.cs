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
/// cannot - a corridor with no doodad line, of which The Stone Citadel has several. The recording holds
/// the terrain whole (the capture's world pass reads it), the room files are beside it, and
/// tile-identities.txt says what each laid tile's file asks - a capture from 0.1.155 on; without it the
/// search scores corners alone and says so. The places the doodads found are where the truth is, so each
/// is scored by the search's own yardstick and ranked against its list: a search that puts them first,
/// and alone, can be trusted with a room that has no doodads, and one that does not cannot. Both halves
/// of that comparison are printed, per room.
/// </remarks>
public class CaptureReplayTests
{
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
    /// search-replay.txt: every room searched by its ground and tiles over the recording's terrain - its best places, how many tie at the top, and where each place its doodads found ranks by the same yardstick.
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
        Func<string, TileIdentity?>? identity = Identities(folder, out int identities);
        said.AppendLine(identity is null
            ? $"no {CaptureReport.TileIdentitiesFile} in the capture - the tiles are not checked, the corners alone are"
            : string.Create(CultureInfo.InvariantCulture, $"{identities} tile identities from {CaptureReport.TileIdentitiesFile}"));
        if (grid.TilesX != placing.TilesX || grid.TilesY != placing.TilesY)
        {
            said.AppendLine(CultureInfo.InvariantCulture, $"the recording's grid is {grid.TilesX} x {grid.TilesY} tiles where capture.txt says {placing.TilesX} x {placing.TilesY} - the recording's is searched");
        }

        bool[] walkable = grid.WalkableTileMask();
        said.AppendLine();
        for (var one = 0; one < placing.Rooms.Count; one++)
        {
            (string room, RoomLayout layout) = placing.Rooms[one];
            string name = Path.GetFileNameWithoutExtension(room);
            RoomSearch search = RoomFinder.Find(layout, ground, grid.TilesX, grid.TilesY, grid.Tiles, identity, walkable: walkable);
            if (search.Why.Length > 0)
            {
                said.Append(name).Append(": no search - ").AppendLine(search.Why);
                continue;
            }

            said.Append(name).Append(CultureInfo.InvariantCulture, $": {search.Fits} places fit every corner, {search.Candidates.Count} listed by their {(search.TileChecked ? "tiles, then corners" : "corners")} - {search.Corners} corners in the stamp, {search.Free} free, {search.Left} big slots left out").AppendLine();

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

            // THE OTHER HALF: each place the doodads found, scored by the same yardstick and ranked against the list.
            RoomDoodadPlaces places = placing.Placed[one].Places;
            if (places.Places.Count == 0)
            {
                continue;
            }

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
    private static Func<string, TileIdentity?>? Identities(string folder, out int count)
    {
        count = 0;
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

        count = known.Count;
        return file => known.GetValueOrDefault(file);
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
