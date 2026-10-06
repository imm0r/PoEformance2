using PoEformance.Game.Files;

namespace PoEformance.Features;

/// <summary>One runeshape combination the Runecraft monoliths can offer, out of Expedition2Recipes.</summary>
/// <param name="Row">The table row, which is what a live recipe row's stride counts in.</param>
/// <param name="Id">The engine id - "4SlotExaltedOrb3" - the key every live read joins on.</param>
/// <param name="Size">How many runes the combination takes: the length of <paramref name="Runes"/>.</param>
/// <param name="Runes">The runes by hole, as Expedition2Runes row indices. Hole p must hold Runes[p].</param>
/// <param name="RewardPath">The fixed reward's metadata path, or empty for a rolled one.</param>
/// <param name="RewardName">The fixed reward's English name out of BaseItemTypes, or empty.</param>
/// <param name="RewardCount">How many of it the recipe pays.</param>
/// <param name="RewardGemLevel">For a rolled gem, the level it rolls at; else 0.</param>
/// <param name="Description">The game's own wording for a rolled reward - "Uncut Skill Gem".</param>
/// <param name="MinLevel">The area-level band the recipe is offered in.</param>
/// <param name="MaxLevel">Its upper end. 1..100 on the untiered recipes.</param>
/// <param name="AreaTags">Content tags the area must share one of, or empty for no gate.</param>
public sealed record RuneshapeRecipe(
    int Row,
    string Id,
    int Size,
    IReadOnlyList<int> Runes,
    string RewardPath,
    string RewardName,
    int RewardCount,
    int RewardGemLevel,
    string Description,
    int MinLevel,
    int MaxLevel,
    IReadOnlyList<int> AreaTags)
{
    /// <summary>Whether the reward is a fixed item rather than something rolled.</summary>
    public bool HasReward => RewardPath.Length > 0;

    /// <summary>The rune in a hole, or -1 past the end.</summary>
    public int RuneAt(int hole) => hole >= 0 && hole < Runes.Count ? Runes[hole] : -1;
}

/// <summary>
/// Every runeshape recipe, every rune's name and the partial-offer weights, out of the install.
/// </summary>
/// <remarks>
/// WHY THE INSTALL AND NOT A SHIPPED EXPORT. The reference plugin ships a JSON built from a
/// data dump by a script that is not in its repository, and that file goes stale the patch a
/// recipe is added - the HF9 area gate reached it as a hand edit. The install's own tables are
/// what the client reads, in the client's own version, by the route the item book and the
/// reward catalogue already take. It costs one background read a session.
///
/// WHAT THE THREE TABLES SAY, from the plugin's decoding of the in-game offer builder
/// (FUN_141e32ab0, docs/monolith-partial-recipes.md): a monolith with N holes and anchor rune A
/// in hole p offers a recipe when its rune at hole p is A, its size is at most N, the area
/// level is inside its band, and - for a recipe SHORTER than N - Expedition2RunesWeights has a
/// row for (A, p + 1, size) whose MinAreaLevel the area meets. The anchor-less unique monolith
/// skips the anchor and the size gate both. See <see cref="MonolithOffers"/>.
///
/// NEVER THROWS. A missing install, a missing layout or a table whose shape has moved leave an
/// empty catalogue that says why, and the monoliths then list no offers and say so.
/// </remarks>
public sealed class RecipeCatalog
{
    /// <summary>The unnamed i32 array at the end of Expedition2Recipes - see data/expedition-tables.json.</summary>
    private const string AreaTagsColumn = "column_19";

    private readonly Dictionary<string, RuneshapeRecipe> _byId;
    private readonly Dictionary<long, int> _partialMinLevel;

    private RecipeCatalog(
        IReadOnlyList<RuneshapeRecipe> recipes,
        IReadOnlyList<string> runeNames,
        Dictionary<long, int> partialMinLevel,
        IReadOnlyList<string> said)
    {
        Recipes = recipes;
        RuneNames = runeNames;
        _partialMinLevel = partialMinLevel;
        _byId = new Dictionary<string, RuneshapeRecipe>(recipes.Count, StringComparer.Ordinal);
        foreach (RuneshapeRecipe recipe in recipes)
        {
            _byId.TryAdd(recipe.Id, recipe);
        }

        Say = said;
    }

    /// <summary>Nothing read - before the install has been asked, and after it refused.</summary>
    public static RecipeCatalog Empty { get; } = new([], [], [], []);

    /// <summary>Every recipe, in table order.</summary>
    public IReadOnlyList<RuneshapeRecipe> Recipes { get; }

