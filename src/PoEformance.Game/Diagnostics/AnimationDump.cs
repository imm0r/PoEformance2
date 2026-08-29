using System.Globalization;
using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Components;
using PoEformance.Game.Entities;

namespace PoEformance.Game.Diagnostics;

/// <summary>What the game's table says, and how it differs from the shipped one.</summary>
/// <param name="Changed">Ids both have, where the names differ. The rows worth reading.</param>
/// <param name="Added">Ids the game has and the file does not.</param>
/// <param name="Missing">
/// Ids the file has and the game did not answer for. Not necessarily a deletion - a row the walk
/// could not read looks the same from here, which is why they are listed rather than dropped.
/// </param>
public sealed record AnimationDumpResult(
    bool Confirmed,
    (int First, int Second) ConfirmedBy,
    ulong Base,
    int Observations,
    IReadOnlyDictionary<int, string> Names,
    IReadOnlyList<(int Id, string Shipped, string Game)> Changed,
    IReadOnlyList<(int Id, string Game)> Added,
    IReadOnlyList<(int Id, string Shipped)> Missing)
{
    /// <summary>Highest id the game answered for.</summary>
    public int HighestId => Names.Count == 0 ? -1 : Names.Keys.Max();

    /// <summary>Nothing was read.</summary>
    public static AnimationDumpResult Nothing { get; } =
        new(false, (-1, -1), 0, 0, new Dictionary<int, string>(), [], [], []);
}

/// <summary>
/// Regenerates <c>data/animations.tsv</c> from the running game.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS. The shipped table is hand-maintained, extracted once from the AHK tool, and
/// its own header calls a name "a LABEL, never a fact". The first six ids ever checked against
/// the game turned up one that was wrong. Nobody knows how many of the other 1078 are, and nobody
/// can find out by reading them. The game can be asked instead.
///
/// IT DOES NOT OVERWRITE THE SHIPPED FILE, deliberately. It writes a new one beside it and prints
/// what changed, because re-extracting a table that a dozen behaviours classify off should be a
/// deliberate act with a diff someone looked at - not something a play session does quietly. The
/// same argument the schema makes about offsets: this is knowledge, and knowledge gets reviewed.
///
/// IT NEEDS THE GAME TO BE DOING SOMETHING. The base can only be derived from a sighting of a
/// real animation's row, and two DIFFERENT animations have to agree before it is trusted - see
/// <see cref="AnimationTable"/> for why one is not enough. Walking around and hitting something
/// is plenty; standing still in town is not.
///
/// BOTH ACTION SLOTS ARE SAMPLED, which also answers a question nobody has asked yet: the row
/// pointer was found on a SKILL wrapper, and whether a MOVE wrapper carries it at the same offset
/// is unverified. The report says which slot each sighting came from, so a session where only
/// running happened either works or says plainly that it did not.
/// </remarks>
public sealed class AnimationDump
{
    private readonly IMemoryReader _reader;
    private readonly OffsetSchema _schema;
    private readonly EntityReader _entities;

    private readonly int _skillActionPtr;
    private readonly int _moveActionPtr;
    private readonly int _animationId;
    private readonly int _animationRow;

    private ulong _cachedPlayer;
    private ulong _cachedActor;

    public AnimationDump(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;
        _schema = schema;
        _entities = new EntityReader(reader, schema);

        StructDef actor = schema.Structs["Actor"];
        _skillActionPtr = actor.OffsetOf("SkillActionPtr");
        _moveActionPtr = actor.OffsetOf("MoveActionPtr");
        _animationId = actor.OffsetOf("AnimationId");
        _animationRow = schema.Structs["ActionWrapper"].OffsetOf("AnimationRow");
    }

    /// <summary>The table being built up.</summary>
    public AnimationTable Table { get; } = new();

    /// <summary>Which wrapper slots have produced a sighting, for the report.</summary>
    public HashSet<string> Slots { get; } = [];

    /// <summary>Offers this frame's sightings to the table. True once the base is confirmed.</summary>
    public bool Sample(ulong gameStatesStatic)
    {
        GameChainAddresses chain = GameChain.Resolve(_reader, _schema, gameStatesStatic);
        if (!chain.InGame)
        {
            return Table.IsConfirmed;
        }

        // A FAILED RESOLUTION IS NEVER CACHED - the same rule every other sampler here keeps.
        if (chain.PlayerEntity != _cachedPlayer || _cachedActor == 0)
        {
            _cachedActor = _entities.Read(chain.PlayerEntity)?.Component("Actor") ?? 0;
            _cachedPlayer = chain.PlayerEntity;
        }

        if (_cachedActor == 0 || !_reader.TryRead(_cachedActor + (ulong)_animationId, out int animation))
        {
            return Table.IsConfirmed;
        }

        Offer(animation, _skillActionPtr, "skill");
        Offer(animation, _moveActionPtr, "move");
        return Table.IsConfirmed;
    }

    private void Offer(int animation, int slot, string name)
    {
        ulong wrapper = _reader.ReadPointer(_cachedActor + (ulong)slot);
        if (!MemoryReaderExtensions.IsPlausiblePointer(wrapper))
        {
            return;
        }

        ulong row = _reader.ReadPointer(wrapper + (ulong)_animationRow);
        if (!MemoryReaderExtensions.IsPlausiblePointer(row))
        {
            return;
        }

        // Only count it as a sighting if the row actually names something. A plausible pointer
        // that leads nowhere would otherwise be a vote for a base derived from noise.
        if (SkillHunt.TextAt(_reader, _reader.ReadPointer(row)) is null)
        {
            return;
        }

        Slots.Add(name);
        Table.Observe(animation, row);
    }

