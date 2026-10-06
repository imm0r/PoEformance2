using PoEformance.Features;
using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// The install's runeshape recipes, runes and partial-offer weights, joined.
/// </summary>
/// <remarks>
/// SYNTHETIC .dat FILES against the VENDORED layouts in data/expedition-tables.json and
/// data/item-tables.json, the way RewardCatalogTests does it: the join is what is checked here,
/// and whether the layouts fit a real install is settled at runtime by each file's own row size.
/// </remarks>
public class RecipeCatalogTests
{
    /// <summary>The 34 runes in table order, as the plugin's dump and the schema both list them.</summary>
    internal static readonly string[] RuneNames =
    [
        "Fire", "Cold", "Lightning", "Tempest", "Momentum", "Bloodletting", "Stone", "Adaptive",
        "Arcane", "Toxic", "Electrocuting", "Protective", "Cyclonic", "Vision", "Tidal", "Rebirth",
        "Prismatic", "Gasp", "Moon", "Celestial", "Opulent", "Rage", "Wisdom", "Sky", "Earth", "Life",
        "Bond", "Ward", "Soul", "Death", "Oath", "Time", "Power", "Bait",
    ];

    /// <summary>One recipe to lay into the fake table.</summary>
    internal sealed record Recipe(
        string Id, int[] Runes, string? RewardPath = null, string RewardName = "", int Count = 1,
        string Description = "", int MinLevel = 1, int MaxLevel = 100, int[]? AreaTags = null);

    /// <summary>One partial-offer weight: this anchor rune at this 1-based slot permits this size from this level.</summary>
    internal readonly record struct Weight(int Rune, int Slot, int Size, int MinLevel);

