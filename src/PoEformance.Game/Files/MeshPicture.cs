using System.Numerics;

namespace PoEformance.Game.Files;

/// <summary>
/// Draws a <see cref="SkinnedMesh"/> into a picture, on the processor.
/// </summary>
/// <remarks>
/// NO GRAPHICS DEVICE, ON PURPOSE. The overlay draws through ImGui, which has no 3D pipeline, so
/// a mesh has to become a TEXTURE before it can appear - and the moment it is one, everything
/// downstream already exists: GamePicture is what GameArt hands out for item icons and the
/// overlay uploads those today.
///
/// WHAT THAT BUYS IS THE ABILITY TO CHECK IT. A renderer talking to the overlay's D3D11 device
/// could only ever be judged by looking at a screenshot on a machine with the game; this one
/// produces an array of pixels that a test can measure - is the silhouette the right way up, does
/// it fill the frame, is the far side hidden behind the near one. Every one of those is a
/// question about a number, which is what this project keeps asking for.
///
/// AND IT IS PER FRAME WHILE SOMEBODY IS TURNING IT, which it was once written not to be. Dragging
/// a model redraws it on every frame the cursor moves: measured at 384 square against a real rig of
/// 4322 triangles with a 512 square skin, that is 4.1 ms of processor per frame - affordable beside
/// an overlay frame of about 6 ms, and only while the button is down. What it is NOT affordable
/// with is a fresh pair of buffers each time; see <see cref="Canvas"/>.
///
/// THE SKIN IS READ TRILINEARLY, from the level of <see cref="Mipmaps"/> whose texels are about a
/// pixel across, and that roughly doubles what a textured pixel costs: a quad filling the whole
/// frame with a 2048 square skin measured 9.0 ms a frame at 384 square against 4.8 with the
/// one-texel read it replaced, and 25.7 against 16.1 at 768. A real rig covers about a third of
/// the frame. What the one-texel read cost instead was a monster drawn as grain - see Mipmaps for
/// the report - and a picture that is wrong is not cheap at any price.
///
/// THE MODEL STANDS ALONG NEGATIVE Z, which is measured rather than assumed: BasicSkeleton's box
/// runs x ±77, y ±14.5, z -189 to -0.4, so the long axis is z, the feet are at the end nearest
/// zero and the head is at -189. Read with y as the up axis a skeleton comes out lying on its
/// face, 29 units tall and 154 wide.
/// </remarks>
public static class MeshPicture
{
    /// <summary>The biggest picture that will be drawn, each way.</summary>
    /// <remarks>A cap rather than a hope: the size reaches this from a caller, and the buffers are square.</remarks>
    public const int Widest = 2048;

    /// <summary>How much of the frame the model fills at rest, leaving a margin around it.</summary>
    public const float Fill = 0.86f;

    /// <summary>The furthest out a zoom may pull, and the closest it may push.</summary>
    /// <remarks>
    /// CLAMPED IN THE RENDERER RATHER THAN TRUSTED FROM THE CALLER, because the cost of a zoom is
    /// not symmetric: pulling back only wastes frame, while pushing in makes each triangle cover
    /// more pixels, and a mesh magnified far enough is a handful of triangles painting the whole
    /// buffer over and over. Six is already closer than any monster needs.
    /// </remarks>
    public const float Nearest = 0.4f;

    /// <inheritdoc cref="Nearest"/>
    public const float Furthest = 6f;

    /// <summary>How many squares across the ground grid is drawn.</summary>
    public const int Squares = 10;

    /// <summary>How far the ground grid reaches, as a share of what the camera fitted.</summary>
    /// <remarks>
    /// CHOSEN BY LOOKING, because "does the floor fit in the frame" is a question about a picture.
    /// A square seen at 45 degrees shows its DIAGONAL, so the usable share is roughly this times
    /// 2 times root 2 - at 0.35 that is 0.99 of what the camera fitted, which lands just inside
    /// the frame at the steepest tilt the portrait allows.
    /// </remarks>
    public const float Floor = 0.35f;

    /// <summary>How far under the feet the ground sits, as a share of the model's height.</summary>
    /// <remarks>Enough to settle the depth test, far too little to look like the monster floats.</remarks>
    public const float Under = 0.002f;

