namespace PoEformance.Features;

/// <summary>
/// Watches what the currency is worth and writes the record of it.
/// </summary>
/// <remarks>
/// THE ONE JOB HERE IS DECIDING WHETHER A READING IS FIT TO BE WRITTEN DOWN. Counting the purse
/// belongs to <see cref="StashInspector"/>, pricing it to <see cref="PriceBook"/>, and keeping
/// the record to <see cref="WealthHistory"/>. What is left - and what nothing else can do,
/// because it needs all three at once - is refusing the readings that would put a fact in the
/// record that was never true.
///
/// A GRAPH IS BELIEVED IN A WAY A LIVE NUMBER IS NOT. A readout that is briefly wrong corrects
/// itself the next tick and nobody is any the wiser. A point written into a record that never
/// resets is there for good: it is a dip somebody will later try to remember spending, and no
/// amount of correct points after it takes it back. So the bar for writing is higher than the
/// bar for showing, and the two are deliberately different here - see <see cref="Fit"/>.
///
/// Fed from the reader thread and read from the renderer. Everything it holds is either
/// immutable or already guarded by the history's own lock.
/// </remarks>
public sealed class WealthTracker
{
    private readonly WealthHistory _history;
    private Reading _now = Reading.Nothing;

    public WealthTracker(WealthHistory? history = null) => _history = history ?? new WealthHistory();

    /// <summary>What the purse came to at the last count, whether or not it was recorded.</summary>
    /// <param name="Exalted">The total.</param>
    /// <param name="Rate">Exalted per Divine, or 0 when the book has no rate.</param>
    /// <param name="Stacks">How many stacks of currency were there.</param>
    /// <param name="Priced">How many of them the book could price.</param>
    /// <param name="Unpriced">And how many it could not.</param>
    /// <param name="At">Unix milliseconds of the count, or 0 when there has not been one.</param>
    /// <param name="StashSeenAt">When the stash half was last actually looked at.</param>
    public readonly record struct Reading(
        double Exalted,
        double Rate,
        int Stacks,
        int Priced,
        int Unpriced,
        long At,
        long StashSeenAt)
    {
        public static Reading Nothing { get; }

        /// <summary>The same total in Divine, or 0 when there is no rate to divide by.</summary>
        public double Divine => Rate > 0 ? Exalted / Rate : 0;

        /// <summary>Whether anything has been counted at all.</summary>
        public bool Any => At > 0;

        /// <summary>Whether some of the purse is missing from the total.</summary>
        public bool Incomplete => Unpriced > 0;
    }

    /// <summary>The record this is writing. Never replaced, so a view can hold on to it.</summary>
    public WealthHistory History => _history;

    /// <summary>The last count, priced. Cheap to read every frame.</summary>
    public Reading Now => _now;

    /// <summary>
    /// Whether a reading is fit to go into the record.
    /// </summary>
    /// <remarks>
    /// TWO REFUSALS, both of things that look like a loss and are not:
    ///
    /// - A BOOK THAT IS NOT READY. No prices, or no rate to convert them by. Everything values
    ///   at nothing, and a purse that "went to zero" is written down.
    /// - A PURSE THE BOOK PRICED NOTHING OF while there was plainly something in it. That is not
    ///   an empty purse, it is a book that has never heard of what is in this one - a league it
    ///   has not been refreshed for, a refresh that half-failed - and the reading it produces is
    ///   a zero that is indistinguishable from having spent everything.
    ///
    /// A PARTLY-priced purse IS recorded, deliberately, and that is the line. Some currency is
    /// permanently unpriced - poe.ninja does not quote everything, and the volume gate throws
    /// away the lines nobody trades - so waiting for a complete purse would mean waiting for
    /// ever. Those items are missing from every point equally, so the SHAPE of the record, which
    /// is what a wealth graph is read for, stays true; the views say how many are missing.
    /// </remarks>
    public static bool Fit(Reading reading, bool bookReady)
        => bookReady && reading.At > 0 && (reading.Priced > 0 || reading.Stacks == 0);

    /// <summary>
    /// Prices the latest count and records it if it is fit to be.
    /// </summary>
    /// <param name="purse">The last count from <see cref="StashInspector.Purse"/>.</param>
    /// <param name="book">What things are worth. Its rate is stored with the point.</param>
    /// <param name="nowMs">Unix milliseconds.</param>
    /// <param name="trade">The other half of "what is this worth", where there is one.</param>
    /// <returns>True when a point was written to the record.</returns>
    public bool Update(PurseView? purse, PriceBook? book, long nowMs, TradePrices? trade = null)
    {
        if (purse is null || book is null)
        {
            return false;
        }

        Valued worth = book.Purse(purse.Pages, trade);

        var reading = new Reading(
            worth.Exalted,
            book.Rate,
            CurrencyPurse.Stacks(purse.Pages),
            worth.Priced,
            worth.Unpriced,
            nowMs,
            purse.StashSeenAt);

        _now = reading;

        return Fit(reading, book.Ready)
               && _history.Note(nowMs, reading.Exalted, reading.Rate, reading.Stacks);
    }

    /// <summary>How much the purse has moved over the last stretch of time, in Exalted.</summary>
    /// <remarks>
    /// Null when the record does not reach that far back, which is not the same as no change -
    /// see <see cref="WealthHistory.ChangeSince"/>.
    /// </remarks>
    public double? Over(TimeSpan span, long nowMs)
        => _history.ChangeSince(nowMs - (long)span.TotalMilliseconds);

    /// <summary>Everything the record holds, first point to last.</summary>
    public double? Overall => _history.Change;

    /// <summary>
    /// How much the purse moved, and over how long it ACTUALLY moved that much.
    /// </summary>
    /// <param name="Exalted">The change.</param>
    /// <param name="Over">The stretch it really covers, which may be shorter than the one asked for.</param>
    /// <param name="WholeRecord">
    /// Whether the record was too young to answer the question asked, so this is everything it
    /// holds instead. A view must SAY so when this is set - see <see cref="Moved"/>.
    /// </param>
    public readonly record struct Movement(double Exalted, TimeSpan Over, bool WholeRecord);

    /// <summary>
    /// The change over a stretch, falling back to the whole record when it is younger than that.
    /// </summary>
    /// <remarks>
    /// THE FALLBACK IS CARRIED RATHER THAN HIDDEN, which is the whole reason this exists instead
    /// of each view doing its own. Fifteen minutes of profit reported as "the last two hours" is
    /// a number somebody divides to get an hourly rate, and it is wrong by a factor of eight. So
    /// what comes back says how long it really covers, and a view that draws the figure without
    /// the span is drawing a lie it was handed the truth about.
    ///
    /// Null when there is no movement to report at all: fewer than two points, or one point and
    /// nothing to compare it against.
    /// </remarks>
    public Movement? Moved(TimeSpan wanted, long nowMs)
    {
        if (Over(wanted, nowMs) is { } inWindow)
        {
            return new Movement(inWindow, wanted, false);
        }

        if (Overall is not { } whole || _history.Earliest is not { } first)
        {
            return null;
        }

        return new Movement(whole, TimeSpan.FromMilliseconds(Math.Max(0, nowMs - first.At)), true);
    }
}
