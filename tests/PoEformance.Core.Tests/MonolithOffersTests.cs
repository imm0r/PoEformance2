using PoEformance.Features;

namespace PoEformance.Core.Tests;

/// <summary>The in-game offer rule, gate by gate.</summary>
public class MonolithOffersTests
{
    private const int Opulent = 20;
    private const int Bond = 26;

    private static RecipeCatalog Catalog() => RecipeCatalogTests.With(
        [
            // Full-size, Opulent in hole 2: the plain case.
            new RecipeCatalogTests.Recipe("5SlotExaltedOrb3", [7, 11, Opulent, 0, 3], "Metadata/Items/Currency/CurrencyAddModToRare", "Exalted Orb", 3),
            // Full-size, Bond in hole 2: the anchor does not match.
            new RecipeCatalogTests.Recipe("5SlotChaosOrb1", [7, 11, Bond, 0, 3], "Metadata/Items/Currency/CurrencyRerollRare", "Chaos Orb"),
            // Shorter than the holes, Opulent in hole 2: needs a weight.
            new RecipeCatalogTests.Recipe("4SlotRegalOrb1", [7, 11, Opulent, 0], "Metadata/Items/Currency/CurrencyUpgradeMagicToRare", "Regal Orb"),
            // Shorter still: no weight at all.
            new RecipeCatalogTests.Recipe("3SlotAugment1", [7, 11, Opulent], "Metadata/Items/Currency/CurrencyAddModToMagic", "Orb of Augmentation"),
            // Too short to reach the anchor's hole.
            new RecipeCatalogTests.Recipe("2SlotTransmute1", [7, 11], "Metadata/Items/Currency/CurrencyUpgradeToMagic", "Orb of Transmutation"),
            // Longer than the monolith.
            new RecipeCatalogTests.Recipe("6SlotDivine1", [7, 11, Opulent, 0, 3, 4], "Metadata/Items/Currency/CurrencyModValues", "Divine Orb"),
            // Tiered: only in its band.
            new RecipeCatalogTests.Recipe("5SlotFlux12", [7, 11, Opulent, 0, 5], "Metadata/Items/Currency/Flux12", "Thaumaturgic Flux (Level 12)", 1, "", 60, 69),
            // Gated to an area tag.
            new RecipeCatalogTests.Recipe("5SlotLogbook1", [7, 11, Opulent, 0, 6], "Metadata/Items/Expedition/Logbook", "Aldur's Logbook", 1, AreaTags: [54]),
        ],
        [new RecipeCatalogTests.Weight(Opulent, 3, 4, 68)]);

    private static List<string> Offered(RecipeCatalog catalog, int holes, int anchorRune, int anchorHole, int level, bool unique, IReadOnlySet<int>? tags)
        => [.. MonolithOffers.Offered(catalog, holes, anchorRune, anchorHole, level, unique, tags).Select(r => r.Id)];

    [Fact]
    public void EachGateDropsWhatItShould()
    {
        RecipeCatalog catalog = Catalog();
        var tagged = new HashSet<int> { 54 };

        RuneshapeRecipe Id(string id) => catalog.ById(id)!;
        OfferDrop Verdict(string id, int level = 75, IReadOnlySet<int>? tags = null)
            => MonolithOffers.Verdict(Id(id), 5, Opulent, 2, level, false, catalog, tags);

        Assert.Equal(OfferDrop.Offered, Verdict("5SlotExaltedOrb3"));
        Assert.Equal(OfferDrop.AnchorMismatch, Verdict("5SlotChaosOrb1"));
        Assert.Equal(OfferDrop.Offered, Verdict("4SlotRegalOrb1", level: 75));
        Assert.Equal(OfferDrop.PartialWeights, Verdict("4SlotRegalOrb1", level: 60));
        Assert.Equal(OfferDrop.PartialWeights, Verdict("3SlotAugment1"));
        Assert.Equal(OfferDrop.NoRuneAtAnchor, Verdict("2SlotTransmute1"));
        Assert.Equal(OfferDrop.SizeOverHoles, Verdict("6SlotDivine1"));
        Assert.Equal(OfferDrop.LevelBand, Verdict("5SlotFlux12", level: 75));
        Assert.Equal(OfferDrop.Offered, Verdict("5SlotFlux12", level: 65));
        Assert.Equal(OfferDrop.Offered, Verdict("5SlotFlux12", level: 0));

        // The area gate: off while the tags could not be read, shut without the tag, open with it.
        Assert.Equal(OfferDrop.Offered, Verdict("5SlotLogbook1", tags: null));
        Assert.Equal(OfferDrop.AreaTags, Verdict("5SlotLogbook1", tags: new HashSet<int>()));
        Assert.Equal(OfferDrop.Offered, Verdict("5SlotLogbook1", tags: tagged));
    }

    [Fact]
    public void TheOfferedListIsTheRuleOverTheCatalogue()
    {
        RecipeCatalog catalog = Catalog();

        Assert.Equal(
            ["5SlotExaltedOrb3", "4SlotRegalOrb1", "5SlotLogbook1"],
            Offered(catalog, 5, Opulent, 2, 75, unique: false, tags: null));

        // At 60 the partial loses its weight (68 and up) and the tiered Flux enters its band.
        Assert.Equal(
            ["5SlotExaltedOrb3", "5SlotFlux12", "5SlotLogbook1"],
            Offered(catalog, 5, Opulent, 2, 60, unique: false, tags: new HashSet<int> { 54 }));

        // No holes, no offers; an unresolved anchor, none either.
        Assert.Empty(Offered(catalog, 0, Opulent, 2, 75, unique: false, tags: null));
        Assert.Empty(Offered(catalog, 5, -1, 2, 75, unique: false, tags: null));
    }

    [Fact]
    public void TheUniqueMonolithSkipsTheAnchorAndTheSizeGate()
    {
        RecipeCatalog catalog = Catalog();

        // Everything that fits five holes and the level, whatever its runes - the game's
        // skip-anchor branch, which is why that list is so long.
        Assert.Equal(
            ["5SlotExaltedOrb3", "5SlotChaosOrb1", "4SlotRegalOrb1", "3SlotAugment1", "2SlotTransmute1", "5SlotLogbook1"],
            Offered(catalog, 5, -1, -1, 75, unique: true, tags: null));
    }
}
