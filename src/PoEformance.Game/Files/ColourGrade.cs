using System.Globalization;
using System.Numerics;

namespace PoEformance.Game.Files;

/// <summary>
/// An environment's colour grade - post_transform's 3D table - read from its DDS and applied the way the game's post process applies it.
/// </summary>
/// <remarks>
/// THE GAME'S OWN ARITHMETIC, from postprocessuber.hlsl's ApplyColorGrading and the copy of it in
/// imgui.hlsl, with GGG_POE_1 off as it is for PoE2:
/// <code>
///   l      = max(max(r, g), max(b, 1))
///   colour = lerp(table0(pow(saturate(colour / l), 1 / 2.2)), table1(...), ratio) * l
/// </code>
/// read trilinearly with a clamping sampler at level nought - SAMPLE_TEX3DLOD with SamplerLinearClamp.
/// The table is looked up by the colour with a gamma of 2.2 taken off, and it gives back LINEAR
/// light: imgui.hlsl hands its result straight to ApplyOETF, the function that encodes linear light
/// for the screen. It comes after the exposure (ApplyToneMapping) and the desaturation, a gameplay
/// effect this does not draw.
///
/// ONE TABLE: the second one and the ratio blend two environments while the game moves from one to
/// the next. A picture lit by one environment is that environment's table alone.
///
/// WHAT THE FILE HOLDS was not seen before this was written, so nothing about it is assumed beyond
/// what the shader declares - a 3D texture. The plain formats a table can be stored in are read,
/// the sRGB ones decoded to linear as a graphics card decodes them when it samples; a file in any
/// other format, or one that is not a volume, says exactly what it is instead of being drawn.
///
/// HOW ITS TEXELS ARE MEANT IS SETTLED BY THE TABLE ITSELF. A card reads an 8-bit texture either as
/// it stands or through an sRGB view, which decodes every texel to linear light before it filters -
/// whichever the engine creates it as, and the old DDS header cannot even say. The shader decides
/// what a table that changes nothing must hold: the lookup is by pow(c, 1/2.2) and must give back c,
/// so a neutral table holds either its own coordinate, read through an sRGB view, or that
/// coordinate's 2.2nd power, read as it stands. A grade is a table near neutral, so it is read the
/// way of the neutral table it lies nearer, and the line under the picture prints both distances.
/// The first build read every table as it stands, and AzmerianRanges' came out washed - lifted
/// shadows, white rock, the gamma put on twice.
/// </remarks>
public sealed class ColourGrade
{
    /// <summary>The widest side read - a colour table is a few dozen texels across.</summary>
    public const int LargestSide = 256;

    private const int HeaderBytes = 128;
    private const int Dx10Bytes = 20;
    private const uint DepthFlag = 0x800000;
    private const uint VolumeCaps = 0x200000;
    private const int Texture3D = 4;

    /// <summary>Entries in the table that stands in for pow(x, 1/2.2) - a pow per channel per pixel otherwise.</summary>
    private const int GammaSteps = 4096;

    private static readonly float[] Gamma = GammaTable();

    private readonly Vector3[] _texels;
    private readonly int _width, _height, _depth;

    private ColourGrade(Vector3[] texels, int width, int height, int depth, string format, bool decoded, float fromGamma, float fromLinear)
    {
        _texels = texels;
        _width = width;
        _height = height;
        _depth = depth;
        Format = format;
        Decoded = decoded;
        FromGammaNeutral = fromGamma;
        FromLinearNeutral = fromLinear;
    }

    /// <summary>What it was stored as and how big, and how its texels were taken, for the line that says what graded the picture.</summary>
    public string Format { get; }

    /// <summary>Whether its texels were decoded from sRGB to linear light after reading - see the remarks.</summary>
    public bool Decoded { get; }

    /// <summary>How far the texels as read lie from a neutral table of gamma-encoded values, on average per channel.</summary>
    public float FromGammaNeutral { get; }

    /// <summary>How far they lie from a neutral table of linear values.</summary>
    public float FromLinearNeutral { get; }

