using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;

namespace PoEformance.Overlay;

/// <summary>
/// Draws a <see cref="ColumnStore"/>: only the rows on screen, with the numbers encoded.
/// </summary>
/// <remarks>
/// CLIPPED BY HAND, WHICH IS WHY THERE IS NO ROW CAP. The window this came out of drew at most
/// 1500 rows and told anybody with more to narrow their search - a cap that is not only a limit
/// but a place where the answer can be off screen while the screen looks complete. Rows are a
/// fixed height, so which of them the scroll position exposes is arithmetic, and an empty box
/// stands in for the ones above and below so the scrollbar still measures the whole table. What
/// it costs is what is visible rather than what exists, and it stays that way if the table
/// doubles.
///
/// Not ImGuiListClipper, the same call <see cref="IconPicker"/> and AtlasLogWindow made: in these
/// bindings it is reached through a raw pointer with a matching Destroy, which is a lifetime to
/// get right in code that cannot be run on the machine it is written on, and it buys nothing over
/// arithmetic on a fixed row height.
///
/// THE BAR BEHIND A NUMBER IS NOT DECORATION. A column of figures answers "how much" and says
/// nothing about "is that a lot", which is the question somebody scrolling a table of thousands
/// actually has - and the answer is in the column itself. <see cref="ColumnSpread"/> works out the
/// scale and records why it is the ninetieth percentile rather than the maximum or the rank.
///
/// LENGTH IS THE ONLY THING THE BAR SAYS. Colour here means boss, or selected, or over the scale -
/// categories, never amounts. Two encodings for one meaning is how a dense table stops being
/// readable at a glance, because the reader has to learn which of them to believe.
/// </remarks>
/// <param name="Ink">A colour for a row's first cell, where it has one.</param>
/// <param name="Hover">What a tooltip on a row says. Only asked for the row under the cursor.</param>
/// <param name="Ranged">A drag across a column's histogram, as the values it picked out.</param>
/// <param name="Cleared">A right-click on a histogram, which throws that column's range away.</param>
public sealed record DataGridHooks(
    Func<int, Vector4?>? Ink = null,
    Func<int, string>? Hover = null,
    Action<int, double, double>? Ranged = null,
    Action<int>? Cleared = null);

[SupportedOSPlatform("windows")]
public sealed class DataGrid
{
    private const ImGuiTableFlags Flags =
        ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY
        | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.Sortable
        | ImGuiTableFlags.SortMulti;

    /// <summary>How wide the mark on a bar that has run past its scale is.</summary>
    private const float Mark = 2f;

    /// <summary>How tall the histogram under a column's name is, in multiples of the text height.</summary>
    private const float PlotLines = 0.9f;

    /// <summary>How many columns a sort may be made of.</summary>
    /// <remarks>
    /// FOUR, which is more than anybody holds in their head at once and is bounded so that the
    /// comparison can read out of two fixed arrays rather than out of ImGui's own memory - a sort
    /// comparison runs thousands of times and has no business touching the UI library at all.
    /// </remarks>
    private const int MostSorts = 4;

    private readonly int[] _by = new int[MostSorts];
    private readonly bool[] _up = new bool[MostSorts];
    private readonly Comparison<int> _compare;

    private ColumnStore _store = ColumnStore.Empty;
    private int _sorts;
    private int _first;

    /// <summary>The list has to be put back in order - it was just refiltered or rebuilt.</summary>
    private bool _resort = true;

    public DataGrid() => _compare = Compare;

    /// <summary>Says the row list has changed underneath, so the next draw re-sorts it.</summary>
    public void Resort() => _resort = true;

