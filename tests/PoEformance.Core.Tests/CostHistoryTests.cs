using PoEformance.Features;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Keeping what every read cost, so a whole map can be looked at afterwards.
/// </summary>
/// <remarks>
/// A live number answers "is it slow right now", which is the question you can already answer
/// by looking at the screen. The useful ones need a history: did it get worse as the map
/// filled up, was that stutter the terrain or the entities, does this mechanic cost anything.
/// </remarks>
public class CostHistoryTests
{
    private static ReadCost Cost(double total, double entities = 0, double terrain = 0)
        => new(total, entities, 0, terrain, 0, 10, 0);

    [Fact]
    public void ItKeepsWhatItIsGiven()
    {
        var history = new CostHistory();
        history.Add(Cost(5), area: 7, atMs: 1000);
        history.Add(Cost(7), area: 7, atMs: 1030);

        Assert.Equal(2, history.Count);
        Assert.Equal([5f, 7f], history.Series(CostPhase.Total));
    }

    [Fact]
    public void AReadThatCostNothingIsNotASample()
    {
        // Outside the game there is no work to record, and a run of zeroes flattens every
        // graph drawn beside it - the shape of the real samples disappears against them.
        var history = new CostHistory();
        history.Add(default, area: 7, atMs: 1000);

        Assert.Equal(0, history.Count);
    }

    [Fact]
    public void TheOldestGoesWhenItIsFull()
    {
        var history = new CostHistory();
        for (int i = 1; i <= CostHistory.Capacity + 100; i++)
        {
            history.Add(Cost(i), area: 1, atMs: i);
        }

        float[] series = history.Series(CostPhase.Total);

        Assert.Equal(CostHistory.Capacity, series.Length);
        Assert.Equal(101f, series[0]);                                  // the first hundred fell off
        Assert.Equal(CostHistory.Capacity + 100f, series[^1]);          // and the newest is last
    }

    [Fact]
    public void ItCanBeScopedToONEMap()
    {
        // The scope anybody actually asks for. A buffer holding the last twenty minutes spans
        // several maps, and "how did THIS map behave" is the question.
        var history = new CostHistory();
        history.Add(Cost(5), area: 1, atMs: 1000);
        history.Add(Cost(9), area: 2, atMs: 2000);
        history.Add(Cost(11), area: 2, atMs: 3000);

        Assert.Equal([9f, 11f], history.Series(CostPhase.Total, area: 2));
        Assert.Equal(3, history.Series(CostPhase.Total).Length);
    }

    [Fact]
    public void EachPhaseIsItsOwnSeries()
    {
        // The whole point of a breakdown: which of them moved.
        var history = new CostHistory();
        history.Add(Cost(10, entities: 6, terrain: 3), area: 1, atMs: 1000);

        Assert.Equal([6f], history.Series(CostPhase.Entities));
        Assert.Equal([3f], history.Series(CostPhase.Terrain));
        Assert.Equal([1f], history.Series(CostPhase.Other));
    }

    [Fact]
    public void TheWORSTReadingIsKeptRatherThanSmoothedAway()
    {
        // A stutter is one bad frame among two hundred good ones. An average reports that as
        // fine, which is exactly the reading that sends somebody looking in the wrong place.
        var history = new CostHistory();
        for (int i = 0; i < 200; i++)
        {
            history.Add(Cost(4), area: 1, atMs: i);
        }

        history.Add(Cost(120), area: 1, atMs: 500);

        CostSummary summary = history.Summarise(CostPhase.Total);

        Assert.Equal(120, summary.Worst);
        Assert.True(summary.Typical < 5, "one spike should barely move the average - that is why Worst is kept");
    }

    [Fact]
    public void TheNinetyFifthSitsBetweenTheTypicalAndTheWorst()
    {
        var history = new CostHistory();
        for (int i = 1; i <= 100; i++)
        {
            history.Add(Cost(i), area: 1, atMs: i);
        }

        CostSummary summary = history.Summarise(CostPhase.Total);

        Assert.InRange(summary.NinetyFifth, summary.Typical, summary.Worst);
        Assert.Equal(1, summary.Best);
        Assert.Equal(100, summary.Samples);
    }

    [Fact]
    public void SummarisingNothingIsNotAnError()
        => Assert.Equal(0, new CostHistory().Summarise(CostPhase.Total).Samples);

    [Fact]
    public void ItListsTheMapsItHoldsNewestFirst()
    {
        // The list a person picks from - "the map I am in" and "the one before it".
        var history = new CostHistory();
        history.Add(Cost(5), area: 1, atMs: 1000);
        history.Add(Cost(5), area: 2, atMs: 2000);
        history.Add(Cost(5), area: 1, atMs: 3000);

        Assert.Equal([1u, 2u], history.Areas());
    }

    [Fact]
    public void ItSaysHowLongAMapHasBeenMeasured()
    {
        var history = new CostHistory();
        history.Add(Cost(5), area: 4, atMs: 10_000);
        history.Add(Cost(5), area: 4, atMs: 70_000);

        Assert.Equal(60, history.SecondsIn(4));
    }

    [Fact]
    public void ClearingStartsAFreshMeasurement()
    {
        var history = new CostHistory();
        history.Add(Cost(5), area: 1, atMs: 1000);
        history.Clear();

        Assert.Equal(0, history.Count);
        Assert.Empty(history.Series(CostPhase.Total));
    }

    [Fact]
    public async Task WritingAndReadingAtOnceDoesNotTearIt()
    {
        // The reader thread adds while the renderer plots. A torn read here would be a graph
        // that occasionally shows nonsense, which is the kind of fault that gets blamed on the
        // measurement rather than on the buffer.
        var history = new CostHistory();
        var stop = false;

        var writer = Task.Run(() =>
        {
            for (int i = 1; i <= 20_000; i++)
            {
                history.Add(Cost(i % 50 + 1), area: 1, atMs: i);
            }

            stop = true;
        });

        while (!stop)
        {
            float[] series = history.Series(CostPhase.Total);
            Assert.All(series, value => Assert.InRange(value, 1f, 50f));
        }

        await writer;
        Assert.True(history.Count > 0);
    }
}
