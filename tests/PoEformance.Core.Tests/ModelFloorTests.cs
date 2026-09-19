using System.Numerics;
using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// The floor under a model, measured as the lines it hands the overlay.
/// </summary>
/// <remarks>
/// EVERYTHING HERE IS A SUM the overlay would otherwise have to be trusted with: where the axes
/// cross, how far apart the squares are in the game's units, how a level fades out and the one
/// above takes over, and which side of the picture the floor goes on. The line list is the
/// whole of what the overlay draws, so a rule that holds on the list holds on the screen.
/// </remarks>
public class ModelFloorTests
{
    /// <summary>Nothing to look at, or a floor seen edge on, is no lines rather than a throw.</summary>
    [Fact]
    public void NothingIsHandedOverWithoutAModelOrSeenEdgeOn()
    {
        Assert.Empty(Floor(null));
        Assert.Empty(Floor(SkinnedMesh.None));
        Assert.Empty(Floor(Post(), tilt: 0f));
        Assert.Empty(Floor(Post(), side: 0f));
        Assert.Empty(Floor(Post(), tile: 0f));
        Assert.Empty(Floor(Post(), tile: float.NaN));
        Assert.Empty(Floor(Post(), lens: 0f));
        Assert.NotEmpty(Floor(Post()));

        Assert.Throws<ArgumentNullException>(
            () => ModelFloor.Of(null!, MeshPicture.Camera.Of(Post(), 0f, 0.5f, 1f, Vector2.Zero), 400f));
    }

    /// <summary>The axes cross at the model's origin and run along its own x and y - not at the bottom of its box.</summary>
    /// <remarks>
    /// MEASURED ON A REAL RIG: the feet sit at z = 0.98 in the bind pose and stay there through
    /// every frame of an animation while the pelvis dips fifteen units - the game plants a monster
    /// by its origin. Two monsters in a row showed the box bottom wrong from the live client, one
    /// shin-deep in the floor and the next hovering over it. The sunk post's box runs ten units
    /// past its origin, so the two places are forty pixels apart on the picture.
    ///
    /// WHICH AXIS IS WHICH IS THE OTHER HALF: the line of constant x runs ALONG y and is the y
    /// axis. Swapped, the axes are drawn in each other's colour and nothing else changes. At the
    /// origin the floor is drawn at the model's own scale, so the axes leave it the way the
    /// orthographic model's axes do, and only bend away from that further out.
    /// </remarks>
    [Fact]
    public void TheAxesCrossAtTheOriginAndRunAlongTheModelsAxes()
    {
        MeshPicture.Camera camera = MeshPicture.Camera.Of(Sunk(), 0.4f, 0.6f, 1f, Vector2.Zero);
        var lines = new List<ModelFloor.Line>();
        ModelFloor.Of(lines, camera, 400f);

        Vector2 origin = Flat(camera.Place(Vector3.Zero));
        Vector2 alongX = Flat(camera.Place(Vector3.UnitX)) - origin;
        Vector2 alongY = Flat(camera.Place(Vector3.UnitY)) - origin;
        Vector2 bottom = Flat(camera.Place(new Vector3(0f, 0f, 10f)));

        List<ModelFloor.Line> x = lines.Where(one => one.Stroke == ModelFloor.Stroke.AxisX).ToList();
        List<ModelFloor.Line> y = lines.Where(one => one.Stroke == ModelFloor.Stroke.AxisY).ToList();
        Assert.NotEmpty(x);
        Assert.NotEmpty(y);

        Assert.All(x, piece => Assert.True(Through(piece, origin), "an x axis piece is off a line through the origin"));
        Assert.All(y, piece => Assert.True(Through(piece, origin), "a y axis piece is off a line through the origin"));
        Assert.All(x, piece => Assert.False(Through(piece, bottom), "the x axis runs through the box's bottom"));

        // Leaving the origin, each axis runs nearer the way the model's own axis does than the
        // way the other one does - nearer rather than exactly, since a line's picture in
        // perspective leans by how far its point is from the picture's centre.
        Vector2 alongXUnit = Vector2.Normalize(alongX);
        Vector2 alongYUnit = Vector2.Normalize(alongY);
        ModelFloor.Line nearX = x.OrderBy(piece => Vector2.Distance(piece.From, origin)).First();
        ModelFloor.Line nearY = y.OrderBy(piece => Vector2.Distance(piece.From, origin)).First();
        Assert.True(
            MathF.Abs(Cross(Direction(nearX), alongXUnit)) < MathF.Abs(Cross(Direction(nearX), alongYUnit)),
            "the x axis leaves the origin nearer the model's y than its x");
        Assert.True(
            MathF.Abs(Cross(Direction(nearY), alongYUnit)) < MathF.Abs(Cross(Direction(nearY), alongXUnit)),
            "the y axis leaves the origin nearer the model's x than its y");
        Assert.True(MathF.Abs(Cross(Direction(nearX), alongXUnit)) < 0.05f, "the x axis leaves the origin far off the model's x");
    }

