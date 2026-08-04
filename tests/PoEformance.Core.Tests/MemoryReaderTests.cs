using PoEformance.Core.Memory;

namespace PoEformance.Core.Tests;

public class MemoryReaderTests
{
    [Fact]
    public void TypedRead_RoundTrips()
    {
        var reader = new FakeMemoryReader()
            .Place(0x1000, 0x2A000000DEADBEEFUL)
            .Place<float>(0x2000, 3.5f);

        Assert.Equal(0x2A000000DEADBEEFUL, reader.Read<ulong>(0x1000));
        Assert.Equal(0xDEADBEEF, reader.Read<uint>(0x1000));
        Assert.Equal(3.5f, reader.Read<float>(0x2000));
    }

    [Fact]
    public void FailedRead_ReturnsFalseAndDefault()
    {
        var reader = new FakeMemoryReader();
        Assert.False(reader.TryRead(0x5000, out int value));
        Assert.Equal(0, value);
    }

    [Fact]
    public void PartiallyCoveredRead_Fails_LikeAnUnmappedPage()
    {
        var reader = new FakeMemoryReader().Place(0x1000, 1, 2, 3, 4);
        Span<byte> eight = stackalloc byte[8];
        Assert.False(reader.TryRead(0x1000, eight)); // bytes 5..8 missing
        Assert.True(reader.TryRead(0x1000, eight[..4]));
    }

    [Fact]
    public void ReadPointer_FiltersImplausibleValues()
    {
        var reader = new FakeMemoryReader()
            .Place(0x1000, 0x7FFA12345678UL)   // plausible user-space
            .Place(0x2000, 0xFFFF800000000000UL) // kernel-space garbage
            .Place(0x3000, 0x42UL);              // tiny int, not a pointer

        Assert.Equal(0x7FFA12345678UL, reader.ReadPointer(0x1000));
        Assert.Equal(0UL, reader.ReadPointer(0x2000));
        Assert.Equal(0UL, reader.ReadPointer(0x3000));
    }

    [Fact]
    public void ReadChain_FollowsHopsAndStopsOnBrokenLink()
    {
        var reader = new FakeMemoryReader()
            .Place(0x10_0000 + 0x290, 0x20_0000UL)  // base+0x290 -> A
            .Place(0x20_0000 + 0x598, 0x30_0000UL); // A+0x598 -> B

        Assert.Equal(0x30_0000UL, reader.ReadChain(0x10_0000, 0x290, 0x598));
        Assert.Equal(0UL, reader.ReadChain(0x10_0000, 0x290, 0x999)); // unreadable hop
    }

    [Fact]
    public void ReadUtf16_StopsAtNulAndSurvivesShortRegions()
    {
        var reader = new FakeMemoryReader().PlaceUtf16(0x1000, "Metadata/Monsters/Skeleton");
        Assert.Equal("Metadata/Monsters/Skeleton", reader.ReadUtf16(0x1000));

        // String right at the end of readable memory: the 512-char request would fail
        // as one big read; the chunked reader must still return the text.
        var edge = new FakeMemoryReader().PlaceUtf16(0x2000 - 20, "abc");
        Assert.Equal("abc", edge.ReadUtf16(0x2000 - 20, maxChars: 512));
    }

    [Fact]
    public void ReadStdWString_Inline_And_OutOfLine()
    {
        // Short string: SSO inline.
        var sso = new FakeMemoryReader().PlaceStdWString(0x1000, "Std", 0);
        Assert.Equal("Std", sso.ReadStdWString(0x1000));

        // Long string: header points at out-of-line data.
        var far = new FakeMemoryReader().PlaceStdWString(0x1000, "Metadata/Characters/Int/IntFour", 0x90000);
        Assert.Equal("Metadata/Characters/Int/IntFour", far.ReadStdWString(0x1000));
    }

    [Fact]
    public void ReadStdWString_RejectsGarbageHeader()
    {
        var reader = new FakeMemoryReader()
            .Place(0x1000, new byte[32]) // all zero: size 0 -> empty
            .Place<long>(0x2000 + 16, -5) // negative size
            .Place<long>(0x2000 + 24, 100);

        Assert.Equal(string.Empty, reader.ReadStdWString(0x1000));
        Assert.Equal(string.Empty, reader.ReadStdWString(0x2000));
    }
}