    /// <summary>
    /// The two buffers a drawing works in, kept so that turning a model does not throw them away.
    /// </summary>
    /// <remarks>
    /// THIS EXISTS BECAUSE THE NUMBER WAS MEASURED RATHER THAN ASSUMED. At 384 square the pixels
    /// come to 576 KB and the depth buffer to another 576 KB, and both are over the 85 KB that
    /// sends an array to the LARGE OBJECT HEAP. Allocating a pair per call cost nothing while a
    /// model was drawn once per monster picked; the moment a drag redraws it per frame the same
    /// code threw away 1.1 MB a frame - 67 MB and thirteen gen-2 collections per SECOND of
    /// turning, which is a stutter in the overlay rather than a number in a profiler. One canvas
    /// held by the caller costs that 1.1 MB once.
    ///
    /// THE PIXELS ARE LENT, NOT GIVEN. The picture handed back points into this canvas, so it is
    /// good only until the next drawing into the same one. That suits the caller it was made for -
    /// the portrait copies the pixels into a texture and is done with them - and it is why the
    /// allocating overload is still the one a test should reach for.
    /// </remarks>
    public sealed class Canvas
    {
        /// <param name="size">How many pixels each way, clamped to <see cref="Widest"/>.</param>
        public Canvas(int size)
        {
            Size = Math.Clamp(size, 1, Widest);
            Pixels = new byte[Size * Size * 4];
            Depth = new float[Size * Size];
        }

        /// <summary>How many pixels each way this canvas draws.</summary>
        public int Size { get; }

        internal byte[] Pixels { get; }

        internal float[] Depth { get; }
    }

    /// <summary>
    /// Draws the mesh into buffers of its own, turned about its up axis by <paramref name="turn"/>.
    /// </summary>
    /// <param name="mesh">What to draw. An empty one gives an empty picture.</param>
    /// <param name="size">How many pixels each way.</param>
    /// <param name="turn">Rotation about the model's up axis, in radians.</param>
    /// <param name="tilt">Rotation towards the viewer, in radians. Zero looks at it level.</param>
    /// <param name="ink">The colour to shade with where there is no skin, red green blue in 0..1.</param>
    /// <param name="skin">
    /// The monster's own colour texture with its levels, or null to draw it in <paramref name="ink"/>.
    /// What <see cref="MaterialFile.Albedo"/> names, decoded by <see cref="GameArt"/> and halved
    /// down by <see cref="Mipmaps"/>.
    /// </param>
    /// <param name="zoom">How much closer than the fitted view, 1 being the whole model in frame.</param>
    /// <param name="ground">Whether to draw the grid the model stands on.</param>
    public static GamePicture Of(
        SkinnedMesh? mesh,
        int size,
        float turn = 0f,
        float tilt = 0f,
        Vector3 ink = default,
        Mipmaps? skin = null,
        float zoom = 1f,
        bool ground = false)
        => Of(mesh, new Canvas(size), turn, tilt, ink, skin, zoom, ground);

    /// <summary>
    /// Draws the mesh into a canvas the caller keeps, for anything that draws it more than once.
    /// </summary>
    /// <param name="mesh">What to draw. An empty one gives an empty picture.</param>
    /// <param name="canvas">Where to draw. Its pixels are overwritten, and lent out - see <see cref="Canvas"/>.</param>
    /// <param name="turn">Rotation about the model's up axis, in radians.</param>
    /// <param name="tilt">Rotation towards the viewer, in radians. Zero looks at it level.</param>
    /// <param name="ink">The colour to shade with where there is no skin, red green blue in 0..1.</param>
    /// <param name="skin">The monster's own colour texture with its levels, or null to draw it in <paramref name="ink"/>.</param>
    /// <param name="zoom">How much closer than the fitted view, 1 being the whole model in frame.</param>
    /// <param name="ground">Whether to draw the grid the model stands on.</param>
    public static GamePicture Of(
        SkinnedMesh? mesh,
        Canvas canvas,
        float turn = 0f,
        float tilt = 0f,
        Vector3 ink = default,
        Mipmaps? skin = null,
        float zoom = 1f,
        bool ground = false)
        => Of(mesh, canvas, mesh?.Positions ?? [], mesh?.Normals ?? [], turn, tilt, ink, skin, zoom, ground);

