using System.Numerics;
using PoEformance.Game.Ui;

namespace PoEformance.Core.Tests;

/// <summary>
/// The region the map may be drawn on: a rectangle with the game's interface cut out of it.
/// </summary>
/// <remarks>
/// THE CHECK THAT MATTERS is <see cref="Carved_IsAPartitionOfWhatIsLeft"/>, and it is worth
/// saying why the others are not enough. Every layer that draws continuous geometry draws it
/// once per free piece, so a set of pieces that OVERLAP paints those pixels twice - which for
/// a translucent heat patch or a route line is visibly wrong and for a solid one is not, so
/// eyeballing it settles nothing. A set that leaves a GAP loses a strip of map, which looks
/// exactly like a projection that is slightly off. Both failures are invisible in a screenshot
/// and both are arithmetic, so they are checked as arithmetic: total area, no overlaps, and
/// nothing outside the bounds or inside a hole.
/// </remarks>
public class ScreenRegionTests
{
    private static readonly ScreenRect Screen = ScreenRect.Window(2560f, 1440f);

    [Fact]
    public void Whole_IsOnePieceAndAcceptsEverythingInside()
    {
        ScreenRegion region = ScreenRegion.Whole(Screen);

        Assert.Equal(Screen, Assert.Single(region.Free));
        Assert.True(region.Contains(new Vector2(1f, 1f)));
        Assert.True(region.Contains(new Vector2(2559f, 1439f)));
        Assert.False(region.Contains(new Vector2(2560f, 720f)));
        Assert.False(region.Contains(new Vector2(-1f, 720f)));
    }

    [Fact]
    public void ABandAcrossTheBottom_LeavesOneRectangleAboveIt()
    {
        // The default keep-out and the case worth keeping cheap: PoE2's interface is all along
        // the bottom edge, so the answer is a single rectangle and the terrain quad is drawn
        // once. A sweep that did not merge its slabs back together would say three.
        ScreenRegion region = ScreenRegion.Of(
            Screen, [new ScreenRect(0f, 1152f, 2560f, 1440f)]);

        ScreenRect free = Assert.Single(region.Free);
        Assert.Equal(new ScreenRect(0f, 0f, 2560f, 1152f), free);
        Assert.True(region.Contains(new Vector2(1280f, 1151f)));
        Assert.False(region.Contains(new Vector2(1280f, 1153f)));
    }

    [Fact]
    public void AHoleInTheMiddle_IsCutOutWithoutLosingTheRestOfTheScreen()
    {
        var hole = new ScreenRect(1000f, 600f, 1600f, 900f);
        ScreenRegion region = ScreenRegion.Of(Screen, [hole]);

        Assert.False(region.Contains(new Vector2(1300f, 700f)));
        Assert.True(region.Contains(new Vector2(999f, 700f)));
        Assert.True(region.Contains(new Vector2(1300f, 599f)));
        AssertPartition(Screen, region, [hole]);
    }

    [Fact]
    public void TwoCornersAndAStrip_StillCoverExactlyWhatIsLeft()
    {
        // The shape somebody ends up with after carving the default band into the pieces their
        // own screen actually needs: an orb at each bottom corner and the experience strip.
        ScreenRect[] holes =
        [
            new ScreenRect(0f, 1150f, 300f, 1440f),
            new ScreenRect(2260f, 1150f, 2560f, 1440f),
            new ScreenRect(0f, 1400f, 2560f, 1440f),
        ];

        ScreenRegion region = ScreenRegion.Of(Screen, holes);

        Assert.True(region.Contains(new Vector2(1280f, 1300f)));  // between the two orbs
        Assert.False(region.Contains(new Vector2(100f, 1300f)));  // on the left orb
        Assert.False(region.Contains(new Vector2(1280f, 1420f))); // on the strip
        AssertPartition(Screen, region, holes);
    }

