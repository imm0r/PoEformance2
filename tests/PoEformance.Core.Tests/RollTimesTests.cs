using PoEformance.Features;

namespace PoEformance.Core.Tests;

/// <summary>
/// The steering's own measurement, as something that can actually be read.
/// </summary>
/// <remarks>
/// WHY THIS REPLACED A SINGLE NUMBER. The confirmation time was first shown as the latest
/// reading, and the owner's answer to that was the finding: "eigentlich nicht lesbar, weil da im
/// Bruchteil einer Sekunde der Text direkt wieder überschrieben wird". A roll happens about once
/// a second in a fight, so a status line carrying the last one is gone before it can be read -
/// and a fight is the only place the measurement is ever taken.
///
/// IT IS ALSO THE BETTER MEASUREMENT, which is what makes this a fix rather than a presentation
/// change. One confirmation is one frame of one moment, and a stutter, a zone load or a shader
/// compile all produce a single large number indistinguishable from a finding. A spread over the
/// last thirty-two says what the machine does, and the range beside the middle value is what
/// keeps the outlier visible instead of averaging it away.
/// </remarks>
public class RollTimesTests
{
    /// <summary>Nothing measured says nothing, rather than saying "nothing".</summary>
    /// <remarks>
    /// It is appended to a status line that reads perfectly well on its own, so an empty string
    /// is the right answer - a permanent "no rolls yet" is worse than a shorter line.
    /// </remarks>
    [Fact]
    public void BeforeAnyRollItSaysNothingAtAll()
    {
        var times = new RollTimes();

        Assert.Equal(string.Empty, times.Describe());
        Assert.Equal(0, times.Count);
    }

    /// <summary>One roll reads as one roll, singular, without a range.</summary>
    [Fact]
    public void OneRollReadsAsOne()
    {
        var times = new RollTimes();
        times.Add(18);

        Assert.Equal("1 roll seen in 18 ms", times.Describe());
    }

    /// <summary>The ordinary case: a spread and the value in the middle of it.</summary>
    [Fact]
    public void SeveralRollsReportTheirSpread()
    {
        var times = new RollTimes();
        foreach (int ms in new[] { 19, 17, 24, 18, 20 })
        {
            times.Add(ms);
        }

        Assert.Equal("5 rolls seen in 17-24 ms (middle 19)", times.Describe());
    }

    /// <summary>
    /// One stutter moves the range and not the middle - which is the point of both.
    /// </summary>
    /// <remarks>
    /// THE WHOLE ARGUMENT FOR THE MIDDLE VALUE and for printing the range beside it. A single
    /// 180 ms frame would drag a mean of these five up by more than thirty milliseconds and make
    /// a healthy machine look like one that needs the ceiling raised. The middle ignores it; the
    /// range is what stops it being hidden.
    /// </remarks>
    [Fact]
    public void OneStutterIsVisibleWithoutMovingTheMiddle()
    {
        var times = new RollTimes();
        foreach (int ms in new[] { 19, 17, 180, 18, 20 })
        {
            times.Add(ms);
        }

        string steady = times.Describe();

        Assert.Contains("17-180 ms", steady, StringComparison.Ordinal);
        Assert.Contains("middle 19", steady, StringComparison.Ordinal);
    }

    /// <summary>Holds that ran to the ceiling are counted and named separately.</summary>
    /// <remarks>
    /// They are not failures on their own - a roll chained out of another one never changes the
    /// animation id and lands here every time - but a session where most rolls reach the ceiling
    /// is the one worth looking at, and that cannot be seen if they are folded into the times.
    /// </remarks>
    [Fact]
    public void CeilingHoldsAreCountedApartFromTheTimes()
    {
        var times = new RollTimes();
        times.Add(18);
        times.Add(20);
        times.Add(-1);

        Assert.Equal("3 rolls, 2 seen in 18-20 ms (middle 20), 1 on the ceiling", times.Describe());
    }

    /// <summary>Nothing confirmed at all says so, without inventing a range.</summary>
    [Fact]
    public void NoneConfirmedSaysSo()
    {
        var times = new RollTimes();
        times.Add(-1);
        times.Add(-1);

        Assert.Equal("2 rolls, none confirmed", times.Describe());
    }

    /// <summary>Identical times read as one number rather than as a range of nothing.</summary>
    [Fact]
    public void OneRepeatedTimeIsNotARange()
    {
        var times = new RollTimes();
        times.Add(17);
        times.Add(17);
        times.Add(17);

        Assert.Equal("3 rolls seen in 17 ms", times.Describe());
    }

    /// <summary>
    /// Only the last few are kept, so the reading describes where you are playing now.
    /// </summary>
    /// <remarks>
    /// An area with a heavy effect load runs at a different frame rate from a hideout, and a
    /// window that never forgets would blend the two into a spread describing neither.
    /// </remarks>
    [Fact]
    public void OnlyTheLastFewAreRemembered()
    {
        var times = new RollTimes();
        for (int i = 0; i < RollTimes.Remembered * 2; i++)
        {
            times.Add(100 + i);
        }

        Assert.Equal(RollTimes.Remembered, times.Count);

        // The oldest survivor is the first of the second half, so nothing from the first
        // half can still be in the range.
        Assert.Equal(100 + RollTimes.Remembered, times.Confirmed[0]);
    }

    /// <summary>Clearing forgets everything and goes back to saying nothing.</summary>
    [Fact]
    public void ClearingEmptiesIt()
    {
        var times = new RollTimes();
        times.Add(18);
        times.Clear();

        Assert.Equal(0, times.Count);
        Assert.Equal(string.Empty, times.Describe());
    }

    /// <summary>
    /// Written from the steering thread while the renderer reads it, sixty times a second.
    /// </summary>
    /// <remarks>
    /// A roll runs on its own thread and the status line is built during a draw, so these two
    /// genuinely do meet. Worth a test rather than a comment because the failure is an exception
    /// out of the middle of a collection being enumerated as it changes - which would surface as
    /// the overlay dying during a fight, at the moment it is most needed.
    /// </remarks>
    [Fact]
    public void ItSurvivesBeingReadWhileItIsWrittenTo()
    {
        const int Rounds = 20_000;
        var times = new RollTimes();

        // Both sides run a FIXED number of rounds rather than one racing the other to a flag.
        // The first shape of this test had the reader spin until the writer set a flag, and the
        // writer finished first every time - so it asserted nothing at all while passing.
        var writer = new Thread(() =>
        {
            for (int i = 0; i < Rounds; i++)
            {
                times.Add(i % 7 == 0 ? -1 : 15 + (i % 30));
            }
        });

        writer.Start();

        for (int i = 0; i < Rounds; i++)
        {
            Assert.NotNull(times.Describe());
            Assert.True(times.Count <= RollTimes.Remembered);
        }

        writer.Join();

        Assert.Equal(RollTimes.Remembered, times.Count);
    }
}
