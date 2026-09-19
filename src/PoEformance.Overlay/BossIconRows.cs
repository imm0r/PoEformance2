using System.Numerics;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.World;

namespace PoEformance.Overlay;

/// <summary>
/// The endgame maps whose boss has no picture yet, as a list somebody can work through.
/// </summary>
/// <remarks>
/// WHY A LIST IS POSSIBLE AT ALL: the set of maps is known exactly, from the game's own
/// EndgameMaps.dat, so "what is still missing" is arithmetic rather than something noticed one
/// map at a time - see <see cref="BossIconPlan"/>, which measures that not one of the 173 ids
/// resolves a picture from its name. The work is therefore long and finite, which is the shape
/// of thing that deserves a list.
///
/// ON THE TAB AND NOT IN A WINDOW OF ITS OWN, because it is read between maps rather than
/// during one, and because it belongs beside the switch it is about. The three states it
/// separates are the three things somebody can do next: make the picture, paste the art into
/// the sheet, or tick the row off as having no boss.
///
/// A ROW IS A CHOICE AND NOT A COMMAND. Clicking one only says "this is the map I am doing
/// next", which the model pane's export reads to prefill its area field - see
/// <see cref="MonsterPortrait.AreaNow"/>. Nothing is written until the write button is pressed
/// in the window that shows the picture being written.
/// </remarks>
internal sealed class BossIconRows(Func<BossIcons> icons, Func<AtlasMapNames> maps)
{
    /// <summary>How many rows are drawn at most. The plan is 173 long and the tab is not.</summary>
    private const int MostRows = 400;

    /// <summary>How tall the scrolling area is, in rows.</summary>
    private const int Tall = 12;

    /// <summary>Longest the search box takes.</summary>
    private const uint FindLength = 64;

    private readonly List<BossIconTask> _rows = [];
    private string _find = string.Empty;
    private bool _showDone;
    private bool _showQuiet;
    private int _revision = -1;
    private int _missing = -1;
    private int _count = -1;
    private string _said = string.Empty;

    /// <summary>
    /// The map somebody picked to do next, or empty. What the export's area field prefills from.
    /// </summary>
    public string Picked { get; private set; } = string.Empty;

    /// <summary>The list, its filters and what it adds up to.</summary>
    public void Draw()
    {
        BossIcons written = icons();
        Plan(written);

        (int open, int waiting, int done, int skipped) = BossIconPlan.Count(_rows);
        ImGuiText.Wrapped(
            OverlayInk.Quiet,
            $"{_rows.Count} endgame maps: {done} wear their boss's picture, {waiting} are written"
                + $" down and waiting for the art, {skipped} have no boss, {open} still to do.");

        ImGui.SetNextItemWidth(200f);
        ImGui.InputTextWithHint("##boss-plan-find", "find by id or name...", ref _find, FindLength);
        ImGui.SameLine();
        ImGui.Checkbox("done too", ref _showDone);
        ImGui.SameLine();
        ImGui.Checkbox("hideouts and towers", ref _showQuiet);

        Table(written);

        if (_said.Length > 0)
        {
            ImGuiText.Wrapped(OverlayInk.Quiet, _said);
        }
    }

    /// <summary>
    /// Works the plan out again when something it is made of moved, and not per frame.
    /// </summary>
    /// <remarks>
    /// Three things can move it: an entry was written, an arena was collected, or the game
    /// taught the map table something. Each is counted rather than watched, because all three
    /// are cheap integers beside the walk - 173 maps against the sheet's name table and the
    /// collected log - that this would otherwise do sixty times a second.
    /// </remarks>
    private void Plan(BossIcons written)
    {
        AtlasMapNames table = maps();
        if (written.Revision == _revision && written.MissingCount == _missing && table.Count == _count)
        {
            return;
        }

        _revision = written.Revision;
        _missing = written.MissingCount;
        _count = table.Count;

        _rows.Clear();
        _rows.AddRange(BossIconPlan.Of(table, written, name => IconNames.CellFor(name) > 0));
    }

