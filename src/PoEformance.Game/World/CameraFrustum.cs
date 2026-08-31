using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Game.World;

/// <summary>
/// One face of the view frustum: a unit normal and the plane's distance.
/// </summary>
/// <remarks>
/// The normals point INWARD, so a point is on the visible side of a face when
/// <see cref="Distance"/> is not negative. That convention is the game's, measured rather
/// than assumed: every corner of the frustum reads a distance of zero or above against all
/// six faces.
/// </remarks>
public readonly record struct FrustumPlane(float A, float B, float C, float D)
{
    /// <summary>Signed distance from the plane; positive is inside.</summary>
    public float Distance(float x, float y, float z) => (A * x) + (B * y) + (C * z) + D;

    /// <summary>Length of the normal, which is 1 for a real plane.</summary>
    public float NormalLength => MathF.Sqrt((A * A) + (B * B) + (C * C));
}

/// <summary>
/// The camera's view frustum, as the game itself keeps it: six clipping planes and the eight
/// corners they meet at, in WORLD coordinates.
/// </summary>
/// <remarks>
/// This is the game's own answer to "is that thing on screen", and it costs six multiply-adds
/// rather than a projection - no matrix, no viewport, no letterbox correction. It sits
/// immediately before the world-to-screen matrix in WorldData, which is not a coincidence and
/// is worth knowing for a second reason: reading a 4x4 matrix sixteen bytes into the plane
/// array is what defeated this project's first matrix invariant. See the schema.
///
/// TWO THINGS IT IS NOT. It is the 3D camera, so it answers "inside the view volume" and NOT
/// "not covered by the inventory panel" - that is <see cref="Ui.PanelReader"/>'s question. And
/// it is not a substitute for the projection: it says whether, not where.
/// </remarks>
public sealed class CameraFrustum
{
    /// <summary>Faces in the game's own order; 4 and 5 are the near and far pair.</summary>
    public const int PlaneCount = 6;

    /// <summary>The corners of the view volume - four on the near face, four on the far.</summary>
    public const int CornerCount = 8;

    private readonly FrustumPlane[] _planes;
    private readonly (float X, float Y, float Z)[] _corners;

    private CameraFrustum(FrustumPlane[] planes, (float X, float Y, float Z)[] corners)
    {
        _planes = planes;
        _corners = corners;
    }

    /// <summary>The six clipping planes, normals pointing inward.</summary>
    public IReadOnlyList<FrustumPlane> Planes => _planes;

    /// <summary>The eight world points where three faces meet.</summary>
    public IReadOnlyList<(float X, float Y, float Z)> Corners => _corners;

    /// <summary>
    /// Which way the camera looks: the near plane's normal.
    /// </summary>
    /// <remarks>
    /// Free here, and a second opinion on what <see cref="ScreenBasis"/> derives the long way
    /// round from the matrix. The two are independent, so they disagreeing is a drift alarm
    /// rather than a puzzle.
    /// </remarks>
    public (float X, float Y, float Z) ViewDirection => (_planes[4].A, _planes[4].B, _planes[4].C);

    /// <summary>How deep the view volume is, in world units.</summary>
    public float Depth => _planes[4].D + _planes[5].D;

    /// <summary>Whether a world point is inside all six faces.</summary>
    public bool Contains(float x, float y, float z) => Margin(x, y, z) >= 0;

    /// <summary>
    /// Distance to the nearest face: positive inside, negative outside by that much.
    /// </summary>
    /// <remarks>
    /// The signed form rather than a bool because "how far outside" is the useful half for
    /// anything drawing an edge marker, and because a caller wanting a margin - keep reading
    /// a monster that is a step off the screen - can ask for one instead of hard-clipping.
    /// </remarks>
    public float Margin(float x, float y, float z)
    {
        float worst = float.MaxValue;
        foreach (FrustumPlane plane in _planes)
        {
            worst = MathF.Min(worst, plane.Distance(x, y, z));
        }

        return worst;
    }

