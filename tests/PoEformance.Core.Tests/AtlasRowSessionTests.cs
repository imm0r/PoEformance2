using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Diagnostics;
using PoEformance.Game.Ui;

namespace PoEformance.Core.Tests;

/// <summary>
/// What hangs off every atlas node, from the FIRST capture that swept it
/// (<c>tests/fixtures/session-2026-09-atlasrows.rec</c>, 2026-09-14).
/// </summary>
/// <remarks>
/// EVERYTHING BELIEVED ABOUT AtlasNodeData.AtlasRowPtr RESTED ON ONE ROW until this capture, and
/// that row was misleading: MapRugosa's position holds AtlasOutsideFortressPath72, a PATH node
/// whose name is a [DNT] placeholder, which made the whole column look like engine bookkeeping.
/// 547 rows say otherwise.
///
/// WHAT THE COLUMN IS. Each row's Passives id names what that node IS in the atlas's own
/// structure, and the families are the atlas laid out: AtlasInsideFortressWhite (100),
/// -Red (70), -YellowLeft and -YellowRight (50 each) are the TIER REGIONS; AtlasOutsideFortressPath
/// (66) is the filler between them; AtlasRedGate / AtlasWhiteGate / AtlasYellowGate and the
/// matching Locks are the progression gates; AtlasWallTower (8) and AtlasOuterTower are towers;
/// AtlasLeague{Abyss,Breach,Delirium,Incursion}{Inner,Outer,Crack,District,Hub}Node is league
/// content; AtlasNormalMapsWith{Abyss,Breach,Delirium,Incursion,Ritual} are ordinary maps hosting
/// a mechanic; AtlasQuest* are the quest positions. That is a richer description of a node than
/// anything data/atlas-maps.json carries, and it comes from the game.
///
/// A ROW BELONGS TO ONE NODE. 547 rows for 547 nodes, not one shared - so this is not a position
/// definition several maps draw from. And 80 map ids appear BOTH with a row and without one, which
/// settles it further: whether a node has a row is a property of the node, never of the map.
///
/// IT IS NOT UNIVERSAL: 547 of 2517 nodes. A column that reads on a fifth of the atlas is a
/// different fact from one that reads everywhere, and the sweep exists to have measured that
/// rather than assumed either way.
///
/// WHAT THIS CAPTURE COULD NOT BRING BACK are the sentences: it holds every objective and blocked
/// message ID and not one of the strings behind them, because the build that made it read the
/// texts only for six rows chosen arbitrarily - and those six were path positions carrying none.
/// That is fixed in the probe (the texts are read wherever the column is set, cached by row), so
/// the NEXT capture has them. The ids are pinned here meanwhile, and they are self-describing
/// enough to be useful on their own.
/// </remarks>
public class AtlasRowSessionTests
{
    private static string FixturePath
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return Path.Combine(dir.FullName, "tests", "fixtures", "session-2026-09-atlasrows.rec");
        }
    }

    private static ReplayMemoryReader Load() => ReplayMemoryReader.Load(File.OpenRead(FixturePath));

    private static string Probe(ReplayMemoryReader replay)
    {
        OffsetSchema schema = RealSessionTests.LiveSchema();
        GameChainAddresses chain = GameChain.Resolve(replay, schema, replay.ResolvedStatics["GameStates"]);
        Assert.Equal(GameStateKind.InGame, chain.State);

        List<AtlasNode> nodes = new AtlasReader(replay, schema, new UiElementReader(replay, schema))
            .Read(chain.UiRoot, new UiScale(3440, 1440, 0));
        Assert.True(nodes.Count > 1000, $"only {nodes.Count} nodes read off the panel");

        return string.Join('\n', new AtlasRowProbe(replay, schema).Probe(nodes.ConvertAll(n => new ProbedNode(
            n.Index, n.Address, n.MapId, n.BadgeIds, n.State == AtlasNodeState.Completed))));
    }

    [Fact]
    public void TheColumnReachesAFifthOfTheAtlasAndTheTableStatesItsOwnSize()
    {
        // The number the single sample could not give. Not universal, and not a handful either.
        using ReplayMemoryReader replay = Load();
        string said = Probe(replay);

        Assert.Contains("2517 nodes, 547 with a readable atlas row, 547 distinct rows", said, StringComparison.Ordinal);
        Assert.Contains(
            "table \"Data/Balance/EndgameMapAtlas.dat\": 1242 rows of 0x121, computed 0x121 - AGREE",
            said,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ARowBelongsToOneNodeRatherThanToAPositionSeveralMapsShare()
    {
        // 547 rows for 547 nodes. The probe's own discriminator, and the reason it prints both
        // halves: a shared row would have meant something entirely different about the column.
        using ReplayMemoryReader replay = Load();

        Assert.Contains(
            "0 rows are reached by more than one node, 0 by nodes of DIFFERENT maps",
            Probe(replay),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ThePassiveNamesTheNodesPlaceInTheAtlasRatherThanBeingBookkeeping()
    {
        // The finding that overturns the single sample. Tier regions, gates, towers, league
        // structure - all named by the game, none of it in data/atlas-maps.json.
        using ReplayMemoryReader replay = Load();
        string said = Probe(replay);

        Assert.Contains("547 distinct Passives ids", said, StringComparison.Ordinal);
        foreach (string family in new[] { "AtlasOutsideFortressPath", "AtlasNormalMapsWithBreach" })
        {
            Assert.Contains(family, said, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheObjectiveAndSubTreeVocabulariesAreSmallEnoughToGroupBy()
    {
        // Seven objectives over 113 positions and four sub-trees - the four circular panels of the
        // Atlas Skills screen. Small vocabularies are what make a column worth grouping on.
        using ReplayMemoryReader replay = Load();
        string said = Probe(replay);

        Assert.Contains("113 carry a MapObjective, 365 a BlockedMessage, 0 at least one Stat", said, StringComparison.Ordinal);
        Assert.Contains("7 distinct objectives, 4 distinct sub-trees", said, StringComparison.Ordinal);

        foreach (string id in new[] { "Incursion", "Abyss", "Breach", "Delirium" })
        {
            Assert.Contains(id, said, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheRowsPrintedInFullAreTheONESCARRYINGSomethingRatherThanWhicheverCameFirst()
    {
        // The defect this capture exposed, pinned so it cannot come back: the detail block used to
        // take six arbitrary rows, drew six path positions with every column empty, and sent the
        // capture back without a single sentence in it. Now the rows with an objective sort first.
        using ReplayMemoryReader replay = Load();
        string said = Probe(replay);

        int first = said.IndexOf("  row 0x", StringComparison.Ordinal);
        Assert.True(first >= 0, "the probe printed no row in full");

        string block = said[first..];
        int next = block.IndexOf("\n  row 0x", StringComparison.Ordinal);
        block = next > 0 ? block[..next] : block;

        Assert.Contains("MapObjective 0x", block, StringComparison.Ordinal);
        Assert.DoesNotContain("MapObjective 0x0 (not a pointer)", block, StringComparison.Ordinal);
    }
}