    /// <summary>The floor runs into the distance: lines of a family meet at one point, and fade towards it.</summary>
    /// <remarks>
    /// THE REPORT FROM THE LIVE CLIENT ON THE ORTHOGRAPHIC FLOOR: from a little above, where a
    /// monster is actually looked at from, parallel lines pressed flat lose everything that made
    /// the floor a floor. In perspective the lines of one family meet at a vanishing point - so,
    /// looking along the model's y, every line of constant x crosses the y axis at the same place
    /// - and along each of them the ink falls off towards that point as the lines crowd. With the
    /// eye moved off to a great distance the same lines are parallel again, which is what shows
    /// the lens is doing it.
    /// </remarks>
    [Fact]
    public void TheFloorRunsIntoTheDistance()
    {
        MeshPicture.Camera camera = MeshPicture.Camera.Of(Post(), 0f, 0.4f, 0.5f, Vector2.Zero);
        var lines = new List<ModelFloor.Line>();
        ModelFloor.Of(lines, camera, 600f);

        ModelFloor.Line axis = lines.First(piece => piece.Stroke == ModelFloor.Stroke.AxisY);
        Vector2 axisDirection = Vector2.Normalize(axis.To - axis.From);
        List<ModelFloor.Line> slanted = lines
            .Where(piece => piece.Stroke == ModelFloor.Stroke.Grid && MathF.Abs(Direction(piece).Y) > 0.2f)
            .ToList();
        Assert.True(slanted.Count > 20, $"only {slanted.Count} slanted pieces to judge by");

        Vector2? vanishing = null;
        foreach (ModelFloor.Line piece in slanted)
        {
            Vector2 met = Meet(piece, axis.From, axisDirection);
            vanishing ??= met;
            Assert.True(Vector2.Distance(met, vanishing.Value) < 2e-3f, $"a line crosses the y axis at {met}, not {vanishing}");
        }

        // Along a line, the ink falls towards the vanishing point and never rises. A line's pieces
        // are handed over in order from its near end, each starting where the last one ended,
        // which is how they are told apart here; the frame's own fade is taken back out first.
        var runs = 0;
        var dropped = 0;
        var last = float.MaxValue;
        var first = float.NaN;
        ModelFloor.Line? before = null;
        foreach (ModelFloor.Line piece in slanted)
        {
            if (before is null || Vector2.Distance(before.Value.To, piece.From) > 1e-5f)
            {
                runs++;
                dropped += first - last > 0.1f ? 1 : 0;
                last = float.MaxValue;
                first = float.NaN;
            }

            float fromFade = ModelFloor.Vignette(piece.From);
            float toFade = ModelFloor.Vignette(piece.To);
            if (fromFade > 0.05f && toFade > 0.05f)
            {
                float near = piece.FromAlpha / fromFade;
                float far = piece.ToAlpha / toFade;
                Assert.True(near <= last + 1e-3f && far <= near + 1e-3f, "the ink rises towards the vanishing point");
                last = far;
                if (float.IsNaN(first))
                {
                    first = near;
                }
            }

            before = piece;
        }

        dropped += first - last > 0.1f ? 1 : 0;
        Assert.True(runs > 5, $"only {runs} lines to follow");
        Assert.True(dropped > 3, $"only {dropped} lines actually fade towards the distance");

        // With the eye a million lengths away, the lines of a family are parallel.
        ModelFloor.Of(lines, camera, 600f, ModelFloor.Tile, 1e6f);
        List<ModelFloor.Line> flat = lines
            .Where(piece => piece.Stroke == ModelFloor.Stroke.Grid && MathF.Abs(Direction(piece).Y) > 0.2f)
            .ToList();
        Assert.True(flat.Count > 20, $"only {flat.Count} slanted pieces far away");
        ModelFloor.Line farAxis = lines.First(piece => piece.Stroke == ModelFloor.Stroke.AxisY);
        Assert.All(flat, piece => Assert.True(Parallel(piece, farAxis.To - farAxis.From), "a distant eye still converges the lines"));
    }

