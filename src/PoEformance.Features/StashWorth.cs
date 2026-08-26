using PoEformance.Game.Items;

namespace PoEformance.Features;

/// <summary>What a pile of items came to, and how much of it nobody knows.</summary>
/// <param name="Exalted">The total, in Exalted Orbs.</param>
/// <param name="Priced">How many items that total is made of.</param>
/// <param name="Unpriced">And how many were left out because nothing is known about them.</param>
/// <remarks>
/// THE UNPRICED COUNT IS NOT DECORATION. A total on its own is read as "what this is worth",
/// and a book that could price nine of forty items would answer that question with a number
/// that is off by a factor nobody can see. Carried alongside so a view can say both.
/// </remarks>
public readonly record struct Valued(double Exalted, int Priced, int Unpriced)
{
    /// <summary>Nothing looked at yet.</summary>
    public static Valued Nothing { get; }

    /// <summary>How many items were looked at.</summary>
    public int Items => Priced + Unpriced;
}

/// <summary>
/// What the things in a stash are worth, given a book of prices.
/// </summary>
/// <remarks>
/// TWO KEYS, AND THE CHOICE BETWEEN THEM MATTERS. Anything fungible is found by its ART, which
/// is a handle both sides carry and which works on a client running in any language. Uniques
/// have no such handle - every Astramentis draws the same picture as its base - so they are
/// found by NAME, and the name is the one resolved from the item's ItemVisualIdentity id rather
/// than anything the game painted.
///
/// ONLY UNIQUES ARE ASKED FOR BY NAME, deliberately. The listed half of the book also holds
/// tablets, which would be found by their base type's name - and so would any ordinary item
/// whose base happens to be spelled like a listed line, which would then carry that line's
/// price. Tablets going unpriced is the cost, and it is the cheaper of the two: an item with no
/// price shows none, and an item with a wrong price is believed.
/// </remarks>
public static class StashWorth
{
    /// <summary>
    /// What ONE of an item is worth, or null when nothing is known about it.
    /// </summary>
    /// <param name="trade">
    /// Asked only for what the book had nothing on, and only about uniques. In that order on
    /// purpose: the book is a whole market averaged and gated, and a trade answer is ten
    /// listings at one moment - a good answer to a question the book could not answer at all,
    /// and a worse one to a question it could.
    /// </param>
    public static double? Unit(this PriceBook book, InspectedItem item, TradePrices? trade = null)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(item);

        string? unique = item.Unique.Length > 0 ? item.Unique : null;

        // THE BASE NAME IS OFFERED FIRST, because in Path of Exile 2 a picture is not a key: an
        // orb and its Greater and Perfect variants draw the same art and are worth up to four
        // hundred times as much. See PriceBook.Fungible - and note the name handed over is the
        // one the shipped table resolved from the metadata path, not anything the client painted,
        // so this stays language-independent.
        return book.Fungible(item.Art, item.Base)
               ?? book.Worth(item.Art, unique)
               ?? (unique is not null ? trade?.Worth(unique) : null);
    }

    /// <summary>What the item is worth as it sits - the stack included.</summary>
    public static double? Of(this PriceBook book, InspectedItem item, TradePrices? trade = null)
        => Unit(book, item, trade) is { } unit ? unit * Math.Max(1, item.Stack) : null;

    /// <summary>What a list of items comes to.</summary>
    public static Valued Across(this PriceBook book, IEnumerable<StashSlot> items, TradePrices? trade = null)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(items);

        double total = 0;
        var priced = 0;
        var unpriced = 0;

        foreach (StashSlot slot in items)
        {
            if (Of(book, slot.Item, trade) is { } worth)
            {
                total += worth;
                priced++;
            }
            else
            {
                unpriced++;
            }
        }

        return new Valued(total, priced, unpriced);
    }

    /// <summary>And what a set of pages comes to.</summary>
    public static Valued Across(this PriceBook book, IEnumerable<StashPage> pages, TradePrices? trade = null)
    {
        ArgumentNullException.ThrowIfNull(pages);

        Valued all = Valued.Nothing;
        foreach (StashPage page in pages)
        {
            Valued one = Across(book, page.Items, trade);
            all = new Valued(all.Exalted + one.Exalted, all.Priced + one.Priced, all.Unpriced + one.Unpriced);
        }

        return all;
    }

    /// <summary>
    /// An amount of Exalted, short enough to sit in a stash cell.
    /// </summary>
    /// <remarks>
    /// Fewer digits the larger it gets: the difference between 3841 and 3862 Exalted is not
    /// information anybody acts on, and "3.8k" is. Below one Exalted the digits ARE the answer,
    /// so they stay.
    /// </remarks>
    public static string Money(double exalted) => exalted switch
    {
        >= 1_000_000 => $"{exalted / 1_000_000:0.#}M",
        >= 1_000 => $"{exalted / 1_000:0.#}k",
        >= 10 => $"{exalted:0}",
        >= 1 => $"{exalted:0.#}",
        > 0 => $"{exalted:0.##}",
        _ => "0",
    };
}
