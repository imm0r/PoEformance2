using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Entities;

namespace PoEformance.Game.Diagnostics;

/// <summary>One component of one entity, whole, on one frame.</summary>
/// <param name="Component">The class name the game gave it.</param>
/// <param name="EntityId">The game's own id, so an entity can be followed across frames.</param>
/// <param name="Path">Its metadata path.</param>
/// <param name="Bytes">The component from +0x00 to its pool cell, or as far as the read got.</param>
/// <param name="WorldX">The entity's own position, carried so a position INSIDE the component can be recognised.</param>
public sealed record ComponentObservation(
    string Component,
    uint EntityId,
    string Path,
    ulong Address,
    byte[] Bytes,
    float WorldX,
    float WorldY,
    float WorldZ);

/// <summary>What one frame of the sweep saw.</summary>
public sealed record SweepFrame(int Frame, IReadOnlyList<ComponentObservation> Seen);

/// <summary>
/// Reads whole components of the classes nobody has a layout for.
/// </summary>
/// <remarks>
/// THE FOUR IT EXISTS FOR are the ones the danger model needs and neither reference has:
/// LimitedLifespan, DiesAfterTime, GroundEffect and Beam. That is not an oversight in the
/// references - GameHelper2 registers layouts for 21 components and treats these as MARKERS,
/// testing only whether the component is present (its own note calls them "components present on
/// entities but with no registered layout"), and the AHK tool's DiesAfterTime decoder reads the
/// two header fields and stops. So there are no candidate offsets to verify here. This is a
/// blind sweep, and the decoding happens afterwards against the file.
///
/// WHY WHOLE COMPONENTS AND ALL FOUR AT ONCE, which is the question that decided the design:
/// each of them is tiny - the measured pool cells are 0x60, 0x50, 0xC0 and 0xA0 - so "the whole
/// component" is complete by construction rather than a judgement about where to stop, the same
/// argument that made the Monster component cheap to be thorough about. And they are not many at
/// a time: across every committed recording the concurrent maxima are 39, 3, 11 and 5, so
/// reading every carrier of all four costs under 9 KB a frame at the worst moment in any session
/// on file. There is no saving worth having from doing them one at a time, and a real cost:
/// these things appear during fights, and the situations overlap. Four captures would be four
/// fights and four chances to miss.
///
/// THE TWO SIGNALS THE DECODE RESTS ON, both settled by the game rather than by argument:
///  1. A COUNTDOWN must reach zero when the entity leaves the list. The entity list is in the
///     same recording, so this is checkable offline without believing anything in advance - and
///     the lifetimes are already measured: LimitedLifespan carriers live a median 0.5 s, the
///     DiesAfterTime totems 7-19 s, ground effects 13-50 s.
///  2. A POSITION inside a component must be near the entity's own. That is why WorldX/Y/Z ride
///     along on every observation: three floats that look like coordinates prove nothing, and
///     three floats within a few units of where the game already says the entity is do.
/// </remarks>
public sealed class ComponentSweep
{
    /// <summary>The classes swept, and why these four. See the class remarks.</summary>
    public static readonly string[] Undecoded =
        ["LimitedLifespan", "DiesAfterTime", "GroundEffect", "Beam"];

    /// <summary>Largest component read attempted when the schema names no cell.</summary>
    public const int DefaultCell = 0xC0;

    private readonly IMemoryReader _reader;
    private readonly OffsetSchema _schema;
    private readonly EntityReader _entities;
    private readonly EntityMapReader _map;
    private readonly int _awake;
    private readonly int _worldPosition;
    private readonly Dictionary<string, int> _cells = new(StringComparer.Ordinal);
    private readonly byte[] _buffer = new byte[DefaultCell];

    public ComponentSweep(IMemoryReader reader, OffsetSchema schema, IReadOnlyList<string>? components = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;
        _schema = schema;
        _entities = new EntityReader(reader, schema);
        _map = new EntityMapReader(reader, schema);
        _awake = schema.Structs["AreaInstance"].OffsetOf("AwakeEntities");
        _worldPosition = schema.Structs["Render"].OffsetOf("CurrentWorldPosition");

        foreach (string name in components ?? Undecoded)
        {
            // The cell when the schema knows one, and it is the schema's job to know: these were
            // measured from the recordings and belong with the rest of the offsets rather than
            // in a table here.
            _cells[name] = schema.Structs.TryGetValue(name, out StructDef? def)
                && def.Constants.TryGetValue("PoolCell", out long cell)
                    ? (int)Math.Min(cell, DefaultCell)
                    : DefaultCell;
        }
    }

