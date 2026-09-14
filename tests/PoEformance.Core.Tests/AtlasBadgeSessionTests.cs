using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Diagnostics;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// What the icons over the atlas actually are, across all THREE real 0.5.5 captures.
/// </summary>
/// <remarks>
/// THE QUESTION THIS ANSWERS was put the obvious way round: the game draws an icon over many maps
/// and an exclamation mark over a quest one, so surely that is where the curated words in
/// data/atlas-maps.json could come from instead. The captures say it is TWO things wearing one
/// coat, and the split is sharp.
///
/// MOST badges are the node's ROLL. The same map id carries different badges on different nodes -
/// MapPit reads eight distinct badge sets in one capture - so no property of the MAP can be read
/// off them, and a tags column least of all.
///
/// EXACTLY FOUR are the map, the same four on every capture, and nothing else ever qualifies:
///   0x00020000  the QUEST marker, on the _Quest maps and on nothing else
///   0x000203E9  the CLAIMABLE HIDEOUT
///   0x000203EB  a UNIQUE map
///   0x000203EA  a second unique marker, only ever on MapUniqueMerchant03_Raft
///
/// AND A COMPLETED NODE DROPS ITS MARKER, which is the part that cannot be guessed and the part
/// that decides whether the rule works at all: the claimed MapHideoutFelled_Claimable carries
/// nothing while five unclaimed Canal hideouts carry 0x203E9, and every Completed unique reads
/// bare beside its Locked twin. Judge the badges over completed nodes too and the unique marker
/// looks like content.
///
/// Run over three captures on purpose. One capture cannot tell a stable badge from a map that
/// happens to have one node, and the atlas is re-rolled between them - so an id that holds in all
/// three is being measured rather than observed.
/// </remarks>
public class AtlasBadgeSessionTests
{
    /// <summary>The quest marker - the exclamation mark the game draws over a quest map.</summary>
    private const uint Quest = 0x0002_0000;

    /// <summary>The claimable hideout, which is what data/atlas-maps.json tags "hideout".</summary>
    private const uint Claimable = 0x0002_03E9;

    /// <summary>A unique map.</summary>
    private const uint Unique = 0x0002_03EB;

    /// <summary>The second unique marker. Only one map has ever shown it.</summary>
    private const uint UniqueOther = 0x0002_03EA;

    public static TheoryData<string> Captures =>
    [
        "session-2026-09-atlas.rec",
        "session-2026-09-worldareas.rec",
        "session-2026-09-catalogue.rec",
    ];

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

    private static ReplayMemoryReader Load(string fixture)
        => ReplayMemoryReader.Load(File.OpenRead(Path.Combine(Root.FullName, "tests", "fixtures", fixture)));

    private static List<AtlasNode> Nodes(ReplayMemoryReader replay)
    {
        OffsetSchema schema = RealSessionTests.LiveSchema();
        GameChainAddresses chain = GameChain.Resolve(replay, schema, replay.ResolvedStatics["GameStates"]);
        Assert.Equal(GameStateKind.InGame, chain.State);

        List<AtlasNode> nodes = new AtlasReader(replay, schema, new UiElementReader(replay, schema))
            .Read(chain.UiRoot, new UiScale(3440, 1440, 0));
        Assert.True(nodes.Count > 100, $"only {nodes.Count} nodes read off the panel");
        return nodes;
    }

    /// <summary>The nodes a marker can be judged on: the ones that have not been run.</summary>
    private static List<AtlasNode> Live(List<AtlasNode> nodes)
        => [.. nodes.Where(n => n.State != AtlasNodeState.Completed && n.MapId.Length > 0)];

    private static string Probe(ReplayMemoryReader replay, List<AtlasNode> nodes)
    {
        OffsetSchema schema = RealSessionTests.LiveSchema();
        return string.Join('\n', new AtlasBadgeProbe(replay, schema, new UiElementReader(replay, schema))
            .Probe(nodes.ConvertAll(n => new ProbedNode(
                n.Index, n.Address, n.MapId, n.BadgeIds, n.State == AtlasNodeState.Completed))));
    }

