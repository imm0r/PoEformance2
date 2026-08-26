using PoEformance.Features;

namespace PoEformance.Core.Tests;

/// <summary>
/// The record of what the purse has been worth - and the one rule that separates it from every
/// other history in this tool: nothing but a button ever empties it.
/// </summary>
public class WealthHistoryTests
{
    private static string Scratch() =>
        Path.Combine(Path.GetTempPath(), $"poeformance-wealth-{Guid.NewGuid():N}.json");

    // A fixed wall-clock moment to count from, so nothing here depends on when it runs.
    private const long Start = 1_800_000_000_000;

    [Fact]
    public void AReadingBecomesAPointAndSurvivesARestart()
    {
        string path = Scratch();
        try
        {
            var history = new WealthHistory();
            Assert.True(history.Note(Start, 1234.5, 581, 12));
            Assert.True(history.Save(path));

            WealthHistory back = WealthHistory.Load(path);

            Assert.True(back.Readable);
            Assert.Equal(1, back.Count);
            Assert.Equal(Start, back.Since);

            WealthPoint point = back.Latest!.Value;
            Assert.Equal(Start, point.At);
            Assert.Equal(1234.5, point.Exalted, 3);
            Assert.Equal(581, point.Rate, 3);
            Assert.Equal(12, point.Stacks);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TwoReadingsTooCloseTogetherAreOnePoint()
    {
        // Picking up currency moves the total several times a minute. A record of a month does
        // not get truer for holding every one of them.
        var history = new WealthHistory();

        Assert.True(history.Note(Start, 100, 581, 1));
        Assert.False(history.Note(Start + 1_000, 200, 581, 2));
        Assert.False(history.Note(Start + WealthHistory.MinGapMs - 1, 300, 581, 3));
        Assert.True(history.Note(Start + WealthHistory.MinGapMs, 400, 581, 4));

        Assert.Equal(2, history.Count);
    }

    [Fact]
    public void APurseThatHasNotMovedWritesNothingUntilTheHeartbeat()
    {
        // The two halves of it: an unchanged reading is not a point, right up until enough time
        // has passed that its absence would be indistinguishable from the tool being closed.
        var history = new WealthHistory();

        Assert.True(history.Note(Start, 500, 581, 3));
        Assert.False(history.Note(Start + WealthHistory.MinGapMs, 500, 581, 3));
        Assert.False(history.Note(Start + (WealthHistory.HeartbeatMs / 2), 500, 581, 3));

        Assert.True(history.Note(Start + WealthHistory.HeartbeatMs, 500, 581, 3));
        Assert.Equal(2, history.Count);
    }

    [Fact]
    public void AGapInTheRecordThereforeMeansTheToolWasNotRunning()
    {
        // What the heartbeat buys, stated as the fact it is there to protect. Two points a day
        // apart can only happen when nothing was watching in between - a purse that sat
        // untouched with the tool open would have left heartbeats all the way across.
        var history = new WealthHistory();

        history.Note(Start, 500, 581, 3);
        history.Note(Start + 86_400_000, 500, 581, 3);

        IReadOnlyList<WealthPoint> all = history.All;
        Assert.Equal(2, all.Count);
        Assert.True(all[1].At - all[0].At > WealthHistory.HeartbeatMs);
    }

    [Fact]
    public void AClockThatWentBackwardsIsRefused()
    {
        // Corrections, timezone changes, a machine that boots with a bad clock and fixes it a
        // minute later. A record whose points are not in order draws a graph that folds back on
        // itself, and every query here assumes the order.
        var history = new WealthHistory();

        Assert.True(history.Note(Start, 100, 581, 1));
        Assert.False(history.Note(Start - 60_000, 999, 581, 9));

        Assert.Equal(1, history.Count);
        Assert.Equal(100, history.Latest!.Value.Exalted, 3);
    }

    [Fact]
    public void NothingEmptiesItButAReset()
    {
        var history = new WealthHistory();
        for (var i = 0; i < 20; i++)
        {
            history.Note(Start + (i * WealthHistory.MinGapMs), 100 + i, 581, i);
        }

        Assert.Equal(20, history.Count);

        history.Reset(Start + 999_999);

        Assert.Equal(0, history.Count);
        Assert.Equal(Start + 999_999, history.Since);
        Assert.Null(history.Latest);
    }

    [Fact]
    public void GrowingPastTheCapThinsTheOldHalfRatherThanDroppingIt()
    {
        // THE POINT OF THE WHOLE DESIGN. A ring buffer would drop the oldest sample to make
        // room, which is right for a graph of one map and would be a quiet permanent theft
        // here. What must survive is the SPAN - how far back the record goes - while the
        // resolution of the distant past is allowed to go coarse.
        var history = new WealthHistory();

        long at = Start;
        for (var i = 0; i <= WealthHistory.Most; i++)
        {
            history.Note(at, 1000 + i, 581, 1);
            at += WealthHistory.MinGapMs;
        }

        Assert.True(history.Count <= WealthHistory.Most, $"{history.Count} points survived the cap");

        // The record still begins where it began and ends where it ends.
        Assert.Equal(Start, history.Earliest!.Value.At);
        Assert.Equal(at - WealthHistory.MinGapMs, history.Latest!.Value.At);

        // And the recent half was not touched: the newest points are still one gap apart.
        IReadOnlyList<WealthPoint> all = history.All;
        Assert.Equal(WealthHistory.MinGapMs, all[^1].At - all[^2].At);
    }

    [Fact]
    public void AndThinningRepeatedlyStillNeverLosesTheBeginning()
    {
        // Thinning runs again every time the cap is passed. Whatever it does to the middle, the
        // first point is what says how far back the record reaches, and it has to be the last
        // thing standing.
        var history = new WealthHistory();

        long at = Start;
        for (var i = 0; i < WealthHistory.Most * 3; i++)
        {
            history.Note(at, 1000 + i, 581, 1);
            at += WealthHistory.MinGapMs;
        }

        Assert.Equal(Start, history.Earliest!.Value.At);
        Assert.True(history.Count <= WealthHistory.Most);
    }

    [Fact]
    public void AWindowStartsFromWhatWasTrueWhenItBegan()
    {
        // A window opening mid-flat-stretch has no point of its own to start from. Without the
        // anchor the graph draws the window's first reading as if the value had jumped to it.
        var history = new WealthHistory();

        history.Note(Start, 100, 581, 1);
        history.Note(Start + (WealthHistory.HeartbeatMs * 4), 300, 581, 3);

        IReadOnlyList<WealthPoint> window =
            history.Between(Start + (WealthHistory.HeartbeatMs * 2), Start + (WealthHistory.HeartbeatMs * 5));

        Assert.Equal(2, window.Count);
        Assert.Equal(100, window[0].Exalted, 3);
    }

    [Fact]
    public void AChangeNobodyHasARecordOfIsNotAChangeOfZero()
    {
        // The distinction a readout has to be able to make. "The record does not go back that
        // far" and "it went back that far and nothing happened" are different answers, and
        // showing 0 for the first one invents a fact.
        var history = new WealthHistory();
        history.Note(Start, 100, 581, 1);
        history.Note(Start + WealthHistory.HeartbeatMs, 175, 581, 1);

        Assert.Null(history.ChangeSince(Start - 60_000));
        Assert.Equal(75, history.ChangeSince(Start)!.Value, 3);
        Assert.Equal(75, history.Change!.Value, 3);
    }

    [Fact]
    public void AnEmptyRecordAnswersNothingRatherThanZero()
    {
        var history = new WealthHistory();

        Assert.Null(history.Latest);
        Assert.Null(history.Earliest);
        Assert.Null(history.Change);
        Assert.Null(history.ChangeSince(Start));
        Assert.Null(history.At(Start));
    }

    [Fact]
    public void ADamagedFileIsReportedRatherThanReplaced()
    {
        // Whatever is in that file is somebody's whole history. The one thing worse than not
        // being able to draw it is overwriting it with today.
        string path = Scratch();
        try
        {
            File.WriteAllText(path, "{ this is not json");

            WealthHistory history = WealthHistory.Load(path);

            Assert.False(history.Readable);
            Assert.Equal(0, history.Count);
            Assert.Contains("not json", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AHandEditedFileIsPutBackInOrder()
    {
        // The file outlives every version of the tool that wrote it, and it is plain JSON
        // somebody can edit. Every query here assumes the points are in order.
        string path = Scratch();
        try
        {
            File.WriteAllText(
                path,
                $$"""
                {"since":{{Start}},"points":[
                  {"at":{{Start + 2000}},"ex":300,"rate":581,"stacks":3},
                  {"at":{{Start}},"ex":100,"rate":581,"stacks":1},
                  {"at":0,"ex":999,"rate":581,"stacks":9},
                  {"at":{{Start + 1000}},"ex":200,"rate":581,"stacks":2}]}
                """);

            WealthHistory history = WealthHistory.Load(path);

            Assert.True(history.Readable);

            // The point with no timestamp is dropped; the rest come back oldest first.
            Assert.Equal(3, history.Count);
            Assert.Equal([100d, 200d, 300d], history.All.Select(point => point.Exalted));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MissingFileIsAnEmptyRecordRatherThanAFailure()
    {
        WealthHistory history = WealthHistory.Load(Scratch());

        Assert.True(history.Readable);
        Assert.Equal(0, history.Count);
        Assert.Equal(0, history.Since);
    }

    [Fact]
    public void APointCarriesItsOwnRateSoTheMarketCannotRewriteThePast()
    {
        // Sixty Divine held through a week when Divine doubled against Exalted is still sixty
        // Divine. Converting an old point at today's rate would draw that week as a halving the
        // player never experienced.
        var history = new WealthHistory();

        history.Note(Start, 6000, 100, 1);                                  // 60 Divine at 100
        history.Note(Start + WealthHistory.HeartbeatMs, 12000, 200, 1);     // still 60, rate doubled

        IReadOnlyList<WealthPoint> all = history.All;

        Assert.Equal(60, all[0].Divine, 3);
        Assert.Equal(60, all[1].Divine, 3);
    }

    [Fact]
    public void ARateOfNothingIsNoDivineReadingRatherThanADivisionByZero()
    {
        // What a point taken before the price book had an answer looks like.
        var history = new WealthHistory();
        history.Note(Start, 500, 0, 4);

        Assert.Equal(0, history.Latest!.Value.Divine);
    }

    [Fact]
    public void SavingClearsTheDirtyFlagAndNotingSetsIt()
    {
        string path = Scratch();
        try
        {
            var history = new WealthHistory();
            Assert.False(history.Dirty);

            history.Note(Start, 100, 581, 1);
            Assert.True(history.Dirty);

            history.Save(path);
            Assert.False(history.Dirty);

            history.Note(Start + WealthHistory.HeartbeatMs, 200, 581, 2);
            Assert.True(history.Dirty);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
