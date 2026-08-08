using PoEformance.Features;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Working out where a ritual line could go, and what each way would pay.
/// </summary>
/// <remarks>
/// Every rule here is a DECISION rather than an address, which is why the planner is pure and
/// why these tests can be exact. Two of them look like bugs and are not, so both are pinned:
/// a blocked map still occupies its rank among the candidates, and a route that cannot reach
/// full length is not offered at all.
/// </remarks>
public class RitualPlannerTests
{
    private static RitualMods Pool()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "data", "ritual-mods.json")))
        {
            dir = dir.Parent;
        }

        return RitualMods.Load(Path.Combine(dir!.FullName, "data", "ritual-mods.json"));
    }

    private static List<RitualMod> Rewards() => Pool().Available(new Dictionary<int, int>());

    /// <summary>A line over a made-up atlas: a chain of maps, each leading to the next.</summary>
    private static RitualLineState Chain(int length, uint lineId = 1, int secondChance = 0)
    {
        var candidates = new Dictionary<(int X, int Y), IReadOnlyList<(int X, int Y)>>();
        for (int i = 0; i < length; i++)
        {
            candidates[(i, 0)] = i + 1 < length ? [(i + 1, 0)] : [];
        }

        return new RitualLineState(lineId, [], candidates, secondChance);
    }

    private static readonly HashSet<(int X, int Y)> Nothing = [];

    [Fact]
    public void AROUTEIsOfferedOnlyWhenItReachesFullLength()
    {
        // The game refuses a click that would strand the line short of its length, so a branch
        // that dead-ends is not a worse route - it is one nobody can walk. Offered, it would be
        // a plan the game will not let you carry out.
        RitualLineState line = Chain(3);          // start plus two more, so a 3-map line fits
        List<RitualMod> pool = Rewards();

        Assert.Single(RitualPlanner.Plan(line, Nothing, [(0, 0)], pool, length: 3));
        Assert.Empty(RitualPlanner.Plan(line, Nothing, [(0, 0)], pool, length: 4));
    }

    [Fact]
    public void ABLOCKEDMapStillOccupiesItsRankAmongTheCandidates()
    {
        // The reward a map rolls depends on WHICH candidate it is, counted in order. A blocked
        // neighbour cannot be taken but is still counted - so removing it before ranking would
        // shift everything after it and predict the wrong reward for every one of them.
        //
        // Checked by comparing against a line where the blocked map is genuinely absent: if the
        // rank were recomputed, the two would agree.
        var withBlocked = new Dictionary<(int X, int Y), IReadOnlyList<(int X, int Y)>>
        {
            [(0, 0)] = [(1, 0), (2, 0)],   // (1,0) ranks first, (2,0) second
            [(2, 0)] = [],
        };

        var withoutIt = new Dictionary<(int X, int Y), IReadOnlyList<(int X, int Y)>>
        {
            [(0, 0)] = [(2, 0)],           // now (2,0) ranks FIRST
            [(2, 0)] = [],
        };

        List<RitualMod> pool = Rewards();
        var blocked = new HashSet<(int X, int Y)> { (1, 0) };

        RitualChain kept = Assert.Single(RitualPlanner.Plan(
            new RitualLineState(7, [], withBlocked, 0), blocked, [(0, 0)], pool, length: 2));

        RitualChain absent = Assert.Single(RitualPlanner.Plan(
            new RitualLineState(7, [], withoutIt, 0), Nothing, [(0, 0)], pool, length: 2));

        Assert.Equal((2, 0), kept.Stops[0].At);
        Assert.Equal((2, 0), absent.Stops[0].At);

        // Same map, same line, different RANK - so a different reward. If these are equal the
        // rank is being recomputed and every prediction past a blocked map is wrong.
        Assert.NotEqual(absent.Stops[0].First, kept.Stops[0].First);
    }

    [Fact]
    public void EVERYStartIsPlannedFromUntilALineHasBeenBegun()
    {
        // Which is the useful case: it answers "where should I start", not "what happens if I
        // start here". A hover-only version would need the answer before the question.
        var candidates = new Dictionary<(int X, int Y), IReadOnlyList<(int X, int Y)>>
        {
            [(0, 0)] = [(0, 1)],
            [(0, 1)] = [],
            [(5, 5)] = [(5, 6)],
            [(5, 6)] = [],
        };

        IReadOnlyList<RitualChain> plans = RitualPlanner.Plan(
            new RitualLineState(3, [], candidates, 0), Nothing, [(0, 0), (5, 5)], Rewards(), length: 2);

        Assert.Equal(2, plans.Count);
        Assert.Contains(plans, plan => plan.From == (0, 0));
        Assert.Contains(plans, plan => plan.From == (5, 5));
    }

    [Fact]
    public void ANDOnceItHasBegunOnlyItsOwnEndIs()
    {
        // The question changes from "where do I start" to "where do I go next", and the maps
        // already taken are not available again.
        var candidates = new Dictionary<(int X, int Y), IReadOnlyList<(int X, int Y)>>
        {
            [(0, 0)] = [(0, 1)],
            [(0, 1)] = [(0, 2)],
            [(0, 2)] = [],
            [(5, 5)] = [(5, 6)],
            [(5, 6)] = [],
        };

        var line = new RitualLineState(3, [(0, 0), (0, 1)], candidates, 0);
        IReadOnlyList<RitualChain> plans = RitualPlanner.Plan(
            line, Nothing, [(0, 0), (5, 5)], Rewards(), length: 3);

        RitualChain only = Assert.Single(plans);
        Assert.Equal((0, 1), only.From);
        Assert.Equal([(0, 2)], only.Stops.Select(stop => stop.At));
    }

    [Fact]
    public void ANDAMapAlreadyOnTheLineIsNotOfferedAgain()
    {
        // The candidate tables point both ways, so without this the line walks back down itself.
        var candidates = new Dictionary<(int X, int Y), IReadOnlyList<(int X, int Y)>>
        {
            [(0, 0)] = [(0, 1)],
            [(0, 1)] = [(0, 0), (0, 2)],
            [(0, 2)] = [(0, 1)],
        };

        IReadOnlyList<RitualChain> plans = RitualPlanner.Plan(
            new RitualLineState(3, [], candidates, 0), Nothing, [(0, 0)], Rewards(), length: 3);

        RitualChain only = Assert.Single(plans);
        Assert.Equal([(0, 1), (0, 2)], only.Stops.Select(stop => stop.At));
    }

    [Fact]
    public void THEStartCountsTowardsTheLineLength()
    {
        // A five-map line starting where you stand takes four more, not five. Getting this
        // wrong offers routes one map too long, every one of which the game refuses.
        RitualLineState line = Chain(6);

        Assert.Equal(4, Assert.Single(RitualPlanner.Plan(line, Nothing, [(0, 0)], Rewards(), 5)).Stops.Count);
        Assert.Equal(2, Assert.Single(RitualPlanner.Plan(line, Nothing, [(0, 0)], Rewards(), 3)).Stops.Count);
    }

    [Fact]
    public void THEBestRouteComesFirstWhenSomethingIsWorthMore()
    {
        // A route with two ways to go. Weighting whatever the second one rolls should float it
        // to the top, whichever it happens to be - the test must not assume a reward name.
        var candidates = new Dictionary<(int X, int Y), IReadOnlyList<(int X, int Y)>>
        {
            [(0, 0)] = [(1, 0), (2, 0)],
            [(1, 0)] = [],
            [(2, 0)] = [],
        };

        var line = new RitualLineState(11, [], candidates, 0);
        List<RitualMod> pool = Rewards();

        IReadOnlyList<RitualChain> plain = RitualPlanner.Plan(line, Nothing, [(0, 0)], pool, length: 2);
        Assert.Equal(2, plain.Count);

        string wanted = plain[1].Stops[0].First;
        var worth = new Dictionary<string, int> { [wanted] = 100 };

        IReadOnlyList<RitualChain> sorted = RitualPlanner.Plan(line, Nothing, [(0, 0)], pool, 2, worth);
        Assert.Equal(wanted, sorted[0].Stops[0].First);
        Assert.Equal(100, sorted[0].Worth);
        Assert.Equal(0, sorted[1].Worth);
    }

    [Fact]
    public void AROUTEKeepsItsNameWhenTheAtlasIsPlannedAgain()
    {
        // The key is what a selection is remembered by, so it has to be built from positions:
        // two maps can share a name, and a route named after them would jump to a different one.
        RitualLineState line = Chain(3);
        RitualChain first = Assert.Single(RitualPlanner.Plan(line, Nothing, [(0, 0)], Rewards(), 3));
        RitualChain again = Assert.Single(RitualPlanner.Plan(line, Nothing, [(0, 0)], Rewards(), 3));

        Assert.Equal(first.Key, again.Key);
        Assert.Equal("0,0|1,0|2,0", first.Key);
    }

    [Fact]
    public void NOTHINGIsPlannedWithoutAPoolOrATable()
    {
        // Both are ordinary: the pool is missing until somebody ships the file, and the table
        // is empty whenever the atlas is not in ritual mode.
        Assert.Empty(RitualPlanner.Plan(Chain(3), Nothing, [(0, 0)], [], length: 3));
        Assert.Empty(RitualPlanner.Plan(
            new RitualLineState(1, [], new Dictionary<(int X, int Y), IReadOnlyList<(int X, int Y)>>(), 0),
            Nothing, [(0, 0)], Rewards(), length: 3));

        // And a line already at its full length has nowhere left to go.
        Assert.Empty(RitualPlanner.Plan(Chain(3), Nothing, [(0, 0)], Rewards(), length: 1));
    }

    [Fact]
    public void ABLOCKEDStartIsNotPlannedFrom()
    {
        RitualLineState line = Chain(3);
        var blocked = new HashSet<(int X, int Y)> { (0, 0) };

        Assert.Empty(RitualPlanner.Plan(line, blocked, [(0, 0)], Rewards(), length: 3));
    }

    [Fact]
    public void ATwoRewardMapCarriesBothOfThem()
    {
        RitualLineState line = Chain(3, lineId: 5, secondChance: 100);
        RitualChain plan = Assert.Single(RitualPlanner.Plan(line, Nothing, [(0, 0)], Rewards(), length: 3));

        Assert.All(plan.Stops, stop => Assert.True(stop.Twice));
        Assert.Equal(4, plan.Rewards.Count());
    }

    [Theory]
    [InlineData("4 Exalted Orbs", "Exalted Orbs x4")]
    [InlineData("2 Perfect Chaos Orbs", "Perfect Chaos Orbs x2")]
    [InlineData("Omen of Amelioration", "Omen: Amelioration")]
    [InlineData("Contains a very rare Unique Item", "Very Rare Unique")]
    [InlineData("Contains 3 Ritual Altars", "3 Ritual Altars")]
    [InlineData("Monsters Sacrificed grant 25% increased Tribute", "+25% Tribute")]
    [InlineData("Rerolling Favours has no Cost the first time", "+Free Reroll")]
    [InlineData("Something nobody has written a rule for", "Something nobody has written a rule for")]
    [InlineData("", "")]
    public void AREWARDIsShortenedToWhatFitsOnARow(string text, string expected)
    {
        // A route is five rewards side by side and the game's wording is a sentence each.
        // Unrecognised text passes through, so a new league reads long rather than blank.
        Assert.Equal(expected, RitualRewards.Short(text));
    }

    [Fact]
    public void EVERYRewardInThePoolHasAShortNameToChooseFrom()
    {
        IReadOnlyList<string> all = RitualRewards.Everything(Pool());

        Assert.NotEmpty(all);
        Assert.DoesNotContain(string.Empty, all);
        Assert.Equal(all.Count, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal([.. all.OrderBy(one => one, StringComparer.OrdinalIgnoreCase)], all);
    }
}
