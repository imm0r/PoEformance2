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

    private readonly StructureInspector _inspector;

    /// <summary>Where we came from, so following a pointer can be undone.</summary>
    /// <remarks>
    /// Descending through pointers is the whole way an unknown structure gets explored, and
    /// without a way back every wrong turn means typing the address again from memory. The
    /// trail also IS the finding: "AreaInstance then +0x598 then +0x20" is what gets written
    /// down, not the address it happens to land on today.
    /// </remarks>
    private readonly List<(ulong Address, int Offset)> _trail = [];

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

    public DissectorWindow(StructureInspector inspector)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        _inspector = inspector;
    }

    /// <summary>Whether the window is on screen. Nothing is read while it is not.</summary>
    public bool Visible { get; set; }

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
        if (ImGui.Begin("Memory dissector", ref open, ImGuiWindowFlags.NoFocusOnAppearing))
        {
            DrawControls(view);
            ImGui.Separator();
            DrawTrail();
            DrawPeek(view);
            ImGui.Separator();
            DrawRows(view);
        }

        ImGui.End();
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
            _trail.Clear();
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
        _structNames ??= ["(no names)", .. _inspector.KnownStructures];
        ImGui.SetNextItemWidth(220f);
        ImGui.Combo("known layout", ref _structIndex, _structNames, _structNames.Length);

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
    }

    private void DrawTrail()
    {
        if (_trail.Count == 0)
        {
            return;
        }

        if (ImGui.SmallButton("back"))
        {
            (ulong address, int _) = _trail[^1];
            _trail.RemoveAt(_trail.Count - 1);
            _address = address;
            _root = StructureRoot.Custom;
            _litAt.Clear();
        }

        ImGui.SameLine();
        ImGui.TextColored(NameText, Path());
        ImGui.SameLine();
        if (ImGui.SmallButton("copy"))
        {
            ImGui.SetClipboardText(Path());
        }
    }

    /// <summary>The way here, written the way it would be written down.</summary>
    private string Path()
    {
        string route = _trail[0].Offset < 0 && _root != StructureRoot.Custom
            ? _root.ToString()
            : $"0x{_trail[0].Address:X}";

        foreach ((ulong _, int offset) in _trail.Skip(1).Append((0UL, _peekOffset)))
        {
            if (offset >= 0)
            {
                route += $" -> +0x{offset:X}";
            }
        }

        return route;
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

        if (!ImGui.BeginTable("rows", 7, Flags))
        {
            return;
        }

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("offset", ImGuiTableColumnFlags.WidthFixed, 66f);
        ImGui.TableSetupColumn("name", ImGuiTableColumnFlags.WidthFixed, 150f);
        ImGui.TableSetupColumn("guess", ImGuiTableColumnFlags.WidthFixed, 60f);
        ImGui.TableSetupColumn("hex", ImGuiTableColumnFlags.WidthFixed, 150f);
        ImGui.TableSetupColumn("whole", ImGuiTableColumnFlags.WidthFixed, 130f);
        ImGui.TableSetupColumn("decimal", ImGuiTableColumnFlags.WidthFixed, 140f);
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

        ImGui.EndTable();
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
            _trail.Add((_address, viaOffset));
        }
        else
        {
            _trail.Clear();
        }

        _address = address;
        _root = StructureRoot.Custom;
        _typedAddress = address.ToString("X", CultureInfo.InvariantCulture);
        _peekOffset = -1;
        _litAt.Clear();

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
