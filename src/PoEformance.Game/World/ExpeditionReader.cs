using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Components;
using PoEformance.Game.Entities;
using PoEformance.Game.Ui;

namespace PoEformance.Game.World;

/// <summary>The charge counts as the game holds them.</summary>
/// <param name="Total">The map's charge total - the maximum. 0 when it did not read inside 1..64.</param>
/// <param name="Placed">How many are down, from the controller's own vector.</param>
/// <param name="Source">Which route found the controller, for the readout.</param>
public readonly record struct ExpeditionCounts(int Total, int Placed, string Source);

/// <summary>
/// Reads what an expedition's route planner needs off the game: the map's modifiers, the
/// charge counts, the detonator's state, a blocker's state, a relic's mods.
/// </summary>
/// <remarks>
/// EVERY OFFSET HERE IS yokkenUA's RunecraftHelper's (0.5.5), and none has been seen by this
/// tool - see the schema's ExpeditionController, ExpeditionPlacementState, ExpeditionHud and
/// AreaInstance.MapMods for the provenance and the drift each has already been through. Each
/// read is guarded by content and fails to "unknown" rather than to a number: a controller is
/// believed only when its type id and its back-pointer to the ServerData both hold, a total
/// only inside 1..64, a map modifier only when the vector reads as one.
///
/// THE CONTROLLER IS A SERVERDATA FIELD, not a UI object, and that is the plugin's hard-won
/// finding: its first route went through the counter widget, which exists only while the
/// player stands at the detonator and whose child path drifts between map loads, so the
/// planner kept falling back to a guessed budget. The ServerData slot is range-independent.
/// The widget stays as the last resort, and its remaining-count text as the fallback total.
/// </remarks>
public sealed class ExpeditionReader
{
    /// <summary>The chain's start.</summary>
    public const string DetonatorPath = "Metadata/MiscellaneousObjects/Expedition/ExpeditionDetonator";

    /// <summary>A placed charge.</summary>
    public const string ExplosivePath = "Metadata/MiscellaneousObjects/Expedition/ExpeditionExplosive";

    /// <summary>A reward marker - the flag over a buried chest. Its MinimapIcon names the reward type.</summary>
    public const string MarkerPath = "Metadata/MiscellaneousObjects/Expedition/ExpeditionMarker";

    /// <summary>A field relic ("remnant"), whose mods apply when the blast reaches it.</summary>
    public const string RelicPath = "Metadata/MiscellaneousObjects/Expedition/ExpeditionRelic";

    /// <summary>The Kalguur Sentinel - a buff that empowers the logbook-tier markers once detonated.</summary>
    public const string SentinelPath = "Sentinel/SentinelRandomEncounterObject";

    /// <summary>The path fragment of the one blocker verified as an explosive-chain gate.</summary>
    /// <remarks>
    /// Restricted to this one on purpose: many tilesets carry TriggerableBlockage on terrain that
    /// is NOT a gate (RootBlocker and the like), and treating those as walls makes the planner
    /// see a maze the blast chain never opens.
    /// </remarks>
    public const string GatePath = "DevourerSegment";

    private const int MostModLists = 5;
    private const int MostModsPerList = 64;

    private readonly IMemoryReader _reader;
    private readonly EntityReader _entities;
    private readonly StateMachineReader _machines;
    private readonly UiElementReader _elements;

    private readonly int _mapMods;
    private readonly int _pairSize;
    private readonly int _mostPairs;
    private readonly int _radiusKey;
    private readonly int _placementKey;

    private readonly int _controllerSlot;
    private readonly int _controllerSlotWas;
    private readonly int _scanStart;
    private readonly int _scanEnd;
    private readonly int _typeIdAt;
    private readonly int _typeId;
    private readonly int _managerAt;
    private readonly int _placementAt;
    private readonly int _placedVec;
    private readonly int _placedEntry;
    private readonly int _totalAt;
    private readonly int _mostCharges;

    private readonly int[] _hudPath;
    private readonly int _counterDepth;
    private readonly int _hudController;
    private readonly int _mostRemaining;
    private readonly int _text;

