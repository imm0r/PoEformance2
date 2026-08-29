using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Components;
using PoEformance.Game.Entities;

namespace PoEformance.Game.Diagnostics;

/// <summary>One tick of the hunt: the player's Actor block, position, and followed pointers.</summary>
/// <param name="Window">The raw Actor component bytes, <see cref="ActionHunt.WindowSize"/> long.</param>
/// <param name="Followed">
/// For each followed slot (byte offset into <paramref name="Window"/>) that held a plausible
/// pointer this tick, the first <see cref="ActionHunt.FollowSize"/> bytes behind it.
/// </param>
public sealed record ActionHuntSample(
    ulong ActorAddress,
    byte[] Window,
    float PlayerX,
    float PlayerY,
    IReadOnlyDictionary<int, byte[]> Followed);

/// <summary>A pointer slot that comes and goes with activity - an ActionPtr candidate.</summary>
/// <param name="ActingNonNull">Share of acting frames in which the slot held a pointer.</param>
/// <param name="QuietNonNull">Share of idle frames in which it still held one.</param>
public sealed record ActionPointerCandidate(int Offset, double ActingNonNull, double QuietNonNull, int Toggles)
{
    /// <summary>How cleanly the slot separates acting from idle. 1.0 is a perfect switch.</summary>
    public double Score => ActingNonNull - QuietNonNull;
}

/// <summary>A small integer that is zero at rest and something while acting - an ActionId candidate.</summary>
public sealed record ActionIdCandidate(int Offset, string Kind, double QuietZero, double ActingNonZero, int DistinctValues)
{
    /// <summary>Separation first; a few extra distinct values break ties over a plain bool.</summary>
    public double Score => QuietZero + ActingNonZero + (Math.Min(DistinctValues, 8) / 32.0);
}

/// <summary>
/// A pair inside a followed block that the player then walks to - a Destination candidate.
/// </summary>
/// <param name="Kind">How the pair was decoded: two f32s or two i32s.</param>
/// <param name="Segments">How many arrivals the fit is built on.</param>
/// <param name="FitQuality">Worst-axis R-squared of "player's end position against the pair".</param>
/// <param name="Scale">
/// World units per pair unit from the fit. ~1 means the pair is in world units; a grid pair
/// comes out at the world-per-grid factor instead, which is why the fit is scale-free.
/// </param>
/// <param name="EndError">Mean distance between the fitted prediction and the real arrival.</param>
public sealed record DestinationCandidate(
    int PointerOffset, int PairOffset, string Kind, int Segments, double FitQuality, double Scale, double EndError);

/// <summary>Everything one hunt run concluded.</summary>
/// <param name="ActingAnimationIds">Distinct AnimationIds seen while acting, for the cast cross-check.</param>
/// <param name="CastTypeMatches">How many of those ids are CastTypes of the player's own skills.</param>
public sealed record ActionHuntFindings(
    int Frames,
    int ActingFrames,
    IReadOnlyList<ActionPointerCandidate> Pointers,
    IReadOnlyList<ActionIdCandidate> Ids,
    IReadOnlyList<DestinationCandidate> Destinations,
    IReadOnlyList<int> ActingAnimationIds,
    int CastTypeMatches);

