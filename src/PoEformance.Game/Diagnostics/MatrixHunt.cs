using PoEformance.Core.Memory;
using PoEformance.Game.World;

namespace PoEformance.Game.Diagnostics;

/// <summary>One candidate world-to-screen matrix, scored against the live scene.</summary>
/// <param name="Offset">Byte offset from WorldData where the 4x4 float matrix starts.</param>
/// <param name="Transposed">False for the game's convention, true for its transpose.</param>
/// <param name="PlayerOffCentre">How far the player lands from the screen centre, in NDC.</param>
/// <param name="Spread">NDC extent the rest of the scene occupies - the anti-collapse test.</param>
/// <param name="PlayerW">The player's clip w: the camera distance, for plausibility.</param>
/// <param name="OnScreenFraction">Fraction of entities landing inside the viewport.</param>
public sealed record ProjectionCandidate(
    int Offset,
    bool Transposed,
    double PlayerOffCentre,
    double Spread,
    double PlayerW,
    double OnScreenFraction)
{
    /// <summary>
    /// Ranking score. Spread is what a wrong matrix cannot fake, so it leads; being centred
    /// and having most of the scene on screen refine the order.
    /// </summary>
    public double Score => (Math.Min(Spread, 4.0) * (1.0 - Math.Min(PlayerOffCentre, 1.0))) + OnScreenFraction;
}

/// <summary>
/// Finds the world-to-screen matrix by testing candidates against the REAL scene instead of
/// against a byte pattern.
/// </summary>
/// <remarks>
/// This exists because pattern-matching the matrix failed in a way that is worth
/// remembering. The old check looked for a unit-length direction vector at a fixed spot
/// inside the matrix - but a unit vector proves only that something structured lives there,
/// and a block of frustum planes looks identical. Worse, the projection check that was
/// supposed to catch the mistake ("does the player land at the screen centre?") PASSES
/// trivially for a wrong matrix: if the matrix blows w up to millions, every numerator
/// divided by it collapses to ~0, so everything - including the player - lands dead centre.
///
/// So the decisive test is not one point, it is the SCENE. With the camera following the
/// player, a correct matrix must do two things at once:
///   1. put the player at the centre, and
///   2. spread the other entities out in proportion to their world distance.
/// A collapsed matrix passes (1) and fails (2). A shifted or transposed read usually passes
/// (2) and fails (1). Only the real matrix does both, which makes this test decisive where
/// the single-point check was not.
/// </remarks>
public static class MatrixHunt
{
    /// <summary>Clip w below this is degenerate; above it, implausible as a camera distance.</summary>
    private const double MinW = 1.0;
    private const double MaxW = 1_000_000.0;

    /// <summary>The player must land at least this close to the centre, in NDC units.</summary>
    private const double MaxPlayerOffCentre = 0.35;

    /// <summary>The scene must occupy at least this much NDC, or the matrix collapsed it.</summary>
    private const double MinSpread = 0.05;

    /// <summary>
    /// Scans <paramref name="worldData"/> + [<paramref name="start"/>..<paramref name="end"/>)
    /// for matrices that project <paramref name="snapshot"/> correctly. Best candidate first.
    /// </summary>
    /// <remarks>
    /// Reading the window in one sweep is deliberate: it also means a <c>--record</c> session
    /// CAPTURES the window, so the same hunt can be re-run offline against the recording.
    /// </remarks>
    public static List<ProjectionCandidate> Find(
        IMemoryReader reader, ulong worldData, WorldSnapshot snapshot, int start = 0, int end = 0x800)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(snapshot);

        var results = new List<ProjectionCandidate>();
        if (worldData == 0 || snapshot.Player is not WorldEntity player || snapshot.Entities.Count < 2)
        {
            return results;
        }

        // One buffer over the whole window; unreadable chunks stay zero and simply never
        // produce a candidate (a zero matrix has no spread).
        int size = end - start + 64;
        var window = new byte[size];
        const int chunk = 0x100;
        for (int position = 0; position < size; position += chunk)
        {
            reader.TryRead(worldData + (ulong)(start + position), window.AsSpan(position, Math.Min(chunk, size - position)));
        }

        var matrix = new float[16];
        for (int offset = 0; offset + 64 <= size; offset += 4)
        {
            Buffer.BlockCopy(window, offset, matrix, 0, 64);

            foreach (bool transposed in (ReadOnlySpan<bool>)[false, true])
            {
                ProjectionCandidate? candidate = Evaluate(matrix, transposed, start + offset, player, snapshot.Entities);
                if (candidate is not null)
                {
                    results.Add(candidate);
                }
            }
        }

