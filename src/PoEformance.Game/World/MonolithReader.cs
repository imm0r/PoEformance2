using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Components;
using PoEformance.Game.Ui;

namespace PoEformance.Game.World;

/// <summary>The RuneStation behind one Runecraft monolith, as read this scan.</summary>
/// <param name="Address">The station, or 0 when none was found - then <paramref name="Why"/> says where the walk stopped.</param>
/// <param name="HoleCount">N, the monolith's holes. 0 when it did not read.</param>
/// <param name="AnchorRune">The pre-placed rune as an Expedition2Runes row index, or -1.</param>
/// <param name="AnchorHole">Which hole the anchor sits in, 0-based, or -1.</param>
/// <param name="Anchorless">
/// True when the station carries NO anchor at all - the "unique" monolith, which offers every
/// recipe that fits its holes. Not the same as an anchor that failed to read.
/// </param>
/// <param name="GlowSockets">The socket positions the game frames in gold - what a chosen recipe propagates from.</param>
/// <param name="RecipeMode">The server's monolith type: 1 dig, 3 unique, 0 the standalone one. -1 unread.</param>
/// <param name="Empowered">Whether a Power rune is already in effect over this station.</param>
/// <param name="SelectedRecipeId">The committed recipe's id, or empty while nothing is chosen.</param>
/// <param name="PanelOpen">Whether the Runeshape Combinations panel is open on THIS monolith.</param>
/// <param name="Why">What did not read, in words. Empty when everything did.</param>
public sealed record MonolithStation(
    ulong Address,
    int HoleCount,
    int AnchorRune,
    int AnchorHole,
    bool Anchorless,
    IReadOnlyList<int> GlowSockets,
    int RecipeMode,
    bool Empowered,
    string SelectedRecipeId,
    bool PanelOpen,
    string Why)
{
    /// <summary>Whether a station was found at all.</summary>
    public bool Resolved => Address != 0;

    /// <summary>Whether a recipe is committed on this monolith.</summary>
    public bool Committed => SelectedRecipeId.Length > 0;

    /// <summary>Whether the game draws a gold frame here - the modes whose panel rows get the socket vector.</summary>
    public bool FramesASocket => RecipeMode is 1 or 2;

    public static MonolithStation None(string why) => new(0, 0, -1, -1, false, [], -1, false, string.Empty, false, why);
}

/// <summary>What the monolith device's own state machine says.</summary>
/// <param name="Sockets">The 'sockets' state - the hole count, capped at six by the game.</param>
/// <param name="Activated">The 'activated' state: 0 dormant, 1 available, 7 or 8 once collected.</param>
/// <param name="Rerolled">The 'is_rerolled' state: sealed by a currency reroll.</param>
/// <param name="Summary">Every state as "name=value", for the readout.</param>
public readonly record struct MonolithStates(int Sockets, int Activated, bool Rerolled, string Summary)
{
    /// <summary>What the plugin took as "collected" before the map icon was known to say so.</summary>
    public bool LooksCollected => Activated >= 7;
}

/// <summary>
/// Reads the station behind a Runecraft monolith, and the device's own states.
/// </summary>
/// <remarks>
/// WHAT A MONOLITH IS IN MEMORY: an ordinary entity (the Expedition2Encounter device, with a
/// MinimapIcon the game flips from Expedition2RemnantActive to Expedition2RemnantDeactivated when
/// it is collected) whose interesting half - what rune is socketed, how many holes, what is
/// chosen - lives in a RuneStation that is NOT a component. The station registers itself as a
/// LISTENER on the device's StateMachine, and that registration is the way back to it: each
/// listener node points into the station at a fixed offset, and the station names its device at
/// another. That route, and every field it leads to, is yokkenUA's RunecraftHelper's (0.5.5),
/// Ghidra-backed and live-verified there and not yet here - see the schema's RuneStation.
///
/// EVERYTHING IS CHECKED BY CONTENT where the plugin found that a plausible pointer lies: the
/// anchor index must land on a rune row stride inside the table, the hole count inside 1..16,
/// the chosen recipe's id must read as "&lt;digits&gt;Slot...", the panel listener must point
/// into the game module. A drifted offset then reads as a sentence on the Runecraft tab rather
/// than as a monolith that offers the wrong recipes.
/// </remarks>
public sealed class MonolithReader
{
    /// <summary>The fragment every Runecraft monolith device's path carries.</summary>
    public const string DevicePath = "Expedition2Encounter";

    /// <summary>The map icon's stem: Active while there, Deactivated once collected.</summary>
    public const string IconStem = "Expedition2Remnant";

    /// <summary>The icon suffix the game puts on a collected monolith.</summary>
    public const string CollectedIcon = "Expedition2RemnantDeactivated";

    private const int MostIdChars = 64;

    private readonly IMemoryReader _reader;
    private readonly StateMachineReader _machines;

