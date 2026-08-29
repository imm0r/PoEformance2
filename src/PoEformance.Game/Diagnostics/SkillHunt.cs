using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Components;
using PoEformance.Game.Entities;

namespace PoEformance.Game.Diagnostics;

/// <summary>One string reachable from a skill object, and the pointer chain that reached it.</summary>
/// <param name="Chain">
/// Where it was found, as a readable path - <c>skill+0x018+0x000</c>. The chain is the finding:
/// a name is only useful if the same offsets produce it again next session.
/// </param>
public sealed record SkillText(string Chain, string Text);

/// <summary>What one sampled frame saw.</summary>
/// <param name="SkillObject">The <c>Actor.CurrentSkillPtr</c> value, or 0 when nothing is casting.</param>
/// <param name="CastTypeHits">
/// Offsets inside an <c>ActiveSkillDetails</c> entry that held this frame's live animation id as
/// an i32, with how many entries held it. The hunt for the field the schema records as broken -
/// see <see cref="SkillHunt"/>.
/// </param>
public sealed record SkillHuntSample(
    int AnimationId,
    short ActionId,
    ulong SkillObject,
    ulong Wrapper,
    IReadOnlyList<SkillText> Texts,
    IReadOnlyDictionary<int, int> CastTypeHits,
    int SkillTableEntries);

/// <summary>A chain that produced text, and what it produced per animation.</summary>
/// <param name="IsAFunction">
/// True when every animation id mapped to exactly one string AND no string was shared between
/// two animations. That is the property that makes a chain an identity rather than a coincidence.
/// </param>
public sealed record SkillNameCandidate(
    string Chain,
    IReadOnlyDictionary<int, IReadOnlyList<string>> ByAnimation,
    bool IsAFunction)
{
    /// <summary>How many different animations this chain produced a name for.</summary>
    public int Animations => ByAnimation.Count;
}

/// <summary>Everything the session concluded.</summary>
public sealed record SkillHuntFindings(
    int Frames,
    int CastingFrames,
    int FramesWithSkillObject,
    IReadOnlyList<ulong> SkillObjects,
    IReadOnlyDictionary<ulong, IReadOnlyList<int>> AnimationsPerObject,
    IReadOnlyList<SkillNameCandidate> Candidates,
    IReadOnlyList<(int Offset, int Frames)> CastTypeOffsets,
    int SkillTableEntries);

/// <summary>
/// Hunts for the SKILL'S NAME: follows <c>Actor.CurrentSkillPtr</c> until it reaches text.
/// </summary>
/// <remarks>
/// WHAT THIS IS FOR. The evasion warning knows an attack is committed and where it lands, and
/// nothing about WHAT it is - so every threat is modelled the same way, as a line to get off.
/// Naming the skill is the first step towards anything better, and it is worth having on its own:
/// a per-skill filter beats a per-animation one, because an animation id is a number nobody can
/// read and a skill id is a word.
///
/// WHY TEXT IS THE THING TO SEARCH FOR, and what makes this cheap rather than a fishing trip: in
/// the game's data both <c>ActiveSkills</c> and <c>GrantedEffects</c> carry <c>Id: string</c> as
/// their FIRST column (checked against dat-schema's poe2/_Core.gql), and this codebase already
/// resolves two other dat rows exactly that way - see <c>ItemReader</c>, where "the dat row's
/// first field is a pointer to the mod's id string". So a row in memory looks like a pointer to
/// printable wide text, which is a fingerprint almost nothing else has. The hunt walks two hops
/// out of the skill object and reports every string it can reach, with the offsets that reached
/// it.
///
/// WHAT THE COMMITTED FIXTURES ALREADY SETTLED, so this session does not re-ask it:
///
///  - THE SKILL OBJECT IS REAL AND ITS BLOCK IS KNOWN. Four distinct objects appear in
///    <c>session-2026-08-monsters.rec</c>, each with 0x200 bytes captured. Most of the pointers
///    inside point back INTO the object (embedded vectors); the ones that leave it are at 0x000,
///    0x008, 0x010, 0x1F0 and 0x1F8, and NOTHING in any recording follows them. That is the gap
///    this hunt exists to fill, and it is why it follows rather than merely samples.
///  - ONE ANIMATION IS NOT ONE SKILL OBJECT. The schema recorded a 1:1 correspondence from 27
///    frames of three skills; over the monster session it is four objects to three animation ids,
///    with two distinct objects both playing 299. So the object is finer-grained than the
///    animation - per cast, or two skills sharing an animation - and a reader must not assume the
///    pointer identifies the skill on its own. <c>SkillObjectIsFinerThanAnimationTests</c> pins it.
///  - THE ACTION WRAPPER DOES NOT CARRY IT, at least not in its first 0x200: no offset there ever
///    equals the frame's <c>CurrentSkillPtr</c>, and no pointer offset is a function of the
///    animation. That is a negative result worth the ink, because PoE1 put the skill in the
///    wrapper at 0x150 and PoE2 has TargetGrid there - so the obvious port is wrong. This hunt
///    reads FURTHER into the wrapper (0x400) because the question it answers - can the skill be
///    named at COMMITMENT rather than at cast - is the one that matters most for a warning.
///  - THE TIMING PROBLEM IS REAL AND MEASURED: over that fixture only 53 of 122 frames with a
///    skill action committed had a skill object at all. Naming a skill from
///    <c>CurrentSkillPtr</c> therefore cannot be the whole answer for a warning that wants to
///    fire before the cast is under way.
///
/// SO IT ALSO HUNTS THE OTHER ROUTE, through the actor's own granted-skill table. The schema
/// records <c>ActiveSkillDetails.CastType</c> at 0x0C as reading zero on every entry of a real
/// 41-skill table, i.e. wrong for PoE2, and notes that settling it "needs a recording made by a
/// build that reads the whole block". This is that build: it scans each entry for the frame's
/// LIVE animation id, so the offset that holds it turns up by itself rather than being guessed.
/// That route names a skill without any action pointer, which is the half the timing problem
/// needs.
/// </remarks>
public sealed class SkillHunt
{
    /// <summary>How much of the skill object to read.</summary>
    private const int SkillBlock = 0x200;

