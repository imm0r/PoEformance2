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
/// Three things make that possible, and the third is the one that matters:
///
/// - every plausible reading of a row at once, since bytes carry no type;
/// - a pointer can be FOLLOWED, so an unknown structure can be walked rather than only stared
///   at, with a trail back;
/// - and rows that CHANGE are marked. Reading an unknown structure tells you almost nothing.
///   Reading it before and after doing something tells you which part of it was that thing -
///   which is the only way to find a field whose meaning is a verb, because nothing about the
///   bytes says "health" until something takes some away.
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

    private readonly StructureInspector _inspector;

    /// <summary>Where we came from, so following a pointer can be undone.</summary>
    /// <remarks>
    /// Descending through pointers is the whole way an unknown structure gets explored, and
    /// without a way back every wrong turn means typing the address again from memory. What
    /// the trail SAYS is the finding - see PointerTrail, which owns that because it is logic
    /// and got it wrong three ways while it lived in here.
    /// </remarks>
    private readonly PointerTrail _trail = new();

    /// <summary>When each row last changed, for fading the highlight out.</summary>
    private readonly Dictionary<int, long> _litAt = [];

    private StructureRoot _root = StructureRoot.AreaInstance;
    private string _typedAddress = string.Empty;
    private ulong _address;
    private int _size = 512;
    private int _strideIndex;          // 0 = eight bytes, 1 = four
    private int _structIndex;
    private string[]? _structNames;
    private int _snapshotSequence;
    private int _peekSequence;
    private int _peekOffset = -1;
    private bool _onlyChanged;

    /// <summary>What sent us here, when something did. Shown so the address has a meaning.</summary>
    private string _cameFrom = string.Empty;

    public DissectorWindow(StructureInspector inspector)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        _inspector = inspector;
    }

    /// <summary>Whether the window is on screen. Nothing is read while it is not.</summary>
    public bool Visible { get; set; }

    /// <summary>
    /// Points the dissector at an address, from somewhere else in the tool.
    /// </summary>
    /// <remarks>
    /// What makes the entity browser worth having: it can say a component is at an address
    /// and nothing describes it, and this is the step from knowing that to looking at it.
    /// Typing the address across by hand would be the whole friction of the thing.
    /// </remarks>
    /// <param name="knownLayout">A schema structure to name the rows by, when one applies.</param>
    public void Show(ulong address, string label = "", string knownLayout = "")
    {
        if (address == 0)
        {
            return;
        }

        Visible = true;
        GoTo(address, -1, keepTrail: false);
        _cameFrom = label;

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

    /// <summary>The schema's structure names, with "no names" first. Built once.</summary>
    private string[] Names() => _structNames ??= ["(no names)", .. _inspector.KnownStructures];

    /// <summary>Draws the window and publishes what it wants read next.</summary>
    public void Render()
    {
        if (!Visible)
        {
            _inspector.Request(StructureRequest.Idle);
            return;
        }

        StructureView view = _inspector.View;
        Remember(view);

        ImGui.SetNextWindowSize(new Vector2(940f, 620f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(new Vector2(60f, 60f), ImGuiCond.FirstUseEver);

        bool open = Visible;
        bool expanded = ImGui.Begin("Memory dissector", ref open, ImGuiWindowFlags.NoFocusOnAppearing);

        // End() in a finally, and this is not defensive habit - it is the difference between
        // a reported frame and a dead process. The overlay catches an escaping exception and
        // skips the frame, but an exception thrown between Begin and End leaves ImGui's
        // window stack unbalanced, and ImGui then asserts and takes the process down anyway.
        // Catching the exception was never enough on its own.
        try
        {
            if (expanded)
            {
                DrawControls(view);
                ImGui.Separator();
                DrawTrail();
                DrawPeek(view);
                ImGui.Separator();
                DrawRows(view);
            }
        }
        finally
        {
            ImGui.End();
        }
        Visible = open;

        _inspector.Request(new StructureRequest(
            Enabled: true,
            Root: _root,
            Address: _address,
            Size: _size,
            Stride: _strideIndex == 0 ? 8 : 4,
            StructName: SelectedStruct(),
            SnapshotSequence: _snapshotSequence,
            PeekOffset: _peekOffset,
            PeekSequence: _peekSequence));
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
            _cameFrom = string.Empty;
            _litAt.Clear();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(170f);
        if (ImGui.InputText("address", ref _typedAddress, 20, ImGuiInputTextFlags.EnterReturnsTrue)
            && ulong.TryParse(_typedAddress.Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase),
                NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong typed))
        {
            GoTo(typed, -1, keepTrail: false);
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
                "Remember these bytes. Do something in the game, and whatever moved\n"
                + "since is marked - which is how a field whose meaning is a verb gets found.");
        }

        ImGui.SameLine();
        ImGui.Checkbox("only what moved", ref _onlyChanged);

        ImGui.TextColored(DimText, view.Status);

        if (_cameFrom.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(NameText, $"- {_cameFrom}");
        }
    }

    private void DrawTrail()
    {
        if (_trail.IsEmpty)
        {
            return;
        }

        if (ImGui.SmallButton("back") && _trail.TryStepBack(out ulong address))
        {
            _address = address;
            _root = StructureRoot.Custom;
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

    private void DrawPeek(StructureView view)
    {
        if (view.Peek is not { } peek || view.PeekOffset < 0)
        {
            return;
        }

        ImGui.TextColored(DimText, $"+0x{view.PeekOffset:X} leads to");
        ImGui.SameLine();
        ImGui.TextColored(ColourOf(peek.Kind), $"{peek.Kind}: {peek.Summary}");
    }

    private void DrawRows(StructureView view)
    {
        if (view.Slots.Count == 0)
        {
            return;
        }

        const ImGuiTableFlags Flags =
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable
            | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit;

        if (!ImGui.BeginTable("rows", 8, Flags))
        {
            return;
        }

        try
        {
            DrawRowsInto(view);
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
            if (_onlyChanged && !moved && !_litAt.ContainsKey(slot.Offset))
            {
                continue;
            }

            ImGui.TableNextRow();
            Highlight(slot.Offset, moved, now);

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
                ImGui.TextColored(TextFound, text);
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(text);
                }
            }

            ImGui.TableNextColumn();
            if (slot.Followable)
            {
                // Two separate acts on purpose. Peeking is cheap and answers "is this worth
                // my time"; descending changes where you are and should not happen by
                // accident on the way to asking.
                if (ImGui.SmallButton($"peek##{slot.Offset}"))
                {
                    _peekOffset = slot.Offset;
                    _peekSequence++;
                }

                ImGui.SameLine();
                if (ImGui.SmallButton($"go##{slot.Offset}"))
                {
                    GoTo(slot.Raw, slot.Offset, keepTrail: true);
                }

                ImGui.SameLine();
                ImGui.TextColored(PointerText, $"0x{slot.Raw:X}");
            }
        }
    }

    /// <summary>Lights a row that moved, and lets it fade.</summary>
    private void Highlight(int offset, bool sinceBaseline, long now)
    {
        if (!_litAt.TryGetValue(offset, out long lit))
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
            _litAt.Remove(offset);
            return;
        }

        // A row marked against the baseline stays lit while it stands; a live flicker fades,
        // so a busy structure does not end up a solid block of colour.
        float strength = sinceBaseline ? 0.30f : 0.30f * (1f - Math.Clamp(age, 0f, 1f));
        ImGui.TableSetBgColor(
            ImGuiTableBgTarget.RowBg0,
            ImGui.GetColorU32(new Vector4(1f, 0.45f, 0.15f, strength)));
    }

    /// <summary>Notes which rows moved this tick, so the fade has something to work from.</summary>
    private void Remember(StructureView view)
    {
        long now = Environment.TickCount64;
        foreach (int offset in view.Live)
        {
            _litAt[offset] = now;
        }

        if (view.Address != _address && view.Address != 0 && _root != StructureRoot.Custom)
        {
            // A named root resolved somewhere new - a zone change, normally. Nothing about
            // the old address is worth carrying over.
            _address = view.Address;
            _litAt.Clear();
        }
    }

    private void GoTo(ulong address, int viaOffset, bool keepTrail)
    {
        if (keepTrail)
        {
            // Name the start before the line below turns it into a plain address - by the
            // second hop there is nothing left to name it from.
            if (_trail.IsEmpty)
            {
                _trail.Restart(_root != StructureRoot.Custom ? _root.ToString()
                    : _cameFrom.Length > 0 ? _cameFrom
                    : $"0x{_address:X}");
            }

            _trail.Follow(_address, viaOffset);
        }
        else
        {
            _trail.Restart(string.Empty);
        }

        _address = address;
        _root = StructureRoot.Custom;
        _typedAddress = address.ToString("X", CultureInfo.InvariantCulture);
        _peekOffset = -1;
        _litAt.Clear();
        _cameFrom = string.Empty;

        // Names belong to the structure they were chosen for. Carrying them onto whatever a
        // pointer led to would label the new place with the old one's fields.
        _structIndex = 0;
    }

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
        TargetKind.Structure => PointerText,
        _ => DimText,
    };
}
