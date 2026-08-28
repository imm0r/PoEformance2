using PoEformance.Game.Items;

namespace PoEformance.Features;

/// <summary>
/// What the currency a character is carrying comes to.
/// </summary>
/// <remarks>
/// A NARROWER QUESTION THAN <see cref="StashWorth"/>, on purpose, and the narrowing is what
/// makes it answerable often. Valuing a whole stash means pricing every rare and every unique,
/// which is where the price book is least sure of itself - the listing gate throws away thin
/// lines, so a total swings by whatever fraction of the gear happened to be priceable that
/// refresh. Currency is the half the book is BEST at: it is keyed by picture, it is gated on
/// traded volume rather than on asking prices, and a stack of Exalted is worth what a stack of
/// Exalted is worth. A line on a graph is only worth drawing if two points on it are comparable,
/// and gear valuations are not.
///
/// EQUIPPED GEAR IS NOT IN IT even when something worn somehow matches, because this counts what
/// could be spent. That falls out of the currency rule anyway; the kind test is there so that
/// widening the rule later cannot quietly start counting a worn item.
///
/// EVERYTHING IS IN EXALTED because <see cref="PriceBook"/> is - see its remarks. Divine is not
/// a second measurement, it is this one divided by <see cref="PriceBook.Rate"/>, which is why
/// nothing here has to know which picture the Divine Orb draws.
/// </remarks>
public static class CurrencyPurse
{
    /// <summary>Whether this page's contents count towards what could be spent.</summary>
    public static bool Counts(StashPage? page)
        => page is not null && page.Kind is InventoryKind.Backpack or InventoryKind.Stash;

    /// <summary>The currency in one page, and nothing else in it.</summary>
    public static IEnumerable<StashSlot> CurrencyIn(StashPage? page)
        => page is null || !Counts(page)
            ? []
            : page.Items.Where(slot => CurrencyPaths.IsCurrency(slot.Item));

    /// <summary>
    /// What the currency across these pages is worth, in Exalted.
    /// </summary>
    /// <remarks>
    /// The unpriced count is carried through from <see cref="StashWorth"/> and matters more here
    /// than it does there, not less: this total is going into a HISTORY, and a point taken while
    /// the book knew nothing about half the purse would sit on the graph beside points that knew
    /// everything, as a crash that never happened. What uses this has to look at
    /// <see cref="Valued.Unpriced"/> before it writes the point down.
    /// </remarks>
    public static Valued Purse(this PriceBook book, IEnumerable<StashPage>? pages, TradePrices? trade = null)
    {
        ArgumentNullException.ThrowIfNull(book);

        if (pages is null)
        {
            return Valued.Nothing;
        }

        Valued all = Valued.Nothing;
        foreach (StashPage page in pages)
        {
            Valued one = book.Across(CurrencyIn(page), trade);
            all = new Valued(all.Exalted + one.Exalted, all.Priced + one.Priced, all.Unpriced + one.Unpriced);
        }

        return all;
    }

    /// <summary>One kind of currency in the purse, and what it is contributing.</summary>
    /// <param name="Called">What the game calls it, or its metadata path where no name resolved.</param>
    /// <param name="Stack">How many, across every page it appears on.</param>
    /// <param name="Unit">What one is worth in Exalted, or null when the book has no price.</param>
    /// <param name="Exalted">What the whole holding comes to. 0 when there is no price.</param>
    public readonly record struct PurseLine(string Called, int Stack, double? Unit, double Exalted);

    /// <summary>
    /// What the total is MADE OF, biggest contributor first.
    /// </summary>
    /// <remarks>
    /// THE ANSWER TO "THAT NUMBER LOOKS WRONG", and there is no other way to give it. A single
    /// total is unfalsifiable: it is either believed or distrusted, and neither reaction can be
    /// acted on. Broken into the stacks that produced it, a wrong total stops being a mystery -
    /// one line is holding an implausible count, or carrying a price that is not what that
    /// currency trades at, and the reader can see WHICH in a second.
    ///
    /// GROUPED BY THE PICTURE AND THE NAME TOGETHER, which is the pair it is priced on. The
    /// picture alone was the key, on the reasoning that two different currencies can never draw
    /// the same one. PoE2 says otherwise: an orb, its Greater and its Perfect variant share one
    /// art file and the game paints the tier as an overlay. Five families do it - Transmutation,
    /// Augmentation, Regal, Exalted, Chaos - so a stash holding 3,357 plain Orbs of Transmutation,
    /// 425 Greater and 114 Perfect came out as ONE line reading 3,896, beside whichever of the
    /// three unit prices was written last. The count was real and the row was a fiction.
    ///
    /// The name ALONE is still not the key, for the reason the old remark gave: a name is what a
    /// table resolved, so two things it has not heard of would merge. Both together are safe -
    /// same picture and same name is the same currency - and it is the same pair the price book
    /// keys on, so a row can never group differently from the way it was valued.
    ///
    /// One line per currency also keeps the count comparable to what the game's own tab shows,
    /// which is the check somebody actually performs - and the tab shows the tiers apart.
    ///
    /// UNPRICED LINES ARE IN IT, at the bottom with a null unit. They contribute nothing to the
    /// total, and leaving them out would hide the other half of "is this right" - a purse whose
    /// biggest holding is unpriced is understated, and that is just as wrong as an overstatement.
    /// </remarks>
    public static IReadOnlyList<PurseLine> Breakdown(
        this PriceBook book, IEnumerable<StashPage>? pages, TradePrices? trade = null)
    {
        ArgumentNullException.ThrowIfNull(book);

        if (pages is null)
        {
            return [];
        }

        var found = new Dictionary<string, PurseLine>(StringComparer.OrdinalIgnoreCase);

        foreach (StashPage page in pages)
        {
            foreach (StashSlot slot in CurrencyIn(page))
            {
                InspectedItem item = slot.Item;

                // The art AND the name, because the art alone is not an identity - see above. The
                // path is the fallback for an item whose picture did not read, which must still be
                // its OWN line rather than joining another's; a NUL joins the two halves so no
                // name can be made to read as a different picture's key.
                string key = item.Art.Length > 0
                    ? $"{item.Art}\0{item.Called}"
                    : item.Path;
                int stack = Math.Max(1, item.Stack);
                double? unit = book.Unit(item, trade);

                // A struct that was not there comes back as default, whose Called is NULL rather
                // than empty - the one place a record struct differs from the class it reads like.
                found.TryGetValue(key, out PurseLine had);

                found[key] = new PurseLine(
                    string.IsNullOrEmpty(had.Called) ? item.Called : had.Called,
                    had.Stack + stack,
                    unit ?? had.Unit,
                    had.Exalted + (unit is { } each ? each * stack : 0));
            }
        }

        // Priced first and biggest first within that, so what is driving the total is at the top
        // and what is missing from it is a short list at the bottom.
        return [.. found.Values
            .OrderByDescending(line => line.Unit is not null)
            .ThenByDescending(line => line.Exalted)
            .ThenBy(line => line.Called, StringComparer.Ordinal)];
    }

    /// <summary>How many separate stacks of currency are being carried.</summary>
    /// <remarks>
    /// Not the same as <see cref="Valued.Items"/>, which only counts what got as far as being
    /// asked about. This is what is actually there, so a purse the book could price none of
    /// still says how much of it there was.
    /// </remarks>
    public static int Stacks(IEnumerable<StashPage>? pages)
        => pages is null ? 0 : pages.Sum(page => CurrencyIn(page).Count());
}
