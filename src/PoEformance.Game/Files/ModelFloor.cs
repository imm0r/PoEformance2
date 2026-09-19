using System.Numerics;

namespace PoEformance.Game.Files;

/// <summary>
/// The floor a model stands on, as lines for whoever shows the picture to draw under or over it.
/// </summary>
/// <remarks>
/// LINES, NOT PIXELS, and that is the report it answers. The first floors were rasterised into
/// the picture with the model, one pixel wide and depth-tested against it - and the picture is
/// drawn a rung below the pane while an animation plays, then stretched over it. At two to four
/// times its size a one-pixel line is a staircase with two-to-four-pixel steps, which the live
/// client called "so unglaublich pixelig". A line handed to the overlay's draw list is drawn at
/// the SCREEN's resolution and anti-aliased, whatever rung the model is drawn at, and that is the
/// whole reason this is a list of lines rather than a pass over the pixels.
///
/// WHAT THAT GIVES UP IS THE DEPTH TEST, and it costs less than it sounds. A monster is planted on
/// the floor by its origin - measured, the feet at z = 0.98 through every frame of a walk - so the
/// whole of it is on one side of the plane, and which side the eye is on settles the order for
/// the whole picture at once: seen from above the floor is behind all of the model, seen from
/// below it is in front of all of it. See <see cref="Under"/>. What is lost is a weapon hanging
/// below the feet, which is drawn over the floor it should sink into.
///
/// IN THE GAME'S UNITS, which is what "a boss stands on more squares than a rat" needs. A terrain
/// tile is 250 world units - GameHelper2's <c>TileStructure.TileToWorldConversion</c>, the same
/// 250 the map radar divides by 23 for a grid cell - and a model's own units are those, scaled by
/// the ModelSizeMultiplier its variety row carries; see <see cref="TileOn"/>. The squares are the
/// tile divided by ten, and ten of them the next brighter line, in decades: what the live client
/// asked for was Blender's floor, so Blender's rules are followed rather than reinvented, out of
/// its overlay grid shader and its default theme. The finest level fades as its lines crowd, the
/// level above carries the emphasis the finest has lost, the x axis is red and the y axis green,
/// and the whole floor fades as the view drops towards level.
///
/// IN THIS LAYER, for the reason PictureLadder gives: the overlay cannot be tested, and every
/// rule above is a sum. Positions are shares of the picture's side, so a line is the same line on
/// every rung and the caller multiplies once.
/// </remarks>
public static class ModelFloor
{
    /// <summary>World units to a terrain tile, which is what a square of the floor measures ten of.</summary>
    /// <remarks>
    /// GameHelper2's <c>TileStructure.TileToWorldConversion</c>, and the 250 in
    /// <c>MapRadar.WorldToGrid</c> and <c>TerrainGrid.CellsPerTile</c>. Not a number of this tool's.
    /// </remarks>
    public const float Tile = 250f;

    /// <summary>Lines to a brighter one: a tile is ten squares, and ten tiles the square above that.</summary>
    public const int Decade = 10;

    /// <summary>The spacing on the picture, in pixels, under which a level's lines are gone.</summary>
    /// <remarks>
    /// LINES CLOSER THAN A FEW PIXELS ARE NOT A GRID, THEY ARE A FILL - seen nearly level, the
    /// family that runs across the picture is pressed together by the foreshortening, and the live
    /// client showed it as a solid band. Blender fades its finest level over a wider range of
    /// pixel sizes than this, and its fine grid is a suggestion until it is zoomed well in; five
    /// to one keeps the finest level whole for a stretch of zoom before the next finer one begins
    /// to show, which reads as a grid. Nothing pops at the change of level whatever the window,
    /// because the level above is whole before the finest one goes - see <see cref="Family"/>.
    /// </remarks>
    public const float Crowded = 6f;

    /// <summary>The spacing, in pixels, at which a level's lines are whole.</summary>
    public const float Spaced = 30f;

    /// <summary>How much of its ink the finer of two levels keeps against the emphasised one.</summary>
    /// <remarks>Blender's theme draws its grid at half alpha and its emphasised lines at full.</remarks>
    public const float Faint = 0.5f;

    /// <summary>The longest piece a line is cut into, as a share of the side, so the fade along it can bend.</summary>
    /// <remarks>
    /// A line is one colour from end to end where it is drawn, and the frame's fade is not - so a
    /// line is handed over in pieces, each with the fade at both ends and straight between them. A
    /// twelfth of the side keeps the straight stretches within a few hundredths of the curve.
    /// </remarks>
    public const float Piece = 1f / 12f;

    /// <summary>Under this much alpha a line is not worth handing over.</summary>
    public const float Invisible = 0.02f;

    /// <summary>The most lines one level of one family may put across the frame, past which the inputs are not a floor.</summary>
    private const int Most = 4096;

