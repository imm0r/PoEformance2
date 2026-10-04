using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Features;
using PoEformance.Game.Components;
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
/// between them, 136 seconds for what is two walks' worth of reading. Each recording is walked
/// once, and the two are walked SIDE BY SIDE: a class runs serially in xUnit, so two walks of
/// twenty-odd seconds one after the other were this class's whole length, and that length was
/// the suite's floor once the sweep class had been dealt with.
/// </remarks>
public sealed class GroundTypeWalk
{
    private readonly Task<Walk> _hideout = Task.Run(() => Of("session-2026-08-sweep.rec"));
    private readonly Task<Walk> _map = Task.Run(() => Of("session-2026-09-groundtypes.rec"));

    /// <summary>What one capture showed.</summary>
    /// <param name="PerEntity">The type rows each ground effect showed, in order - see GroundTypeReadingTests for why the key is address as well as id.</param>
    /// <param name="Readings">Every ground-effect reading in the capture.</param>
    /// <param name="WithType">Those that carried a type row.</param>
    public sealed record Walk(Dictionary<(uint Id, ulong Address), List<int>> PerEntity, int Readings, int WithType);

    /// <summary>The walk of a capture, by its fixture name.</summary>
    public Walk For(string fixture) => fixture switch
    {
        "session-2026-08-sweep.rec" => _hideout.GetAwaiter().GetResult(),
        "session-2026-09-groundtypes.rec" => _map.GetAwaiter().GetResult(),
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

/// <summary>
/// The sweep capture read through <see cref="WorldReader"/> once, every fifth frame, for every test in
/// <see cref="HazardReadingTests"/>.
/// </summary>
/// <remarks>
/// SIX WALKS BECAME ONE. Each test rebuilt the same list of snapshots for itself - the reader, the
/// schema, the seek per frame - at some seven seconds each. The snapshots are plain objects once
/// read, so one list serves every test; <see cref="SweepWalk"/> does the same for the raw sweep.
/// </remarks>
public sealed class HazardWalk
{
    /// <summary>Every fifth frame, as the tests always read it.</summary>
    public const uint Step = 5;

    public HazardWalk()
    {
        using var replay = ReplayMemoryReader.Load(File.OpenRead(HazardReadingTests.Fixture(SweepWalk.Fixture)));
        OffsetSchema schema = RealSessionTests.Schema();
        var world = new WorldReader(replay, schema);
        ulong gameStates = replay.ResolvedStatics["GameStates"];

        for (uint frame = 0; frame < replay.FrameCount; frame += Step)
        {
            replay.Seek(frame);
            WorldSnapshot snapshot = world.Read(gameStates);
            if (snapshot.InGame)
            {
                Snapshots.Add((replay.FrameTimes[(int)frame] / 1000.0, snapshot));
            }
        }
    }

    /// <summary>Every in-game snapshot of the capture, with the frame's timestamp in seconds.</summary>
    public List<(double Seconds, WorldSnapshot Snapshot)> Snapshots { get; } = [];
}

/// <summary>
/// The fight capture read once, every frame and with actions, for every test in
/// <see cref="EvasionAgainstFightTests"/>.
/// </summary>
/// <remarks>
/// THE READ IS THE COST AND THE PLANNER IS NOT. Four of the five tests replayed the whole session
/// through WorldReader with actions on, each under its own settings - and only the settings
/// differed, which the planner reads and the reader never sees. So the snapshots are read once
/// here and each test runs its planner over them: the same ticks, from the same frames, at the
/// price of one walk instead of four.
/// </remarks>
public sealed class FightWalk
{
    /// <summary>The capture: 130 seconds in which 54 monsters attacked the player.</summary>
    public const string Fixture = "session-2026-08-monsters.rec";

    public FightWalk()
    {
        using var replay = ReplayMemoryReader.Load(File.OpenRead(EvasionAgainstFightTests.FixturePath));
        OffsetSchema schema = RealSessionTests.Schema();

        // The reader must be asked for actions, exactly as the app asks when the feature is on.
        var world = new WorldReader(replay, schema) { ReadActions = true };
        ulong gameStates = replay.ResolvedStatics["GameStates"];

        for (uint frame = 0; frame < replay.FrameCount; frame++)
        {
            replay.Seek(frame);
            Frames.Add((replay.FrameTimes[(int)frame], world.Read(gameStates)));
        }
    }

    /// <summary>Every frame's snapshot, with the recording's own clock in milliseconds.</summary>
    public List<(uint Time, WorldSnapshot Snapshot)> Frames { get; } = [];

    /// <summary>Runs a planner over the whole session, as the app would have that evening.</summary>
    public List<EvasionTick> Replay(EvasionSettings settings)
    {
        var planner = new EvasionPlanner(settings);
        var ticks = new List<EvasionTick>(Frames.Count);
        foreach ((uint time, WorldSnapshot snapshot) in Frames)
        {
            // The recording's own clock, so the cooldown behaves as it did live rather than
            // being handed a fresh millisecond per frame.
            ticks.Add(planner.Evaluate(snapshot, AnimationNames.Empty, true, time));
        }

        return ticks;
    }
}
