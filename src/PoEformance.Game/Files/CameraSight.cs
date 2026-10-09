using System.Numerics;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Game.Files;

/// <summary>
/// The game's camera as the overlay last read it: its matrix and where the player stood under it.
/// </summary>
/// <param name="Matrix">The world-to-clip matrix, the 16 floats WorldToScreen reads - a copy, so a later frame cannot change it.</param>
/// <param name="Player">The player's world position the camera was following when the matrix was read.</param>
public sealed record CameraShot(float[] Matrix, Vector3 Player)
{
    /// <summary>
    /// The screen's width over its height, read off the matrix itself.
    /// </summary>
    /// <remarks>
    /// A PERSPECTIVE STRETCHES CLIP X BY THE FOCAL LENGTH OVER THE ASPECT AND CLIP Y BY THE FOCAL
    /// LENGTH ALONE, and a turn keeps lengths, so the two columns' lengths divide to the aspect. It is
    /// taken from the matrix rather than the overlay's window because the load that asks runs off the
    /// frame, where the window's size cannot be asked for.
    /// </remarks>
    public float Aspect
    {
        get
        {
            if (Matrix.Length < 16)
            {
                return 16f / 9f;
            }

            float x = new Vector3(Matrix[0], Matrix[4], Matrix[8]).Length();
            float y = new Vector3(Matrix[1], Matrix[5], Matrix[9]).Length();
            return x > 0f && y > 0f && float.IsFinite(y / x) ? Math.Clamp(y / x, 0.5f, 4f) : 16f / 9f;
        }
    }
}

/// <summary>
/// Which parts of a laid room the game's camera can show from anywhere the player can stand within its reach - and so which parts a picture of it may leave out.
/// </summary>
/// <remarks>
/// THE GAME'S OWN CAMERA, MOVED, NOT A DIRECTION GUESSED. The camera follows the player at a fixed
/// angle and distance - the player is always in the middle of the screen, which is what the matrix
/// hunt itself rests on - so the camera standing over another place is the matrix read now, moved by
/// how far that place is from where the player stands. The places are the area's own walkable cells,
/// one about every half of the screen's smaller side, out to the screen's reach past the room's
/// edge: a player standing in the next room sees into this one. Each is a perspective view with the
/// game's own near plane and screen, so a cliff the camera stands in front of hides what is behind
/// it, and one behind the camera hides nothing - an orthographic view along the camera's direction
/// would have let a mountain at the far edge of the room hide the valley in front of it.
///
/// A TRIANGLE IS KEPT WHERE ANY VIEW SEES IT: drawn into that view's depth after every solid
/// triangle, at least one of the texels it covers is no further than the nearest thing there plus
/// <see cref="Bias"/>. A triangle smaller than a texel is asked at its middle, against the furthest of
/// the nine texels round it, so a pebble at the foot of a wall is not lost to the wall's texel.
///
/// WHAT HIDES: solid triangles only. A cut-out leaf has holes the depth here would not have, and a
/// mixed or added shape hides nothing in the picture either. WHAT IS ALWAYS KEPT is the caller's to
/// say - a shadow-only caster is never seen, and casting is what it is for.
///
/// AND DOODADS ARE ASKED BEFORE THEY ARE LOADED - see <see cref="See"/> and <see cref="BoxSeen"/> -
/// against the tiles' depth in a coarser copy of every view: first against the furthest depth of the
/// block of texels it covers, kept beside the view in halving levels, and where that cannot say no,
/// with its own six faces drawn against the texels.
///
/// A PICTURE, NOT THE GAME. The picture is orthographic and can be turned to look from anywhere; what
/// the game's camera cannot see from any walkable place is left out, and a picture turned to look
/// from below shows the holes. The book's switch puts everything back.
/// </remarks>
public sealed class CameraSight
{
    /// <summary>Rows of each view's depth in the final test. Columns follow the screen's shape.</summary>
    public const int Tall = 216;

    /// <summary>Rows of each view's depth in the doodads' test.</summary>
    public const int CoarseTall = 90;

    /// <summary>How far behind the nearest thing at a texel, in world units along the view, a triangle may lie and still count as seen.</summary>
    public const float Bias = 8f;

    /// <summary>The nearest a point may be to the camera, in world units, and still be drawn.</summary>
    public const float Near = 1f;

    /// <summary>Most places the camera is put at; past it they are spread further apart.</summary>
    public const int MostViews = 384;

    /// <summary>
    /// How many places the camera is put at across the screen's smaller side.
    /// </summary>
    /// <remarks>
    /// TWO, so every point is seen from four to six places at as many angles across the screen.
    /// Three saw each from over twelve, and drawing a triangle into every view it falls in was nearly
    /// all the time the sight took.
    /// </remarks>
    private const float PerScreen = 2f;