    /// <summary>The squares are decades of the game's tile in the model's units, whatever the model's size.</summary>
    /// <remarks>
    /// "A BOSS STANDS ON MORE SQUARES THAN A RAT", which the rasterised floor got backwards by
    /// dividing the model's own box into ten. A tile is 250 world units, and the game draws a mesh
    /// scaled by its variety's multiplier - so in the mesh's units a tile is 250 over that, and
    /// a monster drawn at 300 stands on tiles a third the size. Looking straight down, where the
    /// floor's x is the picture's x, the spacing between neighbouring lines is read straight off.
    /// </remarks>
    [Theory]
    [InlineData(40f, 100)]
    [InlineData(80f, 100)]
    [InlineData(40f, 300)]
    public void TheSquaresAreDecadesOfTheGamesTileInTheModelsUnits(float tall, int modelSize)
    {
        SkinnedMesh post = Post(tall);
        float tile = ModelFloor.TileOn(modelSize);
        MeshPicture.Camera camera = MeshPicture.Camera.Of(post, 0f, MathF.PI / 2f, 1f, Vector2.Zero);
        var lines = new List<ModelFloor.Line>();
        ModelFloor.Of(lines, camera, 400f, tile);

        float originX = camera.Place(Vector3.Zero).X;
        List<float> at = Uprights(lines).Select(x => (x - originX) / camera.Scale).OrderBy(x => x).ToList();
        Assert.True(at.Count >= 3, $"only {at.Count} upright lines to measure between");

        for (var one = 1; one < at.Count; one++)
        {
            float spacing = at[one] - at[one - 1];
            float decade = MathF.Log10(spacing / tile);
            Assert.True(
                MathF.Abs(decade - MathF.Round(decade)) < 1e-3f,
                $"lines {spacing:F3} units apart, which is not a decade of a {tile:F2} unit tile");
        }

        // The tile itself is where the game puts it, and never zero.
        Assert.Equal(250f, ModelFloor.TileOn(100));
        Assert.Equal(125f, ModelFloor.TileOn(200));
        Assert.Equal(500f, ModelFloor.TileOn(50));
        Assert.Equal(250f, ModelFloor.TileOn(0));
        Assert.Equal(250f, ModelFloor.TileOn(-7));
    }