    /// <summary>
    /// How much of the action wrapper to read - DEEPER than the action hunt's 0x200.
    /// </summary>
    /// <remarks>
    /// The first 0x200 is already ruled out (see the type's remarks), so re-reading only that
    /// would be a session spent confirming a negative. PoE1's wrapper carried its skill at 0x150
    /// and PoE2 has moved everything, so "further out" is the only direction left to look.
    /// </remarks>
    private const int WrapperBlock = 0x400;

    /// <summary>How much to read at a pointer that left the first block.</summary>
    private const int FollowBlock = 0x100;

    /// <summary>Most pointers followed out of any one block, so a garbage block cannot run away.</summary>
    private const int MostPointers = 24;

    /// <summary>Longest name read. Skill ids are short words; this is generous.</summary>
    private const int MostChars = 64;

    /// <summary>Shortest run of printable characters that counts as a name rather than as noise.</summary>
    private const int LeastChars = 3;

    /// <summary>How much of a string to ask for at a time. See TextAt for why it is not one read.</summary>
    private const int ChunkBytes = 16;

    /// <summary>Most granted-skill entries walked.</summary>
    private const int MostSkills = 128;

    /// <summary>How much of an ActiveSkillDetails entry to scan for the live animation id.</summary>
    private const int DetailsBlock = 0x100;

    private readonly IMemoryReader _reader;
    private readonly OffsetSchema _schema;
    private readonly EntityReader _entities;

    private readonly int _currentSkill;
    private readonly int _skillAction;
    private readonly int _activeSkills;
    private readonly int _actionId;
    private readonly int _animationId;
    private readonly int _activeSkillPtr;
    private readonly int _skillEntrySize;

    /// <summary>
    /// The granted-skill table, read ONCE per address rather than per frame.
    /// </summary>
    /// <remarks>
    /// The table is static for the life of the character, so re-reading it sixty times a minute
    /// would put 32 KB of identical bytes through the recorder per tick and answer nothing new.
    /// Cached by entry address, which also survives the vector being re-read each frame.
    /// </remarks>
    private readonly Dictionary<ulong, byte[]> _details = [];

    private ulong _cachedPlayer;
    private ulong _cachedActor;

    public SkillHunt(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;
        _schema = schema;
        _entities = new EntityReader(reader, schema);

        StructDef actor = schema.Structs["Actor"];
        _currentSkill = actor.OffsetOf("CurrentSkillPtr");
        _skillAction = actor.OffsetOf("SkillActionPtr");
        _activeSkills = actor.OffsetOf("ActiveSkills");
        _actionId = actor.OffsetOf("ActionId");
        _animationId = actor.OffsetOf("AnimationId");

        _activeSkillPtr = schema.Structs["ActiveSkillStructure"].OffsetOf("ActiveSkillPtr");
        _skillEntrySize = (int)schema.Structs["ActiveSkillStructure"].Constants["Size"];
    }

