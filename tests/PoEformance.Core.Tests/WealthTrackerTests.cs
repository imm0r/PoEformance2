using PoEformance.Features;
using PoEformance.Game.Items;

namespace PoEformance.Core.Tests;

/// <summary>
/// Which readings are fit to be written into a record that never resets.
/// </summary>
/// <remarks>
/// THE ASYMMETRY IS THE POINT. A readout that is briefly wrong corrects itself next tick; a
/// point written into the record is a dip somebody will later try to remember spending. So the
/// cases here are all the same shape: a reading that LOOKS like a loss and is not.
/// </remarks>
public class WealthTrackerTests
{
    private const long Start = 1_800_000_000_000;

    private static string Captured(string name)
    {
        foreach (string root in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var at = new DirectoryInfo(root);
            while (at is not null)
            {
                string candidate = Path.Combine(at.FullName, "fixtures", name);
                if (File.Exists(candidate))
                {
                    return File.ReadAllText(candidate);
                }

                at = at.Parent;
            }
        }

        throw new FileNotFoundException($"captured answer {name} not found");
    }

    private static PriceBook Real()
    {
        var book = new PriceBook();
        book.Add(PriceKind.Exchange, Captured("ninja-exchange.json"));
        return book;
    }

    /// <summary>
    /// The base name out of the SHIPPED table, exactly as the reader fills it in the game.
    /// </summary>
    /// <remarks>
    /// It used to be the literal "base", which no price line is ever called - harmless while the
    /// picture alone could answer, and a fixture that priced nothing the moment the picture
    /// stopped being allowed to. A test whose items are named something the book cannot know
    /// tests the book's fallback, not the tool.
    /// </remarks>
    private static readonly ItemNames Names = TestNames.Shipped;

    private static InspectedItem Item(string path, string art, int stack)
        => new(1, path, Names.Base(path), string.Empty, string.Empty, -1, null, stack, 0, art, [], []);

    private static PurseView Purse(params InspectedItem[] items)
        => new(
            [
                new StashPage(
                    1,
                    InventoryKind.Backpack,
                    "backpack",
                    12,
                    5,
                    [.. items.Select((item, i) => new StashSlot(new StashedItem((ulong)i + 1, i, 0, 1, 1), item))]),
            ],
            Start,
            1,
            string.Empty);

    private static InspectedItem Exalted(int stack)
        => Item("Metadata/Items/Currency/CurrencyAddModToRare", "Art/2DItems/Currency/CurrencyAddModToRare.dds", stack);

    private static InspectedItem Whetstone(int stack)
        => Item("Metadata/Items/Currency/CurrencyWeaponQuality", "Art/2DItems/Currency/CurrencyWeaponQuality.dds", stack);

    [Fact]
    public void APricedPurseIsCountedAndRecorded()
    {
        var tracker = new WealthTracker();

        Assert.True(tracker.Update(Purse(Exalted(40)), Real(), Start));

        Assert.Equal(40, tracker.Now.Exalted, 0);
        Assert.Equal(40d / 581, tracker.Now.Divine, 4);
        Assert.Equal(1, tracker.Now.Stacks);
        Assert.Equal(1, tracker.History.Count);
    }

    [Fact]
    public void ABookWithNoPricesRecordsNothing()
    {
        // Everything values at nothing, so the reading is a purse that "went to zero". Shown,
        // because the readout is allowed to say it has no prices - but never written down.
        var tracker = new WealthTracker();

        Assert.False(tracker.Update(Purse(Exalted(40)), new PriceBook(), Start));

        Assert.Equal(0, tracker.History.Count);
    }

    [Fact]
    public void AndAPurseTheBookPricedNothingOfRecordsNothingEither()
    {
        // The failure the Ready flag does not catch: a book full of prices for a league this
        // purse is not in, or a refresh that half-failed. Whetstone is deliberately unpriced in
        // the captured answer, so a purse of nothing but Whetstones is exactly this shape.
        var tracker = new WealthTracker();

        Assert.False(tracker.Update(Purse(Whetstone(200)), Real(), Start));

        Assert.Equal(0, tracker.History.Count);
        Assert.Equal(1, tracker.Now.Stacks);
        Assert.Equal(1, tracker.Now.Unpriced);
        Assert.True(tracker.Now.Incomplete);
    }