    [Fact]
    public void OverlappingKeepOuts_AreNotCountedTwice()
    {
        // Two zones a user dragged across each other, which is the ordinary way to end up with
        // an L shape. The pieces must still partition the rest - a sweep that treated the
        // overlap as blocked twice would produce a zero-height rectangle in the middle.
        ScreenRect[] holes =
        [
            new ScreenRect(400f, 400f, 1200f, 800f),
            new ScreenRect(900f, 600f, 1600f, 1100f),
        ];

        ScreenRegion region = ScreenRegion.Of(Screen, holes);

        Assert.False(region.Contains(new Vector2(1000f, 700f))); // inside both
        AssertPartition(Screen, region, holes);
    }

    [Fact]
    public void KeepOutsAreClippedToTheBounds_AndTheOnesEntirelyOutsideAreDropped()
    {
        // A zone dragged half off the screen, and one belonging to a map that is nowhere near
        // it. Neither may widen the region or produce a rectangle with negative area.
        ScreenRegion region = ScreenRegion.Of(
            Screen,
            [
                new ScreenRect(-500f, -500f, 200f, 200f),
                new ScreenRect(4000f, 400f, 4200f, 600f),
            ]);

        ScreenRect kept = Assert.Single(region.KeptOut);
        Assert.Equal(new ScreenRect(0f, 0f, 200f, 200f), kept);
        AssertPartition(Screen, region, [kept]);
    }

    [Fact]
    public void NonsenseKeepOutsAreIgnoredRatherThanBelieved()
    {
        // A torn read, or a settings file somebody edited by hand. The region has to come back
        // describing pixels that exist, because everything downstream draws into it.
        ScreenRegion region = ScreenRegion.Of(
            Screen,
            [
                new ScreenRect(float.NaN, 0f, 100f, 100f),
                new ScreenRect(500f, 500f, 400f, 400f),   // inside out
                new ScreenRect(0f, 0f, 1e9f, 1e9f),       // absurd
            ]);

        Assert.Empty(region.KeptOut);
        Assert.Equal(Screen, Assert.Single(region.Free));
    }

    [Fact]
    public void AMapCoveredEntirelyHasNothingToDrawOn()
    {
        // Not a failure - it is what "the interface is over all of it" has to come out as, and
        // the caller checks exactly this before drawing rather than pushing an empty clip.
        ScreenRegion region = ScreenRegion.Of(Screen, [Screen]);

        Assert.Empty(region.Free);
        Assert.False(region.HasAnything);
        Assert.False(region.Contains(new Vector2(1280f, 720f)));
    }

    [Fact]
    public void MoreKeepOutsThanTheCapAreDroppedRatherThanSweptOver()
    {
        // The sweep is quadratic in the number of zones and every piece it makes is a redraw,
        // so a list that grew without bound would show up as a frozen overlay. The cap makes
        // that a wrong picture instead, which is the failure that can be seen and reported.
        List<ScreenRect> many = [];
        for (int i = 0; i < ScreenRegion.MostKeptOut + 8; i++)
        {
            many.Add(new ScreenRect(i * 20f, 0f, (i * 20f) + 10f, 100f));
        }

        ScreenRegion region = ScreenRegion.Of(Screen, many);

        Assert.Equal(ScreenRegion.MostKeptOut, region.KeptOut.Count);
    }

    [Fact]
    public void Carved_IsAPartitionOfWhatIsLeft()
    {
        // The general statement, over a spread of arrangements rather than one picture: for any
        // set of holes the free pieces cover everything outside them, nothing inside them, and
        // no pixel twice. A seeded sequence so a failure is reproducible.
        var random = new Random(20260830);

        for (int round = 0; round < 60; round++)
        {
            List<ScreenRect> holes = [];
            int count = random.Next(1, 5);
            for (int i = 0; i < count; i++)
            {
                float left = random.Next(0, 2400);
                float top = random.Next(0, 1300);
                holes.Add(new ScreenRect(
                    left, top, left + random.Next(40, 600), top + random.Next(40, 400)));
            }

            ScreenRegion region = ScreenRegion.Of(Screen, holes);
            AssertPartition(Screen, region, region.KeptOut);
        }
    }

