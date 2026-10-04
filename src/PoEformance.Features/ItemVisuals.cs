using PoEformance.Game.Files;

namespace PoEformance.Features;

/// <summary>
/// One item the game has a model for: what it is called, and which <c>.ao</c> draws it.
/// </summary>
/// <param name="Path">
/// The book's key. A base type's own metadata path - <c>Metadata/Items/Weapons/...</c> - or, for a
/// unique, <see cref="ItemVisuals.UniquePrefix"/> and its art id, because a unique has no path of its
/// own: it is a base type wearing different art.
/// </param>
/// <param name="Name">The words on the item. A unique's name, or a base type's.</param>
/// <param name="Class">The item class's name - "One Hand Swords" - or empty where it did not resolve.</param>
/// <param name="Unique">Whether this row came from UniqueStashLayout rather than BaseItemTypes.</param>
/// <param name="DropLevel">The base type's drop level. Zero for a unique, which the layout does not carry.</param>
/// <param name="Width">Inventory cells across. Zero for a unique.</param>
/// <param name="Height">Inventory cells down. Zero for a unique.</param>
/// <param name="Art">ItemVisualIdentity's Id - the key the game exposes on the item itself.</param>
/// <param name="Ao">ItemVisualIdentity.AOFile.</param>
/// <param name="Ao2">ItemVisualIdentity.AOFile2. See <see cref="ItemVisuals"/> for why both are kept.</param>
/// <param name="Icon">ItemVisualIdentity.DDSFile - the inventory picture, which GameArt already reads.</param>
public sealed record ItemVisual(
    string Path,
    string Name,
    string Class,
    bool Unique,
    int DropLevel,
    int Width,
    int Height,
    string Art,
    string Ao,
    string Ao2,
    string Icon)
{
    /// <summary>
    /// The <c>.ao</c> the model pane opens first: AOFile, or AOFile2 where the first is empty.
    /// </summary>
    public string Model => Ao.Length > 0 ? Ao : Ao2;
}

/// <summary>
/// Every item with a 3D model, read out of the install's own tables.
/// </summary>
/// <remarks>
/// THE ITEM BOOK'S TABLE, and the same route the monster book takes: an item's model is an
/// <c>.ao</c> exactly as a monster's is, so once a row says which <c>.ao</c>, everything after it -
/// the mesh, the materials, the textures, the renderer - is MonsterModels' walk unchanged. What
/// is new is only the first hop, and it is a join over the install's own tables:
///
///     BaseItemTypes.ItemVisualIdentity -> ItemVisualIdentity.AOFile / AOFile2
///     BaseItemTypes.ItemClass          -> ItemClasses.Name
///     UniqueStashLayout.WordsKey       -> Words.Text            (a unique's name)
///     UniqueStashLayout.ItemVisualIdentityKey -> ItemVisualIdentity (a unique's own art)
///
/// UNIQUES ARE ROWS OF THEIR OWN because they are drawn differently. A unique is a base type
/// wearing other art, and that art is a different ItemVisualIdentity row with a different .ao -
/// so listing only the base types would show every unique sword as the plain sword it is built on.
///
/// WHY THE INSTALL RATHER THAN MEMORY: ItemVisualIdentity, UniqueStashLayout and Words are not in
/// the loader's file table on a live 0.5.5 client (see UniqueNames), so the files are read. The
/// layouts are vendored from poe-tool-dev/dat-schema in data/item-tables.json and checked against
/// each FILE's own row size at runtime; <see cref="Say"/> reports which way it went per table.
///
/// BOTH AOFile AND AOFile2 ARE KEPT, AND NEITHER IS CALLED "THE" MODEL. The schema names the two
/// columns and says nothing about what separates them, and no item .ao has been read against the
/// game yet to settle it. Choosing one here would be a guess presented as a fact; the window
/// offers both where both are filled, and the model walk says what it found in each.
///
/// ROWS WITHOUT ANY .ao ARE LEFT OUT and counted: currency, gems and the like have an icon and no
/// model, and a book of models that listed them would be mostly rows that draw nothing.
/// </remarks>
public sealed class ItemVisuals
{
    /// <summary>What a unique's key starts with, since a unique has no metadata path of its own.</summary>
    public const string UniquePrefix = "Unique/";

    private readonly Dictionary<string, int> _rows;

    private ItemVisuals(IReadOnlyList<ItemVisual> all, IReadOnlyList<string> said)
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
    public static ItemVisuals Empty { get; } = new([], []);

    /// <summary>Every item with a model, base types first and then uniques, each in table order.</summary>
    public IReadOnlyList<ItemVisual> All { get; }

    /// <summary>One line per table about where it came from and whether its layout held.</summary>
    public IReadOnlyList<string> Say { get; }

    /// <summary>How many items there are.</summary>
    public int Count => All.Count;

    /// <summary>The item with this key, or null.</summary>
    public ItemVisual? Find(string? path)
        => path is { Length: > 0 } && _rows.TryGetValue(path, out int row) ? All[row] : null;

