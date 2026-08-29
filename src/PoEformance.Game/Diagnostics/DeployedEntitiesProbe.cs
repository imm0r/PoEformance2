using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Entities;

namespace PoEformance.Game.Diagnostics;

/// <summary>One decoded entry of a candidate DeployedEntities vector.</summary>
/// <param name="InEntityMap">
/// Whether <paramref name="EntityId"/> names an entity that is actually in the area's map.
/// This is the whole point of the probe - see <see cref="DeployedEntitiesProbe"/>.
/// </param>
public readonly record struct DeployedEntry(
    uint EntityId, int SkillsDatId, int ObjectType, int Counter, bool InEntityMap);

/// <summary>What one candidate offset, read at one stride, turned out to hold.</summary>
/// <param name="Readable">
/// Whether the vector header could be read at all. Distinct from <paramref name="Empty"/>
/// ON PURPOSE: replaying a recording made before this probe existed fails every read here,
/// and reporting that as "nothing was deployed" would invent a measurement out of a file
/// that simply does not contain one.
/// </param>
public readonly record struct DeployedReading(
    int Offset,
    string Label,
    int Stride,
    ulong Begin,
    ulong End,
    bool Readable,
    bool Empty,
    bool HeaderSane,
    long ByteLength,
    long Count,
    IReadOnlyList<DeployedEntry> Entries)
{
    /// <summary>Entries whose id names a real entity in the area.</summary>
    public int Matched => Entries.Count(e => e.InEntityMap);

    /// <summary>
    /// A reading that the game itself confirms: entries that decode to entities the area
    /// actually contains.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT "the header looks like a vector and the count is small". Both
    /// candidates sit in the same component a few bytes apart, so both can easily produce a
    /// plausible pair of pointers and a believable count; that is the check that made the
    /// last two Actor vectors look settled when only one of them was. An id that names a
    /// living entity cannot be produced by luck.
    ///
    /// Entries.Count > 0 rather than Count > 0, and the difference is not cosmetic: a span
    /// whose length does not divide by the stride decodes NOTHING, and "every entry matched"
    /// is vacuously true of no entries at all. That reading is the stride failure this probe
    /// exists to catch, so it must not be able to report itself as the answer.
    /// </remarks>
    public bool Confirmed => !Empty && HeaderSane && Entries.Count > 0 && Matched == Entries.Count;
}

/// <summary>The probe's verdict across every candidate it tried.</summary>
public readonly record struct DeployedProbeResult(
    bool InGame,
    ulong Actor,
    int SchemaOffset,
    IReadOnlyList<DeployedReading> Readings)
{
    /// <summary>The reading the game confirmed, when exactly one did.</summary>
    public DeployedReading? Winner
    {
        get
        {
            DeployedReading[] confirmed = [.. Readings.Where(r => r.Confirmed)];
            return confirmed.Length == 1 ? confirmed[0] : null;
        }
    }

    /// <summary>
    /// True when not one candidate could be read - a replay of a session recorded before
    /// this probe existed, which contains no bytes for these addresses at all.
    /// </summary>
    public bool NoData => InGame && Readings.Count > 0 && Readings.All(r => !r.Readable);

    /// <summary>
    /// True when nothing was deployed, so no candidate could be told from any other.
    /// </summary>
    /// <remarks>
    /// THE STATE THAT HAS TO BE REPORTED RATHER THAN SWALLOWED. An empty vector is what a
    /// correct offset shows with no minions out AND what a wrong one shows always, so a
    /// session in this state proves nothing at all. Reading it as "empty, therefore fine" is
    /// exactly how the 0xC18 reading survived as long as it did.
    /// </remarks>
    public bool Inconclusive =>
        InGame && !NoData && Readings.Count > 0 && Readings.All(r => r.Empty || !r.HeaderSane);
}

/// <summary>
/// Reads Actor.DeployedEntities at the schema's offset AND at its neighbours, and asks the
/// GAME which one is right: the entries of a correct vector name entities that are really
/// in the area.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS AT ALL. The schema put this vector at 0xC18 with a note saying an empty
/// reading was expected and harmless. GameHelper2 has since moved it to 0xC28 - by the same
/// +0x10, in the same commit, as the AnimationId shift this project already confirmed in
/// game. A 0x10-short read lands in padding and reads begin==end forever, which is the exact
/// symptom the note called harmless, so the note could never have caught the drift it was
/// dismissing.
///
/// It could not be settled from the recordings either: every fixture under tests/fixtures
/// was checked and not one holds a byte of the Actor tail, because nothing in the build had
/// ever read it. A recording only ever contains the reads the running build performed - so
/// the fix for an unanswerable question is to make the build ASK it, which is what this does.
/// It runs unconditionally for the same reason PlayerProbe does: the reads are what a
/// --record session needs to carry, and they cost about a hundred bytes.
///
/// It reads the schema's own offset plus its two neighbours rather than a hardcoded pair, so
/// it keeps working as a re-drift aid after this drift is settled - whichever way the field
/// moves next, the probe still brackets it.
/// </remarks>
public sealed class DeployedEntitiesProbe
{
    /// <summary>How far either side of the schema's offset to look.</summary>
    /// <remarks>
    /// 0x10 because that is the step this struct actually drifts in: AnimationId moved
    /// +0x10 and this field moved +0x10 behind it.
    /// </remarks>
    private const int NeighbourStep = 0x10;

