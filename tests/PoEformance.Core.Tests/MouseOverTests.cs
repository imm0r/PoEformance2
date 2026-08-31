using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// The three reads that ask the game what the cursor is on.
/// </summary>
/// <remarks>
/// The chain itself is confirmed in HoverHuntTests, against the capture that settled it. What
/// is left here is the part a production reader has to get right REGARDLESS of the chain: it
/// runs on every frame of every session, including loading screens, replays of recordings made
/// before anything read the slot, and whatever the memory looks like a millisecond after an
/// area change. All of those must come back 0 rather than an address, because a caller cannot
/// tell a garbage entity pointer from a real one and would happily draw a highlight on it.
/// </remarks>
public class MouseOverTests
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

    [Fact]
    public void WithoutAnInGameState_ItReadsNothing()
    {
        var reader = new MouseOverReader(new FakeMemoryReader(), RealSessionTests.Schema());
        Assert.Equal(0ul, reader.Read(0));
    }

    [Fact]
    public void OnASessionThatNeverReadTheChain_ItIsZeroThroughoutRatherThanGarbage()
    {
        // Every recording made before the confirmation is like this, and they are replayed by
        // most of this suite: a reader that returned an address from unserved memory would put
        // a phantom hover into every one of them.
        using var replay = ReplayMemoryReader.Load(File.OpenRead(Fixture("session-2026-08-map.rec")));
        OffsetSchema schema = RealSessionTests.Schema();
        var hovered = new MouseOverReader(replay, schema);
        ulong gameStates = replay.ResolvedStatics["GameStates"];

        int checked_ = 0;
        for (uint frame = 0; frame < replay.FrameCount; frame += 10)
        {
            replay.Seek(frame);
            GameChainAddresses chain = GameChain.Resolve(replay, schema, gameStates);
            if (chain.InGameState == 0)
            {
                continue;
            }

            checked_++;
            Assert.Equal(0ul, hovered.Read(chain.InGameState));
        }

        Assert.True(checked_ > 10, $"only {checked_} frames had an InGameState to try");
    }

    [Fact]
    public void TheSnapshotCarriesTheHoveredAddress()
    {
        // The wiring, end to end: a snapshot read the way the overlay reads it comes back with
        // Hovered filled on the frames the chain says something was hovered. Pinned because a
        // reader that works and a snapshot field that is never assigned look identical from
        // anywhere downstream.
        using var replay = ReplayMemoryReader.Load(File.OpenRead(Fixture("session-2026-08-hoverhunt.rec")));
        OffsetSchema schema = RealSessionTests.Schema();
        var world = new WorldReader(replay, schema);
        var hovered = new MouseOverReader(replay, schema);
        ulong gameStates = replay.ResolvedStatics["GameStates"];

        int withHover = 0, agreed = 0;
        for (uint frame = 0; frame < replay.FrameCount; frame++)
        {
            replay.Seek(frame);
            WorldSnapshot snapshot = world.Read(gameStates);
            if (!snapshot.InGame)
            {
                continue;
            }

            GameChainAddresses chain = GameChain.Resolve(replay, schema, gameStates);
            if (snapshot.Hovered == hovered.Read(chain.InGameState))
            {
                agreed++;
            }

            if (snapshot.Hovered != 0)
            {
                withHover++;
            }
        }

        Assert.True(withHover > 50, $"only {withHover} frames carried a hover");
        Assert.True(agreed > 900, $"the snapshot disagreed with the reader on {agreed} frames");

        // WHY THIS DOES NOT ALSO JOIN snapshot.Entities, which is the obvious next assertion
        // and was written first: it fails, 8 of 143, and for a reason that says nothing about
        // the hover. A replay only serves reads that HAPPENED, and this file was captured by
        // --hoverhunt, which reads entity headers and the Monster component and nothing else -
        // so a WorldReader replaying it cannot build most of the list it would normally build.
        // The join against the game's own AwakeEntities pointers, which this capture does
        // contain, is in HoverHuntTests and is 143 of 143.
    }
}
