using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Diagnostics;

namespace PoEformance.Core.Tests;

/// <summary>
/// The sweep for the four components neither reference had a layout for, and what it decoded.
/// </summary>
/// <remarks>
/// One capture answered two of the four and closed the other two with a negative, which is the
/// shape worth keeping: GroundEffect carries a countdown in seconds, Beam carries the line it
/// draws, and LimitedLifespan and DiesAfterTime carry no timer at all in any of the three forms
/// one could take. The last of those is a RESULT and is tested as one - a component named
/// LimitedLifespan that does not hold a lifespan is exactly the thing a later reader would
/// otherwise assume had simply not been looked at.
///
/// Both positive findings rest on a check the game settles rather than on the shape of the bytes,
/// and the tests are written around those checks rather than around the values:
///  - the countdown must PREDICT the frame the entity leaves the list, from tens of seconds out;
///  - the beam's far end must pick out entities where THE MIDPOINT OF THE SAME LINE does not.
/// Landing near an entity is cheap in a fight. The control is the evidence.
/// </remarks>
public class ComponentSweepTests
{
    private static string Fixture(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "tests", "fixtures", name);
    }

    private static List<SweepFrame> Replay(string fixture, uint step = 10)
    {
        using var replay = ReplayMemoryReader.Load(File.OpenRead(Fixture(fixture)));
        OffsetSchema schema = RealSessionTests.Schema();
        var sweep = new ComponentSweep(replay, schema);
        ulong gameStates = replay.ResolvedStatics["GameStates"];

        var frames = new List<SweepFrame>();
        for (uint frame = 0; frame < replay.FrameCount; frame += step)
        {
            replay.Seek(frame);
            if (sweep.SampleFrame(gameStates, (int)frame) is { } got)
            {
                frames.Add(got);
            }
        }

        return frames;
    }

    /// <summary>Each entity's readings in frame order, with the frame's timestamp in seconds.</summary>
    private static Dictionary<uint, List<(double Seconds, byte[] Bytes)>> Tracks(
        string fixture, string component, out double lastSecond)
    {
        using var replay = ReplayMemoryReader.Load(File.OpenRead(Fixture(fixture)));
        OffsetSchema schema = RealSessionTests.Schema();
        var sweep = new ComponentSweep(replay, schema);
        ulong gameStates = replay.ResolvedStatics["GameStates"];

        var tracks = new Dictionary<uint, List<(double, byte[])>>();
        lastSecond = 0;
        for (uint frame = 0; frame < replay.FrameCount; frame++)
        {
            replay.Seek(frame);
            if (sweep.SampleFrame(gameStates, (int)frame) is not { } got)
            {
                continue;
            }

            double seconds = replay.FrameTimes[(int)frame] / 1000.0;
            lastSecond = seconds;
            foreach (ComponentObservation o in got.Seen.Where(o =>
                string.Equals(o.Component, component, StringComparison.Ordinal)))
            {
                tracks.TryAdd(o.EntityId, []);
                tracks[o.EntityId].Add((seconds, o.Bytes));
            }
        }

        return tracks;
    }

    [Fact]
    public void OnARecordingMadeBeforeTheSwitchExisted_ItFindsNoBytesAndSaysSo()
    {
        // A replay only serves reads that happened. Every fixture older than --sweep is like
        // this, and a sweep that came back with observations here would be reading something
        // other than what it thinks it is.
        foreach (string fixture in (string[])
            ["session-2026-08-effects.rec", "session-2026-08-deployed.rec", "session-2026-08-buffs.rec"])
        {
            List<SweepFrame> frames = Replay(fixture);
            Assert.NotEmpty(frames);
            Assert.All(frames, f => Assert.Empty(f.Seen));
        }
    }

    [Fact]
    public void WithNoBytesToShow_TheReportSaysThatRatherThanConcluding()
    {
        var text = new StringWriter();
        ComponentSweep.Report(Replay("session-2026-08-effects.rec"), RealSessionTests.Schema(), text);
        string report = text.ToString();

        Assert.Contains("NOT ONE of the swept components was readable", report, StringComparison.Ordinal);

        // The sentences a report built on absent data must never produce.
        Assert.DoesNotContain("countdown candidate", report, StringComparison.Ordinal);
        Assert.DoesNotContain("a world point", report, StringComparison.Ordinal);
        Assert.DoesNotContain("AS DECODED", report, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptySweepIsReportedAsNoFrames()
    {
        var text = new StringWriter();
        ComponentSweep.Report([], RealSessionTests.Schema(), text);
        Assert.Contains("no frames", text.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheSchemaCarriesACellForEveryComponentTheSweepReads()
    {
        // Without a cell the sweep falls back to a default, and a fallback that goes unnoticed is
        // how a component gets swept at the wrong width for a year.
        OffsetSchema schema = RealSessionTests.Schema();
        foreach (string name in ComponentSweep.Undecoded)
        {
            StructDef def = schema.Structs[name];
            Assert.True(def.Constants.ContainsKey("PoolCell"), $"{name} has no measured PoolCell");
            Assert.True(def.Constants["PoolCell"] <= ComponentSweep.DefaultCell,
                $"{name}'s cell exceeds the sweep's buffer");
        }

        // The two that stayed empty, and it is a finding rather than a gap: one capture asked
        // them for a timer in three different forms and none was there. Somebody adding a field
        // here from a reference should fail this and go read the struct's note first.
        Assert.Empty(schema.Structs["LimitedLifespan"].Fields);
        Assert.Empty(schema.Structs["DiesAfterTime"].Fields);
    }

    /// <summary>
    /// GroundEffect+0x58 predicts when the game stops listing the effect.
    /// </summary>
    /// <remarks>
    /// The test is deliberately not "the value falls to zero" - an alpha, a fade or any decaying
    /// quantity does that. It is that "now + value" names the delisting moment from tens of
    /// seconds out, with a spread narrow enough that nothing but a clock could hold it.
    ///
    /// The 0.38 s bias is asserted rather than tolerated away: the timer reaches zero a beat
    /// BEFORE the entity leaves, consistently, which is a fact about how the game despawns and
    /// would be thrown away by a test that only checked |error| against a loose bound.
    /// </remarks>
    [Fact]
    public void GroundEffectCarriesSecondsRemaining_AndPredictsTheDelisting()
    {
        OffsetSchema schema = RealSessionTests.Schema();
        int at = schema.Structs["GroundEffect"].OffsetOf("SecondsRemaining");
        Dictionary<uint, List<(double Seconds, byte[] Bytes)>> tracks =
            Tracks("session-2026-08-sweep.rec", "GroundEffect", out double lastSecond);

        var errors = new List<double>();
        int expired = 0;
        foreach ((uint _, List<(double Seconds, byte[] Bytes)> track) in tracks)
        {
            if (track.Count < 6 || track[^1].Seconds > lastSecond - 0.5)
            {
                continue; // still on screen at the end - no expiry to check against
            }

            expired++;
            double death = track[^1].Seconds;
            foreach ((double seconds, byte[] bytes) in track)
            {
                float value = BitConverter.ToSingle(bytes, at);
                if (float.IsFinite(value) && value > 0.05)
                {
                    errors.Add(seconds + value - death);
                }
            }
        }

        Assert.True(expired >= 40, $"only {expired} effects expired inside the capture");
        Assert.True(errors.Count > 1000, $"only {errors.Count} readings");

        errors.Sort();
        double median = errors[errors.Count / 2];
        double low = errors[errors.Count / 20], high = errors[errors.Count * 19 / 20];

        // It reaches zero BEFORE the entity goes, by a consistent margin.
        Assert.InRange(median, -0.6, -0.2);

        // And the band is the evidence: 90% of 1400+ readings inside a fifth of a second. A
        // value that merely decayed could not hold that against a wall clock.
        Assert.True(high - low < 0.25, $"the error band is {high - low:F2}s wide - too loose to be a clock");
        Assert.True(errors.Count(e => Math.Abs(e) < 1) > errors.Count * 0.95,
            "fewer than 95% of predictions land within a second");
    }

    /// <summary>
    /// Beam holds the line it draws, and the midpoint control is what proves the far end.
    /// </summary>
    [Fact]
    public void BeamCarriesItsTwoEnds_AndTheMidpointControlSeparatesThem()
    {
        OffsetSchema schema = RealSessionTests.Schema();
        StructDef beam = schema.Structs["Beam"];
        int source = beam.OffsetOf("SourceX"), target = beam.OffsetOf("TargetX");

        List<SweepFrame> frames = Replay("session-2026-08-sweep.rec", step: 1);
        List<ComponentObservation> beams =
            [.. frames.SelectMany(f => f.Seen).Where(o => string.Equals(o.Component, "Beam", StringComparison.Ordinal))];
        Assert.True(beams.Count > 500, $"only {beams.Count} beam readings");

        // The near end IS the entity's own position - the anchor that makes the pair readable.
        // Exact, not approximate: it is the same float the Render component holds.
        foreach (ComponentObservation o in beams)
        {
            Assert.Equal(o.WorldX, BitConverter.ToSingle(o.Bytes, source));
            Assert.Equal(o.WorldY, BitConverter.ToSingle(o.Bytes, source + 4));
        }

        // The far end is somewhere else - a line, not a point.
        var lengths = beams.Select(o =>
        {
            float ax = BitConverter.ToSingle(o.Bytes, source), ay = BitConverter.ToSingle(o.Bytes, source + 4);
            float bx = BitConverter.ToSingle(o.Bytes, target), by = BitConverter.ToSingle(o.Bytes, target + 4);
            return Math.Sqrt(((bx - ax) * (bx - ax)) + ((by - ay) * (by - ay)));
        }).ToList();
        Assert.True(lengths.Min() > 5, "some beam has no length at all");

        // THE CONTROL, and the frame it is asked in matters: a beam ends on somebody standing
        // there AT THAT MOMENT, so the far end is matched against that frame's entities rather
        // than against every point the session ever held. Asking it against the beam carriers'
        // own positions instead - the first way this was written - scored the real finding at 69
        // of 1098, because the thing a beam points at carries none of the swept components.
        int endNear = 0, midNear = 0, tried = 0;
        foreach (SweepFrame f in frames)
        {
            if (f.EntityPoints.Count < 5)
            {
                continue;
            }

            foreach (ComponentObservation o in f.Seen.Where(o =>
                string.Equals(o.Component, "Beam", StringComparison.Ordinal)))
            {
                float ax = BitConverter.ToSingle(o.Bytes, source), ay = BitConverter.ToSingle(o.Bytes, source + 4);
                float bx = BitConverter.ToSingle(o.Bytes, target), by = BitConverter.ToSingle(o.Bytes, target + 4);
                double Nearest(float x, float y) => f.EntityPoints.Min(p =>
                    Math.Sqrt(((x - p.X) * (x - p.X)) + ((y - p.Y) * (y - p.Y))));

                tried++;
                if (Nearest(bx, by) < ComponentSweep.NearMiss) { endNear++; }
                if (Nearest((ax + bx) / 2, (ay + by) / 2) < ComponentSweep.NearMiss) { midNear++; }
            }
        }

        Assert.True(tried > 500, $"only {tried} readings had a crowd to check against");
        Assert.True(endNear > tried * 0.8, $"the far end only hit an entity {endNear}/{tried} times");
        Assert.True(endNear > midNear * 2,
            $"the midpoint scores {midNear}/{tried} against the far end's {endNear}"
            + " - without that gap, 'lands near an entity' says nothing in a crowded fight");

        var text = new StringWriter();
        ComponentSweep.Report(frames, schema, text);
        Assert.Contains("AS DECODED", text.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Neither LimitedLifespan nor DiesAfterTime holds a timer, in any of three forms.
    /// </summary>
    /// <remarks>
    /// The negative is worth a test because the names invite the opposite assumption, and because
    /// it took a capture with 52,803 and 5,310 readings to earn: a slot could hold the time
    /// REMAINING (falls to zero at expiry), the DEADLINE (constant per entity, ordered like the
    /// death times) or the DURATION (constant per entity, ordered like the lifetimes). All three
    /// were asked. None answered.
    /// </remarks>
    [Fact]
    public void NeitherLifespanComponentHoldsATimer_InAnyOfTheThreeForms()
    {
        foreach (string component in (string[])["LimitedLifespan", "DiesAfterTime"])
        {
            Dictionary<uint, List<(double Seconds, byte[] Bytes)>> tracks =
                Tracks("session-2026-08-sweep.rec", component, out double lastSecond);

            var expired = tracks.Values
                .Where(t => t.Count >= 6 && t[^1].Seconds < lastSecond - 0.5)
                .ToList();
            Assert.True(expired.Count >= 20, $"{component}: only {expired.Count} expired to judge by");

            int width = expired.Min(t => t.Min(x => x.Bytes.Length));
            var deaths = expired.Select(t => t[^1].Seconds).ToList();
            var lives = expired.Select(t => t[^1].Seconds - t[0].Seconds).ToList();

            for (int off = 0; off + 4 <= width; off += 4)
            {
                // 1. Time remaining: does it fall to near zero at expiry on every entity?
                var firsts = expired.Select(t => BitConverter.ToSingle(t[0].Bytes, off)).ToList();
                var finals = expired.Select(t => BitConverter.ToSingle(t[^1].Bytes, off)).ToList();
                bool countsDown = firsts.Zip(finals).All(p =>
                    float.IsFinite(p.First) && float.IsFinite(p.Second)
                    && p.First > 0.05 && Math.Abs(p.Second) < 0.05);
                Assert.False(countsDown, $"{component}+0x{off:X2} counts down - the schema says none does");

                // 2 and 3. Deadline or duration: a constant that is ordered like the death times
                // or like the lifetimes. Correlation, so it holds whatever the units are.
                foreach (bool asFloat in (bool[])[true, false])
                {
                    var v = expired.Select(t => asFloat
                        ? BitConverter.ToSingle(t[0].Bytes, off)
                        : BitConverter.ToUInt32(t[0].Bytes, off)).Select(x => (double)x).ToList();
                    if (v.Any(x => !double.IsFinite(x)) || v.Distinct().Count() < 5)
                    {
                        continue;
                    }

                    Assert.True(Math.Abs(Pearson(v, deaths)) < 0.95,
                        $"{component}+0x{off:X2} tracks the death time - that would be a deadline");
                    Assert.True(Math.Abs(Pearson(v, lives)) < 0.95,
                        $"{component}+0x{off:X2} tracks the lifetime - that would be a duration");
                }
            }
        }
    }

    private static double Pearson(List<double> a, List<double> b)
    {
        double ma = a.Average(), mb = b.Average();
        double num = a.Zip(b).Sum(p => (p.First - ma) * (p.Second - mb));
        double da = Math.Sqrt(a.Sum(x => (x - ma) * (x - ma)));
        double db = Math.Sqrt(b.Sum(x => (x - mb) * (x - mb)));
        return da == 0 || db == 0 ? 0 : num / (da * db);
    }
}