    /// <summary>The line the probe printed for one badge id.</summary>
    private static string LineFor(string said, uint id)
    {
        int at = said.IndexOf($"0x{id:X8}", StringComparison.Ordinal);
        Assert.True(at >= 0, $"the probe did not mention 0x{id:X8}");
        int end = said.IndexOf('\n', at);
        return end > 0 ? said[at..end] : said[at..];
    }

    [Theory]
    [MemberData(nameof(Captures))]
    public void TheQuestMarkerIsABadgeAndItIsExactlyTheQuestMaps(string fixture)
    {
        // The reading that turns "the game shows an exclamation mark" into something readable.
        // Both halves are needed: every live _Quest node carries it, and NO other node does - a
        // marker merely common on quest maps would pass the first check alone.
        using ReplayMemoryReader replay = Load(fixture);
        List<AtlasNode> nodes = Nodes(replay);

        List<AtlasNode> quests = [.. Live(nodes).Where(n => n.MapId.EndsWith("_Quest", StringComparison.Ordinal))];
        Assert.NotEmpty(quests);
        Assert.All(quests, node => Assert.Contains(Quest, node.BadgeIds));
        Assert.DoesNotContain(
            nodes,
            node => node.BadgeIds.Contains(Quest) && !node.MapId.EndsWith("_Quest", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Captures))]
    public void TheClaimableHideoutIsOneToo(string fixture)
    {
        // Same shape, and it matters more than the quest one: this is the only curated tag in
        // data/atlas-maps.json that the game turns out to mark on the node itself.
        using ReplayMemoryReader replay = Load(fixture);
        List<AtlasNode> nodes = Nodes(replay);

        List<AtlasNode> claimables = [.. Live(nodes).Where(n => n.MapId.Contains("Hideout", StringComparison.Ordinal))];
        Assert.NotEmpty(claimables);
        Assert.All(claimables, node => Assert.Contains(Claimable, node.BadgeIds));
        Assert.DoesNotContain(
            nodes,
            node => node.BadgeIds.Contains(Claimable) && !node.MapId.Contains("Hideout", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Captures))]
    public void ACompletedNodeHasDroppedItsMarker(string fixture)
    {
        // The rule everything above rests on, and the one a single screenshot would never show.
        // Stated as an absolute because that is how it reads: not one completed node on any of the
        // three captures carries any of the four.
        using ReplayMemoryReader replay = Load(fixture);
        List<AtlasNode> nodes = Nodes(replay);

        List<AtlasNode> done = [.. nodes.Where(n => n.State == AtlasNodeState.Completed)];
        Assert.NotEmpty(done);
        foreach (uint marker in new[] { Quest, Claimable, Unique, UniqueOther })
        {
            Assert.DoesNotContain(done, node => node.BadgeIds.Contains(marker));
        }

        // And it is not that completed nodes are simply bare - they keep their rolled content.
        Assert.Contains(done, node => node.BadgeIds.Count > 0);
    }

    [Theory]
    [MemberData(nameof(Captures))]
    public void TheProbeFindsExactlyThoseFourWithoutBeingToldWhichIdsToLookFor(string fixture)
    {
        // The probe carries no list of special ids - it measures "every uncompleted node of every
        // map that carries this carries it" and reports whatever passes. That the same four come
        // out on all three captures, and nothing else ever does, is what says the rule is the
        // right one; it is also what will surface a marker a later league adds with no edit here.
        using ReplayMemoryReader replay = Load(fixture);
        string said = Probe(replay, Nodes(replay));

        foreach (uint id in new[] { Quest, Claimable, Unique, UniqueOther })
        {
            Assert.Contains("STABLE", LineFor(said, id), StringComparison.Ordinal);
        }

        Assert.Equal(4, said.Split('\n').Count(line => line.Contains("STABLE", StringComparison.Ordinal)));
    }

