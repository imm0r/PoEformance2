using System.Globalization;

namespace PoEformance.Features;

/// <summary>
/// How long the last few steered rolls took the game to notice, as one readable line.
/// </summary>
/// <remarks>
/// WHY AN AGGREGATE AND NOT THE LAST NUMBER, which is what this replaced. The measurement is
/// taken during a fight and a roll happens about once a second, so a status line showing the
/// latest one is overwritten before it can be read - the owner's words: "im Bruchteil einer
/// Sekunde direkt wieder überschrieben". A summary of the last few moves slowly enough to read
/// while playing, and stands still afterwards.
///
/// IT IS ALSO THE BETTER MEASUREMENT, which is the part that would matter even if the line held
/// still. A single confirmation is one frame of one moment: a stutter, a zone load, a shader
/// compile all show up as one large number that looks exactly like a finding. A spread over
/// thirty-two rolls says what the machine actually does, and says whether it is steady.
///
/// THE MIDDLE VALUE RATHER THAN THE MEAN, for the same reason: one 200 ms frame moves a mean of
/// thirty samples by six milliseconds and moves the middle by nothing. The range is reported
/// beside it precisely so the outlier is still visible - a middle of 19 with a range of 17-24 is
/// a healthy machine, and a middle of 19 with a range of 17-180 is one worth asking about. On an
/// even count it takes the UPPER of the two, which is the conservative direction for a number
/// somebody may size a timeout against.
///
/// ONLY WATCHED ROLLS ARE COUNTED. A roll sent with nothing able to confirm it - no animation
/// table, no Actor address - measured nothing, and recording it as "unconfirmed" would read as
/// the game having failed to notice rather than as nobody having looked.
/// </remarks>
public sealed class RollTimes
{
    /// <summary>How many rolls are kept.</summary>
    /// <remarks>
    /// About forty seconds of fighting at the default cooldown, which is the balance being
    /// struck: long enough that the spread is not two samples, short enough that it describes
    /// the area being played rather than one from an hour ago with a different effect load.
    /// </remarks>
    public const int Remembered = 32;

    private readonly object _gate = new();
    private readonly Queue<int> _rolls = new();

    /// <summary>Steered rolls remembered, confirmed or not.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _rolls.Count;
            }
        }
    }

    /// <summary>Records one steered roll. Negative <paramref name="confirmedMs"/> means the
    /// game never confirmed it and the hold ran to its ceiling.</summary>
    public void Add(int confirmedMs)
    {
        lock (_gate)
        {
            _rolls.Enqueue(confirmedMs < 0 ? -1 : confirmedMs);
            while (_rolls.Count > Remembered)
            {
                _rolls.Dequeue();
            }
        }
    }

    /// <summary>Forgets everything, for a new area or a changed setting.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _rolls.Clear();
        }
    }

    /// <summary>
    /// The confirmed times in milliseconds, oldest first, without the unconfirmed ones.
    /// </summary>
    public IReadOnlyList<int> Confirmed
    {
        get
        {
            lock (_gate)
            {
                return [.. _rolls.Where(ms => ms >= 0)];
            }
        }
    }

    /// <summary>What the last few rolls cost, or empty when there have been none.</summary>
    /// <remarks>
    /// Empty rather than "no rolls yet" on purpose: this is appended to a status line that says
    /// something useful on its own, and a permanent "nothing here" is worse than a shorter line.
    /// </remarks>
    public string Describe()
    {
        int total;
        int[] seen;
        lock (_gate)
        {
            total = _rolls.Count;
            seen = [.. _rolls.Where(ms => ms >= 0)];
        }

        if (total == 0)
        {
            return string.Empty;
        }

        string rolls = total == 1 ? "1 roll" : $"{total} rolls";
        if (seen.Length == 0)
        {
            return $"{rolls}, none confirmed";
        }

        Array.Sort(seen);
        int fastest = seen[0];
        int slowest = seen[^1];
        int middle = seen[seen.Length / 2];

        string span = fastest == slowest
            ? $"{fastest} ms"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{fastest}-{slowest} ms (middle {middle})");

        int missed = total - seen.Length;
        return missed == 0
            ? $"{rolls} seen in {span}"
            : $"{rolls}, {seen.Length} seen in {span}, {missed} on the ceiling";
    }
}