    /// <summary>Whether a row survives the filters.</summary>
    private bool Shows(BossIconTask row)
    {
        if (!_showDone && row.State is BossIconState.Done or BossIconState.Skipped)
        {
            return false;
        }

        if (!_showQuiet && BossIconPlan.IsQuiet(row))
        {
            return false;
        }

        string find = _find.Trim();
        return find.Length == 0
            || row.Id.Contains(find, StringComparison.OrdinalIgnoreCase)
            || row.Name.Contains(find, StringComparison.OrdinalIgnoreCase)
            || row.Boss.Contains(find, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The rows themselves.</summary>
    private void Table(BossIcons written)
    {
        if (!ImGui.BeginTable(
                "##boss-plan-rows",
                4,
                ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY
                    | ImGuiTableFlags.BordersInnerV,
                new Vector2(0f, ImGui.GetTextLineHeightWithSpacing() * Tall)))
        {
            return;
        }

        try
        {
            ImGui.TableSetupColumn("map", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("id", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("boss");
            ImGui.TableSetupColumn("no boss");
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            int shown = 0;
            foreach (BossIconTask row in _rows)
            {
                if (!Shows(row) || ++shown > MostRows)
                {
                    continue;
                }

                Row(row, written);
            }
        }
        finally
        {
            ImGui.EndTable();
        }
    }

    /// <summary>One map: what it is, where it stands, and the tick that says it has no boss.</summary>
    private void Row(BossIconTask row, BossIcons written)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();

        bool picked = string.Equals(Picked, row.Id, StringComparison.OrdinalIgnoreCase);
        ImGui.PushStyleColor(ImGuiCol.Text, Ink(row.State));
        if (ImGui.Selectable($"{row.Name}###boss-plan-{row.Id}", picked, ImGuiSelectableFlags.SpanAllColumns))
        {
            // Clicking the row it is already on lets go of it, so the export goes back to
            // prefilling from where the player actually is.
            Picked = picked ? string.Empty : row.Id;
        }

        ImGui.PopStyleColor();

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(Said(row));
        }

        ImGui.TableNextColumn();
        ImGui.TextColored(OverlayInk.Quiet, row.Id);

        ImGui.TableNextColumn();
        ImGui.TextColored(
            row.Boss.Length > 0 ? OverlayInk.Name : OverlayInk.Quiet,
            row.Boss.Length > 0 ? row.Boss : row.Family);

        ImGui.TableNextColumn();
        bool skip = row.State == BossIconState.Skipped;
        if (ImGui.Checkbox($"##boss-plan-skip-{row.Id}", ref skip))
        {
            written.Skip(row.Id, skip, out string said);
            _said = said;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("tick a map that has no boss to make a picture for.\nit stops being counted as work.");
        }
    }

    /// <summary>The colour a state is drawn in: done is quiet, work is not.</summary>
    private static Vector4 Ink(BossIconState state) => state switch
    {
        BossIconState.Done => OverlayInk.Good,
        BossIconState.Waiting => OverlayInk.Warn,
        BossIconState.Skipped => OverlayInk.Quiet,
        _ => OverlayInk.Ink,
    };

    /// <summary>What a row's state means, and what is known about it.</summary>
    private static string Said(BossIconTask row)
    {
        string where = row.Tiles.Count > 0
            ? $"\narena tile seen here: {row.Tiles[0]}"
            : "\nno arena tile collected here yet - the map has not been played with the tool";

        return row.State switch
        {
            BossIconState.Done =>
                $"{row.Family} - the marker wears this picture.{where}",
            BossIconState.Waiting =>
                $"written down as {row.Family}, but the sheet has no cell under that name yet:"
                    + $" the art still has to go into assets/icons.png.{where}",
            BossIconState.Skipped =>
                $"ticked off as having no boss.{where}",
            _ =>
                "no picture and nothing written down. pose its boss in the Monster Book's model"
                    + $" pane and write the entry from there.{where}",
        };
    }
}
