using System.Buffers.Binary;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Game.Components;

/// <summary>What one of the player's granted skills is, as far as anything here can say.</summary>
/// <param name="Key">
/// What identifies the skill ACROSS the objects that carry it: its ActiveSkills.dat row where
/// that could be read, otherwise the skill object itself. See <see cref="PlayerSkills"/>.
/// </param>
/// <param name="Id">The dat row's id - "spark", "orb_of_storms" - or empty when the row was not reached.</param>
public readonly record struct SkillIdentity(ulong Key, string Id)
{
    /// <summary>Whether the lasting key was reached, rather than the object standing in for it.</summary>
    public bool IsLasting => Id.Length > 0;
}

/// <summary>
/// The player's granted-skill table, as the set of skill objects in it.
/// </summary>
/// <remarks>
/// WHAT IT IS FOR: the interface refers to skills by POINTER. A skill-bar slot carries the
/// address of the skill object it shows (see <c>SkillBarReader</c>) and the Skills panel's rows
/// are expected to carry the same, so joining a row to a slot means knowing which pointers are
/// skills at all - and that is this table, <c>Actor.ActiveSkills</c>, which every reference
/// reads: GameHelper2's Actor component and the AHK tool's ReadPlayerSkills both walk it, and
/// the schema records it proven in place by content (42 entries, 10/10 valid detail pointers).
///
/// THE KEY IS THE DAT ROW, NOT THE OBJECT, where it can be. The skill objects are the actor's,
/// and whether they outlive an area change is not established; the row in ActiveSkills.dat is
/// the same for the life of the process, so a figure remembered against it survives whatever
/// the actor does. The row pointer sits at <c>ActiveSkillDetails.ActiveSkillsDatPtr</c> in both
/// references and is VERIFIED BY CONTENT here rather than trusted: a dat row's first field is
/// a pointer to its id string - the rule <c>ItemReader</c> and <c>ActionReader</c> already rest
/// on - so a candidate whose first field does not reach a short plain identifier is not a row,
/// and that skill keeps the object as its key. The id read that way is a bonus: "spark" in a
/// readout is worth more than an address.
///
/// READ ON A CLOCK, not per tick: the table changes when a gem is socketed and at no other
/// time, and it is forty entries of two pointer hops each. The identities are kept across
/// readings, so the hops and the string are paid once per skill object.
/// </remarks>
public sealed class PlayerSkills
{
    /// <summary>How long a reading of the table stands.</summary>
    public const long RefreshMs = 2000;

    /// <summary>A bound on a count read out of memory. The schema's invariant allows this many.</summary>
    private const int MostEntries = 512;

    /// <summary>Longest id accepted from a dat row. The real ones are a few words joined by underscores.</summary>
    private const int LongestId = 64;

    private const long Never = long.MinValue;

    private readonly IMemoryReader _reader;
    private readonly int _table;
    private readonly int _entrySize;
    private readonly int _details;
    private readonly int _datRow;
    private readonly byte[] _entries;

    private Dictionary<ulong, SkillIdentity> _known = [];
    private Dictionary<ulong, SkillIdentity> _spare = [];
    private ulong _actor;
    private long _readAt = Never;

    public PlayerSkills(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;
        _table = schema.Structs["Actor"].OffsetOf("ActiveSkills");

        StructDef entry = schema.Structs["ActiveSkillStructure"];
        _entrySize = (int)entry.Constants["Size"];
        _details = entry.OffsetOf("ActiveSkillPtr");
        _datRow = schema.Structs["ActiveSkillDetails"].OffsetOf("ActiveSkillsDatPtr");
        _entries = new byte[MostEntries * _entrySize];
    }

    /// <summary>How many skills the table holds, as last read.</summary>
    public int Count => _known.Count;

    /// <summary>How many of them reached their dat row, and so have an id and a lasting key.</summary>
    public int Named { get; private set; }

    /// <summary>Bumped whenever the set of skills changes, so a reader keyed on it can notice.</summary>
    public int Version { get; private set; }

    /// <summary>Whether this address is one of the player's skill objects.</summary>
    public bool Contains(ulong details) => _known.ContainsKey(details);

