using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;

namespace PoEformance.Overlay;

/// <summary>One line of the check's report, with the section it belongs to.</summary>
/// <param name="Number">Where it came in the report, so a sort can be undone.</param>
/// <param name="Section">The heading above it - ENDGAMEMAPS, NODE TOKENS.</param>
/// <param name="Text">The line itself, still indented as the producer wrote it.</param>
public readonly record struct AtlasLogLine(int Number, string Section, string Text);

/// <summary>
/// The atlas check's report, as a tab of its own with a table in it.
/// </summary>
/// <remarks>
/// IT OUTGREW THE BOX IT WAS IN. The report started at twenty-odd lines and was drawn in a nine-row
/// scrolling child under the filters, which was right for twenty lines. It is now eight hundred:
/// every EndgameMaps row that repeats, every content the file disagrees with, every effect id with
/// the game's name for it, every token on screen. Nine rows of eight hundred, in a box that cannot
/// be resized, with no way to jump to the part somebody came for, is a worse answer than no answer -
/// you cannot even tell whether the thing you are looking for is in there.
///
/// SO IT IS A TAB, and the last one: it is a diagnostic, not a setting, and nobody lands on it by
/// accident. A tab gets the whole window, which is the only thing that makes eight hundred lines
/// navigable at all.
///
/// AND A TABLE, because the report is already sectioned and nothing was using that. The producers
/// write a heading at column zero and indent everything under it, so the section a line belongs to
/// is derivable without any of them changing - and once each line carries its section, the ordinary
/// table machinery does the rest: filter to one section, search the text, sort, resize, copy.
///
/// THE FILTER IS THE FEATURE, not the sort. What somebody actually does here is "show me the node
/// tokens" or "find 25862", and both of those are one box away now instead of a scroll through
/// eight hundred lines at nine at a time.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class AtlasLogWindow(Func<IReadOnlyList<string>> report)
{
    /// <summary>Most rows drawn. Far above any report this produces; see Table for why there is one.</summary>
    private const int MostRows = 4096;

    /// <summary>Longest search anybody needs for a line of a diagnostic.</summary>
    private const int SearchLength = 64;

    /// <summary>What a section with no name of its own is called.</summary>
    private const string Preamble = "read";

    /// <summary>Every section, as the filter's first entry.</summary>
    private const string Everything = "(all)";

    private static readonly Vector4 DimText = OverlayInk.Quiet;

    private readonly Func<IReadOnlyList<string>> _report = report
        ?? throw new ArgumentNullException(nameof(report));

    private string _search = string.Empty;
    private string _section = Everything;

    // Split once per report rather than per frame: the report only changes when the button is
    // pressed, and eight hundred lines of string work sixty times a second for a tab somebody is
    // reading is exactly the sort of thing this project does not do.
    private IReadOnlyList<string> _raw = [];
    private List<AtlasLogLine> _lines = [];
    private List<string> _sections = [];

    /// <summary>
    /// Which heading a line sits under, by the shape the producers already write.
    /// </summary>
    /// <remarks>
    /// A LINE AT COLUMN ZERO OPENS A SECTION and everything indented belongs to it. That is not a
    /// convention invented here to be parsed - it is how WorldAreaCatalogue, EndgameMapCatalogue
    /// and EndgameMapContentCatalogue have always written their reports, because an indented
    /// sub-line is how a person reads a tree in a monospaced box.
    ///
    /// The name is the part before the first " - ", which is where those headings put the summary:
    /// "ENDGAMEMAPS - "Data/Balance/EndgameMaps.dat", 173 rows of 0xF1". Failing that the whole
    /// line, capped - a heading nobody recognises is still a divider.
    /// </remarks>
    public static List<AtlasLogLine> Split(IReadOnlyList<string> report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var lines = new List<AtlasLogLine>(report.Count);
        string section = Preamble;
        for (int i = 0; i < report.Count; i++)
        {
            string line = report[i];
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
            {
                int dash = line.IndexOf(" - ", StringComparison.Ordinal);
                section = dash > 0 ? line[..dash] : line;
                if (section.Length > 40)
                {
                    section = section[..40];
                }
            }

            lines.Add(new AtlasLogLine(i + 1, section, line));
        }

        return lines;
    }

    /// <summary>The report as a table: filter, search, sort.</summary>
    public void DrawTab()
    {
        IReadOnlyList<string> report = _report();
        if (report.Count == 0)
        {
            ImGui.TextColored(
                DimText,
                "Nothing yet. \"Check the Read\" on the Atlas page walks the tables and writes its"
                + " account here.");
            return;
        }

        if (!ReferenceEquals(report, _raw))
        {
            _raw = report;
            _lines = Split(report);
            _sections = [Everything, .. _lines.Select(line => line.Section).Distinct(StringComparer.Ordinal)];
            if (!_sections.Contains(_section, StringComparer.Ordinal))
            {
                _section = Everything;
            }
        }

        Controls();

        string find = _search.Trim();
        List<AtlasLogLine> rows =
        [
            .. _lines.Where(line =>
                (_section == Everything || string.Equals(line.Section, _section, StringComparison.Ordinal))
                && (find.Length == 0 || line.Text.Contains(find, StringComparison.OrdinalIgnoreCase))),
        ];

        ImGui.SameLine();
        ImGui.TextColored(DimText, $"{rows.Count} of {_lines.Count} lines");

        Table(rows);
    }

    /// <summary>The section picker, the search box and the copy button, on one line.</summary>
    private void Controls()
    {
        ImGui.SetNextItemWidth(220f);
        if (ImGui.BeginCombo("###atlas-log-section", _section))
        {
            foreach (string section in _sections)
            {
                if (ImGui.Selectable(section, section == _section))
                {
                    _section = section;
                }
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(260f);
        ImGui.InputTextWithHint("###atlas-log-find", "find in the text...", ref _search, SearchLength);

        // COPY THE WHOLE THING, not the filtered view: what this is for is pasting a report into a
        // conversation about it, and a report with the interesting part filtered out is worse than
        // no report. The filter is for reading here.
        ImGui.SameLine();
        if (ImGui.Button("Copy all"))
        {
            ImGui.SetClipboardText(string.Join('\n', _raw));
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"All {_lines.Count} lines to the clipboard, filter ignored.");
        }
    }

    /// <summary>The lines, in the mono face, sortable by any column.</summary>
    /// <remarks>
    /// MONO FOR THE SAME REASON THE BOX ALWAYS WAS: nearly every line is a figure or an address,
    /// and the producers step their sub-lines in with spaces because they are built two layers away
    /// from anything that could ask for an indent. In a proportional face those spaces are the
    /// narrowest glyph there is and the tree collapses.
    /// </remarks>
    private static void Table(List<AtlasLogLine> rows)
    {
        if (!ImGui.BeginTable(
                "##atlas-log",
                3,
                ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY
                    | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.Sortable))
        {
            return;
        }

        try
        {
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.DefaultSort);
            ImGui.TableSetupColumn("section");
            ImGui.TableSetupColumn("line", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            Sort(rows);

            OverlayFonts.PushMono();
            try
            {
                // CAPPED rather than clipped with ImGuiListClipper, which these bindings reach
                // through a raw pointer and a matching Destroy - a lifetime to get right in code
                // that cannot be run on this machine. See IconPicker, which made the same call.
                // The cap is well above any report this produces, and the filter is the real
                // navigation here anyway.
                foreach (AtlasLogLine line in rows.Take(MostRows))
                {
                    Row(line);
                }
            }
            finally
            {
                OverlayFonts.PopMono();
            }
        }
        finally
        {
            // In a finally and unconditionally: EndTable pairs with BeginTable whatever it
            // returned, and an exception between the two leaves ImGui's stack unbalanced.
            ImGui.EndTable();
        }

        if (rows.Count > MostRows)
        {
            ImGui.TextColored(OverlayInk.Warn, $"...and {rows.Count - MostRows} more - narrow the search");
        }
    }

    private static void Row(AtlasLogLine line)
    {
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        ImGui.TextColored(DimText, line.Number.ToString(System.Globalization.CultureInfo.InvariantCulture));

        ImGui.TableNextColumn();
        ImGui.TextColored(DimText, line.Section);

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(line.Text);
    }

    /// <summary>Re-orders in place when a column header has been clicked.</summary>
    /// <remarks>
    /// The line NUMBER is the default and the way back: a report is written in an order that means
    /// something, and a sort that cannot be undone would lose it.
    /// </remarks>
    private static unsafe void Sort(List<AtlasLogLine> rows)
    {
        // ImGui hands back a null pointer until the header row has been drawn and somebody has
        // chosen a column, so the wrapper cannot be trusted without checking the pointer it wraps.
        ImGuiTableSortSpecsPtr specs = ImGui.TableGetSortSpecs();
        if (specs.NativePtr == null || specs.SpecsCount == 0)
        {
            return;
        }

        ImGuiTableColumnSortSpecsPtr by = specs.Specs;
        bool up = by.SortDirection == ImGuiSortDirection.Ascending;

        rows.Sort((left, right) =>
        {
            int said = by.ColumnIndex switch
            {
                1 => string.Compare(left.Section, right.Section, StringComparison.OrdinalIgnoreCase),
                2 => string.Compare(left.Text, right.Text, StringComparison.OrdinalIgnoreCase),
                _ => left.Number.CompareTo(right.Number),
            };

            // Ties break on the line number, so a sort by section keeps each section in the order
            // it was written rather than in whatever order the comparison happened to leave it.
            return (up ? said : -said) is var order and not 0 ? order : left.Number.CompareTo(right.Number);
        });
    }
}