    /// <summary>The table, or null with why not.</summary>
    /// <param name="dds">The file's bytes, unpacked - GameArt.ReadRaw.</param>
    /// <param name="why">Why there is no table, or empty.</param>
    public static ColourGrade? Read(byte[]? dds, out string why)
    {
        why = string.Empty;
        if (dds is null || dds.Length < HeaderBytes || dds[0] != (byte)'D' || dds[1] != (byte)'D' || dds[2] != (byte)'S' || dds[3] != (byte)' ')
        {
            why = "not a DDS";
            return null;
        }

        uint flags = BitConverter.ToUInt32(dds, 8);
        int height = BitConverter.ToInt32(dds, 12);
        int width = BitConverter.ToInt32(dds, 16);
        int depth = BitConverter.ToInt32(dds, 24);
        uint pixelFlags = BitConverter.ToUInt32(dds, 80);
        int fourCc = BitConverter.ToInt32(dds, 84);
        int bits = BitConverter.ToInt32(dds, 88);
        uint caps2 = BitConverter.ToUInt32(dds, 112);
        bool dx10 = (pixelFlags & 0x4) != 0 && dds[84] == (byte)'D' && dds[85] == (byte)'X' && dds[86] == (byte)'1' && dds[87] == (byte)'0';

        bool volume;
        int start = HeaderBytes;
        Stored format;
        if (dx10)
        {
            if (dds.Length < HeaderBytes + Dx10Bytes)
            {
                why = "the DX10 header is cut short";
                return null;
            }

            int dxgi = BitConverter.ToInt32(dds, HeaderBytes);
            volume = BitConverter.ToInt32(dds, HeaderBytes + 4) == Texture3D;
            start += Dx10Bytes;
            format = OfDxgi(dxgi);
            if (format.Kind == Kind.Unread)
            {
                why = string.Create(CultureInfo.InvariantCulture, $"{DxgiName(dxgi)}, which is not read{(volume ? string.Empty : ", and not a volume")}");
                return null;
            }
        }
        else
        {
            volume = (caps2 & VolumeCaps) != 0 || (flags & DepthFlag) != 0;
            format = (pixelFlags & 0x4) != 0
                ? fourCc switch
                {
                    36 => new Stored(Kind.Unorm16x4, 8, "A16B16G16R16"),
                    113 => new Stored(Kind.Half4, 8, "A16B16G16R16F"),
                    116 => new Stored(Kind.Float4, 16, "A32B32G32R32F"),
                    _ => new Stored(Kind.Unread, 0, string.Create(CultureInfo.InvariantCulture, $"four-character code {FourCcText(dds)}")),
                }
                : (pixelFlags & 0x40) != 0 && bits is 24 or 32
                    ? new Stored(Kind.Masked, bits / 8, string.Create(CultureInfo.InvariantCulture, $"{bits}-bit colour by masks"))
                    : new Stored(Kind.Unread, 0, string.Create(CultureInfo.InvariantCulture, $"pixel format flags 0x{pixelFlags:X}, {bits} bits"));
            if (format.Kind == Kind.Unread)
            {
                why = $"{format.Name}, which is not read";
                return null;
            }
        }

        if (!volume || depth < 2)
        {
            why = string.Create(CultureInfo.InvariantCulture, $"not a volume: {width} x {height}{(depth > 1 ? $" x {depth}" : string.Empty)}, {format.Name}");
            return null;
        }

        if (width is < 2 or > LargestSide || height is < 2 or > LargestSide || depth > LargestSide)
        {
            why = string.Create(CultureInfo.InvariantCulture, $"{width} x {height} x {depth}, past the {LargestSide} a side read");
            return null;
        }

        int texels = width * height * depth;
        var table = new Vector3[texels];
        if (format.Kind == Kind.Bc6h || format.Kind == Kind.Bc6hSigned)
        {
            if (!Blocks(dds, start, width, height, depth, format.Kind == Kind.Bc6hSigned, table))
            {
                why = "shorter than its BC6H slices";
                return null;
            }
        }
        else
        {
            if (dds.Length < start + ((long)texels * format.Bytes))
            {
                why = string.Create(CultureInfo.InvariantCulture, $"shorter than {width} x {height} x {depth} of {format.Name}");
                return null;
            }

            Masks masks = format.Kind == Kind.Masked
                ? new Masks(BitConverter.ToUInt32(dds, 92), BitConverter.ToUInt32(dds, 96), BitConverter.ToUInt32(dds, 100))
                : default;
            for (int at = 0, from = start; at < texels; at++, from += format.Bytes)
            {
                table[at] = Texel(dds.AsSpan(from, format.Bytes), format.Kind, masks);
            }
        }

        // THE NEUTRAL TABLE IT LIES NEARER says how its texels are meant - see the remarks.
        (float fromGamma, float fromLinear) = Neutrality(table, width, height, depth);
        bool decoded = fromGamma < fromLinear;
        if (decoded)
        {
            for (var at = 0; at < table.Length; at++)
            {
                Vector3 one = table[at];
                table[at] = new Vector3(Linear(one.X), Linear(one.Y), Linear(one.Z));
            }
        }

        string viewless = format.Kind is Kind.Rgba8 or Kind.Bgra8 or Kind.Masked ? string.Empty : ", although its format has none";
        string taken = decoded
            ? string.Create(CultureInfo.InvariantCulture,
                $"held gamma-encoded ({fromGamma:0.000} from a neutral table so, {fromLinear:0.000} as linear light) - decoded as an sRGB view decodes it{viewless}")
            : string.Create(CultureInfo.InvariantCulture,
                $"held as linear light ({fromLinear:0.000} from a neutral table so, {fromGamma:0.000} as gamma-encoded) - read as it stands");
        return new ColourGrade(
            table, width, height, depth, string.Create(CultureInfo.InvariantCulture, $"{format.Name} {width} x {height} x {depth}, {taken}"), decoded, fromGamma, fromLinear);
    }