    /// <summary>The identity of a skill object, or default when it is not one.</summary>
    public SkillIdentity IdentityOf(ulong details)
        => _known.TryGetValue(details, out SkillIdentity who) ? who : default;

    /// <summary>The lasting key for a skill object - its dat row, or itself - or 0 when it is not one.</summary>
    public ulong KeyOf(ulong details)
        => _known.TryGetValue(details, out SkillIdentity who) ? who.Key : 0;

    /// <summary>Re-reads the table when the clock, or a change of actor, says so.</summary>
    /// <param name="actor">The player's Actor component, or 0 when there is none to read.</param>
    /// <param name="nowMs">A monotonic clock.</param>
    public void Refresh(ulong actor, long nowMs)
    {
        if (actor == 0)
        {
            Forget();
            return;
        }

        if (actor == _actor && _readAt != Never && nowMs - _readAt < RefreshMs)
        {
            return;
        }

        if (actor != _actor)
        {
            // Another actor is another table, and its identities with it: the objects behind
            // the old addresses are gone, and an address can be reused for something else.
            Forget();
            _actor = actor;
        }

        _readAt = nowMs;

        ulong first = _reader.ReadPointer(actor + (ulong)_table);
        ulong last = _reader.ReadPointer(actor + (ulong)(_table + 8));
        if (!MemoryReaderExtensions.IsPlausiblePointer(first) || last <= first)
        {
            Clear();
            return;
        }

        long bytes = (long)(last - first);
        if (bytes % _entrySize != 0 || bytes / _entrySize > MostEntries)
        {
            return; // a torn read mid-resize, or not a table at all: what was known stands
        }

        Span<byte> table = _entries.AsSpan(0, (int)bytes);
        if (!_reader.TryRead(first, table))
        {
            return;
        }

        Dictionary<ulong, SkillIdentity> next = _spare;
        next.Clear();
        int named = 0;
        bool changed = false;

        for (int at = 0; at < table.Length; at += _entrySize)
        {
            ulong details = BinaryPrimitives.ReadUInt64LittleEndian(table.Slice(at + _details));
            if (!MemoryReaderExtensions.IsPlausiblePointer(details) || next.ContainsKey(details))
            {
                continue;
            }

            if (!_known.TryGetValue(details, out SkillIdentity who))
            {
                who = Identify(details);
                changed = true;
            }

            next[details] = who;
            if (who.IsLasting)
            {
                named++;
            }
        }

        changed |= next.Count != _known.Count;

        _spare = _known;
        _known = next;
        Named = named;
        if (changed)
        {
            Version++;
        }
    }

    private void Forget()
    {
        Clear();
        _actor = 0;
        _readAt = Never;
    }

    private void Clear()
    {
        if (_known.Count > 0)
        {
            _known.Clear();
            Version++;
        }

        Named = 0;
    }

    /// <summary>The dat row and its id, when the two hops reach one; otherwise the object itself.</summary>
    private SkillIdentity Identify(ulong details)
    {
        ulong row = _reader.ReadPointer(details + (ulong)_datRow);
        if (MemoryReaderExtensions.IsPlausiblePointer(row))
        {
            ulong text = _reader.ReadPointer(row);
            if (MemoryReaderExtensions.IsPlausiblePointer(text))
            {
                string id = _reader.ReadUnicodeString(text, LongestId);
                if (LooksLikeAnId(id))
                {
                    return new SkillIdentity(row, id);
                }
            }
        }

        return new SkillIdentity(details, string.Empty);
    }

    /// <summary>The shape of a dat id: short, ASCII, letters and digits joined by underscores.</summary>
    /// <remarks>
    /// The fingerprint that tells a row from a pointer that happens to reach text: an address
    /// that lands in the middle of something prints as a few readable characters and then
    /// anything at all, and one wrong character is enough to refuse it.
    /// </remarks>
    private static bool LooksLikeAnId(string text)
    {
        if (text.Length is 0 or > LongestId)
        {
            return false;
        }

        foreach (char c in text)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-' && c != '.')
            {
                return false;
            }
        }

        return true;
    }
}
