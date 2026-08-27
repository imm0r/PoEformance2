using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoEformance.Features;

/// <summary>
/// What the purse came to at one moment.
/// </summary>
/// <remarks>
/// THE TIME IS WALL-CLOCK, in Unix milliseconds, and that is the one thing separating this from
/// every other history in the tool. The rest measure a map or a fight and key on
/// <c>Environment.TickCount64</c>, which is milliseconds since the machine booted - a number that
/// restarts at zero the next time the game is launched. A record spanning sessions cannot be
/// built on it: the second session's points would all sort before the first session's.
///
/// THE RATE IS STORED WITH THE POINT rather than applied when it is drawn. Converting an old
/// point at today's rate would make the Divine line move on days the purse did not: sixty Divine
/// held through a week when Divine doubled against Exalted is still sixty Divine, and a graph
/// that showed it halving would be reporting the market as if it were the player's own doing.
/// Storing the rate costs eight bytes and keeps each point's two readings both true of the same
/// moment.
/// </remarks>
/// <param name="At">Unix milliseconds.</param>
/// <param name="Exalted">What the currency came to, in Exalted.</param>
/// <param name="Rate">Exalted per Divine at that moment. 0 when the book had no rate.</param>
/// <param name="Stacks">How many separate stacks of currency were being carried.</param>
/// <param name="Drift">
/// How much of this record's whole movement, up to and including this point, was the PRICES
/// moving rather than the holdings.
/// </param>
/// <remarks>
/// DRIFT IS CUMULATIVE, and that is what makes it answerable over any window later: the price
/// share of a stretch is one subtraction, Drift at the end minus Drift at the start, and the
/// rest of the movement is what was actually picked up.
///
/// It has to be accumulated at the MOMENT, because that is the only time both holdings and both
/// price books are in hand. A record of totals cannot be decomposed afterwards - "the purse went
/// from 200k to 220k" says nothing about whether twenty thousand was gathered or repriced, and
/// no amount of arithmetic on the stored numbers recovers it.
///
/// Zero on points written before this existed, which reads as "all of it was gathered". That is
/// the wrong answer for those points and the only one available; a window that reaches back into
/// them says so rather than quietly attributing the lot.
/// </remarks>
public readonly record struct WealthPoint(
    [property: JsonPropertyName("at")] long At,
    [property: JsonPropertyName("ex")] double Exalted,
    [property: JsonPropertyName("rate")] double Rate = 0,
    [property: JsonPropertyName("stacks")] int Stacks = 0,
    [property: JsonPropertyName("drift")] double Drift = 0)
{
    /// <summary>The same amount in Divine, at the rate that was in force when it was taken.</summary>
    /// <remarks>
    /// NOT WRITTEN TO THE FILE, and the attribute is the whole reason this remark exists. A
    /// public read-only property is serialised by default, so both of these were being stored
    /// beside the fields they are computed FROM - a "Divine" that no reader ever looks at (there
    /// is no setter for it) and a "When" that repeats "at" in words. Two redundant fields per
    /// point, in the one file whose size <see cref="WealthHistory.Most"/> exists to bound, and
    /// two more chances for a hand-edited file to disagree with itself.
    /// </remarks>
    [JsonIgnore]
    public double Divine => Rate > 0 ? Exalted / Rate : 0;

    /// <inheritdoc cref="Divine"/>
    [JsonIgnore]
    public DateTimeOffset When => DateTimeOffset.FromUnixTimeMilliseconds(At);
}

/// <summary>The record as it is written down.</summary>
/// <param name="Since">
/// Unix milliseconds of when this record began - the first run, or the last time somebody reset
/// it by hand. Kept separately from the first point because the two differ: a reset happens the
/// moment the button is pressed, and the first point after it lands whenever the game is next
/// looked at.
/// </param>
public sealed record WealthLog(
    [property: JsonPropertyName("since")] long Since = 0,
    [property: JsonPropertyName("points")] IReadOnlyList<WealthPoint>? Points = null);