    /// <summary>
    /// How far a table lies, on average per channel, from the two tables that change nothing: one holding each texel's own coordinate, one holding its 2.2nd power.
    /// </summary>
    /// <remarks>
    /// A TEXEL'S COORDINATE IS ITS CENTRE, (i + 0.5) / n: the shader samples at the colour itself, and
    /// a clamping sampler reads texel i whole at that place - so that is what the table must hold there
    /// to give the colour back.
    /// </remarks>
    private static (float FromGamma, float FromLinear) Neutrality(Vector3[] table, int width, int height, int depth)
    {
        double gamma = 0, linear = 0;
        int at = 0;
        for (var z = 0; z < depth; z++)
        {
            float b = (z + 0.5f) / depth;
            for (var y = 0; y < height; y++)
            {
                float g = (y + 0.5f) / height;
                for (var x = 0; x < width; x++, at++)
                {
                    float r = (x + 0.5f) / width;
                    Vector3 held = table[at];
                    var coordinate = new Vector3(r, g, b);
                    var power = new Vector3(MathF.Pow(r, 2.2f), MathF.Pow(g, 2.2f), MathF.Pow(b, 2.2f));
                    Vector3 off = Vector3.Abs(held - coordinate), offLinear = Vector3.Abs(held - power);
                    gamma += off.X + off.Y + off.Z;
                    linear += offLinear.X + offLinear.Y + offLinear.Z;
                }
            }
        }

        double channels = table.Length * 3.0;
        return ((float)(gamma / channels), (float)(linear / channels));
    }

    /// <summary>A linear colour graded - the game's ApplyColorGrading with one table; linear in and out.</summary>
    public Vector3 Apply(Vector3 colour)
    {
        float most = MathF.Max(MathF.Max(colour.X, colour.Y), MathF.Max(colour.Z, 1f));
        float share = 1f / most;
        return Sample(Encoded(colour.X * share), Encoded(colour.Y * share), Encoded(colour.Z * share)) * most;
    }

    /// <summary>The table read trilinearly at a place nought to one each way, clamped as the game's sampler clamps.</summary>
    /// <remarks>
    /// THE EIGHT TEXELS ARE TWO ROWS OF FOUR: the corners are found once, and each step along x is a
    /// one or a nought added to an index, so a texel on the far edge reads itself twice as a clamping
    /// card does. Once per pixel of a lit picture, so it is written for that.
    /// </remarks>
    internal Vector3 Sample(float u, float v, float w)
    {
        float x = Math.Clamp((u * _width) - 0.5f, 0f, _width - 1);
        float y = Math.Clamp((v * _height) - 0.5f, 0f, _height - 1);
        float z = Math.Clamp((w * _depth) - 0.5f, 0f, _depth - 1);
        int x0 = (int)x, y0 = (int)y, z0 = (int)z;
        float fx = x - x0, fy = y - y0, fz = z - z0;
        int stepX = x0 < _width - 1 ? 1 : 0;
        int stepY = y0 < _height - 1 ? _width : 0;
        int stepZ = z0 < _depth - 1 ? _width * _height : 0;
        int a = (((z0 * _height) + y0) * _width) + x0;
        Vector3[] t = _texels;
        Vector3 c00 = Vector3.Lerp(t[a], t[a + stepX], fx);
        Vector3 c10 = Vector3.Lerp(t[a + stepY], t[a + stepY + stepX], fx);
        int b = a + stepZ;
        Vector3 c01 = Vector3.Lerp(t[b], t[b + stepX], fx);
        Vector3 c11 = Vector3.Lerp(t[b + stepY], t[b + stepY + stepX], fx);
        return Vector3.Lerp(Vector3.Lerp(c00, c10, fy), Vector3.Lerp(c01, c11, fy), fz);
    }

