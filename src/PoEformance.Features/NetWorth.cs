using PoEformance.Game.Items;

namespace PoEformance.Features;

/// <summary>What one inventory is holding, and how sure that is.</summary>
/// <param name="Id">The game's own id for the tab, which is what an exclusion is remembered by.</param>
/// <param name="Called">What to show it as.</param>
/// <param name="Exalted">What its currency comes to.</param>
/// <param name="Stacks">How many stacks of currency are in it.</param>
/// <param name="Priced">How many of those the feed could put a number on.</param>
/// <param name="Unpriced">
/// And how many it could not. Shown rather than swallowed: a tab whose contents are half
/// unpriced is not a tab worth what it says, and a total that hides that is worse than no total.
/// </param>
/// <param name="Counted">Whether this tab is in the total at all.</param>
public readonly record struct TabWorth(
    int Id, string Called, double Exalted, int Stacks, int Priced, int Unpriced, bool Counted)
{
    /// <summary>Whether anything in it could be valued.</summary>
    public bool Known => Priced > 0;
}

/// <summary>
/// What a character is worth, tab by tab.
/// </summary>
/// <remarks>
/// THE SAME COUNT THE WEALTH TRACKER MAKES, split up. The tracker answers "am I up or down"
/// with one number over time; this answers "where is it" with one number per tab, at a moment.
/// Both read the same pages, and if they ever disagreed one of them would be lying.
///
/// PRICED FROM THE GAME'S OWN EXCHANGE, which is the whole reason this can exist at all. The
/// aggregated index knows 38 currencies in Standard; the exchange knows 95, and once one hop is
/// allowed it prices 84 of them - see <see cref="ExchangePairs"/>. A per-tab breakdown built on
/// the smaller table would report the Essence tab, the Rune tab and the Omen tab as empty, which
/// is not a rounding error but the wrong answer.
///
/// AN EXCLUDED TAB IS STILL COUNTED AND STILL SHOWN, just not added up. Somebody excluding a
/// mule tab wants it out of the total, not out of sight - and a tab that vanished when unticked
/// could never be found again to tick.
/// </remarks>
public static class NetWorth
{
    /// <summary>What every inventory holds, richest first.</summary>
    /// <param name="pages">The inventories, as the stash read them.</param>
    /// <param name="pairs">The exchange, for pricing. Null falls back to the book alone.</param>
    /// <param name="book">Where a price comes from when the exchange has none.</param>
    /// <param name="skipping">Tab ids the player has unticked.</param>
    public static IReadOnlyList<TabWorth> ByTab(
        IEnumerable<StashPage>? pages,
        ExchangePairs? pairs,
        PriceBook? book = null,
        IReadOnlySet<int>? skipping = null)
    {
        if (pages is null)
        {
            return [];
        }

        var tabs = new List<TabWorth>();
        foreach (StashPage page in pages)
        {
            if (!CurrencyPurse.Counts(page))
            {
                continue;
            }

            double exalted = 0;
            var stacks = 0;
            var priced = 0;
            var unpriced = 0;

            foreach (StashSlot slot in CurrencyPurse.CurrencyIn(page))
            {
                stacks++;
                int held = Math.Max(1, slot.Item.Stack);

                if (Unit(slot.Item, pairs, book) is { } unit and > 0)
                {
                    exalted += unit * held;
                    priced++;
                }
                else
                {
                    unpriced++;
                }
            }

            tabs.Add(new TabWorth(
                page.Id,
                page.Called,
                exalted,
                stacks,
                priced,
                unpriced,
                skipping is null || !skipping.Contains(page.Id)));
        }

        // Richest first, because the question a breakdown answers is "where is it" and the answer
        // is almost always the top two lines. Ties keep the game's own order so a page does not
        // swap places with its neighbour every time a stack changes.
        return [.. tabs.OrderByDescending(tab => tab.Exalted)];
    }

    /// <summary>What the ticked tabs come to.</summary>
    public static TabWorth Total(IReadOnlyList<TabWorth>? tabs)
    {
        if (tabs is null)
        {
            return default;
        }

        double exalted = 0;
        int stacks = 0, priced = 0, unpriced = 0, counted = 0;
        foreach (TabWorth tab in tabs)
        {
            if (!tab.Counted)
            {
                continue;
            }

            counted++;
            exalted += tab.Exalted;
            stacks += tab.Stacks;
            priced += tab.Priced;
            unpriced += tab.Unpriced;
        }

        return new TabWorth(-1, $"{counted} of {tabs.Count} tabs", exalted, stacks, priced, unpriced, true);
    }

    /// <summary>
    /// What one of something is worth, from the exchange first and the book after.
    /// </summary>
    /// <remarks>
    /// THE EXCHANGE GOES FIRST because it is executed trades in the league being played, where
    /// the book is an index that may not cover the item at all. The book is not dropped, though:
    /// it still carries the uniques and anything the exchange had a quiet hour for.
    ///
    /// THE PATH IS THE KEY. The feed names its markets by metadata path and the game hands this
    /// tool the same string for every item it reads, so the two join with nothing in between -
    /// no id table, no name matching, nothing to fall out of step when somebody renames a thing.
    /// </remarks>
    public static double? Unit(InspectedItem? item, ExchangePairs? pairs, PriceBook? book)
    {
        if (item is null)
        {
            return null;
        }

        if (pairs?.Worth(item.Path) is { Known: true } worth)
        {
            return worth.Exalted;
        }

        return book?.Unit(item);
    }
}
