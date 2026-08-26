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

    private static InspectedItem Item(string path, string art, int stack)
        => new(1, path, "base", string.Empty, string.Empty, -1, null, stack, 0, art, [], []);

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
    public void WithNothingRecordedThereIsNoMovementToReport()
    {
        var tracker = new WealthTracker();

        Assert.Null(tracker.Moved(TimeSpan.FromHours(1), Start));

        tracker.Update(Purse(Exalted(100)), Real(), Start);

        // One point is a total, not a movement: there is nothing for it to have moved from.
        Assert.Null(tracker.Moved(TimeSpan.FromHours(1), Start));
    }

    [Fact]
    public void AWindowTheRecordDoesNotReachIsNotAChangeOfZero()
    {
        var tracker = new WealthTracker();
        tracker.Update(Purse(Exalted(100)), Real(), Start);

        Assert.Null(tracker.Over(TimeSpan.FromMinutes(5), Start));
    }
}
