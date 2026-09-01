using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Entities;

namespace PoEformance.Features;

/// <summary>One component an entity carries.</summary>
/// <param name="Fields">
/// How many fields the schema has for this component. Zero is the interesting case: the game
/// says the component is there and nobody has written down what is in it.
/// </param>
/// <param name="Values">
/// The component's declared fields, read - but only while the window has this one open.
/// Null means "not asked for", which is a different thing from a component with nothing in
/// it, and the window says so rather than drawing an empty list under an open row.
/// </param>
public readonly record struct ComponentEntry(
    string Name, ulong Address, int Fields, IReadOnlyList<FieldReading>? Values = null)
{
    /// <summary>True when at least one field of this component is written down.</summary>
    /// <remarks>
    /// FIELDS, not "the schema has a struct of this name" - which is what this asked before,
    /// and the answer flattered us. A struct declared with no fields (BaseEvents, Functions
    /// and InteractionAction all are) counted as described, so a monster reporting "5 of 19
    /// not described" really had 7 that nobody had written a byte of. A number whose entire
    /// job is to point at what is unknown must not round it down.
    /// </remarks>
    public bool Described => Fields > 0;
}

/// <summary>How often a component turns up across a whole area, and how much of it is written down.</summary>
public readonly record struct ComponentTally(string Name, int Count, int Fields)
{
    /// <inheritdoc cref="ComponentEntry.Described"/>
    public bool Described => Fields > 0;
}

/// <summary>One timed effect sitting on the inspected entity.</summary>
/// <remarks>
/// Read for the SELECTED entity only, which is what makes it affordable: the Buffs component
/// is a vector walk and several reads per effect, fine once on demand and not fine for every
/// entity every tick. The player's buffs are read every tick for the flask rules; this is the
/// same reader pointed at whatever is under the cursor.
///
/// Why it is worth having at all: a thing that expires says so here, with a clock on it. The
/// entity classification asks whether something is temporary by looking for a DiesAfterTime
/// COMPONENT, and a flame wall does not carry one - measured, over 9,313 sightings - which
/// left "it obviously has a duration, the game shows it in the tooltip" and "we cannot see a
/// duration" both plausible and neither checked. This is what checks it.
/// </remarks>
public readonly record struct TimedEffect(string Name, float TimeLeft, float TotalTime, int Charges);

/// <summary>One (stat id, value) pair off the inspected entity's Stats component.</summary>
/// <remarks>
/// The game keeps an entity's numbers here as a flat vector of pairs - an id and an integer,
/// eight bytes each - and the ids are the same ones the game's own Stats table names. So this
/// is a list of everything the game currently believes about that entity, in a form that can
/// be looked up rather than guessed at: the AHK tool ships 27,004 of those names extracted
/// from the game data, in which 347 is base_skill_effect_duration and 351 is
/// skill_effect_duration.
///
/// Read for the SELECTED entity only, same as the buffs beside it, and for the same reason.
/// </remarks>
/// <param name="Source">
/// Which of the entity's two StatsInternal bags this came from. Load-bearing: the same stat
/// appears in both with DIFFERENT values, so a list that concatenates them and drops the
/// label is a list in which every number is ambiguous. Read off the merged list, this
/// character's mana was 4,580 and their fire resistance 55%; their character sheet says 5,415
/// and 73%, and both of those are in the other bag.
/// </param>
public readonly record struct EntityStat(uint Id, int Value, string Name = "", string Source = "");