    /// <summary>The PoE1 element size, kept so a stride drift is visible rather than fatal.</summary>
    /// <remarks>
    /// GameHelper2's note: "PoE2 grew this element from 20 to 24 bytes". A reader that
    /// divides the vector's byte length by the wrong element size rejects the whole vector
    /// and reports zero entries - a SECOND way to read "no deployed entities" off a correct
    /// pointer. Trying both strides is what tells the two failures apart.
    /// </remarks>
    private const int LegacyStride = 0x14;

    /// <summary>Most entries to decode. A player cannot deploy anything like this many.</summary>
    private const int MostEntries = 16;

    private readonly IMemoryReader _reader;
    private readonly OffsetSchema _schema;
    private readonly EntityReader _entities;
    private readonly EntityMapReader _map;

    public DeployedEntitiesProbe(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;
        _schema = schema;
        _entities = new EntityReader(reader, schema);
        _map = new EntityMapReader(reader, schema);
    }

    public DeployedProbeResult Run(ulong gameStatesStatic)
    {
        GameChainAddresses chain = GameChain.Resolve(_reader, _schema, gameStatesStatic);
        int schemaOffset = _schema.Structs["Actor"].OffsetOf("DeployedEntities");
        if (!chain.InGame)
        {
            return new DeployedProbeResult(false, 0, schemaOffset, []);
        }

        Entity? player = _entities.Read(chain.PlayerEntity);
        ulong actor = player?.Component("Actor") ?? 0;
        if (actor == 0)
        {
            return new DeployedProbeResult(true, 0, schemaOffset, []);
        }

        // The ids a correct vector must name - the reference the game supplies for free.
        // Visuals are included because leaving them out can only ever produce a FALSE
        // NEGATIVE here: anything deployed that the filter drops reads as an id the area
        // does not contain, which is indistinguishable from a wrong offset.
        int awake = _schema.Structs["AreaInstance"].OffsetOf("AwakeEntities");
        Dictionary<uint, ulong> live =
            _map.ReadEntityPointers(chain.AreaInstance + (ulong)awake, includeVisuals: true);

        StructDef entry = _schema.Structs["DeployedEntity"];
        int stride = (int)entry.Constants["Size"];

        var readings = new List<DeployedReading>();
        foreach ((int offset, string label) in Candidates(schemaOffset))
        {
            readings.Add(ReadAt(actor, offset, label, stride, entry, live));

            // Only worth a second reading when the stride could change the answer. It does
            // exactly when the byte span divides by one element size and not the other.
            DeployedReading legacy = ReadAt(actor, offset, label, LegacyStride, entry, live);
            if (!legacy.Empty && legacy.HeaderSane && legacy.ByteLength % LegacyStride == 0
                && legacy.ByteLength % stride != 0)
            {
                readings.Add(legacy);
            }
        }

        return new DeployedProbeResult(true, actor, schemaOffset, readings);
    }

    private static IEnumerable<(int Offset, string Label)> Candidates(int schemaOffset) =>
    [
        (schemaOffset, "schema"),
        (schemaOffset - NeighbourStep, "-0x10"),
        (schemaOffset + NeighbourStep, "+0x10"),
    ];

