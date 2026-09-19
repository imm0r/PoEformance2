namespace PoEformance.Game.Files;

/// <summary>Which side of the silhouette an outline is drawn on.</summary>
public enum OutlineSide
{
    /// <summary>None at all.</summary>
    None,

    /// <summary>Grown outwards, leaving the picture itself untouched.</summary>
    Outward,

    /// <summary>Painted over the picture's own outermost pixels.</summary>
    Inward,
}

/// <summary>
/// Puts the black outline round a picture that the game's own map icons have.
/// </summary>
/// <remarks>
/// MEASURED, NOT ASSUMED. Over the 27 Active boss icons in the sheet, mean luminance by
/// distance from the edge of the silhouette runs 5.4, 23.2, 56.3, 61.6, 61.2 - so the
/// outermost pixel is black (92% of those pixels are darker than 16 of 255, mean colour
/// r6 g5 b4), the second is half way out of it, and the art itself starts at the third. It is
/// part of the SILHOUETTE rather than a frame around it: the outer ring's own alpha averages
/// 170, which is the outline being antialiased against nothing.
///
/// AND IT IS NOT GREYED. The Inactive halves carry the same black rim - 10 to 13 against cores
/// of 45 to 65 - so whatever the Active one has, its pair has too. Black survives the greying
/// anyway (the mean of three zeroes, scaled, is zero), but the order here is grey first and
/// outline second, so that stays true if either ever stops being a multiply.
///
/// WHY A RENDERED MODEL NEEDS IT: without one it ends at its own silhouette and dissolves into
/// whatever the minimap is drawing under it, which is exactly what the game's artists put the
/// rim there to prevent. A model made into an icon and dropped beside thirty that have one is
/// the one that looks wrong, and it is not obvious why until they are side by side.
///
/// TWO SIDES, BECAUSE THEY LOSE DIFFERENT THINGS. Grown outwards the picture is untouched and
/// the shape gets bigger, which is what a 3D render wants - at a sheet cell's size there is no
/// detail to spare. Painted inwards it is closer to how the art was drawn, at the price of the
/// outermost pixels of the thing itself: on a blade or a horn that is most of what there was.
///
/// DRAWN AT THE PICTURE'S OWN SIZE AND SHRUNK AFTERWARDS, which is why the edge here is hard:
/// at sixteen picture pixels to a cell pixel, the downscale is what antialiases it, and doing
/// both would only soften it twice.
/// </remarks>
public static class PictureOutline
{
    /// <summary>How wide the game's own outline is, in CELL pixels. See the type remarks.</summary>
    public const float Measured = 1.5f;

    /// <summary>The widest the pane offers. Past this the icon is a black blob with a middle.</summary>
    public const float Widest = 3f;

    /// <summary>Alpha at or above which a pixel counts as part of the silhouette.</summary>
    /// <remarks>
    /// Half. The renderer writes either a covered pixel or a cleared one, so almost nothing
    /// lands between - what this really decides is what happens to the few pixels a partly
    /// transparent texture leaves behind, and half is the answer that does not grow an outline
    /// around a puff of smoke.
    /// </remarks>
    private const byte Solid = 128;

    /// <summary>Orthogonal and diagonal steps of the chamfer distance, in thirds of a pixel.</summary>
    /// <remarks>
    /// 3 and 4 rather than 1 and 1.414 so the whole distance map stays in integers. The pair is
    /// the standard chamfer approximation and is within about 3% of the true distance, which at
    /// a radius of a few dozen pixels is a fraction of one - and it is then shrunk sixteen to
    /// one. An exact Euclidean transform would cost a second pass over the picture to be
    /// invisible.
    /// </remarks>
    private const int Straight = 3;

    /// <inheritdoc cref="Straight"/>
    private const int Diagonal = 4;

