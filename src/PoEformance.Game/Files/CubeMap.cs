using System.Globalization;
using System.Numerics;

namespace PoEformance.Game.Files;

/// <summary>
/// One environment cube map's top level, decoded to linear light, and read by direction.
/// </summary>
/// <remarks>
/// WHAT THE INSTALL HOLDS, from the env survey rather than assumed: every diffuse and specular cube
/// an .env names is a DX10 DDS in BC6H_UF16 (DXGI 95), the diffuse ones 32 square with six levels -
/// 178 of 179 - and the one exception, black_cube.dds, 32-bit colour with the old header's masks.
/// Both are read; anything else says what it is and is not drawn.
///
/// ONLY THE TOP LEVEL. The game reads the diffuse cube at level nought (SAMPLE_TEXCUBELOD with a
/// level of 0 in AddDiffuseEnvironmentLight), so the top level is the one that lights a surface.
///
/// THE FACES ARE DIRECT3D'S: +x, -x, +y, -y, +z, -z one after another, each with its levels, and
/// a direction picks its face and its place on it by the table every graphics card follows - the
/// one the OpenGL specification prints as "Cube Map Texture Selection", which Direct3D shares, the
/// face images written top row first. That is the lookup the card does with whatever direction the
/// shader hands it; which direction that is - the normal turned by env_map_rotation - is the
/// environment's business, not the file's.
/// </remarks>
public sealed class CubeMap
{
    private const int HeaderBytes = 128;
    private const int Dx10Bytes = 20;
    private const int Bc6hUnsigned = 95;
    private const int Bc6hSigned = 96;

    /// <summary>The largest face this reads - the specular cubes are 512 square, the diffuse ones 32.</summary>
    public const int LargestFace = 1024;

    private readonly float[] _texels;

    private CubeMap(int size, float[] texels, string format)
    {
        Size = size;
        _texels = texels;
        Format = format;
    }

    /// <summary>How many texels each face is across.</summary>
    public int Size { get; }

    /// <summary>What it was stored as, for the line that says what lit the picture.</summary>
    public string Format { get; }

    /// <summary>The cube, or null with why not.</summary>
    /// <param name="dds">The file's bytes, unpacked - GameArt.ReadRaw.</param>
    /// <param name="why">Why there is no cube, or empty.</param>
    public static CubeMap? Read(byte[]? dds, out string why)
    {
        why = string.Empty;
        if (dds is null || dds.Length < HeaderBytes || dds[0] != (byte)'D' || dds[1] != (byte)'D' || dds[2] != (byte)'S' || dds[3] != (byte)' ')
        {
            why = "not a DDS";
            return null;
        }

        int height = BitConverter.ToInt32(dds, 12);
        int width = BitConverter.ToInt32(dds, 16);
        int levels = Math.Max(1, BitConverter.ToInt32(dds, 28));
        uint flags = BitConverter.ToUInt32(dds, 80);
        uint caps2 = BitConverter.ToUInt32(dds, 112);
        if (width != height || width < 1 || width > LargestFace)
        {
            why = string.Create(CultureInfo.InvariantCulture, $"a cube's faces are square and at most {LargestFace} across; this is {width} x {height}");
            return null;
        }

        int size = width;
        var texels = new float[6 * size * size * 3];
        bool dx10 = (flags & 0x4) != 0 && dds[84] == (byte)'D' && dds[85] == (byte)'X' && dds[86] == (byte)'1' && dds[87] == (byte)'0';
        if (dx10)
        {
            if (dds.Length < HeaderBytes + Dx10Bytes)
            {
                why = "the DX10 header is cut short";
                return null;
            }

            int format = BitConverter.ToInt32(dds, HeaderBytes);
            bool cube = (BitConverter.ToUInt32(dds, HeaderBytes + 8) & 0x4) != 0;
            if (!cube)
            {
                why = "the DX10 header does not say cube";
                return null;
            }

            if (format is not (Bc6hUnsigned or Bc6hSigned))
            {
                why = string.Create(CultureInfo.InvariantCulture, $"DXGI format {format}, which is not read - only BC6H");
                return null;
            }

            bool signed = format == Bc6hSigned;
            int faceBytes = 0;
            for (int level = 0, side = size; level < levels; level++, side = Math.Max(1, side / 2))
            {
                faceBytes += Blocks(side) * Blocks(side) * Bc6h.BlockBytes;
            }

            int start = HeaderBytes + Dx10Bytes;
            if (dds.Length < start + (6 * faceBytes))
            {
                why = "shorter than six faces";
                return null;
            }

            Span<float> block = stackalloc float[48];
            int across = Blocks(size);
            for (int face = 0; face < 6; face++)
            {
                int from = start + (face * faceBytes);
                for (int by = 0; by < across; by++)
                {
                    for (int bx = 0; bx < across; bx++)
                    {
                        Bc6h.Block(dds.AsSpan(from + (((by * across) + bx) * Bc6h.BlockBytes), Bc6h.BlockBytes), block, signed);
                        for (int row = 0; row < 4; row++)
                        {
                            int y = (by * 4) + row;
                            for (int column = 0; column < 4; column++)
                            {
                                int x = (bx * 4) + column;
                                if (x >= size || y >= size)
                                {
                                    continue;
                                }

                                int to = (((face * size * size) + (y * size) + x) * 3);
                                int at = ((row * 4) + column) * 3;
                                texels[to] = block[at];
                                texels[to + 1] = block[at + 1];
                                texels[to + 2] = block[at + 2];
                            }
                        }
                    }
                }
            }

            return new CubeMap(size, texels, string.Create(CultureInfo.InvariantCulture, $"BC6H_{(signed ? "SF16" : "UF16")} {size} square"));
        }

        // THE OLD HEADER: 32-bit colour by its masks, all six faces present.
        int bits = BitConverter.ToInt32(dds, 88);
        if ((flags & 0x40) == 0 || bits != 32)
        {
            why = string.Create(CultureInfo.InvariantCulture, $"neither BC6H nor 32-bit colour (pixel format flags 0x{flags:X}, {bits} bits)");
            return null;
        }

        if ((caps2 & 0x200) == 0 || ((caps2 >> 10) & 0x3F) != 0x3F)
        {
            why = "not a cube with all six faces";
            return null;
        }

        uint red = BitConverter.ToUInt32(dds, 92), green = BitConverter.ToUInt32(dds, 96), blue = BitConverter.ToUInt32(dds, 100);
        int levelPixels = 0;
        for (int level = 0, side = size; level < levels; level++, side = Math.Max(1, side / 2))
        {
            levelPixels += side * side;
        }

        if (dds.Length < HeaderBytes + (6 * levelPixels * 4))
        {
            why = "shorter than six faces";
            return null;
        }

        for (int face = 0; face < 6; face++)
        {
            int from = HeaderBytes + (face * levelPixels * 4);
            for (int pixel = 0; pixel < size * size; pixel++)
            {
                uint value = BitConverter.ToUInt32(dds, from + (pixel * 4));
                int to = ((face * size * size) + pixel) * 3;
                texels[to] = Masked(value, red);
                texels[to + 1] = Masked(value, green);
                texels[to + 2] = Masked(value, blue);
            }
        }

        return new CubeMap(size, texels, string.Create(CultureInfo.InvariantCulture, $"32-bit colour {size} square"));
    }