    /// <summary>
    /// Reads the tables out of the install and joins them, or says why it could not.
    /// </summary>
    /// <remarks>
    /// NEVER THROWS. BaseItemTypes and ItemVisualIdentity are the two that must read - without
    /// either there is no model to name. ItemClasses is optional and only costs the class column.
    /// UniqueStashLayout and Words are optional together and only cost the uniques.
    /// </remarks>
    public static ItemVisuals Read(GameFiles? files, QuestTableLayouts? layouts)
    {
        if (files is null)
        {
            return new ItemVisuals([], ["items: no install to read, so the item book is empty"]);
        }

        if (layouts is null)
        {
            return new ItemVisuals([], ["items: data/item-tables.json did not load, so the item book is empty"]);
        }

        var said = new List<string>();

        (LoadedTable? bases, string basesWhy) = QuestTables.Open(
            files, layouts, "BaseItemTypes", null, "Id", "Name");
        (LoadedTable? art, string artWhy) = QuestTables.Open(
            files, layouts, "ItemVisualIdentity", null, "Id");
        (LoadedTable? classes, string classesWhy) = QuestTables.Open(
            files, layouts, "ItemClasses", null, "Id", "Name");
        (LoadedTable? layout, string layoutWhy) = QuestTables.Open(
            files, layouts, "UniqueStashLayout", null);
        (LoadedTable? words, string wordsWhy) = QuestTables.Open(
            files, layouts, "Words", null, "Text");

        said.Add("  BaseItemTypes      " + (bases?.Say ?? basesWhy));
        said.Add("  ItemVisualIdentity " + (art?.Say ?? artWhy));
        said.Add("  ItemClasses        " + (classes?.Say ?? classesWhy));
        said.Add("  UniqueStashLayout  " + (layout?.Say ?? layoutWhy));
        said.Add("  Words              " + (words?.Say ?? wordsWhy));

        if (bases is not { Usable: true } || art is not { Usable: true })
        {
            said.Insert(0, "items: BaseItemTypes or ItemVisualIdentity did not read, so the item book is empty");
            return new ItemVisuals([], said);
        }

        Art visuals = Art.Of(art.File, layouts);
        if (!visuals.Ready)
        {
            said.Insert(0, "items: a column of ItemVisualIdentity is not in the vendored layout");
            return new ItemVisuals([], said);
        }

        int idAt = layouts.OffsetOf("BaseItemTypes", "Id");
        int nameAt = layouts.OffsetOf("BaseItemTypes", "Name");
        int classAt = layouts.OffsetOf("BaseItemTypes", "ItemClass");
        int widthAt = layouts.OffsetOf("BaseItemTypes", "Width");
        int heightAt = layouts.OffsetOf("BaseItemTypes", "Height");
        int dropAt = layouts.OffsetOf("BaseItemTypes", "DropLevel");
        int visualAt = layouts.OffsetOf("BaseItemTypes", "ItemVisualIdentity");

        if (idAt < 0 || nameAt < 0 || classAt < 0 || widthAt < 0 || heightAt < 0 || dropAt < 0 || visualAt < 0)
        {
            said.Insert(0, "items: a column of BaseItemTypes is not in the vendored layout");
            return new ItemVisuals([], said);
        }

        string[] classNames = Classes(classes, layouts);

        var all = new List<ItemVisual>(bases.File.Rows / 2);
        var bare = 0;

        DatFile table = bases.File;
        for (var row = 0; row < table.Rows; row++)
        {
            int visual = table.Reference(row, visualAt).RowIn(art.File.Rows);
            if (visual < 0 || !visuals.HasModel(visual))
            {
                bare++;
                continue;
            }

            string path = table.Text(row, idAt);
            if (path.Length == 0)
            {
                continue;
            }

            int kind = table.Reference(row, classAt).RowIn(classNames.Length);
            all.Add(visuals.Item(
                visual,
                path,
                table.Text(row, nameAt) is { Length: > 0 } named ? named : Tail(path),
                kind >= 0 ? classNames[kind] : string.Empty,
                unique: false,
                table.I32(row, dropAt),
                table.I32(row, widthAt),
                table.I32(row, heightAt)));
        }

        int bases_ = all.Count;
        int uniques = Uniques(layout, words, layouts, visuals, art.File.Rows, all);

        said.Insert(0, $"items: {bases_} base types and {uniques} uniques with a model from the install's own tables,"
            + $" {bare} base types without one left out");
        return new ItemVisuals(all, said);
    }

