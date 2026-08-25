using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Game.Components;

/// <summary>One buff or debuff currently on the player.</summary>
/// <param name="Name">
/// The ENGINE identifier - "fire_wall" - and the only name anything matches on.
/// </param>
/// <param name="FlaskSlot">Belt slot 1-5 for a flask buff, 0 for anything else.</param>
/// <param name="DisplayName">
/// The readable name from the same row - "Flame Wall" - or empty when it could not be read.
/// </param>
/// <param name="Description">The buff's own description text, or empty.</param>
/// <remarks>
/// The two names are carried separately and never conflated. An id is what a rule matches,
/// because it is the same on every client; a display name is what a person recognises, and
/// nobody looking at "Flame Wall" on their own screen would guess "fire_wall". Offering only
/// the first is what made buff rules guesswork.
/// </remarks>
public readonly record struct ActiveBuff(
    string Name,
    float TimeLeft,
    float TotalTime,
    int Charges,
    int FlaskSlot,
    bool IsFlask,
    string DisplayName = "",
    string Description = "");

/// <summary>The player's active buffs and debuffs.</summary>
public sealed class ActiveBuffs
{
    public static ActiveBuffs None { get; } = new([]);

    public IReadOnlyList<ActiveBuff> All { get; }

    public ActiveBuffs(IReadOnlyList<ActiveBuff> all)
    {
        ArgumentNullException.ThrowIfNull(all);
        All = all;
    }

    /// <summary>True while the flask in that belt slot is still doing its job.</summary>
    /// <remarks>
    /// The reason flask automation needs buffs at all: re-using a flask whose effect is
    /// still running spends a charge for nothing. A timer cannot answer this, because
    /// duration varies with the flask's own modifiers - the game's own remaining-time is
    /// the only honest source.
    /// </remarks>
    public bool IsFlaskActive(int slot)
        => slot > 0 && All.Any(b => b.IsFlask && b.FlaskSlot == slot && b.TimeLeft > 0);