    /// <summary>
    /// Draws the rows named in <paramref name="rows"/> and returns what is selected after it.
    /// </summary>
    /// <param name="id">The table's ImGui id.</param>
    /// <param name="store">Every column there is.</param>
    /// <param name="columns">Which of them to draw, in order. The first one carries the selection.</param>
    /// <param name="rows">Which rows, in which order. SORTED IN PLACE when a header is clicked.</param>
    /// <param name="chosen">The selected row, or -1. Returned unchanged when nothing is clicked.</param>
    /// <param name="ranges">The ranges in force, so the histograms can show what they picked.</param>
    /// <param name="hooks">What the grid cannot work out for itself.</param>
    public int Draw(
        string id,
        ColumnStore store,
        IReadOnlyList<int> columns,
        List<int> rows,
        int chosen,
        IReadOnlyList<ColumnRange> ranges,
        DataGridHooks? hooks = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(ranges);

        if (columns.Count == 0 || store.Columns.Length == 0)
        {
            return chosen;
        }

        if (!ImGui.BeginTable(id, columns.Count, Flags))
        {
            return chosen;
        }

        try
        {
            for (var at = 0; at < columns.Count; at++)
            {
                DataColumn column = store.Columns[columns[at]];

                // PROSE STRETCHES AND EVERYTHING ELSE DOES NOT: a name is as long as it is, while a
                // column of figures, or of one short word out of a handful, wants to be exactly
                // wide enough for the widest of them and no wider.
                bool stretch = column.Shape == ColumnShape.Text;
                ImGuiTableColumnFlags flags = stretch
                    ? ImGuiTableColumnFlags.WidthStretch
                    : ImGuiTableColumnFlags.WidthFixed;

                if (at == 0)
                {
                    flags |= ImGuiTableColumnFlags.DefaultSort;
                }

                // ASKED FOR RATHER THAN FITTED, and this is the price of clipping: a table only
                // ever sees the rows on screen, so left to fit its own content a column is sized to
                // forty rows of two thousand. The width is the widest cell the COLUMN holds, which
                // the store worked out when it was built, or the header and its sort arrow.
                if (stretch)
                {
                    ImGui.TableSetupColumn(column.Name, flags);
                }
                else
                {
                    float wide = Math.Max(
                        ImGui.CalcTextSize(column.Widest).X,
                        ImGui.CalcTextSize(column.Name).X + ImGui.GetFontSize());

                    ImGui.TableSetupColumn(column.Name, flags, wide + (ImGui.GetStyle().CellPadding.X * 2f));
                }
            }

            ImGui.TableSetupScrollFreeze(0, 1);
            Headers(store, columns, ranges, hooks);

            // AFTER THE HEADER ROW, because ImGui has no sort specs to offer until it has been
            // drawn once - and before the body, so the first frame of a new list is in order
            // rather than in whatever order the dictionary handed it over in.
            Sort(store, columns, rows);

            return Body(store, columns, rows, chosen, hooks);
        }
        finally
        {
            // In a finally and unconditionally: EndTable pairs with BeginTable whatever it
            // returned, and an exception between the two leaves ImGui's stack unbalanced.
            ImGui.EndTable();
        }
    }

    /// <summary>
    /// The header row: each column's name, and under it the shape of what is in it.
    /// </summary>
    /// <remarks>
    /// DRAWN OUT RATHER THAN TableHeadersRow, which draws the names and nothing else. What the
    /// histogram adds is the question a column of figures cannot answer on its own - not "how much
    /// is this one" but "how much is there, and where does this sit in it" - for one line of
    /// vertical space across the whole table.
    ///
    /// AND IT IS THE FILTER. Dragging across it picks a range of values out, which is the one
    /// interaction here somebody finds by accident rather than by being told; the bins it picked
    /// stay lit afterwards, so the header says what the table is currently hiding.
    /// </remarks>
    private static void Headers(
        ColumnStore store, IReadOnlyList<int> columns, IReadOnlyList<ColumnRange> ranges, DataGridHooks? hooks)
    {
        float plot = ImGui.GetTextLineHeight() * PlotLines;
        ImGui.TableNextRow(
            ImGuiTableRowFlags.Headers,
            ImGui.GetTextLineHeight() + plot + (ImGui.GetStyle().CellPadding.Y * 2f));

        for (var at = 0; at < columns.Count; at++)
        {
            ImGui.TableNextColumn();
            int index = columns[at];

            // BY STORE COLUMN AND NOT BY POSITION: hiding a column shifts every position after it,
            // and an id built from the position would hand one column's drag state to another.
            ImGui.PushID(index);

            try
            {
                ImGui.TableHeader(store.Columns[index].Name);
                Histogram(store.Columns[index], index, ranges, hooks, plot);
            }
            finally
            {
                ImGui.PopID();
            }
        }
    }

