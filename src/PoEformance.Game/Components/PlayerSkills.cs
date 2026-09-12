using System.Buffers.Binary;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Game.Components;

/// <summary>What one of the player's granted skills is, as far as anything here can say.</summary>
/// <param name="Key">
/// What identifies the skill ACROSS the objects that carry it: its ActiveSkills.dat row where
/// that could be reached, otherwise the skill object itself. See <see cref="PlayerSkills"/>.
/// </param>
/// <param name="Id">The dat row's id - "spark", "orb_of_storms" - or empty when no row was reached.</param>
/// <param name="Name">
/// The dat row's DisplayedName - "Spark", in the client's language - or empty. What the Skills
/// panel prints on the skill's row, and so the join to it.
/// </param>
public readonly record struct SkillIdentity(ulong Key, string Id, string Name)
{
    /// <summary>Whether a row was reached, rather than the object standing in for it.</summary>
    public bool IsLasting => Id.Length > 0;

    /// <summary>Whether the row also yielded the name the panel prints.</summary>
    public bool HasName => Name.Length > 0;
}

/// <summary>
/// The player's granted-skill table, as the set of skill objects in it, each with its name.
/// </summary>
/// <remarks>
/// WHAT IT IS FOR: the interface refers to skills by POINTER and by NAME. A skill-bar slot
/// carries the address of the skill object it shows (see <c>SkillBarReader</c>); the Skills
/// panel's rows print the skill's displayed name and, as it turned out in game, no pointer.
/// Joining the two means knowing, for every skill object, what it is called - and that is this
/// table, <c>Actor.ActiveSkills</c>, which every reference reads (GameHelper2's Actor component
/// and the AHK tool's ReadPlayerSkills both walk it) and which the schema records proven in
/// place by content: 42 entries, 10/10 valid detail pointers.
///
/// THE NAME IS IN A DAT ROW, and reaching the row is the part that needed settling - twice.
/// Both references name a direct pointer to the ActiveSkills.dat row on the skill object and
/// neither USES it; in game (0.5.5, 2026-09-12) it reached a row for none of 49 skills. What
/// both resolve a name through is the GrantedEffectsPerLevel row on the object, whose first
/// column is the GrantedEffects row, whose ActiveSkill column is the ActiveSkills row - and
/// the second run in game found the pointer at that place leading to a row that BEGINS WITH AN
/// ID, which a per-level row does not: so it is a GrantedEffects row (or the ActiveSkills row
/// itself), one hop shorter than the references say. Hence <see cref="Resolve"/>, which reads a
/// pointer as whichever of the three rows it proves to be, from the most telling reading to the
/// least: an ActiveSkills row with its name, a GrantedEffects row whose ActiveSkill column
/// reaches one (at either of the two columns the witnesses disagree on - dat-schema's widths
/// compute 0x4F, GameHelper2 reads 0x57), a per-level row whose GrantedEffect does, and last
/// an ActiveSkills row without a name. EVERY STEP IS VERIFIED BY CONTENT: a dat row's first
/// field is a pointer to its id string - the rule <c>ItemReader</c> and <c>ActionReader</c>
/// already rest on - so a pointer is a row only when that first field reaches a short plain
/// identifier. The direct field, the per-level field and then any pointer in the object's
/// first 0x100 bytes are tried in that order, and the reading that answered is reported, so
/// the next drift names itself in the readout instead of reading as a panel nobody opened.
///
/// THE KEY IS THE DAT ROW, NOT THE OBJECT, where it was reached: the skill objects are the
/// actor's, and whether they outlive an area change is not established, while a dat row is the
/// same for the life of the process, so a figure remembered against it survives whatever the
/// actor does.
///
/// READ ON A CLOCK, not per tick: the table changes when a gem is socketed and at no other
/// time, and it is forty entries of a few pointer hops each. The identities are kept across
/// readings, so the hops and the strings are paid once per skill object.
/// </remarks>
public sealed class PlayerSkills
{
    /// <summary>How long a reading of the table stands.</summary>
    public const long RefreshMs = 2000;

    /// <summary>A bound on a count read out of memory. The schema's invariant allows this many.</summary>
    private const int MostEntries = 512;

    /// <summary>Longest id accepted from a dat row. The real ones are a few words joined by underscores.</summary>
    private const int LongestId = 64;

    /// <summary>Longest displayed name accepted where the schema puts it. "Cast on Critical Strike" is twenty-three.</summary>
    private const int LongestName = 64;

    /// <summary>
    /// Longest name accepted when it has to be looked for in the row - shorter, because the
    /// column after the name is the description, a sentence, and the bound is what keeps a
    /// short one from passing for a name.
    /// </summary>
    private const int LongestHuntedName = 40;

    /// <summary>How much of a skill object is searched for its row when the known places fail.</summary>
    public const int HuntBytes = 0x100;

