using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;

namespace PoEformance.Overlay;

/// <summary>
/// The world-map pins, and what a locked one is waiting on.
/// </summary>
/// <remarks>
/// THE USEFUL HALF IS THE INVERSE. Which pins the map shows is a thing the map already shows;
/// which flags a pin is WAITING ON is not visible anywhere in the game, because a locked pin
/// simply is not drawn. That is the question this answers, and it is the same join the quest
/// steps use - a pin's conditions are references into QuestFlags, and the character's set is a
/// bitset indexed by those rows.
///
/// AND IT SETTLES A QUESTION IT CANNOT ANSWER ALONE. MapPins carries five flag columns and,
/// unlike QuestStates, nothing names them: they could all be required, they could be
/// alternatives, one could be an exclusion. So the two readings are counted side by side in
/// the header, and whichever matches the number of pins the game actually draws is the answer.
/// One look rather than an argument - the same way Order and Message were settled.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class MapPinWindow
{
    private static readonly Vector4 DimText = OverlayTheme.Quiet;
    private static readonly Vector4 GoodText = new(0.55f, 0.9f, 0.65f, 1f);
    private static readonly Vector4 WarnText = new(1f, 0.6f, 0.35f, 1f);
    private static readonly Vector4 ActText = new(0.75f, 0.8f, 0.95f, 1f);

    private readonly QuestWatch _watch;
    private string _search = string.Empty;
    private bool _lockedOnly = true;
    private bool _columns;

    public MapPinWindow(QuestWatch watch)
    {
        ArgumentNullException.ThrowIfNull(watch);
        _watch = watch;
    }

    /// <summary>Draws the tab's content.</summary>
    public void DrawTab()
    {
        IReadOnlyList<MapPin> pins = _watch.Pins;
        if (pins.Count == 0)
        {
            ImGui.TextColored(WarnText, "no pins read");
            ImGui.TextColored(DimText, "MapPins is optional - the quest steps work without it. See the Quests tab for why it did not open.");
            return;
        }

        int all = pins.Count(p => p.All);
        int any = pins.Count(p => p.Any);
        int free = pins.Count(p => p.Unconditional);

        ImGui.TextColored(DimText, $"{pins.Count} pins, {free} with no flag conditions at all");

        // THE MEASUREMENT. Nothing in the data says whether a pin's five flag columns are all
        // required or alternatives, so both readings are counted here. Compare against the
        // number of pins the game's own world map draws: whichever matches is what the columns
        // mean, and the comment in MapPinProgress should then say so.
        ImGui.TextColored(ActText, $"every condition met: {all}");
        ImGui.SameLine();
        ImGui.TextColored(ActText, $"   any condition met: {any}");
        ImGui.TextColored(
            DimText,
            "what the five flag columns mean is not known - count the pins your world map draws"
            + " and see which of these two it matches");

        ImGui.Checkbox("only the locked ones", ref _lockedOnly);
        ImGui.SameLine();
        ImGui.Checkbox("show the columns", ref _columns);

        ImGui.SetNextItemWidth(220f);
        ImGui.InputTextWithHint("##pin-search", "filter by name", ref _search, 64);

        ImGui.Separator();

        if (!ImGui.BeginChild("pin-list", Vector2.Zero, ImGuiChildFlags.Borders))
        {
            ImGui.EndChild();
            return;
        }

        var act = -1;
        foreach (MapPin pin in pins.OrderBy(p => p.Act).ThenBy(p => p.Name, StringComparer.Ordinal))
        {
            // A pin with no conditions is always there and has nothing to wait on, so it is
            // noise in a list about what is waiting.
            if (pin.Unconditional || pin.Name.Length == 0)
            {
                continue;
            }

            if (_lockedOnly && pin.All)
            {
                continue;
            }

            if (_search.Length > 0 && !pin.Name.Contains(_search, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (pin.Act != act)
            {
                act = pin.Act;
                ImGui.TextColored(ActText, act > 0 ? $"Act {act}" : "no act");
                ImGui.Separator();
            }

            Row(pin);
        }

        ImGui.EndChild();
    }

    private void Row(MapPin pin)
    {
        ImGui.PushID(pin.Row);

        ImGui.TextColored(pin.All ? GoodText : DimText, pin.Name);
        ImGui.SameLine();
        ImGui.TextColored(DimText, pin.All ? "shown" : pin.Any ? "partly" : "waiting");

        // WHAT IT IS WAITING ON, which is the line this tab exists for. Named, because a row
        // number says nothing and "a1q3-SpokeToFarrow" says what to go and do.
        foreach (PinCondition condition in pin.Real)
        {
            foreach (int flag in condition.Wanting)
            {
                ImGui.TextColored(WarnText, $"    needs: {Name(flag)}");
            }
        }

        if (_columns)
        {
            foreach (PinCondition condition in pin.Real)
            {
                ImGui.TextColored(
                    condition.Met ? GoodText : DimText,
                    $"    {condition.Column,-13} {condition.Held.Count}/{condition.Rows.Count}");
            }
        }

        ImGui.PopID();
    }

    private string Name(int row)
    {
        string id = _watch.FlagId(row);
        return id.Length > 0 ? id : $"flag row {row}";
    }
}