    [Fact]
    public void ALineThroughAKeepOutIsCutRatherThanDropped()
    {
        // WHY CUT AND NOT DROP: on the atlas the lines ARE the content, and a connection removed
        // because it clips the corner of an open panel is a route that silently is not there.
        ScreenRegion region = ScreenRegion.Of(Screen, [new ScreenRect(1000f, 0f, 1400f, 1440f)]);

        List<(Vector2 From, Vector2 To)> pieces = [];
        region.ClipSegment(new Vector2(0f, 700f), new Vector2(2560f, 700f), pieces);

        Assert.Equal(2, pieces.Count);
        Assert.Equal(new Vector2(0f, 700f), pieces[0].From);
        Assert.Equal(new Vector2(1000f, 700f), pieces[0].To);
        Assert.Equal(new Vector2(1400f, 700f), pieces[1].From);
        Assert.Equal(new Vector2(2560f, 700f), pieces[1].To);
    }

    [Fact]
    public void ALineClearOfEverythingComesBackWhole()
    {
        // The case almost every line is in, and the one that has to stay cheap: no allocation
        // of intervals, one piece out, the same two endpoints in.
        ScreenRegion region = ScreenRegion.Of(Screen, [new ScreenRect(0f, 1152f, 2560f, 1440f)]);

        List<(Vector2 From, Vector2 To)> pieces = [];
        region.ClipSegment(new Vector2(100f, 200f), new Vector2(900f, 400f), pieces);

        (Vector2 from, Vector2 to) = Assert.Single(pieces);
        Assert.Equal(new Vector2(100f, 200f), from);
        Assert.Equal(new Vector2(900f, 400f), to);
    }

    [Fact]
    public void ALineWhollyInsideAKeepOutDisappears()
    {
        ScreenRegion region = ScreenRegion.Of(Screen, [new ScreenRect(900f, 600f, 1600f, 900f)]);

        List<(Vector2 From, Vector2 To)> pieces = [];
        region.ClipSegment(new Vector2(1000f, 700f), new Vector2(1500f, 800f), pieces);

        Assert.Empty(pieces);
    }

    [Fact]
    public void ALineIsCutByEveryKeepOutItCrosses()
    {
        // Several bites out of one connection, which is the ordinary case on the atlas: the
        // interface is a row of parts along the bottom and a line runs the width of the screen.
        ScreenRegion region = ScreenRegion.Of(
            Screen,
            [
                new ScreenRect(300f, 0f, 500f, 1440f),
                new ScreenRect(1200f, 0f, 1500f, 1440f),
                new ScreenRect(2000f, 0f, 2100f, 1440f),
            ]);

        List<(Vector2 From, Vector2 To)> pieces = [];
        region.ClipSegment(new Vector2(0f, 100f), new Vector2(2560f, 100f), pieces);

        Assert.Equal(4, pieces.Count);
        Assert.Equal(0f, pieces[0].From.X);
        Assert.Equal(300f, pieces[0].To.X);
        Assert.Equal(500f, pieces[1].From.X);
        Assert.Equal(2560f, pieces[3].To.X);
        Assert.All(pieces, piece => Assert.Equal(100f, piece.From.Y));
    }

    [Fact]
    public void OverlappingKeepOutsCutOneHoleInALineRatherThanTwo()
    {
        // The intervals have to be merged, or the second one re-opens the piece the first
        // closed and a stub of line is drawn back across the panel.
        ScreenRegion region = ScreenRegion.Of(
            Screen,
            [
                new ScreenRect(600f, 0f, 1200f, 1440f),
                new ScreenRect(1000f, 0f, 1800f, 1440f),
            ]);

        List<(Vector2 From, Vector2 To)> pieces = [];
        region.ClipSegment(new Vector2(0f, 500f), new Vector2(2560f, 500f), pieces);

        Assert.Equal(2, pieces.Count);
        Assert.Equal(600f, pieces[0].To.X);
        Assert.Equal(1800f, pieces[1].From.X);
    }