    /// <summary>Most texels a box is read over along each side of the screen - see <see cref="BoxSeen"/>.</summary>
    private const int Reads = 8;

    private readonly Matrix4x4[] _views;
    private readonly int _wide;
    private readonly int _coarseWide;
    private float[][]? _coarse;
    private int[] _levelAt = [];
    private int[] _levelWide = [];
    private int[] _levelTall = [];

    private CameraSight(Matrix4x4[] views, float aspect, float step)
    {
        _views = views;
        _wide = Math.Clamp((int)MathF.Round(Tall * aspect), Tall / 2, Tall * 4);
        _coarseWide = Math.Clamp((int)MathF.Round(CoarseTall * aspect), CoarseTall / 2, CoarseTall * 4);
        Step = step;
    }

    /// <summary>How many places the camera was put at.</summary>
    public int Views => _views.Length;

    /// <summary>How far apart the places are, in world units.</summary>
    public float Step { get; }

    /// <summary>Each place's view, model space to clip space - row vectors, as System.Numerics multiplies.</summary>
    public IReadOnlyList<Matrix4x4> Matrices => _views;

    /// <summary>
    /// The camera put over every walkable place within its reach of a model laid in the area, or null and why not.
    /// </summary>
    /// <param name="shot">The camera as read, or null outside the game.</param>
    /// <param name="grid">The area the model is laid in: where the player can stand, and how high the ground is there.</param>
    /// <param name="origin">Where the model's own nought lies in the world, x and y; its z is the world's.</param>
    /// <param name="least">The low corner of what must be seen, model space.</param>
    /// <param name="most">The high corner.</param>
    /// <param name="why">Why there is none, or empty.</param>
    public static CameraSight? Over(CameraShot? shot, TerrainGrid? grid, Vector2 origin, Vector3 least, Vector3 most, out string why)
    {
        if (shot is null || shot.Matrix.Length < 16)
        {
            why = "the game's camera is not known - it is read in the game";
            return null;
        }

        if (grid is null || grid.Width <= 0 || grid.Height <= 0)
        {
            why = "the area is not known";
            return null;
        }

        float[] flat = shot.Matrix;
        var camera = new Matrix4x4(
            flat[0], flat[1], flat[2], flat[3],
            flat[4], flat[5], flat[6], flat[7],
            flat[8], flat[9], flat[10], flat[11],
            flat[12], flat[13], flat[14], flat[15]);
        const float cell = MapView.WorldToGrid;
        Vector3 player = shot.Player;
        var stands = new Vector2(player.X, player.Y);

        // THE GROUND THE SCREEN SHOWS round the player: where its corners and the middles of its
        // edges meet the plane the player stands on.
        Vector2 low = new(float.MaxValue), high = new(float.MinValue);
        Span<Vector2> edges = stackalloc Vector2[4];
        for (var one = 0; one < 8; one++)
        {
            (float across, float down) = one switch
            {
                0 => (-1f, 0f),
                1 => (1f, 0f),
                2 => (0f, -1f),
                3 => (0f, 1f),
                4 => (-1f, -1f),
                5 => (1f, -1f),
                6 => (-1f, 1f),
                _ => (1f, 1f),
            };

            if (OnPlane(camera, across, down, player.Z) is not { } met)
            {
                why = "the game's camera does not look down at the ground";
                return null;
            }

            if (one < 4)
            {
                edges[one] = met;
            }

            low = Vector2.Min(low, met - stands);
            high = Vector2.Max(high, met - stands);
        }

        float wide = Vector2.Distance(edges[0], edges[1]), tall = Vector2.Distance(edges[2], edges[3]);
        float step = MathF.Max(MathF.Min(wide, tall) / PerScreen, cell * 8f);

        // A PLACE CAN SEE THE MODEL where the ground its screen shows reaches it - half as far again,
        // for what stands above the ground and is seen from further off.
        Vector2 pad = (high - low) * 0.5f;
        Vector2 from = Vector2.Max(origin + new Vector2(least.X, least.Y) - high - pad, Vector2.Zero);
        Vector2 to = Vector2.Min(origin + new Vector2(most.X, most.Y) - low + pad, new Vector2(grid.Width, grid.Height) * cell);
        if (!(from.X < to.X && from.Y < to.Y))
        {
            why = "the room lies outside the area's ground";
            return null;
        }

        float under = grid.HeightAt((int)(player.X / cell), (int)(player.Y / cell));
        List<Matrix4x4> views = [];
        for (var attempt = 0; attempt < 4; attempt++)
        {
            views = Places(camera, grid, origin, stands, under, from, to, step);
            if (views.Count <= MostViews)
            {
                break;
            }

            step *= MathF.Sqrt(views.Count / (float)MostViews) * 1.05f;
        }

        if (views.Count == 0)
        {
            why = "no ground the player can stand on lies within the camera's reach of the room";
            return null;
        }

        why = string.Empty;
        return new CameraSight([.. views], shot.Aspect, step);
    }