/// <summary>
/// Hunts the Actor component's ACTION fields - the pointer to what the actor is doing right
/// now, its id, and above all the destination of the current move or cast - by watching the
/// player's own component while a person plays a small protocol.
/// </summary>
/// <remarks>
/// WHY A HUNT AND NOT A PORT. Neither reference reads these for PoE2: GameHelper2's ActorOffset
/// stops at AnimationId plus three vectors, and the AHK tool decodes the same four things.
/// ExileCore2 proves the fields exist - its GameOffsets2 metadata names ActionPtr,
/// SimpleActionPtr and ActionId on the actor and exposes CurrentAction.Destination - but that
/// DLL decodes its offset values at runtime, so it contributes names and a shape, not numbers.
/// The shape is PoE1's (ExileApi): ActionPtr 0x1A8 and a short ActionId 0x208, both BELOW
/// AnimationId (0x230 there, 0x8B0 here), pointing at a wrapper with Destination at 0x170.
///
/// THE TEST IS ONE THE GAME SETTLES. Nobody records where the person clicked; instead a
/// destination candidate must be a pair that stays CONSTANT while the player's own position
/// CONVERGES onto a line through it - across several separate arrivals, fitted scale-free so
/// world-unit and grid-unit encodings both qualify on the same evidence. A decoy that merely
/// looks like coordinates has no reason to predict where the player ends up. This is the same
/// lesson MatrixHunt carries: score against what the game then does, never against structure.
///
/// SPLIT FOR THE RECORDER. <see cref="SampleFrame"/> performs every read - the whole window
/// plus the followed blocks - so a <c>--record</c> session captures the hunt's raw material,
/// and <see cref="Analyze"/> is pure over the samples, so the same conclusion can be re-drawn
/// offline from the recording (seek a frame, sample, repeat), exactly like FacingTests does.
///
/// "ACTING" HERE MEANS AnimationId != 0, which is Idle - deliberately NOT
/// <see cref="AnimationNames.IsQuiet"/>. That classifier answers "is danger coming" and counts
/// walking as quiet; a plain walk IS an action to the actor (it is precisely the one whose
/// destination a click-move protocol exercises), so for this correlation movement must count
/// as acting.
/// </remarks>
public sealed class ActionHunt
{
    /// <summary>
    /// Bytes of Actor read per tick: covers AnimationId (0x8B0) and the vector block through
    /// upstream's DeployedEntityArray reading (0xC28 + 0x18), with margin.
    /// </summary>
    public const int WindowSize = 0xC80;

    /// <summary>
    /// Bytes read behind each followed pointer: past PoE1's Destination at 0x170, with margin.
    /// </summary>
    public const int FollowSize = 0x200;

    /// <summary>Most slots followed at once - a cap on per-tick reads, not on candidates.</summary>
    private const int MostFollowed = 12;

    /// <summary>Chunk size for window reads; failures zero a chunk instead of the whole window.</summary>
    private const int ReadChunk = 0x100;

    /// <summary>Most skill entries walked for the CastType table. A 9-gem character has 42.</summary>
    private const int MostSkills = 128;

    /// <summary>Fewest frames of each state before a correlation means anything.</summary>
    private const int MinFramesPerState = 5;

    /// <summary>An action pointer must be non-null this much MORE while acting than while idle.</summary>
    private const double MinPointerSeparation = 0.5;

    /// <summary>An id field must be zero in at least this share of idle frames.</summary>
    private const double MinQuietZero = 0.9;

    /// <summary>...and non-zero in at least this share of acting frames.</summary>
    private const double MinActingNonZero = 0.6;

    /// <summary>A pair moving further than this between ticks starts a new segment.</summary>
    private const double PairJump = 1.0;

    /// <summary>Fewest ticks a pair must sit still for its segment to count as an arrival.</summary>
    private const int MinSubSegmentFrames = 5;

    /// <summary>Fewest distinct arrivals a destination fit is allowed to rest on.</summary>
    private const int MinSegments = 3;

    /// <summary>Worst-axis R-squared a destination candidate must reach.</summary>
    private const double MinFitQuality = 0.95;

    /// <summary>Pair components beyond this are garbage, not coordinates.</summary>
    private const double LargestCoordinate = 1e9;

    private readonly IMemoryReader _reader;
    private readonly OffsetSchema _schema;
    private readonly EntityReader _entities;
    private readonly RenderReader _render;
    private readonly int _animationId;
    private readonly int _activeSkills;
    private readonly int _skillEntrySize;
    private readonly int _skillDetailsPtr;
    private readonly int _castType;

    // Toggle memory per 8-byte slot, kept across ticks: a slot is followed from the moment it
    // has shown BOTH a null and a plausible pointer, because that flip is the action shape.
    private readonly bool[] _slotSeenNull = new bool[WindowSize / 8];
    private readonly bool[] _slotSeenPtr = new bool[WindowSize / 8];
    private readonly List<int> _follow = [];

    private ulong _cachedPlayer;
    private ulong _cachedActor;
    private ulong _cachedRender;
    private List<int> _playerCastTypes = [];

