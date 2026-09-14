using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Diagnostics;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// What the icons over the atlas actually are, across all FIVE real 0.5.5 captures.
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
/// FOUR are the map, on every capture:
///   0x00020000  a GATE. Its carriers sit on atlas positions whose passives are AtlasRedLock1/2
///               and AtlasYellowLock1/2 - the tier locks - and one Expedition boss sub-area.
///   0x000203E9  claimable hideouts AND Expedition logbooks, which is a membership rather than
///               a meaning, and is left as one.
///   0x000203EB  a UNIQUE map
///   0x000203EA  a second unique marker, only ever on MapUniqueMerchant03_Raft
///
/// TWO OF THOSE NAMES WERE WRONG UNTIL THE FOURTH CAPTURE, and the way they were wrong is the
/// reason this class now runs over every fixture there is. On the first three, 0x20000 sat only on
/// *_Quest maps and 0x203E9 only on MapHideout*_Claimable - and these tests asserted, and passed,
/// that NOTHING ELSE carried them. Then a capture with Expedition nodes showed 0x20000 on
/// ExpeditionSubArea_MedvedBoss and 0x203E9 on four ExpeditionLogBook_* areas. The three captures
/// had simply never contained one. "Nothing else carries it" was a fact about the recordings, not
/// about the game, and only the other direction - every *_Quest node carries 0x20000, every
/// *_Claimable carries 0x203E9 - was ever checkable. That is what is asserted now.
///
/// AND A COMPLETED NODE DROPS ITS MARKER, which is the part that cannot be guessed and the part
/// that decides whether the rule works at all: the claimed MapHideoutFelled_Claimable carries
/// nothing while five unclaimed Canal hideouts carry 0x203E9, and every Completed unique reads
/// bare beside its Locked twin. Judge the badges over completed nodes too and the unique marker
/// looks like content. And it is the NODE that clears it, not the entitlement: the owner already
/// held the Canal and Limestone hideouts when the last capture was taken, and six Canal
/// claimables in it are still marked. So the marker says "this node is an unrun claimable", not
/// "this hideout can still be earned" - which is the reading the game's own mechanic invites.
///
/// Run over three captures on purpose. One capture cannot tell a stable badge from a map that
/// happens to have one node, and the atlas is re-rolled between them - so an id that holds in all
/// three is being measured rather than observed.
/// </remarks>
public class AtlasBadgeSessionTests
{
    /// <summary>A gate. Named for the AtlasRedLock/AtlasYellowLock passives its carriers sit on.</summary>
    private const uint Gate = 0x0002_0000;

    /// <summary>Claimable hideouts and Expedition logbooks alike. See the class remarks.</summary>
    private const uint Claimable = 0x0002_03E9;

    /// <summary>A unique map.</summary>
    private const uint Unique = 0x0002_03EB;

    /// <summary>The second unique marker. Only one map has ever shown it.</summary>
    private const uint UniqueOther = 0x0002_03EA;

