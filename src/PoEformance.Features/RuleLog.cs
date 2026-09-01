namespace PoEformance.Features;

/// <summary>One thing a rule did, and how many times in a row it did it.</summary>
/// <param name="At">
/// The WALL clock, not the engine's. This started as an age - "4s ago" - which is the right
/// answer for one line on a status bar and the wrong one for a list: reading a sequence of
/// steps means asking how far apart they were, and subtracting two ages in your head while
/// both of them are counting up is not something anybody does. A timestamp also survives being
/// screenshotted, which is how this log actually gets reported.
/// </param>
/// <param name="Detail">
/// The measurement behind the line - which entity, what life, how many presses - kept in its
/// own field rather than glued onto <paramref name="What"/> so it can have its own column.
/// Empty where there is nothing to measure.
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
    DateTime At,
    string Rule,
    string What,
    string Detail,
    int Count,
    bool Blocked)
{
    /// <summary>The timestamp as the log shows it.</summary>
    /// <remarks>
    /// To the millisecond, and that is not decoration: the whole point of the cull trace is
    /// that its steps happen within a few milliseconds of each other, so a stamp cut at the
    /// second would print the entire sequence as one instant.
    /// </remarks>
    public string Clock => At.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The third column: the measurement, and how many times it repeated.</summary>
    public string Measured => Count > 1
        ? Detail.Length > 0 ? $"{Detail}   x{Count}" : $"x{Count}"
        : Detail;
}

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

    /// <summary>Where the timestamps come from.</summary>
    /// <remarks>
    /// Injected on the same argument as the engine's <see cref="Random"/>: a log that called
    /// DateTime.Now itself would put the one thing a test needs to assert - the stamp - out of
    /// the test's reach. Local rather than UTC, because this is read beside a wall clock.
    /// </remarks>
    private readonly Func<DateTime> _now;

    public RuleLog()
        : this(static () => DateTime.Now)
    {
    }

    /// <summary>For tests: a clock whose readings are known in advance.</summary>
    public RuleLog(Func<DateTime> now)
    {
        ArgumentNullException.ThrowIfNull(now);
        _now = now;
    }

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

    /// <summary>Records something a rule DID, with what it measured while doing it.</summary>
    /// <param name="bad">
    /// Whether the thing that happened was a failure. Separate from
    /// <see cref="Blocked(string, string, string)"/>, which is about a state that HOLDS and so
    /// is written only when it changes: an outcome that reads badly is still an event, and
    /// suppressing the second identical one would hide a cull failing twice in a row - which is
    /// precisely the shape of the problem worth catching.
    /// </param>
    public void Acted(string rule, string what, string detail = "", bool bad = false)
    {
        lock (_gate)
        {
            // A rule that acts has plainly stopped being blocked, so the next block is worth
            // writing even if it is the same one as before. Without this, a rule that
            // alternates between working and failing writes its failure once and then looks
            // permanently fixed.
            _blocked.Remove(rule);
            Append(rule, what, detail, blocked: bad);
        }
    }

    /// <summary>Records why a rule did nothing - only when that reason has changed.</summary>
    public void Blocked(string rule, string why, string detail = "")
    {
        lock (_gate)
        {
            if (_blocked.TryGetValue(rule, out string? held) && string.Equals(held, why, StringComparison.Ordinal))
            {
                return;
            }

            _blocked[rule] = why;
            Append(rule, why, detail, blocked: true);
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

    /// <summary>Adds an entry, or bumps the one at the end when it says the same thing.</summary>
    private void Append(string rule, string what, string detail, bool blocked)
    {
        if (_entries.Count > 0)
        {
            RuleLogEntry last = _entries[^1];

            // The DETAIL has to match too, not just the wording. Two culls of two different
            // monsters both say "target found", and collapsing them would throw away the only
            // part that says they were different events.
            if (last.Blocked == blocked
                && string.Equals(last.Rule, rule, StringComparison.Ordinal)
                && string.Equals(last.What, what, StringComparison.Ordinal)
                && string.Equals(last.Detail, detail, StringComparison.Ordinal))
            {
                // The TIME moves to the newest occurrence, so the stamp answers "when did this
                // last happen" rather than "when did this start" - which is what a line
                // reading "x40" is being asked.
                _entries[^1] = last with { At = _now(), Count = last.Count + 1 };
                return;
            }
        }

        _entries.Add(new RuleLogEntry(_now(), rule, what, detail, 1, blocked));

        if (_entries.Count > Keep)
        {
            _entries.RemoveAt(0);
        }
    }
}