    [Theory]
    [MemberData(nameof(Captures))]
    public void TheUniqueMarkersSideWithTheGamesOwnFlagRatherThanWithTheFile(string fixture)
    {
        // A free check on the six ids where IsUniqueMapArea and data/atlas-maps.json disagree.
        // MapUniqueReactor_04 is one of the two the FILE calls unique and the TABLE does not - and
        // it sits on the atlas, uncompleted, carrying no unique marker. So the badge agrees with
        // the table. That is one of the six settled by a third, independent reading.
        using ReplayMemoryReader replay = Load(fixture);
        List<AtlasNode> nodes = Nodes(replay);

        List<AtlasNode> marked = [.. nodes.Where(n => n.BadgeIds.Contains(Unique) || n.BadgeIds.Contains(UniqueOther))];
        Assert.NotEmpty(marked);
        Assert.All(marked, node => Assert.Contains("Unique", node.MapId, StringComparison.Ordinal));

        List<AtlasNode> reactor = [.. Live(nodes).Where(n => n.MapId == "MapUniqueReactor_04")];
        Assert.NotEmpty(reactor);
        Assert.All(reactor, node =>
        {
            Assert.DoesNotContain(Unique, node.BadgeIds);
            Assert.DoesNotContain(UniqueOther, node.BadgeIds);
        });

        AtlasMapInfo said = AtlasMapNames
            .Load(Path.Combine(Root.FullName, "data", "atlas-maps.json"))
            .Of("MapUniqueReactor_04");
        Assert.True(said.Unique, "the file no longer calls MapUniqueReactor_04 unique, so this check is stale");
    }

    [Theory]
    [MemberData(nameof(Captures))]
    public void TheOrdinaryBadgesAreTheRollAndNotTheMap(string fixture)
    {
        // The finding the whole question turns on, stated as a count rather than an example: most
        // map ids on the atlas appear on several nodes carrying DIFFERENT badges. A per-map tags
        // column cannot be read out of something that disagrees with itself from node to node.
        using ReplayMemoryReader replay = Load(fixture);
        List<AtlasNode> nodes = Nodes(replay);

        var sets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (AtlasNode node in nodes.Where(n => n.MapId.Length > 0))
        {
            (sets.TryGetValue(node.MapId, out HashSet<string>? seen) ? seen : sets[node.MapId] = [])
                .Add(string.Join(',', node.BadgeIds.Order()));
        }

        int split = sets.Count(pair => pair.Value.Count > 1);
        Assert.True(split * 2 > sets.Count, $"only {split} of {sets.Count} map ids carry differing badges");

        // And the commonest badge of all is one of them: "Powerful Map Boss" sits on hundreds of
        // nodes and on only some nodes of the same map.
        Assert.Contains("varies", LineFor(Probe(replay, nodes), 0x0000_0064), StringComparison.Ordinal);
    }

    [Fact]
    public void TheGamesOwnNameForTheMapShapedBadgesIsNotInTheSlotThatHoldsTheContentNames()
    {
        // The limit, measured rather than assumed. +0x2E8 held "Powerful Map Boss" for content id
        // 0x00020064 - the same 0x0002 category - and is NULL for all four markers. So the ids can
        // group the atlas without a file and cannot label it: the words would still have to come
        // from somewhere else. Recorded so the next capture is not spent rediscovering it.
        using ReplayMemoryReader replay = Load("session-2026-09-catalogue.rec");
        string said = Probe(replay, Nodes(replay));

        int at = said.IndexOf($"0x{Quest:X8}", StringComparison.Ordinal);
        string block = said[at..];
        int next = block.IndexOf("\n  0x", StringComparison.Ordinal);
        block = next > 0 ? block[..next] : block;

        Assert.Contains("+0x2E8 -> 0x0 (not a pointer)", block, StringComparison.Ordinal);
        Assert.Contains("+0x278 -> 0x0 (not a pointer)", block, StringComparison.Ordinal);
    }
}
