using System.Numerics;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.World;

namespace PoEformance.Overlay;

/// <summary>
/// What the tool reads out of the game beside what it ships in a file, put where a person can
/// check it.
/// </summary>
/// <remarks>
/// WHY THIS IS AN INTERFACE AND NOT A LOG LINE. Moving the atlas onto values read from memory
/// replaces things anybody can open in an editor with things nobody can see. The owner asked for
/// the replacement to stay visible, and that is the whole job here: the reconciliation at the top
/// answers "would anything break", the catalogue below it answers "what does the game actually
/// say", and the button writes both somewhere a spreadsheet can sort them.
///
/// IT OWNS NO READING. <see cref="MapDataReport"/> is built on the reader thread and published
/// whole; this draws a finished, immutable value. Filtering happens here because a filter is a
/// question about what to show, not about what to read.
/// </remarks>
public sealed class MapDataWindow
{
    /// <summary>Rows drawn before the list is cut short. The union is 442 rows.</summary>
    private const int MostRows = 2048;

    /// <summary>Longest search text taken. A map id is well under this.</summary>
    private const int SearchLength = 64;

    private static readonly Vector4 DimText = OverlayInk.Quiet;
    private static readonly Vector4 GoodText = OverlayInk.Good;
    private static readonly Vector4 BadText = OverlayInk.Bad;
    private static readonly Vector4 WarnText = OverlayInk.Warn;

    private readonly Func<MapDataReport> _report;
    private readonly Func<string, string> _save;

    private string _search = string.Empty;
    private bool _differencesOnly;
    private bool _atlasOnly;
    private string _saved = string.Empty;

