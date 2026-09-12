namespace PoEformance.Game.World;

/// <summary>
/// The layout as a picture something can upload: straight-alpha RGBA, four bytes a texel.
/// </summary>
/// <remarks>
/// One picture is not enough, and the minimap is why. The renderer has no mipmaps - it
/// creates every texture with a single level and samples it bilinearly - so wherever a texel
/// is smaller than a screen pixel, a one-texel line is hit by some pixels and missed by the
/// rest, and a wall becomes a row of dashes. That is not the line being too faint; it is the
/// line being SKIPPED, and no colour or width fixes it. What fixes it is the thing mipmaps
/// are: the same picture at half, quarter and eighth size, each texel the average of the four
/// it replaces, and the drawing picks the size whose texel is nearest a pixel (see
/// <see cref="LevelFor"/>). A line averaged down that way is a fainter, wider line at every
/// pixel instead of a full line at every fourth one.
///
/// The average is taken in PREMULTIPLIED space and stored straight - see <see cref="Halved"/>.
/// The renderer blends straight alpha (SourceAlpha, InverseSourceAlpha), so straight is what
/// it has to be handed; but averaging straight colours weights a clear texel's colour the same
/// as an opaque one's, and a white line beside clear black comes out grey. Averaging the
/// alpha-weighted colours and dividing by the summed alpha is the average of what is seen.
/// </remarks>
public sealed record TerrainPicture(byte[] Pixels, int Width, int Height)
{
    /// <summary>
    /// Fewer screen pixels per texel than this, and the next coarser level is drawn.
    /// </summary>
    /// <remarks>
    /// One over the square root of two. The sizes go in doublings, so this puts the switch
    /// halfway between two of them in the ratio sense: the level chosen is always the one
    /// whose texel is nearest a pixel, between 0.71 and 1.41 of one. Bilinear sampling covers
    /// a two-by-two footprint, so within that band every texel still reaches the screen.
    /// </remarks>
    public const float LeastPixelsPerTexel = 0.70710677f;

    /// <summary>
    /// Which of <paramref name="levels"/> sizes to draw when the finest one's texel measures
    /// <paramref name="texelPixels"/> on screen. Zero is the finest.
    /// </summary>
    public static int LevelFor(float texelPixels, int levels)
    {
        // An unmeasured map draws the finest picture, which is what it drew before there
        // were any others.
        if (levels <= 1 || !(texelPixels > 0f))
        {
            return 0;
        }

        int level = 0;
        float size = texelPixels;
        while (level + 1 < levels && size < LeastPixelsPerTexel)
        {
            size *= 2f;
            level++;
        }

        return level;
    }

    /// <summary>
    /// Divides two colours by their shared envelope, so that ONE tint can draw both.
    /// </summary>
    /// <remarks>
    /// The quad is drawn through a single colour, which multiplies every texel. That was
    /// simply the line's colour while there was only a line; with a fill of its own colour
    /// under it, neither colour can be the tint - a navy fill drawn through a pale-grey tint
    /// is not navy. But a multiplication is exact when it is undone: take the tint as the
    /// channel-wise MAXIMUM of the two, bake each colour as its ratio to that, and the
    /// multiplication puts both back to within a rounding of eight bits. What it costs is
    /// that a colour change now rebuilds the picture, which the page sends once per pick
    /// rather than once per drag of the picker.
    ///
    /// Alphas pass through untouched: they are the line's and the fill's own, and the tint
    /// is opaque.
    /// </remarks>
    /// <param name="line">The line's colour, ABGR as ImGui packs it.</param>
    /// <param name="fill">
    /// The fill's colour, with its opacity for alpha. Left out of the envelope when that
    /// alpha is zero, so a switched-off fill cannot darken the line.
    /// </param>
    public static (uint Tint, uint Line, uint Fill) Split(uint line, uint fill)
    {
        bool filled = (fill >> 24) != 0;
        uint tint = 0xFF00_0000;
        uint bakedLine = line & 0xFF00_0000;
        uint bakedFill = fill & 0xFF00_0000;

        for (int shift = 0; shift < 24; shift += 8)
        {
            uint l = (line >> shift) & 0xFF;
            uint f = filled ? (fill >> shift) & 0xFF : 0;
            uint top = Math.Max(l, f);
            tint |= top << shift;
            if (top == 0)
            {
                continue;
            }

            bakedLine |= (((l * 255) + (top / 2)) / top) << shift;
            bakedFill |= (((f * 255) + (top / 2)) / top) << shift;
        }

        return (tint, bakedLine, bakedFill);
    }

