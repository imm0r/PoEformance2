using System.Globalization;
using System.Text;

namespace PoEformance.Features;

/// <summary>
/// The effect table worked out once, as the columns a grid draws and the text a search reads.
/// </summary>
/// <remarks>
/// THE ITEM BOOK'S SHAPE: the same grid, the same query grammar, the searchable text built once
/// per table - see <see cref="ColumnBook"/>. Its rows are projectiles, animated effects and ground
/// effects, told apart by "kind"; a row's .ao files - its art variants, what it looks like stuck
/// or bouncing - are offered by the window rather than spread over columns.
/// </remarks>
public sealed class EffectBook : ColumnBook
{
    /// <summary>The headings the column chooser groups the columns under.</summary>
    private const string Named = "What it is";
    private const string Files = "Files";

    private EffectBook(
        ColumnStore store,
        string[] paths,
        bool[] projectile,
        string[] find,
        Dictionary<string, int> rows,
        string[] groups,
        bool[] shown,
        Dictionary<string, Held> held,
        Dictionary<string, int> numbers)
        : base(store, paths, find, rows, groups, shown, held, numbers)
    {
        Projectiles = projectile;
    }

    /// <summary>A book with no effects in it.</summary>
    public static EffectBook Empty { get; } = new(ColumnStore.Empty, [], [], [], [], [], [], [], []);

    /// <summary>Which rows are projectiles, for the grid to ink.</summary>
    public bool[] Projectiles { get; }

    /// <summary>Works the table into columns. Never throws; an empty table gives an empty book.</summary>
    public static EffectBook Of(EffectVisuals? table)
    {
        if (table is null || table.Count == 0)
        {
            return Empty;
        }

        int count = table.Count;
        var paths = new string[count];
        var projectile = new bool[count];
        var find = new string[count];
        var rows = new Dictionary<string, int>(count, StringComparer.OrdinalIgnoreCase);

        var names = new string[count];
        var kinds = new string[count];
        var files = new double[count];
        var filesText = new string[count];
        var models = new string[count];
        var folders = new string[count];

        var text = new StringBuilder(256);
        for (var at = 0; at < count; at++)
        {
            EffectVisual one = table.All[at];
            paths[at] = one.Path;
            projectile[at] = one.Kind == EffectVisuals.Projectile;
            rows.TryAdd(one.Path, at);

            names[at] = one.Name;
            kinds[at] = one.Kind;
            files[at] = one.Files.Count;
            filesText[at] = one.Files.Count.ToString(CultureInfo.InvariantCulture);

            string first = one.Files.Count > 0 ? one.Files[0].Ao : string.Empty;
            models[at] = Tail(first);
            int slash = first.LastIndexOf('/');
            folders[at] = slash > 0 ? first[..slash] : string.Empty;

            text.Clear();
            text.Append(one.Path).Append(' ').Append(one.Name).Append(' ').Append(one.Kind);
            foreach (EffectFile file in one.Files)
            {
                text.Append(' ').Append(file.Label).Append(' ').Append(file.Ao);
            }

            find[at] = text.ToString().ToLowerInvariant();
        }

        (DataColumn Column, string Group, bool Shown)[] laid =
        [
            (DataColumn.Words("name", names), Named, true),
            (DataColumn.Labels("kind", kinds), Named, true),
            (DataColumn.Magnitudes("files", string.Empty, files, filesText), Files, true),
            (DataColumn.Words("model", models), Files, true),
            (DataColumn.Words("folder", folders), Files, false),
        ];

        ColumnStore store = ColumnStore.Of([.. laid.Select(one => one.Column)]);

        var held = new Dictionary<string, Held>(StringComparer.Ordinal);
        var numbers = new Dictionary<string, int>(StringComparer.Ordinal);
        Index(store, held, numbers);

        return new EffectBook(
            store,
            paths,
            projectile,
            find,
            rows,
            [.. laid.Select(one => one.Group)],
            [.. laid.Select(one => one.Shown)],
            held,
            numbers);
    }
}