    /// <summary>Performs every read for one frame. Null when not in a world.</summary>
    public SweepFrame? SampleFrame(ulong gameStatesStatic, int frame)
    {
        GameChainAddresses chain = GameChain.Resolve(_reader, _schema, gameStatesStatic);
        if (chain.AreaInstance == 0)
        {
            return null;
        }

        var seen = new List<ComponentObservation>();
        foreach ((uint id, ulong address) in _map.ReadEntityPointers(chain.AreaInstance + (ulong)_awake))
        {
            if (_entities.ReadIdentity(address) is not { } identity || identity.Path.Length == 0)
            {
                continue;
            }

            IReadOnlyDictionary<string, ulong> components = _entities.ReadComponents(address, identity.Details);

            // The entity's own position, read ONCE for the whole entity rather than per
            // component: it is the reference a position inside a component gets checked against,
            // and most of these entities carry only one of the four anyway.
            float x = 0, y = 0, z = 0;
            ulong render = components.GetValueOrDefault("Render");
            if (render != 0)
            {
                x = _reader.Read<float>(render + (ulong)_worldPosition);
                y = _reader.Read<float>(render + (ulong)_worldPosition + 4);
                z = _reader.Read<float>(render + (ulong)_worldPosition + 8);
            }

            foreach ((string name, int cell) in _cells)
            {
                ulong at = components.GetValueOrDefault(name);
                if (at == 0)
                {
                    continue;
                }

                // The ladder the companion hunt earned: one read is all-or-nothing, and a cell
                // measured from too few pairs (DiesAfterTime rests on seven gaps, Beam's divides
                // only 69 of 88) could be too large. Falling back keeps a short read rather than
                // recording nothing at all.
                int got = 0;
                foreach (int size in (int[])[cell, 0x40, 0x20, 0x10])
                {
                    if (size <= cell && _reader.TryRead(at, _buffer.AsSpan(0, size)))
                    {
                        got = size;
                        break;
                    }
                }

                if (got > 0)
                {
                    seen.Add(new ComponentObservation(
                        name, id, identity.Path, at, _buffer[..got], x, y, z));
                }
            }
        }

        return new SweepFrame(frame, seen);
    }

    /// <summary>Says what the session holds, and runs the two checks the game can settle.</summary>
    public static void Report(IReadOnlyList<SweepFrame> frames, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(output);

        output.WriteLine();
        output.WriteLine("component sweep");
        if (frames.Count == 0)
        {
            output.WriteLine("  no frames - never in a world.");
            return;
        }

        // Frame-tagged, because the countdown check is a TIME series and an observation on its
        // own does not know when it happened.
        List<(int Frame, ComponentObservation O)> all =
            [.. frames.SelectMany(f => f.Seen.Select(o => (f.Frame, o)))];
        if (all.Count == 0)
        {
            output.WriteLine($"  {frames.Count} frames, and NOT ONE of the swept components was readable.");
            output.WriteLine("  On a replay that is the ordinary answer: no build had read them, so no");
            output.WriteLine("  recording contains them. Run --sweep against the game to change that.");
            return;
        }

        foreach (IGrouping<string, (int Frame, ComponentObservation O)> byComponent
            in all.GroupBy(t => t.O.Component))
        {
            List<(int Frame, ComponentObservation O)> obs = [.. byComponent];
            int width = obs.Min(t => t.O.Bytes.Length);
            int entities = obs.Select(t => t.O.EntityId).Distinct().Count();

            output.WriteLine();
            output.WriteLine($"  {byComponent.Key}: {obs.Count} readings over {entities} entities,"
                + $" 0x{width:X} bytes each");
            foreach ((string path, int n) in obs.GroupBy(t => t.O.Path)
                .Select(g => (g.Key, g.Select(t => t.O.EntityId).Distinct().Count()))
                .OrderByDescending(t => t.Item2).Take(3))
            {
                output.WriteLine($"      {n,4}  {path}");
            }

            ReportCountdowns(obs, width, output);
            ReportPositions([.. obs.Select(t => t.O)], width, output);
        }
    }

