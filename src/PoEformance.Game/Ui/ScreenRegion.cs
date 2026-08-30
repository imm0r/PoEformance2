using System.Numerics;

namespace PoEformance.Game.Ui;

/// <summary>A rectangle in window pixels, by its edges.</summary>
/// <remarks>
/// EDGES RATHER THAN POSITION AND SIZE, which is the shape every question here is asked in:
/// "does this overlap that", "clip this to that", "what is left of this once that is taken
/// out". A width has to be turned back into a right edge before any of those can be answered,
/// and doing that at four call sites is how one of them ends up off by a pixel.
/// </remarks>
public readonly record struct ScreenRect(float Left, float Top, float Right, float Bottom)
{
    /// <summary>The rectangle a window of this size occupies.</summary>
    public static ScreenRect Window(float width, float height) => new(0f, 0f, width, height);

    public float Width => Right - Left;

    public float Height => Bottom - Top;

    /// <summary>Whether there is anything left to draw in. Sub-pixel counts as nothing.</summary>
    public bool HasArea => Right - Left >= 1f && Bottom - Top >= 1f;

    public Vector2 TopLeft => new(Left, Top);

    public Vector2 BottomRight => new(Right, Bottom);

    /// <summary>
    /// Whether a point falls inside. Half-open on the far edges, so two rectangles that
    /// share an edge never both claim the same pixel.
    /// </summary>
    public bool Contains(Vector2 point)
        => point.X >= Left && point.X < Right && point.Y >= Top && point.Y < Bottom;

    /// <summary>Whether the two rectangles share any area. Touching edges do not count.</summary>
    public bool Overlaps(ScreenRect other)
        => Left < other.Right && Right > other.Left && Top < other.Bottom && Bottom > other.Top;

    /// <summary>The part of this rectangle that is also inside <paramref name="bounds"/>.</summary>
    /// <remarks>
    /// May come back with no area, which is the answer for a rectangle entirely outside -
    /// callers drop those rather than drawing a negative rectangle.
    /// </remarks>
    public ScreenRect ClippedTo(ScreenRect bounds)
        => new(
            Math.Max(Left, bounds.Left),
            Math.Max(Top, bounds.Top),
            Math.Min(Right, bounds.Right),
            Math.Min(Bottom, bounds.Bottom));

    /// <summary>Whether the numbers describe a rectangle at all, rather than a torn read.</summary>
    public bool IsSane
        => float.IsFinite(Left) && float.IsFinite(Top) && float.IsFinite(Right)
           && float.IsFinite(Bottom) && Right > Left && Bottom > Top
           && Math.Abs(Left) < 100_000f && Math.Abs(Top) < 100_000f
           && Math.Abs(Right) < 100_000f && Math.Abs(Bottom) < 100_000f;
}

/// <summary>
/// A rectangle with holes in it: where something may be drawn, and where it may not.
/// </summary>
/// <remarks>
/// WHY A REGION AND NOT A RECTANGLE. The game's large map is drawn across the WHOLE window and
/// the interface is drawn on top of it - the orbs, the flask and skill bars, the experience
/// strip, an open inventory. An overlay cannot get underneath any of that, so anything it
/// paints in the map's coordinate space lands ON the interface unless it is told not to, and a
/// single rectangle cannot say "everywhere except those four places". That is not a cosmetic
/// complaint: a terrain outline over the life orb hides the one number a player is watching.
///
/// A SET OF FREE RECTANGLES, computed once, is what makes that usable. ImGui clips to one
/// rectangle at a time, so continuous geometry - the terrain quad, a route line - has to be
/// drawn once per piece of the region; point markers just ask <see cref="Contains"/>. The
/// sweep below produces the pieces as a partition (no overlaps, nothing counted twice), so
/// drawing per piece draws each pixel exactly once and a route crossing a hole simply stops
/// at its edge instead of being dropped whole.
/// </remarks>
public sealed class ScreenRegion
{
    /// <summary>
    /// How many keep-out rectangles are honoured.
    /// </summary>
    /// <remarks>
    /// The sweep is quadratic in this and every piece it produces costs a draw pass, so a
    /// list that grew without bound - a bug in whatever supplies them - would show up as a
    /// frozen overlay rather than as a wrong picture. A dozen is far past what the interface
    /// has parts.
    /// </remarks>
    public const int MostKeptOut = 16;

    private static readonly ScreenRect[] Nothing = [];

    private readonly ScreenRect[] _free;

    private ScreenRegion(ScreenRect bounds, ScreenRect[] keptOut, ScreenRect[] free)
    {
        Bounds = bounds;
        KeptOut = keptOut;
        _free = free;
    }

    /// <summary>The whole of <paramref name="bounds"/>, with nothing kept out of it.</summary>
    public static ScreenRegion Whole(ScreenRect bounds)
        => new(bounds, Nothing, bounds.HasArea ? [bounds] : Nothing);

    /// <summary>
    /// <paramref name="bounds"/> less every rectangle in <paramref name="keepOut"/>.
    /// </summary>
    /// <remarks>
    /// Keep-out rectangles are clipped to the bounds and the nonsense ones dropped, so a
    /// caller may hand over whatever it measured: a panel that resolved off-screen, a zone
    /// dragged past the edge of the window, a torn read. What comes back is always a
    /// description of pixels that exist.
    /// </remarks>
    public static ScreenRegion Of(ScreenRect bounds, IEnumerable<ScreenRect> keepOut)
    {
        ArgumentNullException.ThrowIfNull(keepOut);

        if (!bounds.HasArea)
        {
            return new ScreenRegion(bounds, Nothing, Nothing);
        }

        List<ScreenRect> blocking = [];
        foreach (ScreenRect rect in keepOut)
        {
            if (blocking.Count >= MostKeptOut)
            {
                break;
            }

            ScreenRect clipped = rect.IsSane ? rect.ClippedTo(bounds) : default;
            if (clipped.HasArea)
            {
                blocking.Add(clipped);
            }
        }

        return blocking.Count == 0
            ? Whole(bounds)
            : new ScreenRegion(bounds, [.. blocking], Carve(bounds, blocking));
    }

