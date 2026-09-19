namespace PoEformance.Game.Files;

/// <summary>
/// Greys a picture the way the game greys its own map icons.
/// </summary>
/// <remarks>
/// WHAT THE GAME DOES, MEASURED OFF ITS OWN ART rather than eyeballed. The icon sheet carries
/// 163 pairs of the same landmark drawn twice, <c>&lt;X&gt;Active</c> and <c>&lt;X&gt;Inactive</c>, and
/// comparing them pixel by pixel says three things:
///
///   - The Inactive one is FULLY DESATURATED. Mean residual saturation across all 163 pairs is
///     0.19 of 255, which is r = g = b to the byte.
///   - ITS ALPHA IS UNTOUCHED, on 141 of the 163. So the shape is the same shape; only what is
///     inside it changed.
///   - AND IT IS DARKER, by a factor on the plain mean of the three channels. An affine fit
///     over the pairs that follow a rule gives slope 0.796 and intercept 0.1 - a multiply, with
///     nothing added, which is why <see cref="Apply"/> multiplies and adds nothing.
///
/// THE FACTOR IS NOT ONE NUMBER, AND THAT IS THE FINDING THE SLIDER EXISTS FOR. Over the 27
/// boss pairs it runs from 0.47 (Graveyard) to 1.14 (Goldcrush) with a median of 0.776, and
/// several are BRIGHTER than the colour average rather than darker. Only 34 pairs in the whole
/// sheet are a clean scale of the mean at all; the rest were repainted by hand. So the default
/// here is the boss pairs' median and the range reaches past 1, because no single number is
/// right and pretending otherwise would just be a wrong number with no way to correct it.
///
/// tests/fixtures/icon-inactive-greys.tsv holds the sampled pixels this was read off, and the
/// test against it checks the FORM - desaturate, keep the alpha, scale by k - rather than the
/// factor, which is the part the game itself is not consistent about.
/// </remarks>
public static class PictureGrey
{
    /// <summary>The factor to grey by unless somebody chooses another. See the type remarks.</summary>
    public const float Measured = 0.78f;

    /// <summary>The darkest the factor may be set - just under the darkest pair in the sheet.</summary>
    public const float Weakest = 0.4f;

    /// <summary>
    /// The brightest. Past one on purpose: the game's own Goldcrush greys to 1.14 of its colour
    /// average, so a range that stopped at one could not reproduce the art it is copying.
    /// </summary>
    public const float Strongest = 1.3f;

    /// <summary>
    /// Greys a picture in place: r = g = b = mean(r, g, b) * factor, with alpha left alone.
    /// </summary>
    /// <remarks>
    /// IN PLACE AND IN ONE PASS, because this runs on the buffer a model was just drawn into -
    /// up to a megapixel - on the thread that draws. Nothing is allocated and each pixel is read
    /// and written once; the arithmetic is integer but for the one multiply, which is the factor.
    ///
    /// THE PLAIN MEAN, not a luminance. Rec.709 and Rec.601 weightings were both fitted against
    /// the game's pairs and both came out worse than the mean on most of them - the art was made
    /// with a mean, whatever a colour-science argument would prefer, and this is copying the art.
    ///
    /// A FULLY TRANSPARENT PIXEL IS LEFT ALONE, which matters because the model is drawn into a
    /// cleared buffer: those pixels are (0,0,0,0) and greying them keeps them at zero anyway, so
    /// this is about not writing to three quarters of a buffer for no change.
    /// </remarks>
    /// <param name="rgba">The pixels, four bytes each, in r, g, b, a order.</param>
    /// <param name="factor">How much of the colour's mean the grey keeps. Clamped to the range above.</param>
    public static void Apply(Span<byte> rgba, float factor = Measured)
    {
        float scale = Math.Clamp(factor, Weakest, Strongest);

        for (int at = 0; at + 3 < rgba.Length; at += 4)
        {
            if (rgba[at + 3] == 0)
            {
                continue;
            }

            // The mean is NOT rounded before it is scaled. Rounding twice moves the result by
            // up to a third of the factor, and the whole point of this number is that it can be
            // compared against the game's own art - see the fixture the test reads.
            float mean = (rgba[at] + rgba[at + 1] + rgba[at + 2]) * (1f / 3f);
            var grey = (byte)Math.Clamp((int)MathF.Round(mean * scale), 0, 255);

            rgba[at] = grey;
            rgba[at + 1] = grey;
            rgba[at + 2] = grey;
        }
    }
}