    /// <summary>Walks the table and compares it with the shipped names.</summary>
    public AnimationDumpResult Read(AnimationNames shipped)
    {
        ArgumentNullException.ThrowIfNull(shipped);

        if (!Table.IsConfirmed)
        {
            return AnimationDumpResult.Nothing;
        }

        IReadOnlyDictionary<int, string> names = Table.ReadAll(_reader);

        var changed = new List<(int, string, string)>();
        var added = new List<(int, string)>();
        foreach ((int id, string game) in names.OrderBy(pair => pair.Key))
        {
            string? was = shipped.Of(id);
            if (was is null)
            {
                added.Add((id, game));
            }
            else if (!string.Equals(was, game, StringComparison.Ordinal))
            {
                changed.Add((id, was, game));
            }
        }

        var missing = new List<(int, string)>();
        for (int id = 0; id <= AnimationTable.MostIds; id++)
        {
            if (!names.ContainsKey(id) && shipped.Of(id) is string only)
            {
                missing.Add((id, only));
            }
        }

        return new AnimationDumpResult(
            true, Table.ConfirmedBy, Table.Base, Table.Observations, names, changed, added, missing);
    }

    /// <summary>Writes the table in the shipped file's format, with its provenance.</summary>
    public static void WriteTsv(AnimationDumpResult result, string path, string provenance)
    {
        ArgumentNullException.ThrowIfNull(result);

        using var writer = new StreamWriter(path);
        writer.WriteLine("# Animation (CastType) ids to names.");
        writer.WriteLine("# GENERATED FROM THE RUNNING GAME - read out of Data/Balance/Animation.dat");
        writer.WriteLine("# through ActionWrapper.AnimationRow (rows are an array, stride 106).");
        writer.WriteLine($"# {provenance}");
        writer.WriteLine("# A name is still a LABEL, never a fact - see AnimationNames - but it is");
        writer.WriteLine("# now the game's label rather than a transcription of one.");

        foreach ((int id, string name) in result.Names.OrderBy(pair => pair.Key))
        {
            writer.WriteLine($"{id.ToString(CultureInfo.InvariantCulture)}\t{name}");
        }
    }

    /// <summary>Prints what was found and what it means for the shipped file.</summary>
    public static void Report(AnimationDumpResult result, TextWriter output, IReadOnlyCollection<string> slots)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(slots);

        output.WriteLine();
        output.WriteLine("ANIMATION TABLE DUMP");
        output.WriteLine(new string('-', 72));

        if (!result.Confirmed)
        {
            output.WriteLine($"  the row array's base was NOT confirmed after {result.Observations} sighting(s).");
            output.WriteLine();
            output.WriteLine("  It needs TWO DIFFERENT animations to agree on it - one is arithmetic,");
            output.WriteLine("  not evidence. Move around and attack something, then run this again.");
            output.WriteLine($"  Wrapper slots that produced a sighting: {(slots.Count == 0 ? "none" : string.Join(", ", slots))}");
            return;
        }

        output.WriteLine(
            $"  base 0x{result.Base:X} confirmed by animations {result.ConfirmedBy.First} and "
            + $"{result.ConfirmedBy.Second} ({result.Observations} sightings, from the "
            + $"{string.Join(" and ", slots)} slot(s))");
        output.WriteLine($"  {result.Names.Count} rows read, highest id {result.HighestId}");
        output.WriteLine();
        output.WriteLine(
            $"  against the shipped table: {result.Changed.Count} changed, "
            + $"{result.Added.Count} new, {result.Missing.Count} the game did not answer for");

        if (result.Changed.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("  CHANGED - the shipped file is wrong about these:");
            foreach ((int id, string shipped, string game) in result.Changed.Take(40))
            {
                output.WriteLine($"    {id,5}  {shipped}  ->  {game}");
            }

            if (result.Changed.Count > 40)
            {
                output.WriteLine($"    ... and {result.Changed.Count - 40} more");
            }
        }

        if (result.Added.Count > 0)
        {
            output.WriteLine();
            output.WriteLine($"  NEW - ids the file has never had ({result.Added.Count}):");
            foreach ((int id, string game) in result.Added.Take(20))
            {
                output.WriteLine($"    {id,5}  {game}");
            }

            if (result.Added.Count > 20)
            {
                output.WriteLine($"    ... and {result.Added.Count - 20} more");
            }
        }

        if (result.Missing.Count > 0)
        {
            output.WriteLine();
            output.WriteLine(
                $"  NO ANSWER - in the file, not read from the game ({result.Missing.Count}).");
            output.WriteLine("  A deleted row and an unreadable one look the same from here, so");
            output.WriteLine("  these are worth a second run before being taken as removals:");
            foreach ((int id, string shipped) in result.Missing.Take(20))
            {
                output.WriteLine($"    {id,5}  {shipped}");
            }

            if (result.Missing.Count > 20)
            {
                output.WriteLine($"    ... and {result.Missing.Count - 20} more");
            }
        }
    }
}