    [Fact]
    public void ALineIsAlsoCutAtTheEdgeOfTheScreen()
    {
        // The atlas pans, so most of its connections run off the side. The visible part is what
        // gets drawn, and a segment entirely outside contributes nothing.
        ScreenRegion region = ScreenRegion.Whole(Screen);

        List<(Vector2 From, Vector2 To)> pieces = [];
        region.ClipSegment(new Vector2(-500f, 300f), new Vector2(500f, 300f), pieces);

        (Vector2 from, Vector2 to) = Assert.Single(pieces);
        Assert.Equal(0f, from.X, 3);
        Assert.Equal(500f, to.X, 3);

        pieces.Clear();
        region.ClipSegment(new Vector2(-900f, 300f), new Vector2(-500f, 300f), pieces);
        Assert.Empty(pieces);
    }

    [Fact]
    public void EveryPieceOfACutLineIsClearAndEveryGapIsBlocked()
    {
        // The general statement, sampled along the line rather than trusted from the endpoints:
        // whatever comes back must be drawable end to end, and whatever was removed must have
        // been inside something. A seeded sequence so a failure is reproducible.
        var random = new Random(20260831);

        for (int round = 0; round < 80; round++)
        {
            List<ScreenRect> holes = [];
            for (int i = 0; i < random.Next(1, 4); i++)
            {
                float left = random.Next(0, 2200);
                float top = random.Next(0, 1200);
                holes.Add(new ScreenRect(
                    left, top, left + random.Next(60, 700), top + random.Next(60, 500)));
            }

            ScreenRegion region = ScreenRegion.Of(Screen, holes);
            var from = new Vector2(random.Next(0, 2560), random.Next(0, 1440));
            var to = new Vector2(random.Next(0, 2560), random.Next(0, 1440));

            List<(Vector2 From, Vector2 To)> pieces = [];
            region.ClipSegment(from, to, pieces);

            foreach ((Vector2 start, Vector2 end) in pieces)
            {
                for (int step = 1; step < 20; step++)
                {
                    Vector2 along = Vector2.Lerp(start, end, step / 20f);
                    Assert.True(region.Contains(along), $"a drawn piece runs through {along}");
                }
            }

            // And nothing drawable was thrown away: every sample the pieces do not cover has to
            // be one the region would have refused anyway.
            for (int step = 0; step <= 40; step++)
            {
                Vector2 along = Vector2.Lerp(from, to, step / 40f);
                if (!region.Contains(along))
                {
                    continue;
                }

                Assert.Contains(pieces, piece => Near(piece, along));
            }
        }
    }

    /// <summary>Whether a point lies on a piece, within a pixel of rounding.</summary>
    private static bool Near((Vector2 From, Vector2 To) piece, Vector2 point)
    {
        Vector2 along = piece.To - piece.From;
        float length = along.LengthSquared();
        if (length < 0.0001f)
        {
            return Vector2.Distance(piece.From, point) < 1f;
        }

        float t = Math.Clamp(Vector2.Dot(point - piece.From, along) / length, 0f, 1f);
        return Vector2.Distance(piece.From + (along * t), point) < 1f;
    }

    [Fact]
    public void AKeepOutTheSizeOfWhatItCoversIsRecognisable()
    {
        // THE FAILURE THIS ANSWERS, and it took two rounds of screenshots to pin down: the world
        // screen the atlas sits on holds furniture that genuinely covers the screen - a vignette,
        // a fade, an input catcher - and honouring one of those is not "keep off that bit", it is
        // "keep off everything". The atlas overlay went blank with every measurement in the
        // readout looking healthy. The caller drops these one at a time so the title bar and the
        // orbs keep working, rather than the whole region collapsing.
        ScreenRect screen = Screen;

        Assert.True(screen.Covers(screen));                                  // exactly the screen
        Assert.True(new ScreenRect(-10f, -10f, 3000f, 2000f).Covers(screen)); // and larger still

        // EXACTLY the whole of it, not most of it. A part that over-claims by half is a thing to
        // see in the readout and switch off, not one to have discarded on your behalf.
        Assert.False(new ScreenRect(0f, 0f, 2560f, 1439f).Covers(screen));
        Assert.False(new ScreenRect(1f, 0f, 2560f, 1440f).Covers(screen));
        Assert.False(new ScreenRect(0f, 1152f, 2560f, 1440f).Covers(screen)); // an ordinary band
    }