    private DeployedReading ReadAt(
        ulong actor, int offset, string label, int stride, StructDef entry, Dictionary<uint, ulong> live)
    {
        ulong at = actor + (ulong)offset;
        if (!_reader.TryRead(at, out ulong begin) || !_reader.TryRead(at + 8, out ulong end))
        {
            return new DeployedReading(offset, label, stride, 0, 0, false, false, false, 0, 0, []);
        }

        if (begin == 0 && end == 0)
        {
            return new DeployedReading(offset, label, stride, 0, 0, true, true, true, 0, 0, []);
        }

        // One null end is not a vector, and subtracting the two would produce a number that
        // reads like a measurement - see SchemaFieldReader for the same trap.
        bool sane = MemoryReaderExtensions.IsPlausiblePointer(begin)
                    && MemoryReaderExtensions.IsPlausiblePointer(end)
                    && end >= begin;
        if (!sane)
        {
            return new DeployedReading(offset, label, stride, begin, end, true, false, false, 0, 0, []);
        }

        long bytes = (long)(end - begin);
        long count = bytes / stride;
        if (bytes % stride != 0 || count > 4096)
        {
            return new DeployedReading(offset, label, stride, begin, end, true, bytes == 0, true, bytes, count, []);
        }

        int idAt = entry.OffsetOf("EntityId");
        int datAt = entry.OffsetOf("ActiveSkillsDatId");
        int typeAt = entry.OffsetOf("DeployedObjectType");
        int counterAt = entry.OffsetOf("Counter");

        var entries = new List<DeployedEntry>();
        for (long i = 0; i < Math.Min(count, MostEntries); i++)
        {
            ulong element = begin + (ulong)(i * stride);
            if (!_reader.TryRead(element + (ulong)idAt, out uint id))
            {
                break;
            }

            entries.Add(new DeployedEntry(
                id,
                _reader.Read<int>(element + (ulong)datAt),
                _reader.Read<int>(element + (ulong)typeAt),
                _reader.Read<int>(element + (ulong)counterAt),
                live.ContainsKey(id)));
        }

        return new DeployedReading(
            offset, label, stride, begin, end, true, bytes == 0, true, bytes, count, entries);
    }

    /// <summary>Runs the probe and writes a human-readable verdict.</summary>
    public DeployedProbeResult Report(ulong gameStatesStatic, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        DeployedProbeResult r = Run(gameStatesStatic);

        output.WriteLine();
        output.WriteLine("deployed-entities probe  (Actor.DeployedEntities: the player's own totems/minions)");

        if (!r.InGame)
        {
            output.WriteLine("  --    not in an area.");
            return r;
        }

        if (r.Actor == 0)
        {
            output.WriteLine("  FAIL  the player has no Actor component - component lookup or offset wrong.");
            return r;
        }

        output.WriteLine($"  actor       0x{r.Actor:X}   schema offset +0x{r.SchemaOffset:X}");
        foreach (DeployedReading reading in r.Readings)
        {
            string head = !reading.Readable
                ? "no data"
                : reading.Empty
                    ? "empty"
                    : reading.HeaderSane
                        ? $"0x{reading.Begin:X}..0x{reading.End:X} {reading.ByteLength}B"
                        : $"0x{reading.Begin:X}..0x{reading.End:X} NOT A VECTOR";

            string body = reading.Entries.Count == 0
                ? reading.Empty || !reading.HeaderSane ? "" : $" -> {reading.Count} elements, none decoded"
                : $" -> {reading.Count} elements, {reading.Matched}/{reading.Entries.Count} in the entity map"
                  + $"  [{string.Join(", ", reading.Entries.Take(4).Select(Describe))}]";

            output.WriteLine(
                $"  +0x{reading.Offset:X3} {reading.Label,-6} stride 0x{reading.Stride:X2}  {head}{body}");
        }

        if (r.NoData)
        {
            // A replay, not a measurement. Saying "nothing deployed" here would be reading a
            // fact out of a file that has none - the same mistake in a new place.
            output.WriteLine(
                "  NO DATA  none of the candidates could be read. Replaying a session recorded before");
            output.WriteLine(
                "           this probe existed cannot answer the question - make a fresh --record.");
            return r;
        }

        if (r.Inconclusive)
        {
            // Say it plainly. This reading is the one that looks like a pass and is not.
            output.WriteLine(
                "  INCONCLUSIVE  nothing is deployed, so every candidate reads empty and none can be");
            output.WriteLine(
                "                told from the others. Summon minions or place a totem and re-run with");
            output.WriteLine(
                "                --record; an empty vector is NOT evidence that the offset is right.");
            return r;
        }

        if (r.Winner is DeployedReading winner)
        {
            output.WriteLine(winner.Offset == r.SchemaOffset
                ? $"  CONFIRMED  the schema offset +0x{winner.Offset:X} decodes {winner.Matched} real entities at stride 0x{winner.Stride:X}."
                : $"  DRIFT  +0x{winner.Offset:X} ({winner.Label}) decodes {winner.Matched} real entities at stride 0x{winner.Stride:X}, "
                  + $"the schema's +0x{r.SchemaOffset:X} does not. Move it.");
            return r;
        }

        output.WriteLine(
            "  UNSETTLED  something is deployed but no candidate decoded entries the entity map knows.");
        output.WriteLine(
            "             The field has moved further than +/-0x10, or the element size changed again.");
        return r;
    }

    private static string Describe(DeployedEntry e)
        => $"id {e.EntityId}{(e.InEntityMap ? "" : "?")} type {e.ObjectType}";
}
