using PoEformance.Core.Diagnostics;

namespace PoEformance.Core.Tests;

/// <summary>
/// Finding the text in a window of memory.
/// </summary>
/// <remarks>
/// Worth being careful about in both directions. Missing a string leaves a name reading as a
/// pointer and two meaningless numbers, which is most of what this game's structures are made
/// of. But a "string" label on ordinary numbers is worse: it sends someone looking for a name
/// that was never there, and unlike a missing one it does not announce itself.
/// </remarks>
public class TextProbeTests
{
    private const ulong Elsewhere = 0x3000_0000_0000;

    /// <summary>Builds a std::wstring header the way the game lays one out.</summary>
    private static byte[] Header(
        string text, bool inline, ulong data = Elsewhere, long? sizeOverride = null, long? capacityOverride = null)
    {
        var bytes = new byte[TextProbe.HeaderSize];

        if (inline)
        {
            System.Text.Encoding.Unicode.GetBytes(text, bytes.AsSpan(0, 16));
            BitConverter.GetBytes((long)(sizeOverride ?? text.Length)).CopyTo(bytes, 16);
            BitConverter.GetBytes(7L).CopyTo(bytes, 24);          // capacity < 8 means inline
            return bytes;
        }

        BitConverter.GetBytes(data).CopyTo(bytes, 0);
        BitConverter.GetBytes((long)(sizeOverride ?? text.Length)).CopyTo(bytes, 16);
        BitConverter.GetBytes(capacityOverride ?? Math.Max(text.Length, 8)).CopyTo(bytes, 24);
        return bytes;
    }

    [Fact]
    public void AShortStringIsReadStraightOutOfTheRow()
    {
        // Costs no read at all: the game keeps a short string in the header itself.
        TextCandidate found = TextProbe.At(Header("Chest", inline: true), 0);

        Assert.Equal(TextShape.Inline, found.Shape);
        Assert.Equal("Chest", found.Text);
    }

    [Fact]
    public void ALongStringSaysWhereItsCharactersAre()
    {
        TextCandidate found = TextProbe.At(Header("Metadata/Monsters/MudGolem", inline: false), 0);

        Assert.Equal(TextShape.Heap, found.Shape);
        Assert.Equal(Elsewhere, found.Address);
        Assert.Equal(26, found.Length);
    }

    [Fact]
    public void TwoOrdinaryNumbersAreNotAString()
    {
        // The mistake that matters. Every row is offered to this, so anything that says yes
        // too easily puts an imaginary name somewhere in every structure.
        var window = new byte[TextProbe.HeaderSize];
        BitConverter.GetBytes(0x0000_0004_0000_0213UL).CopyTo(window, 0);
        BitConverter.GetBytes(531L).CopyTo(window, 16);
        BitConverter.GetBytes(4L).CopyTo(window, 24);          // capacity below size - impossible

        Assert.Equal(TextShape.None, TextProbe.At(window, 0).Shape);
    }

    [Fact]
    public void ALengthNoStringWouldHaveIsRefused()
    {
        // A number that happens to sit where a length would. The reader's own limit is a
        // hundred thousand, which is right when a string is already expected and far too
        // generous when every row is being asked.
        // The capacity has to keep up, or the refusal comes from the consistency check
        // instead and this proves nothing about the limit it names.
        Assert.Equal(
            TextShape.None,
            TextProbe.At(Header("x", inline: false, sizeOverride: 50_000, capacityOverride: 50_000), 0).Shape);
    }

    [Fact]
    public void AnEmptyStringIsNotWorthShowing()
        => Assert.Equal(TextShape.None, TextProbe.At(Header(string.Empty, inline: true, sizeOverride: 0), 0).Shape);

    [Fact]
    public void CharactersThatAreNotTextDoNotBecomeAnInlineString()
    {
        var window = new byte[TextProbe.HeaderSize];
        BitConverter.GetBytes(0x0001_0002_0003_0004UL).CopyTo(window, 0);
        BitConverter.GetBytes(4L).CopyTo(window, 16);
        BitConverter.GetBytes(7L).CopyTo(window, 24);

        Assert.Equal(TextShape.None, TextProbe.At(window, 0).Shape);
    }

    [Fact]
    public void ALongStringPointingNowhereRealIsRefused()
        => Assert.Equal(
            TextShape.None,
            TextProbe.At(Header("Metadata/Monsters/MudGolem", inline: false, data: 42), 0).Shape);

    [Fact]
    public void ARowWithoutRoomForAWholeHeaderIsNotGuessedAt()
    {
        // Near the end of the window there is not enough to judge by, and judging anyway
        // would read the size and capacity out of whatever follows.
        Assert.Equal(TextShape.None, TextProbe.At(new byte[16], 0).Shape);
        Assert.Equal(TextShape.None, TextProbe.At(new byte[TextProbe.HeaderSize], -8).Shape);
    }

    [Fact]
    public void AHeaderIsFoundWhereverItSitsInTheWindow()
    {
        var window = new byte[128];
        Header("Portal", inline: true).CopyTo(window, 64);

        Assert.Equal("Portal", TextProbe.At(window, 64).Text);
        Assert.Equal(TextShape.None, TextProbe.At(window, 0).Shape);
    }

    [Theory]
    [InlineData("Metadata/Monsters/MudGolem", true)]
    [InlineData("Chest", true)]
    [InlineData("ab", false)]                     // too short to mean anything
    [InlineData("", false)]
    [InlineData("回马翻", false)]     // the shape random bytes take as wide characters
    public void TextIsOnlyTextWhenItReadsLikeIt(string text, bool looks)
        => Assert.Equal(looks, TextProbe.LooksLikeText(text));
}