    /// <summary>The finest level fades as its lines crowd, the level above is the brighter one, and nothing pops at the change of level.</summary>
    /// <remarks>
    /// BLENDER'S RULE, which is what makes its floor readable at every zoom: a line's emphasis is
    /// how whole the level below it is, so as the finest level fades to nothing the level above
    /// slides from emphasised to faint in the same motion, and is then the finest level itself at
    /// the ink it was just drawn in. Pulling back through eight octaves of zoom, one line is
    /// followed from whole to gone and never moves by more than a tenth between neighbouring
    /// zooms - a floor that switched levels would jump by half.
    /// </remarks>
    [Fact]
    public void TheFinestLevelFadesAsItCrowdsAndTheNextTakesOverWithoutAPop()
    {
        SkinnedMesh post = Post();
        var lines = new List<ModelFloor.Line>();

        // A tenth of a tile against a hundredth, side by side at one zoom: the coarser is brighter.
        MeshPicture.Camera camera = MeshPicture.Camera.Of(post, 0f, MathF.PI / 2f, 0.7f, Vector2.Zero);
        ModelFloor.Of(lines, camera, 400f);
        float coarse = Strongest(lines, camera, 25f);
        float fine = Strongest(lines, camera, 2.5f);
        Assert.True(fine > 0f, "the hundredth-tile line is missing");
        Assert.True(coarse > fine * 2f, $"a tenth of a tile at {coarse:F3} should be plainly brighter than a hundredth at {fine:F3}");

        // THE INK ITSELF, ON A PANE WIDE ENOUGH TO HOLD BOTH: with the finest level whole and the
        // one under it not yet showing, the finest is drawn at half ink - Blender's grid against
        // its emphasis - and the level above at the full ink, not merely at more.
        camera = MeshPicture.Camera.Of(post, 0f, MathF.PI / 2f, 0.6f, Vector2.Zero);
        ModelFloor.Of(lines, camera, 1200f);
        float whole = Strongest(lines, camera, 2.5f);
        float emphasised = Strongest(lines, camera, 25f);
        Assert.InRange(whole, ModelFloor.Faint - 0.02f, ModelFloor.Faint + 0.02f);
        Assert.True(emphasised > 0.95f, $"the level above a whole level should be at full ink, not {emphasised:F3}");

        // And pulling back, a line goes from whole to gone in small steps.
        float last = float.NaN;
        var seen = 0;
        for (float zoom = MeshPicture.Furthest; zoom >= MeshPicture.Nearest; zoom *= 0.97f)
        {
            camera = MeshPicture.Camera.Of(post, 0f, MathF.PI / 2f, zoom, Vector2.Zero);
            ModelFloor.Of(lines, camera, 400f);
            float now = Strongest(lines, camera, 2.5f);
            if (!float.IsNaN(last))
            {
                Assert.True(MathF.Abs(now - last) < 0.1f, $"the line jumped from {last:F3} to {now:F3} at zoom {zoom:F3}");
            }

            seen += now > 0f ? 1 : 0;
            last = now;
        }

        Assert.True(seen > 20, $"the line was only seen at {seen} zooms");
        Assert.Equal(0f, last);
    }

    /// <summary>A family pressed together by the tilt is fainter than the one that keeps its spacing.</summary>
    /// <remarks>
    /// THE LIVE REPORT ON THE RASTERISED FLOOR: seen nearly level, the lines that run across the
    /// picture were pressed into a solid band while the ones running into the depth stayed apart.
    /// Each family fades by its own spacing, so tilted most of the way down to level the family
    /// across is at a fraction of the other's ink - and straight down, where nothing is pressed,
    /// the two are the same, which is what shows the check can fail. With the eye a million
    /// lengths away, so that the spacing is the same at every place and a line can be found by
    /// where the model's own camera puts it.
    /// </remarks>
    [Fact]
    public void AFamilyPressedTogetherByTheTiltIsFainterThanTheOneThatIsNot()
    {
        (float across, float into) = Families(0.15f);
        Assert.True(into > 0f && across > 0f, "a family is missing");
        Assert.True(across < into * 0.7f, $"across at {across:F3} should be well under into at {into:F3}");

        (float evenAcross, float evenInto) = Families(MathF.PI / 2f);
        Assert.InRange(evenAcross, evenInto - 1e-3f, evenInto + 1e-3f);
    }

