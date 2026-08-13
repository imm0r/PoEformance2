using System.Globalization;
using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Core.Diagnostics;
using PoEformance.Features;

namespace PoEformance.Overlay;

/// <summary>
/// Looks at raw memory, live, over the game.
/// </summary>
/// <remarks>
/// The tool for the structures nobody has decoded yet. Every field in the schema started as a
/// row in a view like this one, and the way it stopped being unknown was somebody watching it
/// while something happened in the game.
///
/// Four things make that possible, and the last two are the ones that matter:
///
/// - every plausible reading of a row at once, since bytes carry no type;
/// - a pointer can be FOLLOWED, so an unknown structure can be walked rather than only stared
///   at, with a trail back;
/// - rows that CHANGE are marked. Reading an unknown structure tells you almost nothing.
///   Reading it before and after doing something tells you which part of it was that thing -
///   which is the only way to find a field whose meaning is a verb, because nothing about the
///   bytes says "health" until something takes some away;
/// - and several PLACES at once, walked through the same offset together. Two monsters side by
///   side separate what a monster IS from what this monster is: the rows that agree are the
///   species, the rows that differ are the individual. One window cannot ask that question at
///   all, and walking the two apart loses the comparison at the first hop.
///
/// Owns no reads, exactly like the interface browser: it publishes what it wants to see and
/// <see cref="StructureInspector"/> serves it where the reading already happens.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DissectorWindow
{
    /// <summary>How long a changed row stays lit after it moved.</summary>
    /// <remarks>
    /// Long enough to catch out of the corner of an eye. A row that changes for one frame is
    /// invisible without this, and a single-frame change is exactly what a key press does.
    /// </remarks>
    private const long FadeMs = 1500;

    private static readonly Vector4 DimText = new(0.62f, 0.65f, 0.72f, 1f);
    private static readonly Vector4 NameText = new(0.85f, 0.78f, 0.45f, 1f);
    private static readonly Vector4 PointerText = new(0.55f, 0.78f, 1f, 1f);
    private static readonly Vector4 FloatText = new(0.65f, 0.9f, 0.65f, 1f);
    private static readonly Vector4 BaselineText = new(1f, 0.55f, 0.35f, 1f);
    private static readonly Vector4 TextFound = new(0.75f, 0.95f, 0.75f, 1f);

    /// <summary>Rows the places disagree on. Its own colour because it is its own finding.</summary>
    /// <remarks>
    /// Deliberately nothing like the orange of a row that MOVED. The two answer different
    /// questions - changed over time, differs between instances - and a row can easily be
    /// both, so they must be legible at the same time and in the same glance.
    /// </remarks>
    private static readonly Vector4 DiffersText = new(1f, 0.72f, 0.95f, 1f);

    private readonly StructureInspector _inspector;

    /// <summary>Where we came from, so following a pointer can be undone.</summary>
    /// <remarks>
    /// Descending through pointers is the whole way an unknown structure gets explored, and
    /// without a way back every wrong turn means typing the address again from memory. What
    /// the trail SAYS is the finding - see PointerTrail, which owns that because it is logic
    /// and got it wrong three ways while it lived in here.
    /// </remarks>
    private readonly PointerTrail _trail = new();

    /// <summary>When each place's row last changed, for fading the highlight out.</summary>
    private readonly Dictionary<(int Place, int Offset), long> _litAt = [];

    /// <summary>
    /// The addresses being read side by side. Never empty; the first is the one the root, the
    /// address box and the schema layout all mean.
    /// </summary>
    private readonly List<Place> _places = [new Place()];

    private StructureRoot _root = StructureRoot.AreaInstance;
    private string _typedAddress = string.Empty;
    private string _typedCompare = string.Empty;
    private int _size = 512;
    private int _strideIndex;          // 0 = eight bytes, 1 = four
    private int _structIndex;
    private string[]? _structNames;
    private int _snapshotSequence;
    private int _peekSequence;
    private int _peekOffset = -1;
    private bool _onlyChanged;
    private int _agreement;            // 0 = every row, 1 = only differing, 2 = only matching
    private bool _hideEmpty = true;

    public DissectorWindow(StructureInspector inspector)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        _inspector = inspector;
    }

    /// <summary>Bytes per row, as the reader wants it.</summary>
    private int Stride => _strideIndex == 0 ? 8 : 4;

    /// <summary>
    /// Points the dissector at an address, from somewhere else in the tool.
    /// </summary>
    /// <remarks>
    /// What makes the entity browser worth having: it can say a component is at an address
    /// and nothing describes it, and this is the step from knowing that to looking at it.
    /// Typing the address across by hand would be the whole friction of the thing.
    ///
    /// Only the state moves here - bringing the dissector's tab in front is the caller's
    /// half, because this class no longer owns a window to show.
    /// </remarks>
    /// <param name="knownLayout">A schema structure to name the rows by, when one applies.</param>
    public void Show(ulong address, string label = "", string knownLayout = "")
    {
        if (address == 0)
        {
            return;
        }

        GoTo(address);
        _places[0].Label = label;

        if (knownLayout.Length > 0)
        {
            // Names() rather than the field: this can be called before the window has ever
            // drawn, and a layout that silently failed to apply is worse than none.
            int at = Array.IndexOf(Names(), knownLayout);
            if (at > 0)
            {
                _structIndex = at;
            }
        }
    }

    /// <summary>
    /// Pins another address beside the ones already open, to be walked with them.
    /// </summary>
    /// <remarks>
    /// The other half of <see cref="Show"/>, and where the comparison comes from in practice:
    /// two monsters picked out of the entity browser, or the same component off two entities.
    /// Typing both addresses by hand is possible and is not what anybody does.
    /// </remarks>
    /// <returns>False when it was already open, or when there is no room for another.</returns>
    public bool Compare(ulong address, string label = "")
    {
        if (address == 0 || _places.Count >= StructureInspector.MaxColumns)
        {
            return false;
        }

        foreach (Place place in _places)
        {
            if (place.Address == address)
            {
                return false;   // already open; adding it twice would compare it with itself
            }
        }

        _places.Add(new Place { Address = address, Label = label });
        Regroup();
        return true;
    }

    /// <summary>The schema's structure names, with "no names" first. Built once.</summary>
    private string[] Names() => _structNames ??= ["(no names)", .. _inspector.KnownStructures];

    /// <summary>Draws the tab's content and publishes what it wants read next.</summary>
    public void DrawTab()
    {
        StructureView view = _inspector.View;
        Remember(view);

        DrawControls(view);
        ImGui.Separator();
        DrawPlaces(view);
        DrawTrail();
        DrawPeek(view);
        ImGui.Separator();
        DrawRows(view);

        _inspector.Request(new StructureRequest(
            Enabled: true,
            Root: _root,
            Address: _places[0].Address,
            Size: _size,
            Stride: Stride,
            StructName: SelectedStruct(),
            SnapshotSequence: _snapshotSequence,
            PeekOffset: _peekOffset,
            PeekSequence: _peekSequence,
            Compared: Pinned()));
    }

    /// <summary>While the tab is not in front, nothing is read for it.</summary>
    public void Idle() => _inspector.Request(StructureRequest.Idle);

    /// <summary>The pinned addresses, as a fresh array the reader thread can hold on to.</summary>
    /// <remarks>
    /// Copied rather than shared: the list behind it is edited by clicks on this thread, and
    /// handing the reader a view onto something being mutated is the one race this design
    /// exists to avoid.
    /// </remarks>
    private ulong[] Pinned()
    {
        if (_places.Count == 1)
        {
            return [];
        }

        var pinned = new ulong[_places.Count - 1];
        for (int i = 1; i < _places.Count; i++)
        {
            pinned[i - 1] = _places[i].Address;
        }

        return pinned;
    }

    private void DrawControls(StructureView view)
    {
        string[] roots = Enum.GetNames<StructureRoot>();
        int rootIndex = (int)_root;

        ImGui.SetNextItemWidth(150f);
        if (ImGui.Combo("start", ref rootIndex, roots, roots.Length))
        {
            _root = (StructureRoot)rootIndex;
            _trail.Restart(_root.ToString());
            _places[0].Label = string.Empty;
            _litAt.Clear();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(170f);
        if (ImGui.InputText("address", ref _typedAddress, 20, ImGuiInputTextFlags.EnterReturnsTrue)
            && TryHex(_typedAddress, out ulong typed))
        {
            GoTo(typed);
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(110f);
        ImGui.Combo("rows", ref _strideIndex, ["8 bytes", "4 bytes"], 2);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(110f);
        if (ImGui.InputInt("bytes", ref _size, 128, 512))
        {
            _size = Math.Clamp(_size, 8, StructureInspector.MaxSize);
        }

        // The schema's own names, laid over the rows. This is also how a MISALIGNED read
        // announces itself: every row gets a name and none of them make sense.
        string[] names = Names();
        ImGui.SetNextItemWidth(220f);
        ImGui.Combo("known layout", ref _structIndex, names, names.Length);

        ImGui.SameLine();
        if (ImGui.Button(view.HasBaseline ? "re-mark" : "mark"))
        {
            _snapshotSequence++;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Remember these bytes, in every place at once. Do something in the game, and\n"
                + "whatever moved since is marked - which is how a field whose meaning is a\n"
                + "verb gets found.");
        }

        ImGui.SameLine();
        ImGui.Checkbox("only what moved", ref _onlyChanged);

        DrawComparisonControls();

        ImGui.TextColored(DimText, view.Status);

        if (_places.Count == 1 && _places[0].Label.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(NameText, $"- {_places[0].Label}");
        }
    }

    /// <summary>
    /// Everything about holding several places at once, on a line of its own.
    /// </summary>
    /// <remarks>
    /// Its own line rather than appended to the controls above, which were already a full row
    /// wide. Crowding these in beside them would push something off the right edge of a narrow
    /// tab - and the first thing to go would be whichever control was added last, which is
    /// exactly the one nobody has learned to look for yet.
    /// </remarks>
    private void DrawComparisonControls()
    {
        if (_places.Count > 1)
        {
            ImGui.SetNextItemWidth(190f);
            ImGui.Combo("show", ref _agreement, ["every row", "only what differs", "only what matches"], 3);

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "Rows that DIFFER between the places belong to the individual - health,\n"
                    + "position, id. Rows that MATCH belong to the kind - the vtable, the class\n"
                    + "pointer, the dat row every one of them was built from.");
            }

            ImGui.SameLine();
            ImGui.Checkbox("hide empty", ref _hideEmpty);

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "Most of a window is zeroes, and zeroes MATCH each other - so without this\n"
                    + "the \"only what matches\" side of the comparison is mostly padding.");
            }

            ImGui.SameLine();
        }

        if (_places.Count >= StructureInspector.MaxColumns)
        {
            ImGui.TextColored(DimText, $"{StructureInspector.MaxColumns} places is as many as are read at once");
            return;
        }

        ImGui.SetNextItemWidth(170f);
        if (ImGui.InputText("compare with", ref _typedCompare, 20, ImGuiInputTextFlags.EnterReturnsTrue)
            && TryHex(_typedCompare, out ulong beside)
            && Compare(beside))
        {
            _typedCompare = string.Empty;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Another address of the SAME kind of structure, read beside this one and\n"
                + "walked through the same offsets with it. The entity browser can send one\n"
                + "over without typing.");
        }
    }

    /// <summary>The places open, one line each, with what each of them is currently saying.</summary>
    private void DrawPlaces(StructureView view)
    {
        if (_places.Count == 1)
        {
            return;   // the controls already name the single place; a list of one is noise
        }

        // A colour that has to be remembered is a colour that gets misread, and this view
        // carries two of them saying different things at the same time. One dim line settles
        // it for good.
        ImGui.TextColored(DiffersText, "offset");
        ImGui.SameLine();
        ImGui.TextColored(DimText, "differs");
        ImGui.SameLine();
        ImGui.TextColored(BaselineText, "  cell");
        ImGui.SameLine();
        ImGui.TextColored(DimText, "moved since the mark      \"=\" same as place 1      \"-\" not read");

        int drop = -1;

        for (int i = 0; i < _places.Count; i++)
        {
            Place place = _places[i];
            StructureColumn column = ColumnAt(view, i);

            if (ImGui.SmallButton($"x##drop{i}"))
            {
                drop = i;
            }

            ImGui.SameLine();
            ImGui.TextColored(i == 0 ? NameText : DimText, $"{i + 1}: 0x{place.Address:X}");

            if (place.Label.Length > 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(NameText, ImGuiText.Escape(place.Label));
            }

            // Only when something is wrong with it. A place reading normally has its row count
            // in the table already, and repeating it per place would bury the one line that
            // matters: a side that quietly reads NOTHING looks exactly like a structure that
            // disagrees with the others about everything.
            if (column.Slots.Count == 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(BaselineText, column.Status);
            }
        }

        if (drop >= 0)
        {
            Drop(drop);
        }
    }

    private void DrawTrail()
    {
        if (_trail.IsEmpty)
        {
            return;
        }

        if (ImGui.SmallButton("back") && _trail.TryStepBack(out IReadOnlyList<ulong> back))
        {
            for (int i = 0; i < _places.Count && i < back.Count; i++)
            {
                _places[i].Address = back[i];
            }

            _root = StructureRoot.Custom;
            _typedAddress = _places[0].Address.ToString("X", CultureInfo.InvariantCulture);
            _litAt.Clear();

            // The trail just changed under us, and stepping back off the last hop empties it.
            // Drawing the rest of this row would describe a route that no longer exists.
            return;
        }

        string route = _trail.Describe();
        ImGui.SameLine();
        ImGui.TextColored(NameText, route);
        ImGui.SameLine();
        if (ImGui.SmallButton("copy"))
        {
            ImGui.SetClipboardText(route);
        }
    }

    /// <summary>
    /// What the peeked row leads to, in every place at once.
    /// </summary>
    /// <remarks>
    /// The fastest way there is to name an unknown row. Two monsters open, peek at a row, and
    /// "WideText: Skeleton" beside "WideText: Zombie" settles what it is in one click - where
    /// the same row on one monster is a pointer to a string that could be anything.
    /// </remarks>
    private void DrawPeek(StructureView view)
    {
        if (view.PeekOffset < 0)
        {
            return;
        }

        bool any = false;

        for (int i = 0; i < _places.Count; i++)
        {
            if (ColumnAt(view, i).Peek is not { } peek)
            {
                continue;
            }

            if (!any)
            {
                ImGui.TextColored(DimText, $"+0x{view.PeekOffset:X} leads to");
                any = true;
            }

            if (_places.Count > 1)
            {
                ImGui.TextColored(DimText, $"  {i + 1}:");
            }

            ImGui.SameLine();
            ImGui.TextColored(ColourOf(peek.Kind), $"{peek.Kind}: {ImGuiText.Escape(peek.Summary)}");
        }
    }

    private void DrawRows(StructureView view)
    {
        int rows = RowCount(view);
        if (rows == 0)
        {
            return;
        }

        const ImGuiTableFlags Flags =
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable
            | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit;

        bool compared = _places.Count > 1;

        // Sideways scrolling only when there is something to scroll to. With one place the
        // columns are sized to fit the window, and turning it on would make them scroll
        // instead of filling it.
        if (!ImGui.BeginTable(
            "rows",
            compared ? _places.Count + 3 : 8,
            compared ? Flags | ImGuiTableFlags.ScrollX : Flags))
        {
            return;
        }

        try
        {
            if (compared)
            {
                DrawComparedInto(view, rows);
            }
            else
            {
                DrawRowsInto(view);
            }
        }
        finally
        {
            ImGui.EndTable();
        }
    }

    private void DrawRowsInto(StructureView view)
    {
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("offset", ImGuiTableColumnFlags.WidthFixed, 66f);
        ImGui.TableSetupColumn("name", ImGuiTableColumnFlags.WidthFixed, 150f);
        ImGui.TableSetupColumn("guess", ImGuiTableColumnFlags.WidthFixed, 60f);
        ImGui.TableSetupColumn("hex", ImGuiTableColumnFlags.WidthFixed, 150f);
        ImGui.TableSetupColumn("whole", ImGuiTableColumnFlags.WidthFixed, 130f);
        ImGui.TableSetupColumn("decimal", ImGuiTableColumnFlags.WidthFixed, 140f);
        ImGui.TableSetupColumn("text", ImGuiTableColumnFlags.WidthFixed, 230f);
        ImGui.TableSetupColumn("follow", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        long now = Environment.TickCount64;

        foreach (StructureSlot slot in view.Slots)
        {
            bool moved = view.SinceBaseline.Contains(slot.Offset);
            if (!Visible(slot.Offset, moved, differs: false))
            {
                continue;
            }

            ImGui.TableNextRow();
            Highlight(ImGuiTableBgTarget.RowBg0, 0, slot.Offset, moved, now);

            ImGui.TableNextColumn();
            ImGui.TextColored(moved ? BaselineText : DimText, $"+0x{slot.Offset:X3}");

            ImGui.TableNextColumn();
            if (view.FieldNames.TryGetValue(slot.Offset, out string? name))
            {
                ImGui.TextColored(NameText, name);
            }

            ImGui.TableNextColumn();
            ImGui.TextColored(ColourOf(slot.Guess), slot.Guess.ToString().ToLowerInvariant());

            ImGui.TableNextColumn();
            ImGui.Text($"{slot.Raw:X16}");

            ImGui.TableNextColumn();
            ImGui.Text(_strideIndex == 0 ? $"{slot.Low}  {slot.High}" : slot.Low.ToString(CultureInfo.InvariantCulture));

            ImGui.TableNextColumn();
            ImGui.TextColored(
                slot.Guess == SlotGuess.Float ? FloatText : DimText,
                _strideIndex == 0
                    ? $"{Short(slot.FloatLow)}  {Short(slot.FloatHigh)}"
                    : Short(slot.FloatLow));

            // Before "follow" on purpose: when a row turns out to be a name, that is the
            // answer, and the address it came from stops being the interesting part.
            ImGui.TableNextColumn();
            if (view.Text.TryGetValue(slot.Offset, out string? text))
            {
                DrawText(text, slot.Offset, place: 0);
            }

            ImGui.TableNextColumn();
            if (slot.Followable)
            {
                DrawWalk(slot.Offset, view);
                ImGui.SameLine();
                ImGui.TextColored(PointerText, $"0x{slot.Raw:X}");
            }
        }
    }

    /// <summary>
    /// The same rows, read in every place at once.
    /// </summary>
    /// <remarks>
    /// LEGIBILITY IS THE WHOLE PROBLEM HERE, and it gets worse with every place added: six
    /// readings of a row times six places is a wall of numbers nobody can scan, and a wall is
    /// worth less than the single window it replaced. Four things keep it readable, and each
    /// one takes something OFF the screen rather than putting more on:
    ///
    /// - one reading per cell instead of six, chosen by what the row most likely is, with the
    ///   full set one hover away for the row that turns out to matter;
    /// - a place that AGREES with the first shows "=" rather than repeating the value, so the
    ///   only numbers left on screen are the ones that differ - which is the finding;
    /// - the filters, which are the real tool: "only what differs" on two monsters of a kind
    ///   turns sixty-four rows into the handful that describe an individual;
    /// - and the offset and name columns frozen, because a value in the fifth place means
    ///   nothing once the row it belongs to has scrolled off the left.
    /// </remarks>
    private void DrawComparedInto(StructureView view, int rows)
    {
        int stride = Stride;

        // Offset, name and the walk buttons stay put while the places scroll sideways. The
        // offset because a value in the fifth place is worthless once the row it belongs to
        // has scrolled off the left - and the buttons because descending is the thing being
        // done here, and with six places open they would otherwise sit off the right edge.
        ImGui.TableSetupScrollFreeze(3, 1);
        ImGui.TableSetupColumn("offset", ImGuiTableColumnFlags.WidthFixed, 66f);
        ImGui.TableSetupColumn("name", ImGuiTableColumnFlags.WidthFixed, 140f);
        ImGui.TableSetupColumn("walk", ImGuiTableColumnFlags.WidthFixed, 96f);

        for (int i = 0; i < _places.Count; i++)
        {
            ImGui.TableSetupColumn($"{i + 1}: {Heading(_places[i])}##place{i}", ImGuiTableColumnFlags.WidthFixed, 185f);
        }

        ImGui.TableHeadersRow();

        long now = Environment.TickCount64;

        for (int index = 0; index < rows; index++)
        {
            int offset = index * stride;
            bool differs = view.Differing.Contains(offset);
            bool moved = MovedAnywhere(view, offset);

            if (!Visible(offset, moved, differs) || (_hideEmpty && EmptyEverywhere(view, index, offset)))
            {
                continue;
            }

            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextColored(differs ? DiffersText : DimText, $"+0x{offset:X3}");

            ImGui.TableNextColumn();
            if (view.FieldNames.TryGetValue(offset, out string? name))
            {
                ImGui.TextColored(NameText, name);
            }

            // Before the values rather than after them, because the column it lives in is one
            // of the frozen ones. A row is followable when ANY place has a pointer there: the
            // one that does is exactly the place worth descending, and the others landing
            // nowhere is itself the finding that they do not have it.
            ImGui.TableNextColumn();
            if (Followable(view, index, offset))
            {
                DrawWalk(offset, view);
            }

            StructureSlot? reference = SlotAt(ColumnAt(view, 0), index, offset);

            for (int i = 0; i < _places.Count; i++)
            {
                ImGui.TableNextColumn();

                StructureColumn column = ColumnAt(view, i);
                if (SlotAt(column, index, offset) is not { } slot)
                {
                    // This place read nothing at all, or has not been read yet. Saying so is
                    // the point: a blank column is not agreement.
                    ImGui.TextColored(DimText, "-");
                    continue;
                }

                Highlight(ImGuiTableBgTarget.CellBg, i, offset, column.SinceBaseline.Contains(offset), now);

                // Repeating a value that already stands two columns to the left buys nothing
                // and costs the row's readability. Saying "same" leaves only the values that
                // are actually different on the screen - and those are what is being looked
                // for. The value itself stays one hover away.
                if (i > 0 && reference is { } first && Alike(first, slot))
                {
                    ImGui.TextColored(DimText, "=");
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip($"same as place 1\n\n{Everything(slot)}");
                    }

                    continue;
                }

                if (column.Text.TryGetValue(offset, out string? text))
                {
                    DrawText(text, offset, i);
                    continue;
                }

                ImGui.TextColored(ColourOf(slot.Guess), Reading(slot));
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(Everything(slot));
                }
            }
        }
    }

    /// <summary>True when at least one place has something worth descending into here.</summary>
    private bool Followable(StructureView view, int index, int offset)
    {
        for (int i = 0; i < _places.Count; i++)
        {
            if (SlotAt(ColumnAt(view, i), index, offset) is { Followable: true })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Peek and go, which act on every place at once.</summary>
    private void DrawWalk(int offset, StructureView view)
    {
        // Two separate acts on purpose. Peeking is cheap and answers "is this worth my time";
        // descending changes where you are and should not happen by accident on the way to
        // asking.
        if (ImGui.SmallButton($"peek##{offset}"))
        {
            _peekOffset = offset;
            _peekSequence++;
        }

        ImGui.SameLine();
        if (ImGui.SmallButton($"go##{offset}"))
        {
            Walk(offset, view);
        }
    }

    /// <summary>A string found in a row. Click to copy, because that is always what happens next.</summary>
    private static void DrawText(string text, int offset, int place)
    {
        // Every string that surfaces here is one somebody is about to paste somewhere else - a
        // metadata path into the noise filter, a file path into the preload meanings, a name
        // into a search - and the column is too narrow to read the long ones, let alone
        // transcribe one by hand.
        ImGui.PushStyleColor(ImGuiCol.Text, TextFound);
        if (ImGui.Selectable($"{ImGuiText.Escape(text)}###text{place}_{offset}"))
        {
            ImGui.SetClipboardText(text);
        }

        ImGui.PopStyleColor();

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"{ImGuiText.Escape(text)}\n\nclick to copy");
        }
    }

    /// <summary>Walks every place through the same offset, keeping them side by side.</summary>
    /// <remarks>
    /// THE reason to hold several places at once. Following the pointer in one of them and
    /// then hunting for the matching pointer in the others by hand loses the comparison at the
    /// first hop - and it is two hops down, in the structure the pointer leads to, that the
    /// interesting fields usually are.
    ///
    /// A place whose row is not a pointer lands on nothing rather than being quietly dropped.
    /// That IS the finding sometimes: the row is a pointer on one monster and empty on
    /// another, which is how an optional component announces itself.
    /// </remarks>
    private void Walk(int offset, StructureView view)
    {
        var from = new ulong[_places.Count];
        for (int i = 0; i < _places.Count; i++)
        {
            from[i] = _places[i].Address;
        }

        if (_trail.IsEmpty)
        {
            // Name the start before the loop below turns it into a plain address - by the
            // second hop there is nothing left to name it from.
            _trail.Restart(_root != StructureRoot.Custom ? _root.ToString()
                : _places[0].Label.Length > 0 ? _places[0].Label
                : $"0x{_places[0].Address:X}");
        }

        _trail.Follow(from, offset);

        for (int i = 0; i < _places.Count; i++)
        {
            _places[i].Address = Target(ColumnAt(view, i), offset);
            _places[i].Label = string.Empty;
        }

        Arrived();
    }

    /// <summary>Where the pointer at this offset leads in one place, or nowhere.</summary>
    private ulong Target(StructureColumn column, int offset)
        => SlotAt(column, offset / Stride, offset) is { Followable: true } slot ? slot.Raw : 0;

    /// <summary>Sends the first place somewhere, leaving anything pinned beside it alone.</summary>
    /// <remarks>
    /// Typing an address retargets ONE place rather than clearing the rest. A comparison is
    /// built up a place at a time and is expensive to assemble; throwing it away because the
    /// first place moved would make every correction cost the whole set.
    /// </remarks>
    private void GoTo(ulong address)
    {
        _places[0].Address = address;
        _places[0].Label = string.Empty;
        _trail.Restart(string.Empty);
        Arrived();
    }

    /// <summary>Settles the state that is true wherever we just landed.</summary>
    private void Arrived()
    {
        _root = StructureRoot.Custom;
        _typedAddress = _places[0].Address.ToString("X", CultureInfo.InvariantCulture);
        _peekOffset = -1;
        _litAt.Clear();

        // Names belong to the structure they were chosen for. Carrying them onto whatever a
        // pointer led to would label the new place with the old one's fields.
        _structIndex = 0;
    }

    /// <summary>Closes one place and leaves the rest where they are.</summary>
    private void Drop(int index)
    {
        _places.RemoveAt(index);
        if (_places.Count == 0)
        {
            _places.Add(new Place());
        }

        if (index == 0)
        {
            // The root named the place that just went. Whatever is first now is a plain
            // address, and saying otherwise would resolve the root over it on the next tick.
            _root = StructureRoot.Custom;
            _typedAddress = _places[0].Address.ToString("X", CultureInfo.InvariantCulture);
        }

        Regroup();
    }

    /// <summary>
    /// Starts the route again, from wherever the places now stand.
    /// </summary>
    /// <remarks>
    /// A route is only a finding when every place took it. Adding one late, or dropping one
    /// half way down, leaves hops that describe a walk the current set never made - and
    /// stepping back through those would send places to addresses they were never at. The
    /// addresses are untouched; only the recorded way there starts over.
    /// </remarks>
    private void Regroup()
    {
        _trail.Restart(string.Empty);
        _litAt.Clear();
    }

    /// <summary>Notes which rows moved this tick, so the fade has something to work from.</summary>
    private void Remember(StructureView view)
    {
        long now = Environment.TickCount64;

        for (int i = 0; i < _places.Count; i++)
        {
            foreach (int offset in ColumnAt(view, i).Live)
            {
                _litAt[(i, offset)] = now;
            }
        }

        if (view.Address != _places[0].Address && view.Address != 0 && _root != StructureRoot.Custom)
        {
            // A named root resolved somewhere new - a zone change, normally. Nothing about
            // the old address is worth carrying over.
            _places[0].Address = view.Address;
            _litAt.Clear();
        }
    }

    /// <summary>Lights a row that moved, and lets it fade.</summary>
    private void Highlight(ImGuiTableBgTarget target, int place, int offset, bool sinceBaseline, long now)
    {
        if (!_litAt.TryGetValue((place, offset), out long lit))
        {
            if (!sinceBaseline)
            {
                return;
            }

            lit = now;
        }

        float age = (now - lit) / (float)FadeMs;
        if (age > 1f && !sinceBaseline)
        {
            _litAt.Remove((place, offset));
            return;
        }

        // A row marked against the baseline stays lit while it stands; a live flicker fades,
        // so a busy structure does not end up a solid block of colour.
        float strength = sinceBaseline ? 0.30f : 0.30f * (1f - Math.Clamp(age, 0f, 1f));
        ImGui.TableSetBgColor(target, ImGui.GetColorU32(new Vector4(1f, 0.45f, 0.15f, strength)));
    }

    /// <summary>Whether a row survives the filters.</summary>
    private bool Visible(int offset, bool moved, bool differs)
    {
        if (_onlyChanged && !moved && !Lit(offset))
        {
            return false;
        }

        if (_places.Count > 1 && (_agreement == 1 ? !differs : _agreement == 2 && differs))
        {
            return false;
        }

        return true;
    }

    private bool Lit(int offset)
    {
        for (int i = 0; i < _places.Count; i++)
        {
            if (_litAt.ContainsKey((i, offset)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when two readings of a row hold the same bytes.
    /// </summary>
    /// <remarks>
    /// The bytes rather than any one reading of them: two rows that agree as integers agree
    /// as floats and as pointers too, and a comparison that used the DISPLAYED reading would
    /// call two different rows equal whenever the guess happened to hide the difference.
    /// </remarks>
    private bool Alike(StructureSlot first, StructureSlot other)
        => _strideIndex == 0 ? first.Raw == other.Raw : first.Low == other.Low;

    /// <summary>True when no place has anything but zeroes in this row.</summary>
    private bool EmptyEverywhere(StructureView view, int index, int offset)
    {
        for (int i = 0; i < _places.Count; i++)
        {
            if (SlotAt(ColumnAt(view, i), index, offset) is { Guess: not SlotGuess.Empty })
            {
                return false;
            }
        }

        return true;
    }

    private bool MovedAnywhere(StructureView view, int offset)
    {
        for (int i = 0; i < _places.Count; i++)
        {
            if (ColumnAt(view, i).SinceBaseline.Contains(offset))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>How many rows the deepest place has. Never fewer than the others show.</summary>
    private int RowCount(StructureView view)
    {
        int rows = 0;
        foreach (StructureColumn column in view.Columns)
        {
            rows = Math.Max(rows, column.Slots.Count);
        }

        return rows;
    }

    /// <summary>
    /// The reading for a place that the view has not caught up with yet.
    /// </summary>
    /// <remarks>
    /// The view is built on the reader thread from a request sent a frame or more ago, so a
    /// place added this frame has no column in it. An empty one keeps the table the shape the
    /// header promised instead of shifting every value one place to the left for a frame.
    /// </remarks>
    private static StructureColumn ColumnAt(StructureView view, int index)
        => index < view.Columns.Count ? view.Columns[index] : StructureColumn.Empty;

    /// <summary>The row at an offset in one place, when that place has one.</summary>
    private static StructureSlot? SlotAt(StructureColumn column, int index, int offset)
    {
        if (index < 0 || index >= column.Slots.Count)
        {
            return null;
        }

        StructureSlot slot = column.Slots[index];

        // Rows are laid out on a fixed stride, so the index IS the offset - but a stride that
        // changed since this view was built would make that quietly false.
        return slot.Offset == offset ? slot : null;
    }

    /// <summary>The one reading of a row most likely to mean something, for a narrow column.</summary>
    private string Reading(StructureSlot slot) => slot.Guess switch
    {
        SlotGuess.Empty => "-",
        SlotGuess.Pointer or SlotGuess.Code => $"0x{slot.Raw:X}",
        SlotGuess.Float => _strideIndex == 0
            ? $"{Short(slot.FloatLow)}  {Short(slot.FloatHigh)}"
            : Short(slot.FloatLow),
        SlotGuess.Integer => _strideIndex == 0
            ? $"{slot.Low}  {slot.High}"
            : slot.Low.ToString(CultureInfo.InvariantCulture),
        _ => _strideIndex == 0 ? $"{slot.Raw:X16}" : $"{slot.Low:X8}",
    };

    /// <summary>Every reading of a row at once, for the hover that follows a suspicion.</summary>
    private string Everything(StructureSlot slot)
        => $"0x{slot.Address:X}  (+0x{slot.Offset:X})\n"
        + $"hex      {(_strideIndex == 0 ? $"{slot.Raw:X16}" : $"{slot.Low:X8}")}\n"
        + $"whole    {slot.Low}{(_strideIndex == 0 ? $"  {slot.High}   |  {slot.Int64}" : string.Empty)}\n"
        + $"decimal  {Short(slot.FloatLow)}{(_strideIndex == 0 ? $"  {Short(slot.FloatHigh)}" : string.Empty)}\n"
        + $"guess    {slot.Guess.ToString().ToLowerInvariant()}";

    /// <summary>What to head a place's column with: its name where it has one, else where it is.</summary>
    private static string Heading(Place place)
    {
        if (place.Label.Length == 0)
        {
            return $"0x{place.Address:X}";
        }

        // The tail of a metadata path. "Metadata/Monsters/Skeletons/Skeleton" in a column
        // header pushes the value out of sight, and the last segment is the part that differs
        // between two of them anyway.
        int cut = place.Label.LastIndexOf('/');
        string name = cut >= 0 && cut + 1 < place.Label.Length ? place.Label[(cut + 1)..] : place.Label;
        return ImGuiText.Escape(name.Length > 20 ? name[..20] : name);
    }

    private static bool TryHex(string typed, out ulong address)
        => ulong.TryParse(
            typed.Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase),
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out address);

    private string SelectedStruct()
        => _structNames is not null && _structIndex > 0 && _structIndex < _structNames.Length
            ? _structNames[_structIndex]
            : string.Empty;

    private static string Short(float value)
        => StructureProbe.SensibleFloat(value) ? value.ToString("0.###", CultureInfo.InvariantCulture) : "-";

    private static Vector4 ColourOf(SlotGuess guess) => guess switch
    {
        SlotGuess.Pointer => PointerText,
        SlotGuess.Float => FloatText,
        SlotGuess.Text => NameText,
        SlotGuess.Code => DimText,
        SlotGuess.Empty => DimText,
        _ => new Vector4(0.85f, 0.85f, 0.9f, 1f),
    };

    private static Vector4 ColourOf(TargetKind kind) => kind switch
    {
        TargetKind.WideText or TargetKind.Text => NameText,
        TargetKind.Vector => new Vector4(0.8f, 0.6f, 1f, 1f),
        TargetKind.DatRow or TargetKind.DatTable => TextFound,
        TargetKind.Structure => PointerText,
        _ => DimText,
    };

    /// <summary>One address the dissector is reading, beside the others.</summary>
    private sealed class Place
    {
        public ulong Address { get; set; }

        /// <summary>What sent us here, when something did. Shown so the address has a meaning.</summary>
        public string Label { get; set; } = string.Empty;
    }
}