    [Fact]
    public void APartlyPricedPurseIsRecordedBecauseTheShapeIsStillTrue()
    {
        // The line. Some currency is permanently unpriced, so waiting for a complete purse means
        // waiting for ever - and the missing items are missing from every point equally, which
        // leaves the shape of the record, the thing it is read for, intact.
        var tracker = new WealthTracker();

        Assert.True(tracker.Update(Purse(Exalted(40), Whetstone(200)), Real(), Start));

        Assert.Equal(40, tracker.Now.Exalted, 0);
        Assert.Equal(1, tracker.Now.Priced);
        Assert.Equal(1, tracker.Now.Unpriced);
        Assert.Equal(1, tracker.History.Count);
    }

    [Fact]
    public void AnEmptyPurseIsARealReadingOfZero()
    {
        // Spending everything is a thing that happens, and it has to be recordable. The refusal
        // above is about a purse with things in it that priced at nothing - not about one that
        // is genuinely empty.
        var tracker = new WealthTracker();

        Assert.True(tracker.Update(Purse(), Real(), Start));

        Assert.Equal(0, tracker.Now.Exalted);
        Assert.Equal(0, tracker.Now.Stacks);
        Assert.Equal(1, tracker.History.Count);
    }

    [Fact]
    public void NothingToCountIsNotAReading()
    {
        var tracker = new WealthTracker();

        Assert.False(tracker.Update(null, Real(), Start));
        Assert.False(tracker.Update(Purse(Exalted(1)), null, Start));
        Assert.Equal(0, tracker.History.Count);
    }

    [Fact]
    public void TheStashHalfBeingStaleDoesNotStopTheCount()
    {
        // During a map the tabs are not loaded and the purse carries what they last held. That
        // is a reading, and refusing it would leave the record blank for exactly the stretch
        // where the currency is actually being picked up.
        var tracker = new WealthTracker();
        PurseView stale = Purse(Exalted(10)) with { StashSeenAt = Start - 3_600_000 };

        Assert.True(tracker.Update(stale, Real(), Start));
        Assert.Equal(Start - 3_600_000, tracker.Now.StashSeenAt);
    }

    [Fact]
    public void ChangeOverAWindowComesOffTheRecord()
    {
        var tracker = new WealthTracker();
        PriceBook book = Real();

        long later = Start + WealthHistory.HeartbeatMs;
        tracker.Update(Purse(Exalted(100)), book, Start);
        tracker.Update(Purse(Exalted(160)), book, later);

        Assert.Equal(60, tracker.Overall!.Value, 0);

        // A window with a reading old enough to be its baseline.
        Assert.Equal(60, tracker.Over(TimeSpan.FromMinutes(10), later)!.Value, 0);
    }

    [Fact]
    public void AWindowLongerThanTheRecordHasNoAnswerRatherThanTheWholeRecord()
    {
        // A record fifteen minutes old cannot say what changed over two hours. Answering with
        // everything it holds would report fifteen minutes of profit as two hours of it, which
        // is the number somebody would then divide to get an hourly rate.
        //
        // The fallback is the VIEW's to make, and it has to be labelled: fall back to Overall
        // and say "since the record began". That is why this primitive stays strict.
        var tracker = new WealthTracker();
        PriceBook book = Real();

        long later = Start + WealthHistory.HeartbeatMs;
        tracker.Update(Purse(Exalted(100)), book, Start);
        tracker.Update(Purse(Exalted(160)), book, later);

        Assert.Null(tracker.Over(TimeSpan.FromHours(2), later));
        Assert.Equal(60, tracker.Overall!.Value, 0);
    }

    [Fact]
    public void FallingBackToTheWholeRecordSaysThatIsWhatItDid()
    {
        // The fallback is carried rather than hidden. Fifteen minutes of profit reported as
        // "the last two hours" is a number somebody divides to get an hourly rate, and it is
        // wrong by a factor of eight - so what comes back has to say how long it really covers.
        var tracker = new WealthTracker();
        PriceBook book = Real();

        long later = Start + WealthHistory.HeartbeatMs;
        tracker.Update(Purse(Exalted(100)), book, Start);
        tracker.Update(Purse(Exalted(160)), book, later);

        WealthTracker.Movement fell = tracker.Moved(TimeSpan.FromHours(2), later)!.Value;

        Assert.Equal(60, fell.Exalted, 0);
        Assert.True(fell.WholeRecord);
        Assert.Equal(TimeSpan.FromMilliseconds(WealthHistory.HeartbeatMs), fell.Over);

        // And when the record DOES span the window, it says so by not claiming otherwise.
        WealthTracker.Movement fits = tracker.Moved(TimeSpan.FromMinutes(10), later)!.Value;

        Assert.False(fits.WholeRecord);
        Assert.Equal(TimeSpan.FromMinutes(10), fits.Over);
    }