    /// <summary>One view per block of the area <paramref name="step"/> across that holds a walkable cell, over the walkable cell nearest the block's middle.</summary>
    private static List<Matrix4x4> Places(
        Matrix4x4 camera, TerrainGrid grid, Vector2 origin, Vector2 stands, float under, Vector2 from, Vector2 to, float step)
    {
        const float cell = MapView.WorldToGrid;
        var nx = (int)MathF.Ceiling((to.X - from.X) / step);
        var ny = (int)MathF.Ceiling((to.Y - from.Y) / step);
        int stride = Math.Max(1, (int)(step / cell / 12f));
        var views = new List<Matrix4x4>();
        for (var by = 0; by < ny; by++)
        {
            for (var bx = 0; bx < nx; bx++)
            {
                Vector2 low = from + (new Vector2(bx, by) * step);
                Vector2 high = Vector2.Min(low + new Vector2(step), to);
                Vector2 middle = (low + high) * 0.5f;
                int x0 = Math.Max(0, (int)(low.X / cell)), x1 = Math.Min(grid.Width - 1, (int)(high.X / cell));
                int y0 = Math.Max(0, (int)(low.Y / cell)), y1 = Math.Min(grid.Height - 1, (int)(high.Y / cell));
                int bestX = -1, bestY = -1;
                float nearest = float.MaxValue;
                for (int y = y0; y <= y1; y += stride)
                {
                    for (int x = x0; x <= x1; x += stride)
                    {
                        if (!grid.IsWalkable(x, y))
                        {
                            continue;
                        }

                        float distance = Vector2.DistanceSquared(new Vector2(x + 0.5f, y + 0.5f) * cell, middle);
                        if (distance < nearest)
                        {
                            nearest = distance;
                            (bestX, bestY) = (x, y);
                        }
                    }
                }

                if (bestX < 0)
                {
                    continue;
                }

                // THE CAMERA FOLLOWS THE PLAYER THERE, height and all: the world moved back by how
                // far the place is from where the player stands, the model's own frame on top.
                Vector2 moved = (new Vector2(bestX + 0.5f, bestY + 0.5f) * cell) - stands;
                float rise = grid.HeightAt(bestX, bestY) - under;
                views.Add(Matrix4x4.CreateTranslation(origin.X - moved.X, origin.Y - moved.Y, -rise) * camera);
            }
        }

        return views;
    }

    /// <summary>Where a point of the screen meets the flat plane at a height, or null where it does not in front of the camera.</summary>
    private static Vector2? OnPlane(Matrix4x4 m, float across, float down, float height)
    {
        // x - across*w = 0 and y - down*w = 0 with z fixed: two lines in x and y.
        float a1 = m.M11 - (across * m.M14), b1 = m.M21 - (across * m.M24);
        float c1 = ((m.M31 - (across * m.M34)) * height) + m.M41 - (across * m.M44);
        float a2 = m.M12 - (down * m.M14), b2 = m.M22 - (down * m.M24);
        float c2 = ((m.M32 - (down * m.M34)) * height) + m.M42 - (down * m.M44);
        float determinant = (a1 * b2) - (a2 * b1);
        if (!(MathF.Abs(determinant) > 1e-12f))
        {
            return null;
        }

        float x = ((-c1 * b2) + (c2 * b1)) / determinant;
        float y = ((-a1 * c2) + (a2 * c1)) / determinant;
        float w = (x * m.M14) + (y * m.M24) + (height * m.M34) + m.M44;
        return w > Near && float.IsFinite(x) && float.IsFinite(y) ? new Vector2(x, y) : null;
    }

