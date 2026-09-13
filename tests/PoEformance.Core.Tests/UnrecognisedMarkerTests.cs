using PoEformance.Features;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Collecting the icon names the marker classifier could not place.
/// </summary>
/// <remarks>
/// The value of the file is that it is COMPLETE and SHORT: every name that ever failed, each
/// exactly once. Both halves are load-bearing and neither is visible in the game - a name
/// missed is a keyword nobody knows to add, and a name repeated per sighting buries the twenty
/// worth reading under thousands of lines.
/// </remarks>
public class UnrecognisedMarkerTests
{
    private static string TempPath()
        => Path.Combine(Path.GetTempPath(), $"poeformance-markers-{Guid.NewGuid():N}.tsv");

    private static WorldEntity Marked(uint id, string icon, string path, PoiKind kind)
        => new(id, id, path, EntityKind.Terrain, 0f, 0f, 0f, Poi: kind, MapIcon: icon);

    private static WorldSnapshot With(params WorldEntity[] entities)
        => new(true, null, entities, new float[16], Area: new AreaInfo("X", "The Well of Souls", 2, false, false));

    [Fact]
    public void AnUnrecognisedNameIsCollectedWithItsPathAndArea()
    {
        string file = TempPath();
        try
        {
            var log = new UnrecognisedMarkers(file);
            int found = log.Note(With(
                Marked(1, "GuildStash", "Metadata/MiscellaneousObjects/GuildStash", PoiKind.Marked)));

            Assert.Equal(1, found);
            Assert.Equal(1, log.Count);

            string[] rows = [.. File.ReadLines(file).Where(l => l.Length > 0 && l[0] != '#')];
            string[] parts = Assert.Single(rows).Split('\t');

            // The name first, because a keyword list is built by pasting that column. The path
            // second, because "GuildStash" says little and the path says what the thing is.
            Assert.Equal("GuildStash", parts[0]);
            Assert.Equal("Metadata/MiscellaneousObjects/GuildStash", parts[1]);
            Assert.Equal("The Well of Souls", parts[2]);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void ANameTheRulesDoRecogniseIsNotCollected()
    {
        string file = TempPath();
        try
        {
            var log = new UnrecognisedMarkers(file);

            // Waypoint and Checkpoint both match a keyword, so neither is a candidate for one.
            Assert.Equal(0, log.Note(With(
                Marked(1, "Waypoint", "Metadata/MiscellaneousObjects/Waypoint", PoiKind.Waypoint),
                Marked(2, "Checkpoint", "Metadata/MiscellaneousObjects/Checkpoint", PoiKind.Checkpoint))));
            Assert.False(File.Exists(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>
    /// The same name is written once, however many times it is met.
    /// </summary>
    /// <remarks>
    /// A hideout's stash is passed on every trip to town. Without this the file would be a
    /// sighting log thousands of lines long, and the twenty names actually worth reading would
    /// be unfindable in it.
    /// </remarks>
    [Fact]
    public void TheSameNameIsOnlyWrittenOnce()
    {
        string file = TempPath();
        try
        {
            var log = new UnrecognisedMarkers(file);
            WorldSnapshot seen = With(Marked(1, "GuildStash", "Metadata/X", PoiKind.Marked));

            Assert.Equal(1, log.Note(seen));
            Assert.Equal(0, log.Note(seen));
            Assert.Equal(0, log.Note(With(Marked(9, "GuildStash", "Metadata/Somewhere/Else", PoiKind.Marked))));

            Assert.Single(File.ReadLines(file), l => l.Length > 0 && l[0] != '#');
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>
    /// A name collected in an earlier run is not collected again in a later one.
    /// </summary>
    /// <remarks>
    /// The deduplication has to survive restarts or it does nothing: the tool is started fresh
    /// every session, and a stash passed on day two would be written again on day three.
    /// </remarks>
    [Fact]
    public void ANameFromAnEarlierRunIsNotWrittenAgain()
    {
        string file = TempPath();
        try
        {
            WorldSnapshot seen = With(Marked(1, "GuildStash", "Metadata/X", PoiKind.Marked));
            Assert.Equal(1, new UnrecognisedMarkers(file).Note(seen));

            // A second instance is a second run of the tool.
            var later = new UnrecognisedMarkers(file);
            Assert.Equal(0, later.Note(seen));
            Assert.Equal(1, later.Count);
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>A place the game did not icon carries no name to collect.</summary>
    [Fact]
    public void APlaceWithNoIconOfItsOwnIsNotACandidate()
    {
        string file = TempPath();
        try
        {
            var log = new UnrecognisedMarkers(file);
            Assert.Equal(0, log.Note(With(
                Marked(1, string.Empty, "Metadata/Terrain/Something", PoiKind.Marked))));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
