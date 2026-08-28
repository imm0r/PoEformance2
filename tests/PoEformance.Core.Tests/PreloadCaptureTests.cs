using PoEformance.Features;

namespace PoEformance.Core.Tests;

/// <summary>
/// The matching rules, against two unedited captures of what PoE2 really loads.
/// </summary>
/// <remarks>
/// WHY CAPTURES AND NOT INVENTED LINES. Every decision this file pins was a guess until an area
/// was dumped and read: that a mechanic's NAME is in every area whether or not the mechanic is,
/// that a line is not always one path, that a handful of paths arrive with backslashes. None of
/// them is visible from the code, and each one fails silently - the symptom of getting any of
/// them wrong is a watch list that never says anything, which looks exactly like an area that
/// simply does not have the thing.
///
/// The pair is chosen to be a control and an experiment: a hideout, whose contents are never a
/// signal, and the richest of four captured maps, which carries Strongboxes, Delirium, Incursion,
/// Breach and Sanctum at once.
/// </remarks>
public sealed class PreloadCaptureTests
{
    private static IReadOnlyList<string> Captured(string name)
    {
        foreach (string root in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var at = new DirectoryInfo(root);
            while (at is not null)
            {
                string candidate = Path.Combine(at.FullName, "fixtures", "preloads", name);
                if (File.Exists(candidate))
                {
                    return [.. File.ReadAllLines(candidate)
                        .Select(line => line.Trim())
                        .Where(line => line.Length > 0 && !line.StartsWith('#'))];
                }

                at = at.Parent;
            }
        }

        throw new FileNotFoundException($"capture {name} not found");
    }

    private static IReadOnlyList<string> Hideout() => Captured("hideout.txt");

    private static IReadOnlyList<string> Map() => Captured("map-rich.txt");

    /// <summary>A map with 850 files in it and not one league mechanic.</summary>
    private static IReadOnlyList<string> Plain() => Captured("map-plain.txt");

    /// <summary>The two currency items every area carries, whatever it contains.</summary>
    private const string OmenItem = "Metadata/Items/Currency/OmenStrongboxOpenableAgain";
    private const string KeyItem = "Metadata/Items/Currency/StrongboxKey";

    /// <summary>The file an area only loads when it actually has one.</summary>
    private const string TheChest = "Metadata/Chests/StrongBoxes/Strongbox.ot.tok";

    [Fact]
    public void AMechanicsNameIsInEveryAreaButItsChestIsNot()
    {
        // THE MEASUREMENT THE WHOLE EXACT-PATH RULE RESTS ON, and the one that could only come
        // from a capture. Search the five captured areas for "Strongbox" and every one of them
        // answers - including a hideout - because two CURRENCY ITEMS carry the word and ship
        // everywhere. So a watch list matching fragments fires in every area for every mechanic
        // whose orb is in the game, which is all of them.
        //
        // The chest itself is the honest signal, and it is in one area of the two.
        HashSet<string>? hideout = PreloadAlerts.Lookup(Hideout());
        HashSet<string>? map = PreloadAlerts.Lookup(Map());

        Assert.True(PreloadAlerts.Here(OmenItem, hideout));
        Assert.True(PreloadAlerts.Here(KeyItem, hideout));
        Assert.True(PreloadAlerts.Here(OmenItem, map));
        Assert.True(PreloadAlerts.Here(KeyItem, map));

        Assert.False(PreloadAlerts.Here(TheChest, hideout));
        Assert.True(PreloadAlerts.Here(TheChest, map));

        // And said the way the alerts themselves ask it, so this is not a test of Here() alone.
        var watch = new[] { new PreloadAlertEntry(TheChest, "Strongbox") };
        Assert.Empty(PreloadAlerts.Found(watch, Hideout()));
        Assert.Single(PreloadAlerts.Found(watch, Map()));
    }