    /// <summary>Every piece lies in the frame, is short, and has faded to nothing by the frame's edge.</summary>
    /// <remarks>
    /// The floor reaches for ever, so the frame is what bounds it, and a floor that ended in a
    /// hard line at the frame's edge was the live client's second report on the floor. The fade is
    /// carried by the pieces' ends, which is why a piece has to be short: between its ends a line
    /// is straight, and a piece a third of the frame long would show the curve as corners.
    /// </remarks>
    [Fact]
    public void PiecesStayInTheFrameAndFadeToNothingAtItsEdge()
    {
        List<ModelFloor.Line> lines = Floor(Post(), turn: 0.7f, tilt: 0.6f, zoom: 0.5f);
        Assert.NotEmpty(lines);

        var middle = 0f;
        foreach (ModelFloor.Line piece in lines)
        {
            Assert.InRange(piece.From.X, -1e-4f, 1f + 1e-4f);
            Assert.InRange(piece.From.Y, -1e-4f, 1f + 1e-4f);
            Assert.InRange(piece.To.X, -1e-4f, 1f + 1e-4f);
            Assert.InRange(piece.To.Y, -1e-4f, 1f + 1e-4f);
            Assert.True(Vector2.Distance(piece.From, piece.To) <= ModelFloor.Piece + 1e-4f, "a piece is too long to bend");

            Assert.True(piece.FromAlpha <= ModelFloor.Vignette(piece.From) + 1e-5f, "a piece is brighter than the frame allows");
            Assert.True(piece.ToAlpha <= ModelFloor.Vignette(piece.To) + 1e-5f, "a piece is brighter than the frame allows");

            if (Vector2.Distance(piece.From, new Vector2(0.5f)) < 0.2f)
            {
                middle = MathF.Max(middle, piece.FromAlpha);
            }
        }

        Assert.True(middle > 0.5f, $"the floor should be solid near the middle, not {middle:F3}");
        Assert.Equal(0f, ModelFloor.Vignette(new Vector2(0f, 0.5f)));
        Assert.Equal(0f, ModelFloor.Vignette(new Vector2(1f, 1f)));
        Assert.Equal(1f, ModelFloor.Vignette(new Vector2(0.5f)));
    }

    /// <summary>The floor is whole from a little above, and only lets go in the last degrees to edge on.</summary>
    /// <remarks>
    /// THE SECOND REPORT ON THE FLOOR FROM THE LIVE CLIENT: Blender's fade by the cube of the
    /// view's drop took the floor away at exactly the angles a monster is looked at from. So at
    /// eight degrees the floor is whole, at three it is half way gone, and at one it is all but
    /// gone - the axes say so, since nothing else fades them.
    /// </remarks>
    [Theory]
    [InlineData(0.15f, 0.98f, 1f)]
    [InlineData(0.05f, 0.45f, 0.55f)]
    [InlineData(0.02f, 0.05f, 0.15f)]
    public void TheFloorIsWholeFromALittleAboveAndOnlyGoesEdgeOn(float tilt, float least, float most)
    {
        float axes = Floor(Post(), tilt: tilt)
            .Where(piece => piece.Stroke != ModelFloor.Stroke.Grid)
            .Max(piece => MathF.Max(Raw(piece), 0f));
        Assert.InRange(axes, least, most);
    }

    /// <summary>The floor is behind the model from above and in front of it from below.</summary>
    /// <remarks>
    /// CHECKED AGAINST THE CAMERA'S OWN DEPTHS rather than asserted from the sign convention: a
    /// point of the model above the floor is placed, the floor point behind it on the picture is
    /// found through the floor's affine map, and the camera says which of the two is nearer. Under
    /// has to agree at every tilt the pane allows, in both directions.
    /// </remarks>
    [Theory]
    [InlineData(0.05f)]
    [InlineData(0.5f)]
    [InlineData(1.047f)]
    [InlineData(-0.05f)]
    [InlineData(-0.5f)]
    [InlineData(-1.047f)]
    public void TheFloorIsBehindTheModelExactlyWhenTheEyeIsAboveIt(float tilt)
    {
        MeshPicture.Camera camera = MeshPicture.Camera.Of(Post(), 0.4f, tilt, 1f, Vector2.Zero);

        // A point of the model half way up it, and the floor point that lands under the same pixel.
        Vector3 model = camera.Place(new Vector3(0f, 0f, -20f));
        Vector3 o = camera.Place(Vector3.Zero);
        Vector2 origin = Flat(o);
        Vector2 ex = Flat(camera.Place(Vector3.UnitX)) - origin;
        Vector2 ey = Flat(camera.Place(Vector3.UnitY)) - origin;
        float det = (ex.X * ey.Y) - (ex.Y * ey.X);
        Vector2 v = Flat(model) - origin;
        float fx = ((ey.Y * v.X) - (ey.X * v.Y)) / det;
        float fy = ((ex.X * v.Y) - (ex.Y * v.X)) / det;
        Vector3 floor = camera.Place(new Vector3(fx, fy, 0f));

        Assert.InRange(floor.X, model.X - 1e-4f, model.X + 1e-4f);
        Assert.InRange(floor.Y, model.Y - 1e-4f, model.Y + 1e-4f);

        // Nearer is less: the floor is behind the model exactly when its depth is the greater.
        Assert.Equal(floor.Z > model.Z, ModelFloor.Under(camera));
        Assert.True(ModelFloor.Under(MeshPicture.Camera.Of(Post(), 0.4f, 0f, 1f, Vector2.Zero)));
    }