    private readonly int _blocked;
    private readonly int _allMods;
    private readonly int _vectorSize;
    private readonly int _modEntrySize;
    private readonly int _modRow;

    public ExpeditionReader(IMemoryReader reader, OffsetSchema schema, UiElementReader elements)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(elements);
        _reader = reader;
        _entities = new EntityReader(reader, schema);
        _machines = new StateMachineReader(reader, schema);
        _elements = elements;

        _mapMods = schema.Structs["AreaInstance"].OffsetOf("MapMods");
        StructDef stats = schema.Structs["ExpeditionStats"];
        _pairSize = (int)stats.Constants["PairSize"];
        _mostPairs = (int)stats.Constants["MostPairs"];
        _radiusKey = (int)stats.Constants["MapExplosionRadiusPct"];
        _placementKey = (int)stats.Constants["MapPlacementDistancePct"];

        StructDef server = schema.Structs["ServerData"];
        _controllerSlot = server.OffsetOf("ExpeditionControllerPtr");
        _controllerSlotWas = (int)server.Constants["ExpeditionControllerPtrWas"];
        _scanStart = (int)server.Constants["ControllerScanStart"];
        _scanEnd = (int)server.Constants["ControllerScanEnd"];

        StructDef controller = schema.Structs["ExpeditionController"];
        _typeIdAt = controller.OffsetOf("TypeId");
        _typeId = (int)controller.Constants["TypeId"];
        _managerAt = controller.OffsetOf("ManagerPtr");
        _placementAt = controller.OffsetOf("PlacementStatePtr");

        StructDef placement = schema.Structs["ExpeditionPlacementState"];
        _placedVec = placement.OffsetOf("PlacedVec");
        _placedEntry = (int)placement.Constants["PlacedEntrySize"];
        _totalAt = placement.OffsetOf("TotalCharges");
        _mostCharges = (int)placement.Constants["MostCharges"];

        StructDef hud = schema.Structs["ExpeditionHud"];
        _hudPath =
        [
            (int)hud.Constants["Child0"], (int)hud.Constants["Child1"], (int)hud.Constants["Child2"], (int)hud.Constants["Child3"],
        ];
        _counterDepth = (int)hud.Constants["CounterDepth"];
        _hudController = hud.OffsetOf("ControllerPtr");
        _mostRemaining = (int)hud.Constants["MostRemaining"];
        _text = schema.Structs["UiElementBase"].OffsetOf("TextPtr");

