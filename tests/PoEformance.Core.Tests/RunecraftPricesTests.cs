using PoEformance.Features;
using PoEformance.Game.Ui;

namespace PoEformance.Core.Tests;

/// <summary>
/// How a recipe row becomes a price: the label's shapes, the three doors, and the figure.
/// </summary>
public class RunecraftPricesTests
{
    /// <summary>
    /// A book with the lines the doors are tested against, in poe.ninja's own shape.
    /// </summary>
    /// <remarks>
    /// The Regal family draws ONE picture at three prices, which is the case the name door
    /// exists for; the Chaos Orb has its own picture and name; the uncut gem has a picture the
    /// whole family shares and a name with the level in it. Volumes are above the gate.
    /// </remarks>
    private static PriceBook Book()
    {
        var book = new PriceBook();
        Assert.True(book.Add(PriceKind.Exchange, """
            {
              "core": { "rates": { "exalted": 500 } },
              "items": [
                { "id": "regal", "name": "Regal Orb", "image": "/gen/image/x/CurrencyUpgradeMagicToRare.png" },
                { "id": "greater-regal-orb", "name": "Greater Regal Orb", "image": "/gen/image/x/CurrencyUpgradeMagicToRare.png" },
                { "id": "perfect-regal-orb", "name": "Perfect Regal Orb", "image": "/gen/image/x/CurrencyUpgradeMagicToRare.png" },
                { "id": "chaos", "name": "Chaos Orb", "image": "/gen/image/x/CurrencyRerollRare.png" },
                { "id": "uncut-skill-gem-19", "name": "Uncut Skill Gem (Level 19)", "image": "/gen/image/x/UncutSkillGem.png" }
              ],
              "lines": [
                { "id": "regal", "primaryValue": 0.001, "volumePrimaryValue": 50 },
                { "id": "greater-regal-orb", "primaryValue": 0.02, "volumePrimaryValue": 50 },
                { "id": "perfect-regal-orb", "primaryValue": 1.0, "volumePrimaryValue": 50 },
                { "id": "chaos", "primaryValue": 0.2, "volumePrimaryValue": 50 },
                { "id": "uncut-skill-gem-19", "primaryValue": 0.1, "volumePrimaryValue": 50 }
              ]
            }
            """) > 0);
        return book;
    }

    private static RunecraftRow Row(
        string label,
        string recipe = "4SlotGreaterRegalOrb1",
        string path = "Metadata/Items/Currency/CurrencyUpgradeMagicToRare2",
        string art = "",
        int count = 0)
        => new(0x1000, label, recipe, path, string.Empty, art, count, 0, 1, 100);

    private static string? Shipped(string? path) => path switch
    {
        "Metadata/Items/Currency/CurrencyUpgradeMagicToRare2" => "Greater Regal Orb",
        "Metadata/Items/Currency/CurrencyRerollRare" => "Chaos Orb",
        _ => null,
    };

    [Fact]
    public void TheLabelIsParsedEitherWayTheClientWritesTheCount()
    {
        // The two shapes the reference saw across its users' clients, and a label with neither.
        Assert.Equal((6, "Armourer's Scrap"), RunecraftPrices.Parse("6x Armourer's Scrap"));
        Assert.Equal((6, "Деталь доспеха"), RunecraftPrices.Parse("Деталь доспеха (6)"));
        Assert.Equal((1, "Exalted Orb"), RunecraftPrices.Parse("Exalted Orb"));
        Assert.Equal((12, "Chaos Orb"), RunecraftPrices.Parse("  12X Chaos Orb  "));

        // Brackets that are not a count stay part of the name - that is the level of a gem.
        Assert.Equal((1, "Uncut Skill Gem (Level 19)"), RunecraftPrices.Parse("Uncut Skill Gem (Level 19)"));

        // The game's own markup is stripped first; nothing is one of nothing.
        Assert.Equal((1, "Unique Body Armour"), RunecraftPrices.Parse("[Rarity|Unique] Body Armour"));
        Assert.Equal((1, string.Empty), RunecraftPrices.Parse(""));
        Assert.Equal((1, string.Empty), RunecraftPrices.Parse(null));
    }

    [Fact]
    public void TheEnglishNameIsTheFirstDoor_AndTellsTheTiersApart()
    {
        // Three tiers, one picture: by name each gets its own price, where the picture alone
        // would be refused as shared. The count comes from the recipe, not the label.
        PriceBook book = Book();
        RunecraftPrice greater = RunecraftPrices.Price(book, Row("3x Greater Regal Orb", count: 3), RewardCatalog.Empty, Shipped);

        Assert.True(greater.Priced);
        Assert.Equal("name", greater.Via);
        Assert.Equal("Greater Regal Orb", greater.Key);
        Assert.Equal(10.0, greater.Unit!.Value, 6);
        Assert.Equal(30.0, greater.Total!.Value, 6);

        // And the picture door would have said nothing about this reward.
        Assert.Null(book.Worth("Art/2DItems/Currency/CurrencyUpgradeMagicToRare.dds"));
    }