    /// <summary>How far into an ActiveSkills row the name is looked for when it is not where the schema says.</summary>
    public const int NameHuntBytes = 0x40;

    private const long Never = long.MinValue;

    private readonly IMemoryReader _reader;
    private readonly int _table;
    private readonly int _entrySize;
    private readonly int _details;
    private readonly int _datRow;
    private readonly int _perLevel;
    private readonly int _grantedEffect;
    private readonly int _activeSkill;
    private readonly int _activeSkillPerReference;
    private readonly int _displayedName;
    private readonly byte[] _entries;

    private Dictionary<ulong, SkillIdentity> _known = [];
    private Dictionary<ulong, SkillIdentity> _spare = [];
    private readonly Dictionary<string, ulong> _byName = new(StringComparer.Ordinal);
    private ulong _actor;
    private long _readAt = Never;
    private string _route = "no row reached";

    public PlayerSkills(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;
        _table = schema.Structs["Actor"].OffsetOf("ActiveSkills");

        StructDef entry = schema.Structs["ActiveSkillStructure"];
        _entrySize = (int)entry.Constants["Size"];
        _details = entry.OffsetOf("ActiveSkillPtr");

        StructDef details = schema.Structs["ActiveSkillDetails"];
        _datRow = details.OffsetOf("ActiveSkillsDatPtr");
        _perLevel = details.OffsetOf("GrantedEffectsPerLevelDatRow");

        _grantedEffect = schema.Structs["GrantedEffectsPerLevelDat"].OffsetOf("GrantedEffect");

        StructDef granted = schema.Structs["GrantedEffectsDat"];
        _activeSkill = granted.OffsetOf("ActiveSkill");
        _activeSkillPerReference = (int)granted.Constants["ActiveSkillPerGameHelper2"];

        _displayedName = schema.Structs["ActiveSkillsDat"].OffsetOf("DisplayedName");
        _entries = new byte[MostEntries * _entrySize];
    }

    /// <summary>How many skills the table holds, as last read.</summary>
    public int Count => _known.Count;

    /// <summary>How many of them reached a dat row, and so have an id and a lasting key.</summary>
    public int Keyed { get; private set; }

    /// <summary>How many of them also have the name the panel prints.</summary>
    public int Named { get; private set; }

    /// <summary>Bumped whenever the set of skills changes, so a reader keyed on it can notice.</summary>
    public int Version { get; private set; }

    /// <summary>How the last row was reached and read, for the readouts - or that none was.</summary>
    public string RouteNote => _route;

    /// <summary>Whether this address is one of the player's skill objects.</summary>
    public bool Contains(ulong details) => _known.ContainsKey(details);

    /// <summary>The identity of a skill object, or default when it is not one.</summary>
    public SkillIdentity IdentityOf(ulong details)
        => _known.TryGetValue(details, out SkillIdentity who) ? who : default;

    /// <summary>The lasting key for a skill object - its dat row, or itself - or 0 when it is not one.</summary>
    public ulong KeyOf(ulong details)
        => _known.TryGetValue(details, out SkillIdentity who) ? who.Key : 0;