    /// <summary>What a line is drawn in.</summary>
    public enum Stroke
    {
        /// <summary>A line of the grid, in the floor's grey.</summary>
        Grid,

        /// <summary>The model's x axis, through its origin. Red, as Blender draws it.</summary>
        AxisX,

        /// <summary>The model's y axis, through its origin. Green, as Blender draws it.</summary>
        AxisY,
    }

    /// <summary>One straight piece of the floor, on the picture.</summary>
    /// <param name="From">Where it starts, as a share of the picture's side from the top left corner.</param>
    /// <param name="To">Where it ends, the same way.</param>
    /// <param name="FromAlpha">How solid it is at the start, 0 to 1, with every fade already in it.</param>
    /// <param name="ToAlpha">How solid it is at the end.</param>
    /// <param name="Stroke">What it is drawn in.</param>
    public readonly record struct Line(Vector2 From, Vector2 To, float FromAlpha, float ToAlpha, Stroke Stroke);

    /// <summary>A tile in a model's own units, from the multiplier the game draws the model at.</summary>
    /// <param name="modelSize">The variety row's ModelSizeMultiplier, in percent. Anything not positive is taken as a hundred.</param>
    /// <remarks>
    /// THE GAME SCALES THE MESH, NOT THE WORLD. A monster drawn at 150 stands one and a half times
    /// as tall as its file, on the same tiles - so on the picture, which draws the file, a tile is
    /// that much SMALLER in the file's units. This is what makes a boss stand on more squares than
    /// a rat by the same amount it does in the game.
    /// </remarks>
    public static float TileOn(int modelSize) => modelSize > 0 ? Tile * 100f / modelSize : Tile;

    /// <summary>Whether the floor lies behind the model, which it does whenever the eye is above it.</summary>
    /// <param name="camera">The camera the picture was drawn from, which is the one the floor is drawn from too.</param>
    /// <remarks>
    /// A POSITIVE TILT LOOKS DOWN: the camera turns the model about x by the tilt before standing
    /// it up, so the floor's far side rises up the picture and its near side comes down towards
    /// the feet, and a point above the floor is nearer the eye than the floor at the pixel behind
    /// it. Looking up from below, the same turn the other way puts the floor in front. Neither is
    /// argued here: ModelFloorTests asks the camera for the depths and checks this against them.
    /// Level is edge on and draws nothing, so its side does not matter.
    ///
    /// ASKED OF THE CAMERA AND NOT OF A TILT, so that the side the floor goes on and the floor
    /// itself come from the same place - the camera the picture was drawn with - and cannot be a
    /// frame apart while the model is being dragged.
    /// </remarks>
    public static bool Under(in MeshPicture.Camera camera) => camera.Tilt >= 0f;

    /// <summary>How much of the floor the frame's own edge leaves, at a point of the picture.</summary>
    /// <remarks>
    /// Whole across most of the picture and gone at the frame's edge, so the floor ends in air
    /// wherever the frame cuts it. The fourth power holds it solid over the middle and lets go in
    /// the last stretch, the same fade the rasterised floor had and the live client accepted.
    /// </remarks>
    public static float Vignette(Vector2 at)
    {
        float dx = at.X - 0.5f;
        float dy = at.Y - 0.5f;
        float away = 4f * ((dx * dx) + (dy * dy));
        return MathF.Max(0f, 1f - (away * away));
    }

