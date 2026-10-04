using System.Globalization;
using System.Text;

namespace PoEformance.Features;

/// <summary>
/// The item table worked out once, as the columns a grid draws and the text a search reads.
/// </summary>
/// <remarks>
/// THE MONSTER BOOK'S SHAPE, ON PURPOSE: the same grid, the same query grammar, the same rule that
/// the searchable text is built once per table and a keystroke only scans strings that already
/// exist - all of which is <see cref="ColumnBook"/>. What is here is what an item row IS.
///
/// FEW COLUMNS SHOWN, MORE TO CHOOSE FROM. An item's interesting part here is its model, and the
/// model is in the pane beside the list rather than in a cell; what the starting columns carry is
/// what somebody narrows by - the class, base or unique, the drop level. The art id and the other
/// file names are there for the question "which file is this", and are switched on when it is asked.
/// </remarks>
public sealed class ItemBook : ColumnBook
{
    /// <summary>The headings the column chooser groups the columns under.</summary>
    private const string Named = "What it is";
    private const string Figures = "Figures";
    private const string Files = "Files";

    private ItemBook(
        ColumnStore store,
        string[] paths,
        bool[] unique,
        string[] find,
        Dictionary<string, int> rows,
        string[] groups,
        bool[] shown,
        Dictionary<string, Held> held,
        Dictionary<string, int> numbers)
        : base(store, paths, find, rows, groups, shown, held, numbers)
    {
        Unique = unique;
    }

    /// <summary>A book with no items in it.</summary>
    public static ItemBook Empty { get; } = new(ColumnStore.Empty, [], [], [], [], [], [], [], []);

    /// <summary>Which rows are uniques, for the grid to ink.</summary>
    public bool[] Unique { get; }

    /// <summary>Works the table into columns. Never throws; an empty table gives an empty book.</summary>
    public static ItemBook Of(ItemVisuals? table)
    {
        if (table is null || table.Count == 0)
        {
            return Empty;
        }

        int count = table.Count;
        var paths = new string[count];
        var unique = new bool[count];
        var find = new string[count];
        var rows = new Dictionary<string, int>(count, StringComparer.OrdinalIgnoreCase);

        var names = new string[count];
        var classes = new string[count];
        var kinds = new string[count];
        var arts = new string[count];
        var drops = new double[count];
        var dropsText = new string[count];
        var cells = new double[count];
        var cellsText = new string[count];
        var widths = new double[count];
        var widthsText = new string[count];
        var heights = new double[count];
        var heightsText = new string[count];
        var models = new string[count];
        var seconds = new string[count];
        var icons = new string[count];

        var text = new StringBuilder(256);
        for (var at = 0; at < count; at++)
        {
            ItemVisual one = table.All[at];
            paths[at] = one.Path;
            unique[at] = one.Unique;
            rows.TryAdd(one.Path, at);

            names[at] = one.Name;
            classes[at] = one.Class;
            kinds[at] = one.Unique ? "unique" : "base";
            arts[at] = one.Art;

            // A UNIQUE'S LEVEL AND SIZE ARE BLANK, NOT ZERO: the layout row it comes from carries
            // neither, and "level 0" would read as an item anybody can drop at the start. The
            // NUMBER is NaN for the same reason - as 0, every unique sat in the drop histogram's
            // first bin, sorted under the level-1 items and answered "drop<10". See ColumnSpread.Of.
            drops[at] = Figure(one.Unique, one.DropLevel);
            dropsText[at] = Blank(one.Unique, one.DropLevel);
            cells[at] = Figure(one.Unique, one.Width * one.Height);
            cellsText[at] = one.Unique
                ? string.Empty
                : string.Create(CultureInfo.InvariantCulture, $"{one.Width}x{one.Height}");
            widths[at] = Figure(one.Unique, one.Width);
            widthsText[at] = Blank(one.Unique, one.Width);
            heights[at] = Figure(one.Unique, one.Height);
            heightsText[at] = Blank(one.Unique, one.Height);

            models[at] = Tail(one.Model);
            seconds[at] = Tail(one.Ao2);
            icons[at] = Tail(one.Icon);

            text.Clear();
            text.Append(one.Path).Append(' ').Append(one.Name).Append(' ').Append(one.Class)
                .Append(' ').Append(kinds[at]).Append(' ').Append(one.Art)
                .Append(' ').Append(one.Ao).Append(' ').Append(one.Ao2);
            find[at] = text.ToString().ToLowerInvariant();
        }

        (DataColumn Column, string Group, bool Shown)[] laid =
        [
            (DataColumn.Words("name", names), Named, true),
            (DataColumn.Labels("class", classes), Named, true),
            (DataColumn.Labels("kind", kinds), Named, true),
            (DataColumn.Words("art", arts), Named, false),
            (DataColumn.Magnitudes("drop", string.Empty, drops, dropsText), Figures, true),
            (DataColumn.Magnitudes("cells", string.Empty, cells, cellsText), Figures, true),
            (DataColumn.Magnitudes("width", string.Empty, widths, widthsText), Figures, false),
            (DataColumn.Magnitudes("height", string.Empty, heights, heightsText), Figures, false),
            (DataColumn.Words("model", models), Files, true),
            (DataColumn.Words("ao2", seconds), Files, false),
            (DataColumn.Words("icon", icons), Files, false),
        ];

        ColumnStore store = ColumnStore.Of([.. laid.Select(one => one.Column)]);

        var held = new Dictionary<string, Held>(StringComparer.Ordinal);
        var numbers = new Dictionary<string, int>(StringComparer.Ordinal);
        Index(store, held, numbers);

        return new ItemBook(
            store,
            paths,
            unique,
            find,
            rows,
            [.. laid.Select(one => one.Group)],
            [.. laid.Select(one => one.Shown)],
            held,
            numbers);
    }

    private static string Blank(bool unique, int value)
        => unique ? string.Empty : value.ToString(CultureInfo.InvariantCulture);

    private static double Figure(bool unique, int value) => unique ? double.NaN : value;
}
