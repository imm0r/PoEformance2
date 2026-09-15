using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// What the game says a map's contents are, against the file that has been saying it.
/// </summary>
/// <remarks>
/// TWO HYPOTHESES WENT INTO ONE CAPTURE AND BOTH CAME BACK CONFIRMED, which has not happened often
/// enough in this project to leave unwritten.
///
/// A BADGE ID IS A CONTENT ROW PLUS 100. Predicted from two things that were already written down -
/// 67 of data/atlas-content.json's 69 badge ids are the consecutive run 0x64..0xA6, and
/// AtlasNode.BadgeVectorBegin says the badge vector holds one BYTE per badge whose content id is
/// "the row plus 100". Row 0 of EndgameMapContent is PowerfulMapBoss and badge 0x64 is "Powerful
/// Map Boss"; row 1 is Breach and 0x65 is "Breach"; 66 of the 67 agree on the name outright.
///
/// AN EFFECT ID IS A Stats.dat ROW INDEX. Predicted because the file's effect ids run 1240..26741
/// and Stats has 27281 rows, and confirmed the only way that can be confirmed: the SENTENCE the
/// game attaches to the stat is the sentence the file attaches to the id. It is not a coincidence
/// of ranges - 43 ids drawn at random from 27281 rows would be expected to hit the 83 these
/// contents grant about a tenth of the time, and this hits fourteen.
///
/// AND THE FILE IS WRONG IN WAYS THAT ONLY THE GAME COULD HAVE SHOWN. Its high effect ids are stale
/// by FOUR rows, so a Water biome reads as Swamp and a Mountain as Desert on the current client;
/// one badge carries another badge's text entirely; four sentences dropped the game's first line;
/// three contents are missing; and 53 of its icon names name no art the game has. Every one of
/// those is a thing the tool has been quietly showing.
/// </remarks>
public class EndgameMapContentSessionTests
{
    /// <summary>The capture taken with the walker in place. See EndgameMapSessionTests for the rest.</summary>
    private const string Capture = "session-2026-09-mapcontent.rec";

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

    private static AtlasContentNames File() => AtlasContentNames.Load(
        Path.Combine(Root.FullName, "data", "atlas-content.json"));

    /// <summary>The table, reached the way the tool reaches it: off a node, through the map walk.</summary>
    private static (EndgameMapContentCatalogue Contents, ReplayMemoryReader Replay) Walked()
    {
        ReplayMemoryReader replay = ReplayMemoryReader.Load(
            System.IO.File.OpenRead(Path.Combine(Root.FullName, "tests", "fixtures", Capture)));

        OffsetSchema schema = RealSessionTests.LiveSchema();
        GameChainAddresses chain = GameChain.Resolve(replay, schema, replay.ResolvedStatics["GameStates"]);
        var endgame = new EndgameMapCatalogue(replay, schema);
        var contents = new EndgameMapContentCatalogue(replay, schema);

        foreach (AtlasNode node in new AtlasReader(replay, schema, new UiElementReader(replay, schema))
            .Read(chain.UiRoot, new UiScale(3440, 1440, 0)))
        {
            if (node.MapId.Length > 0 && endgame.ReadFromNode(node.Address))
            {
                break;
            }
        }

        Assert.NotEqual(0UL, endgame.ContentTable);
        Assert.Equal("EndgameMaps.MapContent", endgame.ContentTableRoute);
        Assert.True(contents.Read(endgame.ContentTable), contents.LastError);
        return (contents, replay);
    }

    private static MapContentRow Row(EndgameMapContentCatalogue contents, long index)
        => Assert.Single(contents.Rows, row => row.Index == index);

    [Fact]
    public void TheTableNamesItselfAndEveryRowReads()
    {
        (EndgameMapContentCatalogue contents, ReplayMemoryReader replay) = Walked();
        using (replay)
        {
            DatTableFacts facts = Assert.IsType<DatTableFacts>(contents.Table);
            Assert.Equal("Data/Balance/EndgameMapContent.dat", facts.Path);
            Assert.Equal(70, facts.Rows);
            Assert.Equal(0x5D, facts.RowSize);
            Assert.Equal(70, contents.Rows.Count);
            Assert.Empty(contents.LastError);
        }
    }