    /// <summary>
    /// The cube's light the way a direction points, read bilinearly within its face.
    /// </summary>
    /// <remarks>
    /// WITHIN THE FACE, CLAMPED AT ITS EDGE: a card filters across a seam, this does not. On a 32
    /// square diffuse cube - light that changes slowly by construction - the half texel at a seam is
    /// not something a picture shows.
    /// </remarks>
    public Vector3 Sample(Vector3 direction)
    {
        float ax = MathF.Abs(direction.X), ay = MathF.Abs(direction.Y), az = MathF.Abs(direction.Z);
        int face;
        float sc, tc, ma;
        if (ax >= ay && ax >= az)
        {
            face = direction.X >= 0f ? 0 : 1;
            sc = direction.X >= 0f ? -direction.Z : direction.Z;
            tc = -direction.Y;
            ma = ax;
        }
        else if (ay >= az)
        {
            face = direction.Y >= 0f ? 2 : 3;
            sc = direction.X;
            tc = direction.Y >= 0f ? direction.Z : -direction.Z;
            ma = ay;
        }
        else
        {
            face = direction.Z >= 0f ? 4 : 5;
            sc = direction.Z >= 0f ? direction.X : -direction.X;
            tc = -direction.Y;
            ma = az;
        }

        if (!(ma > 0f))
        {
            return Vector3.Zero;
        }

        float u = ((sc / ma) + 1f) * 0.5f * Size - 0.5f;
        float v = ((tc / ma) + 1f) * 0.5f * Size - 0.5f;
        u = Math.Clamp(u, 0f, Size - 1);
        v = Math.Clamp(v, 0f, Size - 1);
        int x0 = (int)u, y0 = (int)v;
        int x1 = Math.Min(x0 + 1, Size - 1), y1 = Math.Min(y0 + 1, Size - 1);
        float fx = u - x0, fy = v - y0;
        int start = face * Size * Size;
        Vector3 top = Vector3.Lerp(Texel(start + (y0 * Size) + x0), Texel(start + (y0 * Size) + x1), fx);
        Vector3 bottom = Vector3.Lerp(Texel(start + (y1 * Size) + x0), Texel(start + (y1 * Size) + x1), fx);
        return Vector3.Lerp(top, bottom, fy);
    }

    /// <summary>The average of the whole cube - what the status line calls its brightness.</summary>
    public Vector3 Average()
    {
        var sum = Vector3.Zero;
        int count = _texels.Length / 3;
        for (int at = 0; at < count; at++)
        {
            sum += Texel(at);
        }

        return count > 0 ? sum / count : Vector3.Zero;
    }

    private Vector3 Texel(int at) => new(_texels[at * 3], _texels[(at * 3) + 1], _texels[(at * 3) + 2]);

    private static int Blocks(int side) => Math.Max(1, (side + 3) / 4);

    private static float Masked(uint value, uint mask)
    {
        if (mask == 0)
        {
            return 0f;
        }

        int shift = BitOperations.TrailingZeroCount(mask);
        uint most = mask >> shift;
        return ((value & mask) >> shift) / (float)most;
    }
}