    /// <summary>A line that belongs to several levels is drawn once.</summary>
    /// <remarks>
    /// Every tenth line of a level is a line of the level above, and drawn by both it would be
    /// twice as bright as its neighbours of the coarser level - a floor with a beat in it. Looking
    /// straight down the upright lines are vertical, so each one is its own column, and a column's
    /// pieces have to tile it without overlapping.
    /// </remarks>
    [Fact]
    public void ALineIsDrawnOnceHoweverManyLevelsItBelongsTo()
    {
        List<ModelFloor.Line> lines = Floor(Post(), tilt: MathF.PI / 2f, zoom: 0.5f);

        List<float> columns = Uprights(lines);
        Assert.True(columns.Count > 3, $"only {columns.Count} upright lines");
        foreach (float column in columns)
        {
            List<ModelFloor.Line> pieces = lines
                .Where(piece => MathF.Abs(piece.From.X - column) < 1e-4f && MathF.Abs(piece.To.X - column) < 1e-4f)
                .OrderBy(piece => MathF.Min(piece.From.Y, piece.To.Y))
                .ToList();

            var covered = 0f;
            var reached = -1f;
            foreach (ModelFloor.Line piece in pieces)
            {
                float top = MathF.Min(piece.From.Y, piece.To.Y);
                float foot = MathF.Max(piece.From.Y, piece.To.Y);
                Assert.True(top >= reached - 1e-5f, $"column {column} has overlapping pieces");
                covered += foot - top;
                reached = foot;
            }

            Assert.True(covered <= 1f + 1e-4f, $"column {column} is drawn {covered:F2} times over");
        }
    }

    /// <summary>
    /// The ink, before the frame's fade, of the same line of each family at a tilt: the one a
    /// tenth of a tile from the origin, running across the picture and running into it.
    /// </summary>
    private static (float Across, float Into) Families(float tilt)
    {
        MeshPicture.Camera camera = MeshPicture.Camera.Of(Post(), 0f, tilt, 0.5f, Vector2.Zero);
        var lines = new List<ModelFloor.Line>();
        ModelFloor.Of(lines, camera, 400f, ModelFloor.Tile, 1e6f);

        float acrossAt = camera.Place(new Vector3(0f, 25f, 0f)).Y;
        float intoAt = camera.Place(new Vector3(25f, 0f, 0f)).X;
        var across = 0f;
        var into = 0f;
        foreach (ModelFloor.Line piece in lines)
        {
            if (MathF.Abs(piece.From.Y - acrossAt) < 1e-4f && MathF.Abs(piece.To.Y - acrossAt) < 1e-4f)
            {
                across = MathF.Max(across, Raw(piece));
            }
            else if (MathF.Abs(piece.From.X - intoAt) < 1e-4f && MathF.Abs(piece.To.X - intoAt) < 1e-4f)
            {
                into = MathF.Max(into, Raw(piece));
            }
        }

        return (across, into);
    }

    /// <summary>The strongest ink, before the frame's fade, of the upright line this many model units from the origin.</summary>
    private static float Strongest(List<ModelFloor.Line> lines, MeshPicture.Camera camera, float units)
    {
        float x = camera.Place(new Vector3(units, 0f, 0f)).X;
        var most = 0f;
        foreach (ModelFloor.Line piece in lines)
        {
            if (MathF.Abs(piece.From.X - x) < 1e-4f && MathF.Abs(piece.To.X - x) < 1e-4f)
            {
                most = MathF.Max(most, Raw(piece));
            }
        }

        return most;
    }

