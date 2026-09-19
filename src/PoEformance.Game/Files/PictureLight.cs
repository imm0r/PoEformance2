namespace PoEformance.Game.Files;

/// <summary>
/// Brings a picture to the brightness the game's own map icons are drawn at.
/// </summary>
/// <remarks>
/// WHY A RENDERED MODEL NEEDS THIS. A monster's model is lit for standing in a dungeon, and an
/// icon is painted to be read at 64 pixels against a dark map - so a model made into an icon
/// comes out as the shadow of one. Measured on a real export beside the art it has to sit
/// with: the interior of the game's 27 Active boss icons has a median luminance of 55 (mean
/// 63.6, 10-90% 49 to 86), and the first Varloch export measured 24. Half.
///
/// THE TARGET IS THE INTERIOR, not the whole icon, and the difference is not pedantry. Half
/// the area of a finished icon is its black rim - 52% of the opaque pixels are darker than 24
/// of 255 - so an average over everything measures mostly the outline. This is applied to the
/// model BEFORE the rim is drawn, and the number it aims for is the one measured the same way:
/// three cell pixels in and deeper. Their Inactive halves sit at a median of 50, which is what
/// greying 55 lands on, so one target serves both.
///
/// A GAMMA, NOT A MULTIPLY. The lift needed is large - a factor of two on the mean - and a
/// multiply takes every highlight with it: the gold trim on a boss's armour is already at 150
/// and would clip to white, which is the one thing an icon must not do, because a clipped
/// highlight is detail deleted. A gamma leaves black at black and white at white and moves
/// everything between, which is what a curve in a paint program does for the same reason.
///
/// THE EXPONENT IS SOLVED, NOT COMPUTED. The closed form - log(target)/log(now) on the mean -
/// assumes every pixel sits at the mean, and on a real picture it misses badly: asked for 55
/// on a real export it produced 42. <see cref="GammaFor"/> searches the actual distribution
/// instead, over a histogram, which costs one pass and lands within a unit or two. The rest of
/// the gap is that the gamma is applied per CHANNEL while the target is measured on luminance,
/// and those are not the same operation on a saturated colour - which is why whoever draws
/// this reports what it actually achieved rather than what was asked for.
/// </remarks>
public static class PictureLight
{
    /// <summary>
    /// The brightness to aim for: the median interior of the game's Active boss icons.
    /// </summary>
    /// <remarks>
    /// THE MEDIAN AND NOT THE MEAN, because the mean of those 27 is 63.6 and is pulled there
    /// by a handful of pale ones - Goldcrush and the two ice bosses - while the median of 55
    /// is where the body of them sits.
    /// </remarks>
    public const float Measured = 55f;

    /// <summary>The range the pane offers. Past either end an icon stops looking like the set.</summary>
    public const float Dimmest = 25f;

    /// <inheritdoc cref="Dimmest"/>
    public const float Brightest = 110f;

    /// <summary>The exponent that changes nothing.</summary>
    public const float Untouched = 1f;

    /// <summary>Alpha at or above which a pixel is part of the picture rather than of the space around it.</summary>
    private const byte Solid = 128;

    /// <summary>How far the search may go in either direction, and how many halvings it takes.</summary>
    /// <remarks>
    /// A fifth is brighter and a fifth darker than anything a model needs, and twenty halvings
    /// settle the exponent to six decimal places - which is far past what a byte can show, and
    /// costs twenty passes over a 256-bin histogram rather than over a megapixel.
    /// </remarks>
    private const float Steepest = 0.2f;

    /// <inheritdoc cref="Steepest"/>
    private const float Flattest = 5f;

    /// <inheritdoc cref="Steepest"/>
    private const int Halvings = 20;