    /// <summary>
    /// EVERY capture, and the two at the end are why the class remarks read as they do.
    /// </summary>
    /// <remarks>
    /// The first three were the whole evidence once, and on them two of these markers looked
    /// exclusive to one kind of map. They are not; those three simply held no Expedition node.
    /// Leaving a fixture out of this list is how that mistake was made, so new captures go in.
    /// </remarks>
    public static TheoryData<string> Captures =>
    [
        "session-2026-09-atlas.rec",
        "session-2026-09-worldareas.rec",
        "session-2026-09-catalogue.rec",
        "session-2026-09-atlasrows.rec",
        "session-2026-09-atlastext.rec",
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
    public void EveryQuestMapCarriesTheGateMarker(string fixture)
    {
        // ONE DIRECTION ONLY, and the missing half is the point. This used to assert that no other
        // node carries it, which held on three captures and was refuted by the fourth - see the
        // class remarks. A capture with no _Quest node at all is fine and says nothing either way.
        using ReplayMemoryReader replay = Load(fixture);

        List<AtlasNode> quests = [.. Live(Nodes(replay)).Where(n => n.MapId.EndsWith("_Quest", StringComparison.Ordinal))];
        Assert.All(quests, node => Assert.Contains(Gate, node.BadgeIds));
    }

    [Theory]
    [MemberData(nameof(Captures))]
    public void AndTheGateMarkerIsNotConfinedToThem(string fixture)
    {
        // The refutation, kept as a test so the narrower claim cannot creep back: every carrier is
        // either a _Quest map or an Expedition sub-area, and at least one capture has the latter.
        using ReplayMemoryReader replay = Load(fixture);
        List<AtlasNode> carrying = [.. Nodes(replay).Where(n => n.BadgeIds.Contains(Gate))];

        Assert.All(carrying, node => Assert.True(
            node.MapId.EndsWith("_Quest", StringComparison.Ordinal)
                || node.MapId.StartsWith("Expedition", StringComparison.Ordinal),
            $"{node.MapId} carries the gate marker and is neither a _Quest map nor an Expedition area"));
    }

    [Theory]
    [MemberData(nameof(Captures))]
    public void EveryClaimableHideoutCarriesTheClaimableMarker(string fixture)
    {
        // Again one direction. The other was refuted by four ExpeditionLogBook_* areas carrying
        // the same marker, so what it means is wider than the hideouts that first showed it.
        using ReplayMemoryReader replay = Load(fixture);

        List<AtlasNode> claimables = [.. Live(Nodes(replay)).Where(n => n.MapId.Contains("Hideout", StringComparison.Ordinal))];
        Assert.All(claimables, node => Assert.Contains(Claimable, node.BadgeIds));
    }

    [Theory]
    [MemberData(nameof(Captures))]
    public void AndTheClaimableMarkerCoversExpeditionLogbooksToo(string fixture)
    {
        // The refutation as a test. Both kinds are areas entered once for a one-off reward, which
        // is a description of who carries it and deliberately not a claim about what it means.
        using ReplayMemoryReader replay = Load(fixture);
        List<AtlasNode> carrying = [.. Nodes(replay).Where(n => n.BadgeIds.Contains(Claimable))];

        Assert.All(carrying, node => Assert.True(
            node.MapId.Contains("Hideout", StringComparison.Ordinal)
                || node.MapId.StartsWith("ExpeditionLogBook", StringComparison.Ordinal),
            $"{node.MapId} carries the claimable marker and is neither a hideout nor a logbook"));
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
        foreach (uint marker in new[] { Gate, Claimable, Unique, UniqueOther })
        {
            Assert.DoesNotContain(done, node => node.BadgeIds.Contains(marker));
        }

        // And it is not that completed nodes are simply bare - they keep their rolled content.
        Assert.Contains(done, node => node.BadgeIds.Count > 0);
    }

    [Theory]
    [MemberData(nameof(Captures))]
    public void NothingOutsideTheKnownFourIsEverStable(string fixture)
    {
        // The probe carries no list of special ids - it measures "every uncompleted node of every
        // map that carries this carries it" and reports whatever passes. The invariant worth
        // asserting is therefore NOT that all four turn up in every capture; a capture with no
        // unique node on screen has nothing to say about the unique markers, and demanding it
        // repeats the mistake in the class remarks. What holds everywhere is the other way round:
        // whatever DOES come out stable is one of the four, so a fifth would fail here and be
        // found rather than quietly assumed away.
        using ReplayMemoryReader replay = Load(fixture);
        List<AtlasNode> nodes = Nodes(replay);
        string said = Probe(replay, nodes);

        uint[] known = [Gate, Claimable, Unique, UniqueOther];
        List<string> stable = [.. said.Split('\n').Where(line => line.Contains("STABLE", StringComparison.Ordinal))];
        Assert.NotEmpty(stable);
        Assert.All(stable, line => Assert.Contains(
            known,
            id => line.Contains($"0x{id:X8}", StringComparison.Ordinal)));

        // Deliberately NOT asserted: that a marker present in a capture is stable in it. It often
        // is not - 0x203E9 appears in session-2026-09-atlastext.rec without qualifying - and the
        // whole lesson of this class is that a capture answers only what it happens to contain.
        Assert.NotEmpty(nodes);
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
        Assert.All(marked, node => Assert.Contains("Unique", node.MapId, StringComparison.Ordinal));

        // Conditional on the capture having the node at all - see NothingOutsideTheKnownFourIsEverStable.
        List<AtlasNode> reactor = [.. Live(nodes).Where(n => n.MapId == "MapUniqueReactor_04")];
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

        int at = said.IndexOf($"0x{Gate:X8}", StringComparison.Ordinal);
        string block = said[at..];
        int next = block.IndexOf("\n  0x", StringComparison.Ordinal);
        block = next > 0 ? block[..next] : block;

        Assert.Contains("+0x2E8 -> 0x0 (not a pointer)", block, StringComparison.Ordinal);
        Assert.Contains("+0x278 -> 0x0 (not a pointer)", block, StringComparison.Ordinal);
    }
}
