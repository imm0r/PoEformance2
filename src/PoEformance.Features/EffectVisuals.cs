using PoEformance.Game.Files;

namespace PoEformance.Features;

/// <summary>One of the .ao files an effect can be drawn from, with what the table calls that slot.</summary>
/// <param name="Label">"art 1", "stuck", "bounce" - the column it came from.</param>
/// <param name="Ao">The file.</param>
public readonly record struct EffectFile(string Label, string Ao);

/// <summary>
/// One effect the game has a model for: a projectile, an animated effect or a ground effect.
/// </summary>
/// <param name="Path">The book's key - the kind and the row's id, since the three tables share no key.</param>
/// <param name="Name">The row's own id - the only name these things have.</param>
/// <param name="Kind">"projectile", "animated" or "ground".</param>
/// <param name="Files">Every .ao the row names, in column order. The window offers a choice where there are several.</param>
public sealed record EffectVisual(string Path, string Name, string Kind, IReadOnlyList<EffectFile> Files);

/// <summary>
/// Every effect with a 3D model, read out of the install's own tables.
/// </summary>
/// <remarks>
/// THE EFFECT BOOK'S TABLE, on the item book's route: an effect's model is an <c>.ao</c> like a
/// monster's, so everything after the row is MonsterModels' walk - animated, where the .ao names a
/// skeleton. Three tables say which .ao, and they are the ones dat-schema gives an .ao column that a
/// spell or a skill is drawn from:
///
///     Projectiles     AOFiles (several art variants), Stuck_AOFile, Bounce_AOFile
///     MiscAnimated    AOFile - the animated effects skills and monsters play
///     GroundEffects   AOFile, named through GroundEffectTypes.Id
///
/// PARTICLES ARE NOT HERE. A great deal of what a spell looks like is .pet particle emitters inside
/// .epk packs, and their parameters are decoded by no reference; this book is the meshes, which are.
///
/// NEVER THROWS. Each table is optional - one that does not read costs its own rows - and Say has a
/// line per table saying whether its vendored layout held against the file.
/// </remarks>
public sealed class EffectVisuals
{
    /// <summary>The three kinds, as the "kind" column spells them.</summary>
    public const string Projectile = "projectile";
    public const string Animated = "animated";
    public const string Ground = "ground";

    private readonly Dictionary<string, int> _rows;

    private EffectVisuals(IReadOnlyList<EffectVisual> all, IReadOnlyList<string> said)
    {
        All = all;
        Say = said;
        _rows = new Dictionary<string, int>(all.Count, StringComparer.OrdinalIgnoreCase);
        for (var row = 0; row < all.Count; row++)
        {
            _rows.TryAdd(all[row].Path, row);
        }
    }

    /// <summary>Nothing read.</summary>
    public static EffectVisuals Empty { get; } = new([], []);

    /// <summary>Every effect with a model: projectiles, then animated effects, then ground effects.</summary>
    public IReadOnlyList<EffectVisual> All { get; }

    /// <summary>One line per table about where it came from and whether its layout held.</summary>
    public IReadOnlyList<string> Say { get; }

    /// <summary>How many effects there are.</summary>
    public int Count => All.Count;

    /// <summary>The effect with this key, or null.</summary>
    public EffectVisual? Find(string? path)
        => path is { Length: > 0 } && _rows.TryGetValue(path, out int row) ? All[row] : null;

    /// <summary>Reads the tables out of the install, or says why it could not. Never throws.</summary>
    public static EffectVisuals Read(GameFiles? files, QuestTableLayouts? layouts)
    {
        if (files is null)
        {
            return new EffectVisuals([], ["effects: no install to read, so the effect book is empty"]);
        }

        if (layouts is null)
        {
            return new EffectVisuals([], ["effects: data/effect-tables.json did not load, so the effect book is empty"]);
        }

        var said = new List<string>();
        var all = new List<EffectVisual>();

        (LoadedTable? projectiles, string projectilesWhy) = QuestTables.Open(files, layouts, "Projectiles", "AOFiles", "Id");
        (LoadedTable? animated, string animatedWhy) = QuestTables.Open(files, layouts, "MiscAnimated", null, "Id", "AOFile");
        (LoadedTable? ground, string groundWhy) = QuestTables.Open(files, layouts, "GroundEffects", "AOFile");
        (LoadedTable? types, string typesWhy) = QuestTables.Open(files, layouts, "GroundEffectTypes", null, "Id");

        said.Add("  Projectiles        " + (projectiles?.Say ?? projectilesWhy));
        said.Add("  MiscAnimated       " + (animated?.Say ?? animatedWhy));
        said.Add("  GroundEffects      " + (ground?.Say ?? groundWhy));
        said.Add("  GroundEffectTypes  " + (types?.Say ?? typesWhy));

        int fromProjectiles = Projectiles(projectiles, layouts, all);
        int fromAnimated = Animations(animated, layouts, all);
        int fromGround = Grounds(ground, types, layouts, all);

        said.Insert(0, $"effects: {fromProjectiles} projectiles, {fromAnimated} animated effects and {fromGround} ground effects with a model");
        return new EffectVisuals(all, said);
    }