    /// <summary>How many granted-skill entries the table held, for the status line.</summary>
    public int SkillTableEntries { get; private set; }

    /// <summary>Reads one frame, or null when there is nothing to read.</summary>
    public SkillHuntSample? SampleFrame(ulong gameStatesStatic)
    {
        GameChainAddresses chain = GameChain.Resolve(_reader, _schema, gameStatesStatic);
        if (!chain.InGame)
        {
            return null;
        }

        // A FAILED RESOLUTION IS NEVER CACHED - the same rule the action hunt records, and for
        // the same reason: the player pointer is stable all session, so one loading-screen frame
        // would otherwise switch the hunt off for good.
        if (chain.PlayerEntity != _cachedPlayer || _cachedActor == 0)
        {
            _cachedActor = _entities.Read(chain.PlayerEntity)?.Component("Actor") ?? 0;
            _cachedPlayer = chain.PlayerEntity;
        }

        if (_cachedActor == 0)
        {
            return null;
        }

        int animation = _reader.TryRead(_cachedActor + (ulong)_animationId, out int read) ? read : -1;
        short action = _reader.TryRead(_cachedActor + (ulong)_actionId, out short id) ? id : (short)0;
        ulong skill = _reader.ReadPointer(_cachedActor + (ulong)_currentSkill);
        ulong wrapper = _reader.ReadPointer(_cachedActor + (ulong)_skillAction);

        var texts = new List<SkillText>();
        Harvest(skill, SkillBlock, "skill", texts);
        Harvest(wrapper, WrapperBlock, "wrapper", texts);

        return new SkillHuntSample(
            animation,
            action,
            skill,
            wrapper,
            texts,
            ScanSkillTable(animation),
            SkillTableEntries);
    }

    /// <summary>
    /// Reads a block and collects every string reachable within two more hops.
    /// </summary>
    /// <remarks>
    /// TWO HOPS IS THE SHAPE OF THE ANSWER, not an arbitrary depth: the thing being looked for is
    /// object -> dat row -> id string, and a third hop would multiply the output by the branching
    /// factor while looking for something nobody has a reason to expect there.
    ///
    /// Pointers that land back INSIDE the block are skipped. The four skill objects in the
    /// committed fixture are full of them - embedded vectors whose begin/end point at their own
    /// storage - and following those only re-reads bytes already in hand.
    /// </remarks>
    private void Harvest(ulong address, int size, string label, List<SkillText> into)
    {
        if (!MemoryReaderExtensions.IsPlausiblePointer(address))
        {
            return;
        }

        var block = new byte[size];
        if (!_reader.TryRead(address, block))
        {
            return;
        }

        int followed = 0;
        for (int offset = 0; offset + sizeof(ulong) <= size && followed < MostPointers; offset += sizeof(ulong))
        {
            ulong first = BitConverter.ToUInt64(block, offset);
            if (!MemoryReaderExtensions.IsPlausiblePointer(first)
                || (first >= address && first < address + (ulong)size))
            {
                continue;
            }

            followed++;

            // The pointer might name the thing directly - a row whose id this IS.
            if (TextAt(first) is string direct)
            {
                into.Add(new SkillText($"{label}+0x{offset:X3}", direct));
                continue;
            }

            var next = new byte[FollowBlock];
            if (!_reader.TryRead(first, next))
            {
                continue;
            }

            for (int inner = 0; inner + sizeof(ulong) <= FollowBlock; inner += sizeof(ulong))
            {
                ulong second = BitConverter.ToUInt64(next, inner);
                if (MemoryReaderExtensions.IsPlausiblePointer(second) && TextAt(second) is string found)
                {
                    into.Add(new SkillText($"{label}+0x{offset:X3}+0x{inner:X3}", found));
                }
            }
        }
    }

