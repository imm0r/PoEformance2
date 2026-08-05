using PoEformance.Features;
using PoEformance.Game.Components;

namespace PoEformance.Core.Tests;

/// <summary>The loot filter's setting: its default, its bounds, and that it survives a restart.</summary>
public class OverlaySettingsTests
{
    [Fact]
    public void Default_HidesNormalDrops()
    {
        // The complaint that produced this feature: Path of Exile 2 drops normal items
        // faster than they can be read, and marking all of them buries the ones worth
        // walking to.
        Assert.Equal(ItemRarity.Magic, OverlaySettings.Default.MinLootRarity);
    }

    [Theory]
    [InlineData(ItemRarity.Currency)]
    [InlineData(ItemRarity.Unknown)]
    [InlineData((ItemRarity)99)]
    public void NonThresholdValues_FallBackToTheDefault(ItemRarity written)
    {
        // Currency is a classification, not a rank: drops that carry no rarity component at
        // all. Selecting it as a MINIMUM would read as "currency only" and actually mean
        // "rarity 5 and above", which nothing satisfies.
        Assert.Equal(ItemRarity.Magic, new OverlaySettings(written).Normalised().MinLootRarity);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsByName()
    {
        string path = Path.Combine(Path.GetTempPath(), $"poeformance-overlay-{Guid.NewGuid():N}.json");
        try
        {
            Assert.True(OverlaySettingsStore.Save(new OverlaySettings(ItemRarity.Rare), path));

            Assert.Equal(ItemRarity.Rare, OverlaySettingsStore.Load(path).MinLootRarity);

            // By NAME, so inserting a rarity into the enum later cannot silently turn a
            // saved "Rare" into something else.
            Assert.Contains("Rare", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CorruptFile_FallsBackToTheDefault()
    {
        string path = Path.Combine(Path.GetTempPath(), $"poeformance-overlay-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "not json at all");
            Assert.Equal(ItemRarity.Magic, OverlaySettingsStore.Load(path).MinLootRarity);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