        _blocked = schema.Structs["TriggerableBlockage"].OffsetOf("IsBlocked");
        _allMods = schema.Structs["ObjectMagicProperties"].OffsetOf("AllMods");
        _vectorSize = (int)schema.Structs["StdVector"].Constants["StructSize"];
        StructDef mod = schema.Structs["ModArray"];
        _modEntrySize = (int)mod.Constants["EntrySize"];
        _modRow = mod.OffsetOf("ModsPtr");
    }

    /// <summary>
    /// The area's expedition modifiers: placement distance and explosion radius, each in
    /// percent. Null when the vector did not read - the caller keeps what it had.
    /// </summary>
    /// <remarks>
    /// Null and (0, 0) are different answers on purpose: a genuinely empty vector means no
    /// modifier, while an unreadable one says nothing, and a wrong offset reads as the latter.
    /// The plugin's own drift (0x158 -> 0x150) was silent precisely because the two were one.
    /// </remarks>
    public (int Placement, int Radius)? MapMods(ulong areaInstance)
    {
        if (areaInstance == 0)
        {
            return null;
        }

        if (!_reader.TryRead(areaInstance + (ulong)_mapMods, out ulong begin)
            || !_reader.TryRead(areaInstance + (ulong)_mapMods + 8, out ulong end))
        {
            return null;
        }

        if (begin == 0 && end == 0)
        {
            return (0, 0);
        }

        if (!MemoryReaderExtensions.IsPlausiblePointer(begin) || end < begin)
        {
            return null;
        }

        ulong span = end - begin;
        if (span == 0)
        {
            return (0, 0);
        }

        if (span % (ulong)_pairSize != 0 || span > (ulong)(_pairSize * _mostPairs))
        {
            return null;
        }

        var bytes = new byte[span];
        if (!_reader.TryRead(begin, bytes))
        {
            return null;
        }

        int placement = 0;
        int radius = 0;
        for (int at = 0; at + _pairSize <= bytes.Length; at += _pairSize)
        {
            int key = BitConverter.ToInt32(bytes, at);
            int value = BitConverter.ToInt32(bytes, at + 4);
            if (key == _placementKey)
            {
                placement += value;
            }
            else if (key == _radiusKey)
            {
                radius += value;
            }
        }

        return (placement, radius);
    }

    /// <summary>
    /// The charge counts off the controller, found through the ServerData slot, the old slot,
    /// a scan of the window between, or the counter widget - or null when none holds one.
    /// </summary>
    /// <param name="serverData">What LocalPlayerStruct.ServerDataPtr holds. 0 skips the ServerData routes.</param>
    /// <param name="uiRoot">The UI root, for the widget fallback. 0 skips it.</param>
    public ExpeditionCounts? Counts(ulong serverData, ulong uiRoot)
    {
        if (serverData != 0)
        {
            ulong at = _reader.ReadPointer(serverData + (ulong)_controllerSlot);
            if (LooksLikeController(at, serverData))
            {
                return Read(at, "ServerData slot");
            }

            at = _reader.ReadPointer(serverData + (ulong)_controllerSlotWas);
            if (LooksLikeController(at, serverData))
            {
                return Read(at, "ServerData old slot");
            }

            // The fingerprint is specific enough to run unconditionally, so the next time the
            // slot moves this self-heals instead of leaving the planner on a guessed budget.
            for (int offset = _scanStart; offset <= _scanEnd; offset += 8)
            {
                at = _reader.ReadPointer(serverData + (ulong)offset);
                if (LooksLikeController(at, serverData))
                {
                    return Read(at, $"ServerData scan +0x{offset:X}");
                }
            }
        }

        if (uiRoot != 0)
        {
            ulong widget = Widget(uiRoot);
            if (widget != 0)
            {
                ulong at = _reader.ReadPointer(widget + (ulong)_hudController);
                if (LooksLikeController(at, serverData))
                {
                    return Read(at, "counter widget");
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The charges REMAINING, as the counter widget's text says, or null when the widget is not
    /// there - which is whenever the player is not standing at the detonator.
    /// </summary>
    public int? HudRemaining(ulong uiRoot)
    {
        ulong leaf = Widget(uiRoot);
        for (int i = 0; i < _counterDepth && leaf != 0; i++)
        {
            leaf = _elements.Child(leaf, 0);
        }

        if (leaf == 0)
        {
            return null;
        }

        string text = _reader.ReadStdWString(leaf + (ulong)_text, 32);
        var value = 0;
        var any = false;
        foreach (char c in text)
        {
            if (char.IsAsciiDigit(c))
            {
                value = (value * 10) + (c - '0');
                any = true;
            }
            else if (any)
            {
                break;
            }
        }

        return any && value <= _mostRemaining ? value : null;
    }

    /// <summary>
    /// Whether the detonator has been pressed - the dig is under way and the plan is frozen.
    /// </summary>
    /// <remarks>
    /// The 'activated' state alone: live on 0.5.4 HF3 the triple [activated, light_colour,
    /// third] read [0,0,0] idle, [0,1,0] with a charge placed and [1,2,1] after the press, so
    /// light_colour flips on mere placement and only this one says the dig began.
    /// </remarks>
    public bool DetonatorActivated(ulong entity)
    {
        Entity? read = _entities.Read(entity);
        ulong machine = read?.Component("StateMachine") ?? 0;
        return machine != 0 && _machines.ValueOf(machine, "activated") is { } value && value != 0;
    }

    /// <summary>Whether a blocker is shut: null when it carries no TriggerableBlockage or it did not read.</summary>
    public bool? IsBlocked(ulong entity)
    {
        Entity? read = _entities.Read(entity);
        ulong blockage = read?.Component("TriggerableBlockage") ?? 0;
        if (blockage == 0 || !_reader.TryRead(blockage + (ulong)_blocked, out byte flag))
        {
            return null;
        }

        return flag != 0;
    }

    /// <summary>The ids of the mods on an entity's ObjectMagicProperties - a relic's upsides and downsides.</summary>
    public IReadOnlyList<string> ModIds(ulong entity)
    {
        Entity? read = _entities.Read(entity);
        ulong magic = read?.Component("ObjectMagicProperties") ?? 0;
        if (magic == 0)
        {
            return [];
        }

        var ids = new List<string>();
        for (int list = 0; list < MostModLists; list++)
        {
            ulong vector = magic + (ulong)_allMods + (ulong)(list * _vectorSize);
            ulong first = _reader.ReadPointer(vector);
            ulong last = _reader.ReadPointer(vector + 8);
            if (!MemoryReaderExtensions.IsPlausiblePointer(first) || last <= first)
            {
                continue;
            }

            ulong bytes = last - first;
            if (bytes % (ulong)_modEntrySize != 0 || bytes > (ulong)(_modEntrySize * MostModsPerList))
            {
                continue;
            }

            for (ulong i = 0; i < bytes / (ulong)_modEntrySize; i++)
            {
                ulong row = _reader.ReadPointer(first + (i * (ulong)_modEntrySize) + (ulong)_modRow);
                if (!MemoryReaderExtensions.IsPlausiblePointer(row))
                {
                    continue;
                }

                // The dat row's first field is a pointer to the mod's id string.
                ulong text = _reader.ReadPointer(row);
                string id = MemoryReaderExtensions.IsPlausiblePointer(text) ? _reader.ReadUnicodeString(text, 128) : string.Empty;
                if (id.Length > 0)
                {
                    ids.Add(id);
                }
            }
        }

        return ids;
    }

    /// <summary>The counter widget, or 0 - the child path from the root.</summary>
    private ulong Widget(ulong uiRoot)
    {
        ulong node = uiRoot;
        foreach (int index in _hudPath)
        {
            if (node == 0)
            {
                return 0;
            }

            node = _elements.Child(node, index);
        }

        return node;
    }

    /// <summary>The structural fingerprint: the type id, and the back-pointer to THIS ServerData.</summary>
    private bool LooksLikeController(ulong at, ulong serverData)
    {
        if (!MemoryReaderExtensions.IsPlausiblePointer(at))
        {
            return false;
        }

        if (!_reader.TryRead(at + (ulong)_typeIdAt, out int typeId) || typeId != _typeId)
        {
            return false;
        }

        // The widget route with no ServerData to test against: a vtable is all there is.
        if (serverData == 0)
        {
            return MemoryReaderExtensions.IsPlausiblePointer(_reader.ReadPointer(at));
        }

        return _reader.ReadPointer(at + (ulong)_managerAt) == serverData;
    }

    private ExpeditionCounts? Read(ulong controller, string source)
    {
        ulong state = _reader.ReadPointer(controller + (ulong)_placementAt);
        if (!MemoryReaderExtensions.IsPlausiblePointer(state)
            || !MemoryReaderExtensions.IsPlausiblePointer(_reader.ReadPointer(state)))
        {
            return null;
        }

        var total = 0;
        if (_reader.TryRead(state + (ulong)_totalAt, out byte raw) && raw >= 1 && raw <= _mostCharges)
        {
            total = raw;
        }

        var placed = 0;
        ulong first = _reader.ReadPointer(state + (ulong)_placedVec);
        ulong last = _reader.ReadPointer(state + (ulong)_placedVec + 8);
        if (first != 0 || last != 0)
        {
            if (!MemoryReaderExtensions.IsPlausiblePointer(first) || last < first)
            {
                return null;
            }

            ulong span = last - first;
            if (span % (ulong)_placedEntry != 0 || span > (ulong)(_placedEntry * _mostCharges))
            {
                return null;
            }

            placed = (int)(span / (ulong)_placedEntry);
        }

        return new ExpeditionCounts(total, placed, source);
    }
}
