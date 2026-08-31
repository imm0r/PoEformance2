using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Diagnostics;

namespace PoEformance.Core.Tests;

/// <summary>
/// The sweep for the four components neither reference has a layout for.
/// </summary>
/// <remarks>
/// Same standing rule as the hover hunt before its capture: no build has ever read a byte of
/// LimitedLifespan, DiesAfterTime, GroundEffect or Beam, so not one committed recording contains
/// them, and there is nothing here to verify offsets against. What IS testable before a capture
/// exists is the property that decides whether the capture is worth making - that the sweep
/// reports being unable to answer rather than reading absent memory as a result - plus the two
/// things the recordings genuinely do say without a byte of the components: the pool cells, and
/// how long the carriers live.
///
/// The lifetimes are not decoration. A countdown inside one of these has to reach zero when the
/// game stops listing the entity, and that check is only possible if entities both appear and
/// vanish inside the capture. Pinning the measured lifetimes is pinning what the capture has to
/// be long enough to contain.
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

    [Fact]
    public void OnEveryCommittedRecording_ItFindsNoBytesAndSaysSo()
    {
        // The four are separate allocations nobody has read. A sweep that came back with
        // observations here would be reading something other than what it thinks it is.
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
        ComponentSweep.Report(Replay("session-2026-08-effects.rec"), text);
        string report = text.ToString();

        Assert.Contains("NOT ONE of the swept components was readable", report, StringComparison.Ordinal);

        // The sentences a report built on absent data must never produce.
        Assert.DoesNotContain("countdown candidate", report, StringComparison.Ordinal);
        Assert.DoesNotContain("a world point", report, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptySweepIsReportedAsNoFrames()
    {
        var text = new StringWriter();
        ComponentSweep.Report([], text);
        Assert.Contains("no frames", text.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheSchemaCarriesACellForEveryComponentTheSweepReads()
    {
        // Without a cell the sweep falls back to a default, and a fallback that goes unnoticed
        // is how a component gets swept at the wrong width for a year. The four are the point of
        // the switch, so each must be described.
        OffsetSchema schema = RealSessionTests.Schema();
        foreach (string name in ComponentSweep.Undecoded)
        {
            StructDef def = schema.Structs[name];
            Assert.True(def.Constants.ContainsKey("PoolCell"), $"{name} has no measured PoolCell");
            Assert.True(def.Constants["PoolCell"] <= ComponentSweep.DefaultCell,
                $"{name}'s cell exceeds the sweep's buffer");

            // And no fields, which is the honest state and worth pinning: if somebody adds one
            // from a reference, this fails and sends them to read the note above the struct -
            // neither reference has a layout for any of these.
            Assert.Empty(def.Fields);
        }
    }

    [Fact]
    public void TheCarriersLiveLongEnoughForTheCaptureToBeWorthMaking()
    {
        // The measurement that says a countdown is testable at all, done against the entity list
        // rather than against the components: entities have to be seen appearing AND vanishing,
        // or there is no expiry to check a falling value against.
        //
        // GroundEffect is the one this matters most for, and the reason it is the easiest of the
        // four to record: its carriers stand still for tens of seconds.
        using var replay = ReplayMemoryReader.Load(File.OpenRead(Fixture("session-2026-08-deployed.rec")));
        OffsetSchema schema = RealSessionTests.Schema();
        var entities = new PoEformance.Game.Entities.EntityReader(replay, schema);
        var map = new PoEformance.Game.Entities.EntityMapReader(replay, schema);
        ulong gameStates = replay.ResolvedStatics["GameStates"];
        int awake = schema.Structs["AreaInstance"].OffsetOf("AwakeEntities");

        var first = new Dictionary<uint, int>();
        var last = new Dictionary<uint, int>();
        int worldFrames = 0;

        for (uint frame = 0; frame < replay.FrameCount; frame++)
        {
            replay.Seek(frame);
            GameChainAddresses chain = GameChain.Resolve(replay, schema, gameStates);
            if (chain.AreaInstance == 0)
            {
                continue;
            }

            worldFrames++;
            foreach ((uint id, ulong address) in
                map.ReadEntityPointers(chain.AreaInstance + (ulong)awake, 4096, true))
            {
                if (entities.ReadIdentity(address) is not { } identity || identity.Path.Length == 0)
                {
                    continue;
                }

                if (entities.ReadComponents(address, identity.Details).ContainsKey("GroundEffect"))
                {
                    first.TryAdd(id, (int)frame);
                    last[id] = (int)frame;
                }
            }
        }

        Assert.True(worldFrames > 100, $"only {worldFrames} world frames");
        Assert.NotEmpty(first);

        // At least one carrier both arrived and left inside the recording - the shape a capture
        // needs. Recorded as a fact about this fixture so a future change that stops resolving
        // these entities is caught, rather than as a claim about ground effects in general.
        Assert.Contains(first.Keys, id => first[id] > 0 && last[id] < worldFrames - 1);
    }
}
