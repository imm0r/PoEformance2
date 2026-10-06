using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Game.Components;

/// <summary>One named state of a StateMachine component and its current value.</summary>
public readonly record struct MachineState(string Name, long Value);

/// <summary>
/// Reads a StateMachine component: its named states, and the objects listening to it.
/// </summary>
/// <remarks>
/// THE NAMES AND THE VALUES LIVE APART, which is the one thing worth knowing about this
/// component. The values are a plain vector of i64, one per state; the names are an array of
/// std::string hanging off a separate pointer, StateNameSize apart, indexed by the state's
/// number. Ported from the AHK tool's decoder, which is where that layout was measured.
///
/// Names are CACHED by the array they came from: the array belongs to the machine's definition,
/// shared by every entity of the same kind, so a monolith's states are read as strings once per
/// session rather than once per monolith per scan.
///
/// The listener vector is the other half, and the reason this reader exists at all: the
/// RuneStation behind a Runecraft monolith is reachable from nowhere else. See the schema's
/// StateMachine.ListenerVec.
/// </remarks>
public sealed class StateMachineReader
{
    private readonly IMemoryReader _reader;
    private readonly int _statesPtr;
    private readonly int _valuesFirst;
    private readonly int _valuesLast;
    private readonly int _listeners;
    private readonly int _namesBase;
    private readonly int _nameSize;
    private readonly int _mostStates;
    private readonly int _mostListeners;

    /// <summary>State names by (names array, index). Static per machine definition.</summary>
    private readonly Dictionary<(ulong Names, int Index), string> _names = [];

    public StateMachineReader(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;

        StructDef machine = schema.Structs["StateMachine"];
        _statesPtr = machine.OffsetOf("StatesPtr");
        _valuesFirst = machine.OffsetOf("StatesValuesFirst");
        _valuesLast = machine.OffsetOf("StatesValuesLast");
        _listeners = machine.OffsetOf("ListenerVec");
        _namesBase = (int)machine.Constants["StateNamesBase"];
        _nameSize = (int)machine.Constants["StateNameSize"];
        _mostStates = (int)machine.Constants["MostStates"];
        _mostListeners = (int)machine.Constants["MostListeners"];
    }

    /// <summary>Every state the machine has, named where the names read. Empty when the component does not.</summary>
    public IReadOnlyList<MachineState> Read(ulong component)
    {
        if (!MemoryReaderExtensions.IsPlausiblePointer(component))
        {
            return [];
        }

        ulong first = _reader.ReadPointer(component + (ulong)_valuesFirst);
        ulong last = _reader.Read<ulong>(component + (ulong)_valuesLast);
        if (first == 0 || last < first || (last - first) % 8 != 0)
        {
            return [];
        }

        int count = (int)Math.Min((last - first) / 8, (ulong)_mostStates);
        if (count == 0)
        {
            return [];
        }

        // The names array, found through the definition pointer. A machine whose definition
        // does not read still reports its VALUES, under index names, because a value with a
        // number is more than a failed read and the caller may know the index.
        ulong definition = _reader.ReadPointer(component + (ulong)_statesPtr);
        ulong names = definition != 0 ? _reader.ReadPointer(definition + (ulong)_namesBase) : 0;

        var states = new MachineState[count];
        for (int i = 0; i < count; i++)
        {
            long value = _reader.Read<long>(first + (ulong)(i * 8));
            states[i] = new MachineState(NameOf(names, i), value);
        }

        return states;
    }

    /// <summary>The value of one named state, or null when the machine has no state of that name.</summary>
    public long? ValueOf(ulong component, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (MachineState state in Read(component))
        {
            if (string.Equals(state.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return state.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// The objects listening to this machine, as the nodes the vector holds - one pointer each.
    /// </summary>
    /// <remarks>
    /// Nodes, not the objects themselves: what a node's first qword points INTO depends on the
    /// listener's class, and only the caller knows which class it is looking for. See
    /// MonolithReader, which subtracts the RuneStation's own sub-object offset.
    /// </remarks>
    public IReadOnlyList<ulong> Listeners(ulong component)
    {
        if (!MemoryReaderExtensions.IsPlausiblePointer(component))
        {
            return [];
        }

        ulong first = _reader.ReadPointer(component + (ulong)_listeners);
        ulong last = _reader.Read<ulong>(component + (ulong)_listeners + 8);
        if (first == 0 || last < first || (last - first) % 8 != 0)
        {
            return [];
        }

        int count = (int)Math.Min((last - first) / 8, (ulong)_mostListeners);
        var nodes = new List<ulong>(count);
        for (int i = 0; i < count; i++)
        {
            ulong node = _reader.ReadPointer(first + (ulong)(i * 8));
            if (node != 0)
            {
                nodes.Add(node);
            }
        }

        return nodes;
    }

    private string NameOf(ulong names, int index)
    {
        if (names == 0)
        {
            return "state_" + index;
        }

        if (_names.TryGetValue((names, index), out string? known))
        {
            return known;
        }

        string read = _reader.ReadStdString(names + (ulong)(index * _nameSize), 64);
        string name = read.Length > 0 && IsPlausibleName(read) ? read : "state_" + index;

        // Only a real name is remembered: a definition that was mid-rebuild when it was read
        // would otherwise pin "state_3" on every later read of a machine that has a name there.
        if (name.Length > 0 && !name.StartsWith("state_", StringComparison.Ordinal))
        {
            _names[(names, index)] = name;
        }

        return name;
    }

    /// <summary>A state name is a short run of letters, digits and underscores - "is_rerolled", "sockets".</summary>
    private static bool IsPlausibleName(string name)
    {
        foreach (char c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        return true;
    }
}
