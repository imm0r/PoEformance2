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
/// <param name="Linearity">
/// How well the projected scene fits a plane through world x and y (worst of the two screen
/// axes, 0..1). PoE's camera is a FIXED isometric angle over near-flat ground, so the real
/// matrix maps the world almost perfectly linearly; unrelated blocks do not.
/// </param>
/// <param name="DepthResponse">
/// How much the projection moves when world z changes. A real 3D projection must respond to
/// height; a flat 2D transform (a minimap, say) ignores it entirely.
/// </param>
public sealed record ProjectionCandidate(
    int Offset,
    bool Transposed,
    double PlayerOffCentre,
    double Spread,
    double PlayerW,
    double OnScreenFraction,
    double Linearity,
    double DepthResponse)
{
    /// <summary>
    /// Ranking score, led by <see cref="Linearity"/>.
    /// </summary>
    /// <remarks>
    /// Spread used to lead this, and that was wrong: it rewards a matrix for scattering
    /// points, which several unrelated blocks do better than the real one. Against a live
    /// scene the fixed-camera linearity separated them cleanly where spread did not - the
    /// true matrix fitted at 0.94 while the best-spreading decoy managed 0.73 and another
    /// that centred the player perfectly scored 0.00, because its screen x was constant.
    /// Spread and w are kept as hard admission tests, not as ranking.
    /// </remarks>
    public double Score => Linearity + (0.25 * (1.0 - Math.Min(PlayerOffCentre, 1.0))) + (0.1 * OnScreenFraction);
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
/// player, a correct matrix must do three things at once:
///   1. put the player at the centre,
///   2. spread the other entities out in proportion to their world distance, and
///   3. move a point on screen when its world HEIGHT changes.
/// A collapsed matrix passes (1) and fails (2). A shifted or transposed read usually passes
/// (2) and fails (1). A flat 2D transform can pass both and still fail (3) - and (3) is the
/// one the tool actually needs, since a marker over an entity's head is placed by projecting
/// its height. All three are hard admission tests; only the ranking among survivors is a
/// score, and it is led by linearity.
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
    /// Raising a point by a character's height must move it on screen by at least this much.
    /// </summary>
    /// <remarks>
    /// THIS WAS COMPUTED AND PRINTED FOR MONTHS WITHOUT BEING USED, and a live run showed what
    /// that cost. The hunt reported "BEST: W2SMatrix = 0x2A8" against a schema offset the same
    /// run had just proven, and the winner's depth response was 0.000 - it ignores world z
    /// entirely. It won on off-centre alone: 0.0044 against the real matrix's 0.1773, worth
    /// 0.0432 of score, while linearity actually favoured the real one by 0.0174. That perfect
    /// centring is the documented failure mode rather than a merit, and its cause is visible
    /// in the same row: w of 480,696 against the real 2,114, which divides every numerator
    /// down to nothing. Spread agrees - 0.826 against 1.778 - so three columns called it and
    /// the ranking still did not.
    ///
    /// Depth is the right test to promote because it is the one the tool's purpose depends on.
    /// A marker over an entity's head is placed by projecting Z - ModelBounds.Z, so a matrix
    /// that does not respond to height cannot do the job it is being chosen for, however
    /// neatly it centres the player. The separation is not marginal either: every rejected
    /// candidate in that run scored exactly 0.000 while the real matrix scored 0.119, so this
    /// threshold sits an order of magnitude below the true value and two above the decoys.
    /// </remarks>
    private const double MinDepthResponse = 0.01;

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
        if (worldData == 0 || snapshot.Player is not WorldEntity player)
        {
            return results;
        }

        // ONLY WHAT THE GAME IS LISTING NOW. A snapshot also carries the places it has stopped
        // listing, and every one of those is by definition far outside the bubble and so off
        // the screen - which is precisely what one half of the score measures. Scoring a
        // CORRECT matrix against them would count its off-screen fraction against it, in
        // proportion to how much of the map has been walked. A hunt that gets worse the longer
        // you play is the shape of check this file exists to avoid.
        List<WorldEntity> scene = [.. snapshot.Listed];
        if (scene.Count < 2)
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
                ProjectionCandidate? candidate = Evaluate(matrix, transposed, start + offset, player, scene);
                if (candidate is not null)
                {
                    results.Add(candidate);
                }
            }
        }

        // Ties broken by offset, so the answer does not depend on the sort's internals. The
        // scene puts two IDENTICAL matrices 0x40 apart - 0x1A0 and 0x1E0, the same sixteen
        // floats twice - and List.Sort is not stable, so which one the report named as best
        // was a coin flip between runs. Reporting a different offset each time from the same
        // memory is worse than reporting an arbitrary one of two equals.
        results.Sort((a, b) =>
        {
            int byScore = b.Score.CompareTo(a.Score);
            return byScore != 0 ? byScore : a.Offset.CompareTo(b.Offset);
        });
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
        int onScreen = 0;
        var worldX = new List<double>(entities.Count);
        var worldY = new List<double>(entities.Count);
        var screenX = new List<double>(entities.Count);
        var screenY = new List<double>(entities.Count);

        foreach (WorldEntity entity in entities)
        {
            (double ex, double ey, double ew) = Clip(matrix, transposed, entity.WorldX, entity.WorldY, entity.WorldZ);
            if (ew <= MinW)
            {
                continue;
            }

            double nx = ex / ew, ny = ey / ew;
            worldX.Add(entity.WorldX); worldY.Add(entity.WorldY);
            screenX.Add(nx); screenY.Add(ny);
            minX = Math.Min(minX, nx); maxX = Math.Max(maxX, nx);
            minY = Math.Min(minY, ny); maxY = Math.Max(maxY, ny);
            if (Math.Abs(nx) <= 1 && Math.Abs(ny) <= 1)
            {
                onScreen++;
            }
        }

        int projected = screenX.Count;
        if (projected < 2)
        {
            return null;
        }

        double spread = Math.Max(maxX - minX, maxY - minY);
        if (spread < MinSpread)
        {
            return null;
        }

        // A projection must respond to height. Re-project the player a character's height up
        // and see whether anything moves - a flat 2D transform will not budge.
        (double hx, double hy, double hw) = Clip(matrix, transposed, player.WorldX, player.WorldY, player.WorldZ + 100f);
        double depthResponse = hw > MinW
            ? Math.Abs((hx / hw) - playerX) + Math.Abs((hy / hw) - playerY)
            : 0;

        if (depthResponse < MinDepthResponse)
        {
            return null;
        }

        double linearity = projected >= 8
            ? Math.Min(FitQuality(worldX, worldY, screenX), FitQuality(worldX, worldY, screenY))
            : 0;

        return new ProjectionCandidate(
            offset, transposed, offCentre, spread, pw, onScreen / (double)projected, linearity, depthResponse);
    }

    /// <summary>
    /// R-squared of a least-squares plane fitting one screen axis to world x and y.
    /// </summary>
    /// <remarks>
    /// This is the discriminator that actually works on real data, and it comes straight
    /// from how the game's camera behaves: the angle is FIXED and the ground is near-flat,
    /// so world x/y map onto the screen almost linearly. A decoy block reaches ~0.7 at best,
    /// and one whose screen x is constant scores 0 - which is exactly how it managed to
    /// "centre" the player. Taking the worse of the two axes is what catches that case,
    /// since a block can easily be linear in one axis and meaningless in the other.
    /// </remarks>
    private static double FitQuality(List<double> x1, List<double> x2, List<double> y)
    {
        int n = y.Count;
        double sx1 = 0, sx2 = 0, sy = 0;
        for (int i = 0; i < n; i++)
        {
            sx1 += x1[i]; sx2 += x2[i]; sy += y[i];
        }

        double mx1 = sx1 / n, mx2 = sx2 / n, my = sy / n;

        double s11 = 0, s22 = 0, s12 = 0, s1y = 0, s2y = 0, syy = 0;
        for (int i = 0; i < n; i++)
        {
            double d1 = x1[i] - mx1, d2 = x2[i] - mx2, dy = y[i] - my;
            s11 += d1 * d1; s22 += d2 * d2; s12 += d1 * d2;
            s1y += d1 * dy; s2y += d2 * dy; syy += dy * dy;
        }

        double determinant = (s11 * s22) - (s12 * s12);
        if (Math.Abs(determinant) < 1e-12 || syy < 1e-12)
        {
            return 0; // degenerate: the screen axis does not vary at all
        }

        double a = ((s22 * s1y) - (s12 * s2y)) / determinant;
        double b = ((s11 * s2y) - (s12 * s1y)) / determinant;

        double residual = 0;
        for (int i = 0; i < n; i++)
        {
            double predicted = my + (a * (x1[i] - mx1)) + (b * (x2[i] - mx2));
            residual += (y[i] - predicted) * (y[i] - predicted);
        }

        return Math.Max(0, 1.0 - (residual / syy));
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

        output.WriteLine("  offset  layout      w    off-centre   spread  on-screen  linearity  depth");
        foreach (ProjectionCandidate c in candidates.Take(8))
        {
            output.WriteLine(
                $"  +0x{c.Offset:X3}  {(c.Transposed ? "transposed" : "direct    ")} "
                + $"{c.PlayerW,8:F1}  {c.PlayerOffCentre,8:F4} {c.Spread,8:F3}  {c.OnScreenFraction,7:P0}"
                + $"  {c.Linearity,9:F4}  {c.DepthResponse,5:F3}"
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