    /// <summary>
    /// Adds a row per unique art, or nothing where the two tables it needs did not read.
    /// </summary>
    /// <remarks>
    /// ONE ROW PER ART, NOT PER LAYOUT ROW. Several uniques have an alternate-art layout row that
    /// names the same art as their ordinary one, and two rows drawing one model would be the same
    /// picture twice. Plain art wins the name over alternate - UniqueNames' rule, for the same
    /// reason: the answer must not depend on row order.
    /// </remarks>
    private static int Uniques(
        LoadedTable? layout,
        LoadedTable? words,
        QuestTableLayouts layouts,
        Art visuals,
        int artRows,
        List<ItemVisual> into)
    {
        if (layout is not { Usable: true } || words is not { Usable: true })
        {
            return 0;
        }

        int wordsAt = layouts.OffsetOf("UniqueStashLayout", "WordsKey");
        int artAt = layouts.OffsetOf("UniqueStashLayout", "ItemVisualIdentityKey");
        int alternateAt = layouts.OffsetOf("UniqueStashLayout", "IsAlternateArt");
        int textAt = layouts.OffsetOf("Words", "Text");
        if (wordsAt < 0 || artAt < 0 || alternateAt < 0 || textAt < 0)
        {
            return 0;
        }

        // Art row to (name, alternate), first plain one wins.
        var found = new Dictionary<int, (string Name, bool Alternate)>();
        var order = new List<int>();
        DatFile table = layout.File;
        for (var row = 0; row < table.Rows; row++)
        {
            int visual = table.Reference(row, artAt).RowIn(artRows);
            int word = table.Reference(row, wordsAt).RowIn(words.File.Rows);
            if (visual < 0 || word < 0 || !visuals.HasModel(visual))
            {
                continue;
            }

            string name = words.File.Text(word, textAt);
            if (name.Length == 0)
            {
                continue;
            }

            bool alternate = table.Bool(row, alternateAt);
            if (found.TryGetValue(visual, out (string Name, bool Alternate) had))
            {
                if (had.Alternate && !alternate)
                {
                    found[visual] = (name, alternate);
                }

                continue;
            }

            found[visual] = (name, alternate);
            order.Add(visual);
        }

        foreach (int visual in order)
        {
            string id = visuals.Id(visual);
            into.Add(visuals.Item(
                visual, UniquePrefix + id, found[visual].Name, string.Empty, unique: true, 0, 0, 0));
        }

        return order.Count;
    }

    /// <summary>Each ItemClasses row's name, its Id where the name is empty, or none at all.</summary>
    private static string[] Classes(LoadedTable? classes, QuestTableLayouts layouts)
    {
        if (classes is not { Usable: true })
        {
            return [];
        }

        int idAt = layouts.OffsetOf("ItemClasses", "Id");
        int nameAt = layouts.OffsetOf("ItemClasses", "Name");
        if (idAt < 0 || nameAt < 0)
        {
            return [];
        }

        var names = new string[classes.File.Rows];
        for (var row = 0; row < names.Length; row++)
        {
            names[row] = classes.File.Text(row, nameAt) is { Length: > 0 } named
                ? named
                : classes.File.Text(row, idAt);
        }

        return names;
    }

    private static string Tail(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash >= 0 && slash + 1 < path.Length ? path[(slash + 1)..] : path;
    }

    /// <summary>
    /// ItemVisualIdentity's four columns this reads, each row's read at most once.
    /// </summary>
    /// <remarks>
    /// READ ONCE PER ROW AND CACHED, because a row is asked about twice - "has it a model" while
    /// filtering, and its paths when the item is made - and a unique can share its art row with
    /// nothing else while thousands of base types point at a few hundred rows.
    /// </remarks>
    private sealed class Art
    {
        private readonly DatFile _file;
        private readonly int _id;
        private readonly int _ao;
        private readonly int _ao2;
        private readonly int _icon;
        private readonly (string Id, string Ao, string Ao2, string Icon)?[] _read;

        private Art(DatFile file, int id, int ao, int ao2, int icon)
        {
            _file = file;
            _id = id;
            _ao = ao;
            _ao2 = ao2;
            _icon = icon;
            _read = new (string, string, string, string)?[file.Rows];
        }

        public bool Ready => _id >= 0 && _ao >= 0 && _ao2 >= 0 && _icon >= 0;

        public static Art Of(DatFile file, QuestTableLayouts layouts)
            => new(
                file,
                layouts.OffsetOf("ItemVisualIdentity", "Id"),
                layouts.OffsetOf("ItemVisualIdentity", "AOFile"),
                layouts.OffsetOf("ItemVisualIdentity", "AOFile2"),
                layouts.OffsetOf("ItemVisualIdentity", "DDSFile"));

        public bool HasModel(int row)
        {
            (_, string ao, string ao2, _) = Row(row);
            return ao.Length > 0 || ao2.Length > 0;
        }

        public string Id(int row) => Row(row).Id;

        public ItemVisual Item(
            int row, string path, string name, string kind, bool unique, int drop, int width, int height)
        {
            (string id, string ao, string ao2, string icon) = Row(row);
            return new ItemVisual(path, name, kind, unique, drop, width, height, id, ao, ao2, icon);
        }

        private (string Id, string Ao, string Ao2, string Icon) Row(int row)
        {
            if (_read[row] is { } done)
            {
                return done;
            }

            (string, string, string, string) made = (
                _file.Text(row, _id),
                _file.Text(row, _ao).Trim(),
                _file.Text(row, _ao2).Trim(),
                _file.Text(row, _icon));
            _read[row] = made;
            return made;
        }
    }
}