    /// <summary>The rectangle the region is cut out of.</summary>
    public ScreenRect Bounds { get; }

    /// <summary>What is kept out of it, clipped to the bounds.</summary>
    public IReadOnlyList<ScreenRect> KeptOut { get; }

    /// <summary>
    /// What is left, as rectangles that do not overlap. Clip to these and draw once per piece.
    /// </summary>
    public IReadOnlyList<ScreenRect> Free => _free;

    /// <summary>Whether anything can be drawn at all - false once the region is covered.</summary>
    public bool HasAnything => _free.Length > 0;

    /// <summary>Whether a point may be drawn on.</summary>
    /// <remarks>
    /// Asked against the KEEP-OUT list rather than the free pieces: the free list is longer,
    /// and "is it in the bounds and not in a hole" is the same answer in a fraction of the
    /// comparisons. This runs once per marker per frame.
    /// </remarks>
    public bool Contains(Vector2 point)
    {
        if (!Bounds.Contains(point))
        {
            return false;
        }

        foreach (ScreenRect blocked in KeptOut)
        {
            if (blocked.Contains(point))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>What the region covers, for a readout.</summary>
    public override string ToString()
        => $"{Bounds.Width:F0}x{Bounds.Height:F0} at {Bounds.Left:F0},{Bounds.Top:F0}"
           + $" less {KeptOut.Count} in {_free.Length} pieces";

    /// <summary>
    /// Cuts the holes out, as a partition of what is left.
    /// </summary>
    /// <remarks>
    /// A VERTICAL SWEEP. Every keep-out edge splits the bounds into slabs; within one slab the
    /// blocked rows are the same all the way across, so the free rows there are a handful of
    /// intervals and each is a finished rectangle. That is exact - no rectangle is invented and
    /// none is missed - and it needs no polygon library for what is a handful of boxes.
    ///
    /// SLABS ARE MERGED BACK as the sweep goes, whenever the next one has a free interval with
    /// the same top and bottom. Without that, a single hole in the middle of the screen would
    /// come out as six rectangles instead of four, and every piece is a redraw of the whole
    /// map layer. Merging costs one comparison per interval and is what keeps the usual case -
    /// one band across the bottom - down to the one rectangle it really is.
    /// </remarks>
    private static ScreenRect[] Carve(ScreenRect bounds, List<ScreenRect> blocking)
    {
        List<float> cuts = [bounds.Left, bounds.Right];
        foreach (ScreenRect blocked in blocking)
        {
            cuts.Add(blocked.Left);
            cuts.Add(blocked.Right);
        }

        cuts.Sort();

        List<ScreenRect> done = [];

        // The pieces from the previous slab that are still growing rightwards. A piece stays
        // open only while the next slab leaves exactly the same rows free.
        List<ScreenRect> open = [];
        List<ScreenRect> stillOpen = [];

        for (int i = 1; i < cuts.Count; i++)
        {
            float left = cuts[i - 1];
            float right = cuts[i];
            if (right - left < 0.5f)
            {
                continue; // a duplicate edge, or a slab too thin to hold a pixel
            }

            stillOpen.Clear();
            foreach ((float top, float bottom) in FreeRows(bounds, blocking, left, right))
            {
                int carried = open.FindIndex(
                    piece => piece.Right >= left - 0.001f
                             && Math.Abs(piece.Top - top) < 0.001f
                             && Math.Abs(piece.Bottom - bottom) < 0.001f);

                if (carried >= 0)
                {
                    stillOpen.Add(open[carried] with { Right = right });
                    open.RemoveAt(carried);
                }
                else
                {
                    stillOpen.Add(new ScreenRect(left, top, right, bottom));
                }
            }

            // Whatever the new slab did not continue is finished at the slab boundary.
            done.AddRange(open.Where(piece => piece.HasArea));
            open.Clear();
            open.AddRange(stillOpen);
        }

        done.AddRange(open.Where(piece => piece.HasArea));
        return [.. done];
    }

    /// <summary>The rows of one slab nothing is keeping out, top to bottom.</summary>
    private static List<(float Top, float Bottom)> FreeRows(
        ScreenRect bounds, List<ScreenRect> blocking, float left, float right)
    {
        List<(float Top, float Bottom)> blocked = [];
        foreach (ScreenRect rect in blocking)
        {
            // Only what covers this slab all the way across matters: the slab edges came from
            // these very rectangles, so anything overlapping it at all spans it.
            if (rect.Left < right && rect.Right > left)
            {
                blocked.Add((rect.Top, rect.Bottom));
            }
        }

        blocked.Sort((a, b) => a.Top.CompareTo(b.Top));

        List<(float Top, float Bottom)> free = [];
        float at = bounds.Top;
        foreach ((float top, float bottom) in blocked)
        {
            if (top - at >= 1f)
            {
                free.Add((at, top));
            }

            at = Math.Max(at, bottom);
        }

        if (bounds.Bottom - at >= 1f)
        {
            free.Add((at, bounds.Bottom));
        }

        return free;
    }
}