/// <summary>Narrowing a stat list to what somebody is looking for.</summary>
/// <remarks>
/// Here rather than in the window that draws the box, so it can be tested: a search that
/// quietly fails to match is worse than no search at all. The list it narrows is the ANSWER TO
/// "IS THIS STAT THERE", and a player who types a name, sees nothing and concludes the stat is
/// absent has been misled by the tool - which is the one thing this browser must not do.
/// </remarks>
public static class EntityStats
{
    /// <summary>
    /// The stats a search leaves, in the order they were read.
    /// </summary>
    /// <remarks>
    /// Name, id AND value, because all three are things somebody arrives with: a name from the
    /// game's Stats table, an id from a schema note, and a value off a tooltip they are trying
    /// to find the source of.
    ///
    /// The ORDER is left alone. The list is grouped by which bag each stat came from, and the
    /// same stat sits in both bags with different values - so sorting by anything would pull
    /// that grouping apart and make every row ambiguous again.
    /// </remarks>
    public static List<EntityStat> Matching(IReadOnlyList<EntityStat> stats, string? search)
    {
        ArgumentNullException.ThrowIfNull(stats);

        if (string.IsNullOrEmpty(search))
        {
            return [.. stats];
        }

        var found = new List<EntityStat>();
        foreach (EntityStat stat in stats)
        {
            if (Matches(stat, search))
            {
                found.Add(stat);
            }
        }

        return found;
    }

    private static bool Matches(EntityStat stat, string search)
        => stat.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
           || Text(stat.Id).Contains(search, StringComparison.Ordinal)
           || Text(stat.Value).Contains(search, StringComparison.Ordinal);