    /// <summary>
    /// The floor's lines, for a picture drawn from this camera and shown this wide.
    /// </summary>
    /// <param name="lines">Where the lines go. Cleared first, and reused so a caller allocates nothing per frame.</param>
    /// <param name="camera">The view the picture was drawn from.</param>
    /// <param name="side">How many pixels the picture is shown across, which is what decides how crowded the lines are.</param>
    /// <param name="tile">A tile in the model's own units - see <see cref="TileOn"/>.</param>
    /// <remarks>
    /// THE FLOOR IS THE PLANE THROUGH THE MODEL'S ORIGIN, at z = 0, and not the bottom of its box:
    /// two monsters in a row showed the box bottom wrong from the live client, one shin-deep in the
    /// floor and the next hovering above it. Its map onto the picture is affine, so the frame's
    /// four corners map back onto the floor and bound the lines worth drawing, and each of those is
    /// clipped to the frame before it is cut into pieces. The cost is set by the frame and the
    /// spacing, never by how far the floor reaches - it reaches for ever.
    ///
    /// THE LEVEL FOLLOWS THE BETTER-SPACED FAMILY, as it did on the rasterised floor: the finest
    /// decade whose lines land at least <see cref="Crowded"/> pixels apart in one family or the
    /// other. Each family then fades each level by its own spacing, so a family pressed together by
    /// the tilt is gone while the other stays.
    /// </remarks>
    public static void Of(List<Line> lines, in MeshPicture.Camera camera, float side, float tile = Tile)
    {
        ArgumentNullException.ThrowIfNull(lines);
        lines.Clear();

        if (!camera.Ready || !(side > 0f) || !(tile > 0f) || !float.IsFinite(tile))
        {
            return;
        }

        // Blender's fade at steep angles for the contents of the floor plane, which is what stops
        // a nearly level view from being stripes across the whole frame: the lines that run into
        // the depth keep their spacing under any tilt, and only this fade takes them out.
        float dropped = 1f - MathF.Abs(MathF.Sin(camera.Tilt));
        float angle = 1f - (dropped * dropped * dropped);
        if (!(angle > Invisible))
        {
            return;
        }

        // The floor's map onto the picture: its origin, and one unit along each of its axes.
        Vector3 o = camera.Place(Vector3.Zero);
        Vector3 x = camera.Place(Vector3.UnitX);
        Vector3 y = camera.Place(Vector3.UnitY);
        var origin = new Vector2(o.X, o.Y);
        var ex = new Vector2(x.X - o.X, x.Y - o.Y);
        var ey = new Vector2(y.X - o.X, y.Y - o.Y);
        float det = (ex.X * ey.Y) - (ex.Y * ey.X);
        if (!(MathF.Abs(det) > 1e-12f))
        {
            return;
        }

        // HOW FAR APART NEIGHBOURING LINES OF EACH FAMILY LAND, in pixels per model unit. One
        // square of the floor is a parallelogram on the picture, of area det; a family's spacing
        // is that area over the length of the other family's edge.
        float perX = MathF.Abs(det) / ey.Length() * side;
        float perY = MathF.Abs(det) / ex.Length() * side;
        float best = MathF.Max(perX, perY);
        if (!(best > 0f) || !float.IsFinite(best))
        {
            return;
        }

        // The finest decade of the tile whose lines are still apart in the better family.
        float finest = MathF.Ceiling(MathF.Log10(Crowded / (tile * best)));
        if (!(finest >= -8f) || !(finest <= 8f))
        {
            return;
        }

        // The frame's corners, back on the floor, bound what is worth drawing.
        float leastX = float.MaxValue;
        float mostX = float.MinValue;
        float leastY = float.MaxValue;
        float mostY = float.MinValue;
        Span<Vector2> corners = [new(0f, 0f), new(1f, 0f), new(0f, 1f), new(1f, 1f)];
        foreach (Vector2 corner in corners)
        {
            float vx = corner.X - origin.X;
            float vy = corner.Y - origin.Y;
            float fx = ((ey.Y * vx) - (ey.X * vy)) / det;
            float fy = ((ex.X * vy) - (ex.Y * vx)) / det;
            leastX = MathF.Min(leastX, fx);
            mostX = MathF.Max(mostX, fx);
            leastY = MathF.Min(leastY, fy);
            mostY = MathF.Max(mostY, fy);
        }

        // Lines of constant x run along y, so the one through the origin is the Y AXIS, and the
        // other way about. Getting this backwards paints the axes in each other's colour.
        Family(lines, origin, ex, ey, perX, (int)finest, tile, leastX, mostX, angle, Stroke.AxisY);
        Family(lines, origin, ey, ex, perY, (int)finest, tile, leastY, mostY, angle, Stroke.AxisX);
    }

