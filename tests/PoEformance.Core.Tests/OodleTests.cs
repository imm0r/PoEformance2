using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// Choosing a decoder for an install, and saying what happened when one refuses.
/// </summary>
/// <remarks>
/// THE ONE LAYER THAT CANNOT BE TESTED AGAINST REAL DATA. There is no Oodle compressor to build
/// a fixture with, so nothing here can decompress anything - the chunk table, the range
/// arithmetic and the index are all checked elsewhere against data the tests build themselves,
/// and both decoders are handed in for exactly this reason.
///
/// What CAN be checked is everything around it, and that is where both of the failures that hurt
/// actually were: which decoder gets picked and whether anybody is told, and what happens to a
/// refusal on its way out. A refused chunk that says nothing leaves "it will not decompress" as
/// the whole of what anybody can find out - which is where this went, on a real install, for a
/// day.
/// </remarks>
public class OodleTests
{
    [Fact]
    public void WITHOUTAnInstallItFallsBackAndSaysSo()
    {
        // Not silently. Which decoder ran is the difference between "the shipped one cannot read
        // your install" and "the tool cannot read your install", and those have different cures.
        Oodle.Unpacker none = Oodle.For(null);

        Assert.Contains("managed", none.Which, StringComparison.Ordinal);
        Assert.Contains("no game folder", none.Which, StringComparison.Ordinal);
    }

    [Fact]
    public void ANDAFolderWithNoOodleInItSaysThatRatherThanNothing()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"no-oodle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);

        try
        {
            Oodle.Unpacker fallback = Oodle.For(folder);

            Assert.Contains("managed", fallback.Which, StringComparison.Ordinal);
            Assert.Contains(Oodle.LibraryPattern, fallback.Which, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder);
        }
    }

    [Fact]
    public void AREFUSEDChunkCarriesWhatTheDecoderSaidAndWhatItSaidItAbout()
    {
        // Thrown away before, and that was the whole problem: a decoder that refuses an install
        // knows why, and the one line the tool printed did not.
        byte[] rubbish = [0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04, 0x05];

        Assert.Null(Oodle.Decompress(rubbish, 4096));

        // The first bytes because that is where a chunk names its own compressor: told them,
        // somebody who knows the format can say what this install packs with, from a log line.
        Assert.Contains("DEADBEEF", Oodle.LastRefusal, StringComparison.Ordinal);
        Assert.NotEqual(string.Empty, Oodle.LastRefusal);
    }

    [Fact]
    public void ANDNothingItIsAskedIsWorthTakingTheProcessDownFor()
    {
        // It runs while a stash is being drawn. The decoder is a PORT and has paths it does not
        // implement - NotImplementedException was not among the ones caught, so a bundle that
        // reached one took the whole tool down from a draw rather than costing one picture.
        byte[][] awkward =
        [
            [],
            [0x00],
            [0xFF, 0xFF, 0xFF, 0xFF],
            [.. Enumerable.Range(0, 256).Select(one => (byte)one)],
        ];

        foreach (byte[] one in awkward)
        {
            Assert.Null(Oodle.Decompress(one, 0));
            Assert.Null(Oodle.Decompress(one, -1));

            // Whatever it makes of these, it COMES BACK. Null is an answer; an exception here
            // is the overlay gone mid-frame.
            Assert.Null(Record.Exception(() => Oodle.Decompress(one, 64 * 1024)));
        }
    }
}