    /// <summary>pow(saturate(x), 1/2.2) from the table, read linearly between its steps.</summary>
    internal static float Encoded(float value)
    {
        if (!(value > 0f))
        {
            return 0f;
        }

        if (value >= 1f)
        {
            return 1f;
        }

        float at = value * GammaSteps;
        var step = (int)at;
        return float.Lerp(Gamma[step], Gamma[step + 1], at - step);
    }

    private static float[] GammaTable()
    {
        var table = new float[GammaSteps + 1];
        for (var step = 0; step <= GammaSteps; step++)
        {
            table[step] = MathF.Pow(step / (float)GammaSteps, 1f / 2.2f);
        }

        return table;
    }

    private static bool Blocks(byte[] dds, int start, int width, int height, int depth, bool signed, Vector3[] table)
    {
        int across = Math.Max(1, (width + 3) / 4), down = Math.Max(1, (height + 3) / 4);
        int sliceBytes = across * down * Bc6h.BlockBytes;
        if (dds.Length < start + ((long)sliceBytes * depth))
        {
            return false;
        }

        Span<float> block = stackalloc float[48];
        for (var slice = 0; slice < depth; slice++)
        {
            int from = start + (slice * sliceBytes);
            for (var by = 0; by < down; by++)
            {
                for (var bx = 0; bx < across; bx++)
                {
                    Bc6h.Block(dds.AsSpan(from + (((by * across) + bx) * Bc6h.BlockBytes), Bc6h.BlockBytes), block, signed);
                    for (var row = 0; row < 4; row++)
                    {
                        int y = (by * 4) + row;
                        for (var column = 0; column < 4; column++)
                        {
                            int x = (bx * 4) + column;
                            if (x < width && y < height)
                            {
                                int at = ((row * 4) + column) * 3;
                                table[(((slice * height) + y) * width) + x] = new Vector3(block[at], block[at + 1], block[at + 2]);
                            }
                        }
                    }
                }
            }
        }

        return true;
    }

    private static Vector3 Texel(ReadOnlySpan<byte> bytes, Kind kind, Masks masks)
    {
        switch (kind)
        {
            case Kind.Rgba8:
                return new Vector3(bytes[0], bytes[1], bytes[2]) / 255f;
            case Kind.Rgba8Srgb:
                return new Vector3(Linear(bytes[0] / 255f), Linear(bytes[1] / 255f), Linear(bytes[2] / 255f));
            case Kind.Bgra8:
                return new Vector3(bytes[2], bytes[1], bytes[0]) / 255f;
            case Kind.Bgra8Srgb:
                return new Vector3(Linear(bytes[2] / 255f), Linear(bytes[1] / 255f), Linear(bytes[0] / 255f));
            case Kind.Rgb10A2:
            {
                uint value = BitConverter.ToUInt32(bytes);
                return new Vector3(value & 0x3FF, (value >> 10) & 0x3FF, (value >> 20) & 0x3FF) / 1023f;
            }

            case Kind.Rg11B10:
            {
                uint value = BitConverter.ToUInt32(bytes);
                return new Vector3(Small(value & 0x7FF, 6), Small((value >> 11) & 0x7FF, 6), Small((value >> 22) & 0x3FF, 5));
            }

            case Kind.Rgb9E5:
            {
                uint value = BitConverter.ToUInt32(bytes);
                float scale = MathF.Pow(2f, (int)(value >> 27) - 15 - 9);
                return new Vector3(value & 0x1FF, (value >> 9) & 0x1FF, (value >> 18) & 0x1FF) * scale;
            }

            case Kind.Half4:
                return new Vector3((float)BitConverter.ToHalf(bytes), (float)BitConverter.ToHalf(bytes[2..]), (float)BitConverter.ToHalf(bytes[4..]));
            case Kind.Unorm16x4:
                return new Vector3(BitConverter.ToUInt16(bytes), BitConverter.ToUInt16(bytes[2..]), BitConverter.ToUInt16(bytes[4..])) / 65535f;
            case Kind.Float4:
            case Kind.Float3:
                return new Vector3(BitConverter.ToSingle(bytes), BitConverter.ToSingle(bytes[4..]), BitConverter.ToSingle(bytes[8..]));
            case Kind.Masked:
            {
                uint value = bytes.Length >= 4 ? BitConverter.ToUInt32(bytes) : (uint)(bytes[0] | (bytes[1] << 8) | (bytes[2] << 16));
                return new Vector3(Masked(value, masks.Red), Masked(value, masks.Green), Masked(value, masks.Blue));
            }

            default:
                return Vector3.Zero;
        }
    }