        results.Sort((a, b) => b.Score.CompareTo(a.Score));
        return results;
    }

    /// <summary>Scores one matrix against the scene, or null if it fails a hard test.</summary>
    private static ProjectionCandidate? Evaluate(
        float[] matrix, bool transposed, int offset, WorldEntity player, IReadOnlyList<WorldEntity> entities)
    {
        (double px, double py, double pw) = Clip(matrix, transposed, player.WorldX, player.WorldY, player.WorldZ);
        if (pw is <= MinW or > MaxW)
        {
            return null;
        }

        double playerX = px / pw, playerY = py / pw;
        double offCentre = Math.Sqrt((playerX * playerX) + (playerY * playerY));
        if (offCentre > MaxPlayerOffCentre)
        {
            return null;
        }

        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        int onScreen = 0, projected = 0;

        foreach (WorldEntity entity in entities)
        {
            (double ex, double ey, double ew) = Clip(matrix, transposed, entity.WorldX, entity.WorldY, entity.WorldZ);
            if (ew <= MinW)
            {
                continue;
            }

            double nx = ex / ew, ny = ey / ew;
            projected++;
            minX = Math.Min(minX, nx); maxX = Math.Max(maxX, nx);
            minY = Math.Min(minY, ny); maxY = Math.Max(maxY, ny);
            if (Math.Abs(nx) <= 1 && Math.Abs(ny) <= 1)
            {
                onScreen++;
            }
        }

        if (projected < 2)
        {
            return null;
        }

        double spread = Math.Max(maxX - minX, maxY - minY);
        return spread < MinSpread
            ? null
            : new ProjectionCandidate(offset, transposed, offCentre, spread, pw, onScreen / (double)projected);
    }

    /// <summary>
    /// Clip coordinates for a world point.
    /// </summary>
    /// <remarks>
    /// The game's convention (matching the AHK tool's NavProject and GameHelper2's
    /// Vector4.Transform) takes each clip component as a dot with a COLUMN of the flat
    /// array: x from {0,4,8,12}, y from {1,5,9,13}, w from {3,7,11,15}. The transposed
    /// variant is tested too because a matrix stored the other way round still spreads the
    /// scene correctly, and telling the two apart by eye is exactly what went wrong before.
    /// </remarks>
    public static (double X, double Y, double W) Clip(float[] m, bool transposed, float x, float y, float z)
    {
        ArgumentNullException.ThrowIfNull(m);
        return transposed
            ? (((double)m[0] * x) + ((double)m[1] * y) + ((double)m[2] * z) + m[3],
               ((double)m[4] * x) + ((double)m[5] * y) + ((double)m[6] * z) + m[7],
               ((double)m[12] * x) + ((double)m[13] * y) + ((double)m[14] * z) + m[15])
            : (((double)m[0] * x) + ((double)m[4] * y) + ((double)m[8] * z) + m[12],
               ((double)m[1] * x) + ((double)m[5] * y) + ((double)m[9] * z) + m[13],
               ((double)m[3] * x) + ((double)m[7] * y) + ((double)m[11] * z) + m[15]);
    }

    /// <summary>Writes a human-readable hunt report.</summary>
    public static void Report(IReadOnlyList<ProjectionCandidate> candidates, int currentOffset, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(output);

        output.WriteLine();
        output.WriteLine("matrix hunt (candidates scored against the live scene)");
        if (candidates.Count == 0)
        {
            output.WriteLine("  none - no offset in the scanned window both centres the player AND spreads the");
            output.WriteLine("  scene. Widen the scan, or the matrix is not reached from WorldData.");
            return;
        }

        output.WriteLine("  offset  layout      w    off-centre   spread  on-screen");
        foreach (ProjectionCandidate c in candidates.Take(8))
        {
            output.WriteLine(
                $"  +0x{c.Offset:X3}  {(c.Transposed ? "transposed" : "direct    ")} "
                + $"{c.PlayerW,8:F1}  {c.PlayerOffCentre,8:F4} {c.Spread,8:F3}  {c.OnScreenFraction,7:P0}"
                + (c.Offset == currentOffset ? "   <- schema" : string.Empty));
        }

        ProjectionCandidate best = candidates[0];
        output.WriteLine();
        output.WriteLine(best.Offset == currentOffset && !best.Transposed
            ? $"  the schema offset 0x{currentOffset:X} is the best candidate."
            : $"  BEST: W2SMatrix = 0x{best.Offset:X}"
              + (best.Transposed ? " READ TRANSPOSED (the projection convention needs flipping)." : "."));
    }
}
