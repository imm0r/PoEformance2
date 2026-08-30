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

    /// <summary>Whether this rectangle contains the whole of another.</summary>
    /// <remarks>
    /// WHAT IT IS FOR is a keep-out that turns out to be the size of the thing it was meant to
    /// be kept out OF. The world screen the atlas sits on holds furniture that genuinely covers
    /// the screen - a vignette, a fade, an input catcher - and honouring one of those is not
    /// "keep off that bit", it is "keep off everything": indistinguishable from the feature
    /// being switched off, and arrived at without anybody switching it off. That failure is
    /// silent and total, which is the worst pair, so it is worth a named test.
    ///
    /// EXACTLY the whole of it, not most of it. A share would be a number picked to make one
    /// screenshot look right, and it would quietly discard a part that over-claims by half -
    /// which is a thing to see in the readout and switch off, not to have decided for you.
    /// </remarks>
    public bool Covers(ScreenRect other)
        => Left <= other.Left && Top <= other.Top && Right >= other.Right && Bottom >= other.Bottom;

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
    /// <remarks>
    /// RAISED FROM 16, and the reason is worth keeping: a cap that silently drops the TAIL of
    /// the list is a cap that decides which parts of the interface get honoured by their
    /// position in it. When the only source was the HUD, sixteen was far past what the
    /// interface has parts. Then the atlas arrived with a second source - the world screen's
    /// own furniture - and a third, the open panels beside it, and the honest count went to
    /// nearly thirty. The bookmarks panel sat at the end of that list and was drawn over while
    /// every part before it worked, which reads as a measurement problem and is not one.
    ///
    /// So: high enough that a real interface never reaches it, and <see cref="Refused"/> says
    /// so when it does rather than leaving the tail to be found by screenshot.
    /// </remarks>
    public const int MostKeptOut = 64;

    private static readonly ScreenRect[] Nothing = [];

    private readonly ScreenRect[] _free;

    private ScreenRegion(ScreenRect bounds, ScreenRect[] keptOut, ScreenRect[] free, int refused = 0)
    {
        Bounds = bounds;
        KeptOut = keptOut;
        _free = free;
        Refused = refused;
    }

    /// <summary>
    /// How many keep-outs were handed over and not honoured, because the cap was reached.
    /// </summary>
    /// <remarks>
    /// ALWAYS ZERO in a working tool, which is exactly why it is worth reporting: the one time
    /// it was not, the symptom was a single panel being drawn over with nothing anywhere to say
    /// that anything had been dropped.
    /// </remarks>
    public int Refused { get; }

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
        int refused = 0;
        foreach (ScreenRect rect in keepOut)
        {
            ScreenRect clipped = rect.IsSane ? rect.ClippedTo(bounds) : default;
            if (!clipped.HasArea)
            {
                continue; // off the screen, or nonsense: not refused, simply not a rectangle
            }

            // COUNTED RATHER THAN JUST STOPPED. The tail of the list is not junk - it is the
            // parts of the interface that happen to be enumerated last - so a cap being reached
            // is a thing to report, not a thing to do quietly.
            if (blocking.Count >= MostKeptOut)
            {
                refused++;
                continue;
            }

            blocking.Add(clipped);
        }

        return blocking.Count == 0
            ? Whole(bounds)
            : new ScreenRegion(bounds, [.. blocking], Carve(bounds, blocking), refused);
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

    /// <summary>
    /// Whether a whole rectangle is clear of everything kept out.
    /// </summary>
    /// <remarks>
    /// FOR THINGS THAT ARE A BOX RATHER THAN A POINT - a name plate, a reward, an icon. A label
    /// is anchored at a point and drawn a couple of hundred pixels wide around it, so the point
    /// test passes while half the writing lies across the panel it was supposed to stay off.
    ///
    /// ALL OR NOTHING, which is the right answer for text: a plate cut in half by a clip
    /// rectangle is a word broken mid-letter, and worse to look at than one that is not there.
    /// Lines are the opposite case and are cut instead - see <see cref="ClipSegment"/>.
    ///
    /// The BOUNDS are deliberately not consulted. A label near the edge of the screen is drawn
    /// half off it by the game too, and refusing those would strip the outermost row of anything
    /// this is used for.
    /// </remarks>
    public bool Clear(ScreenRect rect)
    {
        foreach (ScreenRect blocked in KeptOut)
        {
            if (blocked.Overlaps(rect))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The parts of a line that may be drawn, appended to <paramref name="into"/>.
    /// </summary>
    /// <remarks>
    /// CUT RATHER THAN DROPPED, and rather than redrawn per free piece. An atlas is a couple of
    /// thousand connections; drawing all of them once for each piece of the region would be tens
    /// of thousands of lines a frame to produce a picture that is mostly clipped away. And a
    /// line dropped whole because it clips a corner of the interface is a connection that
    /// silently is not there - on the atlas, that is the feature's whole content.
    ///
    /// So each segment is cut against the rectangles instead, in the segment's own parameter
    /// space: for each keep-out, the interval of t where the line lies inside it (the standard
    /// Liang-Barsky test), and then the gaps between those intervals are what gets drawn. A
    /// segment that touches nothing costs four comparisons per rectangle and comes back whole,
    /// which is the case almost every line is in.
    /// </remarks>
    public void ClipSegment(Vector2 from, Vector2 to, List<(Vector2 From, Vector2 To)> into)
    {
        ArgumentNullException.ThrowIfNull(into);

        if (!Overlap(from, to, Bounds, out float start, out float end))
        {
            return;
        }

        List<(float From, float To)>? blocked = null;
        foreach (ScreenRect rect in KeptOut)
        {
            if (!Overlap(from, to, rect, out float enters, out float leaves))
            {
                continue;
            }

            float lower = Math.Max(enters, start);
            float upper = Math.Min(leaves, end);
            if (upper > lower)
            {
                (blocked ??= []).Add((lower, upper));
            }
        }

        if (blocked is null)
        {
            into.Add((At(from, to, start), At(from, to, end)));
            return;
        }

        blocked.Sort((left, right) => left.From.CompareTo(right.From));

        float at = start;
        foreach ((float lower, float upper) in blocked)
        {
            if (lower - at > Sliver)
            {
                into.Add((At(from, to, at), At(from, to, lower)));
            }

            at = Math.Max(at, upper);
            if (at >= end)
            {
                return;
            }
        }

        if (end - at > Sliver)
        {
            into.Add((At(from, to, at), At(from, to, end)));
        }
    }

    /// <summary>
    /// A piece of line shorter than this along the segment is not worth emitting.
    /// </summary>
    /// <remarks>
    /// In the segment's own parameter space, so it is a share of its length rather than a
    /// distance: what it stops is a run of zero-length lines where several keep-out rectangles
    /// meet along one connection.
    /// </remarks>
    private const float Sliver = 0.0005f;

    private static Vector2 At(Vector2 from, Vector2 to, float t) => from + ((to - from) * t);

    /// <summary>
    /// The stretch of a segment that lies inside a rectangle, as a t-interval, or false.
    /// </summary>
    /// <remarks>
    /// Liang-Barsky, in the form that answers "where is it INSIDE" rather than "draw this bit":
    /// the same four comparisons then serve both the bounds (keep what is inside) and the
    /// keep-outs (drop what is inside), which is why one helper expresses both.
    /// </remarks>
    private static bool Overlap(
        Vector2 from, Vector2 to, ScreenRect rect, out float enters, out float leaves)
    {
        enters = 0f;
        leaves = 1f;

        float dx = to.X - from.X;
        float dy = to.Y - from.Y;

        Span<float> edge = [-dx, dx, -dy, dy];
        Span<float> room =
        [
            from.X - rect.Left, rect.Right - from.X,
            from.Y - rect.Top, rect.Bottom - from.Y,
        ];

        for (int i = 0; i < 4; i++)
        {
            if (edge[i] == 0f)
            {
                // Parallel to this edge: wholly inside its half-plane, or wholly outside it.
                if (room[i] < 0f)
                {
                    return false;
                }

                continue;
            }

            float crosses = room[i] / edge[i];
            if (edge[i] < 0f)
            {
                if (crosses > leaves)
                {
                    return false;
                }

                enters = Math.Max(enters, crosses);
            }
            else
            {
                if (crosses < enters)
                {
                    return false;
                }

                leaves = Math.Min(leaves, crosses);
            }
        }

        return leaves > enters;
    }

    /// <summary>What the region covers, for a readout.</summary>
    public override string ToString()
        => $"{Bounds.Width:F0}x{Bounds.Height:F0} at {Bounds.Left:F0},{Bounds.Top:F0}"
           + $" less {KeptOut.Count} in {_free.Length} pieces"
           + (Refused > 0 ? $"   ({Refused} REFUSED - past the cap of {MostKeptOut})" : string.Empty);

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
