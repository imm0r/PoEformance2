using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Game.Components;

/// <summary>One buff or debuff currently on the player.</summary>
/// <param name="FlaskSlot">Belt slot 1-5 for a flask buff, 0 for anything else.</param>
public readonly record struct ActiveBuff(
    string Name,
    float TimeLeft,
    float TotalTime,
    int Charges,
    int FlaskSlot,
    bool IsFlask);

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
    private readonly int _stride;
    private readonly int _definitionPtr;
    private readonly int _totalTime;
    private readonly int _timeLeft;
    private readonly int _charges;
    private readonly int _flaskSlot;
    private readonly int _name;
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
        _stride = (int)buffs.Constants["StatusEffectStructSize"];

        StructDef effect = schema.Structs["StatusEffect"];
        _definitionPtr = effect.OffsetOf("BuffDefinitionPtr");
        _totalTime = effect.OffsetOf("TotalTime");
        _timeLeft = effect.OffsetOf("TimeLeft");
        _charges = effect.OffsetOf("Charges");
        _flaskSlot = effect.OffsetOf("FlaskSlot");

        StructDef definition = schema.Structs["BuffDefinition"];
        _name = definition.OffsetOf("Name");
        _buffType = definition.OffsetOf("BuffType");
        _flaskType = (byte)definition.Constants["BuffTypeFlask"];
    }

    /// <summary>Reads every active buff from a Buffs component address.</summary>
    /// <remarks>
    /// The entries sit INLINE in the vector at a fixed stride - they are not pointers to
    /// dereference. GameHelper2 reads them the other way, and that difference is why this
    /// is worth stating twice: the wrong interpretation reads plausible-looking garbage
    /// rather than failing outright.
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

        long count = (long)(last - first) / _stride;
        if (count is < 0 or > 2048)
        {
            return ActiveBuffs.None; // a read caught mid-resize, not a real list
        }

        var buffs = new List<ActiveBuff>((int)Math.Min(count, MaxBuffs));
        for (long i = 0; i < count && buffs.Count < MaxBuffs; i++)
        {
            ActiveBuff? buff = ReadOne(first + (ulong)(i * _stride));
            if (buff is ActiveBuff value)
            {
                buffs.Add(value);
            }
        }

        return new ActiveBuffs(buffs);
    }

    private ActiveBuff? ReadOne(ulong entry)
    {
        ulong definition = _reader.ReadPointer(entry + (ulong)_definitionPtr);
        if (!MemoryReaderExtensions.IsPlausiblePointer(definition))
        {
            return null;
        }

        ulong namePointer = _reader.ReadPointer(definition + (ulong)_name);
        string name = MemoryReaderExtensions.IsPlausiblePointer(namePointer)
            ? _reader.ReadUtf16(namePointer)
            : string.Empty;

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
            type == _flaskType);
    }
}
