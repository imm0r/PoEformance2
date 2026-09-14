using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// The WHOLE WorldAreas table, read out of a REAL 0.5.5 session
/// (<c>tests/fixtures/session-2026-09-catalogue.rec</c>, 2026-09-14).
/// </summary>
/// <remarks>
/// THE CAPTURE THAT ANSWERS WHETHER data/atlas-maps.json CAN GO AWAY. The earlier one sampled six
/// maps and could only say that the columns are reachable; this one walks all 442 rows, so every
/// claim the file makes can be checked against the table rather than against six of it.
///
/// The answer is: names yes, the unique flag ALMOST, the tags NO.
///
/// - 442 of 442 rows decode, which is the whole table and two more than the file's 440 entries.
/// - IsHideout IS the hideout column, and this is the reading that settles the owner's report
///   that it "does not work". The claimable-hideout MAP reads IsMapArea and not IsHideout, while
///   the hideout it grants is a SEPARATE ROW that reads the other way round. Both are in the
///   table, both are called "Canal Hideout", and only the ids tell them apart - which is exactly
///   what the earlier capture guessed and could not prove.
/// - IsUniqueMapArea and the file disagree on SIX ids, so it is not quite the column the file's
///   type: unique was built from. Counted rather than waved away, because it is the one column
///   a switch to memory would silently change.
/// - the tag VOCABULARIES barely overlap. The game's 17 words are mechanical - map, biome,
///   dungeon, pinnacle_boss - and the file's grouping words (expedition, arbiter, quest, boss,
///   lineage, traverse, breach, craft, ritual) are in none of them. The file cannot be deleted
///   on the strength of this table; it can only be reduced to the words the game does not say.
/// </remarks>
public class WorldAreaCatalogueSessionTests
{
    private static DirectoryInfo Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return dir;
        }
    }

    private static ReplayMemoryReader Load()
        => ReplayMemoryReader.Load(File.OpenRead(
            Path.Combine(Root.FullName, "tests", "fixtures", "session-2026-09-catalogue.rec")));

    /// <summary>The file this tool ships, read the way the app reads it.</summary>
    private static AtlasMapNames Curated()
        => AtlasMapNames.Load(Path.Combine(Root.FullName, "data", "atlas-maps.json"));

    /// <summary>
    /// The catalogue, reached the only way there is: through an atlas node that names the table.
    /// </summary>
    private static WorldAreaCatalogue Read(ReplayMemoryReader replay)
    {
        OffsetSchema schema = RealSessionTests.LiveSchema();
        GameChainAddresses chain = GameChain.Resolve(replay, schema, replay.ResolvedStatics["GameStates"]);
        Assert.Equal(GameStateKind.InGame, chain.State);

        var elements = new UiElementReader(replay, schema);
        List<AtlasNode> nodes = new AtlasReader(replay, schema, elements).Read(chain.UiRoot, new UiScale(3440, 1440, 0));
        Assert.True(nodes.Count > 100, $"only {nodes.Count} nodes read off the panel");

        var catalogue = new WorldAreaCatalogue(replay, schema);
        foreach (AtlasNode node in nodes)
        {
            if (node.MapId.Length > 0 && catalogue.ReadFromNode(node.Address))
            {
                break;
            }
        }

        Assert.NotNull(catalogue.Table);
        return catalogue;
    }

    [Fact]
    public void EveryRowOfTheTableDecodes()
    {
        // 442 of 442 rather than "most of them". A stride that is off by anything reads some rows
        // and drops the rest, so the two numbers matching is the check that the walk is aligned.
        using ReplayMemoryReader replay = Load();
        WorldAreaCatalogue catalogue = Read(replay);

        Assert.Equal(442, catalogue.Table!.Rows);
        Assert.Equal(0x2E0, catalogue.Table.RowSize);
        Assert.Equal(442, catalogue.All.Count);
        Assert.Equal(string.Empty, catalogue.LastError);

        Assert.Contains(
            "WORLDAREAS CATALOGUE - \"Data/Balance/WorldAreas.dat\", 442 rows of 0x2E0, 442 read",
            string.Join('\n', catalogue.Describe(Curated())),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheHideoutColumnWorksAndTheClaimableMapIsWhatMadeItLookOtherwise()
    {
        // THE READING THE OWNER ASKED FOR. Reported from a live run as "IsHideout does not work",
        // on the evidence that the hideout maps on the atlas all read 0 - and they do, because an
        // atlas node is never a hideout. The claimable is a MAP that grants one; the hideout is a
        // row of its own that no node points at. Same display name, opposite flags, and the pair
        // is only visible from a walk of the whole table.
        using ReplayMemoryReader replay = Load();
        WorldAreaCatalogue catalogue = Read(replay);

        WorldArea claimable = Assert.IsType<WorldArea>(catalogue.Of("MapHideoutCanal_Claimable"));
        WorldArea hideout = Assert.IsType<WorldArea>(catalogue.Of("HideoutCanal"));

        Assert.Equal("Canal Hideout", claimable.Name);
        Assert.True(claimable.IsMapArea);
        Assert.False(claimable.IsHideout);

        Assert.Equal("Canal Hideout", hideout.Name);
        Assert.False(hideout.IsMapArea);
        Assert.True(hideout.IsHideout);

        // And it is not one lucky row: the table is a third hideouts, which is what a game with a
        // hideout per tileset looks like and what a misplaced byte would not produce.
        Assert.Equal(83, catalogue.All.Values.Count(area => area.IsHideout));
        Assert.Equal(190, catalogue.All.Values.Count(area => area.IsMapArea));
        Assert.Equal(25, catalogue.All.Values.Count(area => area.IsUnique));
        Assert.DoesNotContain(catalogue.All.Values, area => area.IsMapArea && area.IsHideout);
    }

    [Fact]
    public void TheUniqueFlagIsNotQuiteWhatTheFileCallsUnique()
    {
        // Six ids, both directions, and the direction is the interesting half: four areas the
        // game marks unique are ordinary in the file (three of them league bosses, which the file
        // groups by tag instead), and two the file marks unique are ordinary in the game. THE GAME
        // IS THE SOURCE NOW - AtlasMapNames.LearnUnique - so this pins which maps that moved, and
        // a client where the list stops being these six is a client where something changed.
        using ReplayMemoryReader replay = Load();
        WorldAreaCatalogue catalogue = Read(replay);
        AtlasMapNames file = Curated();

        List<string> differs = [.. file.All
            .Where(pair => catalogue.Of(pair.Key) is { } area && area.IsUnique != pair.Value.Unique)
            .Select(pair => pair.Key)
            .Order(StringComparer.Ordinal)];

        Assert.Equal(
            ["ExpeditionLeagueBoss", "MapUniqueInitialTower", "MapUniqueReactor_04", "MapVoidReliquary", "Map_HildaCampsite", "RitualLeagueBoss"],
            differs);

        Assert.True(Assert.IsType<WorldArea>(catalogue.Of("ExpeditionLeagueBoss")).IsUnique);
        Assert.False(Assert.IsType<WorldArea>(catalogue.Of("MapUniqueInitialTower")).IsUnique);

        Assert.Contains(
            "the unique flag differs on 6",
            string.Join('\n', catalogue.Describe(file)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheGamesTagsAreMechanicalAndTheFilesGroupingWordsAreInNoneOfThem()
    {
        // What keeps data/atlas-maps.json alive. The table's vocabulary is 17 words about terrain
        // and content; the file's is about how a person plans an atlas. Neither is derivable from
        // the other, so this test is the shape of the answer rather than a count.
        using ReplayMemoryReader replay = Load();
        WorldAreaCatalogue catalogue = Read(replay);

        HashSet<string> vocabulary = [.. catalogue.All.Values.SelectMany(area => area.Tags)];
        Assert.Equal(17, vocabulary.Count);
        Assert.Contains("map", vocabulary);
        Assert.Contains("map_tower", vocabulary);
        Assert.Contains("dungeon", vocabulary);
        Assert.Contains("pinnacle_boss", vocabulary);
        Assert.Contains("swamp_biome", vocabulary);
        Assert.Contains("has_forest_biome_monsters", vocabulary);

        foreach (string curated in new[] { "expedition", "arbiter", "quest", "boss", "lineage", "traverse", "breach", "craft", "ritual", "tower", "hideout" })
        {
            Assert.DoesNotContain(curated, vocabulary);
        }

        // 153 of 442 rows carry "map" - so the tag is not a stand-in for IsMapArea (190) either.
        Assert.Equal(153, catalogue.All.Values.Count(area => area.Tags.Contains("map")));
    }

    [Fact]
    public void ANDMapTowerIsNotTheFilesTowerEither()
    {
        // The nearest thing to an overlap, and it still is not one. Four maps the game calls
        // map_tower are untagged in the file, and MapPrecursorTowerMountain - a Precursor Tower
        // by name - carries mountain_biome and no map_tower at all. Deriving "tower" from the
        // table would therefore both add and drop maps, which is the finding.
        using ReplayMemoryReader replay = Load();
        WorldAreaCatalogue catalogue = Read(replay);
        AtlasMapNames file = Curated();

        HashSet<string> said = [.. catalogue.All.Values.Where(area => area.Tags.Contains("map_tower")).Select(area => area.Id)];
        HashSet<string> written = [.. file.All.Where(pair => pair.Value.Tagged("tower")).Select(pair => pair.Key)];

        Assert.Equal(11, said.Count);
        Assert.Equal(8, written.Count);
        Assert.Equal(
            ["MapAlpineRidge", "MapBluff", "MapMesa", "MapSwampTower"],
            said.Except(written).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(["MapPrecursorTowerMountain"], written.Except(said).ToArray());
    }

    [Fact]
    public void TheNamesAgreeWithTheFileExceptForOneTheGameSpellsWithATrailingSpace()
    {
        // The column the switch to memory is actually FOR - on a German client the file's English
        // names are wrong on every row, and this one reads whatever the client says. On this
        // English capture it agrees with the file 439 times out of 440, and the one exception is
        // not a disagreement about the name: the game's own string ends in a space. Anything that
        // reads names from here trims them.
        using ReplayMemoryReader replay = Load();
        WorldAreaCatalogue catalogue = Read(replay);
        AtlasMapNames file = Curated();

        List<string> differs = [.. file.All
            .Where(pair => catalogue.Of(pair.Key) is { } area
                && pair.Value.Name.Length > 0
                && !string.Equals(pair.Value.Name, area.Name, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Key)];

        Assert.Equal(["P2_3"], differs);
        Assert.Equal("Sel Khari Sanctuary ", Assert.IsType<WorldArea>(catalogue.Of("P2_3")).Name);
        Assert.Equal("Sel Khari Sanctuary", catalogue.Of("P2_3")!.Name.Trim());

        // Two ids the file carries that the table does not - showcase areas pulled since the file
        // was extracted. Nothing on an atlas points at them, so nothing loses a name.
        List<string> missing = [.. file.All.Where(pair => catalogue.Of(pair.Key) is null).Select(pair => pair.Key).Order(StringComparer.Ordinal)];
        Assert.Equal(["KaruiBossShowcase", "KaruiShowcase"], missing);
    }
}