    /// <summary>A piece's ink with the frame's fade taken back out, where there is enough of it to divide by.</summary>
    private static float Raw(ModelFloor.Line piece)
    {
        float from = ModelFloor.Vignette(piece.From);
        float to = ModelFloor.Vignette(piece.To);
        return MathF.Max(from > 0.5f ? piece.FromAlpha / from : 0f, to > 0.5f ? piece.ToAlpha / to : 0f);
    }

    /// <summary>The picture x of every vertical line, once each.</summary>
    /// <remarks>
    /// CLUSTERED RATHER THAN ROUNDED: the pieces of one line differ in x by the rounding error of
    /// a rotation, a few billionths, and a rounding boundary can fall between two of them.
    /// </remarks>
    private static List<float> Uprights(List<ModelFloor.Line> lines)
    {
        var at = new List<float>();
        foreach (float x in lines
            .Where(piece => MathF.Abs(piece.From.X - piece.To.X) < 1e-5f)
            .Select(piece => piece.From.X)
            .OrderBy(x => x))
        {
            if (at.Count == 0 || x - at[^1] > 1e-4f)
            {
                at.Add(x);
            }
        }

        return at;
    }

    /// <summary>Whether the line a piece lies on passes through a point.</summary>
    private static bool Through(ModelFloor.Line piece, Vector2 point)
    {
        Vector2 run = piece.To - piece.From;
        return MathF.Abs(Cross(point - piece.From, run)) / run.Length() < 1e-4f;
    }

    private static bool Parallel(ModelFloor.Line piece, Vector2 along)
    {
        Vector2 run = piece.To - piece.From;
        return MathF.Abs(Cross(run, along)) < 1e-3f * run.Length() * along.Length();
    }

    /// <summary>Where the line a piece lies on crosses the line through <paramref name="at"/> along <paramref name="direction"/>.</summary>
    private static Vector2 Meet(ModelFloor.Line piece, Vector2 at, Vector2 direction)
    {
        Vector2 run = piece.To - piece.From;
        float t = Cross(at - piece.From, direction) / Cross(run, direction);
        return piece.From + (run * t);
    }

    private static Vector2 Direction(ModelFloor.Line piece) => Vector2.Normalize(piece.To - piece.From);

    private static float Cross(Vector2 a, Vector2 b) => (a.X * b.Y) - (a.Y * b.X);

    private static Vector2 Flat(Vector3 placed) => new(placed.X, placed.Y);

    private static List<ModelFloor.Line> Floor(
        SkinnedMesh? mesh, float turn = 0f, float tilt = 0.5f, float zoom = 1f, float side = 400f,
        float tile = ModelFloor.Tile, float lens = ModelFloor.Lens)
    {
        var lines = new List<ModelFloor.Line>();
        ModelFloor.Of(lines, MeshPicture.Camera.Of(mesh, turn, tilt, zoom, Vector2.Zero), side, tile, lens);
        return lines;
    }

    /// <summary>A post standing on the origin, this tall, the way a monster's model stands: along negative z.</summary>
    private static SkinnedMesh Post(float tall = 40f) => Quad(-5f, 5f, -tall, 0f);

    /// <summary>A post whose box runs ten units past the origin, the way a hanging weapon or a reaching pose does.</summary>
    private static SkinnedMesh Sunk() => Quad(-5f, 5f, -30f, 10f);

    private static SkinnedMesh Quad(float x0, float x1, float z0, float z1)
    {
        Vector3[] places = [new(x0, 0f, z0), new(x1, 0f, z0), new(x1, 0f, z1), new(x0, 0f, z1)];
        var normals = new Vector3[4];
        Array.Fill(normals, Vector3.UnitY);
        var least = new Vector3(float.MaxValue);
        var most = new Vector3(float.MinValue);
        foreach (Vector3 place in places)
        {
            least = Vector3.Min(least, place);
            most = Vector3.Max(most, place);
        }

        return SkinnedMesh.Of(places, normals, [0, 1, 2, 0, 2, 3], least, most);
    }
}