    public ActionHunt(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;
        _schema = schema;
        _entities = new EntityReader(reader, schema);
        _render = new RenderReader(reader, schema);

        StructDef actor = schema.Structs["Actor"];
        _animationId = actor.OffsetOf("AnimationId");
        _activeSkills = actor.OffsetOf("ActiveSkills");

        StructDef entry = schema.Structs["ActiveSkillStructure"];
        _skillEntrySize = checked((int)entry.Constants["Size"]);
        _skillDetailsPtr = entry.OffsetOf("ActiveSkillPtr");
        _castType = schema.Structs["ActiveSkillDetails"].OffsetOf("CastType");

        // A schema drift that pushes AnimationId out of the window would make every frame
        // read as idle - a hunt that quietly finds nothing. Refuse loudly instead.
        if (_animationId + sizeof(int) > WindowSize)
        {
            throw new InvalidOperationException(
                $"Actor.AnimationId (0x{_animationId:X}) is outside the hunt window (0x{WindowSize:X}) - widen WindowSize.");
        }
    }

    /// <summary>Where AnimationId sits in each sample's window, for <see cref="Analyze"/>.</summary>
    public int AnimationIdOffset => _animationId;

    /// <summary>The slots currently being followed - for a live status line, not for scoring.</summary>
    public IReadOnlyList<int> FollowedSlots => _follow;

    /// <summary>
    /// CastTypes of the player's own granted skills, read once per player. The cross-check
    /// table: an AnimationId seen during a cast should be one of these.
    /// </summary>
    public IReadOnlyList<int> PlayerCastTypes => _playerCastTypes;

    /// <summary>
    /// Takes one tick's sample, or null when the game is not in the world or the player's
    /// Actor/Render could not be reached. Performs every read the offline analysis needs,
    /// so a recording made around this call replays.
    /// </summary>
    public ActionHuntSample? SampleFrame(ulong gameStatesStatic)
    {
        GameChainAddresses chain = GameChain.Resolve(_reader, _schema, gameStatesStatic);
        if (!chain.InGame)
        {
            return null;
        }

        // The component walk is ~50 reads; component addresses are stable while the entity
        // lives, so it is done once per player pointer rather than once per tick. On a
        // replay the same code caches the same way, so the reads line up with the recording.
        if (chain.PlayerEntity != _cachedPlayer)
        {
            ResolvePlayer(chain.PlayerEntity);
        }

        if (_cachedActor == 0 || _cachedRender == 0)
        {
            return null;
        }

        if (_render.Read(_cachedRender) is not RenderComponent position)
        {
            return null;
        }

        var window = new byte[WindowSize];
        if (!ReadChunked(_cachedActor, window))
        {
            return null;
        }

        UpdateFollowSet(window);

        var followed = new Dictionary<int, byte[]>();
        foreach (int slot in _follow)
        {
            ulong target = BitConverter.ToUInt64(window, slot);
            if (!MemoryReaderExtensions.IsPlausiblePointer(target))
            {
                continue;
            }

            var block = new byte[FollowSize];
            if (ReadChunked(target, block))
            {
                followed[slot] = block;
            }
        }

        return new ActionHuntSample(_cachedActor, window, position.X, position.Y, followed);
    }

    /// <summary>Resolves the player's Actor and Render once, and its skill CastType table.</summary>
    private void ResolvePlayer(ulong playerEntity)
    {
        _cachedPlayer = playerEntity;
        _cachedActor = 0;
        _cachedRender = 0;
        _playerCastTypes = [];

        Entity? player = _entities.Read(playerEntity);
        if (player is null)
        {
            return;
        }

        _cachedActor = player.Component("Actor");
        _cachedRender = player.Component("Render");
        if (_cachedActor != 0)
        {
            _playerCastTypes = ReadCastTypes(_cachedActor);
        }
    }

    /// <summary>
    /// The CastTypes of every granted skill, through the ActiveSkills vector. Doubles as the
    /// first live exercise of the ActiveSkillStructure/ActiveSkillDetails schema entries.
    /// </summary>
    private List<int> ReadCastTypes(ulong actor)
    {
        var types = new List<int>();
        ulong begin = _reader.ReadPointer(actor + (ulong)_activeSkills);
        ulong end = _reader.Read<ulong>(actor + (ulong)_activeSkills + 8);
        if (begin == 0 || end < begin)
        {
            return types;
        }

        long count = (long)(end - begin) / _skillEntrySize;
        if (count is <= 0 or > 512)
        {
            return types;
        }

        for (int i = 0; i < (int)Math.Min(count, MostSkills); i++)
        {
            ulong details = _reader.ReadPointer(begin + (ulong)(i * _skillEntrySize) + (ulong)_skillDetailsPtr);
            if (details == 0)
            {
                continue;
            }

            if (_reader.TryRead(details + (ulong)_castType, out int castType)
                && castType > 0
                && !types.Contains(castType))
            {
                types.Add(castType);
            }
        }

        return types;
    }