    /// <summary>
    /// Draws the mesh with its vertices somewhere other than the file put them - posed.
    /// </summary>
    /// <param name="mesh">What to draw: its triangles, coordinates and box. Its own vertices are not used.</param>
    /// <param name="canvas">Where to draw. Its pixels are overwritten, and lent out - see <see cref="Canvas"/>.</param>
    /// <param name="positions">Where each vertex is now, one per vertex of the mesh.</param>
    /// <param name="normals">Which way each faces now, the same length.</param>
    /// <param name="turn">Rotation about the model's up axis, in radians.</param>
    /// <param name="tilt">Rotation towards the viewer, in radians. Zero looks at it level.</param>
    /// <param name="ink">The colour to shade with where there is no skin, red green blue in 0..1.</param>
    /// <param name="skin">The monster's own colour texture with its levels, or null to draw it in <paramref name="ink"/>.</param>
    /// <param name="zoom">How much closer than the fitted view, 1 being the whole model in frame.</param>
    /// <param name="ground">Whether to draw the grid the model stands on.</param>
    /// <remarks>
    /// THE CAMERA STAYS ON THE BIND POSE'S BOX, deliberately. An animation moves vertices outside
    /// the box the file wrote - a raised arm, a lunge - and refitting the view to them every frame
    /// would make the whole picture breathe in and out as the monster moved. The box it was fitted
    /// to standing still is the one it is drawn in while it moves.
    ///
    /// POSED ARRAYS THAT DO NOT FIT THE MESH ARE IGNORED in favour of the mesh's own, rather than
    /// indexed past their end: every index in the mesh addresses a vertex, and a short array would
    /// be a crash on the draw thread for a model that could simply have been drawn still.
    /// </remarks>
    public static GamePicture Of(
        SkinnedMesh? mesh,
        Canvas canvas,
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<Vector3> normals,
        float turn = 0f,
        float tilt = 0f,
        Vector3 ink = default,
        Mipmaps? skin = null,
        float zoom = 1f,
        bool ground = false)
    {
        ArgumentNullException.ThrowIfNull(canvas);

        int size = canvas.Size;
        byte[] pixels = canvas.Pixels;

        // CLEARED HERE AND NOT WHERE THE TRIANGLES START, because every way out of this method
        // returns these pixels. A canvas coming back for its second monster would otherwise show
        // the first one wherever the second draws nothing - and the emptier the mesh, the more of
        // the previous monster is left standing.
        Array.Clear(pixels);

        if (mesh is not { Ready: true })
        {
            return new GamePicture(size, size, pixels);
        }

        if (positions.Length != mesh.Positions.Length || normals.Length != mesh.Normals.Length)
        {
            positions = mesh.Positions;
            normals = mesh.Normals;
        }

        if (ink == default)
        {
            ink = new Vector3(0.78f, 0.75f, 0.70f);
        }

        // THE MODEL'S OWN BOX DECIDES THE CAMERA, so a rat and a boss both fill the frame. Its
        // centre becomes the origin and its longest side becomes the scale - there is no world
        // here to place it in, and a fixed scale would draw most monsters as specks.
        Vector3 middle = (mesh.Least + mesh.Most) * 0.5f;
        Vector3 span = Vector3.Abs(mesh.Most - mesh.Least);
        float reach = MathF.Max(span.X, MathF.Max(span.Y, span.Z));
        if (reach <= 0f)
        {
            return new GamePicture(size, size, pixels);
        }

        Matrix4x4 view = Matrix4x4.CreateTranslation(-middle)
            * Matrix4x4.CreateRotationZ(turn)
            * Matrix4x4.CreateRotationX(tilt)

            // Z IS THE MODEL'S UP AND IT POINTS THE WRONG WAY. Turning a quarter about x stands
            // the model on its feet for a screen whose y grows downwards; without it, every
            // monster is drawn upside down, which is a picture and is the wrong one.
            * Matrix4x4.CreateRotationX(-MathF.PI / 2f);

        float scale = size * Fill * Math.Clamp(zoom, Nearest, Furthest) / reach;
        float half = size * 0.5f;

        float[] depth = canvas.Depth;
        Array.Fill(depth, float.MaxValue);

        // The light sits over the viewer's shoulder, which is the one placement that never leaves
        // a face black: anything pointing at the camera is lit.
        Vector3 lamp = Vector3.Normalize(new Vector3(-0.35f, -0.55f, -0.75f));

        // BEFORE THE MESH, so the model's own depth test hides whatever runs behind it. Painting
        // the floor afterwards would need a second rule about what may cover what; a depth buffer
        // already has one.
        if (ground)
        {
            Ground(pixels, depth, size, mesh, view, scale, half);
        }

        // The skin is only usable if it decoded AND the mesh carries coordinates to look it up
        // with - see SkinnedMesh.Coordinated for what an uncoordinated mesh would paint.
        Mipmaps? usable = skin is not null && mesh.Coordinated ? skin : null;

        Span<Vector3> corner = stackalloc Vector3[3];
        Span<Vector3> facing = stackalloc Vector3[3];
        Span<Vector2> onSkin = stackalloc Vector2[3];

        for (var one = 0; one + 2 < mesh.Indices.Length; one += 3)
        {
            for (var part = 0; part < 3; part++)
            {
                int point = mesh.Indices[one + part];
                Vector3 place = Vector3.Transform(positions[point], view);

                corner[part] = new Vector3(
                    (place.X * scale) + half,
                    (place.Y * scale) + half,
                    place.Z);

                facing[part] = Vector3.TransformNormal(normals[point], view);
                onSkin[part] = mesh.Coordinates[point];
            }

            Triangle(pixels, depth, size, corner, facing, onSkin, lamp, ink, usable);
        }

        return new GamePicture(size, size, pixels);
    }

