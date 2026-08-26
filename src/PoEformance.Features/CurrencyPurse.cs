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

    /// <summary>How many separate stacks of currency are being carried.</summary>
    /// <remarks>
    /// Not the same as <see cref="Valued.Items"/>, which only counts what got as far as being
    /// asked about. This is what is actually there, so a purse the book could price none of
    /// still says how much of it there was.
    /// </remarks>
    public static int Stacks(IEnumerable<StashPage>? pages)
        => pages is null ? 0 : pages.Sum(page => CurrencyIn(page).Count());
}
