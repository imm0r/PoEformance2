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
/// THE NAME IS IN A DAT ROW, and reaching the row is the part that needed settling - over four
/// runs in game (0.5.5, 2026-09-12). Both references name a direct pointer to the
/// ActiveSkills.dat row on the skill object and neither USES it; in game it reached a row for
/// none of 49 skills. What both resolve a name through is the GrantedEffectsPerLevel row on the
/// object, whose first column is the GrantedEffects row, whose ActiveSkill column is the
/// ActiveSkills row - and where the references had that field (+0x48) the game now keeps a
/// pointer to a DIFFERENT id-first row ("StormCloud"), which passed the fingerprint for a row
/// of the skill's, and a second id in it for the skill's name, until readings were ranked; the
/// per-level row had moved to +0x58, where the search of the object found it and where the
/// schema now puts it (its comment carries the evidence). Hence <see cref="ReadingOf"/>, which
/// reads a pointer as whichever of the three rows it proves to be, from the most telling reading
/// to the least: an ActiveSkills row with its name, a GrantedEffects row whose ActiveSkill column
/// reaches one (at either of the two columns the witnesses disagree on - dat-schema's widths
/// compute 0x4F, GameHelper2 reads 0x57 - or, failing both, wherever in the row a reference to
/// one sits), a per-level row whose GrantedEffect does, and last an ActiveSkills row without a
/// name. EVERY STEP IS VERIFIED BY CONTENT: a dat row's first field is a pointer to its id
/// string - the rule <c>ItemReader</c> and <c>ActionReader</c> already rest on - so a pointer is
/// a row only when that first field reaches a short plain identifier. The direct field, the
/// per-level field and then any pointer in the object's first 0x100 bytes are tried in that
/// order, and the reading that answered is reported, so the next drift names itself in the
/// readout instead of reading as a panel nobody opened.
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

    /// <summary>How much of a GrantedEffects row is searched for its reference to the ActiveSkills row.</summary>
    /// <remarks>
    /// BYTE BY BYTE, not in eights: dat columns are packed, so a row reference sits wherever
    /// the columns before it end - 0x4F and 0x57 are the two the witnesses name - and an
    /// aligned scan would step over it.
    /// </remarks>
    public const int RowHuntBytes = 0x100;

    /// <summary>How many strings, and how many references, the survey of a row quotes.</summary>
    private const int SurveyMost = 6;

    /// <summary>How long a quoted string in the survey may be before it is cut.</summary>
    private const int SurveyCut = 24;

    private const long Never = long.MinValue;

    /// <summary>How much a reading of a pointer can be trusted, most first. See <see cref="ReadingOf"/>.</summary>
    private const int Certain = 3;
    private const int Provisional = 2;
    private const int Bare = 1;
    private const int Nothing = 0;

    /// <summary>What one pointer proved to lead to.</summary>
    private readonly record struct Reading(SkillIdentity Who, int Strength, string How);

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
    private ulong _surveyed;
    private string _survey = string.Empty;

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

    /// <summary>
    /// The skill's dat row, reached whichever way reaches it; otherwise the object itself.
    /// </summary>
    /// <remarks>
    /// THE STRONGEST READING WINS, not the first: the known places and then every pointer in
    /// the object are read, and a reading that found the name where the schema puts it ends
    /// the search, while a weaker one - a name hunted for in the row, or an id alone - is kept
    /// only until something better turns up. The third run in game is why: the row at the
    /// references' per-level field (+0x48) begins with an id, and a hunt for its name found
    /// another id further in ("StormCloud" as "QuakeSlam"), which was taken for a name and
    /// ended the search sixteen bytes short of the per-level row, which 0.5.5 had moved to
    /// +0x58. The schema carries the new place now; the search stays for the next move.
    /// </remarks>
    private SkillIdentity Identify(ulong details)
    {
        Reading best = default;

        if (Consider(_reader.ReadPointer(details + (ulong)_datRow), $"+0x{_datRow:X}", ref best)
            || Consider(_reader.ReadPointer(details + (ulong)_perLevel), $"+0x{_perLevel:X}", ref best))
        {
            return Settle(best);
        }

        Span<byte> block = stackalloc byte[HuntBytes];
        if (_reader.TryRead(details, block))
        {
            for (int at = 0; at + 8 <= HuntBytes; at += 8)
            {
                if (at == _datRow || at == _perLevel)
                {
                    continue;
                }

                if (Consider(BinaryPrimitives.ReadUInt64LittleEndian(block[at..]), $"hunted +0x{at:X}", ref best))
                {
                    break;
                }
            }
        }

        return best.Strength == Nothing
            ? new SkillIdentity(details, string.Empty, string.Empty)
            : Settle(best);
    }

    /// <summary>Reads a pointer and keeps the reading when it beats the best so far. True when it ends the search.</summary>
    private bool Consider(ulong pointer, string place, ref Reading best)
    {
        Reading reading = ReadingOf(pointer);
        if (reading.Strength > best.Strength)
        {
            best = reading with { How = $"{place} {reading.How}" };
        }

        return best.Strength == Certain;
    }

    private SkillIdentity Settle(Reading best)
    {
        _route = best.How;
        return best.Who;
    }

    /// <summary>
    /// What a pointer leads to, read as whichever row it proves to be - see the remarks on the
    /// class for the order. A name found where the schema puts it makes the reading certain;
    /// a name hunted for in the row, or no name at all, leaves it provisional.
    /// </summary>
    private Reading ReadingOf(ulong pointer)
    {
        if (!MemoryReaderExtensions.IsPlausiblePointer(pointer))
        {
            return default;
        }

        bool isRow = ActiveSkillRow(pointer, out SkillIdentity asRow, out int nameAt);
        if (isRow && nameAt == _displayedName)
        {
            return new Reading(asRow, Certain, "row");
        }

        if (GrantedEffectsRow(pointer, out SkillIdentity who, out int column))
        {
            return new Reading(who, Certain, $"granted row then +0x{column:X}");
        }

        if (GrantedEffectsRow(_reader.ReadPointer(pointer + (ulong)_grantedEffect), out who, out column))
        {
            return new Reading(who, Certain, $"per-level row then +0x{column:X}");
        }

        if (isRow && nameAt >= 0)
        {
            return new Reading(asRow, Provisional, $"row, name at +0x{nameAt:X}");
        }

        return isRow ? new Reading(asRow, Bare, "row (no name)") : default;
    }

    /// <summary>
    /// A GrantedEffects row - one with an id - whose ActiveSkill column reaches an ActiveSkills
    /// row with its name where the schema puts it. The two columns the witnesses name are tried
    /// first, then the whole row, byte by byte.
    /// </summary>
    private bool GrantedEffectsRow(ulong granted, out SkillIdentity who, out int column)
    {
        who = default;
        column = 0;
        if (!RowId(granted, out _))
        {
            return false;
        }

        if (NamedActiveSkillRow(_reader.ReadPointer(granted + (ulong)_activeSkill), out who))
        {
            column = _activeSkill;
            return true;
        }

        if (NamedActiveSkillRow(_reader.ReadPointer(granted + (ulong)_activeSkillPerReference), out who))
        {
            column = _activeSkillPerReference;
            return true;
        }

        Span<byte> row = stackalloc byte[RowHuntBytes];
        if (!_reader.TryRead(granted, row))
        {
            return false;
        }

        for (int at = 8; at + 8 <= RowHuntBytes; at++)
        {
            if (at == _activeSkill || at == _activeSkillPerReference)
            {
                continue;
            }

            ulong pointer = BinaryPrimitives.ReadUInt64LittleEndian(row[at..]);
            if (pointer != granted && NamedActiveSkillRow(pointer, out who))
            {
                column = at;
                return true;
            }
        }

        return false;
    }

    /// <summary>An ActiveSkills row beyond doubt: an id first, and a name where the schema puts it.</summary>
    private bool NamedActiveSkillRow(ulong row, out SkillIdentity who)
    {
        who = default;
        if (!MemoryReaderExtensions.IsPlausiblePointer(row) || !RowId(row, out string id))
        {
            return false;
        }

        string name = RowText(row + (ulong)_displayedName, LongestName);
        if (name.Length == 0)
        {
            return false;
        }

        who = new SkillIdentity(row, id, name);
        return true;
    }

    /// <summary>
    /// What the pointers of a skill object and of its first row lead to, quoted - for the readout.
    /// </summary>
    /// <remarks>
    /// THE LAYOUT, READ OFF THE SCREEN. Every reading above is a guess the game confirms or
    /// refuses, and a refusal says nothing about what is there instead; this does. For the
    /// object: each pointer that leads to a row with an id, with the id. For the first such
    /// row: each string it holds, and each pointer in it that leads to a row with an id, with
    /// that row's name where the schema puts it. Byte by byte for the references, as columns
    /// are packed. Cached per object, since it is asked every tick and answers once.
    /// </remarks>
    public string SurveyOf(ulong details)
    {
        if (details == _surveyed)
        {
            return _survey;
        }

        _surveyed = details;
        _survey = details == 0 ? string.Empty : Survey(details);
        return _survey;
    }

    private string Survey(ulong details)
    {
        Span<byte> block = stackalloc byte[HuntBytes];
        if (!_reader.TryRead(details, block))
        {
            return "object unreadable";
        }

        var text = new System.Text.StringBuilder("object:");
        ulong firstRow = 0;
        int firstAt = -1;
        for (int at = 0; at + 8 <= HuntBytes; at += 8)
        {
            ulong pointer = BinaryPrimitives.ReadUInt64LittleEndian(block[at..]);
            if (RowId(pointer, out string id))
            {
                text.Append(" +0x").Append(at.ToString("X", System.Globalization.CultureInfo.InvariantCulture))
                    .Append("→\"").Append(Cut(id)).Append('"');
                if (firstRow == 0)
                {
                    firstRow = pointer;
                    firstAt = at;
                }
            }
        }

        if (firstRow == 0)
        {
            return text.Append(" no row").ToString();
        }

        Span<byte> row = stackalloc byte[RowHuntBytes];
        if (!_reader.TryRead(firstRow, row))
        {
            return text.Append("  row unreadable").ToString();
        }

        text.Append("  row@+0x").Append(firstAt.ToString("X", System.Globalization.CultureInfo.InvariantCulture)).Append(": str");
        int quoted = 0;
        for (int at = 0; at + 8 <= RowHuntBytes && quoted < SurveyMost; at += 8)
        {
            ulong pointer = BinaryPrimitives.ReadUInt64LittleEndian(row[at..]);
            if (!MemoryReaderExtensions.IsPlausiblePointer(pointer))
            {
                continue;
            }

            string value = _reader.ReadUnicodeString(pointer, LongestName).Trim();
            if (LooksLikeAName(value) || LooksLikeAnId(value))
            {
                text.Append(" +0x").Append(at.ToString("X", System.Globalization.CultureInfo.InvariantCulture))
                    .Append(" \"").Append(Cut(value)).Append('"');
                quoted++;
            }
        }

        text.Append("; ref");
        int referenced = 0;
        for (int at = 8; at + 8 <= RowHuntBytes && referenced < SurveyMost; at++)
        {
            ulong pointer = BinaryPrimitives.ReadUInt64LittleEndian(row[at..]);
            if (pointer == firstRow || !RowId(pointer, out string id))
            {
                continue;
            }

            text.Append(" +0x").Append(at.ToString("X", System.Globalization.CultureInfo.InvariantCulture))
                .Append("→\"").Append(Cut(id)).Append('"');
            string name = RowText(pointer + (ulong)_displayedName, LongestName);
            if (name.Length > 0)
            {
                text.Append("(\"").Append(Cut(name)).Append("\")");
            }

            referenced++;
        }

        return text.ToString();
    }

    private static string Cut(string value)
        => value.Length <= SurveyCut ? value : string.Concat(value.AsSpan(0, SurveyCut - 1), "…");

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