    /// <summary>
    /// Fills one triangle, keeping whichever fragment is nearest.
    /// </summary>
    /// <remarks>
    /// A DEPTH BUFFER AND NOT A SORT. Painting back to front is the cheaper trick and it is wrong
    /// on exactly the meshes that matter here: a monster's own arm crossing its chest has no
    /// ordering that draws both correctly, because the triangles interleave. Per pixel is the only
    /// answer that does not depend on the order the file happens to list them in.
    ///
    /// NO BACK-FACE CULLING. It would halve the work, and it needs a winding order that the format
    /// has not been shown to keep - cull the wrong way and the model turns inside out. The depth
    /// buffer already hides the far side, so this costs time rather than correctness, and time is
    /// what there is plenty of for a picture drawn once.
    /// </remarks>
    private static void Triangle(
        byte[] pixels,
        float[] depth,
        int size,
        ReadOnlySpan<Vector3> corner,
        ReadOnlySpan<Vector3> facing,
        ReadOnlySpan<Vector2> onSkin,
        Vector3 lamp,
        Vector3 ink,
        Mipmaps? skin)
    {
        float area = Cross(corner[0], corner[1], corner[2]);
        if (MathF.Abs(area) < 1e-6f)
        {
            return;
        }

        float level = skin is null ? 0f : Level(corner, onSkin, area, skin);

        int least = Math.Max(0, (int)MathF.Floor(Min3(corner[0].X, corner[1].X, corner[2].X)));
        int most = Math.Min(size - 1, (int)MathF.Ceiling(Max3(corner[0].X, corner[1].X, corner[2].X)));
        int top = Math.Max(0, (int)MathF.Floor(Min3(corner[0].Y, corner[1].Y, corner[2].Y)));
        int foot = Math.Min(size - 1, (int)MathF.Ceiling(Max3(corner[0].Y, corner[1].Y, corner[2].Y)));

        for (int y = top; y <= foot; y++)
        {
            for (int x = least; x <= most; x++)
            {
                var place = new Vector3(x + 0.5f, y + 0.5f, 0f);

                float first = Cross(corner[1], corner[2], place) / area;
                float second = Cross(corner[2], corner[0], place) / area;
                float third = 1f - first - second;

                if (first < 0f || second < 0f || third < 0f)
                {
                    continue;
                }

                float away = (first * corner[0].Z) + (second * corner[1].Z) + (third * corner[2].Z);
                int at = (y * size) + x;
                if (away >= depth[at])
                {
                    continue;
                }

                depth[at] = away;

                Vector3 normal = (first * facing[0]) + (second * facing[1]) + (third * facing[2]);
                if (normal.LengthSquared() > 1e-6f)
                {
                    normal = Vector3.Normalize(normal);
                }

                // TWO-SIDED, because the mesh's winding is not established and a single-sided
                // light leaves whole limbs black where the triangles happen to face away.
                float lit = MathF.Abs(Vector3.Dot(normal, lamp));
                float shade = 0.22f + (0.78f * lit);

                Vector3 colour = ink;
                if (skin is not null)
                {
                    // AFFINE INTERPOLATION IS EXACT HERE. The projection is orthographic, so a
                    // coordinate across the triangle really is linear in screen space - the
                    // perspective correction a game renderer needs would be dividing by a w that
                    // is always one.
                    Vector2 spot = (first * onSkin[0]) + (second * onSkin[1]) + (third * onSkin[2]);
                    colour = Sample(skin, spot, level);
                }

                pixels[(at * 4) + 0] = Byte(colour.X * shade);
                pixels[(at * 4) + 1] = Byte(colour.Y * shade);
                pixels[(at * 4) + 2] = Byte(colour.Z * shade);
                pixels[(at * 4) + 3] = 255;
            }
        }
    }