    private static int Projectiles(LoadedTable? table, QuestTableLayouts layouts, List<EffectVisual> into)
    {
        if (table is not { Usable: true })
        {
            return 0;
        }

        int idAt = layouts.OffsetOf("Projectiles", "Id");
        int artAt = layouts.OffsetOf("Projectiles", "AOFiles");
        int stuckAt = layouts.OffsetOf("Projectiles", "Stuck_AOFile");
        int bounceAt = layouts.OffsetOf("Projectiles", "Bounce_AOFile");
        if (idAt < 0 || artAt < 0 || stuckAt < 0 || bounceAt < 0)
        {
            return 0;
        }

        int before = into.Count;
        DatFile file = table.File;
        for (var row = 0; row < file.Rows; row++)
        {
            var files = new List<EffectFile>();
            IReadOnlyList<string> art = Texts(file, row, artAt);
            for (var one = 0; one < art.Count; one++)
            {
                files.Add(new EffectFile(art.Count == 1 ? "art" : $"art {one + 1}", art[one]));
            }

            foreach (string stuck in Texts(file, row, stuckAt))
            {
                files.Add(new EffectFile("stuck", stuck));
            }

            if (file.Text(row, bounceAt).Trim() is { Length: > 0 } bounce)
            {
                files.Add(new EffectFile("bounce", bounce));
            }

            Add(into, Projectile, file.Text(row, idAt).Trim(), files);
        }

        return into.Count - before;
    }

    private static int Animations(LoadedTable? table, QuestTableLayouts layouts, List<EffectVisual> into)
    {
        if (table is not { Usable: true })
        {
            return 0;
        }

        int idAt = layouts.OffsetOf("MiscAnimated", "Id");
        int aoAt = layouts.OffsetOf("MiscAnimated", "AOFile");
        if (idAt < 0 || aoAt < 0)
        {
            return 0;
        }

        int before = into.Count;
        DatFile file = table.File;
        for (var row = 0; row < file.Rows; row++)
        {
            string ao = file.Text(row, aoAt).Trim();
            Add(into, Animated, file.Text(row, idAt).Trim(), ao.Length > 0 ? [new EffectFile("art", ao)] : []);
        }

        return into.Count - before;
    }

    /// <summary>
    /// Ground effects, named by their type.
    /// </summary>
    /// <remarks>
    /// A ROW HAS NO ID OF ITS OWN - GroundEffects is keyed by its type - so the name is the type's Id,
    /// and the row number is added where two rows share a type, which several do.
    /// </remarks>
    private static int Grounds(LoadedTable? table, LoadedTable? types, QuestTableLayouts layouts, List<EffectVisual> into)
    {
        if (table is not { Usable: true })
        {
            return 0;
        }

        int typeAt = layouts.OffsetOf("GroundEffects", "GroundEffectTypesKey");
        int aoAt = layouts.OffsetOf("GroundEffects", "AOFile");
        int idAt = layouts.OffsetOf("GroundEffectTypes", "Id");
        if (typeAt < 0 || aoAt < 0)
        {
            return 0;
        }

        int before = into.Count;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DatFile file = table.File;
        for (var row = 0; row < file.Rows; row++)
        {
            IReadOnlyList<string> art = Texts(file, row, aoAt);
            if (art.Count == 0)
            {
                continue;
            }

            string name = string.Empty;
            if (types is { Usable: true } && idAt >= 0
                && file.Reference(row, typeAt).RowIn(types.File.Rows) is >= 0 and var type)
            {
                name = types.File.Text(type, idAt).Trim();
            }

            if (name.Length == 0 || !seen.Add(name))
            {
                name = (name.Length > 0 ? name + " #" : "row ") + row.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            var files = new List<EffectFile>(art.Count);
            for (var one = 0; one < art.Count; one++)
            {
                files.Add(new EffectFile(art.Count == 1 ? "art" : $"art {one + 1}", art[one]));
            }

            Add(into, Ground, name, files);
        }

        return into.Count - before;
    }

    /// <summary>Keeps a row that names at least one .ao and has a name, keyed by kind and name.</summary>
    private static void Add(List<EffectVisual> into, string kind, string name, IReadOnlyList<EffectFile> files)
    {
        if (name.Length > 0 && files.Count > 0)
        {
            into.Add(new EffectVisual(kind + "/" + name, name, kind, files));
        }
    }

    /// <summary>A string-array column: eight bytes an element, each an offset into the variable section.</summary>
    private static IReadOnlyList<string> Texts(DatFile file, int row, int offset)
        => [.. file.References(row, offset, elementWidth: 8)
            .Select(one => file.TextAt(one.First).Trim().Replace('\\', '/'))
            .Where(one => one.Length > 0)];
}