    /// <summary>One family of parallel lines, at the finest level and the two above it.</summary>
    /// <param name="step">Where one unit of the coordinate this family holds constant lands on the picture.</param>
    /// <param name="along">Where one unit of the other coordinate lands: the direction the lines run.</param>
    /// <param name="per">The family's spacing in pixels per model unit.</param>
    /// <param name="finest">The finest decade drawn, as a power of ten of the tile.</param>
    /// <param name="least">The lowest value of the held coordinate that crosses the frame.</param>
    /// <param name="most">The highest.</param>
    /// <param name="angle">How much the tilt leaves of the whole floor.</param>
    /// <param name="axis">What the line through the origin is drawn in.</param>
    /// <remarks>
    /// EACH LINE ONCE, AT THE LEVEL IT BELONGS TO. Every tenth line of a level is a line of the
    /// level above, so a level draws only the lines that are not - and the top level draws them
    /// all, giving the ones that belong higher still the emphasis of their own level, since the
    /// levels above the top all look alike. The line through the origin belongs to every level and
    /// is drawn once, as the axis.
    ///
    /// THE EMPHASIS OF A LINE IS HOW WHOLE THE LEVEL BELOW IT IS, which is Blender's rule and the
    /// reason nothing pops when a level goes: as the finest level fades to nothing, the level above
    /// slides from emphasised to faint in the same motion and is then the finest level itself, at
    /// the same ink it was just drawn in.
    /// </remarks>
    private static void Family(
        List<Line> lines, Vector2 origin, Vector2 step, Vector2 along, float per, int finest,
        float tile, float least, float most, float angle, Stroke axis)
    {
        for (int level = finest; level <= finest + 2; level++)
        {
            bool top = level == finest + 2;
            float unit = tile * MathF.Pow(Decade, level);
            float alpha = Shown(unit * per) * angle;
            float weight = Weight(unit * per);

            // Below the top, a level too faint to see is skipped whole rather than line by line -
            // it is the crowded one, with the most lines. The top level is never skipped: the axis
            // is drawn there whatever the grid is doing, and its lines are few.
            if (!top && !(alpha * weight > Invisible))
            {
                continue;
            }

            // The lines that belong to the level above get its ink, at the top level only; below
            // it they are skipped and the level above draws them.
            float aboveAlpha = Shown(unit * Decade * per) * angle;
            float aboveWeight = Weight(unit * Decade * per);

            // Whole numbers of the unit, held as integers so that "every tenth" is exact however
            // far from the origin the frame has been panned.
            float lower = MathF.Ceiling(least / unit);
            float upper = MathF.Floor(most / unit);
            if (!(upper - lower < Most) || !(MathF.Abs(lower) < 1e9f) || !(MathF.Abs(upper) < 1e9f))
            {
                continue;
            }

            for (long i = (long)lower; i <= (long)upper; i++)
            {
                bool above = i % Decade == 0;
                if (above && !top)
                {
                    continue;
                }

                Vector2 at = origin + (step * (i * unit));
                if (i == 0)
                {
                    Cut(lines, at, along, angle, axis);
                }
                else if (above)
                {
                    Cut(lines, at, along, aboveAlpha * aboveWeight, Stroke.Grid);
                }
                else
                {
                    Cut(lines, at, along, alpha * weight, Stroke.Grid);
                }
            }
        }
    }

    /// <summary>How much of a level is drawn, from the spacing its lines land at on the picture.</summary>
    private static float Shown(float spacing)
    {
        float t = Math.Clamp((spacing - Crowded) / (Spaced - Crowded), 0f, 1f);
        return t * t * (3f - (2f * t));
    }

    /// <summary>The ink a level is drawn in: faint while the level below it is whole, and whole once that one has gone.</summary>
    private static float Weight(float spacing)
        => Faint + ((1f - Faint) * Shown(spacing / Decade));

    /// <summary>The line through a point along a direction, clipped to the frame and cut into pieces that carry the frame's fade.</summary>
    private static void Cut(List<Line> lines, Vector2 at, Vector2 along, float alpha, Stroke stroke)
    {
        float t0 = float.NegativeInfinity;
        float t1 = float.PositiveInfinity;
        if (!Clip(at.X, along.X, ref t0, ref t1) || !Clip(at.Y, along.Y, ref t0, ref t1))
        {
            return;
        }

        Vector2 start = at + (along * t0);
        Vector2 end = at + (along * t1);
        float length = Vector2.Distance(start, end);

        // NOTHING LONGER THAN THE FRAME'S DIAGONAL IS A CLIPPED LINE, and the check is not
        // decoration: a length that is not finite makes a count of pieces that is not either, and
        // since .NET 9 a float past the top of an int casts to int.MaxValue rather than wrapping,
        // which is a loop of two billion pieces per line. Found by a mutant of the clip above that
        // took the tests from a quarter of a second to twenty minutes and counting.
        if (!(length > 1e-6f) || !(length <= 2f))
        {
            return;
        }

        var pieces = (int)MathF.Ceiling(length / Piece);
        Vector2 last = start;
        float lastAlpha = alpha * Vignette(start);
        for (var piece = 1; piece <= pieces; piece++)
        {
            Vector2 next = Vector2.Lerp(start, end, (float)piece / pieces);
            float nextAlpha = alpha * Vignette(next);
            if (lastAlpha > Invisible || nextAlpha > Invisible)
            {
                lines.Add(new Line(last, next, lastAlpha, nextAlpha, stroke));
            }

            last = next;
            lastAlpha = nextAlpha;
        }
    }

    /// <summary>Narrows a line's parameter range to where one axis lies inside the frame; false where none of it does.</summary>
    private static bool Clip(float start, float delta, ref float t0, ref float t1)
    {
        if (MathF.Abs(delta) < 1e-9f)
        {
            return start >= 0f && start <= 1f;
        }

        float enter = -start / delta;
        float leave = (1f - start) / delta;
        if (enter > leave)
        {
            (enter, leave) = (leave, enter);
        }

        t0 = MathF.Max(t0, enter);
        t1 = MathF.Min(t1, leave);
        return t0 <= t1;
    }
}