/// <summary>
/// The whole record of what the purse has been worth, across every session.
/// </summary>
/// <remarks>
/// IT NEVER CLEARS ITSELF. Not on a new league, not on a new character, not when the file gets
/// big, not when the game closes - only <see cref="Reset"/>, which nothing calls but a button.
/// That is the requirement, and it is worth stating as one because every other history in this
/// tool does the opposite: <c>DamageHistory</c> is a ring buffer that drops its oldest sample to
/// make room, which is right for a graph of one map and would be a quiet, permanent theft here.
///
/// SO THE FILE IS BOUNDED BY THINNING RATHER THAN BY DROPPING - see <see cref="Most"/>. The
/// record keeps spanning everything it ever spanned; what degrades with age is how finely it is
/// resolved. A month ago at hourly resolution is a true picture of a month ago. A month ago
/// missing entirely is not.
///
/// A GAP IN THE RECORD MEANS THE TOOL WAS NOT RUNNING, and that is what <see cref="Heartbeat"/>
/// buys. Without it, "unchanged for three days" and "not launched for three days" are the same
/// straight line between two points, and only one of those is a fact about the player's wealth.
/// Written from the reader thread and read from the renderer, so it locks - a handful of writes
/// an hour against one read a frame.
/// </remarks>
public sealed class WealthHistory
{
    /// <summary>Where the record lives, next to the rest of the configuration.</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "config", "wealth.json");

    /// <summary>
    /// The closest two points are allowed to be.
    /// </summary>
    /// <remarks>
    /// Picking up currency during a map moves the total several times a minute, and a graph of a
    /// month does not become truer for holding every one of them. This is the resolution the
    /// record is kept at while something is actively happening.
    /// </remarks>
    public const long MinGapMs = 30_000;

    /// <summary>How long an unchanged purse goes before a point is written anyway.</summary>
    /// <remarks>
    /// See the type remarks: this is what makes a gap mean something. Long enough that standing
    /// in a hideout for an hour costs four points.
    /// </remarks>
    public const long HeartbeatMs = 15 * 60_000;

    /// <summary>Points kept before the oldest half is thinned.</summary>
    /// <remarks>
    /// At the heartbeat alone this is over three months of continuous running, and thinning
    /// buys the next three months at half the resolution rather than ending the record. The
    /// file is a few hundred kilobytes at this size, which is the actual constraint - it is
    /// read and rewritten whole.
    /// </remarks>
    public const int Most = 10_000;

    /// <summary>How far two readings must differ before they are two points rather than one.</summary>
    /// <remarks>
    /// A hundredth of an Exalted. Not zero, because the total is floating point summed over
    /// hundreds of stacks and the last bits of it wobble without anything having happened -
    /// which at a 30-second gap would write a point every 30 seconds forever.
    /// </remarks>
    public const double Enough = 0.01;

    private readonly Lock _gate = new();
    private readonly List<WealthPoint> _points = [];
    private long _since;
    private bool _dirty;

    /// <summary>When this record began, or was last reset by hand. Unix milliseconds.</summary>
    public long Since
    {
        get
        {
            lock (_gate)
            {
                return _since;
            }
        }
    }

