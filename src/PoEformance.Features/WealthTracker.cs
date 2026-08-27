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
    private bool _trusted;

    public WealthTracker(WealthHistory? history = null)
    {
        _history = history ?? new WealthHistory();

        // PICKED UP WHERE THE RECORD LEFT OFF, not started at zero. Only differences of the
        // drift are ever read, but they are read ACROSS points - and a run that began counting
        // again from zero would put its first point below every point before it, so any window
        // spanning the restart would report a price collapse that never happened.
        _drift = _history.Latest?.Drift ?? 0;
    }

    /// <summary>The book the last reading was priced with, for measuring what the next one moved.</summary>
    private PriceBook? _priced;

    /// <summary>
    /// Everything the PRICES have moved across this record, in Exalted, up to the last reading.
    /// </summary>
    /// <remarks>
    /// The book only. A unique whose asking price moved on the trade site is not counted here
    /// and lands in the gathered half, which is the smaller error of the two available: the book
    /// covers the currency that makes up almost all of a purse, and the alternative is pricing
    /// every unique twice per reading to catch the rest.
    /// </remarks>
    private double _drift;

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

    /// <summary>Whether the last count was one this would have written down. See <see cref="Fit"/>.</summary>
    public bool Trusted => _trusted;

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
    ///
    /// A THIRD REFUSAL, added after reading a real record: a book that is still being ASSEMBLED.
    /// The first point ever written carried a rate of 381.3 where every point thirty seconds
    /// later carried 473.4, across an unchanged 49 stacks - so that point understated the purse
    /// by nearly forty per cent, permanently, in a record that never resets. Ready is not
    /// enough: it means "has a rate and some prices", which a half-arrived refresh also has.
    /// </remarks>
    /// <param name="settling">
    /// Whether the price store is mid-refresh. From <c>PriceStore.Busy</c> - passed in rather
    /// than reached for, because this layer prices things and does not fetch them.
    /// </param>
    public static bool Fit(Reading reading, bool bookReady, bool settling = false)
        => bookReady && !settling && reading.At > 0 && (reading.Priced > 0 || reading.Stacks == 0);

    /// <summary>
    /// Prices the latest count and records it if it is fit to be.
    /// </summary>
    /// <param name="purse">The last count from <see cref="StashInspector.Purse"/>.</param>
    /// <param name="book">What things are worth. Its rate is stored with the point.</param>
    /// <param name="nowMs">Unix milliseconds.</param>
    /// <param name="trade">The other half of "what is this worth", where there is one.</param>
    /// <returns>True when a point was written to the record.</returns>
    /// <param name="settling">Whether the price store is mid-refresh. See <see cref="Fit"/>.</param>
    public bool Update(
        PurseView? purse, PriceBook? book, long nowMs, TradePrices? trade = null, bool settling = false)
    {
        if (purse is null || book is null)
        {
            return false;
        }

        Valued worth = book.Purse(purse.Pages, trade);

        // WHAT THE PRICES DID, measured against THE SAME HOLDINGS. This is the only moment it
        // can be measured: both books are in hand and the purse has not moved between them, so
        // the difference is the prices and nothing else. A record of totals cannot be taken
        // apart afterwards - "the purse went from 200k to 220k" does not say whether twenty
        // thousand was gathered or repriced.
        //
        // Only when the book has actually been REPLACED. Handed the same book twice, the
        // difference is zero anyway, but pricing the whole purse a second time to learn that is
        // a walk over every stack for nothing.
        if (_priced is { } before && !ReferenceEquals(before, book))
        {
            _drift += worth.Exalted - before.Purse(purse.Pages, trade).Exalted;
        }

        _priced = book;

        var reading = new Reading(
            worth.Exalted,
            book.Rate,
            CurrencyPurse.Stacks(purse.Pages),
            worth.Priced,
            worth.Unpriced,
            nowMs,
            purse.StashSeenAt);

        _now = reading;
        _trusted = Fit(reading, book.Ready, settling);

        return _trusted && _history.Note(nowMs, reading.Exalted, reading.Rate, reading.Stacks, _drift);
    }

    /// <summary>
    /// What the purse is worth NOW, for measuring against - the live count where it can be
    /// believed, and the last recorded point where it cannot.
    /// </summary>
    /// <remarks>
    /// THE TWO FIGURES ON SCREEN HAVE TO COME FROM THE SAME PLACE, and this is where that is
    /// decided. A panel showing "0 ex" beside "+494.6k over 33m" happened on a real screen: the
    /// total was the live count and the change ended at the last RECORDED point, and the two had
    /// drifted apart because the crash to zero fell inside the thirty seconds during which no
    /// point may be written. Both halves were doing what they were told; together they described
    /// a purse that never existed.
    ///
    /// The live count wins where it is fit to be written down, because that is the "Ist-Zustand"
    /// the panel is read for. Where it is NOT - no prices yet, a book mid-refresh - the last
    /// recorded point is the honest endpoint, since the live figure in that state is a zero that
    /// means "not known" rather than "spent".
    /// </remarks>
    /// <param name="Live">
    /// Whether this is the count as of a moment ago, or the last one that could be believed. A
    /// view has to say which - a stale figure presented as current is the same lie as a wrong
    /// one, just harder to notice.
    /// </param>
    /// <param name="At">
    /// THE MOMENT THIS FIGURE DESCRIBES, which is not always now. Carried because a change is
    /// measured from a window ending at the figure, and a frozen figure with a window that
    /// keeps sliding is two halves about different moments all over again - see
    /// <see cref="Over"/>, which is where that was still true after the value itself was fixed.
    /// </param>
    public readonly record struct Shown(double Exalted, double Rate, bool Live, long At)
    {
        /// <summary>The same amount in Divine, or 0 when there is no rate to divide by.</summary>
        public double Divine => Rate > 0 ? Exalted / Rate : 0;
    }

    /// <summary>
    /// The figure to put on screen - and the one every change is measured to.
    /// </summary>
    /// <remarks>
    /// ONE SOURCE FOR BOTH HALVES. See the type remarks for the screen this comes from: a total
    /// and a change that were each correct about a different moment, side by side, describing a
    /// purse that never existed. Anything drawing both must take them from here.
    ///
    /// Null only before anything has ever been counted or recorded.
    /// </remarks>
    public Shown? Showing
        => _trusted && _now.Any ? new Shown(_now.Exalted, _now.Rate, true, _now.At)
            : _history.Latest is { } last ? new Shown(last.Exalted, last.Rate, false, last.At)
            : null;

    /// <summary>How much the purse has moved over the last stretch of time, in Exalted.</summary>
    /// <remarks>
    /// Null when the record does not reach that far back, which is not the same as no change -
    /// see <see cref="WealthHistory.ChangeSince"/>.
    ///
    /// THE WINDOW ENDS AT THE FIGURE, not at the clock, and that is the whole of this. Where the
    /// count cannot be believed the figure freezes at the last recorded point - correctly - but
    /// the baseline went on being "one hour before now", so it walked forward through the record
    /// while the endpoint stood still. Reported from a map with no prices loaded: the same
    /// 516 div beside "+8 div" and, two minutes later, the same 516 div beside "-42 div". Both
    /// halves came from the record; they were about different hours of it.
    ///
    /// Measuring back from the figure's own moment freezes both ends together, so a total that
    /// is not moving sits beside a change that is not moving either.
    /// </remarks>
    public double? Over(TimeSpan span, long nowMs)
    {
        if (Showing is not { } shown)
        {
            return null;
        }

        long ends = shown.Live ? nowMs : shown.At;
        return _history.At(ends - (long)span.TotalMilliseconds) is { } from
            ? shown.Exalted - from.Exalted
            : null;
    }

    /// <summary>Everything the record holds, first point to now.</summary>
    /// <remarks>
    /// Both ends are fixed points here, so this never had the sliding-baseline problem
    /// <see cref="Over"/> did - the earliest reading does not move as the clock does.
    /// </remarks>
    public double? Overall
        => Showing is { } shown && _history.Earliest is { } from ? shown.Exalted - from.Exalted : null;

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

    /// <summary>What a stretch of movement was made of.</summary>
    /// <param name="Gathered">What was picked up or spent, at the prices of the moment.</param>
    /// <param name="Repriced">And what the same holdings became worth as the prices moved.</param>
    /// <remarks>
    /// THERE IS NO "IS THIS TRUSTWORTHY" FLAG, and one was written before it was noticed that it
    /// could not be filled in honestly. A point recorded before the drift existed carries zero;
    /// so does a point recorded after it, over a stretch where the prices genuinely did not
    /// move. The two are the same number and nothing distinguishes them.
    ///
    /// What that costs is bounded and heals itself: the first window reaching back past the
    /// upgrade attributes this run's drift to it, and every window after the record has filled
    /// with new points is exact.
    /// </remarks>
    public readonly record struct MadeOf(double Gathered, double Repriced)
    {
        /// <summary>The two halves together, which is the movement itself.</summary>
        public double Exalted => Gathered + Repriced;
    }

    /// <summary>
    /// How much of a stretch was picked up and how much was the prices moving.
    /// </summary>
    /// <remarks>
    /// ONE SUBTRACTION, because the drift is cumulative: the price share of any stretch is its
    /// drift at the end less its drift at the start, and everything else that moved is what was
    /// actually gathered. The decomposition itself happened at each reading, where both books
    /// were in hand - see the drift field.
    /// </remarks>
    public MadeOf? Made(TimeSpan span, long nowMs)
    {
        if (Showing is not { } shown)
        {
            return null;
        }

        long ends = shown.Live ? nowMs : shown.At;
        if (_history.At(ends - (long)span.TotalMilliseconds) is not { } from)
        {
            return null;
        }

        double moved = shown.Exalted - from.Exalted;
        double repriced = _drift - from.Drift;
        return new MadeOf(moved - repriced, repriced);
    }

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
