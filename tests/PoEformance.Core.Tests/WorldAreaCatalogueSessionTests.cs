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
/// - 442 of 442 rows decode, which is the whole table. The file is now 173 entries, because it was
///   cut to the maps EndgameMaps.dat says the atlas can actually hold; this table is every AREA in
///   the game, which is what made "atlas-maps" a misnomer for as long as it was a copy of it.
/// - IsHideout IS the hideout column, and this is the reading that settles the owner's report
///   that it "does not work". The claimable-hideout MAP reads IsMapArea and not IsHideout, while
///   the hideout it grants is a SEPARATE ROW that reads the other way round. Both are in the
///   table, both are called "Canal Hideout", and only the ids tell them apart - which is exactly
///   what the earlier capture guessed and could not prove.
/// - IsUniqueMapArea and the file disagree on FIVE of the atlas's maps, so it is not quite the
///   column the file's type: unique was built from - and the GAME now wins. Counted rather than
///   waved away, because it is the one column a switch to memory silently changed.
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
        // Five ids, both directions, and the direction is the interesting half: three areas the
        // game marks unique are ordinary in the file, and two the file marks unique are ordinary
        // in the game. THE GAME IS THE SOURCE NOW - AtlasMapNames.LearnUnique - so this pins which
        // maps moved, and a client where the list stops being these five is one where something
        // changed.
        //
        // IT WAS SIX. ExpeditionLeagueBoss was the sixth and is not in EndgameMaps.dat, so it left
        // with the other 266 when the file was cut to the maps the atlas can hold. Nothing about
        // the game changed; the file stopped making a claim about an area no atlas node carries.
        using ReplayMemoryReader replay = Load();
        WorldAreaCatalogue catalogue = Read(replay);
        AtlasMapNames file = Curated();

        List<string> differs = [.. file.All
            .Where(pair => catalogue.Of(pair.Key) is { } area && area.IsUnique != pair.Value.Unique)
            .Select(pair => pair.Key)
            .Order(StringComparer.Ordinal)];

        Assert.Equal(
            ["MapUniqueInitialTower", "MapUniqueReactor_04", "MapVoidReliquary", "Map_HildaCampsite", "RitualLeagueBoss"],
            differs);

        // The sixth is still in the TABLE and still unique there - only the file's claim about it
        // went away, which is what says the cut removed an entry rather than a fact.
        Assert.True(Assert.IsType<WorldArea>(catalogue.Of("ExpeditionLeagueBoss")).IsUnique);
        Assert.False(Assert.IsType<WorldArea>(catalogue.Of("MapUniqueInitialTower")).IsUnique);

        Assert.Contains(
            "the unique flag differs on 5",
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
        Assert.Equal(7, written.Count);

        // MapPrecursorTower joined the four the table tags and the file does not, for a different
        // reason than they have: it is the template row the five biome variants roll from, it is
        // not in EndgameMaps.dat, and so it went when the file was cut to the atlas's own maps.
        Assert.Equal(
            ["MapAlpineRidge", "MapBluff", "MapMesa", "MapPrecursorTower", "MapSwampTower"],
            said.Except(written).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(["MapPrecursorTowerMountain"], written.Except(said).ToArray());
    }

    [Fact]
    public void TheNamesAgreeWithTheFileExceptForOneTheGameSpellsWithATrailingSpace()
    {
        // The column the switch to memory is actually FOR - on a German client the file's English
        // names are wrong on every row, and this one reads whatever the client says. On this
        // English capture the two now agree on ALL 173, and the file no longer carries an id the
        // table has never heard of.
        //
        // BOTH NUMBERS IMPROVED BY SUBTRACTION, which is worth saying so nobody reads it as a fix.
        // The file used to be 440 entries and disagreed on P2_3 while carrying KaruiBossShowcase
        // and KaruiShowcase, which the table does not have. All three were campaign or showcase
        // areas; cutting the file to the 173 maps the atlas can actually hold took them with it.
        using ReplayMemoryReader replay = Load();
        WorldAreaCatalogue catalogue = Read(replay);
        AtlasMapNames file = Curated();

        List<string> differs = [.. file.All
            .Where(pair => catalogue.Of(pair.Key) is { } area
                && pair.Value.Name.Length > 0
                && !string.Equals(pair.Value.Name, area.Name, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Key)];

        Assert.Equal(173, file.Count);
        Assert.Empty(differs);
        Assert.DoesNotContain(file.All, pair => catalogue.Of(pair.Key) is null);

        // THE TRAILING SPACE IS STILL REAL and still has to be trimmed by anything reading names
        // from here - it is simply no longer visible through the file, because P2_3 is a campaign
        // zone. Asserted off the TABLE so the knowledge does not leave with the entry.
        Assert.Equal("Sel Khari Sanctuary ", Assert.IsType<WorldArea>(catalogue.Of("P2_3")).Name);
        Assert.Equal("Sel Khari Sanctuary", catalogue.Of("P2_3")!.Name.Trim());
    }
}
