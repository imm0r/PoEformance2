using System.Globalization;
using PoEformance.Features;
using PoEformance.Game.Files;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// The placing run again from a capture, without the game: POEF_CAPTURE names the capture's folder, and what the tool would write to rooms-placed.txt from its room-files.txt and doodads.txt goes to placing-replay.txt beside them.
/// </summary>
/// <remarks>
/// A DEV TOOL IN THE SHAPE OF A TEST, because the test project is where the finder and the capture's
/// texts are already built and referenced. With the variable unset it does nothing, so CI never
/// touches it; set, it is how a change to RoomDoodadFinder is held against a real area before it goes
/// near the game - the capture key's reason for writing every room file (CaptureReport.RoomFiles).
/// Run it with <c>POEF_CAPTURE=&lt;folder&gt; dotnet test --filter CaptureReplay</c>.
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

    /// <summary>rooms-placed.txt as the tool would write it from the capture's room files and sightings.</summary>
    public static string Replay(string folder)
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

        return string.Create(CultureInfo.InvariantCulture, $"grid {tilesX} x {tilesY} tiles, {survey.Found.Count} sightings, {rooms.Count} rooms\n")
            + CaptureReport.Lined(CaptureReport.Placed(placed)) + "\n\n" + CaptureReport.PlacedEntities(placed, survey)
            + "\n" + CaptureReport.Lined(CaptureReport.Doodads(survey, rooms));
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
                cells[6] == "sleeping"));
        }

        return new DoodadSurvey(0, 0, 0, 0, found, 0d, string.Empty);
    }

    /// <summary>The grid's size from capture.txt's terrain line, or past the farthest sighting where a capture predates the line.</summary>
    private static (int TilesX, int TilesY) Tiles(string summary, DoodadSurvey survey)
    {
        foreach (string line in summary.Split('\n'))
        {
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