    [Fact]
    public void APathPackedIntoALineIsStillWatchable()
    {
        // 46 lines of this one capture pack two paths together with a pipe or a semicolon, and
        // the Metadata half is never a line of its own. Matched whole, those paths cannot be
        // watched at all and never appear in the "In this area" tab - absent rather than wrong.
        IReadOnlyList<string> lines = Map();

        string compound = lines.First(line => line.Contains('|', StringComparison.Ordinal)
                                              && line.Contains("|Metadata", StringComparison.Ordinal));
        string embedded = compound[(compound.IndexOf('|', StringComparison.Ordinal) + 1)..];

        Assert.DoesNotContain(embedded, lines);

        HashSet<string>? map = PreloadAlerts.Lookup(lines);
        Assert.True(PreloadAlerts.Here(embedded, map), "the packed path should be reachable");

        // THE WHOLE LINE STILL MATCHES TOO. A row added from the area tab carries the compound
        // line verbatim, so dropping it would break exactly the lists built the documented way.
        Assert.True(PreloadAlerts.Here(compound, map), "the line itself should still match");
    }

    [Fact]
    public void ABackslashPathMatchesTheSpellingSomebodyWouldType()
    {
        // Two lines in this capture use backslashes where every other line uses slashes. Nobody
        // typing a watch entry would guess which, and guessing wrong matches nothing and explains
        // nothing - the failure this project has already paid for once, in a key binding.
        IReadOnlyList<string> lines = Map();

        string odd = lines.First(line => line.Contains('\\', StringComparison.Ordinal));
        Assert.StartsWith("Metadata", odd, StringComparison.Ordinal);

        HashSet<string>? map = PreloadAlerts.Lookup(lines);
        Assert.True(PreloadAlerts.Here(odd, map), "the capture's own spelling should match");
        Assert.True(
            PreloadAlerts.Here(odd.Replace('\\', '/'), map),
            "and so should the spelling a person would type");
    }

    [Fact]
    public void TheRadarsPathsAreNotPreloadPaths()
    {
        // A LIST THAT WAS ALMOST ADDED. Five constants were offered for the watch list, taken
        // from the reference tool: delveChestStarting, RunestoneTgtPrefix, TempleTgtPrefix,
        // DeliriumShardBossPath, LoathsomeMirePath. They belong to its Radar plugin, where they
        // are matched against ENTITY paths and terrain targets - a different subsystem reading a
        // different list. None of them appears in any of five captured areas, so adding them
        // would have produced five rows that could never match and never say why.
        string[] radar =
        [
            "Metadata/Chests/DelveChests/",
            "Metadata/Terrain/Leagues/Expedition/Tiles/CampaignRunes/",
            "Metadata/Terrain/Leagues/Incursion/Tiles/Features/Waygates/WaygateDevice",
        ];

        HashSet<string>? hideout = PreloadAlerts.Lookup(Hideout());
        HashSet<string>? map = PreloadAlerts.Lookup(Map());

        foreach (string path in radar)
        {
            Assert.False(PreloadAlerts.Here(path, hideout), path);
            Assert.False(PreloadAlerts.Here(path, map), path);
        }
    }

    [Fact]
    public void THETABLEOFSTRONGBOXESIsNotEvidenceOfOne()
    {
        // A DISPROOF WORTH KEEPING, because the path looks exactly like the answer and the next
        // person to read a capture will land on it too. Data/Balance/Strongboxes.dat was offered
        // here as a one-line marker on the strength of four captures. Twenty settled it: seven
        // areas load the table, ONE of them has a box. It is the game reading its own data file,
        // which it does whether or not the area rolled the thing the file describes.
        //
        // Both of these areas load it. Only one of them has a chest, and that is the difference
        // between a marker and a false positive six times in seven.
        const string Table = "Data/Balance/Strongboxes.dat";

        HashSet<string>? rich = PreloadAlerts.Lookup(Map());
        HashSet<string>? plain = PreloadAlerts.Lookup(Plain());

        Assert.True(PreloadAlerts.Here(Table, rich));
        Assert.True(PreloadAlerts.Here(Table, plain));

        Assert.True(PreloadAlerts.Here(TheChest, rich));
        Assert.False(PreloadAlerts.Here(TheChest, plain));
    }

