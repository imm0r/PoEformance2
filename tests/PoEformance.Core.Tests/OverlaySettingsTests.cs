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

    [Theory]
    [InlineData("#96C8FF", 0xFFFFC896)]   // ImGui packs ABGR, the reverse of the page's RGB
    [InlineData("96c8ff", 0xFFFFC896)]    // the leading hash is optional
    [InlineData("#FF0000", 0xFF0000FF)]   // red: the byte order is not symmetric, so this catches a swap
    [InlineData("#00FF00", 0xFF00FF00)]
    public void ColourIsPackedTheWayImGuiReadsIt(string text, uint expected)
        => Assert.Equal(expected, OverlaySettings.ParseColour(text));

    [Theory]
    [InlineData("")]
    [InlineData("nope")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    public void AnUnparseableColourIsRejectedRatherThanDrawnInvisibly(string text)
    {
        // 0 means "no colour", which the caller reads as "keep what you had". Returning a
        // transparent black instead would draw the outline as nothing at all, which looks
        // exactly like the feature being broken.
        Assert.Equal(0u, OverlaySettings.ParseColour(text));
        Assert.Equal(OverlaySettings.Default.TerrainColour,
            new OverlaySettings(ItemRarity.Magic, TerrainColour: text).Normalised().TerrainColour);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(4, 4)]
    [InlineData(99, 6)]
    public void ThicknessIsClampedToWhatStillReadsAsAnOutline(int written, int expected)
        => Assert.Equal(expected, new OverlaySettings(ItemRarity.Magic, TerrainThickness: written)
            .Normalised().TerrainThickness);

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
