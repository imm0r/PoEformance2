namespace PoEformance.Game.Files;

/// <summary>
/// Decodes BC6H blocks - the HDR block format the game stores its environment cube maps in.
/// </summary>
/// <remarks>
/// WHY THERE IS A DECODER HERE AT ALL. Pfim, which decodes every other texture, has no BC6H, and the
/// env survey says that is what the cubes are: 180 specular and 178 diffuse cubes, every one DXGI
/// format 95 (BC6H_UF16). The diffuse cube is an area's ambient light, so a picture that cannot read
/// it cannot be lit the game's way.
///
/// PORTED FROM bcdec (iOrange/bcdec, MIT or the Unlicense), <c>bcdec_bc6h_half</c> line for line in
/// what it does: the same fourteen modes, the same field order within each, the same unquantising
/// and the same 31/64 scale at the end. The modes are written here as tables of fields instead of
/// fourteen blocks of reads, which is the one structural change - each table is that mode's reads in
/// bcdec's order, so a mode can be checked against it field by field.
/// </remarks>
public static class Bc6h
{
    /// <summary>The bytes in one block of four by four texels.</summary>
    public const int BlockBytes = 16;

    /// <summary>
    /// Decodes one block into 48 floats: sixteen texels' red, green and blue, row by row.
    /// </summary>
    /// <param name="block">The block's sixteen bytes.</param>
    /// <param name="rgb">Where the texels go - 48 floats.</param>
    /// <param name="signed">True for BC6H_SF16, false for BC6H_UF16.</param>
    public static void Block(ReadOnlySpan<byte> block, Span<float> rgb, bool signed)
    {
        Span<ushort> halves = stackalloc ushort[48];
        Halves(block, halves, signed);
        for (int i = 0; i < 48; i++)
        {
            rgb[i] = (float)BitConverter.UInt16BitsToHalf(halves[i]);
        }
    }

