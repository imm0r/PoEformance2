using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Which boss the game says stands in an area - WorldAreas' Bosses column, as a file.
/// </summary>
/// <remarks>
/// THE TEST THAT MATTERS IS AGAINST THE SHIPPED FILE, at the bottom: the table is only worth
/// having if it answers for the maps somebody is actually going to play, and the numbers there
/// are what the feature was built on. The rest checks the arrangement that lets the game
/// correct it, which is the part that has to survive a patch.
/// </remarks>
public class AreaBossesTests
{
    private static string Beside(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine([dir.FullName, .. parts])))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine([dir!.FullName, .. parts]);
    }

    private static AreaBosses Written(string areas)
    {
        string path = Path.Combine(Path.GetTempPath(), $"area-bosses-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{\"source\": \"a test\", \"areas\": {" + areas + "}}");
        try
        {
            return AreaBosses.Load(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnAreaAnswersWithItsBossesInTheTablesOwnOrder()
    {
        AreaBosses bosses = Written(
            """
            "MapGrimhaven": ["Metadata/Monsters/WifeMonster/WifeMonsterMap_"],
            "MapUberBoss_IronCitadel": [
                "Metadata/Monsters/Baron/BaronBossHumanFormMap",
                "Metadata/Monsters/Baron/BaronBossCorruptedWolfFormMap"
            ]
            """);

        Assert.Equal(["Metadata/Monsters/WifeMonster/WifeMonsterMap_"], bosses.Of("MapGrimhaven"));

        // Two forms of one boss. The order is the table's, because the first is what gets
        // chosen where one has to be.
        Assert.Equal(2, bosses.Of("MapUberBoss_IronCitadel").Count);
        Assert.Equal("Metadata/Monsters/Baron/BaronBossHumanFormMap", bosses.Of("MapUberBoss_IronCitadel")[0]);

        // Area ids come out of memory and were typed into a file, so the two have to meet.
        Assert.Single(bosses.Of("mapgrimhaven"));

        Assert.Empty(bosses.Of("MapAugury"));
        Assert.Empty(bosses.Of(null));
    }

    [Fact]
    public void AMissingOrBrokenFileCostsTheTableAndNothingElse()
    {
        Assert.Equal(0, AreaBosses.Load(null).Count);
        Assert.Equal(0, AreaBosses.Load(Path.Combine(Path.GetTempPath(), $"no-{Guid.NewGuid():N}.json")).Count);

        string path = Path.Combine(Path.GetTempPath(), $"area-bosses-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ this is not json");
        try
        {
            Assert.Equal(0, AreaBosses.Load(path).Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheGameCorrectsTheFileAndSaysSo()
    {
        AreaBosses bosses = Written("""  "MapBluff": ["Metadata/Monsters/Old/OldBoss"]  """);
        int was = bosses.Revision;

        int moved = bosses.Learn(new Dictionary<string, IReadOnlyList<string>>
        {
            ["MapBluff"] = ["Metadata/Monsters/New/NewBoss"],
            ["MapBrandNew"] = ["Metadata/Monsters/New/AnotherBoss"],
        });

        Assert.Equal(2, moved);
        Assert.NotEqual(was, bosses.Revision);
        Assert.Equal("Metadata/Monsters/New/NewBoss", bosses.Of("MapBluff")[0]);
        Assert.Single(bosses.Of("MapBrandNew"));

        // The FILE's count never moves - it is what shipped, and the report shows both.
        Assert.Equal(1, bosses.Count);
        Assert.Equal(2, bosses.Live);
    }

    [Fact]
    public void AnAreaTheGameSaysHasNoBossLosesTheOneTheFileGaveIt()
    {
        // The case the file gets wrong after a patch: a boss that was removed. An empty list
        // from the game has to beat a filled one from the file, or the marker keeps naming a
        // monster that is not there any more.
        AreaBosses bosses = Written("""  "MapBluff": ["Metadata/Monsters/Old/OldBoss"]  """);

        Assert.Equal(1, bosses.Learn(new Dictionary<string, IReadOnlyList<string>>
        {
            ["MapBluff"] = [],
        }));

        Assert.Empty(bosses.Of("MapBluff"));
    }

    [Fact]
    public void ANothingReadChangesNothing()
    {
        AreaBosses bosses = Written("""  "MapBluff": ["Metadata/Monsters/Old/OldBoss"]  """);
        int was = bosses.Revision;

        // A walk that reached no areas is not a client with no bosses.
        Assert.Equal(0, bosses.Learn(new Dictionary<string, IReadOnlyList<string>>()));
        Assert.Equal(was, bosses.Revision);
        Assert.Single(bosses.Of("MapBluff"));
    }

    [Fact]
    public void EmptyIsNotShared()
    {
        // Teaching one must not teach every other holder of it, tests included.
        AreaBosses one = AreaBosses.Empty;
        one.Learn(new Dictionary<string, IReadOnlyList<string>> { ["MapBluff"] = ["Metadata/X"] });

        Assert.Empty(AreaBosses.Empty.Of("MapBluff"));
    }

    /// <summary>
    /// What the shipped table actually answers for, which is the whole reason it is shipped.
    /// </summary>
    /// <remarks>
    /// Exact numbers, because every one of them is a claim about the data: 206 areas name a
    /// boss, 182 distinct monsters, and on the atlas's own 173 maps it is 125 named and 48 not.
    /// A regeneration against a later patch that moves these should fail here and be written
    /// down rather than pass quietly.
    /// </remarks>
    [Fact]
    public void TheShippedTableNamesTheBossOf125AtlasMaps()
    {
        AreaBosses bosses = AreaBosses.Load(Beside("data", "area-bosses.json"));
        Assert.Equal(206, bosses.Count);

        AtlasMapNames maps = AtlasMapNames.Load(Beside("data", "atlas-maps.json"));
        int named = maps.All.Keys.Count(id => bosses.Of(id).Count > 0);
        Assert.Equal(125, named);
        Assert.Equal(48, maps.Count - named);

        // The case that started this: one boss, two maps, and no way to know from either map.
        Assert.Equal(bosses.Of("MapGrimhaven"), bosses.Of("MapEpitaph"));
        Assert.Equal("Metadata/Monsters/WifeMonster/WifeMonsterMap_", bosses.Of("MapEpitaph")[0]);

        // And the correction that cost a default: a Precursor tower has two guardians, so
        // "a tower has no boss" was wrong.
        Assert.Equal(2, bosses.Of("MapPrecursorTowerDesert").Count);
    }
}