    /// <param name="report">The published report, read fresh each frame.</param>
    /// <param name="save">
    /// Writes the text somewhere and returns where. Injected rather than called directly so this
    /// class stays testable and so the app decides where files land.
    /// </param>
    public MapDataWindow(Func<MapDataReport> report, Func<string, string> save)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(save);
        _report = report;
        _save = save;
    }

    /// <summary>The whole tab.</summary>
    public void DrawTab()
    {
        MapDataReport report = _report();
        if (!report.Anything)
        {
            ImGui.TextColored(WarnText, "nothing read yet");
            ImGuiText.Wrapped(
                DimText,
                "The WorldAreas table is reached through an atlas node, so this fills in once the"
                + " atlas has been open. Press Check on the Atlas tab to force it.");
            ImGui.TextColored(DimText, report.Source);
            return;
        }

        ImGui.TextColored(DimText, report.Source);
        Reconciliation(report);

        ImGui.Separator();
        if (ImGui.CollapsingHeader($"Catalogue - {report.Maps.Count} maps###map-data-catalogue"))
        {
            Catalogue(report);
        }

        ImGui.Separator();
        Export(report);
    }

    /// <summary>The part that answers "would anything break".</summary>
    /// <remarks>
    /// The ratings first, because they are the only thing here that can fail SILENTLY: a rating is
    /// written by display name and resolved to ids through data/atlas-maps.json, so a line that
    /// stops resolving simply never colours a node again and nobody is told.
    /// </remarks>
    private static void Reconciliation(MapDataReport report)
    {
        ImGui.TextColored(DimText, $"{report.InGame} areas in the game, {report.InFile} in the file");

        // THE LINE THAT NAMES THE OLDEST MISTAKE HERE. data/atlas-maps.json is a copy of
        // WorldAreas, which is every AREA in the game - so most of what it calls an atlas map is a
        // hideout, a campaign zone, a Sanctum floor or the login screen. EndgameMaps is the table
        // that knows the difference, and until it has been read this says so rather than guessing.
        ImGui.TextColored(
            report.AtlasMaps == 0 ? WarnText : GoodText,
            report.AtlasMaps == 0
                ? "  ..  EndgameMaps has not been read - nothing here knows which areas are atlas maps"
                : $"  OK  {report.AtlasMaps} of them are ATLAS maps (EndgameMaps.dat);"
                  + $" {report.NotAtlasMaps} file entries are not");

        Verdict(
            report.RatingsUnresolved == 0,
            $"all {report.Ratings.Count} ratings resolve to a map",
            $"{report.RatingsUnresolved} of {report.Ratings.Count} ratings resolve to NOTHING");

        Verdict(
            report.RatingsNeedingTheFile == 0,
            "every rated name is one the game supplies too - the file's name column is not load-bearing",
            $"{report.RatingsNeedingTheFile} ratings hang on a name only the file has");

        Verdict(
            report.NamesDiffer == 0,
            "every name the two share is identical",
            $"{report.NamesDiffer} names differ between the game and the file");

        // NOT a verdict either way: there is nothing left to compare. The file's "type": "unique"
        // key is gone, so this says what is true and whether the table it comes from has been read.
        ImGui.TextColored(
            report.UniqueFromGame ? GoodText : WarnText,
            report.UniqueFromGame
                ? $"  OK  {report.Uniques} unique maps, from the game (WorldAreas.IsUniqueMapArea)"
                : "  ..  WorldAreas has not been read - nothing is known to be unique yet");

        foreach (RatingRow rating in report.Ratings.Where(r => !r.Resolves))
        {
            ImGui.TextColored(BadText, $"      \"{rating.Name}\" ({rating.Rating}) matches no map");
        }

    }

    /// <summary>One line, green when it holds and red when it does not.</summary>
    private static void Verdict(bool good, string yes, string no)
        => ImGui.TextColored(good ? GoodText : BadText, good ? $"  OK  {yes}" : $"  !!  {no}");

    /// <summary>Every map, both sources side by side.</summary>
    private void Catalogue(MapDataReport report)
    {
        ImGui.SetNextItemWidth(220f);
        ImGui.InputTextWithHint("###map-data-find", "find by id or name...", ref _search, SearchLength);
        ImGui.SameLine();
        ImGui.Checkbox("differences only", ref _differencesOnly);
        ImGui.SameLine();
        ImGui.Checkbox("atlas maps only", ref _atlasOnly);

        string find = _search.Trim();
        List<MapDataRow> rows = [.. report.Maps.Where(row => Shows(row, find))];
        ImGui.TextColored(DimText, $"{rows.Count} shown");

        if (!ImGui.BeginTable(
                "##map-data-rows",
                8,
                ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY
                    | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable))
        {
            return;
        }

        try
        {
            ImGui.TableSetupColumn("atlas");
            ImGui.TableSetupColumn("id", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("name (game)", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("name (file)", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("uniq");
            ImGui.TableSetupColumn("rating");
            ImGui.TableSetupColumn("tags (game)", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("tags (file)", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            foreach (MapDataRow row in rows.Take(MostRows))
            {
                ImGui.TableNextRow();

                // The atlas column FIRST, because it is the one that says whether the rest of the
                // row is about the atlas at all. A hideout's name agreeing with the file is true
                // and beside the point.
                ImGui.TableNextColumn();
                ImGui.TextColored(row.AtlasMap ? GoodText : DimText, row.AtlasMap ? "map" : string.Empty);
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(row.AtlasMap
                        ? "EndgameMaps.dat names this area - the atlas can send you here"
                        : report.AtlasMaps == 0
                            ? "EndgameMaps has not been read yet, so nothing is known either way"
                            : "not in EndgameMaps.dat - a hideout, campaign zone, scene or the like");
                }

                ImGui.TableNextColumn();
                ImGui.TextColored(row.OnAtlas ? OverlayInk.Name : DimText, row.Id);
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(row.OnAtlas
                        ? "seen on the atlas this session"
                        : "in a table, not seen on the atlas this session");
                }

                ImGui.TableNextColumn();
                Cell(row.InGame ? row.GameName : "-", row.Name is Agreement.Differ or Agreement.FileOnly);
                ImGui.TableNextColumn();
                Cell(row.InFile ? row.FileName : "-", row.Name is Agreement.Differ or Agreement.GameOnly);

                ImGui.TableNextColumn();
                ImGui.TextColored(DimText, row.GameUnique ? "yes" : string.Empty);

                ImGui.TableNextColumn();
                ImGui.TextColored(DimText, row.Rating?.ToString() ?? string.Empty);
                ImGui.TableNextColumn();
                ImGui.TextColored(DimText, string.Join(' ', row.GameTags));
                ImGui.TableNextColumn();
                ImGui.TextColored(DimText, string.Join(' ', row.FileTags));
            }
        }
        finally
        {
            ImGui.EndTable();
        }

        if (rows.Count > MostRows)
        {
            ImGui.TextColored(WarnText, $"...and {rows.Count - MostRows} more - narrow the search");
        }
    }

    private static void Cell(string text, bool notable)
        => ImGui.TextColored(notable ? WarnText : DimText, text);

    private bool Shows(MapDataRow row, string find)
    {
        if (_atlasOnly && !row.AtlasMap)
        {
            return false;
        }

        // The name is the only field with two sources left to disagree, so it is the only one a
        // "differences" filter can be about.
        if (_differencesOnly && row.Name is not (Agreement.Differ or Agreement.GameOnly or Agreement.FileOnly))
        {
            return false;
        }

        return find.Length == 0
            || row.Id.Contains(find, StringComparison.OrdinalIgnoreCase)
            || row.GameName.Contains(find, StringComparison.OrdinalIgnoreCase)
            || row.FileName.Contains(find, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The button that puts the whole thing where a spreadsheet can reach it.</summary>
    private void Export(MapDataReport report)
    {
        if (ImGui.Button("Save as TSV"))
        {
            _saved = _save(report.ToText());
        }

        ImGui.SameLine();
        ImGuiText.Wrapped(
            _saved.Length > 0 ? GoodText : DimText,
            _saved.Length > 0 ? _saved : "writes every row beside the exe, for checking outside the overlay");
    }
}
