namespace PoEformance.Features;

/// <summary>What a column's values ARE, which is what decides how they may be drawn.</summary>
public enum ColumnShape
{
    /// <summary>Words. Sorted as text, never encoded.</summary>
    Text,

    /// <summary>
    /// A quantity, where more of it means more of something. The only shape that earns a bar.
    /// </summary>
    Magnitude,

    /// <summary>
    /// A code that happens to be written as a number.
    /// </summary>
    /// <remarks>
    /// AttackCrit is the column that forced this to exist. It holds 0, 1 or 2 across the whole
    /// table - 98% of monsters are 0 - and it names a KIND rather than an amount. A bar on it
    /// would say that a 2 is twice a 1, which is not a thing the game means by it. So it is
    /// sorted and printed like any other number and never encoded, which is the same rule
    /// MonsterVariety's own remarks arrived at for the same column.
    /// </remarks>
    Kind,

    /// <summary>A row number into some other table. Sorted, printed as "#4211", never encoded.</summary>
    Row,
}

/// <summary>
/// One column, in the form the grid draws rather than the form the source holds.
/// </summary>
/// <remarks>
/// EVERY CELL'S TEXT IS FORMATTED ONCE, HERE, and that is most of why this type exists. Drawing
/// a row means handing ImGui a string per cell; built in the draw loop that is an allocation per
/// cell per frame, sixty times a second, for as many rows as were submitted. The window this
/// replaces submitted fifteen hundred rows to show forty, so it formatted some seven thousand
/// strings a frame - a third of a million a second - and threw all but a hundred of them away.
/// Pre-formatted, the draw loop reads an array and allocates nothing.
///
/// THE ARRAYS ARE EXPOSED RATHER THAN WRAPPED, for the same reason: the grid indexes them once
/// per visible cell and an accessor per cell is work that buys nothing here. They are built once
/// and never written again.
/// </remarks>
public sealed class DataColumn
{
    private static readonly double[] NoNumbers = [];
    private static readonly float[] NoBars = [];
    private static readonly int[] NoOrder = [];

    /// <summary>Dense rank of each row's text, so sorting words compares ints.</summary>
    /// <remarks>
    /// DENSE, so that equal text really is equal. Ranked by position instead, the nineteen
    /// monsters called "Skeletal Warrior" would each get a different rank and the grid's own
    /// tie-break - which puts them in a fixed, meaningful order - would never run.
    /// </remarks>
    private readonly int[] _order;

    private DataColumn(
        string name,
        ColumnShape shape,
        string unit,
        string[] text,
        double[] number,
        float[] bar,
        int[] order,
        ColumnSpread spread)
    {
        Name = name;
        Shape = shape;
        Unit = unit;
        Text = text;
        Number = number;
        Bar = bar;
        Spread = spread;
        _order = order;

        // THE LONGEST CELL, WORKED OUT NOW, because of what clipping costs a table that sizes its
        // columns to fit: only the rows on screen are ever submitted, so a column auto-fitted to
        // them is a column fitted to forty rows of two thousand - and the first "2600%" further
        // down arrives in a cell too narrow to hold it.
        foreach (string one in text)
        {
            if (one.Length > Widest.Length)
            {
                Widest = one;
            }
        }
    }

    /// <summary>What the header says.</summary>
    public string Name { get; }

    /// <summary>What kind of value this holds - see <see cref="ColumnShape"/>.</summary>
    public ColumnShape Shape { get; }

    /// <summary>The unit, where the data settles one, and empty where it does not.</summary>
    /// <remarks>
    /// EMPTY IS THE COMMON CASE AND THE HONEST ONE. Life, damage, experience and model size sit
    /// around 100 across 2733 rows, so they are percentages; attack speed, movement speed and the
    /// aggro ranges plainly are not, and naming a unit this table cannot prove is how a display
    /// ends up confidently wrong. MonsterVariety's remarks settle which is which.
    /// </remarks>
    public string Unit { get; }

    /// <summary>What each row prints, formatted once.</summary>
    public string[] Text { get; }

    /// <summary>What each row is worth, for sorting and for the bar. Empty on a text column.</summary>
    public double[] Number { get; }

    /// <summary>How much of the bar each row earned. Empty unless this column is encoded.</summary>
    public float[] Bar { get; }

    /// <summary>How this column is spread, which is where the bar's scale came from.</summary>
    public ColumnSpread Spread { get; }

    /// <summary>How many rows it holds.</summary>
    public int Rows => Text.Length;

    /// <summary>Whether this column is drawn with a bar behind its numbers.</summary>
    public bool Encoded => Bar.Length > 0;

    /// <summary>The longest cell in the column, which is what it has to be wide enough for.</summary>
    public string Widest { get; } = string.Empty;