    /// <summary>Every rune's engine id, by Expedition2Runes row.</summary>
    public IReadOnlyList<string> RuneNames { get; }

    /// <summary>One line per table about where it came from and whether its layout held.</summary>
    public IReadOnlyList<string> Say { get; }

    /// <summary>How many recipes are known.</summary>
    public int Count => Recipes.Count;

    /// <summary>A rune's name by row, or "#n" for a row the table does not have.</summary>
    public string RuneName(int index)
        => index >= 0 && index < RuneNames.Count && RuneNames[index].Length > 0 ? RuneNames[index] : $"#{index}";

    /// <summary>The recipe with this engine id, or null.</summary>
    public RuneshapeRecipe? ById(string? id)
        => id is { Length: > 0 } && _byId.TryGetValue(id, out RuneshapeRecipe? found) ? found : null;

    /// <summary>
    /// Whether a recipe shorter than the monolith may be offered for this anchor at this level.
    /// </summary>
    /// <param name="anchorRune">The anchor's Expedition2Runes row.</param>
    /// <param name="anchorHole">Its hole, 0-based - the table counts slots from 1.</param>
    /// <param name="size">The recipe's rune count.</param>
    /// <param name="areaLevel">The area's monster level; 0 or less means unknown and is not gated on.</param>
    public bool PartialAllowed(int anchorRune, int anchorHole, int size, int areaLevel)
        => _partialMinLevel.TryGetValue(Key(anchorRune, anchorHole + 1, size), out int minLevel)
           && (areaLevel <= 0 || areaLevel >= minLevel);

    private static long Key(int rune, int slot, int size) => ((long)rune << 16) | ((long)slot << 8) | (uint)size;