    /// <summary>Reads in page-friendly chunks; a failed chunk stays zero. True if the base read.</summary>
    private bool ReadChunked(ulong address, byte[] buffer)
    {
        bool first = false;
        for (int at = 0; at < buffer.Length; at += ReadChunk)
        {
            int take = Math.Min(ReadChunk, buffer.Length - at);
            bool ok = _reader.TryRead(address + (ulong)at, buffer.AsSpan(at, take));
            if (at == 0)
            {
                first = ok;
            }
        }

        return first;
    }

    /// <summary>
    /// Remembers which slots have shown both states and promotes them to the follow set.
    /// </summary>
    /// <remarks>
    /// Only slots BELOW ActiveSkills are followed: everything from the vector block on is
    /// already known, and in the PoE1 layout the action pointer sits below the skills array.
    /// Candidates over the whole window are still reported - following is what costs reads.
    /// </remarks>
    private void UpdateFollowSet(byte[] window)
    {
        for (int slot = 0; slot + 8 <= window.Length; slot += 8)
        {
            ulong value = BitConverter.ToUInt64(window, slot);
            if (value == 0)
            {
                _slotSeenNull[slot / 8] = true;
            }
            else if (MemoryReaderExtensions.IsPlausiblePointer(value))
            {
                _slotSeenPtr[slot / 8] = true;
            }

            if (_slotSeenNull[slot / 8]
                && _slotSeenPtr[slot / 8]
                && slot < _activeSkills
                && _follow.Count < MostFollowed
                && !_follow.Contains(slot))
            {
                _follow.Add(slot);
            }
        }
    }

    /// <summary>
    /// Draws every conclusion from the samples. Pure - runs identically against a live
    /// session and against a replayed recording of one.
    /// </summary>
    public static ActionHuntFindings Analyze(
        IReadOnlyList<ActionHuntSample> samples, int animationIdOffset, IReadOnlyList<int> playerCastTypes)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(playerCastTypes);

        if (samples.Count == 0)
        {
            return new ActionHuntFindings(0, 0, [], [], [], [], 0);
        }

        int windowSize = samples[0].Window.Length;
        bool[] acting = [.. samples.Select(s => BitConverter.ToInt32(s.Window, animationIdOffset) != 0)];
        int actingFrames = acting.Count(a => a);

        List<int> actingIds = [.. samples
            .Where((_, i) => acting[i])
            .Select(s => BitConverter.ToInt32(s.Window, animationIdOffset))
            .Distinct()
            .Order()];
        int castMatches = actingIds.Count(playerCastTypes.Contains);

