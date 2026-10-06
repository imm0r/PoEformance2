using PoEformance.Features;

namespace PoEformance.Core.Tests;

/// <summary>What the planner's settings keep, and what a hand-edited file cannot make them do.</summary>
public class ExpeditionSettingsTests
{
    [Fact]
    public void TheDefaultsAreOff_WithTheReferencesWeights()
    {
        ExpeditionSettings settings = ExpeditionSettings.Default;

        Assert.False(settings.Enabled);
        Assert.Equal(15, settings.ManualTotal);
        Assert.Equal(7, settings.MonolithMinSockets);
        Assert.Equal(0, settings.RunKey);
        Assert.Equal(40f, settings.RewardWeightOf("RewardChestCurrencyRare"));
        Assert.Equal(1f, settings.RewardWeightOf("RewardChestGems"));
        Assert.Equal(55f, settings.PropRadiusFor("Metadata/Terrain/Gallows/Leagues/Expedition/Objects/ExplodingFill_BoomBarrel"));
        Assert.Equal(112f, settings.PropRadiusFor("Metadata/Terrain/Gallows/Leagues/Expedition/Logbook_Basin/Objects/OilWell"));
        Assert.Equal(0f, settings.PropRadiusFor("Metadata/Terrain/Gallows/Leagues/Expedition/Objects/Rock"));
        Assert.Empty(settings.RelicWeights);
        Assert.Empty(settings.AvoidMods);
        Assert.False(settings.Avoids("ExpeditionRelicDownsideImmuneLightningDamage"));
    }