    private readonly int _owner;
    private readonly int _anchorRow;
    private readonly int _anchorHolder;
    private readonly int _holes;
    private readonly int _anchorHole;
    private readonly int _glow;
    private readonly int _mode;
    private readonly int _empowered;
    private readonly int _selected;
    private readonly int _panel;
    private readonly int _listenerSub;
    private readonly int _holderTable;
    private readonly int _mostGlow;
    private readonly int _runeRowSize;
    private readonly int _runeCount;
    private readonly int _recipeId;
    private readonly int _areaTags;

    public MonolithReader(IMemoryReader reader, OffsetSchema schema, StateMachineReader machines)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(machines);
        _reader = reader;
        _machines = machines;

        StructDef station = schema.Structs["RuneStation"];
        _owner = station.OffsetOf("OwnerEntityPtr");
        _anchorRow = station.OffsetOf("AnchorRuneRowPtr");
        _anchorHolder = station.OffsetOf("AnchorHolderPtr");
        _holes = station.OffsetOf("HoleCount");
        _anchorHole = station.OffsetOf("AnchorHoleIndex");
        _glow = station.OffsetOf("GlowSockets");
        _mode = station.OffsetOf("RecipeMode");
        _empowered = station.OffsetOf("RunesEmpowered");
        _selected = station.OffsetOf("SelectedRecipeRowPtr");
        _panel = station.OffsetOf("PanelListenerPtr");
        _listenerSub = (int)station.Constants["ListenerSubOffset"];
        _holderTable = (int)station.Constants["HolderTablePtr"];
        _mostGlow = (int)station.Constants["MostGlowSockets"];

        StructDef rune = schema.Structs["Expedition2RunesRow"];
        _runeRowSize = (int)rune.Constants["ComputedRowSize"];
        _runeCount = (int)rune.Constants["RuneCount"];

