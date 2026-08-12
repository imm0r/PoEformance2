using System.Text.Json.Serialization;
using PoEformance.Game.Components;
using PoEformance.Game.World;

namespace PoEformance.Features;

/// <summary>
/// One thing worth being told about.
/// </summary>
/// <remarks>
/// EVERY CONDITION LEFT UNSET MEANS "DO NOT CARE", so a rule with none set would match every
/// entity in the area - which is the single worst thing this feature can do. It matches
/// NOTHING instead, and that is checked rather than documented: an empty rule arrives from a
/// hand-edited file, from an editor's "add" button, and from a future field nobody filled in.
///
/// Rules rather than a page of switches. The reference tool grew ten booleans and two
/// comma-separated text boxes, with the priority order buried in the if-chain that read them;
/// adding a condition meant editing that chain, and no user could add one at all. A list has
/// one shape, persists as itself, and the thing a person wants - "tell me about breaches" -
/// is an entry rather than a feature request.
/// </remarks>
/// <param name="Priority">
/// Higher wins when several fire at once. Only ONE banner is shown per moment, because the
/// alternative is a stack of them covering the fight that triggered it.
/// </param>
/// <param name="Kind">What sort of entity. <see cref="EntityKind.Unknown"/> for any.</param>
/// <param name="MinRarity">
/// At least this good. <see cref="ItemRarity.Unknown"/> for any. Currency sorts above Unique
/// on the raw scale, so it is treated as its own thing rather than as a rank - a rule asking
/// for "rare and better" is not asking about currency.
/// </param>
/// <param name="Place">A place of this sort. <see cref="PoiKind.None"/> for any.</param>
/// <param name="PathContains">Part of the metadata path, matched loosely. Empty for any.</param>
/// <param name="WithinDistance">
/// Only when it is this close, in world units. 0 for anywhere in the area. This is the knob
/// that decides whether a rule is useful or unbearable: a strongbox two screens away is worth
/// knowing about, and every monster in the area is not.
/// </param>
public sealed record AlertRule(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("enabled")] bool Enabled = true,
    [property: JsonPropertyName("priority")] int Priority = 50,
    [property: JsonPropertyName("kind")] EntityKind Kind = EntityKind.Unknown,
    [property: JsonPropertyName("minRarity")] ItemRarity MinRarity = ItemRarity.Unknown,
    [property: JsonPropertyName("place")] PoiKind Place = PoiKind.None,
    [property: JsonPropertyName("pathContains")] string PathContains = "",
    [property: JsonPropertyName("withinDistance")] float WithinDistance = 0f)
{
    /// <summary>Whether this rule says anything at all about what to match.</summary>
    /// <remarks>
    /// Distance is deliberately NOT a condition for this. "Anything within 30 units" is a
    /// rule that fires on every scrap of scenery you walk past, so on its own it counts as
    /// having said nothing.
    /// </remarks>
    public bool SaysNothing
        => Kind == EntityKind.Unknown
            && MinRarity == ItemRarity.Unknown
            && Place == PoiKind.None
            && string.IsNullOrWhiteSpace(PathContains);

    /// <summary>Whether an entity at a given distance is what this rule is watching for.</summary>
    public bool Matches(WorldEntity entity, float distance)
    {
        ArgumentNullException.ThrowIfNull(entity);

        if (!Enabled || SaysNothing)
        {
            return false;
        }

        if (WithinDistance > 0f && distance > WithinDistance)
        {
            return false;
        }

        if (Kind != EntityKind.Unknown && entity.Kind != Kind)
        {
            return false;
        }

        // IsPlace rather than Poi: a rule that asks for a place means one that is there.
        // The same absent quest NPC the map stopped drawing would otherwise still fire an
        // alert, which is the louder half of the same bug.
        if (Place != PoiKind.None && (entity.Poi != Place || !entity.IsPlace))
        {
            return false;
        }

        if (MinRarity != ItemRarity.Unknown && !RarityPasses(entity.Rarity))
        {
            return false;
        }

        return PathContains.Length == 0
            || entity.Path.Contains(PathContains, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a rarity is at least what this rule asked for.
    /// </summary>
    /// <remarks>
    /// Currency is a classification rather than a rank - it sits at 5 on the game's scale,
    /// above Unique, while being the plainest thing that drops. Comparing it as a number
    /// makes "unique and better" fire on every stack of gold, so it only ever matches a rule
    /// that asked for currency by name.
    ///
    /// An entity whose rarity has not resolved does not match either. A rule with a rarity in
    /// it is asking a question about rarity, and "not yet known" is not an answer - unlike
    /// the DRAWING side, where an unresolved drop is shown rather than hidden because missing
    /// a unique costs more than glancing at a white one.
    /// </remarks>
    private bool RarityPasses(ItemRarity rarity)
    {
        if (MinRarity == ItemRarity.Currency || rarity == ItemRarity.Currency)
        {
            return MinRarity == rarity;
        }

        return rarity != ItemRarity.Unknown && rarity >= MinRarity;
    }
}

/// <summary>
/// The rules a fresh install starts with.
/// </summary>
/// <remarks>
/// Chosen so that the feature can be switched on and left on. Everything here is either rare
/// enough to be an event or a thing you would walk back for - and nothing fires on ordinary
/// monsters or ordinary drops, which between them make up almost everything in an area and
/// would turn the banner into a permanent fixture. A banner that is always up is a banner
/// nobody reads.
/// </remarks>
public static class DefaultAlerts
{
    public static IReadOnlyList<AlertRule> Rules { get; } =
    [
        new("Unique monster", Priority: 100, Kind: EntityKind.Monster, MinRarity: ItemRarity.Unique),
        new("Unique drop", Priority: 95, Kind: EntityKind.WorldItem, MinRarity: ItemRarity.Unique),
        new("Strongbox", Priority: 80, PathContains: "strongbox"),
        new("Rare monster", Priority: 70, Kind: EntityKind.Monster, MinRarity: ItemRarity.Rare, WithinDistance: 120f),
        new("Shrine", Priority: 60, Place: PoiKind.Shrine),

        // Off by default rather than absent: a rare drop is the most-wanted rule and the one
        // most likely to be too much on a map that carpets the floor, so it is one click away
        // instead of a decision made for everybody.
        new("Rare drop", Enabled: false, Priority: 65, Kind: EntityKind.WorldItem, MinRarity: ItemRarity.Rare),
        new("League mechanic", Enabled: false, Priority: 75, Place: PoiKind.Mechanic),
        new("Way out", Enabled: false, Priority: 30, Place: PoiKind.AreaTransition),
    ];
}