    /// <summary>
    /// Floats that fall towards zero as the entity's last frame approaches.
    /// </summary>
    /// <remarks>
    /// The check that needs no prior belief about the layout, and the reason the entity list is
    /// worth carrying alongside: a countdown is not "a float that decreases" - plenty of things
    /// drift down - it is a float that decreases AND is near zero on the frame the game stops
    /// listing the entity. The second half is what an unrelated float fails.
    /// </remarks>
    private static void ReportCountdowns(
        List<(int Frame, ComponentObservation O)> obs, int width, TextWriter output)
    {
        // Each entity's readings in frame order. Only entities the recording saw DISAPPEAR can
        // say anything: one still listed on the last frame has no expiry to check against, and
        // counting it would let a value that merely drifts down pass.
        int lastFrame = obs.Max(t => t.Frame);
        var series = new Dictionary<uint, List<(int Frame, byte[] Bytes)>>();
        foreach ((int frame, ComponentObservation o) in obs)
        {
            series.TryAdd(o.EntityId, []);
            series[o.EntityId].Add((frame, o.Bytes));
        }

        List<List<(int Frame, byte[] Bytes)>> expired =
        [
            .. series.Values
                .Select(v => v.OrderBy(t => t.Frame).ToList())
                .Where(v => v.Count >= 4 && v[^1].Frame < lastFrame),
        ];

        if (expired.Count < 3)
        {
            output.WriteLine($"    only {expired.Count} entities both appeared and vanished inside the"
                + " capture - too few to test a countdown against expiry.");
            return;
        }

        var hits = new List<(int Offset, int Entities, double WorstEnd)>();
        for (int off = 0; off + 4 <= width; off += 4)
        {
            int fell = 0, tried = 0;
            double worstEnd = 0;

            foreach (List<(int Frame, byte[] Bytes)> track in expired)
            {
                float first = BitConverter.ToSingle(track[0].Bytes, off);
                float last = BitConverter.ToSingle(track[^1].Bytes, off);
                if (!float.IsFinite(first) || !float.IsFinite(last) || first <= 0)
                {
                    continue;
                }

                tried++;
                if (last < first && Math.Abs(last) < Math.Abs(first) * 0.25)
                {
                    fell++;
                    worstEnd = Math.Max(worstEnd, Math.Abs(last));
                }
            }

            if (tried >= 3 && fell == tried)
            {
                hits.Add((off, fell, worstEnd));
            }
        }

        if (hits.Count == 0)
        {
            output.WriteLine("    no slot falls to near zero on every entity - no countdown found here.");
            return;
        }

        foreach ((int offset, int count, double worstEnd) in hits)
        {
            output.WriteLine($"    +0x{offset:X2} FALLS TOWARDS ZERO on all {count} entities followed"
                + $" (largest final value {worstEnd:G4}) - countdown candidate.");
        }
    }

    /// <summary>Three consecutive floats that sit where the game already says the entity is.</summary>
    /// <remarks>
    /// Three floats in a plausible coordinate range are worth nothing on their own - the map is
    /// tens of thousands of units across and almost any float pair lands inside it. Matching the
    /// entity's OWN position, which came out of a different component entirely, is the part that
    /// cannot happen by chance, and a beam's far end is then the interesting near-miss rather
    /// than a failure.
    /// </remarks>
    private static void ReportPositions(List<ComponentObservation> obs, int width, TextWriter output)
    {
        for (int off = 0; off + 8 <= width; off += 4)
        {
            int near = 0, tried = 0;
            foreach (ComponentObservation o in obs)
            {
                if (o.WorldX == 0 && o.WorldY == 0)
                {
                    continue;
                }

                float x = BitConverter.ToSingle(o.Bytes, off);
                float y = BitConverter.ToSingle(o.Bytes, off + 4);
                if (!float.IsFinite(x) || !float.IsFinite(y))
                {
                    continue;
                }

                tried++;
                if (Math.Abs(x - o.WorldX) < 20 && Math.Abs(y - o.WorldY) < 20)
                {
                    near++;
                }
            }

            if (tried >= 10 && near > tried * 0.8)
            {
                output.WriteLine($"    +0x{off:X2} holds the ENTITY'S OWN POSITION on {near} of {tried}"
                    + " readings - a world point, not a coincidence.");
            }
        }
    }
}