        _recipeId = schema.Structs["Expedition2RecipesRow"].OffsetOf("IdPtr");
        _areaTags = schema.Structs["AreaInstance"].OffsetOf("ContentTags");
    }

    /// <summary>
    /// Walks the device's state machine listeners to the station that names the device.
    /// </summary>
    /// <returns>The station, or 0 - and then <paramref name="why"/> says which step failed.</returns>
    public ulong FindStation(ulong stateMachine, ulong device, out string why)
    {
        if (stateMachine == 0)
        {
            why = "the device has no StateMachine component";
            return 0;
        }

        IReadOnlyList<ulong> nodes = _machines.Listeners(stateMachine);
        if (nodes.Count == 0)
        {
            why = "the StateMachine's listener vector is empty or unreadable";
            return 0;
        }

        foreach (ulong node in nodes)
        {
            ulong inside = _reader.ReadPointer(node);
            if (inside <= (ulong)_listenerSub)
            {
                continue;
            }

            ulong candidate = inside - (ulong)_listenerSub;
            if (_reader.ReadPointer(candidate + (ulong)_owner) == device)
            {
                why = string.Empty;
                return candidate;
            }
        }

        why = $"no listener named the device ({nodes.Count} checked)";
        return 0;
    }

    /// <summary>Reads the station's fields, each guarded by what it should look like.</summary>
    public MonolithStation Read(ulong station)
    {
        if (!MemoryReaderExtensions.IsPlausiblePointer(station))
        {
            return MonolithStation.None("no station");
        }

        var why = new List<string>();

        int holes = _reader.Read<int>(station + (ulong)_holes);
        if (holes is < 1 or > 16)
        {
            why.Add($"hole count {holes} is outside 1..16");
            holes = 0;
        }

        int anchorHole = _reader.Read<int>(station + (ulong)_anchorHole);
        if (anchorHole is < 0 or > 15)
        {
            anchorHole = -1;
        }

        // The anchor: null is an ANSWER (the anchor-less unique monolith), a pointer that does
        // not land on a row stride is a failure. The table base is per area, so it is re-read
        // every time rather than cached.
        ulong anchorRow = _reader.ReadPointer(station + (ulong)_anchorRow);
        bool anchorless = anchorRow == 0;
        int anchorRune = -1;
        if (!anchorless)
        {
            anchorRune = RuneIndexOf(station, anchorRow, why);
        }

        int mode = _reader.Read<int>(station + (ulong)_mode);
        if (mode is < 0 or > 15)
        {
            why.Add($"recipe mode {mode} is not one the game uses");
            mode = -1;
        }

        bool empowered = _reader.Read<byte>(station + (ulong)_empowered) != 0;

        string selected = string.Empty;
        ulong selectedRow = _reader.ReadPointer(station + (ulong)_selected);
        if (selectedRow != 0)
        {
            selected = _reader.ReadUnicodeString(_reader.ReadPointer(selectedRow + (ulong)_recipeId), MostIdChars);
            if (!RunecraftPanelReader.LooksLikeRecipeId(selected))
            {
                why.Add($"the chosen recipe's id reads \"{selected}\", not as <digits>Slot...");
                selected = string.Empty;
            }
        }

        ulong panel = _reader.Read<ulong>(station + (ulong)_panel);
        bool panelOpen = panel != 0 && InModule(panel);

        return new MonolithStation(
            station, holes, anchorRune, anchorHole, anchorless,
            GlowSocketsOf(station), mode, empowered, selected, panelOpen,
            string.Join("; ", why));
    }

    /// <summary>The device's own states: sockets, activated, is_rerolled.</summary>
    public MonolithStates States(ulong stateMachine)
    {
        IReadOnlyList<MachineState> states = _machines.Read(stateMachine);
        int sockets = -1;
        int activated = -1;
        var rerolled = false;
        var summary = new System.Text.StringBuilder();
        foreach (MachineState state in states)
        {
            if (summary.Length > 0)
            {
                summary.Append(", ");
            }

            summary.Append(state.Name).Append('=').Append(state.Value);
            if (state.Name.Equals("sockets", StringComparison.OrdinalIgnoreCase))
            {
                sockets = (int)state.Value;
            }
            else if (state.Name.Equals("activated", StringComparison.OrdinalIgnoreCase))
            {
                activated = (int)state.Value;
            }
            else if (state.Name.Equals("is_rerolled", StringComparison.OrdinalIgnoreCase))
            {
                rerolled = state.Value != 0;
            }
        }

        return new MonolithStates(sockets, activated, rerolled, summary.ToString());
    }

    /// <summary>
    /// The area's content tags, or null when the vector did not read as one.
    /// </summary>
    /// <remarks>
    /// NULL SWITCHES THE GATE OFF, deliberately: the gate drops one recipe in the whole
    /// catalogue, and a wrong offset here must cost that one over-offer and nothing more. An
    /// empty vector is a real answer - a tagless area, where the gate applies and drops the
    /// gated recipe - and comes back as an empty set.
    /// </remarks>
    public IReadOnlySet<int>? AreaTags(ulong areaInstance)
    {
        if (!MemoryReaderExtensions.IsPlausiblePointer(areaInstance))
        {
            return null;
        }

        // TryRead, not Read: a vector that could not be read at all must come back as null, not
        // as the empty set a pair of zeros would stand for - the two mean opposite things here.
        if (!_reader.TryRead(areaInstance + (ulong)_areaTags, out ulong first)
            || !_reader.TryRead(areaInstance + (ulong)_areaTags + 8, out ulong last))
        {
            return null;
        }

        if (first == 0 && last == 0)
        {
            return new HashSet<int>();
        }

        if (!MemoryReaderExtensions.IsPlausiblePointer(first) || last < first
            || (last - first) % 4 != 0 || last - first > 4096)
        {
            return null;
        }

        int count = (int)((last - first) / 4);
        var tags = new HashSet<int>(count);
        for (int i = 0; i < count; i++)
        {
            tags.Add(_reader.Read<int>(first + (ulong)(i * 4)));
        }

        return tags;
    }

    /// <summary>(row - base) / stride, with every hop and the arithmetic checked.</summary>
    private int RuneIndexOf(ulong station, ulong anchorRow, List<string> why)
    {
        ulong holder = _reader.ReadPointer(station + (ulong)_anchorHolder);
        if (holder == 0)
        {
            why.Add("anchor holder null");
            return -1;
        }

        ulong table = _reader.ReadPointer(holder + (ulong)_holderTable);
        ulong tableBase = table != 0 ? _reader.ReadPointer(table) : 0;
        if (tableBase == 0)
        {
            why.Add("rune table base null");
            return -1;
        }

        if (anchorRow < tableBase)
        {
            why.Add("anchor row before the rune table");
            return -1;
        }

        ulong delta = anchorRow - tableBase;
        if (delta % (ulong)_runeRowSize != 0)
        {
            why.Add($"anchor row is 0x{delta:X} past the table, not a row stride");
            return -1;
        }

        ulong index = delta / (ulong)_runeRowSize;
        if (index >= (ulong)_runeCount)
        {
            why.Add($"anchor rune index {index} is past the {_runeCount} runes");
            return -1;
        }

        return (int)index;
    }

    private IReadOnlyList<int> GlowSocketsOf(ulong station)
    {
        ulong first = _reader.ReadPointer(station + (ulong)_glow);
        ulong last = _reader.Read<ulong>(station + (ulong)_glow + 8);
        if (first == 0 || last <= first || (last - first) % 4 != 0)
        {
            return [];
        }

        int count = (int)((last - first) / 4);
        if (count > _mostGlow)
        {
            return [];
        }

        var sockets = new List<int>(count);
        for (int i = 0; i < count; i++)
        {
            int socket = _reader.Read<int>(first + (ulong)(i * 4));
            if (socket is >= 0 and < 16 && !sockets.Contains(socket))
            {
                sockets.Add(socket);
            }
        }

        return sockets;
    }

    /// <summary>
    /// Whether a pointer lands inside the game module - a vtable, not a heap object.
    /// </summary>
    /// <remarks>
    /// A reader that knows no module (a replay with none recorded, a fake) accepts any
    /// plausible pointer, so the recordings keep working; the live reader asks the real range.
    /// </remarks>
    private bool InModule(ulong pointer)
        => _reader.ModuleSize == 0
            ? MemoryReaderExtensions.IsPlausiblePointer(pointer)
            : pointer >= _reader.ModuleBase && pointer < _reader.ModuleBase + _reader.ModuleSize;
}
