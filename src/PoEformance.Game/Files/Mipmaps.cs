namespace PoEformance.Game.Files;

/// <summary>
/// A texture and the halved copies of it, down to one texel each way.
/// </summary>
/// <remarks>
/// WHY A PICTURE OF A MONSTER LOOKED LIKE A BAD TELEVISION SIGNAL. A boss's skin is 2048 texels
/// across and its portrait a few hundred pixels, so one pixel stands for a hundred texels or more -
/// and a sampler that picks ONE of them, as this one did, picks a different one for every
/// neighbouring pixel. Wherever the texture has detail, which is everywhere on a monster, the
/// picture comes out as grain, and the grain crawls as the model turns. It was reported from the
/// live client as "grisselig", like poor reception, with a video that showed exactly that.
///
/// THE ANSWER IS AS OLD AS TEXTURED GRAPHICS: keep the texture at every halving, and read from
/// the level whose texels are about the size of a pixel. Each level is the box average of four
/// texels of the one above, so a pixel that stands for a sixteen-by-sixteen patch reads its mean
/// off level four instead of one texel picked out of 256. Built once when the model is read, on the
/// load's own task, and shared by every frame drawn from it: a 2048 square top costs a third again
/// of itself in smaller copies, and one pass over the texels.
///
/// THE GAME'S OWN FILES CARRY THESE LEVELS TOO, and this builds its own regardless. The decoder
/// hands back the top level as plain pixels, and averaging what is already decoded is one loop
/// that needs nothing from the block format - which is what keeps it the same for every texture
/// the install has.
/// </remarks>
public sealed class Mipmaps
{
    private readonly GamePicture[] _levels;

    private Mipmaps(GamePicture[] levels) => _levels = levels;

    /// <summary>How many levels there are, the top included.</summary>
    public int Count => _levels.Length;

    /// <summary>The texture as decoded, full size.</summary>
    public GamePicture Top => _levels[0];

    /// <summary>One level, the top being nought. A level past the last is the last.</summary>
    public GamePicture this[int level] => _levels[Math.Clamp(level, 0, _levels.Length - 1)];

    /// <summary>Builds the levels for a picture, or null where there is no picture.</summary>
    public static Mipmaps? Of(GamePicture? top)
    {
        if (top is not { Ready: true } first)
        {
            return null;
        }

        var levels = new List<GamePicture>(12) { first };
        GamePicture above = first;
        while (above.Width > 1 || above.Height > 1)
        {
            above = Halved(above);
            levels.Add(above);
        }

        return new Mipmaps([.. levels]);
    }

    /// <summary>The box average of every two-by-two of texels; a side already down to one stays one.</summary>
    private static GamePicture Halved(GamePicture above)
    {
        int wide = Math.Max(1, above.Width / 2);
        int high = Math.Max(1, above.Height / 2);
        var rgba = new byte[wide * high * 4];
        byte[] from = above.Rgba;

        for (var y = 0; y < high; y++)
        {
            int y0 = Math.Min(y * 2, above.Height - 1);
            int y1 = Math.Min((y * 2) + 1, above.Height - 1);

            for (var x = 0; x < wide; x++)
            {
                int x0 = Math.Min(x * 2, above.Width - 1);
                int x1 = Math.Min((x * 2) + 1, above.Width - 1);

                int a = ((y0 * above.Width) + x0) * 4;
                int b = ((y0 * above.Width) + x1) * 4;
                int c = ((y1 * above.Width) + x0) * 4;
                int d = ((y1 * above.Width) + x1) * 4;
                int into = ((y * wide) + x) * 4;

                for (var part = 0; part < 4; part++)
                {
                    rgba[into + part] = (byte)((from[a + part] + from[b + part] + from[c + part] + from[d + part] + 2) >> 2);
                }
            }
        }

        return new GamePicture(wide, high, rgba);
    }
}