    [Fact]
    public void ABadgeIdIsAContentRowPlus100()
    {
        // THE HYPOTHESIS, CONFIRMED. Not by the ids lining up - a consecutive run lines up with any
        // table of the right height - but by the WORDS: row 0 is the badge the file calls 0x64 and
        // it says the same thing, and so do 65 of the other 66.
        (EndgameMapContentCatalogue contents, ReplayMemoryReader replay) = Walked();
        using (replay)
        {
            AtlasContentNames file = File();
            Assert.Equal(100u, contents.BadgeIdBase);

            Assert.Equal("PowerfulMapBoss", Row(contents, 0).Id);
            Assert.Equal("Powerful Map Boss", Assert.NotNull(file.Badge(0x64)).Name);
            Assert.Equal("Breach", Row(contents, 1).Id);
            Assert.Equal("Breach", Assert.NotNull(file.Badge(0x65)).Name);

            MapContentRow[] reached =
                [.. contents.Rows.Where(row => file.Badge((uint)row.Index + contents.BadgeIdBase) is not null)];
            Assert.Equal(67, reached.Length);
            Assert.Equal(
                66,
                reached.Count(row => file.Badge((uint)row.Index + contents.BadgeIdBase)!.Value.Name == row.Name));
        }
    }

    [Fact]
    public void ANDTheONEBadgeThatDisagreesIsTheFileCarryingAnotherBadgesText()
    {
        // 0x8C is not a wording difference, it is the wrong content. The file gives it "Monstrous
        // Treasure" - which is what the game calls 0x71, character for character - while the game's
        // own 0x8C is "Behemoth's Bounty". So the tool has been naming a boss drop after a
        // strongbox, and no amount of reading the file could have shown it.
        (EndgameMapContentCatalogue contents, ReplayMemoryReader replay) = Walked();
        using (replay)
        {
            AtlasContentNames file = File();

            Assert.Equal("Behemoth's Bounty", Row(contents, 0x8C - 100).Name);
            Assert.Equal("Monstrous Treasure", Assert.NotNull(file.Badge(0x8C)).Name);
            Assert.Equal("Monstrous Treasure", Assert.NotNull(file.Badge(0x71)).Name);
            Assert.Equal(
                Assert.NotNull(file.Badge(0x71)).Description,
                Assert.NotNull(file.Badge(0x8C)).Description);

            // And the game's 0x71 is the one the file put in both places, so the duplicate is the
            // file's and not the game's.
            Assert.Equal("Monstrous Treasure", Row(contents, 0x71 - 100).Name);
        }
    }

    [Fact]
    public void THEGameHasThreeContentsTheFileHasNeverHeardOf()
    {
        (EndgameMapContentCatalogue contents, ReplayMemoryReader replay) = Walked();
        using (replay)
        {
            AtlasContentNames file = File();
            MapContentRow[] unknown =
                [.. contents.Rows.Where(row => file.Badge((uint)row.Index + contents.BadgeIdBase) is null)];

            Assert.Equal(["AbyssDepths", "AbyssFissure", "Wildwood"], unknown.Select(row => row.Id));
            Assert.Equal([0xA7, 0xA8, 0xA9], unknown.Select(row => row.Index + contents.BadgeIdBase));
        }
    }