    [Fact]
    public void SavedSettingsComeBackAsWritten_ListsIncluded()
    {
        var written = new ExpeditionSettings(
            Enabled: true, ShowSpine: true, RunKey: 0x78, ManualTotal: 20, MonolithMinEx: 50f, MonolithMinSockets: 0,
            MinMarkersPerSpare: 3, MarkerGold: 90,
            RewardWeights: [new("RewardChestCurrency", 30f)],
            RelicWeights: [new("ExpeditionRelicUpsideItemQuantityChest", 20f), new("ExpeditionRelicDownsideAlwaysCrit", 5f)],
            AvoidMods: ["ExpeditionRelicDownsideImmuneLightningDamage", "ExpeditionRelicDownsideGrantNoFlaskCharges"],
            PropRules: [new("ExplodingFill", 55f), new("Objects/Tank", 40f, Enabled: false)],
            TraceFile: true);
        string path = Path.Combine(Path.GetTempPath(), $"expedition-{Guid.NewGuid():N}.json");
        try
        {
            Assert.True(ExpeditionStore.Save(written, path));
            ExpeditionSettings read = ExpeditionStore.Load(path);
            Assert.Equal(written, read);
            Assert.Equal(written.GetHashCode(), read.GetHashCode());
            Assert.True(read.Avoids("ExpeditionRelicDownsideGrantNoFlaskCharges"));
            string text = File.ReadAllText(path);
            Assert.Contains("\"relicWeights\"", text, StringComparison.Ordinal);
            Assert.Contains("\"avoidMods\"", text, StringComparison.Ordinal);
            Assert.Contains("\"pathContains\"", text, StringComparison.Ordinal);

            File.WriteAllText(path, "{ \"enabled\": true }");
            ExpeditionSettings older = ExpeditionStore.Load(path);
            Assert.Equal(ExpeditionSettings.DefaultRewardWeights, older.RewardWeights);
            Assert.Equal(ExpeditionSettings.DefaultPropRules, older.PropRules);
            Assert.Empty(older.AvoidMods);
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Equal(ExpeditionSettings.Default, ExpeditionStore.Load(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json")));
    }

    [Fact]
    public void NormalisedKeepsEveryValueUsable()
    {
        ExpeditionSettings wild = new ExpeditionSettings(
            ManualTotal: 0, MonolithMinEx: -5f, MonolithMinSockets: 99, MinMarkersPerSpare: 9, MarkerWhite: -1, RunKey: 999,
            RewardWeights: [new("RewardChestCurrency", float.NaN), new("", 3f), new("RewardChestCurrency", 7f)],
            RelicWeights: [new("ExpeditionRelicUpsidePackSize", 0f), new("ExpeditionRelicUpsidePackSize", 4f), new("", 2f)],
            AvoidMods: ["", "ExpeditionRelicDownsideAlwaysCrit", "ExpeditionRelicDownsideAlwaysCrit"],
            PropRules: [new("X", float.NaN)]).Normalised();

        Assert.Equal(1, wild.ManualTotal);
        Assert.Equal(0f, wild.MonolithMinEx);
        Assert.Equal(16, wild.MonolithMinSockets);
        Assert.Equal(3, wild.MinMarkersPerSpare);
        Assert.Equal(0, wild.MarkerWhite);
        Assert.Equal(0, wild.RunKey);
        Assert.Equal([new RewardWeight("RewardChestCurrency", 1f)], wild.RewardWeights);
        Assert.Equal([new RelicWeight("ExpeditionRelicUpsidePackSize", 4f)], wild.RelicWeights);
        Assert.Equal(["ExpeditionRelicDownsideAlwaysCrit"], wild.AvoidMods);
        Assert.Equal([new PropRule("X", 0f)], wild.PropRules);

        // A clean record comes back with the same lists.
        ExpeditionSettings clean = ExpeditionSettings.Default;
        Assert.Same(clean.RewardWeights, clean.Normalised().RewardWeights);
        Assert.Same(clean.PropRules, clean.Normalised().PropRules);
        Assert.Same(clean.AvoidMods, clean.Normalised().AvoidMods);
    }

    [Fact]
    public void TheRunKeysAreNamed()
    {
        Assert.Equal("off", RunKeys.Name(0));
        Assert.Equal("F9", RunKeys.Name(0x78));
        Assert.Equal("Left Shift", RunKeys.Name(0xA0));
        Assert.Equal("key 0x41", RunKeys.Name(0x41));
        Assert.Equal(0, RunKeys.IndexOf(0x41));
        Assert.Equal(0x78, RunKeys.Choices[RunKeys.IndexOf(0x78)].Code);
    }

    [Fact]
    public void ARelicIsWorthItsUpsidesLessItsDownsides()
    {
        var weights = new Dictionary<string, float>(StringComparer.Ordinal)
        {
            ["ExpeditionRelicUpsideItemQuantityChest"] = 20f,
            ["ExpeditionRelicDownsideAlwaysCrit"] = 5f,
        };

        Assert.Equal(15.0, ExpeditionRelics.NetWeight(["ExpeditionRelicUpsideItemQuantityChest", "ExpeditionRelicDownsideAlwaysCrit", "ExpeditionRelicUpsidePackSize"], weights), 6);
        Assert.Equal(-5.0, ExpeditionRelics.NetWeight(["ExpeditionRelicDownsideAlwaysCrit"], weights), 6);
        Assert.Equal(0.0, ExpeditionRelics.NetWeight([], weights), 6);

        Assert.True(ExpeditionRelics.TryMatchLogbookRemnant("Metadata/Terrain/Gallows/Leagues/Expedition/Logbook_Wastes/Objects/Sulphite", out string name, out string mod));
        Assert.Equal("Sulphite Stalagmite", name);
        Assert.Equal("ExpeditionRelicUpsideSpecialSulphite", mod);
        Assert.False(ExpeditionRelics.TryMatchLogbookRemnant("Metadata/Terrain/Gallows/Leagues/Expedition/Logbook_Wastes/Objects/KrutogSulphitePath", out _, out _));
        Assert.Equal("ItemQuantityChest", ExpeditionRelics.ShortName("ExpeditionRelicUpsideItemQuantityChest"));
        Assert.Equal("Increased Item Quantity from Chests", RelicModNames.Load(Data("expedition-relic-mods.json")).Label("ExpeditionRelicUpsideItemQuantityChest"));
        Assert.Equal("AlwaysCrit", RelicModNames.Empty.Label("ExpeditionRelicDownsideAlwaysCrit"));
    }

    private static string Data(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "data", name)))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "data", name);
    }
}