    [Fact]
    public void TheInstallsCatalogueOutranksTheShippedTable_AndThePictureIsTheSecondDoor()
    {
        PriceBook book = Book();

        // A path the shipped table has never heard of, named by a catalogue that also carries
        // its picture - the name answers.
        RewardCatalog catalog = RewardCatalogTests.With(
            ("Metadata/Items/Currency/NewOrb", new RewardName("Chaos Orb", "Art/2DItems/Currency/CurrencyRerollRare.dds")));
        RunecraftPrice named = RunecraftPrices.Price(book, Row("1x Something", path: "Metadata/Items/Currency/NewOrb"), catalog, Shipped);
        Assert.Equal("name", named.Via);
        Assert.Equal(100.0, named.Unit!.Value, 6);

        // A name the book does not spell, but a picture it does: the second door.
        RewardCatalog pictured = RewardCatalogTests.With(
            ("Metadata/Items/Currency/NewOrb", new RewardName("Orb of Something", "Art/2DItems/Currency/CurrencyRerollRare.dds")));
        RunecraftPrice byPicture = RunecraftPrices.Price(book, Row("1x Something", path: "Metadata/Items/Currency/NewOrb"), pictured, Shipped);
        Assert.Equal("picture", byPicture.Via);
        Assert.Equal("currencyrerollrare", byPicture.Key);
        Assert.Equal(100.0, byPicture.Unit!.Value, 6);

        // The picture read off the row itself serves when no table has one.
        RunecraftPrice liveArt = RunecraftPrices.Price(
            book,
            Row("1x Something", path: "Metadata/Items/Currency/NewOrb", art: "Art/2DItems/Currency/CurrencyRerollRare.dds"),
            RewardCatalog.Empty,
            Shipped);
        Assert.Equal("picture", liveArt.Via);
    }

    [Fact]
    public void TheLabelIsTheLastDoor_AndWhyNotIsSaidWhenNoneAnswers()
    {
        PriceBook book = Book();

        // No recipe behind the row: an English label still prices, a count read off it.
        RunecraftPrice byLabel = RunecraftPrices.Price(book, Row("2x Chaos Orb", recipe: "", path: ""), RewardCatalog.Empty, Shipped);
        Assert.Equal("label", byLabel.Via);
        Assert.Equal(2, byLabel.Count);
        Assert.Equal(200.0, byLabel.Total!.Value, 6);

        // A rolled reward with a localised label: nothing answers, and the row says which hop.
        RunecraftPrice rolled = RunecraftPrices.Price(book, Row("Деталь доспеха (6)", recipe: "2SlotUncutSkillGem1", path: ""), RewardCatalog.Empty, Shipped);
        Assert.False(rolled.Priced);
        Assert.Equal("no fixed reward", rolled.Via);
        Assert.Equal(6, rolled.Count);

        RunecraftPrice drifted = RunecraftPrices.Price(book, Row("Деталь доспеха (6)", recipe: "", path: ""), RewardCatalog.Empty, Shipped);
        Assert.Equal("no recipe", drifted.Via);

        // A reward the book has no line for reports the ENGLISH name it asked about, so the
        // list says what was looked up rather than what the client painted.
        RunecraftPrice unknown = RunecraftPrices.Price(
            book, Row("1x Mirror", path: "Metadata/Items/Currency/CurrencyDuplicate"),
            RewardCatalogTests.With(("Metadata/Items/Currency/CurrencyDuplicate", new RewardName("Mirror of Kalandra", ""))),
            Shipped);
        Assert.False(unknown.Priced);
        Assert.Equal("not in the book", unknown.Via);
        Assert.Equal("Mirror of Kalandra", unknown.Key);
    }

    [Fact]
    public void TheFigureKeepsADecimalBelowAHundred_AndNoneAbove()
    {
        Assert.Equal("1.0 ex", RunecraftPrices.Format(1.0));
        Assert.Equal("1.0 ex", RunecraftPrices.Format(1.04));
        Assert.Equal("12.5 ex", RunecraftPrices.Format(12.46));
        Assert.Equal("0.25 ex", RunecraftPrices.Format(0.249));
        Assert.Equal("0.013 ex", RunecraftPrices.Format(0.0126));
        Assert.Equal("99.9 ex", RunecraftPrices.Format(99.94));
        Assert.Equal("100 ex", RunecraftPrices.Format(100.4));
        Assert.Equal("1234 ex", RunecraftPrices.Format(1234.4));
        Assert.Equal("? ex", RunecraftPrices.Format(double.NaN));
    }

    [Fact]
    public void TheMedianIsTheMiddleOfThePricedRows()
    {
        Assert.Equal(0, RunecraftPrices.Median([]));
        Assert.Equal(5, RunecraftPrices.Median([5]));
        Assert.Equal(3, RunecraftPrices.Median([9, 1, 3]));
        Assert.Equal(2.5, RunecraftPrices.Median([4, 1, 2, 3]));
    }

    [Fact]
    public void ARecipeIdIsASlotCountAndAName()
    {
        Assert.True(RunecraftPanelReader.LooksLikeRecipeId("10SlotBait"));
        Assert.True(RunecraftPanelReader.LooksLikeRecipeId("2SlotOrbofAugmentation1"));
        Assert.True(RunecraftPanelReader.LooksLikeRecipeId("7SlotCelestialAlloy1"));

        // A sentinel, a path, a bare count, a bare word: none of them is a recipe.
        Assert.False(RunecraftPanelReader.LooksLikeRecipeId(""));
        Assert.False(RunecraftPanelReader.LooksLikeRecipeId(null));
        Assert.False(RunecraftPanelReader.LooksLikeRecipeId("Metadata/Items/Currency/CurrencyAddModToRare"));
        Assert.False(RunecraftPanelReader.LooksLikeRecipeId("4Slot"));
        Assert.False(RunecraftPanelReader.LooksLikeRecipeId("SlotBait"));
        Assert.False(RunecraftPanelReader.LooksLikeRecipeId("123SlotBait"));
    }
}
