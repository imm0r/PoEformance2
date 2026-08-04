using System.Runtime.InteropServices;
using PoEformance.Core.Memory;

namespace PoEformance.Core.Diagnostics;

/// <summary>One candidate location for a drifted world-to-screen matrix.</summary>
/// <param name="Offset">Byte offset from the scanned base where the matrix would start.</param>
/// <param name="Length">Length of the direction vector found at Offset+0x30 (~1.0 = a real basis row).</param>
/// <param name="X">Direction components, for eyeballing plausibility.</param>
/// <param name="Diagonality">
/// Smallest absolute component. A real camera forward points diagonally into the scene, so all
/// three components are meaningfully non-zero; an identity/basis row has a zero here.
/// </param>
public sealed record MatrixCandidate(int Offset, double Length, float X, float Y, float Z, double Diagonality)
{
    /// <summary>
    /// True when the vector is (near) axis-aligned - almost certainly a row of an identity or
    /// basis matrix rather than a camera. Memory is full of these, so they must not be offered
    /// as answers.
    /// </summary>
    public bool IsAxisAligned => Diagonality < AxisAlignedThreshold;

    /// <summary>Below this smallest-component value we treat a unit vector as axis-aligned.</summary>
    public const double AxisAlignedThreshold = 0.1;
}

/// <summary>
/// The drift scanner for the world-to-screen matrix: sweeps a byte window for offsets whose
/// row at +0x30 is a unit vector, then ranks them by how camera-like that vector is.
/// </summary>
/// <remarks>
/// Automates the hunt that recovered the matrix after its previous drift, where the true
/// offset was identified because its w-row direction is the camera forward axis - in PoE2's
/// isometric view roughly (0.467, 0.467, 0.751).
///
/// The first live run taught the crucial lesson: "unit vector" alone is far too weak a
/// signal. Memory contains many identity and basis matrices whose rows are perfect unit
/// vectors like (0,1,0). Those are ranked last and marked, because a camera forward is
/// DIAGONAL - every component non-zero. A scan that returns only axis-aligned hits means
/// "not found here", not "found it".
/// </remarks>
public static class MatrixScan
{
    /// <summary>Byte offset of the direction row inside a row-major 4x4 float matrix.</summary>
    private const int WRowOffset = 0x30;

    /// <summary>How close to 1.0 the vector length must be to count as a candidate.</summary>
    private const double Tolerance = 0.02;

    /// <summary>
    /// Scans <paramref name="baseAddress"/> + [<paramref name="start"/>..<paramref name="end"/>)
    /// and returns every offset whose row at +0x30 is unit-length. Camera-like (diagonal)
    /// candidates come first; axis-aligned ones are kept but ranked last and flagged.
    /// </summary>
    public static List<MatrixCandidate> Scan(IMemoryReader reader, ulong baseAddress, int start, int end)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var candidates = new List<MatrixCandidate>();
        if (baseAddress == 0 || end <= start)
        {
            return candidates;
        }

        // One buffer over the whole window; unreadable chunks stay zero (length 0 - never a
        // false candidate, since a zero vector is not unit-length).
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
            if (Math.Abs(length - 1.0) > Tolerance)
            {
                continue;
            }

            double diagonality = Math.Min(Math.Abs(x), Math.Min(Math.Abs(y), Math.Abs(z)));
            candidates.Add(new MatrixCandidate(start + offset, length, x, y, z, diagonality));
        }

        // Camera-like first (most diagonal), identity/basis rows last.
        candidates.Sort((a, b) => b.Diagonality.CompareTo(a.Diagonality));
        return candidates;
    }

    /// <summary>
    /// True when <paramref name="candidates"/> contains at least one plausible camera - i.e.
    /// a diagonal unit vector rather than only identity/basis rows.
    /// </summary>
    public static bool HasCameraLike(IEnumerable<MatrixCandidate> candidates)
        => candidates.Any(c => !c.IsAxisAligned);

    /// <summary>
    /// Reads the full 4x4 float matrix at <paramref name="baseAddress"/> +
    /// <paramref name="offset"/>, or null when unreadable.
    /// </summary>
    /// <remarks>
    /// A unit direction row proves only that SOMETHING structured lives there - a rotation
    /// matrix has one too. The full matrix distinguishes the cases: a world-to-screen matrix
    /// carries a translation row and non-unit scaling, while a pure rotation has four
    /// unit-length, mutually orthogonal rows and no translation. That difference is what
    /// picks the right candidate when several look camera-like.
    /// </remarks>
    public static float[]? ReadMatrix(IMemoryReader reader, ulong baseAddress, int offset)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var bytes = new byte[64];
        if (!reader.TryRead(baseAddress + (ulong)offset, bytes))
        {
            return null;
        }

        return MemoryMarshal.Cast<byte, float>(bytes).ToArray();
    }

    /// <summary>
    /// Classifies a 4x4 matrix for the report: a pure rotation (all rows unit length, no
    /// translation) is almost never the matrix we want; a projection carries scale and
    /// translation.
    /// </summary>
    public static string Describe(float[] m)
    {
        ArgumentNullException.ThrowIfNull(m);
        if (m.Length < 16)
        {
            return "too short";
        }

        static double RowLength(float[] v, int row)
            => Math.Sqrt((double)v[row * 4] * v[row * 4]
                + (double)v[(row * 4) + 1] * v[(row * 4) + 1]
                + (double)v[(row * 4) + 2] * v[(row * 4) + 2]);

        // A zero row disqualifies outright: real transforms have none. The first live dump
        // showed two candidates that were plainly not matrices (zero rows, last column
        // 0,0,1,0) yet were being called "projection-like" - hence this check comes first.
        for (int row = 0; row < 4; row++)
        {
            if (RowLength(m, row) < 1e-6)
            {
                return "contains a zero row - not a matrix, misaligned read";
            }
        }

        // The real discriminator is the last COLUMN: a camera transform carries world-scale
        // translation there (thousands of units), while a stray basis block has small values.
        double translation = Math.Abs(m[3]) + Math.Abs(m[7]) + Math.Abs(m[11]) + Math.Abs(m[15]);
        if (translation < 10.0)
        {
            return "last column is not world-scale translation - probably a basis block, not a camera";
        }

        int unitRows = 0;
        for (int row = 0; row < 4; row++)
        {
            if (Math.Abs(RowLength(m, row) - 1.0) <= 0.05)
            {
                unitRows++;
            }
        }

        return unitRows == 4
            ? "4 unit rows + world-scale translation - a rigid camera transform, this is the one"
            : $"{unitRows}/4 unit rows + world-scale translation - projection-like";
    }
}
