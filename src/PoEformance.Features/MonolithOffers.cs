namespace PoEformance.Features;

/// <summary>Why a catalogue recipe is, or is not, offered on a monolith.</summary>
/// <remarks>
/// One verdict per gate rather than a bool, so the Runecraft tab can name the gate that dropped
/// a recipe the game plainly offers - which is the whole way a wrong rule gets found.
/// </remarks>
public enum OfferDrop
{
    Offered = 0,

    /// <summary>The recipe is shorter than the anchor's hole: nothing of it sits there.</summary>
    NoRuneAtAnchor,

    /// <summary>More runes than the monolith has holes.</summary>
    SizeOverHoles,

    /// <summary>The recipe's rune at the anchor's hole is not the anchor.</summary>
    AnchorMismatch,

    /// <summary>The area's level is outside the recipe's band.</summary>
    LevelBand,

    /// <summary>Shorter than the monolith, and the weights table does not allow that here.</summary>
    PartialWeights,

    /// <summary>The recipe is gated to areas carrying a content tag this one lacks.</summary>
    AreaTags,
}

/// <summary>
/// The in-game offer rule: which recipes a monolith with these holes and this anchor can roll.
/// </summary>
/// <remarks>
/// DECODED, NOT GUESSED - by the reference plugin out of the client's own offer builder
/// (FUN_141e32ab0, docs/monolith-partial-recipes.md), and the rule is smaller than it looks:
///
///   runes[p] == anchor  AND  size &lt;= N  AND  minLevel &lt;= areaLevel &lt;= maxLevel
///   AND  (size == N  OR  Expedition2RunesWeights permits (anchor, p + 1, size) at this level)
///
/// There is NO category or theme filter; the "theme" a panel seems to have is emergent from
/// which rune sits in which hole. The anchor-less unique monolith runs the builder's skip-anchor
/// branch, which drops both the anchor match and the size gate (FUN_141e33b00, docs 6.12) - so
/// it offers everything that fits its holes, which is why its list is so long. The HF9 area
/// gate is last and rarely fires: one recipe of 322 carries tags today.
///
/// Pure, so the rule is a test rather than a screenshot.
/// </remarks>
public static class MonolithOffers
{
    /// <summary>Whether one recipe is offered, and if not, which gate dropped it.</summary>
    /// <param name="recipe">The catalogue recipe.</param>
    /// <param name="holes">The monolith's hole count, N.</param>
    /// <param name="anchorRune">The anchor rune's row, or -1 when the station has none.</param>
    /// <param name="anchorHole">The anchor's hole, 0-based.</param>
    /// <param name="areaLevel">The area's monster level; 0 or less means unknown and is not gated on.</param>
    /// <param name="skipAnchor">True on the anchor-less unique monolith.</param>
    /// <param name="catalog">For the partial-offer weights.</param>
    /// <param name="areaTags">The area's content tags, or null when they could not be read - then the gate is off.</param>
    public static OfferDrop Verdict(
        RuneshapeRecipe recipe, int holes, int anchorRune, int anchorHole, int areaLevel,
        bool skipAnchor, RecipeCatalog catalog, IReadOnlySet<int>? areaTags)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(catalog);

        if (recipe.Size > holes)
        {
            return OfferDrop.SizeOverHoles;
        }

        // Tiered rewards (Thaumaturgic Flux Levels 5..18) carry a band and only the tier that
        // covers the area is offered; the untiered ones say 1..100 and never drop here.
        if (areaLevel > 0 && recipe.MaxLevel > 0 && (areaLevel < recipe.MinLevel || areaLevel > recipe.MaxLevel))
        {
            return OfferDrop.LevelBand;
        }

        if (!skipAnchor)
        {
            if (anchorHole < 0 || recipe.Size <= anchorHole)
            {
                return OfferDrop.NoRuneAtAnchor;
            }

            if (recipe.RuneAt(anchorHole) != anchorRune)
            {
                return OfferDrop.AnchorMismatch;
            }

            if (recipe.Size != holes && !catalog.PartialAllowed(anchorRune, anchorHole, recipe.Size, areaLevel))
            {
                return OfferDrop.PartialWeights;
            }
        }

        if (recipe.AreaTags.Count > 0 && areaTags is not null)
        {
            var shared = false;
            foreach (int tag in recipe.AreaTags)
            {
                if (areaTags.Contains(tag))
                {
                    shared = true;
                    break;
                }
            }

            if (!shared)
            {
                return OfferDrop.AreaTags;
            }
        }

        return OfferDrop.Offered;
    }

    /// <summary>Every recipe the monolith can roll, in catalogue order.</summary>
    public static List<RuneshapeRecipe> Offered(
        RecipeCatalog catalog, int holes, int anchorRune, int anchorHole, int areaLevel,
        bool skipAnchor, IReadOnlySet<int>? areaTags)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var offered = new List<RuneshapeRecipe>();
        if (holes <= 0)
        {
            return offered;
        }

        foreach (RuneshapeRecipe recipe in catalog.Recipes)
        {
            if (Verdict(recipe, holes, anchorRune, anchorHole, areaLevel, skipAnchor, catalog, areaTags) == OfferDrop.Offered)
            {
                offered.Add(recipe);
            }
        }

        return offered;
    }
}
