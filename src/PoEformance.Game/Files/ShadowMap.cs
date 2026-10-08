using System.Numerics;

namespace PoEformance.Game.Files;

/// <summary>
/// How far along the sun's light each point of a mesh is first met - the depth the sun sees - and so whether a point is in its shadow.
/// </summary>
/// <remarks>
/// THE GAME'S OWN WAY OF ANSWERING THE QUESTION: its sun renders a shadow map (shadows.ffx; the
/// directional light's shadow_map_sampling) and a pixel is in shadow where something nearer the sun
/// was drawn at its place in that map. Here the map is the mesh drawn once along the sun's direction,
/// orthographically, over the mesh's whole extent - a room is a few thousand units across, which a
/// map 2048 square covers at a few units a texel.
///
/// BUILT ONCE PER SUN AND MESH, not per frame: turning the picture moves the eye, not the sun, so
/// the canvas keeps the map until the sun or the mesh changes. Drawn on every core, a triangle at a
/// time, each texel's nearest depth kept by a compare-and-swap - floats past nought order as their
/// bits do, so the depths are kept above nought.
///
/// WHAT CASTS: solid and cut-out shapes, a cut-out one through its texture's alpha as the picture cuts
/// it. Translucent shapes do not, as they write no depth in the picture either.
///
/// READ WITH FOUR TEXELS AND AN OFFSET ALONG THE NORMAL: one texel's comparison is a hard staircase
/// along every shadow's edge, and a surface tested against its own depth shades itself in stripes
/// ("acne") unless the point is moved off it by about a texel first.
/// </remarks>
public sealed class ShadowMap
{
    /// <summary>The map's side, in texels.</summary>
    public const int Side = 2048;

    private readonly int[] _depth;
    private readonly Vector3 _u, _v, _w;
    private readonly Vector2 _least;
    private readonly float _perUnit;
    private readonly float _nearest;

    private ShadowMap(int[] depth, Vector3 u, Vector3 v, Vector3 w, Vector2 least, float perUnit, float nearest, Vector3 direction)
    {
        _depth = depth;
        _u = u;
        _v = v;
        _w = w;
        _least = least;
        _perUnit = perUnit;
        _nearest = nearest;
        Direction = direction;
    }

    /// <summary>The way the light travels that the map was drawn along.</summary>
    public Vector3 Direction { get; }

    /// <summary>How many world units one texel covers.</summary>
    public float Texel => 1f / _perUnit;

    /// <summary>
    /// The map of a mesh along a direction.
    /// </summary>
    /// <param name="places">Every vertex, model space.</param>
    /// <param name="indices">Three per triangle.</param>
    /// <param name="triangles">How many triangles.</param>
    /// <param name="direction">The way the light travels.</param>
    /// <param name="casts">Whether a triangle casts at all, by its index.</param>
    /// <param name="cutout">For a cut-out triangle, its texture and coordinates; null for a solid one.</param>
    /// <param name="threads">How many threads may draw it.</param>
    internal static ShadowMap? Build(
        Vector3[] places, int[] indices, int triangles, Vector3 direction, Func<int, bool> casts, Func<int, (Mipmaps Skin, Vector2[] Coordinates)?> cutout, int threads)
    {
        if (places.Length == 0 || triangles == 0 || !(direction.LengthSquared() > 0f))
        {
            return null;
        }

        Vector3 w = Vector3.Normalize(direction);
        Vector3 helper = MathF.Abs(w.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX;
        Vector3 u = Vector3.Normalize(Vector3.Cross(helper, w));
        Vector3 v = Vector3.Cross(w, u);

        Vector2 least = new(float.MaxValue), most = new(float.MinValue);
        float nearest = float.MaxValue;
        foreach (Vector3 place in places)
        {
            var at = new Vector2(Vector3.Dot(place, u), Vector3.Dot(place, v));
            least = Vector2.Min(least, at);
            most = Vector2.Max(most, at);
            nearest = MathF.Min(nearest, Vector3.Dot(place, w));
        }

        float span = MathF.Max(most.X - least.X, most.Y - least.Y);
        if (!(span > 0f) || !float.IsFinite(span))
        {
            return null;
        }

        // A TEXEL OF MARGIN EACH SIDE, so nothing lands on the edge the reads clamp to.
        float perUnit = (Side - 2) / span;
        least -= new Vector2(1f / perUnit);
        nearest -= 1f;

        var depth = new int[Side * Side];
        Array.Fill(depth, int.MaxValue);
        var corners = new Vector3[places.Length];
        for (var at = 0; at < places.Length; at++)
        {
            Vector3 place = places[at];
            corners[at] = new Vector3(
                (Vector3.Dot(place, u) - least.X) * perUnit,
                (Vector3.Dot(place, v) - least.Y) * perUnit,
                Vector3.Dot(place, w) - nearest);
        }

        Parallel.For(
            0, triangles,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads) },
            one =>
            {
                if (casts(one))
                {
                    Draw(depth, corners, indices, one, cutout(one));
                }
            });

