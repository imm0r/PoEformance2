using System.Globalization;
using PoEformance.Game.Files;
using PoEformance.Game.Ui;

namespace PoEformance.Features;

/// <summary>What one recipe row's price rests on: the key that answered and the door it came through.</summary>
/// <param name="Unit">What one of the reward is worth in Exalted, or null when nothing priced it.</param>
/// <param name="Count">How many of the reward the recipe pays - the recipe's own count, else the label's.</param>
/// <param name="Key">The name or picture that was asked for. Empty when there was nothing to ask.</param>
/// <param name="Via">
/// Which door answered - "name", "picture", "label" - or, unpriced, why not: "no recipe",
/// "no fixed reward", "not in the book". Printed on the Runecraft tab so a row that shows
/// no price says which hop failed instead of staying blank.
/// </param>
public readonly record struct RunecraftPrice(double? Unit, int Count, string Key, string Via)
{
    /// <summary>Whether anything priced it.</summary>
    public bool Priced => Unit is not null;

    /// <summary>What the whole reward is worth - the unit times the count - or null.</summary>
    public double? Total => Unit is { } unit ? unit * Math.Max(1, Count) : null;
}

/// <summary>
/// Turns a panel row into a price: the label into a count and a name, the reward into a key.
/// </summary>
/// <remarks>
/// THREE DOORS, TRIED IN ORDER, each one language-independent until the last:
///
/// 1. THE REWARD'S ENGLISH NAME, resolved from its metadata path through the install's own
///    table or the shipped one, and asked of the book as poe.ninja spells it. This is the door
///    nearly everything comes through, and the one that tells a Greater Regal Orb from a Regal
///    Orb - the two draw one picture and differ a hundredfold.
/// 2. THE REWARD'S PICTURE, from the same table or read off the row itself, for a line poe.ninja
///    lists under a picture and a spelling the tables do not share. Refused where several things
///    draw the picture, by the book's own rule.
/// 3. THE LABEL, which is what the game painted: a count and the reward's name in the client's
///    language. English clients price the rolled rewards this way - "Uncut Skill Gem (Level 19)"
///    is both the label and poe.ninja's name - and every other client gets nothing from it, which
///    is the honest answer and the reason the first two doors exist.
///
/// The count comes from the recipe where it resolved and from the label where it did not: the
/// label's count is digits either way the game writes it, before the name on an English client
/// ("6x Scrap") and after it in brackets on a Russian one.
/// </remarks>
public static class RunecraftPrices
{
    /// <summary>Prices one row.</summary>
    /// <param name="book">What things are worth.</param>
    /// <param name="row">The row as read off the panel.</param>
    /// <param name="catalog">The install's names and pictures, by path. Empty is fine.</param>
    /// <param name="shippedName">The shipped table's English name for a path, or null.</param>
    public static RunecraftPrice Price(
        PriceBook book, RunecraftRow row, RewardCatalog catalog, Func<string?, string?> shippedName)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(shippedName);

        (int labelCount, string labelName) = Parse(row.Label);
        int count = row.RewardCount > 0 ? row.RewardCount : labelCount;

        if (row.RecipeId.Length == 0)
        {
            // No recipe behind the row: the label is all there is. On an English client that is
            // still a price; elsewhere it is a row that says why it has none.
            return ByLabel(book, labelName, count, "no recipe");
        }

        if (row.RewardPath.Length == 0)
        {
            return ByLabel(book, labelName, count, "no fixed reward");
        }

        RewardName? known = catalog.Of(row.RewardPath);
        string english = known?.Name is { Length: > 0 } installed ? installed : shippedName(row.RewardPath) ?? string.Empty;
        if (english.Length > 0 && book.Spelt(english) is { } byName)
        {
            return new RunecraftPrice(byName, count, english, "name");
        }

