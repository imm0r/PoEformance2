using PoEformance.Game.Diagnostics;

namespace PoEformance.Core.Tests;

/// <summary>
/// The badge probe's discriminator, against node lists built by hand.
/// </summary>
/// <remarks>
/// The classification needs no memory at all - it is a question about which nodes carry which
/// badge - so it can be put under a fixture that states the case exactly. That is worth having
/// because the rule is the whole point of the probe: a badge belongs to the MAP when every node
/// of every map carrying it carries it, and to the ROLL the moment one node of the same map does
/// not. Get that backwards and the probe confidently nominates content as a map property, which
/// is precisely the mistake data/atlas-maps.json would then be deleted on.
/// </remarks>
public class AtlasBadgeProbeTests
{
    private const uint MapShaped = 0x0002_0000;
    private const uint Content = 0x0000_0064;

    private static AtlasBadgeProbe Probe()
    {
        var fake = new FakeMemoryReader();
        return new AtlasBadgeProbe(
            fake,
            WorldAreaCatalogueTests.Schema,
            new PoEformance.Game.Ui.UiElementReader(fake, WorldAreaCatalogueTests.Schema));
    }

    private static string Say(params ProbedNode[] nodes)
        => string.Join('\n', Probe().Probe(nodes));

    private static ProbedNode Node(int index, string mapId, params uint[] badges)
        => new(index, 0x10_0000UL + ((ulong)index * 0x1000), mapId, badges);

    private static ProbedNode Done(int index, string mapId, params uint[] badges)
        => new(index, 0x10_0000UL + ((ulong)index * 0x1000), mapId, badges, Completed: true);

    [Fact]
    public void ABadgeEveryNodeOfItsMapsCarriesIsCalledStable()
    {
        // Two maps, every node of both carrying it, nothing else does. This is the shape the
        // quest marker and the claimable hideout have on all three real captures.
        string said = Say(
            Node(0, "MapMothersoul_Male_Quest", MapShaped),
            Node(1, "MapUberBoss_IronCitadel_Quest", MapShaped),
            Node(2, "MapRavine"),
            Node(3, "MapRavine", Content));

        Assert.Contains("0x00020000     2 nodes over   2 maps  STABLE", said, StringComparison.Ordinal);
        Assert.Contains("maps: MapMothersoul_Male_Quest, MapUberBoss_IronCitadel_Quest", said, StringComparison.Ordinal);
    }

    [Fact]
    public void ANDOneNodeOfTheSameMapWithoutItIsEnoughToDisqualifyIt()
    {
        // The case that matters, because it is the one a single-node sample cannot see: the badge
        // still only ever appears on this map, and it is STILL not a property of the map.
        string said = Say(
            Node(0, "MapUniqueLake", MapShaped),
            Node(1, "MapUniqueLake"));

        Assert.Contains("0x00020000     1 nodes over   1 maps  varies between nodes of the same map", said, StringComparison.Ordinal);
        Assert.DoesNotContain("STABLE", said, StringComparison.Ordinal);

        // And a badge that varies is never named: the read would be spent confirming it is content.
        Assert.DoesNotContain("badge 0x", said, StringComparison.Ordinal);
    }

    [Fact]
    public void ContentThatMovesBetweenNodesOfOneMapIsNeverStable()
    {
        string said = Say(
            Node(0, "MapPit", Content),
            Node(1, "MapPit", 0x0088),
            Node(2, "MapPit"),
            Node(3, "MapHive", Content));

        Assert.Contains("0x00000064     2 nodes over   2 maps  varies", said, StringComparison.Ordinal);
        Assert.Contains("0x00000088     1 nodes over   1 maps  varies", said, StringComparison.Ordinal);
    }

    [Fact]
    public void AndTheNameIsReportedAsOutOfReachRatherThanInvented()
    {
        // The reading the real captures produce: the badge child exists, and the slot that holds
        // "Powerful Map Boss" for a content badge is NULL for the map-shaped ones. Saying so is
        // the honest answer; a probe that dropped the line would read as though it had not looked.
        string said = Say(Node(0, "MapUberBoss_StoneCitadel_Quest", MapShaped));

        Assert.Contains("STABLE", said, StringComparison.Ordinal);
        Assert.Contains("no drawn badge child carries 0x00020000", said, StringComparison.Ordinal);
    }

    [Fact]
    public void ACompletedNodeIsSetAsideRatherThanCountedAgainstAMarker()
    {
        // The rule the real captures forced, under a fixture that states it exactly: the claimed
        // node has dropped its marker, and counting it would demote a genuine map property to
        // content. This is the test that fails if somebody "simplifies" the completion check away.
        string said = Say(
            Node(0, "MapUniqueLake", MapShaped),
            Node(1, "MapUniqueLake", MapShaped),
            Done(2, "MapUniqueLake"),
            Done(3, "MapUniqueLake", Content));

        Assert.Contains("0x00020000     2 nodes over   1 maps  STABLE", said, StringComparison.Ordinal);
        Assert.Contains("2 completed nodes set aside", said, StringComparison.Ordinal);

        // And the content the completed node kept is not counted either - it would otherwise read
        // as a badge of a map that no live node carries.
        Assert.DoesNotContain("0x00000064", said, StringComparison.Ordinal);
    }

    [Fact]
    public void ACallerThatPassesNoBadgeIdsIsToldSoRatherThanReportingAnEmptyAtlas()
    {
        string said = string.Join('\n', Probe().Probe([new ProbedNode(0, 0x10_0000, "MapRavine")]));

        Assert.Contains("passed no badge ids", said, StringComparison.Ordinal);
        Assert.DoesNotContain("distinct badge ids", said, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyAtlasSaysNothingAtAll()
        => Assert.Empty(Probe().Probe([]));
}
