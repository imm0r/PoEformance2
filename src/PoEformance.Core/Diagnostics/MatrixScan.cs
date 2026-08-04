using System.Runtime.InteropServices;
using PoEformance.Core.Memory;

namespace PoEformance.Core.Diagnostics;

/// <summary>One candidate location for a drifted world-to-screen matrix.</summary>
/// <param name="Offset">Byte offset from the scanned base where the matrix would start.</param>
/// <param name="Length">Length of the w-row direction vector found at Offset+0x30 (~1.0 = real camera forward).</param>
/// <param name="X">w-row direction components, for eyeballing plausibility.</param>
public sealed record MatrixCandidate(int Offset, double Length, float X, float Y, float Z);

/// <summary>
/// The drift scanner for the world-to-screen matrix: sweeps a byte window for offsets
/// where the three floats at +0x30 form a unit-length vector - the signature of a real
/// camera matrix's w-row direction (the camera forward axis).
/// </summary>
/// <remarks>
/// This automates the exact hunt that recovered the matrix after its last drift: back
/// then a hand-written scan swept WorldData and found the new location because the
/// w-row direction at the true offset is a unit vector ((0.467, 0.467, 0.751) in-game)
/// while every misaligned read puts a huge translation value into one of the slots.
/// A duplicate candidate a few slots away is normal (the game keeps several copies);
/// any of them projects correctly.
///
/// The scan reads the window in chunks (tolerating unreadable pages) and tests every
/// 4-byte-aligned offset, so one call costs a handful of reads, not thousands.
/// </remarks>
public static class MatrixScan
{
    /// <summary>Byte offset of the w-row direction inside a row-major 4x4 float matrix.</summary>
    private const int WRowOffset = 0x30;

    /// <summary>How close to 1.0 the vector length must be to count as a candidate.</summary>
    private const double Tolerance = 0.02;

    /// <summary>
    /// Scans <paramref name="baseAddress"/> + [<paramref name="start"/>..<paramref name="end"/>)
    /// and returns every offset whose w-row is unit-length, ordered by closeness to 1.0.
    /// </summary>
    public static List<MatrixCandidate> Scan(IMemoryReader reader, ulong baseAddress, int start, int end)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var candidates = new List<MatrixCandidate>();
        if (baseAddress == 0 || end <= start)
        {
            return candidates;
        }

        // One buffer over the whole window; unreadable chunks stay zero (length 0 - no
        // false candidates, a zero vector is never unit-length).
        int size = end - start + WRowOffset + 12;
        var buffer = new byte[size];
        const int chunk = 0x100;
        for (int position = 0; position < size; position += chunk)
        {
            int take = Math.Min(chunk, size - position);
            reader.TryRead(baseAddress + (ulong)(start + position), buffer.AsSpan(position, take));
        }

        ReadOnlySpan<float> floats = MemoryMarshal.Cast<byte, float>(buffer);
        for (int offset = 0; offset <= end - start; offset += 4)
        {
            int index = (offset + WRowOffset) / 4;
            if (index + 2 >= floats.Length)
            {
                break;
            }

            float x = floats[index], y = floats[index + 1], z = floats[index + 2];
            double length = Math.Sqrt((double)x * x + (double)y * y + (double)z * z);
            if (Math.Abs(length - 1.0) <= Tolerance)
            {
                candidates.Add(new MatrixCandidate(start + offset, length, x, y, z));
            }
        }

        candidates.Sort((a, b) => Math.Abs(a.Length - 1.0).CompareTo(Math.Abs(b.Length - 1.0)));
        return candidates;
    }
}