    /// <summary>A column of words.</summary>
    public static DataColumn Words(string name, string[] text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new DataColumn(
            name, ColumnShape.Text, string.Empty, text, NoNumbers, NoBars, Rank(text), ColumnSpread.Empty);
    }

    /// <summary>A column of quantities, which earns a bar if it is spread at all.</summary>
    public static DataColumn Magnitudes(string name, string unit, double[] number, string[] text)
    {
        ArgumentNullException.ThrowIfNull(number);
        ArgumentNullException.ThrowIfNull(text);
        Same(number.Length, text.Length, name);

        ColumnSpread spread = ColumnSpread.Of(number);
        float[] bar = NoBars;

        if (spread.Scale > 0d)
        {
            bar = new float[number.Length];
            for (var row = 0; row < number.Length; row++)
            {
                bar[row] = spread.Bar(number[row]);
            }
        }

        return new DataColumn(name, ColumnShape.Magnitude, unit, text, number, bar, NoOrder, spread);
    }

    /// <summary>A column of codes that are written as numbers but are not amounts.</summary>
    public static DataColumn Codes(string name, double[] number, string[] text)
        => Plain(name, ColumnShape.Kind, number, text);

    /// <summary>A column of row numbers into some other table.</summary>
    public static DataColumn RowNumbers(string name, double[] number, string[] text)
        => Plain(name, ColumnShape.Row, number, text);

    /// <summary>How two rows of this column order against each other, ascending.</summary>
    public int Compare(int left, int right)
        => _order.Length > 0
            ? _order[left].CompareTo(_order[right])
            : Number.Length > 0 ? Number[left].CompareTo(Number[right]) : 0;

    private static DataColumn Plain(string name, ColumnShape shape, double[] number, string[] text)
    {
        ArgumentNullException.ThrowIfNull(number);
        ArgumentNullException.ThrowIfNull(text);
        Same(number.Length, text.Length, name);
        return new DataColumn(name, shape, string.Empty, text, number, NoBars, NoOrder, ColumnSpread.Empty);
    }

    private static void Same(int numbers, int strings, string name)
    {
        if (numbers != strings)
        {
            throw new ArgumentException($"Column '{name}' has {numbers} numbers and {strings} strings.");
        }
    }

    private static int[] Rank(string[] text)
    {
        var order = new int[text.Length];
        var by = new int[text.Length];
        for (var row = 0; row < by.Length; row++)
        {
            by[row] = row;
        }

        Array.Sort(
            by, (left, right) => string.Compare(text[left], text[right], StringComparison.OrdinalIgnoreCase));

        var rank = 0;
        for (var at = 0; at < by.Length; at++)
        {
            if (at > 0 && !string.Equals(text[by[at - 1]], text[by[at]], StringComparison.OrdinalIgnoreCase))
            {
                rank++;
            }

            order[by[at]] = rank;
        }

        return order;
    }
}

/// <summary>
/// A table of rows held as columns, built once, drawn many times.
/// </summary>
/// <remarks>
/// COLUMNS AND NOT ROWS, because of what the grid does with it: it draws one column's worth of a
/// row at a time, it sorts on one column, and it scales a bar against one column's own spread.
/// Every one of those is a walk of a single array here, and would be a walk of every record with
/// the rest of each record dragged through the cache the other way round.
/// </remarks>
public sealed class ColumnStore
{
    private ColumnStore(int rows, DataColumn[] columns)
    {
        Rows = rows;
        Columns = columns;
    }

    /// <summary>A store with nothing in it.</summary>
    public static ColumnStore Empty { get; } = new(0, []);

    /// <summary>How many rows every column holds.</summary>
    public int Rows { get; }

    /// <summary>The columns, in the order they are drawn.</summary>
    public DataColumn[] Columns { get; }

    /// <summary>
    /// Gathers columns into a table, which they have to agree about the height of.
    /// </summary>
    /// <remarks>
    /// THROWS, unlike most of what this project reads. A column of a different length is not a
    /// patch that has moved something or a file somebody has not got - it is two arrays built
    /// from the same loop that did not come out the same length, which is a bug in the caller
    /// and wants to be loud.
    /// </remarks>
    public static ColumnStore Of(params DataColumn[] columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        if (columns.Length == 0)
        {
            return Empty;
        }

        int rows = columns[0].Rows;
        foreach (DataColumn column in columns)
        {
            if (column.Rows != rows)
            {
                throw new ArgumentException(
                    $"Column '{column.Name}' has {column.Rows} rows where '{columns[0].Name}' has {rows}.",
                    nameof(columns));
            }
        }

        return new ColumnStore(rows, columns);
    }
}
