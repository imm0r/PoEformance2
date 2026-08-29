using PoEformance.Core.Memory;

namespace PoEformance.Game.Components;

/// <summary>
/// The game's own animation-name table, addressed directly rather than one row at a time.
/// </summary>
/// <remarks>
/// WHAT THIS BUYS OVER LEARNING NAMES AS THEY TURN UP. <see cref="ActionReader"/> already names
/// every animation the tool actually SEES, by following that actor's wrapper to its own row. That
/// covers everything the tool acts on and leaves one thing out: an id it has not seen yet. The
/// rows are an array, so ONE sighting is enough to address ALL of them - and with that, the
/// shipped <c>data/animations.tsv</c> stops being a hand-maintained list and becomes something
/// the game can regenerate.
///
/// THE ARITHMETIC IS MEASURED, NOT ASSUMED. Over the six animations of
/// <c>session-2026-08-skills.rec</c> the row address is <c>base + id * 106</c> exactly, the same
/// stride across every gap including one of 388 ids (see <c>AnimationNamesFromTheGameTests</c>).
/// So the base falls out of any single observation: <c>base = row - id * 106</c>.
///
/// TWO INDEPENDENT OBSERVATIONS MUST AGREE BEFORE IT IS USED, and that is the whole safety of the
/// thing. A base derived from one sighting is a number with no check on it: if the row pointer
/// were ever something else - a different table, a stale read, an offset that drifted - the base
/// would still compute, every row would still "read", and the output would be a full table of
/// confident nonsense. Two sightings of DIFFERENT animations agreeing is a real constraint,
/// because a wrong row pointer has no reason to land at the same base twice.
///
/// Observations of the SAME id do not count as independent: they agree trivially, by arithmetic
/// rather than by evidence.
/// </remarks>
public sealed class AnimationTable
{
    /// <summary>Bytes per row of Data/Balance/Animation.dat. Measured - see the remarks.</summary>
    public const int RowStride = 106;

    /// <summary>
    /// Highest id worth asking for. The schema's own invariant on AnimationId.
    /// </summary>
    /// <remarks>
    /// A bound rather than a count, because nobody knows the count: reading past the end of the
    /// array lands in whatever follows it, which can be mapped and can even hold something that
    /// reads as text. That is why the walk ALSO stops after a run of misses, and why the report
    /// prints the highest id it reached rather than claiming to have found the end.
    /// </remarks>
    public const int MostIds = 8192;

    /// <summary>How many consecutive unreadable rows end the walk.</summary>
    private const int GiveUpAfter = 64;

    private ulong _candidate;
    private int _candidateFrom = -1;

    /// <summary>The row array's base address, valid only once <see cref="IsConfirmed"/>.</summary>
    public ulong Base { get; private set; }

    /// <summary>Whether two different animations have agreed on the base.</summary>
    public bool IsConfirmed { get; private set; }

    /// <summary>How many sightings have been offered.</summary>
    public int Observations { get; private set; }

    /// <summary>The two animation ids that settled the base, for the report.</summary>
    public (int First, int Second) ConfirmedBy { get; private set; } = (-1, -1);

    /// <summary>
    /// Offers one sighting of "animation <paramref name="animationId"/> has its row here".
    /// </summary>
    /// <returns>True once the base is confirmed.</returns>
    public bool Observe(int animationId, ulong rowAddress)
    {
        if (animationId < 0 || animationId > MostIds || !MemoryReaderExtensions.IsPlausiblePointer(rowAddress))
        {
            return IsConfirmed;
        }

        ulong derived = rowAddress - (ulong)(animationId * RowStride);
        Observations++;

        if (IsConfirmed)
        {
            return true;
        }

        if (_candidateFrom < 0)
        {
            _candidate = derived;
            _candidateFrom = animationId;
            return false;
        }

        if (animationId == _candidateFrom)
        {
            // The same animation again. It agrees by arithmetic, not by evidence.
            return false;
        }

        if (derived == _candidate)
        {
            Base = derived;
            IsConfirmed = true;
            ConfirmedBy = (_candidateFrom, animationId);
            return true;
        }

        // They disagree, so at least one is wrong and there is no way to tell which. Keep the
        // newer one and wait for a third: holding the older would make the first bad sighting of
        // a session poison every one after it.
        _candidate = derived;
        _candidateFrom = animationId;
        return false;
    }

    /// <summary>Where an animation's row sits. Meaningless until the base is confirmed.</summary>
    public ulong RowOf(int animationId) => Base + (ulong)(animationId * RowStride);

    /// <summary>The game's name for one animation, or null.</summary>
    /// <remarks>
    /// The row's FIRST field is a pointer to its id string - the same shape this codebase already
    /// reads two other dat rows through (see <c>ItemReader</c>).
    /// </remarks>
    public string? NameOf(IMemoryReader reader, int animationId)
    {
        ArgumentNullException.ThrowIfNull(reader);

        if (!IsConfirmed || animationId < 0 || animationId > MostIds)
        {
            return null;
        }

        ulong id = reader.ReadPointer(RowOf(animationId));
        return Diagnostics.SkillHunt.TextAt(reader, id);
    }

    /// <summary>
    /// Walks the whole table.
    /// </summary>
    /// <remarks>
    /// Stops at the first run of <see cref="GiveUpAfter"/> unreadable rows, or at
    /// <see cref="MostIds"/>. Neither is a claim to have found the end - see the remarks on
    /// MostIds - which is why the caller is told the highest id that answered.
    /// </remarks>
    public IReadOnlyDictionary<int, string> ReadAll(IMemoryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var names = new Dictionary<int, string>();
        if (!IsConfirmed)
        {
            return names;
        }

        int missed = 0;
        for (int id = 0; id <= MostIds && missed < GiveUpAfter; id++)
        {
            if (NameOf(reader, id) is string name)
            {
                names[id] = name;
                missed = 0;
            }
            else
            {
                missed++;
            }
        }

        return names;
    }
}