    [Fact]
    public void WithNothingCountedAtAllThereIsNoMovementToReport()
    {
        var tracker = new WealthTracker();

        Assert.Null(tracker.Moved(TimeSpan.FromHours(1), Start));
        Assert.Null(tracker.Overall);
    }

    [Fact]
    public void TheChangeIsMeasuredAgainstTheLIVECountRatherThanTheLastRecordedPoint()
    {
        // THE BUG THIS EXISTS FOR, off a real screenshot: the panel read "0 ex" as the total and
        // "+494.6k ex over 33m" as the change, at the same moment. The total was the live count
        // and the change ended at the last RECORDED point, and the two had drifted apart because
        // the drop fell inside the thirty seconds during which no point may be written. Both
        // halves were doing as they were told; together they described a purse that never was.
        var tracker = new WealthTracker();
        PriceBook book = Real();

        Assert.True(tracker.Update(Purse(Exalted(100)), book, Start));

        // Too soon to be written down - but it IS what the purse is worth now.
        Assert.False(tracker.Update(Purse(Exalted(160)), book, Start + 1_000));
        Assert.Equal(1, tracker.History.Count);

        Assert.Equal(60, tracker.Overall!.Value, 0);
        Assert.Equal(60, tracker.Moved(TimeSpan.FromHours(1), Start + 1_000)!.Value.Exalted, 0);
    }

    [Fact]
    public void ButNotWhenTheLiveCountIsOneItWouldRefuseToRecord()
    {
        // The other half of it. With no prices the live count is a zero meaning "not known", and
        // measuring against THAT would report the whole purse as having been spent - which is
        // the exact shape of the failure the refusal upstream exists to keep out of the record.
        var tracker = new WealthTracker();

        Assert.True(tracker.Update(Purse(Exalted(100)), Real(), Start));
        Assert.False(tracker.Update(Purse(Exalted(100)), new PriceBook(), Start + 60_000));

        Assert.False(tracker.Trusted);
        Assert.Equal(0, tracker.Now.Exalted);

        // Falls back to the last point that could be believed, so the change is 0 and not -100.
        Assert.Equal(0, tracker.Overall!.Value, 0);
    }

    [Fact]
    public void WhatIsSHOWNAndWhatTheChangeMeasuresToAreTheSameFigure()
    {
        // The invariant behind the "0 ex beside +494.6k" screen. Whatever a view draws as the
        // total has to be the same number every change ends at, in every state - live, stale,
        // and before anything has been counted.
        var tracker = new WealthTracker();
        PriceBook book = Real();

        Assert.Null(tracker.Showing);
        Assert.Null(tracker.Overall);

        tracker.Update(Purse(Exalted(100)), book, Start);
        Assert.True(tracker.Showing!.Value.Live);
        Assert.Equal(100, tracker.Showing!.Value.Exalted, 0);

        // Prices vanish: what is shown falls back to the last believable figure, and the change
        // measured against it is therefore zero rather than the whole purse having been spent.
        tracker.Update(Purse(Exalted(100)), new PriceBook(), Start + 60_000);
        Assert.False(tracker.Showing!.Value.Live);
        Assert.Equal(100, tracker.Showing!.Value.Exalted, 0);
        Assert.Equal(0, tracker.Overall!.Value, 0);
    }

    [Fact]
    public void AReadingTakenWhileThePriceBookIsStillArrivingIsNotRecorded()
    {
        // MEASURED, off the first record this ever wrote: its opening point carried a rate of
        // 381.3 where every point thirty seconds later carried 473.4 - across an unchanged 49
        // stacks. The book was still being assembled, so that point understated the purse by
        // nearly forty per cent, permanently, in a record that never resets.
        //
        // Ready is not enough to catch it: "has a rate and some prices" is also true of a
        // half-arrived refresh.
        var tracker = new WealthTracker();

        Assert.False(tracker.Update(Purse(Exalted(100)), Real(), Start, settling: true));
        Assert.Equal(0, tracker.History.Count);
        Assert.False(tracker.Trusted);

        // And the same reading once it has settled.
        Assert.True(tracker.Update(Purse(Exalted(100)), Real(), Start, settling: false));
        Assert.Equal(1, tracker.History.Count);
    }

