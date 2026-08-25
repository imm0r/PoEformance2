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

/// <summary>How far a walk of the Buffs vector got, whether or not it produced anything.</summary>
/// <param name="Component">The component address, or 0 when the entity has no Buffs.</param>
/// <param name="SpanBytes">Bytes between the vector's two bounds; -1 when they did not read.</param>
/// <param name="Entries">Pointers the span describes.</param>
/// <param name="Followed">Entries whose pointer was worth dereferencing.</param>
/// <param name="Defined">Entries that led to a plausible BuffDefinition.</param>
/// <param name="Named">Entries that produced a non-empty name - the ones a rule can match.</param>
/// <remarks>
/// AN EMPTY BUFF LIST HAS FIVE CAUSES AND USED TO LOOK LIKE ONE. No component, bounds that did
/// not read, a span refused as a partial vector, entries whose pointers led nowhere, and
/// definitions with no readable name are five different faults with five different fixes, and
/// every one of them showed up as "this character has no buffs on" - the exact shape of failure
/// that let a wrong stride survive for months. So the walk reports where it stopped, and the
/// rule editor prints it under the buff list.
/// </remarks>
public readonly record struct BuffRead(
    ulong Component = 0,
    long SpanBytes = -1,
    int Entries = 0,
    int Followed = 0,
    int Defined = 0,
    int Named = 0)
{
    /// <summary>One line, for a panel that has to explain an empty list.</summary>
    public override string ToString()
    {
        if (Component == 0)
        {
            return "no Buffs component on the player";
        }

        if (SpanBytes < 0)
        {
            return $"Buffs at 0x{Component:X}, the vector's bounds did not read";
        }

        if (Entries == 0)
        {
            return $"Buffs at 0x{Component:X}, {SpanBytes} bytes - not a whole number of entries";
        }

        return $"Buffs at 0x{Component:X}, {SpanBytes} bytes = {Entries} entries; "
               + $"{Followed} followed, {Defined} defined, {Named} named";
    }
}

/// <summary>The player's active buffs and debuffs.</summary>
public sealed class ActiveBuffs
{
    public static ActiveBuffs None { get; } = new([]);

    public IReadOnlyList<ActiveBuff> All { get; }

    /// <summary>Where the walk that produced <see cref="All"/> got to.</summary>
    public BuffRead Reading { get; init; }

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
        var reading = new BuffRead(componentAddress);
        if (!MemoryReaderExtensions.IsPlausiblePointer(first) || last <= first)
        {
            return new ActiveBuffs([]) { Reading = reading };
        }

        long span = (long)(last - first);
        reading = reading with { SpanBytes = span };
        long count = span / _pointerSize;
        if (count is < 0 or > 2048 || span % _pointerSize != 0)
        {
            // Not a whole number of pointers, so this is a read caught mid-resize rather than
            // a real list. Checked rather than floored on purpose: flooring is exactly what
            // hid the wrong stride for as long as it did.
            return new ActiveBuffs([]) { Reading = reading };
        }

        reading = reading with { Entries = (int)count };
        var buffs = new List<ActiveBuff>((int)Math.Min(count, MaxBuffs));
        for (long i = 0; i < count && buffs.Count < MaxBuffs; i++)
        {
            ActiveBuff? buff = ReadOne(
                _reader.ReadPointer(first + (ulong)(i * _pointerSize)), ref reading);
            if (buff is ActiveBuff value)
            {
                buffs.Add(value);
            }
        }

        return new ActiveBuffs(buffs) { Reading = reading };
    }

    private ActiveBuff? ReadOne(ulong entry, ref BuffRead reading)
    {
        if (!MemoryReaderExtensions.IsPlausiblePointer(entry))
        {
            return null;
        }

        reading = reading with { Followed = reading.Followed + 1 };
        ulong definition = _reader.ReadPointer(entry + (ulong)_definitionPtr);
        if (!MemoryReaderExtensions.IsPlausiblePointer(definition))
        {
            return null;
        }

        reading = reading with { Defined = reading.Defined + 1 };

        // The id is checked for SHAPE, not just for being non-empty. A pointer that lands on
        // something which is not a string still yields characters - the wrong stride produced
        // "䑐⟄翷" and it went into the picker as a buff somebody could click - and an id that
        // is not [a-z0-9_] is not an id whatever it looks like.
        string name = Identifier(definition + (ulong)_name);
        if (name.Length > 0)
        {
            reading = reading with { Named = reading.Named + 1 };
        }

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

    /// <summary>An engine identifier, or empty when what is there cannot be one.</summary>
    /// <remarks>
    /// The ids in this table are `[a-z0-9_]` - "fire_wall", "flask_effect_life". Anything else
    /// is a pointer that landed on bytes rather than on a row, and the honest answer to that is
    /// nothing at all: a name is what a rule MATCHES, so a plausible-looking wrong one is worse
    /// than none. Deliberately a check a wrong value fails rather than one it passes.
    /// </remarks>
    private string Identifier(ulong at)
    {
        string text = Text(at);
        if (text.Length is 0 or > 128)
        {
            return string.Empty;
        }

        bool letter = false;
        foreach (char c in text)
        {
            if (c is >= 'a' and <= 'z')
            {
                letter = true;
            }
            else if (c is not ((>= '0' and <= '9') or '_'))
            {
                return string.Empty;
            }
        }

        return letter ? text : string.Empty;
    }
}
