using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Entities;

namespace PoEformance.Features;

/// <summary>One component an entity carries.</summary>
/// <param name="Described">
/// True when the schema has a layout of this name. False is the interesting case: the game
/// says the component is there and nobody has written down what is in it.
/// </param>
public readonly record struct ComponentEntry(string Name, ulong Address, bool Described);

/// <summary>How often a component turns up across a whole area, and whether it is described.</summary>
public readonly record struct ComponentTally(string Name, int Count, bool Described);

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
public readonly record struct EntityStat(uint Id, int Value);

/// <summary>What the entity browser wants to see.</summary>
/// <param name="Survey">
/// Entities to count components across, for the one-shot survey. Supplied by the window
/// because it already has the area's entity list - so this costs no extra scan.
/// </param>
public sealed record EntityRequest(
    bool Enabled = false,
    ulong Address = 0,
    IReadOnlyList<ulong>? Survey = null,
    int SurveySequence = 0)
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
    string StatsNote = "")
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
    private readonly OffsetSchema _schema;

    private EntityRequest _request = EntityRequest.Idle;
    private EntityView _view = EntityView.Empty;

    private int _servedSurvey;
    private IReadOnlyList<ComponentTally> _lastSurvey = [];
    private int _lastSurveyed;

    public EntityInspector(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
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
                .Select(pair => new ComponentEntry(pair.Key, pair.Value, _schema.Structs.ContainsKey(pair.Key)))
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

        (List<EntityStat> numbers, string statsNote) = ReadStats(entity);

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
            statsNote);
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
    private (List<EntityStat> Stats, string Note) ReadStats(Entity entity)
    {
        const int MaxStats = 256;
        const int PairSize = 8;

        var stats = new List<EntityStat>();
        ulong component = entity.Component("Stats");
        if (component == 0)
        {
            return (stats, string.Empty);
        }

        StructDef layout = _schema.Structs["Stats"];
        int at = layout.OffsetOf("StatsInternalStatsVector");
        if (!_reader.TryRead(component + (ulong)at, out ulong first)
            || !_reader.TryRead(component + (ulong)(at + 8), out ulong last))
        {
            return (stats, "carries Stats, and it could not be read");
        }

        if (!MemoryReaderExtensions.IsPlausiblePointer(first) || last < first)
        {
            return (stats, "carries Stats, with nothing readable in it");
        }

        long count = (long)(last - first) / PairSize;
        if (count is < 0 or > 65536)
        {
            return (stats, $"carries Stats, and its vector reads as {count} pairs - not believable");
        }

        if (count == 0)
        {
            return (stats, "carries Stats, with no numbers in it");
        }

        int wanted = (int)Math.Min(count, MaxStats);
        byte[] block = new byte[wanted * PairSize];
        if (!_reader.TryRead(first, block))
        {
            return (stats, "carries Stats, and its vector could not be read");
        }

        for (int i = 0; i < wanted; i++)
        {
            stats.Add(new EntityStat(
                BitConverter.ToUInt32(block, i * PairSize),
                BitConverter.ToInt32(block, (i * PairSize) + 4)));
        }

        string note = count > wanted
            ? $"{count} stats, showing the first {wanted}:"
            : $"{count} stats:";
        return (stats, note);
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
                .Select(pair => new ComponentTally(pair.Key, pair.Value, _schema.Structs.ContainsKey(pair.Key)))

                // Undescribed first, and the RAREST of those first within that - a component
                // two entities in an area carry is a far better lead than one everything has.
                .OrderBy(entry => entry.Described)
                .ThenBy(entry => entry.Described ? -entry.Count : entry.Count)
                .ThenBy(entry => entry.Name, StringComparer.Ordinal),
        ];

        return (tally, read);
    }
}