    /// <summary>
    /// Walks the actor's granted-skill table looking for the frame's LIVE animation id.
    /// </summary>
    /// <remarks>
    /// THE FIELD IS FOUND BY WHAT IT CONTAINS, not by where a reference put it. The schema's
    /// CastType offset reads zero on every entry of a real table, so it is wrong for PoE2 - and
    /// the correct one is identifiable without guessing, because while a skill is being cast the
    /// actor's AnimationId IS that skill's cast type. Scanning each entry for the live value and
    /// keeping the offsets that hit, over several different skills, leaves one offset standing.
    ///
    /// Returns offset -> how many entries in the table held the value. An offset that hits on ONE
    /// entry is the interesting kind; one that hits on forty is a coincidence of a common number.
    /// </remarks>
    private Dictionary<int, int> ScanSkillTable(int animation)
    {
        var hits = new Dictionary<int, int>();
        if (animation <= 0)
        {
            return hits;
        }

        ulong begin = _reader.ReadPointer(_cachedActor + (ulong)_activeSkills);
        ulong end = _reader.ReadPointer(_cachedActor + (ulong)_activeSkills + sizeof(ulong));
        if (!MemoryReaderExtensions.IsPlausiblePointer(begin) || end <= begin || _skillEntrySize <= 0)
        {
            return hits;
        }

        long count = Math.Min((long)(end - begin) / _skillEntrySize, MostSkills);
        SkillTableEntries = (int)count;

        for (long index = 0; index < count; index++)
        {
            ulong entry = begin + (ulong)(index * _skillEntrySize);
            ulong details = _reader.ReadPointer(entry + (ulong)_activeSkillPtr);
            if (!MemoryReaderExtensions.IsPlausiblePointer(details))
            {
                continue;
            }

            if (!_details.TryGetValue(details, out byte[]? block))
            {
                block = new byte[DetailsBlock];
                if (!_reader.TryRead(details, block))
                {
                    continue;
                }

                _details[details] = block;
            }

            for (int offset = 0; offset + sizeof(int) <= DetailsBlock; offset += sizeof(int))
            {
                if (BitConverter.ToInt32(block, offset) == animation)
                {
                    hits[offset] = hits.GetValueOrDefault(offset) + 1;
                }
            }
        }

        return hits;
    }

    /// <summary>
    /// A printable wide string at an address, or null.
    /// </summary>
    /// <remarks>
    /// UTF-16 with an ASCII payload, which is what the game's id columns hold - so every second
    /// byte is zero and the rest are printable. That pattern is specific enough to be worth
    /// testing for: a block of pointers, floats or counters essentially never matches it, and a
    /// false positive here costs one line in a report rather than a wrong offset in the schema.
    ///
    /// READ IN SMALL CHUNKS, NOT AS ONE BLOCK, and that is a correctness point rather than a
    /// tuning one: ReadProcessMemory fails a span ENTIRELY if any part of it is unmapped, so
    /// asking for 128 bytes at a short string near the end of a page returns nothing at all -
    /// and a name that is missed because of where the allocator happened to put it is the worst
    /// kind of missing, since it comes back on the next run and looks like flakiness.
    ///
    /// A chunk that fails after some text has already been collected returns what was collected.
    /// Truncation cannot invent a name, and the analysis it feeds treats two different strings
    /// for one skill as a DISQUALIFICATION - so the failure mode is a chain rejected, never a
    /// wrong chain accepted.
    /// </remarks>
    public static string? TextAt(IMemoryReader reader, ulong address, int mostChars = MostChars)
    {
        ArgumentNullException.ThrowIfNull(reader);

        if (!MemoryReaderExtensions.IsPlausiblePointer(address))
        {
            return null;
        }

        Span<char> chars = stackalloc char[mostChars];
        Span<byte> chunk = stackalloc byte[ChunkBytes];
        int length = 0;

        while (length < mostChars)
        {
            if (!reader.TryRead(address + (ulong)(length * 2), chunk))
            {
                break;
            }

            for (int at = 0; at + 1 < chunk.Length && length < mostChars; at += 2)
            {
                if (chunk[at] == 0 && chunk[at + 1] == 0)
                {
                    return length >= LeastChars ? new string(chars[..length]) : null;
                }

                // High byte non-zero means it is not the ASCII-in-UTF-16 this looks for; a byte
                // outside printable range means it is not text at all.
                if (chunk[at + 1] != 0 || chunk[at] < 0x20 || chunk[at] > 0x7E)
                {
                    return null;
                }

                chars[length++] = (char)chunk[at];
            }
        }

        return length >= LeastChars ? new string(chars[..length]) : null;
    }

    private string? TextAt(ulong address) => TextAt(_reader, address);

