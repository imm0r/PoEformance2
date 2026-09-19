using System.Globalization;
using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// Greying a picture the way the game greys its own map icons.
/// </summary>
/// <remarks>
/// THE TEST THAT MATTERS IS AGAINST THE GAME'S OWN ART, at the bottom: the sheet carries 163
/// landmarks drawn twice, in colour and greyed, and tests/fixtures/icon-inactive-greys.tsv is
/// pixels sampled out of the pairs the game greyed by a plain rule. What is checked is the FORM
/// - desaturate to the mean, keep the alpha, scale - because that is the part the game is
/// consistent about. The FACTOR is not: over its 27 boss pairs it runs 0.47 to 1.14, which is
/// why the pane has a slider and why this asserts a range rather than a number.
/// </remarks>
public class PictureGreyTests
{
    private static string FixturePath
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return Path.Combine(dir!.FullName, "tests", "fixtures", "icon-inactive-greys.tsv");
        }
    }

    private sealed record Sample(string Name, float K, byte R, byte G, byte B, byte A, byte Grey, byte GreyAlpha);

    private static List<Sample> Fixture()
    {
        var read = new List<Sample>();
        foreach (string line in File.ReadLines(FixturePath))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] parts = line.Split('\t');
            Assert.Equal(8, parts.Length);
            read.Add(new Sample(
                parts[0],
                float.Parse(parts[1], CultureInfo.InvariantCulture),
                byte.Parse(parts[2], CultureInfo.InvariantCulture),
                byte.Parse(parts[3], CultureInfo.InvariantCulture),
                byte.Parse(parts[4], CultureInfo.InvariantCulture),
                byte.Parse(parts[5], CultureInfo.InvariantCulture),
                byte.Parse(parts[6], CultureInfo.InvariantCulture),
                byte.Parse(parts[7], CultureInfo.InvariantCulture)));
        }

        // Not vacuous: an empty or moved fixture would satisfy every assertion below by never
        // entering a loop, which is the shape of check this project treats as worse than none.
        Assert.True(read.Count > 300, $"only {read.Count} samples read - has the fixture moved?");
        return read;
    }

    [Fact]
    public void ItLeavesOneGreyWhereThereWereThreeColours()
    {
        byte[] pixel = [200, 100, 60, 255];
        PictureGrey.Apply(pixel, 1f);

        // (200 + 100 + 60) / 3 = 120, and all three channels are it.
        Assert.Equal(120, pixel[0]);
        Assert.Equal(120, pixel[1]);
        Assert.Equal(120, pixel[2]);
    }

    [Fact]
    public void TheAlphaIsNotTouched()
    {
        byte[] pixels = [10, 20, 30, 7, 200, 200, 200, 128];
        PictureGrey.Apply(pixels, 0.8f);

        Assert.Equal(7, pixels[3]);
        Assert.Equal(128, pixels[7]);
    }

    [Fact]
    public void AClearPixelStaysClear()
    {
        byte[] pixel = [0, 0, 0, 0];
        PictureGrey.Apply(pixel, 1f);

        // The renderer clears its buffer to zero and draws into it, so most of a model's picture
        // is this - and the export's transparency is exactly these pixels staying as they are.
        Assert.Equal<byte[]>([0, 0, 0, 0], pixel);
    }

    [Fact]
    public void TheFactorScalesIt()
    {
        byte[] half = [100, 100, 100, 255];
        PictureGrey.Apply(half, 0.5f);
        Assert.Equal(50, half[0]);

        // Past one is allowed and is not a mistake: the game's own Goldcrush greys BRIGHTER
        // than its colour average.
        byte[] over = [100, 100, 100, 255];
        PictureGrey.Apply(over, 1.2f);
        Assert.Equal(120, over[0]);
    }

    [Fact]
    public void AFactorOutsideTheRangeIsClampedRatherThanTrusted()
    {
        byte[] silly = [100, 100, 100, 255];
        PictureGrey.Apply(silly, 99f);
        Assert.Equal((byte)MathF.Round(100 * PictureGrey.Strongest), silly[0]);

        byte[] negative = [100, 100, 100, 255];
        PictureGrey.Apply(negative, -5f);
        Assert.Equal((byte)MathF.Round(100 * PictureGrey.Weakest), negative[0]);
    }

    [Fact]
    public void ARaggedBufferIsNotReadPastItsEnd()
    {
        // Five bytes is a pixel and a spare, which is what a wrong length looks like. The spare
        // is left alone rather than being treated as the start of a pixel.
        byte[] ragged = [200, 100, 60, 255, 42];
        PictureGrey.Apply(ragged, 1f);

        Assert.Equal(120, ragged[0]);
        Assert.Equal(42, ragged[4]);
    }

    /// <summary>
    /// The transform reproduces the game's own Inactive art, pair by pair.
    /// </summary>
    /// <remarks>
    /// Each pair is greyed with the factor that pair was drawn with - which the fixture carries,
    /// fitted off the art - and the result is compared against the game's own pixels. The
    /// tolerance is read off the data rather than chosen: the worst pair's mean error over its
    /// twelve samples is 2.9 of 255, so four leaves room for a rounding difference and nothing
    /// like enough for a wrong rule. Per-pixel it is looser, because a hand-touched pixel here
    /// and there is exactly what a mean is for.
    /// </remarks>
    [Fact]
    public void ItMatchesTheGamesOwnInactiveIcons()
    {
        var pairs = Fixture().GroupBy(sample => sample.Name).ToList();
        Assert.True(pairs.Count > 20, $"only {pairs.Count} pairs in the fixture");

        foreach (IGrouping<string, Sample> pair in pairs)
        {
            var off = 0f;
            var alphaOff = 0f;
            var counted = 0;

            foreach (Sample sample in pair)
            {
                byte[] pixel = [sample.R, sample.G, sample.B, sample.A];
                PictureGrey.Apply(pixel, sample.K);

                Assert.Equal(pixel[0], pixel[1]);
                Assert.Equal(pixel[1], pixel[2]);
                Assert.Equal(sample.A, pixel[3]);

                off += Math.Abs(pixel[0] - sample.Grey);
                alphaOff += Math.Abs(sample.A - sample.GreyAlpha);
                counted++;
            }

            Assert.True(
                off / counted <= 4f,
                $"{pair.Key}: mean {off / counted:F2} off the game's own grey over {counted} pixels");

            // AND THE GAME KEEPS THE ALPHA, which is what makes the two halves of a pair the
            // same shape. As a mean and not per pixel: 325 of the fixture's 406 samples are
            // identical and 394 are within one, but a few differ by up to sixteen where the
            // Inactive art was touched up by hand, and one of those is not a different rule.
            Assert.True(
                alphaOff / counted <= 2f,
                $"{pair.Key}: the game's grey moved the alpha by {alphaOff / counted:F2} on average");
        }
    }

    /// <summary>
    /// The default factor is one the game's own art supports.
    /// </summary>
    /// <remarks>
    /// A RANGE RATHER THAN A NUMBER, deliberately. The game greys each icon by its own amount,
    /// so no constant can be "correct" - what can be checked is that the one shipped sits among
    /// the factors the art actually uses rather than having been picked out of the air, and
    /// that the slider reaches every one of them.
    /// </remarks>
    [Fact]
    public void TheDefaultSitsAmongTheFactorsTheGameUses()
    {
        float[] ks = [.. Fixture().Select(sample => sample.K).Distinct().Order()];
        float median = ks[ks.Length / 2];

        Assert.InRange(PictureGrey.Measured, median - 0.05f, median + 0.05f);
        Assert.InRange(ks[0], PictureGrey.Weakest, PictureGrey.Strongest);
        Assert.InRange(ks[^1], PictureGrey.Weakest, PictureGrey.Strongest);
    }
}