        return new ActionHuntFindings(
            samples.Count,
            actingFrames,
            FindPointerCandidates(samples, acting, windowSize),
            FindIdCandidates(samples, acting, windowSize),
            FindDestinationCandidates(samples),
            actingIds,
            castMatches);
    }

    /// <summary>Slots that hold a pointer while acting and nothing while idle.</summary>
    private static List<ActionPointerCandidate> FindPointerCandidates(
        IReadOnlyList<ActionHuntSample> samples, bool[] acting, int windowSize)
    {
        var candidates = new List<ActionPointerCandidate>();
        for (int slot = 0; slot + 8 <= windowSize; slot += 8)
        {
            int actingNonNull = 0, actingFrames = 0, quietNonNull = 0, quietFrames = 0, toggles = 0;
            bool rejected = false;
            bool? previous = null;

            for (int i = 0; i < samples.Count; i++)
            {
                ulong value = BitConverter.ToUInt64(samples[i].Window, slot);
                bool nonNull = value != 0;

                // A slot that ever holds a non-zero NON-pointer is a value field, whatever
                // else it does - this is what keeps AnimationId's own slot out of this table.
                if (nonNull && !MemoryReaderExtensions.IsPlausiblePointer(value))
                {
                    rejected = true;
                    break;
                }

                if (acting[i])
                {
                    actingFrames++;
                    actingNonNull += nonNull ? 1 : 0;
                }
                else
                {
                    quietFrames++;
                    quietNonNull += nonNull ? 1 : 0;
                }

                if (previous is bool last && last != nonNull)
                {
                    toggles++;
                }

                previous = nonNull;
            }

            if (rejected || toggles == 0 || actingFrames < MinFramesPerState || quietFrames < MinFramesPerState)
            {
                continue;
            }

            double a = actingNonNull / (double)actingFrames;
            double q = quietNonNull / (double)quietFrames;
            if (a - q < MinPointerSeparation)
            {
                continue;
            }

            candidates.Add(new ActionPointerCandidate(slot, a, q, toggles));
        }

        SortByScore(candidates, c => c.Score, c => c.Offset);
        return candidates;
    }

    /// <summary>Integer fields that read zero at rest and something while acting.</summary>
    /// <remarks>
    /// AnimationId itself qualifies BY CONSTRUCTION - acting is defined off it - and is kept
    /// in the table on purpose, marked by the report: it is the built-in control that the
    /// classifier finds the one field of this shape everybody already knows about.
    /// </remarks>
    private static List<ActionIdCandidate> FindIdCandidates(
        IReadOnlyList<ActionHuntSample> samples, bool[] acting, int windowSize)
    {
        var candidates = new List<ActionIdCandidate>();
        var found32 = new HashSet<int>();

        foreach ((string kind, int step) in (ReadOnlySpan<(string, int)>)[("i32", 4), ("i16", 2)])
        {
            for (int offset = 0; offset + step <= windowSize; offset += step)
            {
                // An i16 half of an i32 already found tells the same story twice.
                if (kind == "i16" && found32.Contains(offset & ~3))
                {
                    continue;
                }

                int actingNonZero = 0, actingFrames = 0, quietZero = 0, quietFrames = 0;
                var distinct = new HashSet<long>();

                for (int i = 0; i < samples.Count; i++)
                {
                    long value = kind == "i32"
                        ? BitConverter.ToInt32(samples[i].Window, offset)
                        : BitConverter.ToInt16(samples[i].Window, offset);
                    if (value != 0 && distinct.Count <= 64)
                    {
                        distinct.Add(value);
                    }

                    if (acting[i])
                    {
                        actingFrames++;
                        actingNonZero += value != 0 ? 1 : 0;
                    }
                    else
                    {
                        quietFrames++;
                        quietZero += value == 0 ? 1 : 0;
                    }
                }

                if (actingFrames < MinFramesPerState || quietFrames < MinFramesPerState || distinct.Count < 2)
                {
                    continue;
                }

                double zeroAtQuiet = quietZero / (double)quietFrames;
                double nonZeroActing = actingNonZero / (double)actingFrames;
                if (zeroAtQuiet < MinQuietZero || nonZeroActing < MinActingNonZero)
                {
                    continue;
                }

                candidates.Add(new ActionIdCandidate(offset, kind, zeroAtQuiet, nonZeroActing, distinct.Count));
                if (kind == "i32")
                {
                    found32.Add(offset);
                }
            }
        }

        SortByScore(candidates, c => c.Score, c => c.Offset);
        return candidates;
    }

    /// <summary>
    /// Pairs inside followed blocks whose value the player then arrives at - fitted
    /// scale-free across separate arrivals, so world-unit and grid-unit encodings both show.
    /// </summary>
    private static List<DestinationCandidate> FindDestinationCandidates(IReadOnlyList<ActionHuntSample> samples)
    {
        var candidates = new List<DestinationCandidate>();
        List<int> slots = [.. samples.SelectMany(s => s.Followed.Keys).Distinct().Order()];

        foreach (int slot in slots)
        {
            // Runs of consecutive ticks in which this slot was followed; a tick without the
            // block (pointer null, or unreadable) breaks the run, because the pair's story
            // across that gap is unknown.
            var runs = new List<(int Start, int End)>();
            int? runStart = null;
            for (int i = 0; i <= samples.Count; i++)
            {
                bool has = i < samples.Count && samples[i].Followed.ContainsKey(slot);
                if (has)
                {
                    runStart ??= i;
                }
                else if (runStart is int start)
                {
                    runs.Add((start, i - 1));
                    runStart = null;
                }
            }

            foreach ((string kind, bool asFloat) in (ReadOnlySpan<(string, bool)>)[("f32", true), ("i32", false)])
            {
                for (int pairOffset = 0; pairOffset + 8 <= FollowSize; pairOffset += 4)
                {
                    List<(double PairX, double PairY, double EndX, double EndY)> arrivals =
                        CollectArrivals(samples, runs, slot, pairOffset, asFloat);

                    // Constants pass every within-segment test, so the fit must rest on
                    // several DIFFERENT destinations before it is evidence of anything.
                    int distinctPairs = arrivals
                        .Select(s => (Math.Round(s.PairX), Math.Round(s.PairY)))
                        .Distinct()
                        .Count();
                    if (arrivals.Count < MinSegments || distinctPairs < MinSegments)
                    {
                        continue;
                    }

                    (double r2X, double scaleX, double errX) = FitLine(
                        [.. arrivals.Select(s => s.PairX)], [.. arrivals.Select(s => s.EndX)]);
                    (double r2Y, _, double errY) = FitLine(
                        [.. arrivals.Select(s => s.PairY)], [.. arrivals.Select(s => s.EndY)]);

                    double fit = Math.Min(r2X, r2Y);
                    if (fit < MinFitQuality)
                    {
                        continue;
                    }

                    candidates.Add(new DestinationCandidate(
                        slot, pairOffset, kind, arrivals.Count, fit, scaleX, (errX + errY) / 2.0));
                }
            }
        }

        candidates.Sort((a, b) =>
        {
            int byFit = b.FitQuality.CompareTo(a.FitQuality);
            if (byFit != 0)
            {
                return byFit;
            }

            int byError = a.EndError.CompareTo(b.EndError);
            if (byError != 0)
            {
                return byError;
            }

            int bySlot = a.PointerOffset.CompareTo(b.PointerOffset);
            return bySlot != 0 ? bySlot : a.PairOffset.CompareTo(b.PairOffset);
        });
        return candidates;
    }

    /// <summary>
    /// Splits each run into stretches where the pair sits still and keeps, per stretch that
    /// lasted, the pair's value and where the player WAS when the stretch ended - the arrival.
    /// </summary>
    private static List<(double PairX, double PairY, double EndX, double EndY)> CollectArrivals(
        IReadOnlyList<ActionHuntSample> samples,
        List<(int Start, int End)> runs,
        int slot,
        int pairOffset,
        bool asFloat)
    {
        var arrivals = new List<(double, double, double, double)>();

        foreach ((int start, int end) in runs)
        {
            double pairX = double.NaN, pairY = double.NaN;
            int length = 0, lastIndex = 0;

            void Close()
            {
                if (length >= MinSubSegmentFrames)
                {
                    arrivals.Add((pairX, pairY, samples[lastIndex].PlayerX, samples[lastIndex].PlayerY));
                }

                length = 0;
            }

            for (int i = start; i <= end; i++)
            {
                byte[] block = samples[i].Followed[slot];
                double x = asFloat
                    ? BitConverter.ToSingle(block, pairOffset)
                    : BitConverter.ToInt32(block, pairOffset);
                double y = asFloat
                    ? BitConverter.ToSingle(block, pairOffset + 4)
                    : BitConverter.ToInt32(block, pairOffset + 4);

                bool valid = double.IsFinite(x) && double.IsFinite(y)
                    && Math.Abs(x) < LargestCoordinate && Math.Abs(y) < LargestCoordinate;
                if (!valid)
                {
                    Close();
                    pairX = double.NaN;
                    continue;
                }

                bool moved = double.IsNaN(pairX)
                    || Math.Abs(x - pairX) + Math.Abs(y - pairY) > PairJump;
                if (moved)
                {
                    Close();
                    pairX = x;
                    pairY = y;
                }

                length++;
                lastIndex = i;
            }

            Close();
        }

        return arrivals;
    }

    /// <summary>Least-squares line fit: R-squared, slope, and mean absolute error.</summary>
    private static (double R2, double Slope, double MeanError) FitLine(double[] xs, double[] ys)
    {
        int n = xs.Length;
        double meanX = xs.Average(), meanY = ys.Average();
        double sxx = 0, sxy = 0, syy = 0;
        for (int i = 0; i < n; i++)
        {
            double dx = xs[i] - meanX, dy = ys[i] - meanY;
            sxx += dx * dx;
            sxy += dx * dy;
            syy += dy * dy;
        }

        // Degenerate: a pair (or a player) that never varies fits nothing.
        if (sxx < 1e-9 || syy < 1e-9)
        {
            return (0, 0, double.PositiveInfinity);
        }

        double slope = sxy / sxx;
        double intercept = meanY - (slope * meanX);
        double error = 0;
        for (int i = 0; i < n; i++)
        {
            error += Math.Abs(ys[i] - ((slope * xs[i]) + intercept));
        }

        return ((sxy * sxy) / (sxx * syy), slope, error / n);
    }

    /// <summary>Score-descending sort with the offset breaking ties, so runs are comparable.</summary>
    private static void SortByScore<T>(List<T> list, Func<T, double> score, Func<T, int> offset)
        => list.Sort((a, b) =>
        {
            int byScore = score(b).CompareTo(score(a));
            return byScore != 0 ? byScore : offset(a).CompareTo(offset(b));
        });

    /// <summary>Writes a human-readable hunt report.</summary>
    public static void Report(ActionHuntFindings findings, int animationIdOffset, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(output);

        output.WriteLine();
        output.WriteLine($"action hunt ({findings.Frames} frames: {findings.ActingFrames} acting, "
            + $"{findings.Frames - findings.ActingFrames} idle)");

        if (findings.Frames == 0)
        {
            output.WriteLine("  no frames - not in an area, or the player's Actor would not read.");
            return;
        }

        if (findings.ActingFrames == 0 || findings.ActingFrames == findings.Frames)
        {
            output.WriteLine("  no contrast - the run needs BOTH idle moments and actions. Stand still");
            output.WriteLine("  between clicks and casts, then run it again.");
            return;
        }

        output.WriteLine("  action-pointer slots (hold a pointer while acting, nothing while idle):");
        if (findings.Pointers.Count == 0)
        {
            output.WriteLine("    none - no slot below ActiveSkills toggles with activity. Longer run,");
            output.WriteLine("    or the pointer lives above the skills block.");
        }
        else
        {
            output.WriteLine("    offset  acting  idle  toggles");
            foreach (ActionPointerCandidate c in findings.Pointers.Take(8))
            {
                output.WriteLine($"    +0x{c.Offset:X3}  {c.ActingNonNull,6:P0} {c.QuietNonNull,5:P0}  {c.Toggles,7}");
            }
        }

        output.WriteLine("  action-id fields (zero while idle, non-zero while acting):");
        if (findings.Ids.Count == 0)
        {
            output.WriteLine("    none - which is itself wrong: AnimationId has this shape by construction,");
            output.WriteLine("    so an empty table means the window or the schema offset is off.");
        }
        else
        {
            output.WriteLine("    offset  kind  zero@idle  nonzero@acting  values");
        }

        foreach (ActionIdCandidate c in findings.Ids.Take(8))
        {
            output.WriteLine($"    +0x{c.Offset:X3}  {c.Kind}  {c.QuietZero,9:P0}  {c.ActingNonZero,14:P0}  {c.DistinctValues,6}"
                + (c.Offset == animationIdOffset && c.Kind == "i32" ? "   <- AnimationId (schema; the control)" : string.Empty));
        }

        output.WriteLine("  destination pairs (the player arrives where the pair points):");
        if (findings.Destinations.Count == 0)
        {
            output.WriteLine("    none - either no followed pointer carries one, or too few clean arrivals.");
            output.WriteLine("    The protocol is the cure: click, LET THE CHARACTER ARRIVE, repeat.");
        }
        else
        {
            output.WriteLine("    slot    pair   kind  arrivals    fit    scale  end-error");
            foreach (DestinationCandidate c in findings.Destinations.Take(8))
            {
                output.WriteLine($"    +0x{c.PointerOffset:X3}  +0x{c.PairOffset:X3}  {c.Kind}   {c.Segments,8}  {c.FitQuality,5:F3}  {c.Scale,7:F2}  {c.EndError,9:F1}");
            }
        }

        output.WriteLine($"  cast cross-check: {findings.CastTypeMatches} of {findings.ActingAnimationIds.Count} acting animation "
            + "ids are CastTypes of the player's own skills");
        output.WriteLine("    (movement ids never match - only the casts should)");
        output.WriteLine("  next: enter winners in schema/poe2.offsets.json - ActionPtr/ActionId on Actor,");
        output.WriteLine("  the pair as Destination on a new ActionWrapper struct - re-run to confirm,");
        output.WriteLine("  then --record a session as the fixture.");
    }
}
