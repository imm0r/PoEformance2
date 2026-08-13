using PoEformance.Core.Memory;

namespace PoEformance.Core.Tests;

/// <summary>
/// Recording a session must not take capabilities away from it.
/// </summary>
/// <remarks>
/// This exists because of a silent failure that cost a real game session. The heap scan asks
/// whether its reader can enumerate the target's memory regions; the live reader can, but the
/// recorder wraps it, and the wrapper did not forward the question. So <c>--record</c> next to
/// <c>--heapscan</c> answered "no regions" for a process that had thousands, the scan skipped
/// itself, and the report came back with every other section present - which reads as "the
/// scan found nothing" rather than "the scan never ran".
///
/// The combination that broke is the one worth having: a scan whose hits cannot be replayed
/// afterwards is a set of addresses with no way to check them.
/// </remarks>
public class RecordingRegionsTests
{
    /// <summary>A reader that knows its regions, like the live one does.</summary>
    private sealed class MappedReader : IMemoryReader, IMemoryRegions
    {
        public bool IsAttached => true;

        public int ProcessId => 1;

        public ulong ModuleBase => 0x1400000000;

        public uint ModuleSize => 0;

        public bool TryRead(ulong address, Span<byte> destination)
        {
            destination.Clear();
            return true;
        }

        public IEnumerable<MemoryRegion> Regions() =>
            [new MemoryRegion(0x10000, 0x1000), new MemoryRegion(0x20000, 0x2000)];

        /// <summary>Nothing to release; IMemoryReader is IDisposable for the live one's handle.</summary>
        public void Dispose()
        {
        }
    }

    [Fact]
    public void Regions_AreForwardedThroughTheRecorder()
    {
        using var file = new MemoryStream();
        using var recorder = new RecordingMemoryReader(new MappedReader(), file);

        Assert.True(recorder is IMemoryRegions, "a recorded session must still be scannable");

        List<MemoryRegion> regions = [.. ((IMemoryRegions)recorder).Regions()];

        Assert.Equal(2, regions.Count);
        Assert.Equal(0x10000UL, regions[0].Address);
        Assert.Equal(0x2000UL, regions[1].Size);
    }

    /// <summary>
    /// Wrapping a reader that has no regions to give must be empty rather than a throw:
    /// a replay has no process to enumerate, and recording one is still legal.
    /// </summary>
    [Fact]
    public void Regions_AreEmptyWhenTheInnerReaderHasNone()
    {
        using var file = new MemoryStream();
        using var recorder = new RecordingMemoryReader(new FakeMemoryReader(), file);

        Assert.Empty(((IMemoryRegions)recorder).Regions());
    }
}