    /// <summary>An sRGB value as linear light - what a card does to an _SRGB texel when it samples it.</summary>
    private static float Linear(float value) => value <= 0.04045f ? value / 12.92f : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);

    /// <summary>One of R11G11B10_FLOAT's unsigned small floats: a five-bit exponent and a mantissa of the given width.</summary>
    private static float Small(uint value, int mantissaBits)
    {
        uint exponent = value >> mantissaBits;
        uint mantissa = value & ((1u << mantissaBits) - 1u);
        float fraction = mantissa / (float)(1u << mantissaBits);
        return exponent switch
        {
            0 => fraction * MathF.Pow(2f, -14f),
            31 => mantissa == 0 ? float.PositiveInfinity : float.NaN,
            _ => (1f + fraction) * MathF.Pow(2f, (int)exponent - 15),
        };
    }

    private static float Masked(uint value, uint mask)
    {
        if (mask == 0)
        {
            return 0f;
        }

        int shift = BitOperations.TrailingZeroCount(mask);
        return ((value & mask) >> shift) / (float)(mask >> shift);
    }

    private static Stored OfDxgi(int dxgi) => dxgi switch
    {
        2 => new Stored(Kind.Float4, 16, "R32G32B32A32_FLOAT"),
        6 => new Stored(Kind.Float3, 12, "R32G32B32_FLOAT"),
        10 => new Stored(Kind.Half4, 8, "R16G16B16A16_FLOAT"),
        11 => new Stored(Kind.Unorm16x4, 8, "R16G16B16A16_UNORM"),
        24 => new Stored(Kind.Rgb10A2, 4, "R10G10B10A2_UNORM"),
        26 => new Stored(Kind.Rg11B10, 4, "R11G11B10_FLOAT"),
        28 => new Stored(Kind.Rgba8, 4, "R8G8B8A8_UNORM"),
        29 => new Stored(Kind.Rgba8Srgb, 4, "R8G8B8A8_UNORM_SRGB"),
        67 => new Stored(Kind.Rgb9E5, 4, "R9G9B9E5_SHAREDEXP"),
        87 => new Stored(Kind.Bgra8, 4, "B8G8R8A8_UNORM"),
        88 => new Stored(Kind.Bgra8, 4, "B8G8R8X8_UNORM"),
        91 => new Stored(Kind.Bgra8Srgb, 4, "B8G8R8A8_UNORM_SRGB"),
        93 => new Stored(Kind.Bgra8Srgb, 4, "B8G8R8X8_UNORM_SRGB"),
        95 => new Stored(Kind.Bc6h, 0, "BC6H_UF16"),
        96 => new Stored(Kind.Bc6hSigned, 0, "BC6H_SF16"),
        _ => new Stored(Kind.Unread, 0, DxgiName(dxgi)),
    };

    /// <summary>A DXGI format's name where it is one a texture is commonly stored in, else its number.</summary>
    private static string DxgiName(int dxgi) => dxgi switch
    {
        71 => "BC1_UNORM",
        72 => "BC1_UNORM_SRGB",
        74 => "BC2_UNORM",
        75 => "BC2_UNORM_SRGB",
        77 => "BC3_UNORM",
        78 => "BC3_UNORM_SRGB",
        80 => "BC4_UNORM",
        83 => "BC5_UNORM",
        98 => "BC7_UNORM",
        99 => "BC7_UNORM_SRGB",
        _ => string.Create(CultureInfo.InvariantCulture, $"DXGI format {dxgi}"),
    };

    private static string FourCcText(byte[] dds)
    {
        ReadOnlySpan<byte> code = dds.AsSpan(84, 4);
        foreach (byte one in code)
        {
            if (one is < 32 or > 126)
            {
                return BitConverter.ToInt32(dds, 84).ToString(CultureInfo.InvariantCulture);
            }
        }

        return System.Text.Encoding.ASCII.GetString(code);
    }

    private enum Kind
    {
        Unread,
        Rgba8,
        Rgba8Srgb,
        Bgra8,
        Bgra8Srgb,
        Rgb10A2,
        Rg11B10,
        Rgb9E5,
        Half4,
        Unorm16x4,
        Float4,
        Float3,
        Masked,
        Bc6h,
        Bc6hSigned,
    }

    private readonly record struct Stored(Kind Kind, int Bytes, string Name);

    private readonly record struct Masks(uint Red, uint Green, uint Blue);
}