    /// <summary>
    /// The grid the model stands on, drawn on the plane under its feet.
    /// </summary>
    /// <remarks>
    /// THE FEET ARE AT Most.Z AND NOT Least.Z. A model runs along negative z with its head at the
    /// far end - BasicSkeleton's box is z -189 to -0.4 - so the floor is the end NEAREST zero.
    /// Reading it the other way draws the grid across the monster's scalp, which is a picture and
    /// is the wrong one.
    ///
    /// IT IS EDGE ON AT A LEVEL VIEW, and that is right rather than broken. A floor seen from its
    /// own height is a line; tilting down opens it out. It is what makes a turn legible - a bare
    /// model rotating against nothing gives the eye no fixed thing to measure against.
    ///
    /// DEPTH IS WRITTEN, so the model occludes the part of the floor behind it without anybody
    /// deciding an order. The far half of the grid disappearing behind a monster's legs is the
    /// whole reason it reads as a floor and not as wallpaper.
    /// </remarks>
    private static void Ground(
        byte[] pixels, float[] depth, int size, SkinnedMesh mesh,
        Matrix4x4 view, float scale, float half)
    {
        Vector3 middle = (mesh.Least + mesh.Most) * 0.5f;
        Vector3 span = Vector3.Abs(mesh.Most - mesh.Least);

        // MEASURED OFF THE SAME SIDE THE CAMERA IS, and not off the footprint. A floor sized to a
        // monster's own width is wider than the frame for anything tall - a skeleton is 154 across
        // and 189 high, so the camera fits 189 and a footprint-sized floor runs off both edges.
        // Tying it to what the camera fitted keeps the floor inside the picture at every tilt,
        // and still gives a rat a small one and a boss a big one.
        float reach = MathF.Max(span.X, MathF.Max(span.Y, span.Z)) * Floor;
        if (reach <= 0f)
        {
            return;
        }

        // A HAIR BELOW THE FEET AND NOT EXACTLY AT THEM. Most.Z is where the lowest triangle sits,
        // so a floor drawn at it is at the SAME depth as the sole - and the depth test keeps
        // whichever got there first, which is the floor. The symptom is grid lines cutting across
        // a monster's feet. Below means a LARGER z, because the model runs along negative z with
        // its head at the far end.
        float floor = mesh.Most.Z + (span.Z * Under);
        float step = reach * 2f / Squares;

        // Dim enough to stay behind the monster rather than compete with it, and the two middle
        // lines lighter so there is something to read the turn against.
        var faint = new Vector3(0.26f, 0.25f, 0.22f);
        var axis = new Vector3(0.46f, 0.44f, 0.38f);

        for (var i = 0; i <= Squares; i++)
        {
            float at = -reach + (i * step);
            Vector3 ink = i == Squares / 2 ? axis : faint;

            Line(
                pixels, depth, size, view, scale, half,
                new Vector3(middle.X + at, middle.Y - reach, floor),
                new Vector3(middle.X + at, middle.Y + reach, floor),
                ink);

            Line(
                pixels, depth, size, view, scale, half,
                new Vector3(middle.X - reach, middle.Y + at, floor),
                new Vector3(middle.X + reach, middle.Y + at, floor),
                ink);
        }
    }