    [Fact]
    public void AWindowTheRecordDoesNotReachIsNotAChangeOfZero()
    {
        var tracker = new WealthTracker();
        tracker.Update(Purse(Exalted(100)), Real(), Start);

        Assert.Null(tracker.Over(TimeSpan.FromMinutes(5), Start));
    }

    [Fact]
    public void AFrozenTotalGetsAFrozenChangeRatherThanASlidingOne()
    {
        // REPORTED FROM A MAP WITH NO PRICES LOADED: the same 516 div beside "+8 div", and two
        // minutes later the same 516 div beside "-42 div". Nothing had been counted in between -
        // the figure was frozen at the last recorded point, correctly - but the baseline was
        // still "one window before NOW", so it walked forward through the record while the
        // endpoint stood still. Both halves came off the record; they were about different
        // stretches of it.
        //
        // THE STEP HAS TO SIT IN THE BAND THE BASELINE CROSSES, or the two queries land on the
        // same point and the test passes against the bug. A first attempt at this did exactly
        // that: every point fell inside both windows, so both readings agreed and proved nothing.
        var tracker = new WealthTracker();
        PriceBook book = Real();
        var window = TimeSpan.FromMinutes(10);

        long begins = Start;
        tracker.Update(Purse(Exalted(100)), book, begins);                    // reaches back far enough
        tracker.Update(Purse(Exalted(100)), book, begins + (20 * 60_000));
        tracker.Update(Purse(Exalted(150)), book, begins + (22 * 60_000));    // the step
        tracker.Update(Purse(Exalted(150)), book, begins + (25 * 60_000));

        long frozenAt = begins + (25 * 60_000);

        // The book goes away - a map with nothing fetched. Nothing more is written, and the
        // panel falls back to the last recorded point.
        Assert.False(tracker.Update(Purse(Exalted(150)), new PriceBook(), frozenAt + 60_000));
        Assert.False(tracker.Showing!.Value.Live);

        // Two queries whose baselines fall either side of the step: 31 minutes in, the window
        // reaches back to minute 21 (before it); 33 minutes in, to minute 23 (after it).
        double? then = tracker.Over(window, begins + (31 * 60_000));
        double? later = tracker.Over(window, begins + (33 * 60_000));

        // Non-null first: two nulls are equal too, and that would prove nothing.
        Assert.NotNull(then);
        Assert.NotNull(later);

        // A total that is not moving must sit beside a change that is not moving either.
        Assert.Equal(then!.Value, later!.Value, 6);
    }

    [Fact]
    public void ALiveTotalStillMeasuresBackFromNow()
    {
        // The other half: while the count CAN be believed, the window ends at the clock, which
        // is what makes "the last hour" mean the last hour.
        var tracker = new WealthTracker();
        PriceBook book = Real();

        tracker.Update(Purse(Exalted(100)), book, Start);
        tracker.Update(Purse(Exalted(160)), book, Start + WealthHistory.HeartbeatMs);

        Assert.True(tracker.Showing!.Value.Live);
        Assert.Equal(60, tracker.Over(TimeSpan.FromMinutes(10), Start + WealthHistory.HeartbeatMs)!.Value, 0);
    }

    [Fact]
    public void APRICEREFRESHIsNotLoot()
    {
        // THE WHOLE POINT. A purse of six hundred Divine moves by tens when poe.ninja refreshes,
        // and the change line reads that as a map's takings. It is the same items.
        var tracker = new WealthTracker();
        PriceBook book = Real();

        tracker.Update(Purse(Exalted(100)), book, Start);
        tracker.Update(Purse(Exalted(100)), book, Start + WealthHistory.HeartbeatMs);

        WealthTracker.MadeOf made = tracker.Made(TimeSpan.FromMinutes(10), Start + WealthHistory.HeartbeatMs)!.Value;

        // Nothing gathered, nothing repriced - the same book twice.
        Assert.Equal(0, made.Gathered, 6);
        Assert.Equal(0, made.Repriced, 6);
    }

    [Fact]
    public void WHATWASGATHEREDIsWhatTheHoldingsDid()
    {
        var tracker = new WealthTracker();
        PriceBook book = Real();

        tracker.Update(Purse(Exalted(100)), book, Start);
        tracker.Update(Purse(Exalted(160)), book, Start + WealthHistory.HeartbeatMs);

        long at = Start + WealthHistory.HeartbeatMs;
        WealthTracker.MadeOf made = tracker.Made(TimeSpan.FromMinutes(10), at)!.Value;

        // On an unchanged book, all of the movement is gathered - compared against the movement
        // itself rather than a round number, since an Exalted Orb is not priced at exactly one.
        Assert.Equal(tracker.Over(TimeSpan.FromMinutes(10), at)!.Value, made.Gathered, 6);
        Assert.Equal(0, made.Repriced, 6);
    }

