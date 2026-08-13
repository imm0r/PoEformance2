using PoEformance.Features;

namespace PoEformance.Core.Tests;

/// <summary>
/// The route to somewhere in memory: where it started and every offset followed since.
/// </summary>
/// <remarks>
/// These exist because the first version of this lived inside the dissector window, where
/// nothing could reach it, and was wrong three separate ways: it crashed on stepping back off
/// the last hop, it skipped the first offset, and it never showed the name it started from -
/// so every route it produced read as a bare address, which is the one form that is worthless.
/// The crash took the tool down in front of the owner. All three were plain logic errors that
/// any of the tests below would have caught.
/// </remarks>
public class PointerTrailTests
{
    [Fact]
    public void ARouteIsItsStartAndEveryOffsetSinceInORDER()
    {
        // The bug that made the feature pointless while looking like it worked: the first hop
        // was skipped, so a route showed one fewer step than was actually taken.
        var trail = new PointerTrail();
        trail.Restart("AreaInstance");
        trail.Follow([0x1000], 0x598);
        trail.Follow([0x2000], 0x20);

        Assert.Equal("AreaInstance -> +0x598 -> +0x20", trail.Describe());
    }

    [Fact]
    public void TheNameItSTARTEDFromIsWhatMakesTheRouteWorthHaving()
    {
        // "AreaInstance -> +0x598" is true tomorrow. The address it lands on today is not -
        // it moves on the next area load and again on the next restart.
        var trail = new PointerTrail();
        trail.Restart("Metadata/Monsters/Skeleton.Life");
        trail.Follow([0x1000], 0x10);

        Assert.StartsWith("Metadata/Monsters/Skeleton.Life", trail.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void SteppingBackGivesTheAddressItCameFrom()
    {
        var trail = new PointerTrail();
        trail.Restart("AreaInstance");
        trail.Follow([0x1000], 0x598);
        trail.Follow([0x2000], 0x20);

        Assert.True(trail.TryStepBack(out IReadOnlyList<ulong> back));
        Assert.Equal([0x2000UL], back);
        Assert.Equal("AreaInstance -> +0x598", trail.Describe());
    }

    [Fact]
    public void SteppingBackOffTheLASTHopEmptiesItWithoutFallingOver()
    {
        // The crash, exactly. One hop, step back, and the route was then asked to describe
        // itself from a list with nothing in it.
        var trail = new PointerTrail();
        trail.Restart("AreaInstance");
        trail.Follow([0x1000], 0x598);

        Assert.True(trail.TryStepBack(out _));
        Assert.True(trail.IsEmpty);
        Assert.Equal(string.Empty, trail.Describe());
    }

    [Fact]
    public void SteppingBackFromNowhereSaysSoRatherThanThrowing()
    {
        var trail = new PointerTrail();

        Assert.False(trail.TryStepBack(out IReadOnlyList<ulong> nothing));
        Assert.Empty(nothing);
    }

    [Fact]
    public void AHopRemembersWhereEVERYPlaceCameFrom()
    {
        // The dissector walks several addresses through one offset at a time, so stepping
        // back has to put each of them where it individually was. Remembering only the first
        // would strand the rest on the far side of the hop - and a comparison whose places
        // are at different depths is worse than none, because it still looks like one.
        var trail = new PointerTrail();
        trail.Restart("Metadata/Monsters/Skeleton");
        trail.Follow([0x1000, 0x5000, 0x9000], 0x30);

        Assert.True(trail.TryStepBack(out IReadOnlyList<ulong> back));
        Assert.Equal([0x1000UL, 0x5000UL, 0x9000UL], back);
    }

    [Fact]
    public void TheRouteIsONERouteHoweverManyPlacesWalkedIt()
    {
        // And this is why walking them together is worth the trouble: "+0x30 -> +0x8" holding
        // on three monsters at once is a far stronger claim than the same offsets on one, and
        // it is the same one line either way.
        var trail = new PointerTrail();
        trail.Restart("AreaInstance");
        trail.Follow([0x1000, 0x5000], 0x30);
        trail.Follow([0x2000, 0x6000], 0x8);

        Assert.Equal("AreaInstance -> +0x30 -> +0x8", trail.Describe());
    }

    [Fact]
    public void AnUnstartedTrailDescribesItselfAsNothing()
    {
        Assert.Equal(string.Empty, new PointerTrail().Describe());
        Assert.True(new PointerTrail().IsEmpty);
    }

    [Fact]
    public void RestartingForgetsTheOldRoute()
    {
        // Following a pointer to somewhere unrelated must not leave the old way there hanging
        // off the front of the new one.
        var trail = new PointerTrail();
        trail.Restart("AreaInstance");
        trail.Follow([0x1000], 0x598);
        trail.Restart("WorldData");

        Assert.True(trail.IsEmpty);
        Assert.Equal(string.Empty, trail.Describe());

        trail.Follow([0x9000], 0x1A0);
        Assert.Equal("WorldData -> +0x1A0", trail.Describe());
    }

    [Fact]
    public void WithNoNameGivenTheStartIsAtLeastAnAddress()
    {
        var trail = new PointerTrail();
        trail.Restart(string.Empty);
        trail.Follow([0xDEAD0000], 0x8);

        Assert.Equal("0xDEAD0000 -> +0x8", trail.Describe());
    }

    [Fact]
    public void TheHopThatSTARTEDSomewhereCarriesNoOffset()
    {
        // Arriving somewhere is not a step through an offset, so it must not be written as
        // one - "-> +0xFFFFFFFF" is what a negative offset would print.
        var trail = new PointerTrail();
        trail.Restart("AreaInstance");
        trail.Follow([0x1000], -1);
        trail.Follow([0x2000], 0x20);

        Assert.Equal("AreaInstance -> +0x20", trail.Describe());
    }

    [Fact]
    public void ALongDescentIsAllThere()
    {
        var trail = new PointerTrail();
        trail.Restart("InGameState");
        foreach (int offset in (int[])[0x10, 0x20, 0x30, 0x40])
        {
            trail.Follow([0x1000], offset);
        }

        Assert.Equal(4, trail.Count);
        Assert.Equal("InGameState -> +0x10 -> +0x20 -> +0x30 -> +0x40", trail.Describe());
    }
}
