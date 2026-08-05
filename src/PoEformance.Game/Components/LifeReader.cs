using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Game.Components;

/// <summary>One vital pool: life, mana or energy shield.</summary>
/// <param name="Current">What the globe currently holds.</param>
/// <param name="Max">The pool before reservations.</param>
/// <param name="ReservedFlat">Flat amount reserved.</param>
/// <param name="ReservedPercent">Reserved share in hundredths of a percent (5000 = 50%).</param>
public readonly record struct Vital(int Current, int Max, int ReservedFlat, int ReservedPercent)
{
    /// <summary>Total amount reserved, flat plus proportional.</summary>
    public int Reserved => Max <= 0
        ? 0
        : (int)Math.Ceiling(ReservedPercent / 10000f * Max) + ReservedFlat;

    /// <summary>The pool actually usable - what the globe on screen represents.</summary>
    public int Unreserved => Math.Max(0, Max - Reserved);

    /// <summary>
    /// Fill level of the USABLE pool, 0-100, or -1 when there is nothing to measure.
    /// </summary>
    /// <remarks>
    /// Against the unreserved pool, not against <see cref="Max"/> - this is the whole
    /// subtlety of the type, taken from GameHelper2's VitalStruct. With half of mana
    /// reserved by auras, a FULL globe holds Current == Max/2, so measuring against Max
    /// reports 50% forever and anything thresholded on it fires constantly. -1 rather than
    /// 0 for "unknown" keeps a not-yet-loaded pool from reading as an emergency.
    /// </remarks>
    public int Percent
    {
        get
        {
            int unreserved = Unreserved;
            return unreserved <= 0 ? -1 : (int)Math.Round(100d * Current / unreserved);
        }
    }

    /// <summary>True when this pool holds a plausible, loaded value.</summary>
    public bool IsValid => Max > 0 && Current >= 0 && Current <= Max * 4;
}

/// <summary>The player's three pools, as one snapshot.</summary>
public readonly record struct Vitals(Vital Life, Vital Mana, Vital EnergyShield)
{
    /// <summary>
    /// True when the character is dead - or the read landed on an uninitialised struct.
    /// </summary>
    /// <remarks>
    /// Worth its own name because every consumer needs the same guard: at zero life the
    /// life percentage is 0, which looks exactly like the most urgent possible emergency.
    /// Acting on it would mean hammering flasks at a corpse.
    /// </remarks>
    public bool IsDeadOrUnloaded => !Life.IsValid || Life.Current <= 0;
}

/// <summary>Reads the Life component: life, mana and energy shield.</summary>
public sealed class LifeReader
{
    private readonly IMemoryReader _reader;
    private readonly int _health;
    private readonly int _mana;
    private readonly int _energyShield;
    private readonly int _reservedFlat;
    private readonly int _reservedPercent;
    private readonly int _max;
    private readonly int _current;

    public LifeReader(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;

        StructDef life = schema.Structs["Life"];
        _health = life.OffsetOf("Health");
        _mana = life.OffsetOf("Mana");
        _energyShield = life.OffsetOf("EnergyShield");

        StructDef vital = schema.Structs["Vital"];
        _reservedFlat = vital.OffsetOf("ReservedFlat");
        _reservedPercent = vital.OffsetOf("ReservedPercent");
        _max = vital.OffsetOf("Max");
        _current = vital.OffsetOf("Current");
    }

    /// <summary>Reads all three pools from a Life component address, or null on failure.</summary>
    public Vitals? Read(ulong componentAddress)
    {
        if (!MemoryReaderExtensions.IsPlausiblePointer(componentAddress))
        {
            return null;
        }

        return new Vitals(
            ReadVital(componentAddress + (ulong)_health),
            ReadVital(componentAddress + (ulong)_mana),
            ReadVital(componentAddress + (ulong)_energyShield));
    }

    /// <summary>
    /// Reads one vital sub-struct.
    /// </summary>
    /// <remarks>
    /// The vitals are INLINE sub-structs, so the offset is a base address to read relative
    /// to, not a pointer to follow - the same shape as LocalPlayerStruct, and the same
    /// mistake if dereferenced.
    /// </remarks>
    private Vital ReadVital(ulong vitalBase) => new(
        _reader.Read<int>(vitalBase + (ulong)_current),
        _reader.Read<int>(vitalBase + (ulong)_max),
        _reader.Read<int>(vitalBase + (ulong)_reservedFlat),
        _reader.Read<int>(vitalBase + (ulong)_reservedPercent));
}
