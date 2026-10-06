using PoEformance.Game.Files;

namespace PoEformance.Features;

/// <summary>What a reward is called in English, and the picture it draws.</summary>
/// <param name="Name">The English name, as poe.ninja spells it.</param>
/// <param name="Art">The inventory picture's path, "Art/2DItems/Currency/....dds", or empty.</param>
public readonly record struct RewardName(string Name, string Art);

/// <summary>
/// Every base type's English name and picture, out of the install's own tables.
/// </summary>
/// <remarks>
/// WHY THE INSTALL AND NOT THE CLIENT'S MEMORY. A recipe on the Runeshape Combinations panel
/// names its reward by metadata path, and that path has to become the English name poe.ninja
/// prices under. The client's own BaseItemTypes table is loaded in the client's LANGUAGE - see
/// BaseItemTable, whose Name column is what a German client paints - so reading it from memory
/// prices nothing off an English site. The install's data/balance tables are the unlocalised
/// ones, read by the same route the item book takes, and they cost one background read a
/// session.
///
/// WHY NOT <see cref="ItemVisuals"/>, which reads the same two tables: it keeps only the rows
/// with a 3D model, which is exactly the rows a monolith never pays - currency, gems, runes and
/// logbooks have an icon and no .ao. This keeps every row, name and picture, and nothing else.
///
/// THE SHIPPED data/item-names.json IS THE FALLBACK, not this one's: it is an older export of the
/// same column, so where both answer they agree, and where the install cannot be read the
/// shipped table still prices everything it has heard of. See RunecraftPrices.
///
/// NEVER THROWS. A missing install, a missing layout file or a table whose shape has moved all
/// leave an empty catalogue that says why, and the panel then prices by the shipped table alone.
/// </remarks>
public sealed class RewardCatalog
{
    private readonly Dictionary<string, RewardName> _byPath;

    private RewardCatalog(Dictionary<string, RewardName> byPath, IReadOnlyList<string> said)
    {
        _byPath = byPath;
        Say = said;
    }

    /// <summary>Nothing read - the state before the install has been asked, and after it refused.</summary>
    public static RewardCatalog Empty { get; } = new(
        new Dictionary<string, RewardName>(StringComparer.OrdinalIgnoreCase), []);

    /// <summary>One line per table about where it came from and whether its layout held.</summary>
    public IReadOnlyList<string> Say { get; }

    /// <summary>How many base types are named.</summary>
    public int Count => _byPath.Count;

    /// <summary>The name and picture for a metadata path, or null when the table has no such row.</summary>
    public RewardName? Of(string? path)
        => path is { Length: > 0 } && _byPath.TryGetValue(path, out RewardName found) ? found : null;

    /// <summary>
    /// Reads BaseItemTypes and ItemVisualIdentity out of the install and joins them.
    /// </summary>
    /// <remarks>
    /// The picture is optional per row: a base type whose visual reference names no row, or
    /// whose row carries no DDSFile, is still named. The name is what prices; the picture is
    /// the second key, for the rows the price site lists under a picture and nothing else.
    /// </remarks>
    public static RewardCatalog Read(GameFiles? files, QuestTableLayouts? layouts)
    {
        if (files is null)
        {
            return new RewardCatalog(
                new Dictionary<string, RewardName>(StringComparer.OrdinalIgnoreCase),
                ["rewards: no install to read, so the recipe panel names rewards from the shipped table"]);
        }

        if (layouts is null)
        {
            return new RewardCatalog(
                new Dictionary<string, RewardName>(StringComparer.OrdinalIgnoreCase),
                ["rewards: data/item-tables.json did not load, so the recipe panel names rewards from the shipped table"]);
        }

        var said = new List<string>();
        (LoadedTable? bases, string basesWhy) = QuestTables.Open(files, layouts, "BaseItemTypes", null, "Id", "Name");
        (LoadedTable? art, string artWhy) = QuestTables.Open(files, layouts, "ItemVisualIdentity", null, "Id", "DDSFile");
        said.Add("  BaseItemTypes      " + (bases?.Say ?? basesWhy));
        said.Add("  ItemVisualIdentity " + (art?.Say ?? artWhy));

        if (bases is not { Usable: true })
        {
            said.Insert(0, "rewards: BaseItemTypes did not read, so the recipe panel names rewards from the shipped table");
            return new RewardCatalog(new Dictionary<string, RewardName>(StringComparer.OrdinalIgnoreCase), said);
        }

        int idAt = layouts.OffsetOf("BaseItemTypes", "Id");
        int nameAt = layouts.OffsetOf("BaseItemTypes", "Name");
        int visualAt = layouts.OffsetOf("BaseItemTypes", "ItemVisualIdentity");
        int ddsAt = layouts.OffsetOf("ItemVisualIdentity", "DDSFile");
        if (idAt < 0 || nameAt < 0)
        {
            said.Insert(0, "rewards: a column of BaseItemTypes is not in the vendored layout");
            return new RewardCatalog(new Dictionary<string, RewardName>(StringComparer.OrdinalIgnoreCase), said);
        }

        bool pictures = art is { Usable: true } && visualAt >= 0 && ddsAt >= 0;
        var byPath = new Dictionary<string, RewardName>(bases.File.Rows, StringComparer.OrdinalIgnoreCase);
        var painted = 0;

        DatFile table = bases.File;
        for (var row = 0; row < table.Rows; row++)
        {
            string path = table.Text(row, idAt);
            string name = table.Text(row, nameAt);
            if (path.Length == 0 || name.Length == 0)
            {
                continue;
            }

            string picture = string.Empty;
            if (pictures)
            {
                int visual = table.Reference(row, visualAt).RowIn(art!.File.Rows);
                if (visual >= 0)
                {
                    picture = art.File.Text(visual, ddsAt).Replace('\\', '/');
                    if (picture.Length > 0)
                    {
                        painted++;
                    }
                }
            }

            byPath.TryAdd(path, new RewardName(name, picture));
        }

        said.Insert(0, $"rewards: {byPath.Count} base types named from the install's own tables, {painted} with a picture");
        return new RewardCatalog(byPath, said);
    }
}