    internal static QuestTableLayouts Layouts(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "data", file)))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Assert.IsType<QuestTableLayouts>(QuestTableLayouts.Load(Path.Combine(dir!.FullName, "data", file)));
    }

    private static int At(QuestTableLayouts layouts, string table, string column)
    {
        int at = layouts.OffsetOf(table, column);
        Assert.True(at >= 0, $"the vendored layout has no {table}.{column}");
        return at;
    }

    /// <summary>A catalogue holding exactly these recipes, read out of a made-up install.</summary>
    internal static RecipeCatalog With(IReadOnlyList<Recipe> recipes, IReadOnlyList<Weight>? weights = null)
    {
        ArgumentNullException.ThrowIfNull(recipes);
        weights ??= [];
        QuestTableLayouts layouts = Layouts("expedition-tables.json");
        QuestTableLayouts items = Layouts("item-tables.json");

        // The base types the rewards point at, one row per distinct path.
        var paths = new List<(string Path, string Name)>();
        foreach (Recipe recipe in recipes)
        {
            if (recipe.RewardPath is { Length: > 0 } && !paths.Exists(p => p.Path == recipe.RewardPath))
            {
                paths.Add((recipe.RewardPath, recipe.RewardName));
            }
        }

        var bases = new FakeDat(Math.Max(1, paths.Count), items.RowSizeOf("BaseItemTypes"));
        for (var i = 0; i < paths.Count; i++)
        {
            bases.Text(i, At(items, "BaseItemTypes", "Id"), paths[i].Path)
                 .Text(i, At(items, "BaseItemTypes", "Name"), paths[i].Name);
        }

        var runes = new FakeDat(RuneNames.Length, layouts.RowSizeOf("Expedition2Runes"));
        for (var i = 0; i < RuneNames.Length; i++)
        {
            runes.Text(i, At(layouts, "Expedition2Runes", "Id"), RuneNames[i]);
        }

        var table = new FakeDat(recipes.Count, layouts.RowSizeOf("Expedition2Recipes"));
        for (var i = 0; i < recipes.Count; i++)
        {
            Recipe recipe = recipes[i];
            table.Text(i, At(layouts, "Expedition2Recipes", "Id"), recipe.Id)
                 .References(i, At(layouts, "Expedition2Recipes", "Runes"), recipe.Runes)
                 .Text(i, At(layouts, "Expedition2Recipes", "Description"), recipe.Description)
                 .I32(i, At(layouts, "Expedition2Recipes", "RuneCountRequired"), recipe.Runes.Length)
                 .I32(i, At(layouts, "Expedition2Recipes", "MinLevelReq"), recipe.MinLevel)
                 .I32(i, At(layouts, "Expedition2Recipes", "MaxLevelReq"), recipe.MaxLevel)
                 .I32(i, At(layouts, "Expedition2Recipes", "RewardCount"), recipe.Count);

            int rewardRow = recipe.RewardPath is { Length: > 0 } ? paths.FindIndex(p => p.Path == recipe.RewardPath) : -1;
            if (rewardRow >= 0)
            {
                table.Reference(i, At(layouts, "Expedition2Recipes", "Reward"), rewardRow);
            }
            else
            {
                table.Null(i, At(layouts, "Expedition2Recipes", "Reward"));
            }

            if (recipe.AreaTags is { Length: > 0 } tags)
            {
                table.Ints(i, At(layouts, "Expedition2Recipes", "column_19"), tags);
            }
        }

        var ws = new FakeDat(Math.Max(1, weights.Count), layouts.RowSizeOf("Expedition2RunesWeights"));
        for (var i = 0; i < weights.Count; i++)
        {
            ws.Text(i, At(layouts, "Expedition2RunesWeights", "Id"), $"W{i}")
              .I32(i, At(layouts, "Expedition2RunesWeights", "RecipeRuneCount"), weights[i].Size)
              .I32(i, At(layouts, "Expedition2RunesWeights", "HighlightedRuneSlot"), weights[i].Slot)
              .Reference(i, At(layouts, "Expedition2RunesWeights", "HighlightedRune"), weights[i].Rune)
              .I32(i, At(layouts, "Expedition2RunesWeights", "MinAreaLevel"), weights[i].MinLevel);
        }

        if (weights.Count == 0)
        {
            ws.Null(0, At(layouts, "Expedition2RunesWeights", "HighlightedRune"));
        }

        GameFiles? install = FakeInstall.Of(
            ("data/expedition2recipes.datc64", table.Bytes()),
            ("data/expedition2runes.datc64", runes.Bytes()),
            ("data/expedition2runesweights.datc64", ws.Bytes()),
            ("data/baseitemtypes.datc64", bases.Bytes()));
        Assert.NotNull(install);

        RecipeCatalog catalog = RecipeCatalog.Read(install, layouts, items);
        Assert.Equal(recipes.Count, catalog.Count);
        return catalog;
    }

    [Fact]
    public void TheThreeTablesJoin_AndTheRewardsAreNamedThroughTheBaseTypes()
    {
        RecipeCatalog catalog = With(
            [
                new Recipe("5SlotExaltedOrb3", [7, 11, 20, 0, 3], "Metadata/Items/Currency/CurrencyAddModToRare", "Exalted Orb", 3),
                new Recipe("2SlotUncutSkillGem1", [4, 0], Description: "Uncut Skill Gem"),
                new Recipe("7SlotAldursLogbook1", [20, 26, 32, 29, 31, 30, 15], "Metadata/Items/Expedition/Logbook", "Aldur's Logbook", 1, AreaTags: [54]),
            ],
            [new Weight(20, 3, 4, 68), new Weight(20, 3, 3, 1)]);

        Assert.Equal(34, catalog.RuneNames.Count);
        Assert.Equal("Opulent", catalog.RuneName(20));
        Assert.Equal("#99", catalog.RuneName(99));

        RuneshapeRecipe exalted = Assert.IsType<RuneshapeRecipe>(catalog.ById("5SlotExaltedOrb3"));
        Assert.Equal(0, exalted.Row);
        Assert.Equal(5, exalted.Size);
        Assert.Equal([7, 11, 20, 0, 3], exalted.Runes);
        Assert.Equal(20, exalted.RuneAt(2));
        Assert.Equal(-1, exalted.RuneAt(5));
        Assert.Equal("Metadata/Items/Currency/CurrencyAddModToRare", exalted.RewardPath);
        Assert.Equal("Exalted Orb", exalted.RewardName);
        Assert.Equal(3, exalted.RewardCount);
        Assert.True(exalted.HasReward);
        Assert.Empty(exalted.AreaTags);

        RuneshapeRecipe gem = Assert.IsType<RuneshapeRecipe>(catalog.ById("2SlotUncutSkillGem1"));
        Assert.False(gem.HasReward);
        Assert.Equal("Uncut Skill Gem", gem.Description);

        Assert.Equal([54], Assert.IsType<RuneshapeRecipe>(catalog.ById("7SlotAldursLogbook1")).AreaTags);
        Assert.Null(catalog.ById("nothing"));
        Assert.Null(catalog.ById(null));

        // The weights: slot 3 in the table is hole 2 here, a size-4 partial from level 68 and
        // a size-3 one from the start; nothing for a size-2, and an unknown level is not gated.
        Assert.True(catalog.PartialAllowed(20, 2, 4, 70));
        Assert.False(catalog.PartialAllowed(20, 2, 4, 60));
        Assert.True(catalog.PartialAllowed(20, 2, 4, 0));
        Assert.True(catalog.PartialAllowed(20, 2, 3, 1));
        Assert.False(catalog.PartialAllowed(20, 2, 2, 90));
        Assert.False(catalog.PartialAllowed(26, 2, 4, 90));

        Assert.Contains(catalog.Say, line => line.Contains("3 runeshape recipes", StringComparison.Ordinal));
        Assert.Contains(catalog.Say, line => line.Contains("2 partial-offer weights", StringComparison.Ordinal));
    }

    [Fact]
    public void NoInstallOrNoLayoutLeavesAnEmptyCatalogueThatSaysSo()
    {
        RecipeCatalog none = RecipeCatalog.Read(null, Layouts("expedition-tables.json"), Layouts("item-tables.json"));
        Assert.Equal(0, none.Count);
        Assert.Contains(none.Say, line => line.Contains("no install", StringComparison.Ordinal));

        RecipeCatalog unlaid = RecipeCatalog.Read(FakeInstall.Of(), null, Layouts("item-tables.json"));
        Assert.Equal(0, unlaid.Count);
        Assert.Contains(unlaid.Say, line => line.Contains("expedition-tables.json", StringComparison.Ordinal));

        RecipeCatalog missing = RecipeCatalog.Read(FakeInstall.Of(), Layouts("expedition-tables.json"), Layouts("item-tables.json"));
        Assert.Equal(0, missing.Count);
        Assert.Contains(missing.Say, line => line.Contains("not found", StringComparison.Ordinal));

        Assert.Equal(0, RecipeCatalog.Empty.Count);
        Assert.False(RecipeCatalog.Empty.PartialAllowed(20, 2, 4, 70));
    }
}
