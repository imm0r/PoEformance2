using PoEformance.Game.Components;

namespace PoEformance.Features;

/// <summary>
/// One monster worth remembering, and what it cost to put down.
/// </summary>
/// <remarks>
/// THE NUMBER A BUILD IS ACTUALLY TUNED BY, and the one an averaged rate cannot give. "How
/// long does a rare take" is the question behind most single-target complaints, and a dps
/// figure averaged over a map answers it only if the map was one long fight - which no map is.
/// </remarks>
/// <param name="Damage">
/// Everything that came off it: what was watched falling plus whatever pool it still had when
/// it went. So this is the monster's cost, not the size of the last hit - a killing blow far
/// bigger than what was left counts only the part that landed on something.
/// </param>
/// <param name="Seconds">From the first damage seen on it until it went. Zero for a one-shot.</param>
/// <param name="Watched">
/// Whether it was seen being hurt before it vanished. False means the whole of it rests on the
/// assumption that vanishing is dying, so its TIME is not a measurement of anything - which is
/// why the two are told apart rather than averaged together.
/// </param>
public readonly record struct KillRecord(
    long AtMs, uint Area, string Name, ItemRarity Rarity, long Damage, float Seconds, bool Watched)
{
    /// <summary>Damage per second over the fight, where there was a fight to measure.</summary>
    public float Dps => Seconds > 0f ? Damage / Seconds : 0f;
}

/// <summary>
/// The rares and uniques that went down, newest first.
/// </summary>
/// <remarks>
/// RARE AND ABOVE ONLY. A map kills hundreds of white monsters and none of them is a question
/// anybody has; the interesting kill is the one that took long enough to notice.
///
/// Written from the reader thread and read from the renderer, so it locks - a handful of
/// entries a minute against a read once a frame.
/// </remarks>
public sealed class KillLog
{
    /// <summary>Kills kept before the oldest is dropped.</summary>
    public const int Capacity = 256;

    /// <summary>The rarity a monster has to reach to be worth a line.</summary>
    public const ItemRarity Interesting = ItemRarity.Rare;

    private readonly Lock _gate = new();
    private readonly KillRecord[] _kills = new KillRecord[Capacity];
    private int _next;
    private int _count;

    /// <summary>How many kills are held.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>Records one. Called from the reader thread.</summary>
    public void Add(KillRecord kill)
    {
        lock (_gate)
        {
            _kills[_next] = kill;
            _next = (_next + 1) % Capacity;
            _count = Math.Min(_count + 1, Capacity);
        }
    }

    /// <summary>Forgets everything.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _next = 0;
            _count = 0;
        }
    }

    /// <summary>
    /// The kills in a scope, NEWEST FIRST - which is the order they are read in.
    /// </summary>
    /// <param name="area">Only this area's, or 0 for everything held.</param>
    public IReadOnlyList<KillRecord> In(uint area = 0)
    {
        var listed = new List<KillRecord>();

        lock (_gate)
        {
            int start = _count == Capacity ? _next : 0;
            for (int i = _count - 1; i >= 0; i--)
            {
                KillRecord kill = _kills[(start + i) % Capacity];
                if (area == 0 || kill.Area == area)
                {
                    listed.Add(kill);
                }
            }
        }

        return listed;
    }

    /// <summary>
    /// The slowest kill in a scope - the one the build has the most trouble with.
    /// </summary>
    /// <remarks>
    /// Only ones that were WATCHED. A one-shot has no fight to time, so it would win every
    /// "fastest" and lose every "slowest" for a reason that is about the measurement rather
    /// than about the monster.
    /// </remarks>
    public KillRecord? Worst(uint area = 0)
    {
        KillRecord? worst = null;
        foreach (KillRecord kill in In(area))
        {
            if (kill.Watched && (worst is not { } far || kill.Seconds > far.Seconds))
            {
                worst = kill;
            }
        }

        return worst;
    }
}
