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
/// IN PERSPECTIVE, UNDER A MODEL THAT IS NOT. The model is drawn orthographically, and its floor
/// was too at first - and seen from anywhere but well above, an orthographic floor is a set of
/// parallel stripes pressed flat, which the live client showed against Blender's: "verliert das
/// gesamte schöne Grid seine beeindruckende Wirkung". What makes Blender's floor read as a floor
/// from a low angle is that it runs into the distance. So the floor is projected from an eye a
/// few model-lengths in front of the picture's centre, anchored so that the plane through the
/// model's origin is drawn exactly as the orthographic model is: the feet stand where they stood,
/// and only the floor's far and near parts are drawn smaller and larger. A foot a little in front
/// of or behind the origin is off its perspective place by that little over the eye's distance -
/// for a man, a pixel.
///
/// IN THE GAME'S UNITS, which is what "a boss stands on more squares than a rat" needs. A terrain
/// tile is 250 world units - GameHelper2's <c>TileStructure.TileToWorldConversion</c>, the same
/// 250 the map radar divides by 23 for a grid cell - and a model's own units are those, scaled by
/// the ModelSizeMultiplier its variety row carries; see <see cref="TileOn"/>. The squares are the
/// tile divided by ten, and ten of them the next brighter line, in decades: what the live client
/// asked for was Blender's floor, so Blender's rules are followed rather than reinvented, out of
/// its overlay grid shader and its default theme. A level fades where its lines crowd - which in
/// perspective is a place on the floor, not a zoom - the level above carries the emphasis the one
/// below has lost, and the x axis is red and the y axis green.
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

    /// <summary>How far in front of the picture's centre the eye sits, in lengths of the model's longest side.</summary>
    /// <remarks>
    /// Three is a long lens: the floor plainly runs into the distance, while a point of the model
    /// as far in front of the origin as the model is wide is drawn a quarter too large by the
    /// floor's rules and at its true size by the model's - a difference that stays under the eye's
    /// notice at the feet, which is where the two meet.
    /// </remarks>
    public const float Lens = 3f;

    /// <summary>How near the eye the floor is still drawn, as a share of the eye's distance.</summary>
    private const float Near = 0.05f;

    /// <summary>The spacing on the picture, in pixels, under which a level's lines are gone.</summary>
    /// <remarks>
    /// LINES CLOSER THAN A FEW PIXELS ARE NOT A GRID, THEY ARE A FILL - seen nearly level, the
    /// family that runs across the picture is pressed together by the foreshortening, and the live
    /// client showed it as a solid band. Blender fades its finest level over a wider range of
    /// pixel sizes than this, and its fine grid is a suggestion until it is zoomed well in; five
    /// to one keeps the finest level whole for a stretch before the next finer one begins to show,
    /// which reads as a grid. Nothing pops at the change of level whatever the window, because the
    /// level above is whole before the finest one goes - see <see cref="Family"/>.
    /// </remarks>
    public const float Crowded = 6f;

    /// <summary>The spacing, in pixels, at which a level's lines are whole.</summary>
    public const float Spaced = 30f;

    /// <summary>How much of its ink the finer of two levels keeps against the emphasised one.</summary>
    /// <remarks>Blender's theme draws its grid at half alpha and its emphasised lines at full.</remarks>
    public const float Faint = 0.5f;

    /// <summary>The sine of the tilt by which the floor is whole, seen from nearly level.</summary>
    /// <remarks>
    /// A SHORT FADE AT THE EDGE-ON VIEW AND NOTHING MORE. Blender fades its floor by the cube of
    /// the view's drop towards level, and that is what took the floor away from the live client at
    /// the angles it is actually looked at from - from a little above, like a person standing by
    /// the monster. In perspective the density fade does that job where it is needed, place by
    /// place; all this has to do is let go in the last few degrees, where every line of the floor
    /// lands on the horizon.
    /// </remarks>
    public const float Grazing = 0.1f;

    /// <summary>The longest piece a line is cut into, as a share of the side, so the fade along it can bend.</summary>
    /// <remarks>
    /// A line is one colour from end to end where it is drawn, and the fades are not - so a line
    /// is handed over in pieces, each with the fades at both ends and straight between them. A
    /// twelfth of the side keeps the straight stretches within a few hundredths of the curves.
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

    /// <summary>How far under the floor a model's lowest point may sit, as a share of its height, before that is worth a word.</summary>
    /// <remarks>
    /// A sole lies a few units under the plane on every monster measured - the blackguard's at
    /// one, the Cobra Lord's at nine on a model three hundred tall. A twentieth of the height
    /// clears those and catches the one this was written for.
    /// </remarks>
    public const float Sunk = 0.05f;

    /// <summary>
    /// What to say of a model whose lowest point sits well under the floor: nothing for nearly every
    /// monster, and a line for the one that stands in a pit.
    /// </summary>
    /// <param name="lowest">The lowest point of the model as drawn, in its own units below the floor - positive is under it.</param>
    /// <param name="height">The model's height, from its box.</param>
    /// <remarks>
    /// THE COLOSSUS FROM THE LIVE CLIENT, measured with --posedump: its root stays at the origin
    /// through every frame of its idle while its feet hang 1272 units under it - five tiles - and
    /// the same reader puts the Cobra Lord's feet nine units under, which is right. The game plants
    /// a monster by its origin, so a titan fought from the edge of a chasm is modelled with the
    /// chasm below that plane, and the floor drawn there is the floor the game uses. What the pane
    /// owes the reader is to say so, in the status line and not over the picture.
    /// </remarks>
    public static string Planted(float lowest, float height)
        => height > 0f && lowest > Sunk * height
            ? $"planted {lowest:F0} units above its lowest point"
            : string.Empty;

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
    /// <param name="lens">How far in front of the picture's centre the eye sits, in lengths of the model. Ever larger draws the floor ever nearer to how the model is drawn.</param>
    /// <remarks>
    /// THE FLOOR IS THE PLANE THROUGH THE MODEL'S ORIGIN, at z = 0, and not the bottom of its box:
    /// two monsters in a row showed the box bottom wrong from the live client, one shin-deep in the
    /// floor and the next hovering above it.
    ///
    /// THE LEVEL FOLLOWS THE BETTER-SPACED FAMILY AT THE ORIGIN, where the floor is drawn at the
    /// model's own scale: the finest decade whose lines land at least <see cref="Crowded"/> pixels
    /// apart there in one family or the other, and two decades above it for the ground beyond.
    /// Each piece of each line then fades by the spacing at its own place, so a family pressed
    /// together by the tilt, or by the distance, is gone where it is pressed and whole where it
    /// is not. Blender draws a decade under the finest as well, for the ground nearer the eye;
    /// with the eye three model-lengths off, the nearest ground the frame can hold is under twice
    /// as near as the origin, and that decade never reaches the spacing at which it would show.
    /// </remarks>
    public static void Of(List<Line> lines, in MeshPicture.Camera camera, float side, float tile = Tile, float lens = Lens)
    {
        ArgumentNullException.ThrowIfNull(lines);
        lines.Clear();

        if (!camera.Ready || !(side > 0f) || !(tile > 0f) || !float.IsFinite(tile) || !(lens > 0f))
        {
            return;
        }

        float angle = Smooth(MathF.Abs(MathF.Sin(camera.Tilt)) / Grazing);
        if (!(angle > Invisible))
        {
            return;
        }

        // The eye sits in front of the picture's centre, and the plane through the origin is drawn
        // at the orthographic scale: whatever is at the origin's depth lands where the model puts it.
        Vector3 origin = Vector3.Transform(Vector3.Zero, camera.View);
        Vector3 alongX = Vector3.Transform(Vector3.UnitX, camera.View) - origin;
        Vector3 alongY = Vector3.Transform(Vector3.UnitY, camera.View) - origin;
        float eye = lens * camera.Reach;
        float near = Near * eye;
        if (!(eye + origin.Z > near) || !float.IsFinite(eye))
        {
            return;
        }

        var view = new Sight(camera.Centre, camera.Scale * (eye + origin.Z), eye, near, side);

        // Where the lines land at the origin, per model unit, in pixels: the spacing between
        // neighbouring lines of each family, which decides the level. The same numbers every
        // piece of every line is judged by afterwards, at its own place.
        Vector2 o = view.Screen(origin, out float depth);
        Vector2 stepX = view.Step(alongX, o, depth, 1f);
        Vector2 stepY = view.Step(alongY, o, depth, 1f);
        float perX = Spacing(stepX, stepY) * side;
        float perY = Spacing(stepY, stepX) * side;
        float best = MathF.Max(perX, perY);
        if (!(best > 0f) || !float.IsFinite(best))
        {
            return;
        }

        float finest = MathF.Ceiling(MathF.Log10(Crowded / (tile * best)));
        if (!(finest >= -8f) || !(finest <= 8f))
        {
            return;
        }

        var floor = new Ground(origin, alongX, alongY);
        for (int level = (int)finest; level <= (int)finest + 2; level++)
        {
            bool top = level == (int)finest + 2;
            float unit = tile * MathF.Pow(Decade, level);

            // How far away this level can still be seen: its lines at the origin are so many
            // pixels apart, and a line's spacing falls with the depth as the eye's distance does,
            // so past the depth at which even the better family is crowded there is nothing of
            // this level to draw. The frame's edge, mapped back onto the floor no further than
            // that, bounds the lines worth walking.
            float reach = (eye + origin.Z) * MathF.Max(unit * perX, unit * perY) / Crowded;
            if (!view.Bounds(floor, reach, out float leastX, out float mostX, out float leastY, out float mostY))
            {
                continue;
            }

            Family(lines, view, floor, alongX, alongY, unit, top, leastX, mostX, angle, Stroke.AxisY);
            Family(lines, view, floor, alongY, alongX, unit, top, leastY, mostY, angle, Stroke.AxisX);
        }
    }

    /// <summary>One family of parallel lines of the floor in perspective, at one level.</summary>
    /// <param name="step">One unit of the coordinate this family holds constant, in the view.</param>
    /// <param name="along">One unit of the other coordinate: the direction the lines run.</param>
    /// <param name="unit">This level's spacing, in model units.</param>
    /// <param name="top">Whether this is the coarsest level drawn, which draws the lines of the levels above it too.</param>
    /// <param name="least">The lowest value of the held coordinate worth drawing.</param>
    /// <param name="most">The highest.</param>
    /// <param name="angle">How much the tilt leaves of the whole floor.</param>
    /// <param name="axis">What the line through the origin is drawn in.</param>
    /// <remarks>
    /// EACH LINE ONCE, AT THE LEVEL IT BELONGS TO. Every tenth line of a level is a line of the
    /// level above, so a level draws only the lines that are not - and the top level draws them
    /// all, judging the ones that belong higher by their own level's spacing. The line through
    /// the origin belongs to every level and is drawn once, as the axis.
    ///
    /// A LINE'S INK IS DECIDED PIECE BY PIECE, from the spacing of its family at that place: how
    /// far the next line of the same level lands from it there, across the piece's own direction.
    /// Blender's rule for the emphasis holds at every place too - a line is faint where the level
    /// under it is whole, and whole where that level has crowded away - so a level slides from
    /// emphasised to faint along its own length as the floor runs off into the distance.
    /// </remarks>
    private static void Family(
        List<Line> lines, in Sight view, in Ground floor, Vector3 step, Vector3 along, float unit, bool top,
        float least, float most, float angle, Stroke axis)
    {
        float lower = MathF.Ceiling(least / unit);
        float upper = MathF.Floor(most / unit);
        if (!(upper - lower < Most) || !(MathF.Abs(lower) < 1e9f) || !(MathF.Abs(upper) < 1e9f))
        {
            return;
        }

        for (long i = (long)lower; i <= (long)upper; i++)
        {
            bool above = i % Decade == 0;
            if (above && !top)
            {
                continue;
            }

            Vector3 at = floor.Origin + (step * (i * unit));
            float spacing = above ? unit * Decade : unit;
            Stroke stroke = i == 0 ? axis : Stroke.Grid;
            Trace(lines, view, at, along, step, spacing, angle, stroke);
        }
    }

    /// <summary>One line of the floor, from its near end to where it vanishes, clipped to the frame and cut into pieces.</summary>
    /// <param name="at">A point of the line, in the view.</param>
    /// <param name="along">The line's direction, in the view.</param>
    /// <param name="step">One unit of the coordinate the line holds constant, in the view: towards its neighbour.</param>
    /// <param name="spacing">How many units to the neighbouring line of the same level.</param>
    /// <remarks>
    /// THE PICTURE OF A LINE IS A LINE, projection or no projection, so the whole of it is one
    /// straight stretch on the picture: from where it crosses the near plane to the point it
    /// vanishes at, and from end to end if it runs at one depth. Along that stretch the depth is
    /// the near plane's over the remaining share of the way to the vanishing point, which is what
    /// puts a piece's end back on the floor without ever holding a floor coordinate that grows
    /// past what a float can keep.
    /// </remarks>
    private static void Trace(
        List<Line> lines, in Sight view, Vector3 at, Vector3 along, Vector3 step, float spacing, float angle, Stroke stroke)
    {
        Vector2 start;
        Vector2 end;
        float startDepth;
        float endDepth;
        bool vanishes = MathF.Abs(along.Z) > 1e-9f;
        if (vanishes)
        {
            // From the near plane towards the vanishing point, whichever way along the line the
            // eye is: the far end is where the depth has grown without bound.
            float t = (view.Near - view.Eye - at.Z) / along.Z;
            Vector3 nearest = at + (along * t);
            start = view.Screen(nearest, out startDepth);
            end = view.Centre + (view.Focus * new Vector2(along.X, along.Y) / along.Z);
            endDepth = float.PositiveInfinity;
        }
        else
        {
            float depth = view.Eye + at.Z;
            if (!(depth > view.Near))
            {
                return;
            }

            // At one depth the line is drawn to scale, without end either way.
            Vector2 middle = view.Screen(at, out _);
            Vector2 run = view.Focus * new Vector2(along.X, along.Y) / depth;
            float t0 = float.NegativeInfinity;
            float t1 = float.PositiveInfinity;
            if (!Clip(middle.X, run.X, ref t0, ref t1) || !Clip(middle.Y, run.Y, ref t0, ref t1))
            {
                return;
            }

            start = middle + (run * t0);
            end = middle + (run * t1);
            startDepth = depth;
            endDepth = depth;
        }

        // Clipped to the frame as a stretch between its two ends, the far one being the vanishing
        // point where there is one.
        var u0 = 0f;
        var u1 = 1f;
        Vector2 delta = end - start;
        if (!Clip(start.X, delta.X, ref u0, ref u1) || !Clip(start.Y, delta.Y, ref u0, ref u1))
        {
            return;
        }

        Vector2 from = start + (delta * u0);
        Vector2 to = start + (delta * u1);
        float length = Vector2.Distance(from, to);
        // NOTHING LONGER THAN THE FRAME'S DIAGONAL IS A CLIPPED LINE, and the check is not
        // decoration: a length that is not finite makes a count of pieces that is not either, and
        // since .NET 9 a float past the top of an int casts to int.MaxValue rather than wrapping,
        // which is a loop of two billion pieces per line. Found by a mutant of the clip that took
        // the tests from a quarter of a second to twenty minutes and counting.
        if (!(length > 1e-6f) || !(length <= 2f))
        {
            return;
        }

        Vector2 direction = (to - from) / length;
        var pieces = (int)MathF.Ceiling(length / Piece);
        Vector2 last = from;
        float lastAlpha = Ink(view, from, Depth(u0, startDepth, endDepth, vanishes), direction, step, spacing, angle, stroke);
        for (var piece = 1; piece <= pieces; piece++)
        {
            float u = u0 + ((u1 - u0) * piece / pieces);
            Vector2 next = start + (delta * u);
            float nextAlpha = Ink(view, next, Depth(u, startDepth, endDepth, vanishes), direction, step, spacing, angle, stroke);
            if (lastAlpha > Invisible || nextAlpha > Invisible)
            {
                lines.Add(new Line(last, next, lastAlpha, nextAlpha, stroke));
            }

            last = next;
            lastAlpha = nextAlpha;
        }
    }

    /// <summary>The depth of a line's point that lies this far along its stretch on the picture.</summary>
    private static float Depth(float u, float startDepth, float endDepth, bool vanishes)
        => vanishes ? startDepth / MathF.Max(1f - u, 1e-6f) : startDepth + (u * (endDepth - startDepth));

    /// <summary>How solid one end of a piece is: the fades of the frame, the tilt and the family's spacing at that place.</summary>
    private static float Ink(
        in Sight view, Vector2 at, float depth, Vector2 direction, Vector3 step, float spacing, float angle, Stroke stroke)
    {
        float fade = angle * Vignette(at);
        if (stroke != Stroke.Grid)
        {
            return fade;
        }

        // How far the neighbouring line of the same level lands from this point, across the line:
        // one step of the held coordinate at this depth, less its own share of the drop.
        Vector2 across = view.Step(step, at, depth, spacing);
        float apart = MathF.Abs((across.X * direction.Y) - (across.Y * direction.X)) * view.Side;
        return fade * Shown(apart) * Weight(apart);
    }

    /// <summary>The perpendicular spacing of a family of lines whose neighbours land <paramref name="step"/> apart, running <paramref name="along"/>.</summary>
    private static float Spacing(Vector2 step, Vector2 along)
    {
        float run = along.Length();
        return run > 0f ? MathF.Abs((step.X * along.Y) - (step.Y * along.X)) / run : 0f;
    }

    /// <summary>How much of a level is drawn, from the spacing its lines land at on the picture.</summary>
    private static float Shown(float spacing) => Smooth((spacing - Crowded) / (Spaced - Crowded));

    /// <summary>The ink a level is drawn in: faint while the level below it is whole, and whole once that one has gone.</summary>
    private static float Weight(float spacing)
        => Faint + ((1f - Faint) * Shown(spacing / Decade));

    /// <summary>Zero to one, eased at both ends, of a share that may run past either.</summary>
    private static float Smooth(float share)
    {
        float t = Math.Clamp(share, 0f, 1f);
        return t * t * (3f - (2f * t));
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

    /// <summary>The floor in the view: its origin and one unit along each of its axes.</summary>
    private readonly record struct Ground(Vector3 Origin, Vector3 AlongX, Vector3 AlongY);

    /// <summary>
    /// The eye the floor is seen from, and the arithmetic of seeing through it.
    /// </summary>
    /// <param name="Centre">Where the view's axis meets the picture, as a share of the side.</param>
    /// <param name="Focus">Shares of the side per view unit at the origin's depth, times that depth: what a view offset is drawn as, over its own depth.</param>
    /// <param name="Eye">How far in front of the model's centre the eye is, in model units.</param>
    /// <param name="Near">The depth from the eye under which nothing is drawn.</param>
    /// <param name="Side">How many pixels the picture is shown across.</param>
    private readonly record struct Sight(Vector2 Centre, float Focus, float Eye, float Near, float Side)
    {
        /// <summary>Where a point of the view lands on the picture, and how deep it is from the eye.</summary>
        public Vector2 Screen(Vector3 point, out float depth)
        {
            depth = Eye + point.Z;
            return Centre + (Focus * new Vector2(point.X, point.Y) / depth);
        }

        /// <summary>
        /// Where a point <paramref name="units"/> further along <paramref name="step"/> in the view lands, relative to a
        /// point already on the picture at <paramref name="at"/> and <paramref name="depth"/>.
        /// </summary>
        /// <remarks>
        /// The derivative of <see cref="Screen"/> along the step: the step's own offset drawn at
        /// this depth, less the drift the step's change of depth gives the point already there.
        /// Exact for the line's neighbour a small step away, and it never needs the point's own
        /// place in the view, which is what lets it work at a piece's end near the vanishing point.
        /// </remarks>
        public Vector2 Step(Vector3 step, Vector2 at, float depth, float units)
            => units / depth * ((Focus * new Vector2(step.X, step.Y)) - (step.Z * (at - Centre)));

        /// <summary>
        /// The range of floor coordinates the frame can see of a level, out to <paramref name="reach"/> from the eye.
        /// </summary>
        /// <remarks>
        /// THE FRAME'S BOUNDARY, MAPPED BACK ONTO THE FLOOR. What the frame sees of the plane is
        /// convex, so its extent is on its boundary: each edge is sampled, and each sample is the
        /// ray through it meeting the plane - or, where that ray meets the plane behind the near
        /// plane, past the reach or not at all, the point at the reach's depth under the same
        /// pixel, which bounds the floor there just as well. Straight down, every ray meets the
        /// floor at the origin's depth and the reach never enters into it.
        /// </remarks>
        public bool Bounds(in Ground floor, float reach, out float leastX, out float mostX, out float leastY, out float mostY)
        {
            leastX = 0f;
            mostX = 0f;
            leastY = 0f;
            mostY = 0f;
            float far = MathF.Min(reach, Eye * 1e6f);
            if (!(far > Near))
            {
                return false;
            }

            const int Along = 8;
            var seen = 0;
            for (var edge = 0; edge < 4; edge++)
            {
                for (var sample = 0; sample <= Along; sample++)
                {
                    float share = (float)sample / Along;
                    Vector2 at = edge switch
                    {
                        0 => new Vector2(share, 0f),
                        1 => new Vector2(share, 1f),
                        2 => new Vector2(0f, share),
                        _ => new Vector2(1f, share),
                    };

                    if (!Meet(floor, at, far, out float fx, out float fy))
                    {
                        continue;
                    }

                    if (seen++ == 0)
                    {
                        leastX = mostX = fx;
                        leastY = mostY = fy;
                    }
                    else
                    {
                        leastX = MathF.Min(leastX, fx);
                        mostX = MathF.Max(mostX, fx);
                        leastY = MathF.Min(leastY, fy);
                        mostY = MathF.Max(mostY, fy);
                    }
                }
            }

            return seen >= 3;
        }

        /// <summary>Where the ray through a pixel meets the floor, or the floor's coordinates under that pixel at the far depth.</summary>
        private bool Meet(in Ground floor, Vector2 at, float far, out float fx, out float fy)
        {
            // The ray: at depth d from the eye the point is (d (at - Centre) / Focus, d - Eye).
            Vector2 slope = (at - Centre) / Focus;
            Vector3 dir = new(slope.X, slope.Y, 1f);

            // fx AlongX + fy AlongY - d dir = -Origin - (0, 0, Eye): three unknowns, by Cramer.
            Vector3 rhs = -(floor.Origin + new Vector3(0f, 0f, Eye));
            float det = Det(floor.AlongX, floor.AlongY, -dir);
            if (MathF.Abs(det) > 1e-12f)
            {
                float d = Det(floor.AlongX, floor.AlongY, rhs) / det;
                if (d >= Near && d <= far)
                {
                    fx = Det(rhs, floor.AlongY, -dir) / det;
                    fy = Det(floor.AlongX, rhs, -dir) / det;
                    return float.IsFinite(fx) && float.IsFinite(fy);
                }
            }

            // Under the pixel at the far depth, on the floor: the point with that x and z.
            Vector3 point = dir * far - new Vector3(0f, 0f, Eye);
            float flat = (floor.AlongX.X * floor.AlongY.Z) - (floor.AlongX.Z * floor.AlongY.X);
            if (!(MathF.Abs(flat) > 1e-12f))
            {
                fx = 0f;
                fy = 0f;
                return false;
            }

            float vx = point.X - floor.Origin.X;
            float vz = point.Z - floor.Origin.Z;
            fx = ((vx * floor.AlongY.Z) - (vz * floor.AlongY.X)) / flat;
            fy = ((floor.AlongX.X * vz) - (floor.AlongX.Z * vx)) / flat;
            return float.IsFinite(fx) && float.IsFinite(fy);
        }

        private static float Det(Vector3 a, Vector3 b, Vector3 c)
            => Vector3.Dot(a, Vector3.Cross(b, c));
    }
}