    /// <summary>True when a buff or debuff whose name contains <paramref name="needle"/> is on.</summary>
    /// <remarks>
    /// Substring and case-insensitive on purpose: the internal names are things like
    /// "bleeding" or "frozen", and a user configuring a bleed flask should not have to
    /// know the game's exact identifier to match one.
    /// </remarks>
    public bool Has(string needle)
        => !string.IsNullOrWhiteSpace(needle)
           && All.Any(b => b.Name.Contains(needle, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Reads the Buffs component: everything currently affecting the entity.</summary>
public sealed class BuffsReader
{
    /// <summary>Cap on entries read - a corrupt vector must not turn into a huge walk.</summary>
    private const int MaxBuffs = 64;

    private readonly IMemoryReader _reader;
    private readonly int _first;
    private readonly int _last;
    private readonly int _pointerSize;
    private readonly int _definitionPtr;
    private readonly int _totalTime;
    private readonly int _timeLeft;
    private readonly int _charges;
    private readonly int _flaskSlot;
    private readonly int _name;
    private readonly int _displayName;
    private readonly int _description;
    private readonly int _buffType;
    private readonly byte _flaskType;

    public BuffsReader(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;

        StructDef buffs = schema.Structs["Buffs"];
        _first = buffs.OffsetOf("StatusEffectFirst");
        _last = buffs.OffsetOf("StatusEffectLast");
        _pointerSize = (int)buffs.Constants["StatusEffectPointerSize"];

        StructDef effect = schema.Structs["StatusEffect"];
        _definitionPtr = effect.OffsetOf("BuffDefinitionPtr");
        _totalTime = effect.OffsetOf("TotalTime");
        _timeLeft = effect.OffsetOf("TimeLeft");
        _charges = effect.OffsetOf("Charges");
        _flaskSlot = effect.OffsetOf("FlaskSlot");

        StructDef definition = schema.Structs["BuffDefinition"];
        _name = definition.OffsetOf("Name");
        _displayName = definition.OffsetOf("DisplayName");
        _description = definition.OffsetOf("Description");
        _buffType = definition.OffsetOf("BuffType");
        _flaskType = (byte)definition.Constants["BuffTypeFlask"];
    }

    /// <summary>Reads every active buff from a Buffs component address.</summary>
    /// <remarks>
    /// THE VECTOR HOLDS POINTERS, and each one has to be dereferenced to reach a StatusEffect.
    /// This read used to walk it as INLINE structs at 0x50 stride - see the schema for the
    /// recording that settled it - and the failure mode is worth remembering: the span of a
    /// real vector here is 56 to 96 bytes, so dividing by 0x50 floored the count to 0 or 1 and
    /// the tool reported no buffs at all. Nothing threw; every buff condition simply never
    /// matched, and the one entry it did produce carried a name read off a StatusEffect.
    /// </remarks>
    public ActiveBuffs Read(ulong componentAddress)
    {
        if (!MemoryReaderExtensions.IsPlausiblePointer(componentAddress))
        {
            return ActiveBuffs.None;
        }

        ulong first = _reader.ReadPointer(componentAddress + (ulong)_first);
        ulong last = _reader.ReadPointer(componentAddress + (ulong)_last);
        if (!MemoryReaderExtensions.IsPlausiblePointer(first) || last <= first)
        {
            return ActiveBuffs.None;
        }

        long span = (long)(last - first);
        long count = span / _pointerSize;
        if (count is < 0 or > 2048 || span % _pointerSize != 0)
        {
            // Not a whole number of pointers, so this is a read caught mid-resize rather than
            // a real list. Checked rather than floored on purpose: flooring is exactly what
            // hid the wrong stride for as long as it did.
            return ActiveBuffs.None;
        }

        var buffs = new List<ActiveBuff>((int)Math.Min(count, MaxBuffs));
        for (long i = 0; i < count && buffs.Count < MaxBuffs; i++)
        {
            ActiveBuff? buff = ReadOne(_reader.ReadPointer(first + (ulong)(i * _pointerSize)));
            if (buff is ActiveBuff value)
            {
                buffs.Add(value);
            }
        }

        return new ActiveBuffs(buffs);
    }

    private ActiveBuff? ReadOne(ulong entry)
    {
        if (!MemoryReaderExtensions.IsPlausiblePointer(entry))
        {
            return null;
        }

        ulong definition = _reader.ReadPointer(entry + (ulong)_definitionPtr);
        if (!MemoryReaderExtensions.IsPlausiblePointer(definition))
        {
            return null;
        }

        string name = Text(definition + (ulong)_name);

        // The readable pair. Computed offsets rather than observed ones - see the schema - so
        // both are read defensively and an unreadable one is simply absent: the id beside them
        // is what everything actually depends on, and it comes from an offset that is proven.
        string displayName = Text(definition + (ulong)_displayName);
        string description = Text(definition + (ulong)_description);

        byte type = _reader.Read<byte>(definition + (ulong)_buffType);
        short rawSlot = _reader.Read<short>(entry + (ulong)_flaskSlot);

        // Raw 0-4 means belt slot 1-5; anything else did not come from a flask.
        int slot = rawSlot is >= 0 and < 5 ? rawSlot + 1 : 0;

        return new ActiveBuff(
            name,
            _reader.Read<float>(entry + (ulong)_timeLeft),
            _reader.Read<float>(entry + (ulong)_totalTime),
            _reader.Read<short>(entry + (ulong)_charges),
            slot,
            type == _flaskType,
            displayName,
            description);
    }

    /// <summary>A string off a row, or empty when the pointer does not lead to one.</summary>
    private string Text(ulong at)
    {
        ulong pointer = _reader.ReadPointer(at);
        return MemoryReaderExtensions.IsPlausiblePointer(pointer)
            ? _reader.ReadUtf16(pointer)
            : string.Empty;
    }
}