    [Fact]
    public void THETWOHALVESAlwaysAddUpToTheMovementItself()
    {
        // Whatever the split says, it has to describe the SAME movement the line above shows -
        // two figures on screen that do not add up is how somebody stops believing both.
        var tracker = new WealthTracker();
        PriceBook book = Real();

        tracker.Update(Purse(Exalted(100)), book, Start);
        tracker.Update(Purse(Exalted(250)), book, Start + WealthHistory.HeartbeatMs);

        long at = Start + WealthHistory.HeartbeatMs;
        double moved = tracker.Over(TimeSpan.FromMinutes(10), at)!.Value;

        Assert.Equal(moved, tracker.Made(TimeSpan.FromMinutes(10), at)!.Value.Exalted, 6);
    }

    [Fact]
    public void THEDRIFTPicksUpWhereTheRecordLeftOff()
    {
        // A run that began counting again from zero would put its first point below every point
        // before it, so any window spanning the restart would report a price collapse that never
        // happened.
        var history = new WealthHistory();
        history.Note(Start, 100, 400, 1, 250);

        var tracker = new WealthTracker(history);
        tracker.Update(Purse(Exalted(100)), Real(), Start + WealthHistory.HeartbeatMs);

        WealthPoint written = history.Latest!.Value;

        Assert.True(written.At > Start, "the new point should have been written");
        Assert.Equal(250, written.Drift, 6);
    }

    /// <summary>The same captured answer with every price moved, as a refresh moves them.</summary>
    /// <remarks>
    /// The real fixture with primaryValue scaled, so the SHAPE of the book - which items it
    /// knows, how they relate - is the captured one. What is synthetic is only that they all
    /// moved by the same fraction, which no real refresh does; the arithmetic under test does
    /// not care, since it compares two whole valuations rather than any single price.
    /// </remarks>
    private static PriceBook Moved(double by)
    {
        string json = System.Text.RegularExpressions.Regex.Replace(
            Captured("ninja-exchange.json"),
            @"""primaryValue"":\s*([0-9.eE+-]+)",
            match => $@"""primaryValue"": {double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) * by}",
            System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromSeconds(5));

        var book = new PriceBook();
        book.Add(PriceKind.Exchange, json);
        return book;
    }

    [Fact]
    public void APRICEREFRESHLandsInTheRepricedHalfRatherThanTheGatheredOne()
    {
        // THE WHOLE POINT OF THE SPLIT. A purse of six hundred Divine moves by tens when the
        // prices refresh, and one number cannot say whether that was a map's takings.
        var tracker = new WealthTracker();

        tracker.Update(Purse(Exalted(1000)), Real(), Start);
        tracker.Update(Purse(Exalted(1000)), Moved(1.10), Start + WealthHistory.HeartbeatMs);

        long at = Start + WealthHistory.HeartbeatMs;
        WealthTracker.MadeOf made = tracker.Made(TimeSpan.FromMinutes(10), at)!.Value;

        // Not one orb changed hands, so nothing was gathered - all of the movement is prices.
        Assert.Equal(0, made.Gathered, 6);
        Assert.NotEqual(0, made.Repriced);
        Assert.Equal(tracker.Over(TimeSpan.FromMinutes(10), at)!.Value, made.Exalted, 6);
    }

    [Fact]
    public void LOOTANDAPriceMoveInTheSameStretchAreToldApart()
    {
        var tracker = new WealthTracker();

        tracker.Update(Purse(Exalted(1000)), Real(), Start);
        tracker.Update(Purse(Exalted(1400)), Moved(1.10), Start + WealthHistory.HeartbeatMs);

        long at = Start + WealthHistory.HeartbeatMs;
        WealthTracker.MadeOf made = tracker.Made(TimeSpan.FromMinutes(10), at)!.Value;

        // Four hundred orbs picked up, valued at what they were worth when they arrived, and the
        // rest of the movement is the thousand already held becoming dearer.
        Assert.True(made.Gathered > 0, $"gathered came out {made.Gathered}");
        Assert.True(made.Repriced > 0, $"repriced came out {made.Repriced}");
        Assert.Equal(tracker.Over(TimeSpan.FromMinutes(10), at)!.Value, made.Exalted, 6);
    }
}