    /// <summary>The column's distribution, drawn small, and the drag that filters by it.</summary>
    private static void Histogram(
        DataColumn column, int index, IReadOnlyList<ColumnRange> ranges, DataGridHooks? hooks, float plot)
    {
        ColumnSpread spread = column.Spread;
        float room = ImGui.GetContentRegionAvail().X;

        if (spread.Bins.Length == 0 || spread.Tallest <= 0 || room < 4f)
        {
            ImGui.Dummy(new Vector2(1f, plot));
            return;
        }

        ImGui.InvisibleButton("##plot", new Vector2(room, plot));

        Vector2 at = ImGui.GetItemRectMin();
        Vector2 size = ImGui.GetItemRectSize();

        // WHILE THE DRAG IS HAPPENING AND NOT WHEN IT ENDS, so the count under the table moves with
        // the cursor. Filtering 2733 rows is a scan of strings that are already built; done once a
        // frame during a drag it costs less than the frame it is drawn in.
        bool dragging = ImGui.IsItemActive() && size.X > 0f;
        if (dragging && hooks?.Ranged is { } ranged)
        {
            float from = (ImGui.GetIO().MouseClickedPos[0].X - at.X) / size.X;
            float to = (ImGui.GetMousePos().X - at.X) / size.X;
            (double least, double most) = spread.Range(from, to);
            ranged(index, least, most);
        }

        // A RIGHT-CLICK THROWS IT AWAY, because the only other way out of a range somebody set by
        // accident is to drag the whole width back, and that is not obvious either.
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            hooks?.Cleared?.Invoke(index);
        }

        (double from2, double to2) = Picked(ranges, index);

        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        uint dim = ImGui.GetColorU32(OverlayInk.Chrome);
        uint lit = ImGui.GetColorU32(OverlayInk.Accent);
        float step = size.X / spread.Bins.Length;