    /// <summary>Turns a session's samples into the chains worth putting in the schema.</summary>
    public static SkillHuntFindings Analyze(IReadOnlyList<SkillHuntSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var objects = new Dictionary<ulong, SortedSet<int>>();
        var byChain = new Dictionary<string, Dictionary<int, SortedSet<string>>>();
        var castType = new Dictionary<int, int>();
        int casting = 0, withObject = 0, entries = 0;

        foreach (SkillHuntSample sample in samples)
        {
            entries = Math.Max(entries, sample.SkillTableEntries);

            if (MemoryReaderExtensions.IsPlausiblePointer(sample.Wrapper))
            {
                casting++;
            }

            if (MemoryReaderExtensions.IsPlausiblePointer(sample.SkillObject))
            {
                withObject++;
                objects.TryAdd(sample.SkillObject, []);
                objects[sample.SkillObject].Add(sample.AnimationId);
            }

            foreach (SkillText text in sample.Texts)
            {
                byChain.TryAdd(text.Chain, []);
                byChain[text.Chain].TryAdd(sample.AnimationId, []);
                byChain[text.Chain][sample.AnimationId].Add(text.Text);
            }

            // ONLY THE OFFSETS THAT HIT EXACTLY ONE ENTRY. A skill's cast type is unique in its
            // own table, so an offset matching forty entries is matching a common constant.
            foreach ((int offset, int count) in sample.CastTypeHits)
            {
                if (count == 1)
                {
                    castType[offset] = castType.GetValueOrDefault(offset) + 1;
                }
            }
        }

        var candidates = new List<SkillNameCandidate>();
        foreach ((string chain, Dictionary<int, SortedSet<string>> byAnimation) in byChain)
        {
            var mapped = byAnimation.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)[.. pair.Value]);

            // A FUNCTION IN BOTH DIRECTIONS: one name per animation, and no name shared between
            // two animations. The second half is what a shared vtable string or a class name
            // fails - those are the same word for every skill, which looks like an answer until
            // it is asked to tell two skills apart.
            List<string> all = [.. mapped.Values.SelectMany(names => names)];
            bool function = mapped.Values.All(names => names.Count == 1)
                            && all.Count == all.Distinct(StringComparer.Ordinal).Count();

            candidates.Add(new SkillNameCandidate(chain, mapped, function));
        }

        return new SkillHuntFindings(
            samples.Count,
            casting,
            withObject,
            [.. objects.Keys.Order()],
            objects.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<int>)[.. pair.Value]),
            [.. candidates.OrderByDescending(c => c.IsAFunction).ThenByDescending(c => c.Animations).ThenBy(c => c.Chain, StringComparer.Ordinal)],
            [.. castType.Select(pair => (pair.Key, pair.Value)).OrderByDescending(pair => pair.Value)],
            entries);
    }

    /// <summary>Prints the findings.</summary>
    public static void Report(SkillHuntFindings findings, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(output);

        output.WriteLine();
        output.WriteLine("SKILL NAME HUNT");
        output.WriteLine(new string('-', 72));
        output.WriteLine(
            $"  {findings.Frames} frames, {findings.CastingFrames} with a skill action, "
            + $"{findings.FramesWithSkillObject} with a skill object, "
            + $"{findings.SkillTableEntries} granted skills in the table");

        if (findings.CastingFrames == 0)
        {
            output.WriteLine();
            output.WriteLine("  NOTHING WAS CAST. Run this while casting several DIFFERENT skills -");
            output.WriteLine("  the whole method is telling them apart, so one skill proves nothing.");
            return;
        }

        output.WriteLine();
        output.WriteLine($"  skill objects seen: {findings.SkillObjects.Count}");
        foreach (ulong at in findings.SkillObjects)
        {
            IReadOnlyList<int> animations = findings.AnimationsPerObject[at];
            output.WriteLine($"    {at:X}  animation(s) {string.Join(",", animations)}");
        }

        output.WriteLine();
        output.WriteLine("  chains that reached text (a * marks one that tells every skill apart):");
        if (findings.Candidates.Count == 0)
        {
            output.WriteLine("    none - no readable string within two hops of either object.");
        }

        foreach (SkillNameCandidate candidate in findings.Candidates.Take(40))
        {
            string mark = candidate.IsAFunction ? "*" : " ";
            output.WriteLine($"   {mark} {candidate.Chain,-28} {candidate.Animations} animation(s)");
            foreach ((int animation, IReadOnlyList<string> names) in candidate.ByAnimation.OrderBy(p => p.Key))
            {
                output.WriteLine($"        {animation,5} -> {string.Join(" | ", names)}");
            }
        }

        output.WriteLine();
        output.WriteLine("  ActiveSkillDetails offsets holding the live animation id on exactly one entry:");
        if (findings.CastTypeOffsets.Count == 0)
        {
            output.WriteLine("    none - the cast type is not an i32 in the first 0x100 of an entry.");
        }

        foreach ((int offset, int frames) in findings.CastTypeOffsets.Take(10))
        {
            output.WriteLine($"    0x{offset:X2}  on {frames} frame(s)");
        }
    }
}