    /// <summary>
    /// Draws the outline into a picture, in place.
    /// </summary>
    /// <param name="rgba">The pixels, four bytes each, r, g, b, a.</param>
    /// <param name="wide">How many pixels across.</param>
    /// <param name="tall">And down.</param>
    /// <param name="radius">How far the outline reaches, in picture pixels. Nothing happens at zero.</param>
    /// <param name="side">Which side of the silhouette it is drawn on.</param>
    public static void Apply(Span<byte> rgba, int wide, int tall, float radius, OutlineSide side)
    {
        if (side == OutlineSide.None || radius <= 0f || wide <= 0 || tall <= 0
            || rgba.Length < wide * tall * 4)
        {
            return;
        }

        // The distance is measured FROM the silhouette outwards, or from the outside inwards,
        // which is the same transform run against the opposite set of seeds.
        int[] away = Distances(rgba, wide, tall, from: side == OutlineSide.Outward);
        int reach = (int)MathF.Round(radius * Straight);

        for (var at = 0; at < wide * tall; at++)
        {
            if (away[at] > reach)
            {
                continue;
            }

            int pixel = at * 4;
            bool inside = rgba[pixel + 3] >= Solid;

            if (side == OutlineSide.Inward)
            {
                // Only the picture's own pixels, blackened where they are near the edge. The
                // alpha is left alone: the shape does not change, only its colour.
                if (!inside)
                {
                    continue;
                }

                rgba[pixel] = 0;
                rgba[pixel + 1] = 0;
                rgba[pixel + 2] = 0;
                continue;
            }

            if (inside)
            {
                continue;
            }

            // BLACK UNDERNEATH rather than over: where the picture is partly there - a texture
            // with a soft edge - the outline shows through what is missing instead of covering
            // what is present. Compositing "picture over black" with the picture's own alpha is
            // what that is, and where the picture is absent entirely it is plain black.
            byte had = rgba[pixel + 3];
            if (had == 0)
            {
                rgba[pixel] = 0;
                rgba[pixel + 1] = 0;
                rgba[pixel + 2] = 0;
                rgba[pixel + 3] = 255;
                continue;
            }

            rgba[pixel] = (byte)(rgba[pixel] * had / 255);
            rgba[pixel + 1] = (byte)(rgba[pixel + 1] * had / 255);
            rgba[pixel + 2] = (byte)(rgba[pixel + 2] * had / 255);
            rgba[pixel + 3] = 255;
        }
    }

    /// <summary>
    /// How far each pixel is from the silhouette, in thirds of a pixel.
    /// </summary>
    /// <remarks>
    /// Two passes over the picture, forward and backward, each looking only at the neighbours
    /// it has already settled - the standard chamfer transform. Linear in the pixels and with
    /// no allocation beyond the map itself, which matters because this runs on a megapixel.
    /// </remarks>
    /// <param name="from">
    /// True to measure OUT of the silhouette (its own pixels are zero), false to measure INTO
    /// it (everything outside is zero).
    /// </param>
    private static int[] Distances(ReadOnlySpan<byte> rgba, int wide, int tall, bool from)
    {
        var away = new int[wide * tall];
        const int Far = int.MaxValue / 4;

        for (var at = 0; at < away.Length; at++)
        {
            bool inside = rgba[(at * 4) + 3] >= Solid;
            away[at] = inside == from ? 0 : Far;
        }

        for (var y = 0; y < tall; y++)
        {
            for (var x = 0; x < wide; x++)
            {
                int at = (y * wide) + x;
                int best = away[at];
                if (best == 0)
                {
                    continue;
                }

                if (x > 0)
                {
                    best = Math.Min(best, away[at - 1] + Straight);
                }

                if (y > 0)
                {
                    best = Math.Min(best, away[at - wide] + Straight);

                    if (x > 0)
                    {
                        best = Math.Min(best, away[at - wide - 1] + Diagonal);
                    }

                    if (x < wide - 1)
                    {
                        best = Math.Min(best, away[at - wide + 1] + Diagonal);
                    }
                }

                away[at] = best;
            }
        }

        for (int y = tall - 1; y >= 0; y--)
        {
            for (int x = wide - 1; x >= 0; x--)
            {
                int at = (y * wide) + x;
                int best = away[at];
                if (best == 0)
                {
                    continue;
                }

                if (x < wide - 1)
                {
                    best = Math.Min(best, away[at + 1] + Straight);
                }

                if (y < tall - 1)
                {
                    best = Math.Min(best, away[at + wide] + Straight);

                    if (x < wide - 1)
                    {
                        best = Math.Min(best, away[at + wide + 1] + Diagonal);
                    }

                    if (x > 0)
                    {
                        best = Math.Min(best, away[at + wide - 1] + Diagonal);
                    }
                }

                away[at] = best;
            }
        }

        return away;
    }
}
