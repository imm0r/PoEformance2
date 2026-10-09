using System.Text;
using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using Xunit;

namespace PoEformance.Core.Tests;

/// <summary>
/// Searching the whole of the game's memory for runs of bytes - a name as text, a pointer to an address - and what lies round each place.
/// </summary>
public class PatternHuntTests
{
    private const ulong Base = 0x3000_0000;

    /// <summary>A name is found one byte a character and two, a near miss is not, and what lies round it is read back.</summary>
    [Fact]
    public void ANAMEIsFoundAsBothKindsOfTextWithWhatLiesRoundIt()
    {
        var bytes = new byte[0x1000];
        Encoding.ASCII.GetBytes("Texturing_Init\0Texturing\0Texturing_Calc\0Texturing_Final\0").CopyTo(bytes, 0x200);
        Encoding.Unicode.GetBytes("Texturing_Calc").CopyTo(bytes, 0x800);
        Encoding.ASCII.GetBytes("Texturing_Cal").CopyTo(bytes, 0xC00);
        (FakeMemoryReader memory, Space space) = Memory(bytes);

        IReadOnlyList<PatternNeedle> needles = PatternNeedle.Text("Texturing_Calc");
        PatternHuntResult result = PatternHunt.Run(memory, space, needles);

        Assert.Equal([(Base + 0x200 + 25, 0), (Base + 0x800, 1)], result.Sightings.Select(one => (one.At, one.Needle)));
        Assert.Equal(bytes.Length, result.BytesScanned);
        PatternSighting ascii = result.Sightings[0];
        Assert.Contains("Texturing_Init.Texturing.Texturing_Calc.Texturing_Final", PatternHunt.Readable(ascii.Around), StringComparison.Ordinal);

        string report = PatternHunt.Report(result, needles);
        Assert.Contains("0x30000219: Texturing_Calc", report, StringComparison.Ordinal);
        Assert.Contains("0x30000800: Texturing_Calc as UTF-16", report, StringComparison.Ordinal);
        Assert.Contains("T.e.x.t.u.r.i.n.", report, StringComparison.Ordinal);
    }

    /// <summary>A needle that starts in one megabyte chunk and ends in the next is matched whole, once.</summary>
    [Fact]
    public void ANEEDLEAcrossAChunkBoundaryIsFoundOnce()
    {
        var bytes = new byte[(2 * HeapScan.ChunkBytes) + 0x100];
        int across = HeapScan.ChunkBytes - 5;
        Encoding.ASCII.GetBytes("UVSetup_Final").CopyTo(bytes, across);
        (FakeMemoryReader memory, Space space) = Memory(bytes);

        PatternHuntResult result = PatternHunt.Run(memory, space, [new PatternNeedle("stage", Encoding.ASCII.GetBytes("UVSetup_Final"))]);

        PatternSighting one = Assert.Single(result.Sightings);
        Assert.Equal(Base + (ulong)across, one.At);
    }

    /// <summary>Pointers to an address are its eight bytes, lowest first - a table of names is a row of them.</summary>
    [Fact]
    public void POINTERSToAnAddressAreFound()
    {
        var bytes = new byte[0x400];
        BitConverter.GetBytes(Base + 0x300).CopyTo(bytes, 0x40);
        BitConverter.GetBytes(Base + 0x318).CopyTo(bytes, 0x48);
        BitConverter.GetBytes(Base + 0x300).CopyTo(bytes, 0x100);
        (FakeMemoryReader memory, Space space) = Memory(bytes);

        PatternHuntResult result = PatternHunt.Run(memory, space, [PatternNeedle.Pointer(Base + 0x300)]);

        Assert.Equal([Base + 0x40, Base + 0x100], result.Sightings.Select(one => one.At));
        Assert.Equal("a pointer to 0x30000300", PatternNeedle.Pointer(Base + 0x300).Name);
    }

    /// <summary>A needle found more often than the cap keeps the first places and says it was capped.</summary>
    [Fact]
    public void ANEEDLEFoundTooOftenIsCapped()
    {
        var bytes = new byte[0x1000];
        for (var at = 0; at < 100; at++)
        {
            bytes[at * 16] = 0xAB;
            bytes[(at * 16) + 1] = 0xCD;
        }

        (FakeMemoryReader memory, Space space) = Memory(bytes);
        PatternHuntResult result = PatternHunt.Run(memory, space, [new PatternNeedle("ab cd", [0xAB, 0xCD])]);

        Assert.Equal(PatternHunt.MostPerNeedle, result.Sightings.Count);
        Assert.Equal([0], result.Capped);
    }

    private static (FakeMemoryReader Memory, Space Space) Memory(byte[] bytes)
        => (new FakeMemoryReader().Place(Base, bytes), new Space(new MemoryRegion(Base, (ulong)bytes.Length)));

    private sealed class Space(params MemoryRegion[] regions) : IMemoryRegions
    {
        public IEnumerable<MemoryRegion> Regions() => regions;
    }
}