    /// <summary>
    /// Decodes one block into 48 halves, as bcdec's <c>bcdec_bc6h_half</c> hands them out - what the tests hold against it.
    /// </summary>
    public static void Halves(ReadOnlySpan<byte> block, Span<ushort> rgb, bool signed)
    {
        var bits = new Bits(BitConverter.ToUInt64(block), BitConverter.ToUInt64(block[8..]));
        int mode = bits.Read(2);
        if (mode > 1)
        {
            mode |= bits.Read(3) << 2;
        }

        int index = mode switch
        {
            0b00 => 0,
            0b01 => 1,
            0b00010 => 2,
            0b00110 => 3,
            0b01010 => 4,
            0b01110 => 5,
            0b10010 => 6,
            0b10110 => 7,
            0b11010 => 8,
            0b11110 => 9,
            0b00011 => 10,
            0b00111 => 11,
            0b01011 => 12,
            0b01111 => 13,
            _ => -1,
        };

        // A RESERVED MODE IS BLACK - the format's own rule for hardware handed one.
        if (index < 0)
        {
            rgb[..48].Clear();
            return;
        }

        // Red, green, blue; each with its four endpoints w, x, y, z.
        Span<int> ends = stackalloc int[12];
        int partition = 0;
        foreach (Field field in Modes[index])
        {
            if (field.At == PartitionField)
            {
                partition = bits.Read(field.Count);
                continue;
            }

            int value = field.Reversed ? bits.Reversed(field.Count) : bits.Read(field.Count);
            ends[field.At] |= value << field.Shift;
        }

        int regions = index >= 10 ? 0 : 1;
        int wide = Widths[0][index];
        if (signed)
        {
            ends[0] = Extended(ends[0], wide);
            ends[4] = Extended(ends[4], wide);
            ends[8] = Extended(ends[8], wide);
        }

        int count = (regions + 1) * 2;
        if ((index != 9 && index != 10) || signed)
        {
            for (int i = 1; i < count; i++)
            {
                ends[i] = Extended(ends[i], Widths[1][index]);
                ends[4 + i] = Extended(ends[4 + i], Widths[2][index]);
                ends[8 + i] = Extended(ends[8 + i], Widths[3][index]);
            }
        }

        if (index != 9 && index != 10)
        {
            for (int i = 1; i < count; i++)
            {
                for (int channel = 0; channel < 3; channel++)
                {
                    ends[(channel * 4) + i] = Inverse(ends[(channel * 4) + i], ends[channel * 4], wide, signed);
                }
            }
        }

        for (int i = 0; i < count; i++)
        {
            for (int channel = 0; channel < 3; channel++)
            {
                ends[(channel * 4) + i] = Unquantised(ends[(channel * 4) + i], wide, signed);
            }
        }

        ReadOnlySpan<int> weights = index >= 10 ? Weights4 : Weights3;
        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 4; column++)
            {
                int set = index >= 10 ? ((row | column) != 0 ? 0 : 128) : Partitions[(partition * 16) + (row * 4) + column];
                int width = index >= 10 ? 4 : 3;

                // THE FIX-UP INDEX IS WRITTEN WITH A BIT FEWER, its top bit known to be nought.
                if ((set & 0x80) != 0)
                {
                    width--;
                }

                int end = (set & 1) * 2;
                int weight = weights[bits.Read(width)];
                int at = ((row * 4) + column) * 3;
                for (int channel = 0; channel < 3; channel++)
                {
                    int a = ends[(channel * 4) + end];
                    int b = ends[(channel * 4) + end + 1];
                    int mixed = ((a * (64 - weight)) + (b * weight) + 32) >> 6;
                    rgb[at + channel] = Finished(mixed, signed);
                }
            }
        }
    }

    /// <summary>The partition field's place among the endpoint fields.</summary>
    private const byte PartitionField = 12;

    /// <summary>Each mode's endpoint width, then its three delta widths - bcdec's actual_bits_count.</summary>
    private static readonly int[][] Widths =
    [
        [10, 7, 11, 11, 11, 9, 8, 8, 8, 6, 10, 11, 12, 16],
        [5, 6, 5, 4, 4, 5, 6, 5, 5, 6, 10, 9, 8, 4],
        [5, 6, 4, 5, 4, 5, 5, 6, 5, 6, 10, 9, 8, 4],
        [5, 6, 4, 4, 5, 5, 5, 5, 6, 6, 10, 9, 8, 4],
    ];

    private static readonly int[] Weights3 = [0, 9, 18, 27, 37, 46, 55, 64];

    private static readonly int[] Weights4 = [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];

    /// <summary>The 32 two-region shapes, each four rows of four; 128 marks a region's fix-up texel - bcdec's partition_sets.</summary>
    private static readonly byte[] Partitions =
    [
        128, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 129,
        128, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 129,
        128, 1, 1, 1, 0, 1, 1, 1, 0, 1, 1, 1, 0, 1, 1, 129,
        128, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 1, 1, 129,
        128, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 1, 129,
        128, 0, 1, 1, 0, 1, 1, 1, 0, 1, 1, 1, 1, 1, 1, 129,
        128, 0, 0, 1, 0, 0, 1, 1, 0, 1, 1, 1, 1, 1, 1, 129,
        128, 0, 0, 0, 0, 0, 0, 1, 0, 0, 1, 1, 0, 1, 1, 129,
        128, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 1, 129,
        128, 0, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 129,
        128, 0, 0, 0, 0, 0, 0, 1, 0, 1, 1, 1, 1, 1, 1, 129,
        128, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 1, 1, 129,
        128, 0, 0, 1, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 129,
        128, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 129,
        128, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 129,
        128, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 129,
        128, 0, 0, 0, 1, 0, 0, 0, 1, 1, 1, 0, 1, 1, 1, 129,
        128, 1, 129, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0,
        128, 0, 0, 0, 0, 0, 0, 0, 129, 0, 0, 0, 1, 1, 1, 0,
        128, 1, 129, 1, 0, 0, 1, 1, 0, 0, 0, 1, 0, 0, 0, 0,
        128, 0, 129, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0,
        128, 0, 0, 0, 1, 0, 0, 0, 129, 1, 0, 0, 1, 1, 1, 0,
        128, 0, 0, 0, 0, 0, 0, 0, 129, 0, 0, 0, 1, 1, 0, 0,
        128, 1, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 0, 129,
        128, 0, 129, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0,
        128, 0, 0, 0, 1, 0, 0, 0, 129, 0, 0, 0, 1, 1, 0, 0,
        128, 1, 129, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0,
        128, 0, 129, 1, 0, 1, 1, 0, 0, 1, 1, 0, 1, 1, 0, 0,
        128, 0, 0, 1, 0, 1, 1, 1, 129, 1, 1, 0, 1, 0, 0, 0,
        128, 0, 0, 0, 1, 1, 1, 1, 129, 1, 1, 1, 0, 0, 0, 0,
        128, 1, 129, 1, 0, 0, 0, 1, 1, 0, 0, 0, 1, 1, 1, 0,
        128, 0, 129, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 1, 0, 0,
    ];

    /// <summary>
    /// Each mode's fields in the order bcdec reads them: which endpoint, how many bits, how far up they go.
    /// </summary>
    private static readonly Field[][] Modes =
    [
        [G(2, 1, 4), B(2, 1, 4), B(3, 1, 4), R(0, 10), G(0, 10), B(0, 10), R(1, 5), G(3, 1, 4), G(2, 4), G(1, 5), B(3, 1), G(3, 4), B(1, 5), B(3, 1, 1), B(2, 4), R(2, 5), B(3, 1, 2), R(3, 5), B(3, 1, 3), P],
        [G(2, 1, 5), G(3, 1, 4), G(3, 1, 5), R(0, 7), B(3, 1), B(3, 1, 1), B(2, 1, 4), G(0, 7), B(2, 1, 5), B(3, 1, 2), G(2, 1, 4), B(0, 7), B(3, 1, 3), B(3, 1, 5), B(3, 1, 4), R(1, 6), G(2, 4), G(1, 6), G(3, 4), B(1, 6), B(2, 4), R(2, 6), R(3, 6), P],
        [R(0, 10), G(0, 10), B(0, 10), R(1, 5), R(0, 1, 10), G(2, 4), G(1, 4), G(0, 1, 10), B(3, 1), G(3, 4), B(1, 4), B(0, 1, 10), B(3, 1, 1), B(2, 4), R(2, 5), B(3, 1, 2), R(3, 5), B(3, 1, 3), P],
        [R(0, 10), G(0, 10), B(0, 10), R(1, 4), R(0, 1, 10), G(3, 1, 4), G(2, 4), G(1, 5), G(0, 1, 10), G(3, 4), B(1, 4), B(0, 1, 10), B(3, 1, 1), B(2, 4), R(2, 4), B(3, 1), B(3, 1, 2), R(3, 4), G(2, 1, 4), B(3, 1, 3), P],
        [R(0, 10), G(0, 10), B(0, 10), R(1, 4), R(0, 1, 10), B(2, 1, 4), G(2, 4), G(1, 4), G(0, 1, 10), B(3, 1), G(3, 4), B(1, 5), B(0, 1, 10), B(2, 4), R(2, 4), B(3, 1, 1), B(3, 1, 2), R(3, 4), B(3, 1, 4), B(3, 1, 3), P],
        [R(0, 9), B(2, 1, 4), G(0, 9), G(2, 1, 4), B(0, 9), B(3, 1, 4), R(1, 5), G(3, 1, 4), G(2, 4), G(1, 5), B(3, 1), G(3, 4), B(1, 5), B(3, 1, 1), B(2, 4), R(2, 5), B(3, 1, 2), R(3, 5), B(3, 1, 3), P],
        [R(0, 8), G(3, 1, 4), B(2, 1, 4), G(0, 8), B(3, 1, 2), G(2, 1, 4), B(0, 8), B(3, 1, 3), B(3, 1, 4), R(1, 6), G(2, 4), G(1, 5), B(3, 1), G(3, 4), B(1, 5), B(3, 1, 1), B(2, 4), R(2, 6), R(3, 6), P],
        [R(0, 8), B(3, 1), B(2, 1, 4), G(0, 8), G(2, 1, 5), G(2, 1, 4), B(0, 8), G(3, 1, 5), B(3, 1, 4), R(1, 5), G(3, 1, 4), G(2, 4), G(1, 6), G(3, 4), B(1, 5), B(3, 1, 1), B(2, 4), R(2, 5), B(3, 1, 2), R(3, 5), B(3, 1, 3), P],
        [R(0, 8), B(3, 1, 1), B(2, 1, 4), G(0, 8), B(2, 1, 5), G(2, 1, 4), B(0, 8), B(3, 1, 5), B(3, 1, 4), R(1, 5), G(3, 1, 4), G(2, 4), G(1, 5), B(3, 1), G(3, 4), B(1, 6), B(2, 4), R(2, 5), B(3, 1, 2), R(3, 5), B(3, 1, 3), P],
        [R(0, 6), G(3, 1, 4), B(3, 1), B(3, 1, 1), B(2, 1, 4), G(0, 6), G(2, 1, 5), B(2, 1, 5), B(3, 1, 2), G(2, 1, 4), B(0, 6), G(3, 1, 5), B(3, 1, 3), B(3, 1, 5), B(3, 1, 4), R(1, 6), G(2, 4), G(1, 6), G(3, 4), B(1, 6), B(2, 4), R(2, 6), R(3, 6), P],
        [R(0, 10), G(0, 10), B(0, 10), R(1, 10), G(1, 10), B(1, 10)],
        [R(0, 10), G(0, 10), B(0, 10), R(1, 9), R(0, 1, 10), G(1, 9), G(0, 1, 10), B(1, 9), B(0, 1, 10)],
        [R(0, 10), G(0, 10), B(0, 10), R(1, 8), R(0, 2, 10, true), G(1, 8), G(0, 2, 10, true), B(1, 8), B(0, 2, 10, true)],
        [R(0, 10), G(0, 10), B(0, 10), R(1, 4), R(0, 6, 10, true), G(1, 4), G(0, 6, 10, true), B(1, 4), B(0, 6, 10, true)],
    ];

    private static Field P => new(PartitionField, 5, 0, false);

    private static Field R(int end, int count, int shift = 0, bool reversed = false) => new((byte)end, (byte)count, (byte)shift, reversed);

    private static Field G(int end, int count, int shift = 0, bool reversed = false) => new((byte)(4 + end), (byte)count, (byte)shift, reversed);

    private static Field B(int end, int count, int shift = 0, bool reversed = false) => new((byte)(8 + end), (byte)count, (byte)shift, reversed);

    /// <summary>The low <paramref name="bits"/> bits of a value, sign-extended.</summary>
    private static int Extended(int value, int bits) => (value << (32 - bits)) >> (32 - bits);

    /// <summary>A delta endpoint back to an absolute one, wrapped to the base endpoint's width.</summary>
    private static int Inverse(int value, int a0, int bits, bool signed)
    {
        value = (value + a0) & ((1 << bits) - 1);
        return signed ? Extended(value, bits) : value;
    }

    /// <summary>An endpoint of <paramref name="bits"/> bits spread over sixteen - the format document's own arithmetic.</summary>
    private static int Unquantised(int value, int bits, bool signed)
    {
        if (!signed)
        {
            if (bits >= 15)
            {
                return value;
            }

            if (value == 0)
            {
                return 0;
            }

            return value == (1 << bits) - 1 ? 0xFFFF : ((value << 16) + 0x8000) >> bits;
        }

        if (bits >= 16)
        {
            return value;
        }

        bool negative = value < 0;
        value = Math.Abs(value);
        int spread = value == 0 ? 0 : value >= (1 << (bits - 1)) - 1 ? 0x7FFF : ((value << 15) + 0x4000) >> (bits - 1);
        return negative ? -spread : spread;
    }

    /// <summary>An interpolated value as the half it stands for.</summary>
    private static ushort Finished(int value, bool signed)
    {
        if (!signed)
        {
            return (ushort)((value * 31) >> 6);
        }

        value = value < 0 ? -(((-value) * 31) >> 5) : (value * 31) >> 5;
        return value < 0 ? (ushort)(0x8000 | -value) : (ushort)value;
    }

    /// <summary>One field of a mode: which endpoint (red 0-3, green 4-7, blue 8-11, or the partition), its bits, and where they go.</summary>
    private readonly record struct Field(byte At, byte Count, byte Shift, bool Reversed);

    /// <summary>The block's 128 bits, read from the bottom - bcdec's bitstream.</summary>
    private ref struct Bits(ulong low, ulong high)
    {
        private ulong _low = low;
        private ulong _high = high;

        public int Read(int count)
        {
            ulong mask = (1UL << count) - 1;
            int bits = (int)(_low & mask);
            _low >>= count;
            _low |= (_high & mask) << (64 - count);
            _high >>= count;
            return bits;
        }

        /// <summary>The same with the bits the other way round - the last modes' top endpoint bits are written so.</summary>
        public int Reversed(int count)
        {
            int bits = Read(count);
            int result = 0;
            for (int i = 0; i < count; i++)
            {
                result = (result << 1) | (bits & 1);
                bits >>= 1;
            }

            return result;
        }
    }
}
