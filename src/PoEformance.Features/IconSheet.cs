namespace PoEformance.Features;

/// <summary>
/// The one sprite sheet every marker and every status icon is cut from.
/// </summary>
/// <remarks>
/// ONE SHEET RATHER THAN A PATH PER SETTING, and the reason is what the path version actually
/// cost. An icon used to be a file somebody typed the way to, which made it the only setting
/// in the tool that could stop working after it was saved: the file gets moved, renamed, or
/// lost when a new build is unzipped over the old folder, and the symptom is a marker drawn
/// its ordinary way - which is exactly what NOT setting an icon looks like. A sheet that ships
/// inside the assembly cannot be separated from the code that draws it, survives single-file
/// publishing and AOT alike, and needs no path for anybody to type.
///
/// THE GRID WAS MEASURED, NOT ASSUMED. The sheet is 896x4928 with no gutters at all - the art
/// runs to the cell edges, so there are no transparent columns to read a grid off. What settles
/// it is the rows: every fully transparent row in the sheet lands on or beside a multiple of
/// 64 (448, 511-512, 574-575, 700-701, 1087, 1152, 1217, 1343...), and cutting at 64 gives 14
/// by 77 cells of which 1050 hold art and every one of them is self-contained, with content
/// measuring at most 64x64 and a median of 58x61. A guess of 32 or 128 fits the width just as
/// well and shreds every icon, so this is worth the paragraph.
///
/// <see cref="Tile"/> is the only fixed number here. The columns and rows are worked out from
/// whatever the loaded texture actually measures, so a sheet extended downwards in a later
/// release is picked up rather than clipped to a count frozen in this file.
/// </remarks>
public static class IconSheet
{
    /// <summary>
    /// The embedded sheet's file name, matched against the end of the resource name.
    /// </summary>
    public const string Resource = "icons.png";

    /// <summary>
    /// The table naming the sheet's cells, matched against the end of the resource name.
    /// </summary>
    public const string NameTable = "icon-names.tsv";

    /// <summary>
    /// The table naming the cells this project ADDED to the sheet, matched the same way.
    /// </summary>
    /// <remarks>
    /// A FILE OF ITS OWN because <see cref="NameTable"/> is generated: scripts/name-icon-cells.py
    /// rewrites it whole from the icons poe2db publishes, and a boss portrait rendered here is
    /// in none of those - every name written into that table by hand would be gone the next time
    /// the script ran. This one is written by tools/IconBaker and nothing else touches it.
    /// </remarks>
    public const string CustomNameTable = "icon-names-custom.tsv";

    /// <summary>One cell's edge, in sheet pixels. See the measurement in the type remarks.</summary>
    public const int Tile = 64;

    /// <summary>
    /// The first row that holds this project's art rather than the game's.
    /// </summary>
    /// <remarks>
    /// MEASURED, ON THE SHEET AS IT SHIPPED BEFORE ANYTHING WAS ADDED: 77 rows, 1050 cells of
    /// art, the last at cell 1059, and all 28 empty cells in the tail of row 77. Everything from
    /// row 78 on - cell 1079 at fourteen columns - is a picture made here, named in
    /// <see cref="CustomNameTable"/>.
    ///
    /// NEW ART STARTS ON A ROW OF ITS OWN, and the tail of row 77 stays empty on purpose. A
    /// style file stores a cell NUMBER, so the only safe way to add to the sheet is past its
    /// end; keeping the two kinds of art on separate rows also means the line between "the
    /// game's" and "ours" is a row number anybody can see, rather than a cell somewhere in the
    /// middle of a row that only a table knows about.
    /// </remarks>
    public const int OwnRow = 77;

    /// <summary>
    /// Tallest the sheet may grow, in pixels: the texture edge every D3D11 feature level
    /// guarantees.
    /// </summary>
    /// <remarks>
    /// A HARD LIMIT AND NOT A PREFERENCE. The sheet is uploaded as one texture, and the overlay
    /// refuses to shrink it because its grid is addressed in pixels (see IconCache.MaxSheetEdge).
    /// At 64 pixels a row that is 128 rows, 51 of them past <see cref="OwnRow"/> - 714 cells,
    /// or 357 boss pairs, against the hundred or so the endgame actually has.
    /// </remarks>
    public const int MaxEdge = 8192;

    /// <summary>Columns in a sheet of a given pixel width. Never zero - it is a divisor.</summary>
    public static int ColumnsIn(float sheetWidth) => Math.Max(1, (int)(sheetWidth / Tile));

    /// <summary>Rows in a sheet of a given pixel height. Never zero - it is a divisor.</summary>
    public static int RowsIn(float sheetHeight) => Math.Max(1, (int)(sheetHeight / Tile));

    /// <summary>How many cells a sheet of this size holds.</summary>
    public static int CountIn(float sheetWidth, float sheetHeight)
        => ColumnsIn(sheetWidth) * RowsIn(sheetHeight);

    /// <summary>
    /// The column and row a cell number lands on, wrapped to the sheet rather than trusted.
    /// </summary>
    /// <remarks>
    /// Clamped rather than allowed off the end, because the number is stored in a settings file
    /// and a sheet can get SHORTER between releases. Sampling past the edge draws whatever the
    /// sampler decides to repeat, which looks like a wrong icon rather than like a missing one
    /// and so gets blamed on the choice instead of on the sheet.
    /// </remarks>
    public static (int Column, int Row) CellAt(int index, float sheetWidth, float sheetHeight)
    {
        int columns = ColumnsIn(sheetWidth);
        int rows = RowsIn(sheetHeight);
        int safe = Math.Clamp(index, 0, (columns * rows) - 1);
        return (safe % columns, safe / columns);
    }

    /// <summary>The cell number a column and row make on a sheet of this size.</summary>
    public static int IndexOf(int column, int row, float sheetWidth, float sheetHeight)
    {
        int columns = ColumnsIn(sheetWidth);
        int rows = RowsIn(sheetHeight);
        return (Math.Clamp(row, 0, rows - 1) * columns) + Math.Clamp(column, 0, columns - 1);
    }
}