    [Fact]
    public void ANDFourOfItsSentencesThrewAwayTheGamesFIRSTLine()
    {
        // The game writes a content's clauses on separate lines. The published copies joined them
        // with a space, and on four of them dropped the opening clause outright - so "Contains 2
        // additional Shrines / Shrines release an Azmeri Spirit" ships as the second half alone.
        // Not a translation difference: the words that survive are identical.
        (EndgameMapContentCatalogue contents, ReplayMemoryReader replay) = Walked();
        using (replay)
        {
            AtlasContentNames file = File();
            foreach (long index in (long[])[17, 19, 27, 32])
            {
                MapContentRow row = Row(contents, index);
                string game = EndgameMapContentCatalogue.AsTheFileWouldWriteIt(row.Description);
                string shipped = Assert.NotNull(file.Badge((uint)index + contents.BadgeIdBase)).Description;

                // The shipped text is a PART of the game's, word for word - so nothing was reworded,
                // a clause was dropped. Three of the four keep the last clause and ShrineElementalBonus
                // keeps the middle one, which is why this is Contains rather than EndsWith.
                Assert.NotEqual(game, shipped);
                Assert.Contains(shipped, game, StringComparison.Ordinal);
                Assert.Contains("\n", row.Description, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void ANEffectIdIsAStatsRowIndex()
    {
        // THE SECOND HYPOTHESIS, and it is settled by MEANING rather than by range. Every one of
        // these is a stat one of these contents grants, and the file's sentence for the id is the
        // content's own sentence - which a coincidence of two numeric ranges cannot produce.
        (EndgameMapContentCatalogue contents, ReplayMemoryReader replay) = Walked();
        using (replay)
        {
            AtlasContentNames file = File();
            Assert.Equal("Data/Balance/Stats.dat", Assert.IsType<DatTableFacts>(contents.StatsTable).Path);

            foreach ((uint id, string content) in ((uint, string)[])
                [(4731, "BossUniqueItem"), (12815, "MagicMonsters")])
            {
                MapContentRow row = Assert.Single(contents.Rows, row => row.Id == content);
                Assert.Contains(id, row.Stats.Select(stat => (uint)stat));
                Assert.Equal(
                    EndgameMapContentCatalogue.AsTheFileWouldWriteIt(row.Description),
                    Assert.NotNull(file.Effect(id)).Description);
            }

            // AND WHERE A NUMBER APPEARS, THE FILE HOLDS THE PLACEHOLDER AND THE GAME THE NUMBER.
            // That is the difference between a CONTENT, which is one map's worth of a thing, and
            // the effect a node rolls, whose magnitude travels in the top half of the token - so
            // the file's "{0}" is not a wording difference, it is the same sentence one level up.
            MapContentRow corrupted = Assert.Single(contents.Rows, row => row.Id == "CorruptionRandomArea");
            Assert.Contains(4738L, corrupted.Stats);
            Assert.Equal(
                "Area has 2 additional random Waystone Modifiers",
                EndgameMapContentCatalogue.AsTheFileWouldWriteIt(corrupted.Description));
            Assert.Equal("Area has {0} additional random Waystone Modifiers", Assert.NotNull(file.Effect(4738)).Description);
        }
    }

    [Fact]
    public void BUTTheFilesHighEffectIdsAreStaleByFourRows()
    {
        // WHAT A STALE INDEX LOOKS LIKE, and why this is a live fault rather than a curiosity. A
        // stat id is a POSITION in Stats.dat, so rows inserted above one move it. Somewhere between
        // 19545 and 24104 the game gained four rows, and the file kept the old numbers - so the
        // biome effects are all four too low.
        //
        // The consequence is exact and user-visible: a node's token for the WATER biome is 25861 on
        // this client, the file says 25861 is "Also counts as a Swamp Area", and the tool shows
        // Swamp. Mountain shows as Desert. The other four biomes are not in the file at all.
        (EndgameMapContentCatalogue contents, ReplayMemoryReader replay) = Walked();
        using (replay)
        {
            AtlasContentNames file = File();
            (string Content, uint Stat, string Biome)[] biomes =
            [
                ("WaterBiome", 25861, "Water"),
                ("MountainBiome", 25862, "Mountain"),
                ("GrassBiome", 25863, "Grass"),
                ("ForestBiome", 25864, "Forest"),
                ("SwampBiome", 25865, "Swamp"),
                ("DesertBiome", 25866, "Desert"),
            ];

            foreach ((string content, uint stat, string biome) in biomes)
            {
                MapContentRow row = Assert.Single(contents.Rows, row => row.Id == content);
                Assert.Equal([(long)stat], row.Stats);
                Assert.Equal($"Also counts as a {biome} Area", EndgameMapContentCatalogue.AsTheFileWouldWriteIt(row.Description));

                // The file has the same sentence FOUR rows lower - for five of the six. Water falls
                // off the bottom of its run entirely, which is the other half of the same drift:
                // the file's biome block is 25858..25862 where the game's is 25861..25866.
                Assert.Equal(
                    biome == "Water" ? null : $"Also counts as a {biome} Area",
                    file.Effect(stat - 4)?.Description);
            }

            // And what the tool shows today for the two the file does reach unshifted: the wrong
            // biome. The remaining four are not in the file at any id, so they show as nothing.
            Assert.Equal("Also counts as a Swamp Area", Assert.NotNull(file.Effect(25861)).Description);
            Assert.Equal("Also counts as a Desert Area", Assert.NotNull(file.Effect(25862)).Description);
            Assert.Null(file.Effect(25863));
            Assert.Null(file.Effect(25864));
            Assert.Null(file.Effect(25865));
            Assert.Null(file.Effect(25866));
        }
    }

    [Fact]
    public void ANDTheDriftIsFoundByLookingRatherThanByBeingTold()
    {
        // The report has to say this by itself, because the next client will drift again and nobody
        // will be looking for it. Trying a window of shifts turns "9 of 43 match, which could be
        // chance" into "14 match at +4, which is not" - and a wrong shift finds fewer, not more.
        (EndgameMapContentCatalogue contents, ReplayMemoryReader replay) = Walked();
        using (replay)
        {
            string[] said = [.. contents.Describe(File())];
            Assert.Contains(said, line => line.Contains("shifted by +4", StringComparison.Ordinal));
            Assert.Contains(said, line => line.Contains("9 of the file's effect ids", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void THEIconTheFileShipsIsTheArtPathsLastSegment()
    {
        // Which settles where that column came from and closes the gap this project wrote down: the
        // game holds "Art/2DArt/.../AtlasIconContentBreach" and every published copy kept the last
        // word of it. The art ROW calls itself something else again - "Breach" - so the file's name
        // is the path's tail rather than the row's id.
        (EndgameMapContentCatalogue contents, ReplayMemoryReader replay) = Walked();
        using (replay)
        {
            AtlasContentNames file = File();
            MapContentRow breach = Assert.Single(contents.Rows, row => row.Id == "Breach");

            Assert.Equal("Art/2DArt/UIImages/InGame/AtlasScreen/AtlasIconContent/AtlasIconContentBreach", breach.IconPath);
            Assert.Equal("AtlasIconContentBreach", EndgameMapContentCatalogue.ArtName(breach.IconPath));
            Assert.Equal("AtlasIconContentBreach", Assert.NotNull(file.Badge(0x65)).Icon);
            Assert.Equal("Breach", breach.Icon);

            MapContentRow[] pathed = [.. contents.Rows.Where(row => row.IconPath.Length > 0)];
            Assert.Equal(16, pathed.Length);
            Assert.Equal(
                13,
                pathed.Count(row => file.Badge((uint)row.Index + contents.BadgeIdBase) is { } badge
                    && badge.Icon == EndgameMapContentCatalogue.ArtName(row.IconPath)));
        }
    }

    [Fact]
    public void ANDTheOtherFiftyThreeIconNamesNameNoArtTheGameHas()
    {
        // The rows the game gives NO picture for are named anyway in the file - "AtlasMasteryBiome"
        // for a quest area, "Hunter" for the Rogue Exile hunting grounds - and those are PoE1 atlas
        // words. So that column is two different things: thirteen real art names and fifty-three
        // inherited from somewhere else entirely.
        (EndgameMapContentCatalogue contents, ReplayMemoryReader replay) = Walked();
        using (replay)
        {
            AtlasContentNames file = File();
            MapContentRow[] invented =
            [
                .. contents.Rows.Where(row => row.IconPath.Length == 0
                    && file.Badge((uint)row.Index + contents.BadgeIdBase) is { Icon.Length: > 0 }),
            ];

            Assert.Equal(53, invented.Length);
            Assert.Equal("AtlasMasteryBiome", Assert.NotNull(file.Badge(0x6D)).Icon);
            Assert.Equal("QuestArea", Row(contents, 0x6D - 100).Id);
        }
    }

    [Fact]
    public void AStringIsReadInStepsSoALongOneDoesNotCOMEBACKSHORTER()
    {
        // THE TRAP, pinned because it produced a wrong FINDING rather than a failed read.
        // ReadUnicodeString halves its request until a read succeeds, so against a replay holding
        // 160 characters, asking for 512 gets 128 - the bigger ask returns a third less text, and a
        // description cut mid-markup compares unequal to the file and reads as the game disagreeing.
        //
        // Two rows here are longer than this capture recorded. They must come back at the full 160
        // the recording holds, not at the 128 a single oversized request would have yielded.
        (EndgameMapContentCatalogue contents, ReplayMemoryReader replay) = Walked();
        using (replay)
        {
            foreach (long index in (long[])[31, 46])
            {
                Assert.Equal(160, Row(contents, index).Description.Length);
            }

            // And everything else is whole - no row comes back cut at a step boundary.
            Assert.Equal(68, contents.Rows.Count(row => row.Description.Length is 0 or < 160));
        }
    }
}
