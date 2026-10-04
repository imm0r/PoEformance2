using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Diagnostics;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// The sweep capture walked once, every frame, for every test in <see cref="ComponentSweepTests"/>.
/// </summary>
/// <remarks>
/// ONE WALK WHERE THERE WERE FOUR. Each test used to open the recording, rebuild the schema and
/// step every frame through the sweep for itself - and the lifespan test did it twice, once per
/// component. At some fifty seconds a walk that was 193 seconds for seven tests, and because xUnit
/// runs a class serially it was the floor under the whole suite's wall time: nothing else could
/// finish sooner than this class did. The frames are the same frames whichever test asks, so they
/// are walked here, once, and the tests only read them. <see cref="MemoryWalk"/> is the precedent.
///
/// EVERY FRAME, NOT EVERY TENTH, because the tests that need the finest step (the beam control, the
/// expiry prediction) are the ones that cost the most, and a coarser walk would be a second walk.
/// </remarks>
public sealed class SweepWalk
{
    /// <summary>The capture every test here reads.</summary>
    public const string Fixture = "session-2026-08-sweep.rec";

    public SweepWalk()
    {
        using var replay = ReplayMemoryReader.Load(File.OpenRead(ComponentSweepTests.Fixture(Fixture)));
        OffsetSchema schema = RealSessionTests.Schema();
        var sweep = new ComponentSweep(replay, schema);
        ulong gameStates = replay.ResolvedStatics["GameStates"];

        for (uint frame = 0; frame < replay.FrameCount; frame++)
        {
            replay.Seek(frame);
            if (sweep.SampleFrame(gameStates, (int)frame) is { } got)
            {
                Frames.Add(got);
                Seconds.Add(replay.FrameTimes[(int)frame] / 1000.0);
            }
        }
    }

    /// <summary>Every frame the sweep could sample, in order.</summary>
    public List<SweepFrame> Frames { get; } = [];

    /// <summary>Each sampled frame's timestamp in seconds, aligned with <see cref="Frames"/>.</summary>
    public List<double> Seconds { get; } = [];

    /// <summary>Each entity's readings of one component in frame order, with the frame's timestamp.</summary>
    /// <param name="lastSecond">The timestamp of the last sampled frame - what "still on screen at the end" is measured against.</param>
    public Dictionary<uint, List<(double Seconds, byte[] Bytes)>> Tracks(string component, out double lastSecond)
    {
        var tracks = new Dictionary<uint, List<(double, byte[])>>();
        lastSecond = Seconds.Count > 0 ? Seconds[^1] : 0d;

        for (var at = 0; at < Frames.Count; at++)
        {
            double seconds = Seconds[at];
            foreach (ComponentObservation o in Frames[at].Seen)
            {
                if (!string.Equals(o.Component, component, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!tracks.TryGetValue(o.EntityId, out List<(double, byte[])>? track))
                {
                    tracks[o.EntityId] = track = [];
                }

                track.Add((seconds, o.Bytes));
            }
        }

        return tracks;
    }
}

/// <summary>
/// The two ground-effect captures walked once each, for every test in <see cref="GroundTypeReadingTests"/>.
/// </summary>
/// <remarks>
/// THE SAME REASON AS <see cref="SweepWalk"/>: seven tests walked the two recordings seven times
/// between them, 136 seconds for what is two walks' worth of reading. Each recording is walked the
/// first time a test asks for it and never again.
/// </remarks>
public sealed class GroundTypeWalk
{
    private readonly Lazy<Walk> _hideout = new(() => Of("session-2026-08-sweep.rec"));
    private readonly Lazy<Walk> _map = new(() => Of("session-2026-09-groundtypes.rec"));

    /// <summary>What one capture showed.</summary>
    /// <param name="PerEntity">The type rows each ground effect showed, in order - see GroundTypeReadingTests for why the key is address as well as id.</param>
    /// <param name="Readings">Every ground-effect reading in the capture.</param>
    /// <param name="WithType">Those that carried a type row.</param>
    public sealed record Walk(Dictionary<(uint Id, ulong Address), List<int>> PerEntity, int Readings, int WithType);

    /// <summary>The walk of a capture, by its fixture name.</summary>
    public Walk For(string fixture) => fixture switch
    {
        "session-2026-08-sweep.rec" => _hideout.Value,
        "session-2026-09-groundtypes.rec" => _map.Value,
        _ => throw new ArgumentException($"no walk is kept for {fixture}", nameof(fixture)),
    };

    private static Walk Of(string fixture)
    {
        using var replay = ReplayMemoryReader.Load(File.OpenRead(GroundTypeReadingTests.Fixture(fixture)));
        var world = new WorldReader(replay, RealSessionTests.Schema());
        ulong gameStates = replay.ResolvedStatics["GameStates"];

        var perEntity = new Dictionary<(uint, ulong), List<int>>();
        var readings = 0;
        var withType = 0;

        for (uint frame = 0; frame < replay.FrameCount; frame++)
        {
            replay.Seek(frame);
            foreach (WorldEntity entity in world.Read(gameStates).Entities
                         .Where(e => e.IsGroundEffect && !e.IsRemembered))
            {
                readings++;
                if (entity.GroundType is not { } row)
                {
                    continue;
                }

                withType++;
                if (!perEntity.TryGetValue((entity.Id, entity.Address), out List<int>? seen))
                {
                    perEntity[(entity.Id, entity.Address)] = seen = [];
                }

                seen.Add(row);
            }
        }

        return new Walk(perEntity, readings, withType);
    }
}