    [Fact]
    public void ABoxOfWritingIsAllOrNothing()
    {
        // Text is the opposite case from a line: half a name plate cut off by the edge of a
        // panel is a word broken mid-letter, and worse to look at than a label that is absent.
        ScreenRegion region = ScreenRegion.Of(Screen, [new ScreenRect(1000f, 600f, 1600f, 900f)]);

        Assert.True(region.Clear(new ScreenRect(200f, 200f, 500f, 240f)));
        Assert.False(region.Clear(new ScreenRect(900f, 700f, 1100f, 740f)));  // clips the edge
        Assert.False(region.Clear(new ScreenRect(1100f, 700f, 1300f, 740f))); // wholly inside

        // The bounds are deliberately not consulted: a label at the edge of the screen is drawn
        // half off it by the game too, and refusing those would strip the outermost row.
        Assert.True(region.Clear(new ScreenRect(-100f, 200f, 100f, 240f)));
    }

    /// <summary>
    /// Asserts the free pieces are exactly the bounds less the holes: every point that may be
    /// drawn on is in ONE piece, every point that may not is in none.
    /// </summary>
    /// <remarks>
    /// Checked by SAMPLING rather than by a second sweep, because a second implementation of the
    /// same idea agreeing with the first only shows that one person wrote both. Every cell of
    /// the grid the hole edges induce is uniform - a cell cannot be half blocked, since a
    /// boundary would have split it - so one point per cell decides the whole cell, and the
    /// check is exact rather than approximate despite being a sample.
    /// </remarks>
    private static void AssertPartition(
        ScreenRect bounds, ScreenRegion region, IReadOnlyList<ScreenRect> holes)
    {
        foreach (ScreenRect piece in region.Free)
        {
            Assert.True(piece.HasArea, $"a piece with no area: {piece}");
            Assert.Equal(piece, piece.ClippedTo(bounds));
        }

        for (int i = 0; i < region.Free.Count; i++)
        {
            for (int j = i + 1; j < region.Free.Count; j++)
            {
                Assert.False(
                    region.Free[i].Overlaps(region.Free[j]),
                    $"pieces {region.Free[i]} and {region.Free[j]} overlap");
            }
        }

        foreach (Vector2 point in CellCentres(bounds, holes))
        {
            bool blocked = holes.Any(hole => hole.Contains(point));
            int pieces = region.Free.Count(piece => piece.Contains(point));

            Assert.Equal(blocked ? 0 : 1, pieces);
            Assert.Equal(!blocked, region.Contains(point));
        }
    }

    /// <summary>One point inside each cell of the grid the hole edges cut the bounds into.</summary>
    private static IEnumerable<Vector2> CellCentres(
        ScreenRect bounds, IReadOnlyList<ScreenRect> holes)
    {
        List<float> xs = [bounds.Left, bounds.Right];
        List<float> ys = [bounds.Top, bounds.Bottom];
        foreach (ScreenRect hole in holes)
        {
            xs.Add(hole.Left);
            xs.Add(hole.Right);
            ys.Add(hole.Top);
            ys.Add(hole.Bottom);
        }

        xs.Sort();
        ys.Sort();

        for (int i = 1; i < xs.Count; i++)
        {
            if (xs[i] - xs[i - 1] < 1f)
            {
                continue; // narrower than a pixel: the sweep drops these, deliberately
            }

            for (int j = 1; j < ys.Count; j++)
            {
                if (ys[j] - ys[j - 1] >= 1f)
                {
                    yield return new Vector2(
                        (xs[i] + xs[i - 1]) / 2f, (ys[j] + ys[j - 1]) / 2f);
                }
            }
        }
    }
}
