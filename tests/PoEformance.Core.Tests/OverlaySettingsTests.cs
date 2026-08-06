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

/// <summary>
/// The choices that have to survive a restart.
/// </summary>
/// <remarks>
/// Every one of these is decided once and expected to hold. Losing them on each launch is
/// small and constant, which is the kind of friction nobody reports and nobody stops paying -
/// so the round trip is worth a test, and so is the fact that an OLD settings file still
/// loads. A tool that forgets its configuration when it gains an option is worse than one
/// that never had the option.
/// </remarks>
public class OverlaySettingsRoundTripTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"poeformance-overlay-{Guid.NewGuid():N}.json");

    [Fact]
    public void EverythingTheUserCanChangeComesBack()
    {
        var wanted = new PoEformance.Features.OverlaySettings(
            PoEformance.Game.Components.ItemRarity.Rare,
            ShowTerrain: false,
            TerrainColour: "#112233",
            TerrainThickness: 3,
            HideNoise: false,
            ShowPoi: false,
            PoiLabels: false,
            PoiRoutes: false,
            PoiArrows: false,
            DotLabels: true);

        string path = TempPath();
        try
        {
            Assert.True(PoEformance.Features.OverlaySettingsStore.Save(wanted, path));
            Assert.Equal(wanted, PoEformance.Features.OverlaySettingsStore.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ASettingsFileWrittenBEFORETheseOptionsExistedStillLoads()
    {
        // The upgrade case, and the one that would be found by a user rather than by a test:
        // a file from the previous version has none of these keys, and the defaults have to
        // fill in rather than the load failing back to everything-default.
        string path = TempPath();
        File.WriteAllText(path, """{"minLootRarity":2,"showTerrain":false,"terrainColour":"#ABCDEF","terrainThickness":2}""");

        try
        {
            PoEformance.Features.OverlaySettings loaded = PoEformance.Features.OverlaySettingsStore.Load(path);

            Assert.Equal(PoEformance.Game.Components.ItemRarity.Rare, loaded.MinLootRarity);
            Assert.Equal("#ABCDEF", loaded.TerrainColour);
            Assert.False(loaded.ShowTerrain);

            // The new ones take their defaults, which are what the tool did before they existed.
            Assert.True(loaded.HideNoise);
            Assert.True(loaded.ShowPoi);
            Assert.False(loaded.DotLabels);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheDefaultsAreWhatTheToolDidBefore()
    {
        // A new option that changes behaviour on upgrade is a surprise, and surprises get
        // blamed on the wrong thing.
        PoEformance.Features.OverlaySettings fresh = PoEformance.Features.OverlaySettings.Default;

        Assert.True(fresh.HideNoise);
        Assert.True(fresh.ShowPoi);
        Assert.True(fresh.PoiRoutes);
        Assert.False(fresh.DotLabels);
    }
}

/// <summary>
/// Two things write the settings file and only one of them knows every field.
/// </summary>
/// <remarks>
/// The configuration page shows four settings; the overlay has switches of its own. Sending
/// the page's four as a whole record gives the rest their DEFAULTS, and saving that resets
/// every choice made on the overlay - silently, because the page did what it was asked, the
/// file is valid, and the switches are simply back where they started the next time anybody
/// looks. This was live for exactly as long as it took to notice.
/// </remarks>
public class OverlaySettingsMergeTests
{
    private static PoEformance.Features.OverlaySettings Chosen() =>
        PoEformance.Features.OverlaySettings.Default with
        {
            HideNoise = false,
            ShowPoi = false,
            PoiRoutes = false,
            DotLabels = true,
        };

    [Fact]
    public void ThePageChangesWhatItSHOWS()
    {
        var sent = PoEformance.Features.OverlaySettings.Default with
        {
            MinLootRarity = PoEformance.Game.Components.ItemRarity.Rare,
            TerrainColour = "#010203",
            TerrainThickness = 4,
            ShowTerrain = false,
        };

        PoEformance.Features.OverlaySettings merged = Chosen().MergeFromPage(sent);

        Assert.Equal(PoEformance.Game.Components.ItemRarity.Rare, merged.MinLootRarity);
        Assert.Equal("#010203", merged.TerrainColour);
        Assert.Equal(4, merged.TerrainThickness);
        Assert.False(merged.ShowTerrain);
    }

    [Fact]
    public void AndLeavesEVERYTHINGElseAlone()
    {
        // The whole point. A record arriving from the page carries defaults for the fields it
        // does not show, and those defaults are the opposite of what this user chose.
        PoEformance.Features.OverlaySettings merged =
            Chosen().MergeFromPage(PoEformance.Features.OverlaySettings.Default);

        Assert.False(merged.HideNoise);
        Assert.False(merged.ShowPoi);
        Assert.False(merged.PoiRoutes);
        Assert.True(merged.DotLabels);
    }
}