    /// <summary>The skill object called this, exactly as its dat row spells it, or 0 for none.</summary>
    /// <remarks>Two objects with one name - two gems of the same skill - answer with the first read.</remarks>
    public ulong ByName(string name)
        => _byName.TryGetValue(name, out ulong details) ? details : 0;

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
        int keyed = 0;
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
                keyed++;
            }

            if (who.HasName)
            {
                named++;
            }
        }

        changed |= next.Count != _known.Count;

        _spare = _known;
        _known = next;
        Keyed = keyed;
        Named = named;
        if (changed)
        {
            Version++;
            RebuildNames();
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
            _byName.Clear();
            Version++;
        }

        Keyed = 0;
        Named = 0;
    }

    private void RebuildNames()
    {
        _byName.Clear();
        foreach ((ulong details, SkillIdentity who) in _known)
        {
            if (who.HasName)
            {
                _byName.TryAdd(who.Name, details);
            }
        }
    }

    /// <summary>The skill's dat row, reached whichever way reaches it; otherwise the object itself.</summary>
    private SkillIdentity Identify(ulong details)
    {
        if (Resolve(_reader.ReadPointer(details + (ulong)_datRow), out SkillIdentity who, out string how))
        {
            _route = $"+0x{_datRow:X} {how}";
            return who;
        }

        if (Resolve(_reader.ReadPointer(details + (ulong)_perLevel), out who, out how))
        {
            _route = $"+0x{_perLevel:X} {how}";
            return who;
        }

        // Neither known place: any pointer in the object that is a row, or leads to one.
        Span<byte> block = stackalloc byte[HuntBytes];
        if (_reader.TryRead(details, block))
        {
            for (int at = 0; at + 8 <= HuntBytes; at += 8)
            {
                if (at == _datRow || at == _perLevel)
                {
                    continue;
                }

                ulong pointer = BinaryPrimitives.ReadUInt64LittleEndian(block[at..]);
                if (Resolve(pointer, out who, out how))
                {
                    _route = $"hunted +0x{at:X} {how}";
                    return who;
                }
            }
        }

        return new SkillIdentity(details, string.Empty, string.Empty);
    }

    /// <summary>
    /// What a pointer leads to, read as whichever row it proves to be - see the remarks on the
    /// class for the order, and why a nameless ActiveSkills reading comes last.
    /// </summary>
    private bool Resolve(ulong pointer, out SkillIdentity who, out string how)
    {
        who = default;
        how = string.Empty;
        if (!MemoryReaderExtensions.IsPlausiblePointer(pointer))
        {
            return false;
        }

        bool isRow = ActiveSkillRow(pointer, out SkillIdentity asRow, out int nameAt);
        if (isRow && asRow.HasName)
        {
            who = asRow;
            how = Described("row", nameAt);
            return true;
        }

        if (GrantedEffectsRow(pointer, out who, out int column, out nameAt))
        {
            how = Described($"granted row then +0x{column:X}", nameAt);
            return true;
        }

        if (GrantedEffectsRow(_reader.ReadPointer(pointer + (ulong)_grantedEffect), out who, out column, out nameAt))
        {
            how = Described($"per-level row then +0x{column:X}", nameAt);
            return true;
        }

        if (isRow)
        {
            who = asRow;
            how = "row (no name)";
            return true;
        }

        return false;
    }

    /// <summary>The reading, with where the name was when it was not where the schema puts it.</summary>
    private string Described(string reading, int nameAt)
        => nameAt < 0 ? $"{reading} (no name)"
            : nameAt == _displayedName ? reading
            : $"{reading}, name at +0x{nameAt:X}";

    /// <summary>A GrantedEffects row - one with an id - whose ActiveSkill column reaches an ActiveSkills row.</summary>
    private bool GrantedEffectsRow(ulong granted, out SkillIdentity who, out int column, out int nameAt)
    {
        who = default;
        column = 0;
        nameAt = -1;
        if (!RowId(granted, out _))
        {
            return false;
        }

        if (ActiveSkillRow(_reader.ReadPointer(granted + (ulong)_activeSkill), out who, out nameAt))
        {
            column = _activeSkill;
            return true;
        }

        if (ActiveSkillRow(_reader.ReadPointer(granted + (ulong)_activeSkillPerReference), out who, out nameAt))
        {
            column = _activeSkillPerReference;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Whether this is a row with an id, and what it says: the id, and the name where the
    /// schema puts it or, failing that, wherever in the row's first bytes a name-shaped string
    /// hangs. <paramref name="nameAt"/> says which, or -1 for none.
    /// </summary>
    private bool ActiveSkillRow(ulong row, out SkillIdentity who, out int nameAt)
    {
        nameAt = -1;
        if (!RowId(row, out string id))
        {
            who = default;
            return false;
        }

        string name = RowText(row + (ulong)_displayedName, LongestName);
        if (name.Length > 0)
        {
            nameAt = _displayedName;
        }
        else
        {
            for (int at = 8; at < NameHuntBytes && name.Length == 0; at += 8)
            {
                if (at == _displayedName)
                {
                    continue;
                }

                name = RowText(row + (ulong)at, LongestHuntedName);
                if (name.Length > 0 && !string.Equals(name, id, StringComparison.Ordinal))
                {
                    nameAt = at;
                }
                else
                {
                    name = string.Empty;
                }
            }
        }

        who = new SkillIdentity(row, id, name);
        return true;
    }

    /// <summary>A dat row's fingerprint: its first field is a pointer to a short plain id.</summary>
    private bool RowId(ulong row, out string id)
    {
        id = string.Empty;
        if (!MemoryReaderExtensions.IsPlausiblePointer(row))
        {
            return false;
        }

        ulong text = _reader.ReadPointer(row);
        if (!MemoryReaderExtensions.IsPlausiblePointer(text))
        {
            return false;
        }

        id = _reader.ReadUnicodeString(text, LongestId);
        return LooksLikeAnId(id);
    }

    /// <summary>A string column's text, trimmed, or empty when it does not read as a name.</summary>
    private string RowText(ulong column, int longest)
    {
        ulong text = _reader.ReadPointer(column);
        if (!MemoryReaderExtensions.IsPlausiblePointer(text))
        {
            return string.Empty;
        }

        string name = _reader.ReadUnicodeString(text, longest + 1).Trim();
        return name.Length <= longest && LooksLikeAName(name) ? name : string.Empty;
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

    /// <summary>The shape of a displayed name: printable, with a letter in it. Any language.</summary>
    private static bool LooksLikeAName(string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        bool letter = false;
        foreach (char c in text)
        {
            if (char.IsControl(c) || char.IsSurrogate(c))
            {
                return false;
            }

            letter |= char.IsLetter(c);
        }

        return letter;
    }
}