        string picture = known?.Art is { Length: > 0 } fromTable ? fromTable : row.RewardArt;
        if (picture.Length > 0 && book.Worth(picture, null, null) is { } byPicture)
        {
            return new RunecraftPrice(byPicture, count, PriceBook.ArtOf(picture), "picture");
        }

        // The label, last - and the key reported is the English name where one resolved, so a
        // row with no price names the spelling the book was asked for.
        RunecraftPrice byLabel = ByLabel(book, labelName, count, "not in the book");
        return byLabel.Priced || english.Length == 0 ? byLabel : byLabel with { Key = english };
    }

    private static RunecraftPrice ByLabel(PriceBook book, string labelName, int count, string otherwise)
        => labelName.Length > 0 && book.Spelt(labelName) is { } worth
            ? new RunecraftPrice(worth, count, labelName, "label")
            : new RunecraftPrice(null, count, labelName, otherwise);

    /// <summary>
    /// The count and the name out of a row's label, whichever way the client writes them.
    /// </summary>
    /// <remarks>
    /// "6x Armourer's Scrap" and "Деталь доспеха (6)" are the two shapes the reference saw across
    /// its users' clients: the count before the name with an x, or after it in brackets. Both are
    /// recognised; a label with neither is one of the reward, named whole. The game's own
    /// [Key|Text] markup is stripped first, since a reward's description can carry it.
    /// </remarks>
    public static (int Count, string Name) Parse(string? label)
    {
        string text = KeywordGlossary.Plain(label).Trim();
        if (text.Length == 0)
        {
            return (1, string.Empty);
        }

        int digits = 0;
        while (digits < text.Length && char.IsAsciiDigit(text[digits]))
        {
            digits++;
        }

        if (digits > 0 && digits < text.Length && (text[digits] == 'x' || text[digits] == 'X')
            && int.TryParse(text.AsSpan(0, digits), NumberStyles.None, CultureInfo.InvariantCulture, out int before)
            && before > 0)
        {
            return (before, text[(digits + 1)..].TrimStart());
        }

        if (text[^1] == ')')
        {
            int open = text.LastIndexOf('(');
            if (open > 0
                && int.TryParse(text.AsSpan(open + 1, text.Length - open - 2).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int after)
                && after > 0)
            {
                return (after, text[..open].TrimEnd());
            }
        }

        return (1, text);
    }

    /// <summary>
    /// An amount of Exalted as the row shows it: short, with the unit, and never "1,000".
    /// </summary>
    /// <remarks>
    /// The precision follows the magnitude - whole Exalted from a hundred up, one decimal from
    /// one, two below that, three under a tenth - and a sub-hundred figure always keeps a decimal,
    /// so a reward worth about one Exalted reads "1.0 ex" rather than as a thousand. Invariant
    /// culture, because the tool runs invariant and a price should not change its decimal mark
    /// with the machine's locale.
    /// </remarks>
    public static string Format(double exalted)
    {
        if (!double.IsFinite(exalted))
        {
            return "? ex";
        }

        if (exalted >= 100)
        {
            return exalted.ToString("0", CultureInfo.InvariantCulture) + " ex";
        }

        int decimals = exalted >= 1 ? 1 : exalted >= 0.1 ? 2 : 3;
        double rounded = Math.Round(exalted, decimals, MidpointRounding.AwayFromZero);
        string figure = rounded.ToString("0.###", CultureInfo.InvariantCulture);
        if (!figure.Contains('.'))
        {
            figure += ".0";
        }

        return figure + " ex";
    }

    /// <summary>The median of the priced totals, which Relative colouring measures against.</summary>
    public static double Median(IReadOnlyList<double> totals)
    {
        ArgumentNullException.ThrowIfNull(totals);
        if (totals.Count == 0)
        {
            return 0;
        }

        double[] sorted = [.. totals];
        Array.Sort(sorted);
        int n = sorted.Length;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[(n / 2) - 1] + sorted[n / 2]) * 0.5;
    }
}
