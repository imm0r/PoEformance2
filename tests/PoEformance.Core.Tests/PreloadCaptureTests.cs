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