    /// <summary>
    /// Reads Expedition2Recipes, Expedition2Runes and Expedition2RunesWeights, with BaseItemTypes
    /// for the rewards' paths and names.
    /// </summary>
    /// <param name="files">The install, or null for none.</param>
    /// <param name="layouts">data/expedition-tables.json.</param>
    /// <param name="itemLayouts">data/item-tables.json, which carries BaseItemTypes.</param>
    public static RecipeCatalog Read(GameFiles? files, QuestTableLayouts? layouts, QuestTableLayouts? itemLayouts)
    {
        if (files is null)
        {
            return new RecipeCatalog([], [], [], ["recipes: no install to read, so the monoliths list no offers"]);
        }

        if (layouts is null)
        {
            return new RecipeCatalog([], [], [], ["recipes: data/expedition-tables.json did not load, so the monoliths list no offers"]);
        }

        var said = new List<string>();
        (LoadedTable? recipes, string recipesWhy) = QuestTables.Open(files, layouts, "Expedition2Recipes", "Runes", "Id");
        (LoadedTable? runes, string runesWhy) = QuestTables.Open(files, layouts, "Expedition2Runes", null, "Id");
        (LoadedTable? weights, string weightsWhy) = QuestTables.Open(files, layouts, "Expedition2RunesWeights", null, "Id");
        said.Add("  Expedition2Recipes      " + (recipes?.Say ?? recipesWhy));
        said.Add("  Expedition2Runes        " + (runes?.Say ?? runesWhy));
        said.Add("  Expedition2RunesWeights " + (weights?.Say ?? weightsWhy));

        LoadedTable? bases = null;
        if (itemLayouts is not null)
        {
            (bases, string basesWhy) = QuestTables.Open(files, itemLayouts, "BaseItemTypes", null, "Id", "Name");
            said.Add("  BaseItemTypes           " + (bases?.Say ?? basesWhy));
        }
        else
        {
            said.Add("  BaseItemTypes           data/item-tables.json did not load, so rewards are unnamed");
        }

        if (recipes is not { Usable: true })
        {
            said.Insert(0, "recipes: Expedition2Recipes did not read, so the monoliths list no offers");
            return new RecipeCatalog([], [], [], said);
        }

        int idAt = layouts.OffsetOf("Expedition2Recipes", "Id");
        int runesAt = layouts.OffsetOf("Expedition2Recipes", "Runes");
        int descriptionAt = layouts.OffsetOf("Expedition2Recipes", "Description");
        int minAt = layouts.OffsetOf("Expedition2Recipes", "MinLevelReq");
        int maxAt = layouts.OffsetOf("Expedition2Recipes", "MaxLevelReq");
        int rewardAt = layouts.OffsetOf("Expedition2Recipes", "Reward");
        int countAt = layouts.OffsetOf("Expedition2Recipes", "RewardCount");
        int gemAt = layouts.OffsetOf("Expedition2Recipes", "RewardGemLevel");
        int tagsAt = layouts.OffsetOf("Expedition2Recipes", AreaTagsColumn);
        if (idAt < 0 || runesAt < 0 || minAt < 0 || maxAt < 0 || rewardAt < 0 || countAt < 0)
        {
            said.Insert(0, "recipes: a column of Expedition2Recipes is not in the vendored layout");
            return new RecipeCatalog([], [], [], said);
        }

        // The rune names, by row. A table that did not read leaves every name empty, and the
        // offers still work - they compare indices, and the names are for people.
        string[] runeNames = [];
        if (runes is { Usable: true } && layouts.OffsetOf("Expedition2Runes", "Id") is >= 0 and var runeIdAt)
        {
            runeNames = new string[runes.File.Rows];
            for (var row = 0; row < runes.File.Rows; row++)
            {
                runeNames[row] = runes.File.Text(row, runeIdAt);
            }
        }

        // The rewards by BaseItemTypes row: a path and an English name each.
        int baseIdAt = itemLayouts?.OffsetOf("BaseItemTypes", "Id") ?? -1;
        int baseNameAt = itemLayouts?.OffsetOf("BaseItemTypes", "Name") ?? -1;
        bool rewardsNamed = bases is { Usable: true } && baseIdAt >= 0 && baseNameAt >= 0;

        DatFile table = recipes.File;
        var made = new List<RuneshapeRecipe>(table.Rows);
        var unnamed = 0;
        for (var row = 0; row < table.Rows; row++)
        {
            string id = table.Text(row, idAt);
            if (id.Length == 0)
            {
                continue;
            }

            IReadOnlyList<DatReference> runeRefs = table.References(row, runesAt);
            var runeIdx = new int[runeRefs.Count];
            for (var i = 0; i < runeRefs.Count; i++)
            {
                runeIdx[i] = runeRefs[i].RowIn(runeNames.Length > 0 ? runeNames.Length : 64);
            }

            string rewardPath = string.Empty;
            string rewardName = string.Empty;
            int rewardRow = rewardsNamed ? table.Reference(row, rewardAt).RowIn(bases!.File.Rows) : -1;
            if (rewardRow >= 0)
            {
                rewardPath = bases!.File.Text(rewardRow, baseIdAt);
                rewardName = bases.File.Text(rewardRow, baseNameAt);
            }
            else if (!table.Reference(row, rewardAt).IsNothing && rewardsNamed)
            {
                unnamed++;
            }

            made.Add(new RuneshapeRecipe(
                row,
                id,
                runeIdx.Length,
                runeIdx,
                rewardPath,
                rewardName,
                table.I32(row, countAt),
                gemAt >= 0 ? table.I32(row, gemAt) : 0,
                descriptionAt >= 0 ? table.Text(row, descriptionAt) : string.Empty,
                table.I32(row, minAt),
                table.I32(row, maxAt),
                tagsAt >= 0 ? table.Ints(row, tagsAt) : []));
        }

        // The partial-offer weights: (rune, slot, size) -> the lowest level that permits it.
        var partial = new Dictionary<long, int>();
        if (weights is { Usable: true })
        {
            int sizeAt = layouts.OffsetOf("Expedition2RunesWeights", "RecipeRuneCount");
            int slotAt = layouts.OffsetOf("Expedition2RunesWeights", "HighlightedRuneSlot");
            int runeAt = layouts.OffsetOf("Expedition2RunesWeights", "HighlightedRune");
            int levelAt = layouts.OffsetOf("Expedition2RunesWeights", "MinAreaLevel");
            if (sizeAt >= 0 && slotAt >= 0 && runeAt >= 0 && levelAt >= 0)
            {
                DatFile ws = weights.File;
                for (var row = 0; row < ws.Rows; row++)
                {
                    int rune = ws.Reference(row, runeAt).RowIn(runeNames.Length > 0 ? runeNames.Length : 64);
                    if (rune < 0)
                    {
                        continue;
                    }

                    long key = Key(rune, ws.I32(row, slotAt), ws.I32(row, sizeAt));
                    int level = ws.I32(row, levelAt);
                    if (!partial.TryGetValue(key, out int known) || level < known)
                    {
                        partial[key] = level;
                    }
                }
            }
        }

        said.Insert(0, $"recipes: {made.Count} runeshape recipes from the install's own tables, {runeNames.Length} runes,"
            + $" {partial.Count} partial-offer weights"
            + (rewardsNamed ? string.Empty : ", rewards UNNAMED (BaseItemTypes did not read)")
            + (unnamed > 0 ? $", {unnamed} rewards point past the base types" : string.Empty));
        return new RecipeCatalog(made, runeNames, partial, said);
    }
}