    /// <summary>
    /// The mean luminance of a picture's own pixels, ignoring the space around them.
    /// </summary>
    /// <remarks>
    /// Rec.709 weights, which is what every measurement this was built from used - see the
    /// type remarks. Zero when there is nothing solid to measure, which a caller reads as
    /// "nothing to do" rather than as "pitch black".
    /// </remarks>
    public static float MeanOf(ReadOnlySpan<byte> rgba)
    {
        double total = 0;
        var counted = 0;

        for (int at = 0; at + 3 < rgba.Length; at += 4)
        {
            if (rgba[at + 3] < Solid)
            {
                continue;
            }

            total += (0.2126 * rgba[at]) + (0.7152 * rgba[at + 1]) + (0.0722 * rgba[at + 2]);
            counted++;
        }

        return counted == 0 ? 0f : (float)(total / counted);
    }

    /// <summary>
    /// The exponent that brings a picture's mean brightness to a target.
    /// </summary>
    /// <remarks>
    /// Searched over a HISTOGRAM of what the picture actually holds rather than solved from its
    /// mean, for the reason the type remarks give: the closed form treats a picture as though
    /// every pixel were the average one, and a picture with a dark half and a bright half is
    /// not that. One pass to count, then twenty halvings over 256 numbers.
    ///
    /// Returns <see cref="Untouched"/> where there is nothing to do: no solid pixels, a target
    /// that is not a brightness, or a picture that is already black or already white - none of
    /// which a gamma can move, since it fixes both ends by construction.
    /// </remarks>
    public static float GammaFor(ReadOnlySpan<byte> rgba, float target)
    {
        Span<int> seen = stackalloc int[256];
        var counted = 0;

        for (int at = 0; at + 3 < rgba.Length; at += 4)
        {
            if (rgba[at + 3] < Solid)
            {
                continue;
            }

            float lum = (0.2126f * rgba[at]) + (0.7152f * rgba[at + 1]) + (0.0722f * rgba[at + 2]);
            seen[(int)Math.Clamp(MathF.Round(lum), 0f, 255f)]++;
            counted++;
        }

        if (counted == 0 || target <= 0f || target >= 255f)
        {
            return Untouched;
        }

        // A picture with nothing between the ends cannot be moved by a curve that fixes them.
        if (seen[0] + seen[255] == counted)
        {
            return Untouched;
        }

        float low = Steepest;
        float high = Flattest;

        for (var halving = 0; halving < Halvings; halving++)
        {
            float middle = (low + high) / 2f;

            // A smaller exponent is a BRIGHTER picture, so the halves go the opposite way
            // round to the usual search.
            if (Mean(seen, counted, middle) < target)
            {
                high = middle;
            }
            else
            {
                low = middle;
            }
        }

        return (low + high) / 2f;
    }

    /// <summary>
    /// Applies the exponent in place, leaving the alpha and both ends of the range alone.
    /// </summary>
    /// <remarks>
    /// THROUGH A TABLE OF 256, because this runs on a megapixel and a power per channel would
    /// be three million of them. The table is the whole cost of the operation; what is left is
    /// one indexed read per byte.
    /// </remarks>
    public static void Apply(Span<byte> rgba, float gamma)
    {
        if (gamma <= 0f || Math.Abs(gamma - Untouched) < 0.001f)
        {
            return;
        }

        Span<byte> lifted = stackalloc byte[256];
        for (var level = 0; level < 256; level++)
        {
            lifted[level] = (byte)Math.Clamp(
                (int)MathF.Round(255f * MathF.Pow(level / 255f, gamma)), 0, 255);
        }

        for (int at = 0; at + 3 < rgba.Length; at += 4)
        {
            if (rgba[at + 3] == 0)
            {
                continue;
            }

            rgba[at] = lifted[rgba[at]];
            rgba[at + 1] = lifted[rgba[at + 1]];
            rgba[at + 2] = lifted[rgba[at + 2]];
        }
    }

    /// <summary>What the mean would come to at one exponent, from the counts rather than the pixels.</summary>
    private static float Mean(ReadOnlySpan<int> seen, int counted, float gamma)
    {
        double total = 0;
        for (var level = 0; level < 256; level++)
        {
            if (seen[level] != 0)
            {
                total += seen[level] * 255d * Math.Pow(level / 255d, gamma);
            }
        }

        return (float)(total / counted);
    }
}
