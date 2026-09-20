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
/// AND IT IS DRAWN IN BANDS OF ROWS ON EVERY CORE, since the pane grew to 1200 px and an animation
/// plays at thirty frames a second. Each band walks every triangle in the mesh's order and draws
/// the rows that are its own, so no pixel is ever touched by two threads and the picture is the
/// one-threaded one to the byte - the depth test needs no lock because it never has a race. What
/// it buys is most of the core count: on the four threads of the machine this was written on, a
/// rig-sized mesh filling the whole frame went from 26 ms to 9 at 512 square, 67 to 19 at 1024 and
/// 121 to 41 at 1536, best of three - and a real monster covers a third of the frame.
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
///
/// THE FLOOR IS NOT DRAWN HERE ANY MORE. It was, one pixel wide and depth-tested against the
/// model, and it came back from the live client as a staircase: the picture is drawn a rung below
/// the pane while an animation plays and stretched over it, and a stretched pixel line is steps.
/// <see cref="ModelFloor"/> works the same floor out as lines for the overlay's own draw list,
/// from the same <see cref="Camera"/> this draws with, so the two cannot disagree about where the
/// origin is.
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

    /// <summary>
    /// The view a picture is drawn from: where a point of the model lands on it.
    /// </summary>
    /// <param name="View">The model's space turned and tilted, with its up along the picture's y.</param>
    /// <param name="Scale">Shares of the picture's side per model unit. Zero where there is nothing to draw.</param>
    /// <param name="Centre">Where the model's centre lands, as a share of the side from the top left corner.</param>
    /// <param name="Tilt">The tilt it was made with, which is what says which side of the floor the eye is on.</param>
    /// <remarks>
    /// ONE PLACE THAT KNOWS THE CAMERA, because two things are drawn from it - the model into its
    /// pixels here, and the floor into the overlay's draw list by <see cref="ModelFloor"/> - and a
    /// floor worked out from a second copy of the arithmetic would drift off the model's feet by
    /// whatever the copies came to disagree on. In shares of the side rather than pixels, so the
    /// same camera serves the picture at whatever rung it is drawn and the floor at whatever size
    /// it is shown.
    /// </remarks>
    /// <param name="Reach">The longest side of the model's box, in its own units: what the picture was fitted to.</param>
    public readonly record struct Camera(Matrix4x4 View, float Scale, Vector2 Centre, float Tilt, float Reach)
    {
        /// <summary>Whether there is anything to see: a mesh with a box to fit.</summary>
        public bool Ready => Scale > 0f;

        /// <summary>The camera that fits this mesh's box, turned, tilted, zoomed and panned.</summary>
        /// <param name="mesh">What is looked at. Nothing, or a mesh with no box, gives a camera that is not <see cref="Ready"/>.</param>
        /// <param name="turn">Rotation about the model's up axis, in radians.</param>
        /// <param name="tilt">Rotation towards the viewer, in radians. Positive looks down.</param>
        /// <param name="zoom">How much closer than the fitted view, clamped to <see cref="Nearest"/> and <see cref="Furthest"/>.</param>
        /// <param name="pan">Where the model's centre sits, as a share of the picture off its middle. See <see cref="Panned"/>.</param>
        public static Camera Of(SkinnedMesh? mesh, float turn, float tilt, float zoom, Vector2 pan)
        {
            if (mesh is not { Ready: true })
            {
                return default;
            }

            // THE MODEL'S OWN BOX DECIDES THE CAMERA, so a rat and a boss both fill the frame. Its
            // centre becomes the origin and its longest side becomes the scale - there is no world
            // here to place it in, and a fixed scale would draw most monsters as specks.
            Vector3 middle = (mesh.Least + mesh.Most) * 0.5f;
            Vector3 span = Vector3.Abs(mesh.Most - mesh.Least);
            float reach = MathF.Max(span.X, MathF.Max(span.Y, span.Z));
            if (!(reach > 0f))
            {
                return default;
            }

            Matrix4x4 view = Matrix4x4.CreateTranslation(-middle)
                * Matrix4x4.CreateRotationZ(turn)
                * Matrix4x4.CreateRotationX(tilt)

                // Z IS THE MODEL'S UP AND IT POINTS THE WRONG WAY. Turning a quarter about x stands
                // the model on its feet for a screen whose y grows downwards; without it, every
                // monster is drawn upside down, which is a picture and is the wrong one.
                * Matrix4x4.CreateRotationX(-MathF.PI / 2f);

            // THE MIDDLE OF THE PICTURE IS WHERE THE MODEL'S CENTRE LANDS, moved by the pan - a
            // share of the picture rather than pixels, so the same pan draws the same view on
            // every rung.
            if (!float.IsFinite(pan.X) || !float.IsFinite(pan.Y))
            {
                pan = default;
            }

            float scale = Fill * Math.Clamp(zoom, Nearest, Furthest) / reach;
            return new Camera(view, scale, new Vector2(0.5f) + pan, tilt, reach);
        }

        /// <summary>Where a point of the model lands: x and y as shares of the side, z as the depth, nearer being less.</summary>
        public Vector3 Place(Vector3 point)
        {
            Vector3 seen = Vector3.Transform(point, View);
            return new Vector3((seen.X * Scale) + Centre.X, (seen.Y * Scale) + Centre.Y, seen.Z);
        }
    }

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
    ///
    /// THE SCRATCH FOR A MESH LIVES HERE TOO: where every vertex lands and faces this frame, and
    /// each triangle's rows and skin level, worked out once and read by every band. Grown to the
    /// largest mesh drawn and kept, for the reason the buffers are.
    /// </remarks>
    public sealed class Canvas
    {
        /// <param name="size">How many pixels each way, clamped to <see cref="Widest"/>.</param>
        /// <param name="threads">How many threads may draw at once. Anything under one means every processor.</param>
        public Canvas(int size, int threads = 0)
        {
            Size = Math.Clamp(size, 1, Widest);
            Pixels = new byte[Size * Size * 4];
            Depth = new float[Size * Size];
            Threads = Math.Clamp(threads > 0 ? threads : Environment.ProcessorCount, 1, 64);
        }

        /// <summary>How many pixels each way this canvas draws.</summary>
        public int Size { get; }

        /// <summary>How many threads a drawing into this canvas may use.</summary>
        public int Threads { get; }

        internal byte[] Pixels { get; }

        internal float[] Depth { get; }

        internal Vector3[] Corners { get; private set; } = [];

        internal Vector3[] Facings { get; private set; } = [];

        internal int[] Tops { get; private set; } = [];

        internal int[] Feet { get; private set; } = [];

        internal float[] Levels { get; private set; } = [];

        /// <summary>Which texture each triangle wears, as an index into the drawing's palette.</summary>
        /// <remarks>
        /// AN INDEX AND NOT THE TEXTURE ITSELF, so this canvas does not hold a monster's
        /// megabytes alive after it has been drawn. The palette is built per call and is a
        /// handful of entries; this is a number per triangle, worked out once with the rows.
        /// </remarks>
        internal int[] Wears { get; private set; } = [];

        internal void Fit(int vertices, int triangles)
        {
            if (Corners.Length < vertices)
            {
                Corners = new Vector3[vertices];
                Facings = new Vector3[vertices];
            }

            if (Tops.Length < triangles)
            {
                Tops = new int[triangles];
                Feet = new int[triangles];
                Levels = new float[triangles];
                Wears = new int[triangles];
            }
        }
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
    /// <param name="pan">Where the model's centre sits, as a share of the picture off its middle, right and down. See <see cref="Panned"/>.</param>
    /// <param name="skins">
    /// One texture per shape of the mesh, in its order, for a monster built out of parts. A
    /// null entry draws its shape in <paramref name="ink"/>; a shorter list leaves the rest to
    /// <paramref name="skin"/>. See MonsterModel.Skins for why a monster needs more than one.
    /// </param>
    public static GamePicture Of(
        SkinnedMesh? mesh,
        int size,
        float turn = 0f,
        float tilt = 0f,
        Vector3 ink = default,
        Mipmaps? skin = null,
        float zoom = 1f,
        Vector2 pan = default,
        IReadOnlyList<Mipmaps?>? skins = null)
        => Of(mesh, new Canvas(size), turn, tilt, ink, skin, zoom, pan, skins);

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
    /// <param name="pan">Where the model's centre sits, as a share of the picture off its middle, right and down. See <see cref="Panned"/>.</param>
    /// <param name="skins">
    /// One texture per shape of the mesh, in its order, for a monster built out of parts. A
    /// null entry draws its shape in <paramref name="ink"/>; a shorter list leaves the rest to
    /// <paramref name="skin"/>. See MonsterModel.Skins for why a monster needs more than one.
    /// </param>
    public static GamePicture Of(
        SkinnedMesh? mesh,
        Canvas canvas,
        float turn = 0f,
        float tilt = 0f,
        Vector3 ink = default,
        Mipmaps? skin = null,
        float zoom = 1f,
        Vector2 pan = default,
        IReadOnlyList<Mipmaps?>? skins = null)
        => Of(mesh, canvas, mesh?.Positions ?? [], mesh?.Normals ?? [], turn, tilt, ink, skin, zoom, pan, skins);

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
    /// <param name="skins">
    /// One texture per shape of the mesh, in its order, for a monster built out of parts. A
    /// null entry draws its shape in <paramref name="ink"/>; a shorter list leaves the rest to
    /// <paramref name="skin"/>. See MonsterModel.Skins for why a monster needs more than one.
    /// </param>
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
        Vector2 pan = default,
        IReadOnlyList<Mipmaps?>? skins = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);

        int size = canvas.Size;
        byte[] pixels = canvas.Pixels;

        // CLEARED HERE AND NOT WHERE THE TRIANGLES START, because every way out of this method
        // returns these pixels. A canvas coming back for its second monster would otherwise show
        // the first one wherever the second draws nothing - and the emptier the mesh, the more of
        // the previous monster is left standing.
        Array.Clear(pixels);

        Camera camera = Camera.Of(mesh, turn, tilt, zoom, pan);
        if (mesh is null || !camera.Ready)
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

        Matrix4x4 view = camera.View;
        float scale = size * camera.Scale;
        Vector2 centre = camera.Centre * size;

        float[] depth = canvas.Depth;
        Array.Fill(depth, float.MaxValue);

        // The light sits over the viewer's shoulder, which is the one placement that never leaves
        // a face black: anything pointing at the camera is lit.
        Vector3 lamp = Vector3.Normalize(new Vector3(-0.35f, -0.55f, -0.75f));

        // The skin is only usable if it decoded AND the mesh carries coordinates to look it up
        // with - see SkinnedMesh.Coordinated for what an uncoordinated mesh would paint.
        Mipmaps? usable = skin is not null && mesh.Coordinated ? skin : null;

        // ONE TEXTURE PER SHAPE WHERE THERE IS ONE. A monster is built of parts - body, cloak,
        // wings - and each part's coordinates address ITS OWN sheet, so painting all of them
        // from one texture puts the body's pixels on the wings. Reported from the live client
        // by Bahlak the Sky Seer, who came out black with red patches while the game draws him
        // in feathers. The palette holds the distinct textures with "none" at 0, and every
        // triangle carries the index of the one its shape wears.
        Mipmaps?[] palette = Palette(mesh, usable, skins);

        // EVERY VERTEX ONCE, not once per triangle it sits in. A closed mesh lists each vertex in
        // about six triangles, so transforming at the corners was six transforms for one.
        int vertices = positions.Length;
        int triangles = mesh.Indices.Length / 3;
        canvas.Fit(vertices, triangles);
        Vector3[] corners = canvas.Corners;
        Vector3[] facings = canvas.Facings;
        for (var point = 0; point < vertices; point++)
        {
            Vector3 place = Vector3.Transform(positions[point], view);
            corners[point] = new Vector3((place.X * scale) + centre.X, (place.Y * scale) + centre.Y, place.Z);
            facings[point] = Vector3.TransformNormal(normals[point], view);
        }

        // And every triangle's rows and skin level once, so a band can pass over the triangles
        // that do not reach it with one comparison each.
        int[] tops = canvas.Tops;
        int[] feet = canvas.Feet;
        float[] levels = canvas.Levels;
        int[] wears = canvas.Wears;
        int[] indices = mesh.Indices;
        Worn(mesh, palette, skins, usable, wears, triangles);
        for (var one = 0; one < triangles; one++)
        {
            Vector3 a = corners[indices[one * 3]];
            Vector3 b = corners[indices[(one * 3) + 1]];
            Vector3 c = corners[indices[(one * 3) + 2]];
            float area = Cross(a, b, c);
            if (!(MathF.Abs(area) >= 1e-6f))
            {
                tops[one] = int.MaxValue;
                feet[one] = int.MinValue;
                continue;
            }

            tops[one] = Math.Max(0, (int)MathF.Floor(Min3(a.Y, b.Y, c.Y)));
            feet[one] = Math.Min(size - 1, (int)MathF.Ceiling(Max3(a.Y, b.Y, c.Y)));

            // AGAINST THE TEXTURE THIS TRIANGLE WEARS, because the level is worked out from how
            // many texels a pixel steps across and the parts of a monster are not all painted
            // at the same resolution: a 512 square cloak read at a 2048 square body's level is
            // the grain this calculation exists to avoid.
            Mipmaps? worn = palette[wears[one]];
            levels[one] = worn is null
                ? 0f
                : Level(
                    a, b, c,
                    mesh.Coordinates[indices[one * 3]],
                    mesh.Coordinates[indices[(one * 3) + 1]],
                    mesh.Coordinates[indices[(one * 3) + 2]],
                    area, worn);
        }

        // IN BANDS OF ROWS, EACH ON ITS OWN THREAD. A pixel belongs to one band and the triangles
        // are walked in the mesh's order within it, so the picture is the sequential one to the
        // byte however many threads share it - the depth test never sees two threads at once.
        // More bands than threads, so a band the model does not reach costs nothing much and the
        // ones through its middle are shared out.
        var drawing = new Drawing(canvas, mesh, triangles, lamp, ink, palette);
        const int height = 8;
        int bands = (size + height - 1) / height;
        if (canvas.Threads == 1)
        {
            for (var band = 0; band < bands; band++)
            {
                drawing.Band(band * height, Math.Min(size, (band + 1) * height));
            }
        }
        else
        {
            Parallel.For(
                0, bands,
                new ParallelOptions { MaxDegreeOfParallelism = canvas.Threads },
                band => drawing.Band(band * height, Math.Min(size, (band + 1) * height)));
        }

        return new GamePicture(size, size, pixels);
    }

    /// <summary>
    /// The distinct textures a drawing may read, with "none" at nought.
    /// </summary>
    /// <remarks>
    /// A PALETTE RATHER THAN A TEXTURE PER TRIANGLE, because several shapes usually share one
    /// material - a monster with nine shapes and two sheets makes two entries here and nine
    /// numbers in <see cref="Worn"/>, and the hot loop reads a reference out of an array of
    /// three instead of chasing one per triangle.
    ///
    /// INDEX 0 IS ALWAYS "NO TEXTURE", so a shape nothing was found for draws in ink exactly as
    /// a monster with no skin at all does - which is the honest answer and the one that does not
    /// put the body's pixels on the wings.
    /// </remarks>
    private static Mipmaps?[] Palette(
        SkinnedMesh mesh, Mipmaps? usable, IReadOnlyList<Mipmaps?>? skins)
    {
        if (!mesh.Coordinated)
        {
            return [null];
        }

        var found = new List<Mipmaps?> { null };
        if (skins is { Count: > 0 })
        {
            foreach (Mipmaps? one in skins)
            {
                if (one is not null && !found.Contains(one))
                {
                    found.Add(one);
                }
            }
        }

        if (usable is not null && !found.Contains(usable))
        {
            found.Add(usable);
        }

        return [.. found];
    }

    /// <summary>Which palette entry every triangle reads, from the shape it belongs to.</summary>
    /// <remarks>
    /// THE SHAPES ARE RANGES OF INDICES and the renderer works in triangles, so this is the one
    /// place the two are put next to each other. A triangle outside every shape's range - which
    /// a mesh whose shape table did not read has for all of them - falls back to the single
    /// skin, which is exactly what the drawing did before it knew about shapes at all.
    /// </remarks>
    private static void Worn(
        SkinnedMesh mesh,
        Mipmaps?[] palette,
        IReadOnlyList<Mipmaps?>? skins,
        Mipmaps? usable,
        int[] wears,
        int triangles)
    {
        int fallback = Array.IndexOf(palette, usable);
        Array.Fill(wears, fallback < 0 ? 0 : fallback, 0, triangles);

        if (skins is not { Count: > 0 } || !mesh.Coordinated)
        {
            return;
        }

        for (var shape = 0; shape < mesh.Shapes.Count && shape < skins.Count; shape++)
        {
            MeshShape part = mesh.Shapes[shape];
            int at = Array.IndexOf(palette, skins[shape]);
            if (at < 0)
            {
                continue;
            }

            int from = Math.Clamp(part.From / 3, 0, triangles);
            int upto = Math.Clamp((part.From + part.Count) / 3, from, triangles);
            for (int one = from; one < upto; one++)
            {
                wears[one] = at;
            }
        }
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
    /// One frame's drawing: everything a band needs, shared by all of them.
    /// </summary>
    /// <remarks>
    /// A DEPTH BUFFER AND NOT A SORT. Painting back to front is the cheaper trick and it is wrong
    /// on exactly the meshes that matter here: a monster's own arm crossing its chest has no
    /// ordering that draws both correctly, because the triangles interleave. Per pixel is the only
    /// answer that does not depend on the order the file happens to list them in.
    ///
    /// NO BACK-FACE CULLING. It would halve the work, and it needs a winding order that the format
    /// has not been shown to keep - cull the wrong way and the model turns inside out. The depth
    /// buffer already hides the far side, so this costs time rather than correctness.
    ///
    /// EDGE FUNCTIONS, NOT CROSS PRODUCTS PER PIXEL. Whether a pixel is inside a triangle is the
    /// sign of three linear functions of its position, so each is one multiply-add per pixel from
    /// the row's start rather than a cross product and a division; the three barycentric weights
    /// fall out of the same numbers by one reciprocal per triangle. WHICH MEASURED THE SAME on one
    /// thread as the cross products did, on a rig-sized mesh filling the frame - and that is worth
    /// knowing: the time is in the shaded pixels, texturing and lighting, not in deciding which
    /// pixels those are. The bands are what pay, by the core count - see the class remarks.
    /// </remarks>
    private sealed class Drawing
    {
        private readonly byte[] _pixels;
        private readonly float[] _depth;
        private readonly int _size;
        private readonly Vector3[] _corners;
        private readonly Vector3[] _facings;
        private readonly int[] _tops;
        private readonly int[] _feet;
        private readonly float[] _levels;
        private readonly int[] _indices;
        private readonly Vector2[] _coordinates;
        private readonly int _triangles;
        private readonly Vector3 _lamp;
        private readonly Vector3 _ink;
        private readonly Mipmaps?[] _palette;
        private readonly int[] _wears;

        public Drawing(
            Canvas canvas, SkinnedMesh mesh, int triangles, Vector3 lamp, Vector3 ink, Mipmaps?[] palette)
        {
            _pixels = canvas.Pixels;
            _depth = canvas.Depth;
            _size = canvas.Size;
            _corners = canvas.Corners;
            _facings = canvas.Facings;
            _tops = canvas.Tops;
            _feet = canvas.Feet;
            _levels = canvas.Levels;
            _indices = mesh.Indices;
            _coordinates = mesh.Coordinates;
            _triangles = triangles;
            _lamp = lamp;
            _ink = ink;
            _palette = palette;
            _wears = canvas.Wears;
        }

        /// <summary>Draws every triangle's part that falls in the rows from <paramref name="top"/> up to <paramref name="end"/>.</summary>
        public void Band(int top, int end)
        {
            for (var one = 0; one < _triangles; one++)
            {
                if (_feet[one] < top || _tops[one] >= end)
                {
                    continue;
                }

                Rasterise(one, Math.Max(_tops[one], top), Math.Min(_feet[one], end - 1));
            }
        }

        private void Rasterise(int one, int top, int foot)
        {
            int i0 = _indices[one * 3];
            int i1 = _indices[(one * 3) + 1];
            int i2 = _indices[(one * 3) + 2];
            Vector3 c0 = _corners[i0];
            Vector3 c1 = _corners[i1];
            Vector3 c2 = _corners[i2];
            Vector3 f0 = _facings[i0];
            Vector3 f1 = _facings[i1];
            Vector3 f2 = _facings[i2];

            // The three edge functions, turned so that inside is where all three are positive
            // whichever way the triangle winds: w0 grows away from the edge c1-c2, w1 from c2-c0,
            // and w2 is what is left of the area.
            float area = Cross(c0, c1, c2);
            float sign = area < 0f ? -1f : 1f;
            float total = area * sign;
            float inv = 1f / total;
            float a0 = -(c2.Y - c1.Y) * sign;
            float b0 = (c2.X - c1.X) * sign;
            float k0 = (((c2.Y - c1.Y) * c1.X) - ((c2.X - c1.X) * c1.Y)) * sign;
            float a1 = -(c0.Y - c2.Y) * sign;
            float b1 = (c0.X - c2.X) * sign;
            float k1 = (((c0.Y - c2.Y) * c2.X) - ((c0.X - c2.X) * c2.Y)) * sign;

            int least = Math.Max(0, (int)MathF.Floor(Min3(c0.X, c1.X, c2.X)));
            int most = Math.Min(_size - 1, (int)MathF.Ceiling(Max3(c0.X, c1.X, c2.X)));
            float level = _levels[one];

            // THE TEXTURE THIS TRIANGLE'S SHAPE WEARS, not the model's. See Palette.
            Mipmaps? skin = _palette[_wears[one]];
            bool skinned = skin is not null;
            Vector2 s0 = default;
            Vector2 s1 = default;
            Vector2 s2 = default;
            if (skinned)
            {
                s0 = _coordinates[i0];
                s1 = _coordinates[i1];
                s2 = _coordinates[i2];
            }

            for (int y = top; y <= foot; y++)
            {
                float py = y + 0.5f;
                float px = least + 0.5f;
                float row0 = (a0 * px) + (b0 * py) + k0;
                float row1 = (a1 * px) + (b1 * py) + k1;
                int at = (y * _size) + least;

                for (int x = least; x <= most; x++, at++)
                {
                    // From the row's start each time rather than stepped, so a row two thousand
                    // pixels long does not drift by its accumulated rounding.
                    float along = x - least;
                    float w0 = row0 + (a0 * along);
                    float w1 = row1 + (a1 * along);
                    float w2 = total - w0 - w1;
                    if (w0 < 0f || w1 < 0f || w2 < 0f)
                    {
                        continue;
                    }

                    float first = w0 * inv;
                    float second = w1 * inv;
                    float third = w2 * inv;
                    float away = (first * c0.Z) + (second * c1.Z) + (third * c2.Z);
                    if (away >= _depth[at])
                    {
                        continue;
                    }

                    _depth[at] = away;

                    Vector3 normal = (first * f0) + (second * f1) + (third * f2);
                    if (normal.LengthSquared() > 1e-6f)
                    {
                        normal = Vector3.Normalize(normal);
                    }

                    // TWO-SIDED, because the mesh's winding is not established and a single-sided
                    // light leaves whole limbs black where the triangles happen to face away.
                    float lit = MathF.Abs(Vector3.Dot(normal, _lamp));
                    float shade = 0.22f + (0.78f * lit);

                    Vector3 colour = _ink;
                    if (skinned)
                    {
                        // AFFINE INTERPOLATION IS EXACT HERE. The projection is orthographic, so a
                        // coordinate across the triangle really is linear in screen space - the
                        // perspective correction a game renderer needs would be dividing by a w
                        // that is always one.
                        Vector2 spot = (first * s0) + (second * s1) + (third * s2);
                        colour = Sample(skin!, spot, level);
                    }

                    _pixels[at * 4] = Byte(colour.X * shade);
                    _pixels[(at * 4) + 1] = Byte(colour.Y * shade);
                    _pixels[(at * 4) + 2] = Byte(colour.Z * shade);
                    _pixels[(at * 4) + 3] = 255;
                }
            }
        }
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
        Vector3 c0, Vector3 c1, Vector3 c2, Vector2 s0, Vector2 s1, Vector2 s2, float area, Mipmaps skin)
    {
        // The two edges out of the first corner, on the screen and on the skin.
        float e1x = c1.X - c0.X;
        float e1y = c1.Y - c0.Y;
        float e2x = c2.X - c0.X;
        float e2y = c2.Y - c0.Y;
        Vector2 d1 = s1 - s0;
        Vector2 d2 = s2 - s0;

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