    /// <summary>
    /// Colours the outline: the line over a rim over the fill, on a clear ground.
    /// </summary>
    /// <remarks>
    /// The rim is drawn only where there is NO fill. It exists to separate the line from a
    /// ground that happens to match it, and inside the outline the fill IS the ground - a
    /// dark sheet a pale line already reads against, where a second dark band along the
    /// line's inner edge would draw a shadow the game's own map does not have. Outside, the
    /// world is the ground and the rim does its old job. With the fill off this is the rim
    /// everywhere, exactly as before.
    ///
    /// The rim is never more solid than the line it serves: a faint line with a firm black
    /// edge reads as the edge, and the line becomes the thing that is hard to see.
    /// </remarks>
    /// <param name="mask">The line.</param>
    /// <param name="rim">
    /// The line grown by the rim's reach - or the line itself when there is to be no rim,
    /// which the line's own pixels then cover entirely.
    /// </param>
    /// <param name="fill">How much of each pixel is floor, 0 to 255, or null for no fill.</param>
    /// <param name="line">The line's baked colour and alpha, ABGR.</param>
    /// <param name="fillColour">The fill's baked colour, and its alpha at full coverage, ABGR.</param>
    /// <param name="rimAlpha">The rim's alpha, before the line's caps it.</param>
    public static TerrainPicture Paint(
        OutlineMask mask, byte[] rim, byte[]? fill, uint line, uint fillColour, byte rimAlpha)
    {
        ArgumentNullException.ThrowIfNull(mask);
        ArgumentNullException.ThrowIfNull(rim);

        int count = mask.Width * mask.Height;
        ArgumentOutOfRangeException.ThrowIfNotEqual(rim.Length, count);
        if (fill is not null)
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual(fill.Length, count);
        }

        byte lineR = (byte)line;
        byte lineG = (byte)(line >> 8);
        byte lineB = (byte)(line >> 16);
        byte lineA = (byte)(line >> 24);
        byte fillR = (byte)fillColour;
        byte fillG = (byte)(fillColour >> 8);
        byte fillB = (byte)(fillColour >> 16);
        int fillA = (byte)(fillColour >> 24);
        byte rimA = Math.Min(rimAlpha, lineA);

        byte[] cells = mask.Cells;
        var pixels = new byte[count * 4];
        for (int i = 0, at = 0; i < count; i++, at += 4)
        {
            if (cells[i] != 0)
            {
                pixels[at] = lineR;
                pixels[at + 1] = lineG;
                pixels[at + 2] = lineB;
                pixels[at + 3] = lineA;
                continue;
            }

            int cover = fill is null ? 0 : fill[i];
            if (cover == 0)
            {
                // The rim is black, which a cleared array already is.
                if (rim[i] != 0)
                {
                    pixels[at + 3] = rimA;
                }

                continue;
            }

            pixels[at] = fillR;
            pixels[at + 1] = fillG;
            pixels[at + 2] = fillB;
            pixels[at + 3] = (byte)(((fillA * cover) + 127) / 255);
        }

        return new TerrainPicture(pixels, mask.Width, mask.Height);
    }

    /// <summary>The same picture at half the size, each texel the average of four.</summary>
    /// <remarks>
    /// Odd edges are padded with clear: the last texel of an odd row averages two real texels
    /// and two clear ones, so it is half as solid, which is right - half of it covers padding.
    /// Whoever draws the result covers TWICE ITS WIDTH of the finer level's texels, padding
    /// included; stretched over the finer level's own width it would be off by up to a texel
    /// toward the far edge.
    /// </remarks>
    public TerrainPicture Halved()
    {
        int width = (Width + 1) / 2;
        int height = (Height + 1) / 2;
        byte[] source = Pixels;
        var pixels = new byte[width * height * 4];

        for (int oy = 0; oy < height; oy++)
        {
            int y = oy * 2;
            bool twoRows = y + 1 < Height;
            for (int ox = 0; ox < width; ox++)
            {
                int x = ox * 2;
                bool twoColumns = x + 1 < Width;

                int sumA = 0;
                int sumR = 0;
                int sumG = 0;
                int sumB = 0;
                int at = ((y * Width) + x) * 4;
                Weigh(source, at, ref sumA, ref sumR, ref sumG, ref sumB);
                if (twoColumns)
                {
                    Weigh(source, at + 4, ref sumA, ref sumR, ref sumG, ref sumB);
                }

                if (twoRows)
                {
                    int below = at + (Width * 4);
                    Weigh(source, below, ref sumA, ref sumR, ref sumG, ref sumB);
                    if (twoColumns)
                    {
                        Weigh(source, below + 4, ref sumA, ref sumR, ref sumG, ref sumB);
                    }
                }

                int alpha = (sumA + 2) / 4;
                if (alpha == 0)
                {
                    continue;
                }

                // The colour of the premultiplied average, made straight again: the summed
                // weighted colour over the summed weight. Rounded, not truncated - a dark fill
                // is a small number to begin with.
                int half = sumA / 2;
                int to = ((oy * width) + ox) * 4;
                pixels[to] = (byte)((sumR + half) / sumA);
                pixels[to + 1] = (byte)((sumG + half) / sumA);
                pixels[to + 2] = (byte)((sumB + half) / sumA);
                pixels[to + 3] = (byte)alpha;
            }
        }

        return new TerrainPicture(pixels, width, height);
    }

    /// <summary>Adds one texel to a premultiplied sum; a clear texel adds nothing at all.</summary>
    private static void Weigh(byte[] source, int at, ref int sumA, ref int sumR, ref int sumG, ref int sumB)
    {
        int a = source[at + 3];
        if (a == 0)
        {
            return;
        }

        sumA += a;
        sumR += source[at] * a;
        sumG += source[at + 1] * a;
        sumB += source[at + 2] * a;
    }
}
