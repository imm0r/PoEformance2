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
    ///
    /// A QUARTER IS THE FAR END, four times what the camera fitted: asked for from the live client
    /// for the monsters whose pose reaches well past their box, which the first limit of 0.4 could
    /// not get whole into the frame.
    /// </remarks>
    public const float Nearest = 0.25f;

    /// <inheritdoc cref="Nearest"/>
    public const float Furthest = 6f;

    /// <summary>How many squares of the ground the model's longest side spans, seen from the fitted distance.</summary>
    /// <remarks>
    /// THE SQUARES SCALE WITH THE MONSTER, so a rat and a boss both stand on squares that suit
    /// them. Ten, where the first floors had fourteen: reported from the live client as a mesh too
    /// fine to read as a floor. Pulled far back the squares double until they are still squares on
    /// the picture rather than a weave - see Ground.
    /// </remarks>
    public const int Squares = 10;

    /// <summary>How far the ground reaches from the model's origin, as a multiple of what the camera fitted.</summary>
    /// <remarks>
    /// A DISC THAT FADES TO NOTHING AT ITS OWN RIM, and not a plate with an edge. The first floor
    /// reached 0.35 of the model and read as a small rhombus turning with the monster; the second
    /// reached two, on the argument that two runs off the frame at every zoom - which is true only
    /// looking down. Seen nearly level the floor is foreshortened to a sliver and its far edge came
    /// back into the frame as a hard line, which is what the live client showed next. So the rim is
    /// soft at any tilt: the ink fades with distance from the model's origin and is gone by this
    /// far out, and the frame's own edge fades it again for the part that does run off.
    /// </remarks>
    public const float Floor = 2.5f;

    /// <summary>
    /// The spacing on the picture, in pixels, under which a family of the floor's lines is gone,
    /// and at which it is whole.
    /// </summary>
    /// <remarks>
    /// LINES CLOSER THAN A FEW PIXELS ARE NOT A GRID, THEY ARE A FILL. Seen nearly level, the
    /// lines that run across the picture are pressed together by the foreshortening while the
    /// ones that run into the depth keep their spacing, and the live client showed the first
    /// family as a solid band. A family fades out as its spacing closes, and is not drawn at all
    /// once it has closed.
    /// </remarks>
    private const float Crowded = 3f;

    /// <inheritdoc cref="Crowded"/>
    private const float Spaced = 14f;

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
    /// <param name="pan">Where the model's centre sits, as a share of the picture off its middle, right and down. See <see cref="Panned"/>.</param>
    public static GamePicture Of(
        SkinnedMesh? mesh,
        int size,
        float turn = 0f,
        float tilt = 0f,
        Vector3 ink = default,
        Mipmaps? skin = null,
        float zoom = 1f,
        bool ground = false,
        Vector2 pan = default)
        => Of(mesh, new Canvas(size), turn, tilt, ink, skin, zoom, ground, pan);

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
    /// <param name="pan">Where the model's centre sits, as a share of the picture off its middle, right and down. See <see cref="Panned"/>.</param>
    public static GamePicture Of(
        SkinnedMesh? mesh,
        Canvas canvas,
        float turn = 0f,
        float tilt = 0f,
        Vector3 ink = default,
        Mipmaps? skin = null,
        float zoom = 1f,
        bool ground = false,
        Vector2 pan = default)
        => Of(mesh, canvas, mesh?.Positions ?? [], mesh?.Normals ?? [], turn, tilt, ink, skin, zoom, ground, pan);

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
    /// <param name="pan">Where the model's centre sits, as a share of the picture off its middle, right and down. See <see cref="Panned"/>.</param>
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
        bool ground = false,
        Vector2 pan = default)
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

        // THE MIDDLE OF THE PICTURE IS WHERE THE MODEL'S CENTRE LANDS, moved by the pan - a share
        // of the picture rather than pixels, so the same pan draws the same view on every rung.
        if (!float.IsFinite(pan.X) || !float.IsFinite(pan.Y))
        {
            pan = default;
        }

        Vector2 centre = new Vector2(size * 0.5f) + (pan * size);

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
            Ground(pixels, depth, size, mesh, view, scale, centre);
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
                    (place.X * scale) + centre.X,
                    (place.Y * scale) + centre.Y,
                    place.Z);

                facing[part] = Vector3.TransformNormal(normals[point], view);
                onSkin[part] = mesh.Coordinates[point];
            }

            Triangle(pixels, depth, size, corner, facing, onSkin, lamp, ink, usable);
        }

        return new GamePicture(size, size, pixels);
    }

    /// <summary>
    /// Where the model's centre goes so that the point under the pointer stays put across a zoom.
    /// </summary>
    /// <param name="pan">The pan the picture was drawn with.</param>
    /// <param name="pointer">Where the pointer is, as a share of the picture from its top left corner.</param>
    /// <param name="from">The zoom the picture was drawn at.</param>
    /// <param name="to">The zoom it is about to be drawn at.</param>
    /// <remarks>
    /// ZOOMING INTO THE MIDDLE WAS THE ONE THING REPORTED AGAINST THE WHEEL: a monster's head is
    /// never in the middle, so the way to look at it closely was to zoom past it and lose it. This
    /// is the map's rule instead - whatever is under the pointer stays under it, so pushing in on
    /// the head lands on the head.
    ///
    /// THE ARITHMETIC IS ONE LINE. A model point lands at 0.5 + pan + k m, where k grows with the
    /// zoom, so keeping the point under the pointer p where it is while k becomes k f puts the new
    /// pan at (p - 0.5)(1 - f) + f pan. It is HERE RATHER THAN IN THE PORTRAIT for the reason
    /// PictureLadder gives: the overlay cannot be tested, and a sign wrong in this is a zoom that
    /// runs away from the pointer instead of towards it.
    ///
    /// CLAMPED SO THE MODEL CANNOT BE LOST. Pulling back with the pointer in a corner would
    /// otherwise drag the model into that corner and out of the frame; the centre may go no
    /// further off the middle than half of what the model's box spans on the picture, so the box
    /// always reaches the middle - and at the closest zoom that is exactly far enough to bring the
    /// top of the head there.
    /// </remarks>
    public static Vector2 Panned(Vector2 pan, Vector2 pointer, float from, float to)
    {
        if (!float.IsFinite(pointer.X) || !float.IsFinite(pointer.Y))
        {
            pointer = new Vector2(0.5f);
        }

        if (!float.IsFinite(pan.X) || !float.IsFinite(pan.Y))
        {
            pan = default;
        }

        from = float.IsFinite(from) ? Math.Clamp(from, Nearest, Furthest) : 1f;
        to = float.IsFinite(to) ? Math.Clamp(to, Nearest, Furthest) : 1f;
        float grew = to / from;

        Vector2 moved = ((pointer - new Vector2(0.5f)) * (1f - grew)) + (pan * grew);
        float most = 0.5f * Fill * to;
        return new Vector2(Math.Clamp(moved.X, -most, most), Math.Clamp(moved.Y, -most, most));
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
    /// The grid the model stands on, drawn on the plane through its origin.
    /// </summary>
    /// <remarks>
    /// AT THE MODEL'S ORIGIN, AND NOT AT THE BOTTOM OF ITS BOX, which is where the first floors
    /// went and where two monsters in a row showed it wrong from the live client - one standing
    /// shin-deep in the floor, the next hovering above it. Measured on a real rig, the feet sit at
    /// z = 0.98 in the bind pose and STAY there through every frame of an animation while the
    /// pelvis dips by fifteen units: the game plants a monster on the ground by its origin, and its
    /// animations are made to keep the feet on that plane. A box bottom that happens to be
    /// somewhere else - a weapon hanging lower, a pose that reaches - is not where the feet are.
    /// BasicSkeleton's box bottom is -0.4, which is why the box ever looked right.
    ///
    /// The model runs along negative z with its head at the far end, so the ground is z = 0 and
    /// below the feet means a LARGER z. Reading it the other way draws the grid across the
    /// monster's scalp, which is a picture and is the wrong one.
    ///
    /// IT IS EDGE ON AT A LEVEL VIEW, and that is right rather than broken. A floor seen from its
    /// own height is a line; tilting down opens it out. It is what makes a turn legible - a bare
    /// model rotating against nothing gives the eye no fixed thing to measure against.
    ///
    /// DEPTH IS WRITTEN, so the model occludes the part of the floor behind it without anybody
    /// deciding an order. The far half of the grid disappearing behind a monster's legs is the
    /// whole reason it reads as a floor and not as wallpaper.
    ///
    /// ONLY THE LINES THAT CROSS THE FRAME ARE DRAWN. The floor's map onto the picture is affine,
    /// so the frame's four corners map back onto the floor and bound the lines worth drawing, and
    /// each of those is drawn only between the frame's own floor-space edges. The cost is bounded
    /// by the frame and the spacing, not by how far the floor reaches.
    /// </remarks>
    private static void Ground(
        byte[] pixels, float[] depth, int size, SkinnedMesh mesh,
        Matrix4x4 view, float scale, Vector2 centre)
    {
        Vector3 span = Vector3.Abs(mesh.Most - mesh.Least);
        float fitted = MathF.Max(span.X, MathF.Max(span.Y, span.Z));
        if (fitted <= 0f)
        {
            return;
        }

        // A HAIR BELOW THE FEET AND NOT EXACTLY AT THEM, so that a sole lying on the ground wins
        // the depth tie: the test keeps whichever got there first, which would be the floor.
        float floor = span.Z * Under;
        float reach = fitted * Floor;

        // The floor's map onto the picture: its origin, and one unit along each of its axes.
        // THE FLOOR'S DEPTH IS THE PLANE'S AT THE PIXEL'S CENTRE, not the depth of wherever a
        // step along a line happened to land inside the pixel. The model's own depth is taken at
        // pixel centres, and a floor sampled half a pixel off has, at a steep tilt, a depth that
        // differs by more than the hair of clearance under the feet: measured, 24 pixels of a flat
        // sole lost to the floor the moment the lines were stepped from a different start.
        Vector3 origin = Screen(new Vector3(0f, 0f, floor), view, scale, centre);
        Vector3 alongX = Screen(new Vector3(1f, 0f, floor), view, scale, centre);
        Vector3 alongY = Screen(new Vector3(0f, 1f, floor), view, scale, centre);
        Plane plane = Plane.Through(origin, alongX, alongY);

        // Dim enough to stay behind the monster rather than compete with it, and every fifth line
        // lighter - the two through the origin among them - so there is something to read the turn
        // against.
        var faint = new Vector3(0.26f, 0.25f, 0.22f);
        var axis = new Vector3(0.46f, 0.44f, 0.38f);

        // EDGE ON, the plane is a line on the picture and every line of the grid lies along it, so
        // the two axes drawn once are the whole of what can be seen.
        if (!plane.Known)
        {
            Line(pixels, depth, size, view, scale, centre, plane, new Vector2(-reach, 0f), new Vector2(reach, 0f), floor, reach, axis, 1f);
            Line(pixels, depth, size, view, scale, centre, plane, new Vector2(0f, -reach), new Vector2(0f, reach), floor, reach, axis, 1f);
            return;
        }

        // HOW FAR APART NEIGHBOURING LINES OF EACH FAMILY LAND ON THE PICTURE. One square of the
        // floor is a parallelogram there, of area step times det; a family's spacing is that area
        // over the length of the other family's edge.
        var ex = new Vector2(alongX.X - origin.X, alongX.Y - origin.Y);
        var ey = new Vector2(alongY.X - origin.X, alongY.Y - origin.Y);
        float det = (ex.X * ey.Y) - (ex.Y * ey.X);
        float perX = MathF.Abs(det) / ey.Length();
        float perY = MathF.Abs(det) / ex.Length();

        // PULLED FAR BACK THE SQUARES DOUBLE, until the better-spaced family is still a grid on the
        // picture rather than a weave; doubling keeps every line that stays a line where it was.
        float step = fitted / Squares;
        while (step * MathF.Max(perX, perY) < Spaced && step < reach)
        {
            step *= 2f;
        }

        float acrossX = Family(step * perX);
        float acrossY = Family(step * perY);
        if (acrossX <= 0f && acrossY <= 0f)
        {
            return;
        }

        // The frame's corners, back on the floor, bound what is worth drawing - and the disc's rim
        // bounds it again, since past the rim the ink is gone anyway.
        float leastX = reach;
        float mostX = -reach;
        float leastY = reach;
        float mostY = -reach;
        Span<Vector2> corners = [new(0f, 0f), new(size, 0f), new(0f, size), new(size, size)];
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

        leastX = MathF.Max(leastX, -reach);
        mostX = MathF.Min(mostX, reach);
        leastY = MathF.Max(leastY, -reach);
        mostY = MathF.Min(mostY, reach);
        if (leastX > mostX || leastY > mostY)
        {
            return;
        }

        if (acrossX > 0f)
        {
            var from = (int)MathF.Ceiling(leastX / step);
            var to = (int)MathF.Floor(mostX / step);
            for (int i = from; i <= to; i++)
            {
                float at = i * step;
                Line(
                    pixels, depth, size, view, scale, centre, plane,
                    new Vector2(at, leastY), new Vector2(at, mostY), floor, reach,
                    i % 5 == 0 ? axis : faint, acrossX);
            }
        }

        if (acrossY > 0f)
        {
            var from = (int)MathF.Ceiling(leastY / step);
            var to = (int)MathF.Floor(mostY / step);
            for (int j = from; j <= to; j++)
            {
                float at = j * step;
                Line(
                    pixels, depth, size, view, scale, centre, plane,
                    new Vector2(leastX, at), new Vector2(mostX, at), floor, reach,
                    j % 5 == 0 ? axis : faint, acrossY);
            }
        }
    }

    /// <summary>How much of a family of lines is drawn, from the spacing its lines land at on the picture.</summary>
    private static float Family(float spacing)
        => Math.Clamp((spacing - Crowded) / (Spaced - Crowded), 0f, 1f);

    /// <summary>The depth of a plane across the picture, as the affine function of the pixel it is.</summary>
    /// <param name="AlongX">How much deeper it gets per pixel to the right.</param>
    /// <param name="AlongY">How much deeper it gets per pixel down.</param>
    /// <param name="At">Its depth at the picture's top left corner.</param>
    /// <param name="Known">False where the plane is edge-on and has no one depth per pixel.</param>
    private readonly record struct Plane(float AlongX, float AlongY, float At, bool Known)
    {
        /// <summary>The plane through three points already on the picture, with their depths.</summary>
        public static Plane Through(Vector3 origin, Vector3 alongX, Vector3 alongY)
        {
            float e1x = alongX.X - origin.X;
            float e1y = alongX.Y - origin.Y;
            float e2x = alongY.X - origin.X;
            float e2y = alongY.Y - origin.Y;
            float d1 = alongX.Z - origin.Z;
            float d2 = alongY.Z - origin.Z;

            // The same two-by-two solve as Level's: edge-on, the two edges are parallel on the
            // picture and there is nothing to solve.
            float det = (e1x * e2y) - (e1y * e2x);
            if (MathF.Abs(det) < 1e-6f)
            {
                return default;
            }

            float alongScreenX = ((e2y * d1) - (e1y * d2)) / det;
            float alongScreenY = ((e1x * d2) - (e2x * d1)) / det;
            float at = origin.Z - (alongScreenX * origin.X) - (alongScreenY * origin.Y);
            return new Plane(alongScreenX, alongScreenY, at, true);
        }

        /// <summary>The depth at a pixel's centre, or the depth handed in where the plane is edge-on.</summary>
        public float Deep(int x, int y, float sampled)
            => Known ? (AlongX * (x + 0.5f)) + (AlongY * (y + 0.5f)) + At : sampled;
    }

    /// <summary>One straight line of the grid, depth-tested like everything else and fading out at the disc's rim and the frame's edge.</summary>
    /// <param name="from">Where the line starts, on the floor.</param>
    /// <param name="to">Where it ends, on the floor.</param>
    /// <param name="floor">The z the floor is drawn at.</param>
    /// <param name="reach">The disc's radius, where the ink is gone.</param>
    /// <param name="ink">The colour.</param>
    /// <param name="family">How much of this line's family is drawn - see <see cref="Crowded"/>.</param>
    /// <remarks>
    /// STEPPED ALONG THE LONGER SIDE, which is what keeps a line solid at every angle: walking x
    /// on a line that is mostly vertical leaves a dotted one, and the grid turns with the model so
    /// every line is every angle in turn.
    ///
    /// CLIPPED TO THE FRAME BEFORE IT IS STEPPED. At the closest zoom a line of the floor is
    /// thousands of pixels long with a few dozen of them in the frame; the parametric clip finds
    /// those few dozen for four divisions, where stepping the whole line and testing each pixel
    /// was most of the floor's cost spent off the picture. The floor map being affine, the same
    /// parameter finds where on the floor the clipped ends are.
    ///
    /// FADED TWICE. With distance from the model's origin, to nothing at the disc's rim, so the
    /// floor has no edge at any tilt - seen nearly level its rim is inside the frame, and a hard
    /// one was what the live client showed. And with distance from the frame's middle, to nothing
    /// at the frame's own edge, so the part that does run off ends in air wherever the frame cuts
    /// it; that one is in the picture's own space, the same at every zoom and pan. The fourth
    /// power keeps the frame's fade solid over most of the picture and lets go in the last
    /// stretch.
    /// </remarks>
    private static void Line(
        byte[] pixels, float[] depth, int size,
        Matrix4x4 view, float scale, Vector2 centre, Plane plane,
        Vector2 from, Vector2 to, float floor, float reach, Vector3 ink, float family)
    {
        Vector3 a = Screen(new Vector3(from.X, from.Y, floor), view, scale, centre);
        Vector3 b = Screen(new Vector3(to.X, to.Y, floor), view, scale, centre);

        var t0 = 0f;
        var t1 = 1f;
        if (!Clip(a.X, b.X - a.X, size, ref t0, ref t1) || !Clip(a.Y, b.Y - a.Y, size, ref t0, ref t1))
        {
            return;
        }

        Vector3 start = Vector3.Lerp(a, b, t0);
        Vector3 end = Vector3.Lerp(a, b, t1);
        Vector2 onFloorStart = Vector2.Lerp(from, to, t0);
        Vector2 onFloorEnd = Vector2.Lerp(from, to, t1);
        float run = MathF.Max(MathF.Abs(end.X - start.X), MathF.Abs(end.Y - start.Y));
        var steps = (int)MathF.Ceiling(run);
        if (steps <= 0)
        {
            return;
        }

        float radius = size * 0.5f;
        float rim = reach * reach;
        for (var i = 0; i <= steps; i++)
        {
            float t = (float)i / steps;
            Vector3 place = Vector3.Lerp(start, end, t);
            var x = (int)place.X;
            var y = (int)place.Y;

            if (x < 0 || y < 0 || x >= size || y >= size)
            {
                continue;
            }

            float outward = Vector2.Lerp(onFloorStart, onFloorEnd, t).LengthSquared() / rim;
            float dx = place.X - radius;
            float dy = place.Y - radius;
            float away = ((dx * dx) + (dy * dy)) / (radius * radius);
            float fade = family * (1f - outward) * (1f - (away * away));
            if (fade <= 0f)
            {
                continue;
            }

            int spot = (y * size) + x;
            float deep = plane.Deep(x, y, place.Z);
            if (deep >= depth[spot])
            {
                continue;
            }

            depth[spot] = deep;
            pixels[(spot * 4) + 0] = Byte(ink.X);
            pixels[(spot * 4) + 1] = Byte(ink.Y);
            pixels[(spot * 4) + 2] = Byte(ink.Z);
            pixels[(spot * 4) + 3] = Byte(fade);
        }
    }

    /// <summary>Narrows a segment's parameter range to where one axis lies inside the frame; false where none of it does.</summary>
    private static bool Clip(float start, float delta, int size, ref float t0, ref float t1)
    {
        if (MathF.Abs(delta) < 1e-6f)
        {
            return start >= 0f && start < size;
        }

        float enter = -start / delta;
        float leave = (size - start) / delta;
        if (enter > leave)
        {
            (enter, leave) = (leave, enter);
        }

        t0 = MathF.Max(t0, enter);
        t1 = MathF.Min(t1, leave);
        return t0 <= t1;
    }

    /// <summary>A point in the model's own space, put where it lands on the picture.</summary>
    private static Vector3 Screen(Vector3 place, Matrix4x4 view, float scale, Vector2 centre)
    {
        Vector3 seen = Vector3.Transform(place, view);
        return new Vector3((seen.X * scale) + centre.X, (seen.Y * scale) + centre.Y, seen.Z);
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