    /// <summary>One straight line of the grid, depth-tested like everything else.</summary>
    /// <remarks>
    /// STEPPED ALONG THE LONGER SIDE, which is what keeps a line solid at every angle: walking x
    /// on a line that is mostly vertical leaves a dotted one, and the grid turns with the model so
    /// every line is every angle in turn.
    /// </remarks>
    private static void Line(
        byte[] pixels, float[] depth, int size,
        Matrix4x4 view, float scale, float half,
        Vector3 from, Vector3 to, Vector3 ink)
    {
        Vector3 a = Screen(from, view, scale, half);
        Vector3 b = Screen(to, view, scale, half);

        float run = MathF.Max(MathF.Abs(b.X - a.X), MathF.Abs(b.Y - a.Y));
        var steps = (int)MathF.Ceiling(run);
        if (steps <= 0)
        {
            return;
        }

        for (var i = 0; i <= steps; i++)
        {
            Vector3 place = Vector3.Lerp(a, b, (float)i / steps);
            var x = (int)place.X;
            var y = (int)place.Y;

            if (x < 0 || y < 0 || x >= size || y >= size)
            {
                continue;
            }

            int spot = (y * size) + x;
            if (place.Z >= depth[spot])
            {
                continue;
            }

            depth[spot] = place.Z;
            pixels[(spot * 4) + 0] = Byte(ink.X);
            pixels[(spot * 4) + 1] = Byte(ink.Y);
            pixels[(spot * 4) + 2] = Byte(ink.Z);
            pixels[(spot * 4) + 3] = 255;
        }
    }

    /// <summary>A point in the model's own space, put where it lands on the picture.</summary>
    private static Vector3 Screen(Vector3 place, Matrix4x4 view, float scale, float half)
    {
        Vector3 seen = Vector3.Transform(place, view);
        return new Vector3((seen.X * scale) + half, (seen.Y * scale) + half, seen.Z);
    }

    /// <summary>
    /// Which level of the skin a triangle reads, as a number that may fall between two of them.
    /// </summary>
    /// <remarks>
    /// ONE NUMBER PER TRIANGLE, AND THAT IS EXACT HERE rather than an approximation: the projection
    /// is orthographic and the coordinates are interpolated linearly, so how many texels one pixel
    /// steps across is the same at every pixel of the triangle. A game renderer works it out per
    /// pixel because perspective changes it across a face; this one has no perspective.
    ///
    /// THE LONGER OF THE TWO STEPS DECIDES, as a graphics card's sampler does. A face seen nearly
    /// edge-on steps across many texels in one screen direction and few in the other, and the
    /// level that suits the short step is grain along the long one. Blur along the short step is
    /// the price, and it is the cheaper of the two.
    /// </remarks>
    private static float Level(
        ReadOnlySpan<Vector3> corner, ReadOnlySpan<Vector2> onSkin, float area, Mipmaps skin)
    {
        // The two edges out of the first corner, on the screen and on the skin.
        float e1x = corner[1].X - corner[0].X;
        float e1y = corner[1].Y - corner[0].Y;
        float e2x = corner[2].X - corner[0].X;
        float e2y = corner[2].Y - corner[0].Y;
        Vector2 d1 = onSkin[1] - onSkin[0];
        Vector2 d2 = onSkin[2] - onSkin[0];

        // How the coordinate changes per pixel to the right and per pixel down: the two-by-two
        // solve of "the gradient along each edge is that edge's change", whose determinant is the
        // area already in hand. In texels, so that the answer is a count of them.
        float wide = skin.Top.Width;
        float high = skin.Top.Height;
        float ux = ((e2y * d1.X) - (e1y * d2.X)) / area * wide;
        float vx = ((e2y * d1.Y) - (e1y * d2.Y)) / area * high;
        float uy = ((e1x * d2.X) - (e2x * d1.X)) / area * wide;
        float vy = ((e1x * d2.Y) - (e2x * d1.Y)) / area * high;

        float longest = MathF.Max((ux * ux) + (vx * vx), (uy * uy) + (vy * vy));

        // Under one texel a pixel is a magnification, and the top level read bilinearly is right;
        // the same branch takes anything that is not a number, which degenerate coordinates give.
        return longest > 1f ? MathF.Min(0.5f * MathF.Log2(longest), skin.Count - 1) : 0f;
    }