    /// <summary>
    /// Draws what hides doodads into a coarse copy of every view, so that <see cref="BoxSeen"/> can answer.
    /// </summary>
    /// <param name="places">Every vertex, model space.</param>
    /// <param name="indices">Three per triangle - every one of them solid.</param>
    /// <param name="threads">How many threads may draw.</param>
    public void See(Vector3[] places, int[] indices, int threads)
    {
        ArgumentNullException.ThrowIfNull(places);
        ArgumentNullException.ThrowIfNull(indices);

        // EACH LEVEL HALF THE ONE BEFORE, down to a single texel, holding the furthest of the four it
        // covers - so the furthest depth over any block is a handful of reads.
        var at = new List<int>();
        var wides = new List<int>();
        var talls = new List<int>();
        int wide = _coarseWide, tall = CoarseTall, total = 0;
        while (true)
        {
            at.Add(total);
            wides.Add(wide);
            talls.Add(tall);
            total += wide * tall;
            if (wide == 1 && tall == 1)
            {
                break;
            }

            wide = (wide + 1) / 2;
            tall = (tall + 1) / 2;
        }

        _levelAt = [.. at];
        _levelWide = [.. wides];
        _levelTall = [.. talls];
        var coarse = new float[_views.Length][];
        int triangles = indices.Length / 3;
        Bins? bins = triangles > 0 ? Bins.Of(places, indices, triangles) : null;
        int levels = _levelAt.Length, size = total;
        Parallel.For(
            0,
            _views.Length,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads) },
            () => new int[bins?.Count ?? 0],
            (view, _, visible) =>
            {
                var depth = new float[size];
                if (bins is not null)
                {
                    var lens = new Lens(_views[view], _coarseWide, CoarseTall);
                    int count = bins.Visible(lens, visible);
                    for (var one = 0; one < count; one++)
                    {
                        int bin = visible[one];
                        for (int entry = bins.Starts[bin], end = bins.Starts[bin + 1]; entry < end; entry++)
                        {
                            int t = bins.Entries[entry] * 3;
                            Fill(depth, _coarseWide, CoarseTall, lens, places[indices[t]], places[indices[t + 1]], places[indices[t + 2]]);
                        }
                    }
                }

                for (var level = 1; level < levels; level++)
                {
                    Halve(depth, _levelAt[level - 1], _levelWide[level - 1], _levelTall[level - 1], _levelAt[level], _levelWide[level], _levelTall[level]);
                }

                coarse[view] = depth;
                return visible;
            },
            _ => { });
        _coarse = coarse;
    }

    /// <summary>
    /// Whether any view can see into a box, against what <see cref="See"/> drew - true when nothing was drawn.
    /// </summary>
    /// <param name="least">The box's low corner, model space.</param>
    /// <param name="most">Its high corner.</param>
    public bool BoxSeen(Vector3 least, Vector3 most)
    {
        if (_coarse is not { } coarse)
        {
            return true;
        }

        Span<Vector3> corners = stackalloc Vector3[8];
        Span<Vector3> clips = stackalloc Vector3[8];
        for (var one = 0; one < 8; one++)
        {
            corners[one] = new Vector3((one & 1) == 0 ? least.X : most.X, (one & 2) == 0 ? least.Y : most.Y, (one & 4) == 0 ? least.Z : most.Z);
        }

        for (var view = 0; view < _views.Length; view++)
        {
            var lens = new Lens(_views[view], _coarseWide, CoarseTall);
            int behind = 0;
            for (var one = 0; one < 8; one++)
            {
                clips[one] = lens.Clip(corners[one]);
                behind += clips[one].Z < Near ? 1 : 0;
            }

            if (behind == 8 || Outside(clips))
            {
                continue;
            }

            // PART OF IT AT THE CAMERA - a view standing inside it - is taken as seen.
            if (behind > 0)
            {
                return true;
            }

            float left = float.MaxValue, right = float.MinValue, top = float.MaxValue, bottom = float.MinValue, nearest = float.MaxValue;
            foreach (Vector3 clip in clips)
            {
                (float x, float y) = lens.Screen(clip);
                left = MathF.Min(left, x);
                right = MathF.Max(right, x);
                top = MathF.Min(top, y);
                bottom = MathF.Max(bottom, y);
                nearest = MathF.Min(nearest, clip.Z);
            }

            left = MathF.Max(left, 0f);
            top = MathF.Max(top, 0f);
            right = MathF.Min(right, _coarseWide - 0.001f);
            bottom = MathF.Min(bottom, CoarseTall - 0.001f);
            if (left > right || top > bottom)
            {
                continue;
            }

            // THE FINEST LEVEL AT WHICH THE BOX SPANS AT MOST EIGHT TEXELS A SIDE: a coarser one answers
            // in fewer reads, but its texels reach past the box to the screen's empty edge, and a box
            // half the screen across was seen in every view that way.
            int x0 = (int)left, x1 = (int)right, y0 = (int)top, y1 = (int)bottom;
            int level = 0;
            while (level < _levelAt.Length - 1 && ((x1 >> level) - (x0 >> level) >= Reads || (y1 >> level) - (y0 >> level) >= Reads))
            {
                level++;
            }

            float[] depth = coarse[view];
            int from = _levelAt[level], wide = _levelWide[level];
            float furthest = float.MaxValue;
            for (int y = y0 >> level, upto = Math.Min(y1 >> level, _levelTall[level] - 1); y <= upto; y++)
            {
                for (int x = x0 >> level, end = Math.Min(x1 >> level, wide - 1); x <= end; x++)
                {
                    furthest = MathF.Min(furthest, depth[from + (y * wide) + x]);
                }
            }

            // THE BLOCK SAYS HIDDEN ONLY WHERE THE BOX'S NEAREST CORNER IS BEHIND THE FURTHEST THING OVER
            // IT, which a box much wider than it is deep under the ground never is: the ground over its
            // far half lies beyond its near corner. So a box the block lets through has its own faces
            // drawn against the texels, each one asked where it lies.
            if (!Shows(furthest, 1f / nearest))
            {
                continue;
            }

            for (var face = 0; face < Faces.Length; face += 3)
            {
                if (Shown(depth, _coarseWide, CoarseTall, lens, corners[Faces[face]], corners[Faces[face + 1]], corners[Faces[face + 2]]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>A box's twelve triangles, by its corners numbered x first, then y, then z.</summary>
    private static readonly int[] Faces = [0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5, 0, 4, 5, 0, 5, 1, 2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3];

    /// <summary>
    /// Which triangles any view sees, drawn against every solid triangle at <see cref="Tall"/> rows.
    /// </summary>
    /// <param name="places">Every vertex, model space.</param>
    /// <param name="indices">Three per triangle.</param>
    /// <param name="hides">Per triangle, whether it writes depth - a solid one.</param>
    /// <param name="kept">Per triangle, whether it is kept whatever the views say.</param>
    /// <param name="threads">How many threads may draw.</param>
    /// <returns>Per triangle, whether it is seen or kept.</returns>
    public bool[] Seen(Vector3[] places, int[] indices, bool[] hides, bool[] kept, int threads)
    {
        ArgumentNullException.ThrowIfNull(places);
        ArgumentNullException.ThrowIfNull(indices);
        ArgumentNullException.ThrowIfNull(hides);
        ArgumentNullException.ThrowIfNull(kept);

        int triangles = indices.Length / 3;
        var seen = new bool[triangles];
        Array.Copy(kept, seen, Math.Min(kept.Length, triangles));
        if (triangles == 0 || _views.Length == 0)
        {
            return seen;
        }

        Bins bins = Bins.Of(places, indices, triangles);
        int wide = _wide;
        Parallel.For(
            0,
            _views.Length,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads) },
            () => (Depth: new float[wide * Tall], Visible: new int[bins.Count]),
            (view, _, scratch) =>
            {
                var lens = new Lens(_views[view], wide, Tall);
                int count = bins.Visible(lens, scratch.Visible);
                if (count == 0)
                {
                    return scratch;
                }

                float[] depth = scratch.Depth;
                Array.Clear(depth);
                for (var one = 0; one < count; one++)
                {
                    int bin = scratch.Visible[one];
                    for (int entry = bins.Starts[bin], end = bins.Starts[bin + 1]; entry < end; entry++)
                    {
                        int t = bins.Entries[entry];
                        if (hides[t])
                        {
                            Fill(depth, wide, Tall, lens, places[indices[t * 3]], places[indices[(t * 3) + 1]], places[indices[(t * 3) + 2]]);
                        }
                    }
                }

                // A TRIANGLE ANOTHER VIEW HAS SEEN IS NOT ASKED AGAIN - read without a lock, since a
                // stale false costs only a second look and nothing ever writes it back to false.
                for (var one = 0; one < count; one++)
                {
                    int bin = scratch.Visible[one];
                    for (int entry = bins.Starts[bin], end = bins.Starts[bin + 1]; entry < end; entry++)
                    {
                        int t = bins.Entries[entry];
                        if (!seen[t] && Shown(depth, wide, Tall, lens, places[indices[t * 3]], places[indices[(t * 3) + 1]], places[indices[(t * 3) + 2]]))
                        {
                            seen[t] = true;
                        }
                    }
                }

                return scratch;
            },
            _ => { });
        return seen;
    }

    /// <summary>Whether something at <paramref name="near"/> (one over its distance) is no further than <see cref="Bias"/> behind what a texel holds; nought is an empty texel.</summary>
    private static bool Shows(float held, float near) => held <= near + (Bias * held * near);

    /// <summary>Whether every corner lies outside one and the same side of the screen - so none of what they span is on it.</summary>
    private static bool Outside(ReadOnlySpan<Vector3> clips)
    {
        bool left = true, right = true, below = true, above = true;
        foreach (Vector3 clip in clips)
        {
            left &= clip.X < -clip.Z;
            right &= clip.X > clip.Z;
            below &= clip.Y < -clip.Z;
            above &= clip.Y > clip.Z;
        }

        return left || right || below || above;
    }

    /// <summary>Draws a triangle's nearness - one over its distance - into a depth, keeping the nearest at every texel it covers.</summary>
    private static void Fill(float[] depth, int wide, int tall, in Lens lens, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 ca = lens.Clip(a), cb = lens.Clip(b), cc = lens.Clip(c);

        // ONE CROSSING THE CAMERA HIDES NOTHING HERE: left out rather than clipped, which can only
        // keep more of what lies behind it.
        if (ca.Z < Near || cb.Z < Near || cc.Z < Near || Outside([ca, cb, cc]))
        {
            return;
        }

        Raster raster = new(lens, ca, cb, cc, wide, tall);
        if (!raster.Ready)
        {
            return;
        }

        for (int y = raster.Top; y <= raster.Bottom; y++)
        {
            if (!raster.Span(y, out int from, out int to))
            {
                continue;
            }

            float near = raster.Near(from + 0.5f, y + 0.5f);
            int row = y * wide;
            for (int x = from; x <= to; x++)
            {
                if (near > depth[row + x])
                {
                    depth[row + x] = near;
                }

                near += raster.StepNear;
            }
        }
    }

    /// <summary>Whether a triangle shows at any texel it covers, or - covering none - at its middle against the furthest of the nine texels round it.</summary>
    private static bool Shown(float[] depth, int wide, int tall, in Lens lens, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 ca = lens.Clip(a), cb = lens.Clip(b), cc = lens.Clip(c);
        if (Outside([ca, cb, cc]))
        {
            return false;
        }

        int behind = (ca.Z < Near ? 1 : 0) + (cb.Z < Near ? 1 : 0) + (cc.Z < Near ? 1 : 0);
        if (behind == 3)
        {
            return false;
        }

        // AT THE CAMERA: seen, as nothing in front of it can be said to hide it.
        if (behind > 0)
        {
            return true;
        }

        Raster raster = new(lens, ca, cb, cc, wide, tall);
        if (raster.Ready)
        {
            var covered = false;
            for (int y = raster.Top; y <= raster.Bottom; y++)
            {
                if (!raster.Span(y, out int from, out int to))
                {
                    continue;
                }

                covered = true;
                float near = raster.Near(from + 0.5f, y + 0.5f);
                int row = y * wide;
                for (int x = from; x <= to; x++)
                {
                    if (Shows(depth[row + x], near))
                    {
                        return true;
                    }

                    near += raster.StepNear;
                }
            }

            if (covered)
            {
                return false;
            }
        }

        // NEARNESS RUNS STRAIGHT ACROSS THE SCREEN, so at the middle on the screen it is the mean.
        (float ax, float ay) = lens.Screen(ca);
        (float bx, float by) = lens.Screen(cb);
        (float cx, float cy) = lens.Screen(cc);
        float mx = (ax + bx + cx) / 3f, my = (ay + by + cy) / 3f;
        if (!(mx >= 0f && my >= 0f && mx < wide && my < tall))
        {
            return false;
        }

        float middle = ((1f / ca.Z) + (1f / cb.Z) + (1f / cc.Z)) / 3f;
        int tx = (int)mx, ty = (int)my;
        float furthest = float.MaxValue;
        for (int y = Math.Max(0, ty - 1), upto = Math.Min(tall - 1, ty + 1); y <= upto; y++)
        {
            for (int x = Math.Max(0, tx - 1), end = Math.Min(wide - 1, tx + 1); x <= end; x++)
            {
                furthest = MathF.Min(furthest, depth[(y * wide) + x]);
            }
        }

        return Shows(furthest, middle);
    }

    /// <summary>Each texel of a level the furthest - the least near - of the up to four of the level before that it covers.</summary>
    private static void Halve(float[] depth, int from, int wide, int tall, int into, int halfWide, int halfTall)
    {
        for (var y = 0; y < halfTall; y++)
        {
            int y0 = y * 2, y1 = Math.Min(y0 + 1, tall - 1);
            for (var x = 0; x < halfWide; x++)
            {
                int x0 = x * 2, x1 = Math.Min(x0 + 1, wide - 1);
                depth[into + (y * halfWide) + x] = MathF.Min(
                    MathF.Min(depth[from + (y0 * wide) + x0], depth[from + (y0 * wide) + x1]),
                    MathF.Min(depth[from + (y1 * wide) + x0], depth[from + (y1 * wide) + x1]));
            }
        }
    }

    /// <summary>A view's clip x, y and w as three dot products, and where clip space lands on its texels.</summary>
    private readonly struct Lens
    {
        private readonly Vector3 _x, _y, _w;
        private readonly float _x0, _y0, _w0;

        public Lens(in Matrix4x4 m, int wide, int tall)
        {
            _x = new Vector3(m.M11, m.M21, m.M31);
            _y = new Vector3(m.M12, m.M22, m.M32);
            _w = new Vector3(m.M14, m.M24, m.M34);
            _x0 = m.M41;
            _y0 = m.M42;
            _w0 = m.M44;
            HalfWide = wide * 0.5f;
            HalfTall = tall * 0.5f;
        }

        public float HalfWide { get; }

        public float HalfTall { get; }

        /// <summary>Clip x, y and w - w in z.</summary>
        public Vector3 Clip(Vector3 p) => new(Vector3.Dot(p, _x) + _x0, Vector3.Dot(p, _y) + _y0, Vector3.Dot(p, _w) + _w0);

        /// <summary>Where a clip point lands, in texels from the top left - the screen's y runs down, as WorldToScreen.Project has it.</summary>
        public (float X, float Y) Screen(Vector3 clip)
        {
            float inverse = 1f / clip.Z;
            return (HalfWide + (clip.X * inverse * HalfWide), HalfTall - (clip.Y * inverse * HalfTall));
        }
    }

    /// <summary>A triangle on a view's texels: its box, its three edge functions and its nearness, each a plane across the screen.</summary>
    private readonly struct Raster
    {
        private readonly float _a0, _b0, _c0, _a1, _b1, _c1, _a2, _b2, _c2, _an, _bn, _cn, _i0, _i1, _i2;

        public Raster(in Lens lens, Vector3 ca, Vector3 cb, Vector3 cc, int wide, int tall)
        {
            // ONE DIVISION PER CORNER, for the screen and the nearness both.
            float na = 1f / ca.Z, nb = 1f / cb.Z, nc = 1f / cc.Z;
            float hw = lens.HalfWide, ht = lens.HalfTall;
            float ax = hw + (ca.X * na * hw), ay = ht - (ca.Y * na * ht);
            float bx = hw + (cb.X * nb * hw), by = ht - (cb.Y * nb * ht);
            float cx = hw + (cc.X * nc * hw), cy = ht - (cc.Y * nc * ht);
            float area = ((bx - ax) * (cy - ay)) - ((by - ay) * (cx - ax));
            Left = Math.Max(0, (int)MathF.Floor(MathF.Min(ax, MathF.Min(bx, cx))));
            Right = Math.Min(wide - 1, (int)MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx))));
            Top = Math.Max(0, (int)MathF.Floor(MathF.Min(ay, MathF.Min(by, cy))));
            Bottom = Math.Min(tall - 1, (int)MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy))));
            Ready = MathF.Abs(area) > 1e-6f && Left <= Right && Top <= Bottom;
            if (!Ready)
            {
                return;
            }

            // EACH EDGE SIGNED SO THE INSIDE IS POSITIVE, whichever way round the triangle is wound.
            float sign = area < 0f ? -1f : 1f;
            _a0 = -(cy - by) * sign;
            _b0 = (cx - bx) * sign;
            _c0 = (((cy - by) * bx) - ((cx - bx) * by)) * sign;
            _a1 = -(ay - cy) * sign;
            _b1 = (ax - cx) * sign;
            _c1 = (((ay - cy) * cx) - ((ax - cx) * cy)) * sign;
            _a2 = -(by - ay) * sign;
            _b2 = (bx - ax) * sign;
            _c2 = (((by - ay) * ax) - ((bx - ax) * ay)) * sign;
            _i0 = _a0 != 0f ? 1f / _a0 : 0f;
            _i1 = _a1 != 0f ? 1f / _a1 : 0f;
            _i2 = _a2 != 0f ? 1f / _a2 : 0f;

            // ONE OVER W RUNS STRAIGHT ACROSS THE SCREEN, so it is a plane too: the corners' own,
            // weighted by the edges.
            float total = 1f / (area * sign);
            na *= total;
            nb *= total;
            nc *= total;
            _an = (_a0 * na) + (_a1 * nb) + (_a2 * nc);
            _bn = (_b0 * na) + (_b1 * nb) + (_b2 * nc);
            _cn = (_c0 * na) + (_c1 * nb) + (_c2 * nc);
        }

        public bool Ready { get; }

        public int Left { get; }

        public int Right { get; }

        public int Top { get; }

        public int Bottom { get; }

        public float StepNear => _an;

        public float Near(float x, float y) => (_an * x) + (_bn * y) + _cn;

        /// <summary>
        /// The texels of a row whose middles are inside, or false for none.
        /// </summary>
        /// <remarks>
        /// SOLVED PER ROW RATHER THAN TESTED PER TEXEL: each edge is a line, so where it is positive
        /// along a row is everything left or right of one point, and the three meet in one stretch.
        /// Testing the whole box instead visited twice the texels a triangle covers, and drawing the
        /// views was nearly all of the time.
        /// </remarks>
        public bool Span(int y, out int from, out int to)
        {
            float py = y + 0.5f;
            float low = Left + 0.5f, high = Right + 0.5f;
            if (!Edge(_a0, _i0, (_b0 * py) + _c0, ref low, ref high)
                || !Edge(_a1, _i1, (_b1 * py) + _c1, ref low, ref high)
                || !Edge(_a2, _i2, (_b2 * py) + _c2, ref low, ref high))
            {
                from = to = 0;
                return false;
            }

            from = Math.Max(Left, (int)MathF.Ceiling(low - 0.5f));
            to = Math.Min(Right, (int)MathF.Floor(high - 0.5f));
            return from <= to;
        }

        /// <summary>Narrows a row's stretch to where one edge, a x plus <paramref name="rest"/>, is not negative; <paramref name="inverse"/> is one over a.</summary>
        private static bool Edge(float a, float inverse, float rest, ref float low, ref float high)
        {
            if (a > 0f)
            {
                low = MathF.Max(low, -rest * inverse);
            }
            else if (a < 0f)
            {
                high = MathF.Min(high, -rest * inverse);
            }
            else if (rest < 0f)
            {
                return false;
            }

            return low <= high;
        }
    }

    /// <summary>
    /// The triangles sorted into squares of the ground by their middles, each square's box the union of its triangles' - so a view asks a square once rather than every triangle in it.
    /// </summary>
    private sealed class Bins
    {
        /// <summary>Squares to a side, at most.</summary>
        private const int Across = 64;

        private Bins(int count, int[] starts, int[] entries, Vector3[] least, Vector3[] most)
        {
            Count = count;
            Starts = starts;
            Entries = entries;
            Least = least;
            Most = most;
        }

        public int Count { get; }

        /// <summary>Where each square's triangles start in <see cref="Entries"/>; one more than <see cref="Count"/>.</summary>
        public int[] Starts { get; }

        /// <summary>Triangle numbers, square by square.</summary>
        public int[] Entries { get; }

        public Vector3[] Least { get; }

        public Vector3[] Most { get; }

        public static Bins Of(Vector3[] places, int[] indices, int triangles)
        {
            Vector2 low = new(float.MaxValue), high = new(float.MinValue);
            for (var t = 0; t < triangles; t++)
            {
                Vector3 middle = (places[indices[t * 3]] + places[indices[(t * 3) + 1]] + places[indices[(t * 3) + 2]]) / 3f;
                low = Vector2.Min(low, new Vector2(middle.X, middle.Y));
                high = Vector2.Max(high, new Vector2(middle.X, middle.Y));
            }

            float side = MathF.Max(MathF.Max(high.X - low.X, high.Y - low.Y) / Across, 1f);
            int nx = Math.Clamp((int)((high.X - low.X) / side) + 1, 1, Across + 1);
            int ny = Math.Clamp((int)((high.Y - low.Y) / side) + 1, 1, Across + 1);
            int count = nx * ny;
            var bin = new int[triangles];
            var starts = new int[count + 1];
            var least = new Vector3[count];
            var most = new Vector3[count];
            Array.Fill(least, new Vector3(float.MaxValue));
            Array.Fill(most, new Vector3(float.MinValue));
            for (var t = 0; t < triangles; t++)
            {
                Vector3 a = places[indices[t * 3]], b = places[indices[(t * 3) + 1]], c = places[indices[(t * 3) + 2]];
                Vector3 middle = (a + b + c) / 3f;
                int x = Math.Clamp((int)((middle.X - low.X) / side), 0, nx - 1);
                int y = Math.Clamp((int)((middle.Y - low.Y) / side), 0, ny - 1);
                int one = (y * nx) + x;
                bin[t] = one;
                starts[one + 1]++;
                least[one] = Vector3.Min(least[one], Vector3.Min(a, Vector3.Min(b, c)));
                most[one] = Vector3.Max(most[one], Vector3.Max(a, Vector3.Max(b, c)));
            }

            for (var one = 0; one < count; one++)
            {
                starts[one + 1] += starts[one];
            }

            var entries = new int[triangles];
            var next = new int[count];
            Array.Copy(starts, next, count);
            for (var t = 0; t < triangles; t++)
            {
                entries[next[bin[t]]++] = t;
            }

            return new Bins(count, starts, entries, least, most);
        }

        /// <summary>The squares whose box a view can see any of, into <paramref name="into"/>; how many.</summary>
        public int Visible(in Lens lens, int[] into)
        {
            Span<Vector3> clips = stackalloc Vector3[8];
            var count = 0;
            for (var one = 0; one < Count; one++)
            {
                if (Starts[one] == Starts[one + 1])
                {
                    continue;
                }

                Vector3 least = Least[one], most = Most[one];
                var behind = 0;
                for (var corner = 0; corner < 8; corner++)
                {
                    clips[corner] = lens.Clip(new Vector3(
                        (corner & 1) == 0 ? least.X : most.X,
                        (corner & 2) == 0 ? least.Y : most.Y,
                        (corner & 4) == 0 ? least.Z : most.Z));
                    behind += clips[corner].Z < Near ? 1 : 0;
                }

                if (behind < 8 && !Outside(clips))
                {
                    into[count++] = one;
                }
            }

            return count;
        }
    }
}