        return new ShadowMap(depth, u, v, w, least, perUnit, nearest, w);
    }

    /// <summary>How much of the light reaches a point - one in the open, nought in full shadow, between along an edge.</summary>
    /// <param name="place">The point, model space.</param>
    /// <param name="normal">Its normal, unit length - the point is tested a little off its surface.</param>
    public float Lit(Vector3 place, Vector3 normal)
    {
        float texel = 1f / _perUnit;
        Vector3 off = place + (normal * (texel * 1.5f));
        float x = ((Vector3.Dot(off, _u) - _least.X) * _perUnit) - 0.5f;
        float y = ((Vector3.Dot(off, _v) - _least.Y) * _perUnit) - 0.5f;
        float depth = Vector3.Dot(off, _w) - _nearest - (texel * 2f);
        if (x < 0f || y < 0f || x >= Side - 1 || y >= Side - 1)
        {
            return 1f;
        }

        int x0 = (int)x, y0 = (int)y;
        float fx = x - x0, fy = y - y0;
        float a = Open(x0, y0, depth), b = Open(x0 + 1, y0, depth);
        float c = Open(x0, y0 + 1, depth), d = Open(x0 + 1, y0 + 1, depth);
        return float.Lerp(float.Lerp(a, b, fx), float.Lerp(c, d, fx), fy);
    }

    private float Open(int x, int y, float depth)
    {
        int held = _depth[(y * Side) + x];
        return held == int.MaxValue || depth <= BitConverter.Int32BitsToSingle(held) ? 1f : 0f;
    }

    private static void Draw(int[] depth, Vector3[] corners, int[] indices, int one, (Mipmaps Skin, Vector2[] Coordinates)? cutout)
    {
        int i0 = indices[one * 3], i1 = indices[(one * 3) + 1], i2 = indices[(one * 3) + 2];
        Vector3 c0 = corners[i0], c1 = corners[i1], c2 = corners[i2];
        float area = ((c1.X - c0.X) * (c2.Y - c0.Y)) - ((c1.Y - c0.Y) * (c2.X - c0.X));
        if (!(MathF.Abs(area) >= 1e-6f))
        {
            return;
        }

        float sign = area < 0f ? -1f : 1f;
        float total = area * sign;
        float inv = 1f / total;
        int left = Math.Max(0, (int)MathF.Floor(MathF.Min(c0.X, MathF.Min(c1.X, c2.X))));
        int right = Math.Min(Side - 1, (int)MathF.Ceiling(MathF.Max(c0.X, MathF.Max(c1.X, c2.X))));
        int top = Math.Max(0, (int)MathF.Floor(MathF.Min(c0.Y, MathF.Min(c1.Y, c2.Y))));
        int bottom = Math.Min(Side - 1, (int)MathF.Ceiling(MathF.Max(c0.Y, MathF.Max(c1.Y, c2.Y))));

        Mipmaps? skin = null;
        Vector2 s0 = default, s1 = default, s2 = default;
        float level = 0f;
        if (cutout is { } cut)
        {
            skin = cut.Skin;
            s0 = cut.Coordinates[i0];
            s1 = cut.Coordinates[i1];
            s2 = cut.Coordinates[i2];
            level = MeshPicture.Level(c0, c1, c2, s0, s1, s2, area, skin);
        }

        for (int y = top; y <= bottom; y++)
        {
            float py = y + 0.5f;
            for (int x = left; x <= right; x++)
            {
                float px = x + 0.5f;
                float w0 = (((c2.X - c1.X) * (py - c1.Y)) - ((c2.Y - c1.Y) * (px - c1.X))) * sign;
                float w1 = (((c0.X - c2.X) * (py - c2.Y)) - ((c0.Y - c2.Y) * (px - c2.X))) * sign;
                float w2 = total - w0 - w1;
                if (w0 < 0f || w1 < 0f || w2 < 0f)
                {
                    continue;
                }

                float first = w0 * inv, second = w1 * inv, third = w2 * inv;
                if (skin is not null
                    && MeshPicture.Sample4(skin, (first * s0) + (second * s1) + (third * s2), level).W < MeshPicture.CutoutAlpha)
                {
                    continue;
                }

                float away = MathF.Max(0f, (first * c0.Z) + (second * c1.Z) + (third * c2.Z));
                int bits = BitConverter.SingleToInt32Bits(away);
                int at = (y * Side) + x;
                int held = Volatile.Read(ref depth[at]);
                while (bits < held)
                {
                    int was = Interlocked.CompareExchange(ref depth[at], bits, held);
                    if (was == held)
                    {
                        break;
                    }

                    held = was;
                }
            }
        }
    }
}