    /// <summary>
    /// Reads the frustum out of WorldData, or null when it is not there or not plausible.
    /// </summary>
    /// <remarks>
    /// One read: the corners and the planes are contiguous, so the whole block comes over in
    /// a single call next to the matrix's own.
    ///
    /// The validity check is that all six normals are unit length, and it is worth being
    /// precise about what that is doing, because this project has a scar from unit length
    /// being used for something it cannot do. It cannot IDENTIFY the block - plenty of
    /// unrelated float triples are unit length, which is exactly how the matrix decoy passed.
    /// Here the offset is already known and the only question is whether this read landed on
    /// real data, which six unit normals in a row answers perfectly well: a garbage read does
    /// not produce them, and returning null beats handing back a frustum that culls the world.
    /// </remarks>
    public static CameraFrustum? Read(IMemoryReader reader, OffsetSchema schema, ulong worldData)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);

        StructDef world = schema.Structs["WorldData"];
        int cornersAt = world.OffsetOf("FrustumCorners");
        int planesAt = world.OffsetOf("FrustumPlanes");
        return Read(reader, worldData, cornersAt, planesAt);
    }

    /// <summary>Reads the frustum from explicit offsets, for the hunt and for tests.</summary>
    public static CameraFrustum? Read(IMemoryReader reader, ulong worldData, int cornersAt, int planesAt)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (worldData == 0)
        {
            return null;
        }

        int from = Math.Min(cornersAt, planesAt);
        int to = Math.Max(cornersAt + (CornerCount * 12), planesAt + (PlaneCount * 16));

        // The two offsets are schema data and this is a stack buffer, so the span is bounded
        // rather than trusted: 0xC0 covers the block the game actually has, and an edit that
        // put the two halves a megabyte apart should fail here rather than in the stack.
        if (from < 0 || to <= from || to - from > 0x400)
        {
            return null;
        }

        Span<byte> block = stackalloc byte[to - from];
        if (!reader.TryRead(worldData + (ulong)from, block))
        {
            return null;
        }

        var planes = new FrustumPlane[PlaneCount];
        for (int i = 0; i < PlaneCount; i++)
        {
            int at = planesAt - from + (i * 16);
            planes[i] = new FrustumPlane(
                BitConverter.ToSingle(block[at..]), BitConverter.ToSingle(block[(at + 4)..]),
                BitConverter.ToSingle(block[(at + 8)..]), BitConverter.ToSingle(block[(at + 12)..]));

            if (MathF.Abs(planes[i].NormalLength - 1f) > 1e-3f)
            {
                return null;
            }
        }

        var corners = new (float, float, float)[CornerCount];
        for (int i = 0; i < CornerCount; i++)
        {
            int at = cornersAt - from + (i * 12);
            corners[i] = (BitConverter.ToSingle(block[at..]), BitConverter.ToSingle(block[(at + 4)..]),
                BitConverter.ToSingle(block[(at + 8)..]));
        }

        return new CameraFrustum(planes, corners);
    }

    /// <summary>
    /// How far the corners land from the edge of the screen when projected - 0 when the
    /// frustum and the matrix describe the same camera.
    /// </summary>
    /// <remarks>
    /// THE CHECK THIS TYPE EXISTS TO MAKE POSSIBLE, and the sharpest one this project has on
    /// the projection. The eight corners of the view volume ARE the eight corners of the
    /// viewport, so a correct matrix must send every one of them to an edge of normalised
    /// device space: |x| = 1 or |y| = 1. Measured over the eighteen committed recordings it
    /// comes back 0.0000 in every one.
    ///
    /// What makes it worth more than the checks that failed before is that it needs no scene
    /// and no judgement. "Is the player centred" passes for a matrix that collapses the world;
    /// "do the entities spread out" needs entities and a threshold argued over. This is two
    /// independent descriptions of one camera, read from two places, agreeing to the float -
    /// and a matrix that has drifted by so much as four bytes does not agree at all.
    ///
    /// Returns <see cref="double.PositiveInfinity"/> when a corner lands behind the camera,
    /// which is itself a disagreement.
    /// </remarks>
    public double WorstCornerOffEdge(ReadOnlySpan<float> matrix)
    {
        if (matrix.Length < 16)
        {
            return double.PositiveInfinity;
        }

        double worst = 0;
        foreach ((float x, float y, float z) in _corners)
        {
            (double cx, double cy, double cw) = WorldToScreen.Clip(matrix, x, y, z);
            if (cw <= 1e-6)
            {
                return double.PositiveInfinity;
            }

            double ndcX = cx / cw, ndcY = cy / cw;
            double offEdge = Math.Min(Math.Abs(Math.Abs(ndcX) - 1), Math.Abs(Math.Abs(ndcY) - 1));
            worst = Math.Max(worst, offEdge);
        }

        return worst;
    }

    /// <summary>
    /// True when the matrix and the frustum agree closely enough to call it one camera.
    /// </summary>
    /// <remarks>
    /// The tolerance is generous against a measurement that reads zero, deliberately: the
    /// question is "did one of the two drift", and drift moves a corner right off the screen
    /// rather than a hundredth of the way towards its edge. A tight bound would turn a future
    /// float rounding difference into a false alarm about the thing this is meant to protect.
    /// </remarks>
    public bool AgreesWith(ReadOnlySpan<float> matrix) => WorstCornerOffEdge(matrix) < 0.01;
}