    /// <summary>
    /// The skin's colour at a point, read from the level the triangle asked for.
    /// </summary>
    /// <remarks>
    /// BILINEAR WITHIN A LEVEL AND LINEAR BETWEEN TWO, which a graphics card calls trilinear.
    /// Nearest neighbour was the first cut, on the argument that a shrink gains nothing from
    /// filtering - true of a shrink by two, and grain at a shrink by ten, which is what a boss's
    /// skin is at portrait size. With the levels doing the shrinking, the read within a level is
    /// near enough one texel per pixel, and there bilinear is the difference between texels and a
    /// surface. The blend between two levels is what keeps neighbouring triangles that fall either
    /// side of a whole level from meeting at a visible seam.
    /// </remarks>
    private static Vector3 Sample(Mipmaps skin, Vector2 spot, float level)
    {
        var lower = (int)level;
        Vector3 colour = Texel(skin[lower], spot);

        float between = level - lower;
        if (between > 0f && lower + 1 < skin.Count)
        {
            colour = Vector3.Lerp(colour, Texel(skin[lower + 1], spot), between);
        }

        return colour;
    }

    /// <summary>
    /// One level read bilinearly at a point, wrapping at every edge.
    /// </summary>
    /// <remarks>
    /// THE GAME'S V RUNS NEGATIVE - the skeleton's coordinates measured -0.997 to -0.002 - so a
    /// reader that clamped instead of wrapping would paint every monster with the single row of
    /// texels along one edge. Wrapping costs one floor and handles both signs, and the neighbour
    /// past the last texel is the first, so a seam wraps as the coordinate does.
    /// </remarks>
    private static Vector3 Texel(GamePicture level, Vector2 spot)
    {
        float u = spot.X - MathF.Floor(spot.X);
        float v = spot.Y - MathF.Floor(spot.Y);

        // A fraction just under one rounds to one in single precision, and a coordinate that is
        // not a number gives one that is not either; both read the first texel rather than a
        // place past the end.
        if (!(u < 1f))
        {
            u = 0f;
        }

        if (!(v < 1f))
        {
            v = 0f;
        }

        // Texel centres sit half a texel in, so a point half a texel from an edge reads that
        // edge's texel alone and a point on the edge reads it and its neighbour across the wrap.
        float x = (u * level.Width) - 0.5f;
        float y = (v * level.Height) - 0.5f;
        var x0 = (int)MathF.Floor(x);
        var y0 = (int)MathF.Floor(y);
        float fx = x - x0;
        float fy = y - y0;

        int x1 = x0 + 1;
        int y1 = y0 + 1;
        if (x0 < 0)
        {
            x0 += level.Width;
        }

        if (y0 < 0)
        {
            y0 += level.Height;
        }

        if (x1 >= level.Width)
        {
            x1 -= level.Width;
        }

        if (y1 >= level.Height)
        {
            y1 -= level.Height;
        }

        byte[] rgba = level.Rgba;
        int a = ((y0 * level.Width) + x0) * 4;
        int b = ((y0 * level.Width) + x1) * 4;
        int c = ((y1 * level.Width) + x0) * 4;
        int d = ((y1 * level.Width) + x1) * 4;

        Vector3 upper = Vector3.Lerp(
            new Vector3(rgba[a], rgba[a + 1], rgba[a + 2]), new Vector3(rgba[b], rgba[b + 1], rgba[b + 2]), fx);
        Vector3 lower = Vector3.Lerp(
            new Vector3(rgba[c], rgba[c + 1], rgba[c + 2]), new Vector3(rgba[d], rgba[d + 1], rgba[d + 2]), fx);

        return Vector3.Lerp(upper, lower, fy) * (1f / 255f);
    }

    /// <summary>Twice the signed area of a triangle, flattened onto the screen.</summary>
    private static float Cross(Vector3 a, Vector3 b, Vector3 c)
        => ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));

    private static float Min3(float a, float b, float c) => MathF.Min(a, MathF.Min(b, c));

    private static float Max3(float a, float b, float c) => MathF.Max(a, MathF.Max(b, c));

    private static byte Byte(float said) => (byte)Math.Clamp(said * 255f, 0f, 255f);
}