    [Fact]
    public void ARowFiresOnANYOfItsPathsRatherThanAll()
    {
        // WHY A ROW HOLDS SEVERAL. Twenty captured areas say a mechanic is not one file: what a
        // Breach area loads depends on which breach monsters rolled, and across five Breach maps
        // the set of paths ALL of them share is empty. Same for Delirium, Abyss and Vaal. Only
        // Essence had a single file in all five of its maps.
        var breach = new PreloadAlertEntry(
            "Metadata/Terrain/Leagues/Breach/Doodads/TileableWall12.ao",
            "Breach",
            Also: ["Metadata/Terrain/Leagues/Breach/brequelportal.arm"]);

        // Only the SECOND path is in this area, which is the case a single-path row misses.
        string[] area = ["Metadata/Terrain/Leagues/Breach/brequelportal.arm", "Art/Models/Whatever.ast"];

        Assert.Single(PreloadAlerts.Found([breach], area));
        Assert.True(PreloadAlerts.Anywhere(breach, PreloadAlerts.Lookup(area)));

        // And an area with neither says nothing, so this is not a row that always fires.
        Assert.Empty(PreloadAlerts.Found([breach], ["Art/Models/Whatever.ast"]));
    }

    [Fact]
    public void ASavedRowKeepsItsExtraPaths()
    {
        // The file is the point of the feature - a list worth handing to somebody else - so the
        // extra paths have to survive the round trip rather than only living in memory.
        string file = Path.Combine(Path.GetTempPath(), $"preload-{Guid.NewGuid():N}.json");
        try
        {
            var entry = new PreloadAlertEntry("a/one", "Thing", Also: ["a/two", "a/three"]);
            Assert.True(PreloadAlertStore.Save([entry], file));

            IReadOnlyList<PreloadAlertEntry> back = PreloadAlertStore.Load(file);

            Assert.Single(back);
            Assert.Equal(["a/one", "a/two", "a/three"], back[0].Every);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void THESTARTERLISTIsQuietInAHideoutAndSpeaksInAMap()
    {
        // Built from twenty captures, and only from paths that carry their mechanic's own name -
        // see data/preload-alerts.starter.json. A first attempt let any path in and "covered" all
        // five Breach maps with a checkpoint model and a shield-block gem, which fits the sample
        // and predicts nothing.
        IReadOnlyList<PreloadAlertEntry> starter = PreloadAlertStore.Load(Starter());
        Assert.True(starter.Count >= 5, $"only {starter.Count} entries - is the file there?");

        // A hideout has none of it. If this ever fires, a path in the list is not mechanic-bound.
        Assert.Empty(PreloadAlerts.Found(starter, Hideout()));

        // THE CONTROL THAT ACTUALLY COSTS SOMETHING. A hideout is quiet for any list at all - it
        // loads almost nothing - so staying quiet there proves very little. This is a real map,
        // 850 files deep, that simply rolled none of the six; a path that merely correlates with
        // "being in a map" fires here and nowhere else that would catch it.
        Assert.Empty(PreloadAlerts.Found(starter, Plain()));

        IReadOnlyList<PreloadAlertEntry> found = PreloadAlerts.Found(starter, Map());
        var called = found.Select(entry => entry.Shown).ToHashSet(StringComparer.Ordinal);

        // The three this map carries in bulk - 143 Delirium paths, 203 Abyss, 98 Vaal.
        Assert.Contains("Delirium", called);
        Assert.Contains("Abyss", called);
        Assert.Contains("Vaal", called);
    }

    private static string Starter()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "data", "preload-alerts.starter.json")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, "data", "preload-alerts.starter.json");
    }

    [Fact]
    public void MostOfWhatAnAreaLoadsIsWhatEveryAreaLoads()
    {
        // Why a capture is read by diffing it against another one rather than by scrolling it.
        // The two areas here share hundreds of paths that say nothing about either.
        var hideout = new HashSet<string>(Hideout(), StringComparer.OrdinalIgnoreCase);
        var map = new HashSet<string>(Map(), StringComparer.OrdinalIgnoreCase);

        int shared = hideout.Count(map.Contains);

        Assert.True(shared > 200, $"only {shared} shared - are these really two PoE2 areas?");
        Assert.True(
            map.Count - shared > 1000,
            $"a map should carry far more of its own than it shares; {map.Count - shared}");
    }
}
