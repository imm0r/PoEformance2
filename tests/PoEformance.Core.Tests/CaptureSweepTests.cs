using PoEformance.Core.Memory;
using PoEformance.Features;
using PoEformance.Game.Diagnostics;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// The raw half of the capture's memory pass: roots whole, one hop along their pointers, the components around the player - and the recording kept open and wide enough to hold it.
/// </summary>
public class CaptureSweepTests
{
    private const ulong Root = 0x10_0000;
    private const ulong Other = 0x30_0000;
    private const ulong Target = 0x20_0000;
    private const ulong Unmapped = 0x50_0000;

    /// <summary>
    /// A root is read whole and every pointer in it followed once: a mapped target named by its slot, an unmapped one left out, a pointer back at a root not read again - and a root whose second page is unmapped keeps its first.
    /// </summary>
    [Fact]
    public void ROOTSAreReadWholeAndTheirPointersFollowedOnce()
    {
        var root = new byte[CaptureSweep.RootBytes];
        BitConverter.TryWriteBytes(root.AsSpan(0x18), Target);
        BitConverter.TryWriteBytes(root.AsSpan(0x20), Unmapped);
        BitConverter.TryWriteBytes(root.AsSpan(0x28), Other);
        BitConverter.TryWriteBytes(root.AsSpan(0x30), Target);
        var fake = new FakeMemoryReader()
            .Place(Root, root)
            .Place(Other, new byte[0x1000])
            .Place(Target, new byte[CaptureSweep.NeighbourBytes]);

        var sweep = new CaptureSweep(fake, RealSessionTests.LiveSchema());
        var index = new List<string>();
        sweep.Roots([("AreaInstance", Root), ("WorldData", Other), ("Again", Root), ("Nothing", 0)], index);

        Assert.Equal(
            [
                "root AreaInstance at 0x100000: 8192 of 8192 bytes",
                "root WorldData at 0x300000: 4096 of 8192 bytes",
                "  AreaInstance+0x18 -> 0x200000: 2048 bytes",
            ],
            index);
        Assert.Equal(8192 + 4096 + 2048, sweep.BytesRead);
    }

    /// <summary>
    /// Against a real session: the entities around the player, nearest first - the player at nought - each with every component it has, by name.
    /// </summary>
    [Fact]
    public void ENTITIESAroundThePlayerAreListedWithEveryComponent()
    {
        string fixture = Path.Combine(
            Directory.GetParent(RealSessionTests.SceneFixturePath)!.FullName, "session-2026-08-rotation-clickmove.rec");
        using var replay = ReplayMemoryReader.Load(File.OpenRead(fixture));
        var world = new WorldReader(replay, RealSessionTests.Schema());
        replay.Seek(600);
        WorldSnapshot snapshot = world.Read(replay.ResolvedStatics["GameStates"]);
        WorldEntity player = Assert.IsType<WorldEntity>(snapshot.Player);

        var index = new List<string>();
        new CaptureSweep(replay, RealSessionTests.Schema()).Entities(snapshot.Entities, player.WorldX, player.WorldY, index);

        Assert.StartsWith("entities: ", index[0], StringComparison.Ordinal);
        Assert.StartsWith($"entity 0x{player.Address:X} ", index[1], StringComparison.Ordinal);
        Assert.Contains(player.Path, index[1], StringComparison.Ordinal);
        Assert.Contains(index, line => line.StartsWith("  Render 0x", StringComparison.Ordinal));
        Assert.Contains(index, line => line.StartsWith("  Positioned 0x", StringComparison.Ordinal));
    }

    /// <summary>
    /// The whole pass against a real session: a fresh world read finds the area and its entities, the loaded files say why they were not read, and the raw sweep names the chain - in that order.
    /// </summary>
    [Fact]
    public void THEPASSReadsTheWorldFreshThenTheFilesThenTheRawBytes()
    {
        string fixture = Path.Combine(
            Directory.GetParent(RealSessionTests.SceneFixturePath)!.FullName, "session-2026-08-rotation-clickmove.rec");
        using var replay = ReplayMemoryReader.Load(File.OpenRead(fixture));
        replay.Seek(600);

        string said = CaptureMemory.Pass(
            replay, RealSessionTests.Schema(), replay.ResolvedStatics["GameStates"], default, null, null, null, default, 0, 0);

        int world = said.IndexOf("=== a fresh world read, every switch on: ", StringComparison.Ordinal);
        int files = said.IndexOf("=== the area's loaded files: the file root or the area counter did not resolve", StringComparison.Ordinal);
        int raw = said.IndexOf("=== raw: ", StringComparison.Ordinal);
        Assert.True(world >= 0 && files > world && raw > files, said);
        Assert.Contains("in game yes", said, StringComparison.Ordinal);
        Assert.Matches(@"\n[1-9]\d* entities read", said);
        Assert.Contains("chain: state InGame", said, StringComparison.Ordinal);
        Assert.Contains("root AreaInstance at 0x", said, StringComparison.Ordinal);
        Assert.Contains("bytes read in all", said, StringComparison.Ordinal);
    }

    /// <summary>A recording waiting on something runs past its frames until that is done - and no further than its cap.</summary>
    [Fact]
    public void ARECORDINGWaitsForWhatRunsInsideItUpToItsCap()
    {
        using var tap = new TapMemoryReader(new FakeMemoryReader());
        var inside = new TaskCompletionSource();
        _ = tap.Start(new MemoryStream(), [], new TapRecording(2) { Until = inside.Task, MostFrames = 10 });
        for (var tick = 0; tick < 5; tick++)
        {
            tap.MarkFrame();
        }

        Assert.True(tap.Recording);
        inside.SetResult();
        tap.MarkFrame();
        Assert.False(tap.Recording);

        _ = tap.Start(new MemoryStream(), [], new TapRecording(2) { Until = new TaskCompletionSource().Task, MostFrames = 10 });
        for (var tick = 0; tick < 10; tick++)
        {
            tap.MarkFrame();
        }

        Assert.True(tap.Recording);
        tap.MarkFrame();
        Assert.False(tap.Recording);
    }

    /// <summary>A capture's recording keeps a read as large as the terrain's grid, which a session recording leaves out.</summary>
    [Fact]
    public async Task ACAPTURESRecordingKeepsALargeRead()
    {
        const int Large = 512 * 1024;
        var live = new FakeMemoryReader().Place(Root, new byte[Large]);
        using var tap = new TapMemoryReader(live);
        var file = new MemoryStream();
        Task<long> done = tap.Start(file, [], new TapRecording(1) { MaxReadBytes = RecordingFormat.MaxReadLength })!;
        Assert.True(tap.TryRead(Root, new byte[Large]));
        tap.MarkFrame();
        tap.MarkFrame();
        await done;

        using var replay = ReplayMemoryReader.Load(new MemoryStream(file.ToArray()));
        Assert.True(replay.TryRead(Root, new byte[Large]));
    }
}