    /// <summary>How many points are in the record.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _points.Count;
            }
        }
    }

    /// <summary>Whether anything has changed since it was last written to disk.</summary>
    public bool Dirty
    {
        get
        {
            lock (_gate)
            {
                return _dirty;
            }
        }
    }

    /// <summary>The whole record, as a copy nothing else is writing to.</summary>
    /// <remarks>
    /// A copy rather than the list, because the renderer walks it for a whole frame while the
    /// reader thread may be adding to it. At ten thousand points of four fields this is a few
    /// hundred kilobytes a frame, which is why the graph asks for <see cref="Between"/> instead
    /// wherever it can.
    /// </remarks>
    public IReadOnlyList<WealthPoint> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _points];
            }
        }
    }

    /// <summary>The newest point, or null when nothing has been recorded.</summary>
    public WealthPoint? Latest
    {
        get
        {
            lock (_gate)
            {
                return _points.Count > 0 ? _points[^1] : null;
            }
        }
    }

    /// <summary>The oldest point still in the record.</summary>
    public WealthPoint? Earliest
    {
        get
        {
            lock (_gate)
            {
                return _points.Count > 0 ? _points[0] : null;
            }
        }
    }

    /// <summary>
    /// Offers a reading, and says whether it became a point.
    /// </summary>
    /// <remarks>
    /// CALLED OFTEN AND WRITES RARELY - that is the whole of it. Whatever is watching the purse
    /// can hand a reading over as often as it likes; the rules about how much has to change and
    /// how much time has to pass live here, once, rather than in every caller.
    ///
    /// A reading OLDER than the newest point is refused rather than inserted. The clock can move
    /// backwards - a correction, a timezone change, a machine that boots with a bad clock and
    /// fixes it a minute later - and a record whose points are not in order draws a graph that
    /// folds back on itself.
    /// </remarks>
    /// <param name="nowMs">Unix milliseconds. See the remarks on <see cref="WealthPoint"/>.</param>
    /// <returns>True when a point was written.</returns>
    public bool Note(long nowMs, double exalted, double rate, int stacks, double drift = 0)
    {
        lock (_gate)
        {
            if (_points.Count == 0)
            {
                if (_since == 0)
                {
                    _since = nowMs;
                }

                return Write(new WealthPoint(nowMs, exalted, rate, stacks, drift));
            }

            WealthPoint last = _points[^1];

            if (nowMs < last.At || nowMs - last.At < MinGapMs)
            {
                return false;
            }

            bool moved = Math.Abs(exalted - last.Exalted) >= Enough
                         || Math.Abs(rate - last.Rate) >= Enough;

            return (moved || nowMs - last.At >= HeartbeatMs)
                   && Write(new WealthPoint(nowMs, exalted, rate, stacks, drift));
        }
    }

    /// <summary>
    /// Throws the whole record away and starts it again from now.
    /// </summary>
    /// <remarks>
    /// THE ONLY THING THAT EMPTIES THIS, and nothing in the tool calls it - see the type remarks.
    /// It is destructive and it is not undoable, which is why whatever offers it has to make the
    /// user say so twice.
    /// </remarks>
    public void Reset(long nowMs)
    {
        lock (_gate)
        {
            _points.Clear();
            _since = nowMs;
            _dirty = true;
        }
    }

    /// <summary>The points inside a stretch of time, without copying the rest.</summary>
    /// <remarks>
    /// The point in force at <paramref name="fromMs"/> is included even though it is older, when
    /// there is one. A window that began mid-flat-stretch otherwise starts at whatever the next
    /// change happened to be, and the graph draws the window's own first reading as if the value
    /// had jumped to it.
    /// </remarks>
    public IReadOnlyList<WealthPoint> Between(long fromMs, long toMs)
    {
        lock (_gate)
        {
            var found = new List<WealthPoint>();
            WealthPoint? before = null;

            foreach (WealthPoint point in _points)
            {
                if (point.At < fromMs)
                {
                    before = point;
                    continue;
                }

                if (point.At > toMs)
                {
                    break;
                }

                found.Add(point);
            }

            if (before is { } anchor)
            {
                found.Insert(0, anchor);
            }

            return found;
        }
    }

    /// <summary>What the purse was worth at a moment - the last reading taken at or before it.</summary>
    public WealthPoint? At(long ms)
    {
        lock (_gate)
        {
            WealthPoint? found = null;
            foreach (WealthPoint point in _points)
            {
                if (point.At > ms)
                {
                    break;
                }

                found = point;
            }

            return found;
        }
    }

    /// <summary>
    /// How much the purse has moved since a moment, in Exalted.
    /// </summary>
    /// <remarks>
    /// Null when there is nothing to compare - no points, or none as old as the moment asked
    /// about. That is different from a change of zero, and a readout that showed "0" for
    /// "the record does not go back that far" would be inventing a fact.
    /// </remarks>
    public double? ChangeSince(long ms)
    {
        lock (_gate)
        {
            if (_points.Count == 0)
            {
                return null;
            }

            WealthPoint? then = null;
            foreach (WealthPoint point in _points)
            {
                if (point.At > ms)
                {
                    break;
                }

                then = point;
            }

            return then is { } from ? _points[^1].Exalted - from.Exalted : null;
        }
    }

    /// <summary>Everything the record has moved, first point to last.</summary>
    public double? Change
    {
        get
        {
            lock (_gate)
            {
                return _points.Count >= 2 ? _points[^1].Exalted - _points[0].Exalted : null;
            }
        }
    }

    /// <summary>Reads the record back, or starts an empty one.</summary>
    public static WealthHistory Load(string? path = null)
    {
        var history = new WealthHistory();
        string file = path ?? DefaultPath;

        try
        {
            if (!File.Exists(file))
            {
                return history;
            }

            using FileStream stream = File.OpenRead(file);
            WealthLog? log = JsonSerializer.Deserialize(stream, WealthJsonContext.Default.WealthLog);
            if (log is null)
            {
                return history;
            }

            history._since = log.Since;

            // Sorted and de-duplicated on the way in rather than trusted. This file outlives
            // every version of the tool that wrote it, and it is plain JSON somebody can edit;
            // an out-of-order point would draw a graph that folds back on itself, and every
            // query below assumes the order.
            if (log.Points is { Count: > 0 } points)
            {
                history._points.AddRange(points.Where(point => point.At > 0).OrderBy(point => point.At));
            }
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // An unreadable record is an empty one for this run. It is NOT overwritten: whatever
            // is in that file is somebody's whole history, and the one thing worse than not
            // drawing it is replacing it with today.
            return new WealthHistory { _readable = false };
        }

        return history;
    }

    /// <summary>Whether the file on disk could be read. False means it exists and is damaged.</summary>
    public bool Readable => _readable;

    private bool _readable = true;

    /// <summary>Writes the record, returning false when it could not.</summary>
    public bool Save(string? path = null)
    {
        string file = path ?? DefaultPath;

        WealthLog log;
        lock (_gate)
        {
            log = new WealthLog(_since, [.. _points]);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using (FileStream stream = File.Create(file))
            {
                JsonSerializer.Serialize(stream, log, WealthJsonContext.Default.WealthLog);
            }

            lock (_gate)
            {
                _dirty = false;
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Adds a point and keeps the file bounded. Call under the lock.</summary>
    private bool Write(WealthPoint point)
    {
        _points.Add(point);
        _dirty = true;

        if (_points.Count > Most)
        {
            Thin();
        }

        return true;
    }

    /// <summary>
    /// Halves the resolution of the older half, leaving the recent half alone.
    /// </summary>
    /// <remarks>
    /// The oldest point survives - it is at an even index - so the record keeps saying how far
    /// back it goes. Applied to the OLDER HALF only, so what happened this week stays at full
    /// resolution however long the record gets; only the distant past goes coarse, which is the
    /// order in which detail stops being wanted.
    /// </remarks>
    private void Thin()
    {
        int half = _points.Count / 2;
        var kept = new List<WealthPoint>(_points.Count);

        for (int i = 0; i < half; i += 2)
        {
            kept.Add(_points[i]);
        }

        kept.AddRange(_points.GetRange(half, _points.Count - half));

        _points.Clear();
        _points.AddRange(kept);
    }
}

/// <summary>Source-generated JSON, so the record survives Native AOT.</summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(WealthLog))]
public sealed partial class WealthJsonContext : JsonSerializerContext;
