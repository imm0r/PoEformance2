namespace PoEformance.Features;

/// <summary>One thing a rule did, and how many times in a row it did it.</summary>
/// <param name="AtMs">
/// On the engine's clock, not the wall's. Rendered as an AGE, which is what a log read during
/// a fight is actually asked - "is that happening now or left over from the last pack" - and
/// which needs no clock of its own to be testable.
/// </param>
/// <param name="Count">
/// How many identical entries this one stands for. A rule that fired twenty times in a row is
/// one line saying so; twenty lines would push everything else off the page, and the thing
/// worth seeing in a log is almost always the line that is NOT repeating.
/// </param>
/// <param name="Blocked">
/// Whether this is a reason nothing happened rather than something that did. Recorded rather
/// than guessed from the wording: a reader that decides "no key to press" is a failure by
/// looking for the word "no" is a reader that gets the next phrase wrong.
/// </param>
public readonly record struct RuleLogEntry(
    long AtMs,
    string Rule,
    string What,
    int Count,
    bool Blocked);

/// <summary>
/// A rolling history of what the rules did, because the one-line status cannot hold it.
/// </summary>
/// <remarks>
/// WHY A LOG AT ALL. The status line is this tick's reason, and a tick is 33 ms. With one rule
/// that was readable; with six it is a line that changes several times a second, and the
/// owner's report was the plain measurement: no status survives long enough to read. Every
/// question actually asked of the rule engine - did that rule fire, why did it stop, was the
/// cull aimed - is a question about the recent PAST, and a field that only ever shows the
/// present cannot answer any of them.
///
/// WHAT IS RECORDED, AND WHY NOT EVERYTHING. Two different shapes:
///
///   - An ACT is an event. It gets an entry every time, with consecutive identical ones
///     collapsed into a count.
///   - A BLOCK is a state. It gets an entry when it CHANGES, and nothing while it holds -
///     otherwise "no key to press" would be written thirty times a second and the log would
///     hold about six seconds of history.
///
/// The engine keeps the routine pacing - a cooldown, the typing floor - out of here entirely.
/// Those are the engine working as designed, they are true most of the time for most rules,
/// and they would be nine tenths of every page.
///
/// Drawings are not recorded either, on the same argument as the pacing but from the other
/// end: a caption that is holding is ALREADY on screen, continuously, for as long as it holds.
/// Logging it would be writing down what the player is looking at.
/// </remarks>
public sealed class RuleLog
{
    /// <summary>How many entries are kept.</summary>
    /// <remarks>
    /// Bounded because this runs for a whole session on the reader's thread. Two hundred
    /// COLLAPSED entries is a long way back - the collapsing is what makes a small number
    /// enough, since a busy rule spends its entries at one per burst rather than one per tick.
    /// </remarks>
    public const int Keep = 200;

    private readonly List<RuleLogEntry> _entries = [];

    /// <summary>The last blocking reason written for each rule, so a held state is written once.</summary>
    private readonly Dictionary<string, string> _blocked = new(StringComparer.Ordinal);

    private readonly Lock _gate = new();

    /// <summary>How many entries are held right now.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Records something a rule DID.</summary>
    public void Acted(long nowMs, string rule, string what)
    {
        lock (_gate)
        {
            // A rule that acts has plainly stopped being blocked, so the next block is worth
            // writing even if it is the same one as before. Without this, a rule that
            // alternates between working and failing writes its failure once and then looks
            // permanently fixed.
            _blocked.Remove(rule);
            Append(nowMs, rule, what, blocked: false);
        }
    }

    /// <summary>Records why a rule did nothing - only when that reason has changed.</summary>
    public void Blocked(long nowMs, string rule, string why)
    {
        lock (_gate)
        {
            if (_blocked.TryGetValue(rule, out string? held) && string.Equals(held, why, StringComparison.Ordinal))
            {
                return;
            }

            _blocked[rule] = why;
            Append(nowMs, rule, why, blocked: true);
        }
    }

    /// <summary>The newest entries first, at most <paramref name="limit"/> of them.</summary>
    public IReadOnlyList<RuleLogEntry> Recent(int limit)
    {
        if (limit <= 0)
        {
            return [];
        }

        lock (_gate)
        {
            int take = Math.Min(limit, _entries.Count);
            var recent = new List<RuleLogEntry>(take);
            for (int index = _entries.Count - 1; index >= _entries.Count - take; index--)
            {
                recent.Add(_entries[index]);
            }

            return recent;
        }
    }

    /// <summary>Forgets everything, including which rule was blocked by what.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _blocked.Clear();
        }
    }

    /// <summary>How long ago something was, short enough to sit in a narrow column.</summary>
    /// <remarks>
    /// Shared so the overlay and the config window cannot drift into two vocabularies for the
    /// same instant. Sub-second gets a decimal because that is the range these entries arrive
    /// in - "0s" for everything inside a second would make a burst look simultaneous.
    /// </remarks>
    public static string Age(long ms)
    {
        if (ms < 0)
        {
            // A clock that went backwards, which is a test's clock rather than a real one.
            ms = 0;
        }

        if (ms < 1000)
        {
            return $"{ms / 1000d:0.0}s";
        }

        if (ms < 60_000)
        {
            return $"{ms / 1000}s";
        }

        return ms < 3_600_000 ? $"{ms / 60_000}m" : $"{ms / 3_600_000}h";
    }

    /// <summary>Adds an entry, or bumps the one at the end when it says the same thing.</summary>
    private void Append(long nowMs, string rule, string what, bool blocked)
    {
        if (_entries.Count > 0)
        {
            RuleLogEntry last = _entries[^1];
            if (last.Blocked == blocked
                && string.Equals(last.Rule, rule, StringComparison.Ordinal)
                && string.Equals(last.What, what, StringComparison.Ordinal))
            {
                // The TIME moves to the newest occurrence, so the age answers "when did this
                // last happen" rather than "when did this start" - which is what a line
                // reading "fired x40" is being asked.
                _entries[^1] = last with { AtMs = nowMs, Count = last.Count + 1 };
                return;
            }
        }

        _entries.Add(new RuleLogEntry(nowMs, rule, what, 1, blocked));

        if (_entries.Count > Keep)
        {
            _entries.RemoveAt(0);
        }
    }
}