    /// <summary>Invariant, so a search for "4000" matches on every machine's locale.</summary>
    private static string Text<T>(T value)
        where T : IFormattable
        => value.ToString(null, System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>What the entity browser wants to see.</summary>
/// <param name="Survey">
/// Entities to count components across, for the one-shot survey. Supplied by the window
/// because it already has the area's entity list - so this costs no extra scan.
/// </param>
/// <param name="Expand">
/// Components whose declared fields are wanted, by name. Only the open ones, because this is
/// a read per field and an entity carrying twenty components would otherwise pay for all of
/// them every tick to show a handful.
/// </param>
public sealed record EntityRequest(
    bool Enabled = false,
    ulong Address = 0,
    IReadOnlyList<ulong>? Survey = null,
    int SurveySequence = 0,
    IReadOnlyList<string>? Expand = null)
{
    public static EntityRequest Idle { get; } = new();
}

/// <summary>One entity, taken apart.</summary>
public sealed record EntityView(
    ulong Address,
    uint Id,
    string Path,
    IReadOnlyList<ComponentEntry> Components,
    IReadOnlyList<ComponentTally> Survey,
    int SurveyedEntities,
    string Status,
    IReadOnlyList<TimedEffect>? Effects = null,
    string EffectsNote = "",
    IReadOnlyList<EntityStat>? Stats = null,
    string StatsNote = "",

    /// <summary>
    /// Whether a stat bag held more pairs than were read.
    /// </summary>
    /// <remarks>
    /// Carried as a FLAG and not only inside <see cref="StatsNote"/>, because the note is prose
    /// and the thing that has to react to it is a search: over a truncated list, "no rows" is
    /// not an answer, and only something the code can branch on can say so where somebody is
    /// looking. The note read "(of 392)" while a search over the 256 that were read reported an
    /// absence, and prose in the line above did not stop that being taken at face value.
    /// </remarks>
    bool StatsCutShort = false)
{
    public static EntityView Empty { get; } = new(0, 0, string.Empty, [], [], 0, "nothing selected");

    /// <summary>What is currently on this entity, with its clock. Empty when it carries no Buffs.</summary>
    public IReadOnlyList<TimedEffect> Timed => Effects ?? [];

    /// <summary>The entity's own stat pairs. Empty when it carries no Stats component.</summary>
    public IReadOnlyList<EntityStat> Numbers => Stats ?? [];

    /// <summary>How many of this entity's components nobody has described.</summary>
    public int Undescribed => Components.Count(component => !component.Described);
}

/// <summary>
/// Serves the entity browser: what an entity is made of, and what is NOT yet understood.
/// </summary>
/// <remarks>
/// The most direct route to something new in this whole tool, and it needs no reverse
/// engineering to reach: the game already names every component an entity carries, and the
/// reader already lists them. Around twenty have a decoder. The rest have been sitting there
/// in plain sight the entire time, addressed and named, with nothing reading them.
///
/// So this does two things. It takes one entity apart and marks which of its components the
/// schema describes - and it can count components across a WHOLE area, which answers the
/// question that is otherwise impossible to ask: what exists here that nothing understands?
/// A component carried by two entities in a map is far more interesting than one carried by
/// two hundred, and neither is visible one entity at a time.
///
/// This is also the thing a general memory tool cannot do. Cheat Engine can diff bytes; it
/// cannot say "the entity under your cursor has fourteen components and nine are unknown".
/// </remarks>
public sealed class EntityInspector
{
    /// <summary>Entities read in one survey.</summary>
    /// <remarks>
    /// A survey is a few reads per entity, so a crowded area is a few thousand - fine as a
    /// one-shot on the reader thread, and not fine every tick, which is why it is not.
    /// </remarks>
    public const int MaxSurvey = 1024;

    private readonly IMemoryReader _reader;
    private readonly EntityReader _entities;
    private readonly PoEformance.Game.Components.BuffsReader _buffs;
    private readonly PoEformance.Game.Components.StatNames _statNames;
    private readonly OffsetSchema _schema;

    private EntityRequest _request = EntityRequest.Idle;
    private EntityView _view = EntityView.Empty;

    private int _servedSurvey;
    private IReadOnlyList<ComponentTally> _lastSurvey = [];
    private int _lastSurveyed;

    public EntityInspector(
        IMemoryReader reader,
        OffsetSchema schema,
        PoEformance.Game.Components.StatNames? statNames = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _statNames = statNames ?? PoEformance.Game.Components.StatNames.Empty;
        _reader = reader;
        _schema = schema;
        _entities = new EntityReader(reader, schema);
        _buffs = new PoEformance.Game.Components.BuffsReader(reader, schema);
    }

    /// <summary>The newest reading. Never blocks, never null, never partially built.</summary>
    public EntityView View => Volatile.Read(ref _view);

    /// <summary>Tells the reader which entity to take apart. Called from the render thread.</summary>
    public void Request(EntityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Volatile.Write(ref _request, request);
    }

    /// <summary>Reads one entity, and a survey when one was asked for. On the reader thread.</summary>
    public void Service()
    {
        EntityRequest request = Volatile.Read(ref _request);
        if (!request.Enabled)
        {
            return;
        }

        try
        {
            Volatile.Write(ref _view, Build(request));
        }
        catch (Exception exception)
        {
            // Every address here belongs to an entity that can stop existing mid-read, which
            // makes a failed read ordinary rather than exceptional.
            Volatile.Write(ref _view, View with { Status = $"read failed: {exception.Message}" });
        }
    }

    private EntityView Build(EntityRequest request)
    {
        if (request.SurveySequence != _servedSurvey)
        {
            _servedSurvey = request.SurveySequence;
            (_lastSurvey, _lastSurveyed) = Survey(request.Survey ?? []);
        }

        if (request.Address == 0)
        {
            return EntityView.Empty with { Survey = _lastSurvey, SurveyedEntities = _lastSurveyed };
        }

        Entity? entity = _entities.Read(request.Address);
        if (entity is null)
        {
            return EntityView.Empty with
            {
                Survey = _lastSurvey,
                SurveyedEntities = _lastSurveyed,
                Status = $"0x{request.Address:X} did not read as an entity",
            };
        }

        List<ComponentEntry> components =
        [
            .. entity.Components
                .Select(pair => new ComponentEntry(
                    pair.Key, pair.Value, FieldsOf(pair.Key), ValuesOf(pair.Key, pair.Value, request.Expand)))
                .OrderBy(component => component.Described)      // the unknown ones first: they are the point
                .ThenBy(component => component.Name, StringComparer.Ordinal),
        ];

        // One entity, on demand, so the vector walk is affordable here in a way it is not in
        // the world read. A failed read is ordinary - the entity can stop existing mid-walk.
        //
        // THREE OUTCOMES, KEPT APART, because the first version of this drew nothing at all
        // when there was nothing to draw: "this entity carries no Buffs component", "it does
        // and there is nothing on it" and "it does and nobody could read it" then looked
        // identical on screen, and so did "you are running a build without this feature". A
        // screenshot of that answers no question, which was the whole point of adding it.
        List<TimedEffect> effects = [];
        string note = string.Empty;
        ulong buffs = entity.Component("Buffs");
        if (buffs != 0)
        {
            StructDef layout = _schema.Structs["Buffs"];
            bool readable = _reader.TryRead(buffs + (ulong)layout.OffsetOf("StatusEffectFirst"), out ulong _)
                            && _reader.TryRead(buffs + (ulong)layout.OffsetOf("StatusEffectLast"), out ulong _);

            effects.AddRange(_buffs.Read(buffs).All
                .Select(buff => new TimedEffect(buff.Name, buff.TimeLeft, buff.TotalTime, buff.Charges)));

            note = !readable
                ? "carries Buffs, and it could not be read"
                : effects.Count > 0
                    ? $"{effects.Count} on this entity:"
                    : "carries Buffs, with nothing on it";
        }

        (List<EntityStat> numbers, string statsNote, bool cutShort) = ReadStats(entity);

        return new EntityView(
            entity.Address,
            entity.Id,
            entity.Path,
            components,
            _lastSurvey,
            _lastSurveyed,
            $"{components.Count} components, {components.Count(c => !c.Described)} not described"
                + (effects.Count > 0 ? $", {effects.Count} timed" : string.Empty),
            effects,
            note,
            numbers,
            statsNote,
            cutShort);
    }

    /// <summary>
    /// Reads the entity's own stat pairs, and says which kind of nothing it found.
    /// </summary>
    /// <remarks>
    /// A flat vector of (id u32, value i32), which is why it costs one read for the bounds
    /// and one for the block rather than a walk. Capped, because a corrupt pair of pointers
    /// must not turn into a gigabyte of reading - the cap is reported rather than hidden,
    /// since a truncated list that looks complete is the failure worth avoiding here.
    /// </remarks>
    private (List<EntityStat> Stats, string Note, bool CutShort) ReadStats(Entity entity)
    {
        var stats = new List<EntityStat>();
        ulong component = entity.Component("Stats");
        if (component == 0)
        {
            return (stats, string.Empty, false);
        }

        // THE COMPONENT HOLDS NO PAIRS. It holds pointers to StatsInternal structures, and
        // the pairs are in those - which is worth stating because the first version of this
        // read the vector straight off the component, got a zeroed field on every entity, and
        // reported "nothing readable in it" for a flame wall that has stats like anything
        // else. Verified against the recording: Stats+0xF8 and Stats+0x100 both read 0x0.
        StructDef layout = _schema.Structs["Stats"];
        var notes = new List<string>();
        var read = new Dictionary<ulong, string>();
        bool cutShort = false;

        foreach (string source in new[] { "StatsByBuffAndActions", "StatsByItems" })
        {
            if (!_reader.TryRead(component + (ulong)layout.OffsetOf(source), out ulong internals))
            {
                notes.Add($"{source} could not be read");
                continue;
            }

            if (!MemoryReaderExtensions.IsPlausiblePointer(internals))
            {
                notes.Add($"no {source}");
                continue;
            }

            // The two pointers are sometimes the SAME object - a flame wall's are - and
            // listing it twice under two headings reads as two independent sightings of a
            // number that was only ever read once. Said instead of shown.
            if (read.TryGetValue(internals, out string? already))
            {
                notes.Add($"{source} is the same bag as {already}");
                continue;
            }

            read[internals] = source;
            (int taken, string note, bool capped) = ReadPairs(internals, stats, source);
            notes.Add($"{taken} from {source}{note}");
            cutShort |= capped;
        }

        return (stats, $"carries Stats: {string.Join(", ", notes)}", cutShort);
    }

    /// <summary>Pulls the (id, value) pairs out of one StatsInternal, into <paramref name="into"/>.</summary>
    /// <remarks>
    /// Capped, and the cap is REPORTED: a list that stops at 256 while looking complete is how
    /// somebody concludes a stat is absent when it is merely off the end. An empty vector is
    /// begin == end == null and means the entity has no stats from that source, which is not
    /// the same as a vector nobody could read - the two used to print the same sentence.
    /// </remarks>
    private (int Taken, string Note, bool CutShort) ReadPairs(ulong internals, List<EntityStat> into, string source)
    {
        // 1024 rather than 256, because 256 was under what a real character carries. A player's
        // StatsByBuffAndActions bag measured 392 pairs, so the list stopped 136 short - and a
        // search over what was read then answered "that stat is not on this entity" about a
        // stat that may simply have been off the end. The note said "(of 392)" the whole time,
        // which is the only reason it was caught; being told is not the same as it not
        // happening. 1024 is 8 KB in one read, covers that bag two and a half times over, and
        // still refuses a runaway pointer - the count guard above rejects anything past 65536
        // before this cap is reached at all.
        const int MaxStats = 1024;
        const int PairSize = 8;

        StructDef layout = _schema.Structs["StatsInternal"];
        if (!_reader.TryRead(internals + (ulong)layout.OffsetOf("StatsVector"), out ulong first)
            || !_reader.TryRead(internals + (ulong)layout.OffsetOf("StatsVectorLast"), out ulong last))
        {
            return (0, " (unreadable)", false);
        }

        if (first == 0 && last == 0)
        {
            return (0, " (empty)", false);
        }

        if (!MemoryReaderExtensions.IsPlausiblePointer(first) || last < first)
        {
            return (0, " (not a vector)", false);
        }

        long count = (long)(last - first) / PairSize;
        if (count is < 0 or > 65536)
        {
            return (0, $" ({count} pairs - not believable)", false);
        }

        int wanted = (int)Math.Min(count, MaxStats);
        byte[] block = new byte[wanted * PairSize];
        if (wanted > 0 && !_reader.TryRead(first, block))
        {
            return (0, " (vector unreadable)", false);
        }

        for (int i = 0; i < wanted; i++)
        {
            uint id = BitConverter.ToUInt32(block, i * PairSize);
            into.Add(new EntityStat(
                id,
                BitConverter.ToInt32(block, (i * PairSize) + 4),
                _statNames.Of(id) ?? string.Empty,
                source));
        }

        return (wanted, count > wanted ? $" (of {count})" : string.Empty, count > wanted);
    }

    /// <summary>Counts every component name across a set of entities.</summary>
    private (IReadOnlyList<ComponentTally> Tally, int Read) Survey(IReadOnlyList<ulong> addresses)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        int read = 0;

        foreach (ulong address in addresses.Take(MaxSurvey))
        {
            Entity? entity = _entities.Read(address);
            if (entity is null)
            {
                continue;
            }

            read++;
            foreach (string name in entity.Components.Keys)
            {
                counts[name] = counts.TryGetValue(name, out int seen) ? seen + 1 : 1;
            }
        }

        List<ComponentTally> tally =
        [
            .. counts
                .Select(pair => new ComponentTally(pair.Key, pair.Value, FieldsOf(pair.Key)))

                // Undescribed first, and the RAREST of those first within that - a component
                // two entities in an area carry is a far better lead than one everything has.
                .OrderBy(entry => entry.Described)
                .ThenBy(entry => entry.Described ? -entry.Count : entry.Count)
                .ThenBy(entry => entry.Name, StringComparer.Ordinal),
        ];

        return (tally, read);
    }

    /// <summary>How many fields the schema writes down for a component, 0 when it says nothing.</summary>
    private int FieldsOf(string component)
        => _schema.Structs.TryGetValue(component, out StructDef? definition) ? definition.Fields.Count : 0;

    /// <summary>Reads a component's declared fields, for the components the window has open.</summary>
    /// <remarks>
    /// The point of the whole thing: about twenty components have a decoder, and until now the
    /// browser could only say SO. Standing next to a monster and watching Life.Health move as
    /// you hit it is how an offset gets checked against the game, and it was three clicks and
    /// a hex window away.
    /// </remarks>
    private IReadOnlyList<FieldReading>? ValuesOf(string component, ulong address, IReadOnlyList<string>? open)
    {
        if (address == 0 || open is null || !open.Contains(component, StringComparer.Ordinal))
        {
            return null;
        }

        return _schema.Structs.TryGetValue(component, out StructDef? definition) && definition.Fields.Count > 0
            ? SchemaFieldReader.Read(_reader, definition, address, _schema)
            : null;
    }
}
