namespace PoEformance.Game.Items;

/// <summary>
/// What makes an item currency, decided in one place.
/// </summary>
/// <remarks>
/// BY PATH, NEVER BY RARITY, and that is a correction rather than a preference. Currency,
/// fragments and essences carry NO rarity component at all - asking one for its rarity gets
/// nothing back, and a reader that trusts the answer files the single most valuable class of
/// item in the game under Normal. <see cref="InspectedItem.RarityName"/> maps 5 to "Currency"
/// and that mapping is real, but it is what the field says WHEN THERE IS ONE; it is not a test
/// for whether an item is currency, and using it as one silently loses every stack of Exalted.
/// The ground reader learned this first - see its Classify - and this is the rule it learned.
///
/// CONTAINS RATHER THAN STARTS WITH, because the family nests: alongside
/// <c>Metadata/Items/Currency/CurrencyAddModToRare</c> the game also holds
/// <c>Metadata/Items/Currency/Ritual/RitualPinnacleKey</c>, and a prefix test would keep both
/// while a test for the whole segment survives whatever GGG nests next.
///
/// CASE-INSENSITIVE, like the ground reader's, because these paths are compared against
/// strings read out of memory rather than against a table this project controls.
/// </remarks>
public static class CurrencyPaths
{
    /// <summary>The path segment every currency item's metadata path carries.</summary>
    public const string Marker = "/Currency/";

    /// <summary>Whether a metadata path names a currency item.</summary>
    public static bool IsCurrency(string? path)
        => path is not null && path.Contains(Marker, StringComparison.OrdinalIgnoreCase);

    /// <summary>The same question about an item already read.</summary>
    public static bool IsCurrency(InspectedItem? item) => item is not null && IsCurrency(item.Path);
}