        for (var bin = 0; bin < spread.Bins.Length; bin++)
        {
            float tall = (float)spread.Bins[bin] / spread.Tallest;
            float left = at.X + (bin * step);

            // AT LEAST A PIXEL TALL FOR A BIN THAT HOLDS ANYTHING. A bar rounded away to nothing
            // says the same as an empty bin, and "no monster is like this" is a different fact
            // from "three are".
            float high = Math.Max(spread.Bins[bin] > 0 ? 1f : 0f, size.Y * tall);

            draw.AddRectFilled(
                new Vector2(left, at.Y + size.Y - high),
                new Vector2(left + Math.Max(1f, step - 1f), at.Y + size.Y),
                spread.Inside(bin, from2, to2) ? lit : dim);
        }
    }

    /// <summary>The range in force on a column, or one that takes everything.</summary>
    private static (double Least, double Most) Picked(IReadOnlyList<ColumnRange> ranges, int column)
    {
        for (var at = 0; at < ranges.Count; at++)
        {
            if (ranges[at].Column == column)
            {
                return (ranges[at].Least, ranges[at].Most);
            }
        }

        return (double.NegativeInfinity, double.NegativeInfinity);
    }

    private static int Body(
        ColumnStore store,
        IReadOnlyList<int> columns,
        List<int> rows,
        int chosen,
        DataGridHooks? hooks)
    {
        // THE ROW HEIGHT ImGui ACTUALLY LAYS OUT, which is not GetTextLineHeightWithSpacing: a table
        // row is one line of text with the cell padding above and below it, while that call adds
        // ItemSpacing instead. This theme sets CellPadding.Y to 3 and ItemSpacing.Y to 5, so the two
        // differ by a pixel a row - and a spacer built from the wrong one would not line up with the
        // rows it stands in for, which is a scrollbar that measures the wrong table.
        float step = ImGui.GetTextLineHeight() + (ImGui.GetStyle().CellPadding.Y * 2f);
        float scroll = ImGui.GetScrollY();
        float room = ImGui.GetWindowHeight();

        // ONE ROW OF SLACK EITHER SIDE, so that a row half off the edge is drawn rather than
        // popping in. Clamped to the list because it can shrink under a scroll position that has
        // not caught up yet - ImGui fixes that on the next frame, and until then first may be
        // past the end.
        int first = Math.Clamp((int)(scroll / step) - 1, 0, rows.Count);
        int last = Math.Clamp((int)((scroll + room) / step) + 2, first, rows.Count);

        Spacer(first, step);

        uint fill = ImGui.GetColorU32(OverlayInk.Chrome);
        uint over = ImGui.GetColorU32(OverlayInk.Accent);

        for (int at = first; at < last; at++)
        {
            int row = rows[at];

            ImGui.TableNextRow();
            ImGui.TableNextColumn();

            // PUSHED BY ROW NUMBER rather than built into the label, because ImGui keys a
            // selectable on its label and a shared name would make several rows one item. That is
            // not a rare case here: over the shipped export, 2225 of the 2709 named rows share a
            // name with at least one other, 510 names occur more than once, and "Daemon" is on 305
            // rows. It also means no string is built per row per frame, which is the store's point.
            ImGui.PushID(row);

            try
            {
                Vector4? tint = hooks?.Ink?.Invoke(row);
                if (tint is { } colour)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, colour);
                }

                if (ImGui.Selectable(
                        store.Columns[columns[0]].Text[row],
                        row == chosen,
                        ImGuiSelectableFlags.SpanAllColumns))
                {
                    chosen = row;
                }

                if (tint is not null)
                {
                    ImGui.PopStyleColor();
                }

                if (hooks?.Hover is { } hover && ImGui.IsItemHovered())
                {
                    Tip(hover(row));
                }

                for (var column = 1; column < columns.Count; column++)
                {
                    ImGui.TableNextColumn();
                    Cell(store.Columns[columns[column]], row, fill, over);
                }
            }
            finally
            {
                ImGui.PopID();
            }
        }

        // The rows below, which only have to add up to the right height - nothing is drawn past the
        // viewport, so their own striping is never seen and their count does not matter.
        if (last < rows.Count)
        {
            ImGui.TableNextRow(ImGuiTableRowFlags.None, (rows.Count - last) * step);
        }

        return chosen;
    }

    /// <summary>
    /// Stands in for the rows above the viewport, in a row count that keeps the stripes still.
    /// </summary>
    /// <remarks>
    /// ONE SPACER ROW OR TWO, AND WHICH IT IS MATTERS. ImGui stripes a table by how many rows have
    /// been submitted to it, not by which row of the caller's list is being drawn - so a spacer
    /// that is always one row flips every visible row's stripe each time the first visible row
    /// changes, and the stripes then sit still on the screen while the content scrolls through
    /// them. Matching the spacer's row count to the parity of what it stands in for gives each row
    /// back its own stripe.
    ///
    /// ASKED FOR AS A ROW HEIGHT rather than filled with a Dummy, because a Dummy is CONTENT and a
    /// row is padded around its content: a spacer built that way is two cell paddings taller than
    /// the rows it replaces, every time, and the list creeps down the page as it is scrolled.
    ///
    /// Both of those are reasoned from how ImGui lays a table out rather than watched happening -
    /// there is no client on this machine to watch them on. Each is worth a few pixels, and this
    /// method is the whole of either fix.
    /// </remarks>
    private static void Spacer(int rows, float step)
    {
        if (rows <= 0)
        {
            return;
        }

        int lines = (rows & 1) == 1 ? 1 : 2;
        float each = rows * step / lines;

        for (var at = 0; at < lines; at++)
        {
            ImGui.TableNextRow(ImGuiTableRowFlags.None, each);
        }
    }

    /// <summary>One cell: the bar it earned, then the number, right-aligned against it.</summary>
    /// <remarks>
    /// RIGHT-ALIGNED, and not as a nicety: a column of figures is compared digit by digit from the
    /// right, and ragged ones have to be read one at a time. It also puts the number at the far
    /// end of its own bar, so the two are read as one thing rather than as a picture beside a
    /// figure.
    ///
    /// TextUnformatted AND NOT Text, which is load-bearing here: these strings come from the
    /// game's own tables and ImGui's Text calls are printf. A percent sign in a value would eat
    /// the characters behind it. See ImGuiText.
    /// </remarks>
    private static void Cell(DataColumn column, int row, uint fill, uint over)
    {
        string text = column.Text[row];

        if (!column.Encoded)
        {
            ImGui.TextUnformatted(text);
            return;
        }

        float bar = column.Bar[row];
        float room = ImGui.GetContentRegionAvail().X;

        if (bar > 0f && room > 0f)
        {
            Vector2 at = ImGui.GetCursorScreenPos();
            float height = ImGui.GetTextLineHeight();
            ImDrawListPtr draw = ImGui.GetWindowDrawList();

            draw.AddRectFilled(at, new Vector2(at.X + (room * bar), at.Y + height), fill);

            // THE ONE THING A CLAMPED BAR HAS TO SAY IS THAT IT IS CLAMPED. Eight percent of the
            // life column is above the scale and they are the rows worth noticing; without this
            // they are drawn exactly like the ninetieth percentile and look ordinary.
            if (column.Number[row] > column.Spread.Scale)
            {
                draw.AddRectFilled(
                    new Vector2(at.X + room - Mark, at.Y), new Vector2(at.X + room, at.Y + height), over);
            }
        }

        float width = ImGui.CalcTextSize(text).X;
        if (width < room)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (room - width));
        }

        ImGui.TextUnformatted(text);
    }

    private static void Tip(string text)
    {
        if (text.Length == 0 || !ImGui.BeginTooltip())
        {
            return;
        }

        try
        {
            // Built out rather than SetTooltip for the reason ImGuiText.MonoTooltip gives:
            // SetTooltip is printf, and a path or a name may carry a percent sign.
            ImGui.TextUnformatted(text);
        }
        finally
        {
            ImGui.EndTooltip();
        }
    }

    /// <summary>
    /// Re-orders the rows when a column header has been clicked, or the list has just changed.
    /// </summary>
    /// <remarks>
    /// ONLY WHEN ImGui SAYS SO, or when the caller has said the list is new. Sorting a list that is
    /// already sorted, sixty times a second, is the kind of cost that never shows up in a profile
    /// as one thing and is free to avoid.
    /// </remarks>
    private unsafe void Sort(ColumnStore store, IReadOnlyList<int> columns, List<int> rows)
    {
        // ImGui hands back a null pointer until the header row has been drawn and somebody has
        // chosen a column, so the wrapper cannot be trusted without checking the pointer it wraps.
        ImGuiTableSortSpecsPtr specs = ImGui.TableGetSortSpecs();
        if (specs.NativePtr == null || specs.SpecsCount == 0)
        {
            return;
        }

        if (!specs.SpecsDirty && !_resort)
        {
            return;
        }

        specs.SpecsDirty = false;
        _resort = false;

        // COPIED OUT OF ImGui BEFORE THE SORT, not read during it. The comparison runs tens of
        // thousands of times and has no business reaching into the UI library, and the specs are a
        // pointer into memory ImGui owns and rewrites.
        _store = store;
        _sorts = Math.Min(specs.SpecsCount, MostSorts);
        _first = columns[0];

        for (var at = 0; at < _sorts; at++)
        {
            ImGuiTableColumnSortSpecs one = specs.NativePtr->Specs[at];

            // THE SPEC NAMES A POSITION IN THE TABLE, and the table is whatever subset of columns
            // is showing - so it has to be mapped back to the column the store knows. Without this,
            // hiding a column silently re-points every sort after it.
            _by[at] = columns[Math.Clamp(one.ColumnIndex, 0, columns.Count - 1)];
            _up[at] = one.SortDirection == ImGuiSortDirection.Ascending;
        }

        rows.Sort(_compare);
    }

    /// <summary>
    /// Two rows, against every column the sort is made of and then against the ones it is not.
    /// </summary>
    /// <remarks>
    /// TIES BREAK THE SAME WAY WHICHEVER WAY THE SORT RUNS - on the leading column and then on the
    /// row number, which is unique. Sorting by type then puts each type's monsters in one fixed
    /// order rather than in whatever order the comparison happened to leave them, and a list that
    /// reshuffles its ties every time it is re-sorted is a list nobody can keep their place in.
    /// </remarks>
    private int Compare(int left, int right)
    {
        for (var at = 0; at < _sorts; at++)
        {
            int said = _store.Columns[_by[at]].Compare(left, right);
            if (said != 0)
            {
                return _up[at] ? said : -said;
            }
        }

        int last = _store.Columns[_first].Compare(left, right);
        return last != 0 ? last : left.CompareTo(right);
    }
}
