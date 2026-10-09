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
/// plays at thirty frames a second. Each band walks the triangles that reach it (see Binned), in the mesh's order, and draws
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
/// AND IT IS A LEFT-HANDED FRAME, which the picture has to draw as one or it shows the game's
/// mirror image. Found when a room laid from an area's own tiles would not turn to match the game
/// however it was turned, because it was the game's room in a mirror. Two measurements say so,
/// each without the other:
///
///     THE WORLD. The game's map draws grid x up and to the right and grid y up and to the left
///     (MapRadar.Project, which lines up with the map the game draws), with up as minus z. From
///     above that is x to y anticlockwise, so x × y points up, which is -z: left-handed.
///     THE FILES. BasicSkeleton's rig has its toes at -y (ankle y +8.6, toe tip -12.7), its back
///     at +y (aux_back_attachment +13.3), its head along -z - and every L_ bone at +x. Right-
///     handed, a figure's left is up × forward = -x. Blackguard's rig agrees bone for bone, its
///     back-left cloth at +x as well.
///
/// So the view turns x over once, before anything else turns it. There is no back-face culling to
/// turn inside out with it - see Drawing - and the floor is placed by the same camera.
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
    /// The texture alpha below which a cut-out material's pixel is dropped. See <see cref="MaterialBlend.Cutout"/>.
    /// </summary>
    /// <remarks>
    /// HALFWAY, BECAUSE NOTHING SAYS OTHERWISE. The game's reference value lives in its shader, which
    /// no reference reads; half is what a texture authored for a test splits on, its edges being
    /// the only pixels anywhere near it.
    /// </remarks>
    public const float CutoutAlpha = 0.5f;

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

                // THE GAME'S FRAME IS LEFT-HANDED, and every rotation after this is a proper one,
                // so without this turn-over each picture is the game's mirror image - see the
                // remarks on the class. X rather than Y because it leaves the side the model is
                // seen from, and the way a drag turns it, as they were.
                * Matrix4x4.CreateScale(-1f, 1f, 1f)
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

        /// <summary>
        /// Whether a drawing into this canvas keeps its still part, so that the part running with the
        /// clock can be drawn again alone - see <see cref="Again"/>.
        /// </summary>
        /// <remarks>
        /// ASKED FOR, NOT ASSUMED: keeping costs a second picture, depth and winner buffer, and a copy
        /// of each per drawing, which a canvas that is only ever drawn whole would pay for nothing.
        /// </remarks>
        public bool Keeps { get; init; }

        /// <summary>What the last drawing kept to draw its clock's part again, or null where it kept nothing.</summary>
        internal Drawn? Last { get; set; }

        /// <summary>
        /// Which triangle last wrote each pixel's depth, while a drawing keeps its still part - so a tie
        /// in depth goes to whichever comes first in the mesh, however the passes are split.
        /// </summary>
        internal int[] Winners { get; private set; } = [];

        /// <summary>The still part's pixels, depth and winners, as the drawing that kept them left them.</summary>
        internal byte[] KeptPixels { get; private set; } = [];

        /// <inheritdoc cref="KeptPixels"/>
        internal float[] KeptDepth { get; private set; } = [];

        /// <inheritdoc cref="KeptPixels"/>
        internal int[] KeptWinners { get; private set; } = [];

        /// <summary>The triangles whose program reads the clock, in the mesh's order - the first <see cref="Drawn.Clocked"/> of them.</summary>
        internal int[] Clocked { get; private set; } = [];

        /// <summary>Which pass each triangle is drawn in - see <see cref="Binned"/>: nought for none.</summary>
        internal byte[] Kinds { get; private set; } = [];

        /// <summary>Where each pass's bin for each band starts in <see cref="BinEntries"/>, the pass's bands one after another.</summary>
        internal int[] BinStarts { get; private set; } = [];

        /// <summary>Scratch for filling the bins.</summary>
        internal int[] BinCursors { get; private set; } = [];

        /// <summary>The triangles in every bin, each bin in the mesh's order.</summary>
        internal int[] BinEntries { get; private set; } = [];

        /// <summary>Room for sorting this many triangles into this many bands - and for listing the clock's, where asked.</summary>
        internal void Binning(int triangles, int bands, bool clock)
        {
            if (Kinds.Length < triangles)
            {
                Kinds = new byte[triangles];
            }

            if (clock && Clocked.Length < triangles)
            {
                Clocked = new int[triangles];
            }

            if (BinStarts.Length != (3 * bands) + 1)
            {
                BinStarts = new int[(3 * bands) + 1];
                BinCursors = new int[3 * bands];
            }
        }

        /// <summary>Room for this many bin entries.</summary>
        internal void Entries(int count)
        {
            if (BinEntries.Length < count)
            {
                BinEntries = new int[Math.Max(count, BinEntries.Length * 2)];
            }
        }

        /// <summary>Room for the kept buffers, with every pixel's winner set to none.</summary>
        internal void Keeping()
        {
            int pixels = Size * Size;
            if (Winners.Length != pixels)
            {
                Winners = new int[pixels];
                KeptPixels = new byte[pixels * 4];
                KeptDepth = new float[pixels];
                KeptWinners = new int[pixels];
            }

            Array.Fill(Winners, int.MaxValue);
        }

        /// <summary>
        /// The game's clock for the next drawing, in seconds - what a shade program's <c>Time</c> reads. See ShadeProgram.Clock.
        /// </summary>
        /// <remarks>On the canvas because it is the caller's, like the frame an animation is drawn at: set it, then draw.</remarks>
        public float Time { get; set; }

        /// <summary>
        /// The area's dust colour for the next drawing - what a shade program's <c>DustColor</c> reads. See ShadeProgram.Dust.
        /// </summary>
        /// <remarks>
        /// The caller's, like the clock: the area's .env says it (EnvironmentSettings.Dust), and a
        /// picture with no area to ask gets the assumed one, which the light panel names as assumed.
        /// </remarks>
        public Vector3 Dust { get; set; } = EnvironmentSettings.AssumedDust;

        /// <summary>
        /// A pixel the next whole drawing records every fragment of, or null - see <see cref="PixelProbe"/>.
        /// </summary>
        /// <remarks>Cleared and filled by each whole drawing while it is set; <see cref="Again"/> leaves it alone.</remarks>
        public PixelProbe? Probe { get; set; }

        /// <summary>
        /// Where the next whole drawing counts how its translucent shapes came out, or null - see <see cref="LayerTally"/>.
        /// </summary>
        /// <remarks>
        /// NULL IS THE USUAL, and then the drawing does not so much as look at it per pixel: the counting
        /// is a diagnosis asked for, and a picture nobody is diagnosing pays nothing for it.
        /// </remarks>
        public LayerTally? Tally { get; set; }

        /// <summary>
        /// How the next drawing is lit the game's way, or null for the picture's own lamp and ambient - see <see cref="SceneLight"/>.
        /// </summary>
        /// <remarks>
        /// ON THE CANVAS LIKE THE CLOCK: set it, then draw. Null is the usual and changes nothing - every
        /// picture but a lit room is drawn exactly as before.
        /// </remarks>
        public SceneLight? Light { get; set; }

        /// <summary>The last sun's shadow map and what it was drawn for, kept while neither changes - see <see cref="ShadowMap"/>.</summary>
        internal ShadowMap? Shadow { get; set; }

        /// <inheritdoc cref="Shadow"/>
        internal (SkinnedMesh Mesh, Vector3 Direction, int Side)? ShadowFor { get; set; }

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

        /// <summary>How each triangle is put over what is behind it. See <see cref="MaterialBlend"/>.</summary>
        internal MaterialBlend[] Blends { get; private set; } = [];

        /// <summary>Which shape each triangle belongs to, where it is translucent - see <see cref="Stamps"/>.</summary>
        internal int[] Owners { get; private set; } = [];

        /// <summary>
        /// Which translucent shape last blended into each pixel, so no shape blends into one twice.
        /// </summary>
        /// <remarks>
        /// A PIXEL ON THE EDGE TWO TRIANGLES SHARE IS INSIDE BOTH - the inside test takes the edge - and
        /// a solid surface drawn twice there is the same surface, while a translucent one is twice as
        /// opaque: a seam of doubled colour down every diagonal, measured at 191 where 128 was asked
        /// for. A fill rule would settle it on paper and lean on floating point agreeing exactly
        /// across two triangles; remembering who blended last does not. Allocated the first time a
        /// translucent drawing needs it.
        /// </remarks>
        internal int[] Stamps { get; private set; } = [];

        /// <summary>Which shade program each triangle runs, as an index into the drawing's list, or -1 for none.</summary>
        internal int[] Shades { get; private set; } = [];

        /// <summary>The level each of a shaded triangle's texture reads takes, a fixed stride per triangle.</summary>
        internal float[] ShadeLevels { get; private set; } = [];

        /// <summary>Where each vertex is in the model's own space, for a shade program that reads positions.</summary>
        internal Vector3[] Places { get; private set; } = [];

        /// <summary>Which way each vertex faces in the model's own space, for one that reads normals.</summary>
        internal Vector3[] Turns { get; private set; } = [];

        /// <summary>Room for every shaded triangle's levels.</summary>
        internal void Levelled(int levels)
        {
            if (ShadeLevels.Length < levels)
            {
                ShadeLevels = new float[levels];
            }
        }

        /// <summary>
        /// The model-space vertices as an array a band can read, for a drawing with shade programs.
        /// </summary>
        /// <remarks>
        /// THE MESH'S OWN ARRAY WHERE THAT IS WHAT WAS HANDED IN - a model standing still - and a copy
        /// only for a pose, which arrives as a span and must outlive the call for the bands' sake.
        /// Never the mesh's array as the copy's buffer: the next pose would be written into the mesh.
        /// </remarks>
        internal Vector3[] Placed(ReadOnlySpan<Vector3> positions, Vector3[] own)
        {
            if (positions == own)
            {
                return own;
            }

            if (Places.Length < positions.Length)
            {
                Places = new Vector3[positions.Length];
            }

            positions.CopyTo(Places);
            return Places;
        }

        /// <summary>The same for the normals. See <see cref="Placed"/>.</summary>
        internal Vector3[] Turned(ReadOnlySpan<Vector3> normals, Vector3[] own)
        {
            if (normals == own)
            {
                return own;
            }

            if (Turns.Length < normals.Length)
            {
                Turns = new Vector3[normals.Length];
            }

            normals.CopyTo(Turns);
            return Turns;
        }

        internal void Stamped()
        {
            if (Stamps.Length != Size * Size)
            {
                Stamps = new int[Size * Size];
            }

            Array.Fill(Stamps, -1);
        }

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
                Blends = new MaterialBlend[triangles];
                Owners = new int[triangles];
                Shades = new int[triangles];
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
        IReadOnlyList<Mipmaps?>? skins = null,
        IReadOnlyList<MaterialBlend>? blends = null,
        IReadOnlyList<ShadeProgram?>? shades = null)
        => Of(mesh, new Canvas(size), turn, tilt, ink, skin, zoom, pan, skins, blends, shades);

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
        IReadOnlyList<Mipmaps?>? skins = null,
        IReadOnlyList<MaterialBlend>? blends = null,
        IReadOnlyList<ShadeProgram?>? shades = null)
        => Of(mesh, canvas, mesh?.Positions ?? [], mesh?.Normals ?? [], turn, tilt, ink, skin, zoom, pan, skins, blends, shades);

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
        IReadOnlyList<Mipmaps?>? skins = null,
        IReadOnlyList<MaterialBlend>? blends = null,
        IReadOnlyList<ShadeProgram?>? shades = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);

        int size = canvas.Size;
        byte[] pixels = canvas.Pixels;

        // CLEARED HERE AND NOT WHERE THE TRIANGLES START, because every way out of this method
        // returns these pixels. A canvas coming back for its second monster would otherwise show
        // the first one wherever the second draws nothing - and the emptier the mesh, the more of
        // the previous monster is left standing.
        Array.Clear(pixels);
        canvas.Last = null;

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
            ink = UsualInk;
        }

        Matrix4x4 view = camera.View;
        float scale = size * camera.Scale;
        Vector2 centre = camera.Centre * size;

        float[] depth = canvas.Depth;
        Array.Fill(depth, float.MaxValue);

        Vector3 lamp = Lamp;

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
        // about six triangles, so transforming at the corners was six transforms for one. In slices
        // on every core where there are enough to pay for it - see Sliced.
        int vertices = positions.Length;
        int triangles = mesh.Indices.Length / 3;
        canvas.Fit(vertices, triangles);
        Vector3[] corners = canvas.Corners;
        Vector3[] facings = canvas.Facings;
        Vector3[] places = canvas.Placed(positions, mesh.Positions);
        Vector3[] turns = canvas.Turned(normals, mesh.Normals);
        Sliced(canvas, vertices, (from, upto) =>
        {
            for (int point = from; point < upto; point++)
            {
                Vector3 place = Vector3.Transform(places[point], view);
                corners[point] = new Vector3((place.X * scale) + centre.X, (place.Y * scale) + centre.Y, place.Z);
                facings[point] = Vector3.TransformNormal(turns[point], view);
            }
        });

        int[] tops = canvas.Tops;
        int[] feet = canvas.Feet;
        float[] levels = canvas.Levels;
        int[] wears = canvas.Wears;
        int[] indices = mesh.Indices;
        Worn(mesh, palette, skins, usable, wears, triangles);
        bool translucent = Blended(mesh, blends, canvas.Blends, canvas.Owners, triangles);
        if (translucent)
        {
            canvas.Stamped();
        }

        // AND WHICH TRIANGLES RUN A SHADE PROGRAM - see ShadeProgram. Most models have none, and then
        // nothing below changes by so much as a branch per pixel.
        ShadeProgram[] programs = Programmed(mesh, shades, canvas.Shades, triangles);
        int stride = 0;
        foreach (ShadeProgram one in programs)
        {
            stride = Math.Max(stride, one.Samples);
        }

        bool shaded = programs.Length > 0;
        if (shaded)
        {
            canvas.Levelled(triangles * stride);
        }

        // And every triangle's rows, skin level and read levels once, for every band to read - in
        // slices like the vertices, each slice with its own registers for the programs' levels.
        int[] shadeOf = canvas.Shades;
        float[] shadeLevels = canvas.ShadeLevels;
        float time = canvas.Time;
        Vector3 dust = canvas.Dust;
        Sliced(canvas, triangles, (from, upto) =>
        {
            Span<Vector4> scratch = shaded ? stackalloc Vector4[ShadeProgram.MostRegisters] : default;
            Span<Vector2> spots = shaded ? stackalloc Vector2[Math.Max(1, stride) * 3] : default;
            Span<Vector2> cornerSpots = shaded ? stackalloc Vector2[3] : default;
            Span<Vector3> cornerPlaces = shaded ? stackalloc Vector3[3] : default;
            Span<Vector3> cornerTurns = shaded ? stackalloc Vector3[3] : default;
            Span<Vector4> cornerColours = shaded ? stackalloc Vector4[3] : default;
            ShadeProgram? preset = null;
            for (int one = from; one < upto; one++)
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

                // A SHADED TRIANGLE'S READS EACH TAKE A LEVEL OF THEIR OWN: a rock texture tiled twelve
                // times over the shape steps across twelve times the texels its mask does.
                if (shaded && shadeOf[one] >= 0)
                {
                    ShadeProgram program = programs[shadeOf[one]];
                    if (!ReferenceEquals(program, preset))
                    {
                        program.Preset(scratch, time, default, dust);
                        preset = program;
                    }

                    Graded(
                        mesh, program, one, places, turns, a, b, c, area,
                        scratch, spots, cornerSpots, cornerPlaces, cornerTurns, cornerColours, shadeLevels.AsSpan(one * stride, program.Samples));
                }
            }
        });

        // THE CLOCK'S TRIANGLES APART where the canvas keeps the still part - see Again. A triangle
        // runs its program only where it is solid or cut out, so only those can be the clock's.
        bool[] ticking = canvas.Keeps && Array.Exists(programs, one => one.UsesTime)
            ? Array.ConvertAll(programs, one => one.UsesTime)
            : [];
        int clocked = Binned(canvas, triangles, ticking);
        if (clocked > 0)
        {
            canvas.Keeping();
        }

        // A POINT'S DEPTH IS THE VIEW'S THIRD COLUMN, the way into the picture - see ShadeProgram.Eye.
        var eye = new Vector4(view.M13, view.M23, view.M33, view.M43);
        SceneLight? scene = canvas.Light;
        ShadowMap? shadow = scene is { SunShadows: true } && scene.SunColour != Vector3.Zero
            ? Shadowed(canvas, mesh, places, positions == mesh.Positions, triangles, scene.SunDirection, scene.ShadowSide, palette)
            : null;
        var drawn = new Drawn(mesh, triangles, lamp, ink, palette, translucent, programs, stride, places, turns, ticking, clocked, eye)
        {
            Scene = scene,
            Shadow = shadow,
        };
        PixelProbe? probe = canvas.Probe is { } asked && asked.X >= 0 && asked.X < size && asked.Y >= 0 && asked.Y < size ? asked : null;
        probe?.Clear();
        LayerTally? tally = canvas.Tally;
        tally?.Reset(mesh.Shapes.Count);
        InBands(canvas, new Drawing(canvas, drawn, probe), again: false, tally);
        if (probe is not null)
        {
            probe.Final = depth[(probe.Y * size) + probe.X];
            probe.Drawn = true;
        }
        if (clocked > 0)
        {
            canvas.Last = drawn;
        }

        return new GamePicture(size, size, pixels);
    }

    /// <summary>
    /// Draws the canvas's last picture again at the canvas's <see cref="Canvas.Time"/>, drawing only
    /// the triangles whose program reads the clock - the picture a whole drawing would make, to the byte.
    /// </summary>
    /// <param name="canvas">A canvas that <see cref="Canvas.Keeps"/>, last drawn with a program that reads the clock.</param>
    /// <param name="picture">The picture, lent out as <see cref="Of(SkinnedMesh?, Canvas, float, float, Vector3, Mipmaps?, float, Vector2, IReadOnlyList{Mipmaps?}?, IReadOnlyList{MaterialBlend}?, IReadOnlyList{ShadeProgram?}?)"/> lends it.</param>
    /// <returns>False where the canvas kept nothing to draw again from - draw it whole instead.</returns>
    /// <remarks>
    /// WHY IT EXISTS. A tile runs with the clock through a few of its materials - breach_boss_01
    /// through two of a thousand shapes - and drawing it whole on every tick put the whole tile's
    /// cost on each one. Everything else in the picture holds still while only the time moves: the
    /// camera, the vertices, which texture and level each triangle reads.
    ///
    /// SO A KEEPING DRAWING IS SPLIT, and this is the second half of it. The whole drawing draws
    /// every solid triangle that does not read the clock first and keeps what that left - the
    /// pixels, the depth and which triangle won each pixel - then draws the clock's triangles and
    /// the translucent ones over it. Here the kept part is put back, the clock's triangles get their
    /// read levels again at the new time, and they and the translucent ones are drawn as before.
    ///
    /// THE SAME PICTURE, TIES INCLUDED. Drawn in one pass in the mesh's order, a pixel goes to the
    /// nearest triangle and, at equal depth, to the one that comes first - the depth test lets only
    /// a nearer one through. Split into passes, a clock triangle meets a still one that may come
    /// after it in the mesh, so a tie is settled by comparing the two triangles' places in the mesh,
    /// which is what the winner buffer is for: the passes come out exactly as the one pass would.
    /// </remarks>
    public static bool Again(Canvas canvas, out GamePicture picture)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        picture = default;
        if (canvas.Last is not { } drawn)
        {
            return false;
        }

        // THE CLOCK'S TRIANGLES' READ LEVELS, at the new time: a muddle moves the coordinates the
        // levels are worked out from. Every other triangle's are as the whole drawing left them. In
        // slices like the whole drawing's: on a tile whose clock covers half a million triangles they
        // were most of a tick on one thread.
        int[] indices = drawn.Mesh.Indices;
        Vector3[] corners = canvas.Corners;
        int[] shadeOf = canvas.Shades;
        int[] clocked = canvas.Clocked;
        float[] shadeLevels = canvas.ShadeLevels;
        float time = canvas.Time;
        Vector3 dust = canvas.Dust;
        Sliced(canvas, drawn.Clocked, (from, upto) =>
        {
            Span<Vector4> scratch = stackalloc Vector4[ShadeProgram.MostRegisters];
            Span<Vector2> spots = stackalloc Vector2[Math.Max(1, drawn.Stride) * 3];
            Span<Vector2> cornerSpots = stackalloc Vector2[3];
            Span<Vector3> cornerPlaces = stackalloc Vector3[3];
            Span<Vector3> cornerTurns = stackalloc Vector3[3];
            Span<Vector4> cornerColours = stackalloc Vector4[3];
            ShadeProgram? preset = null;
            for (int at = from; at < upto; at++)
            {
                int one = clocked[at];
                Vector3 a = corners[indices[one * 3]];
                Vector3 b = corners[indices[(one * 3) + 1]];
                Vector3 c = corners[indices[(one * 3) + 2]];
                float area = Cross(a, b, c);
                if (!(MathF.Abs(area) >= 1e-6f))
                {
                    continue;
                }

                ShadeProgram program = drawn.Programs[shadeOf[one]];
                if (!ReferenceEquals(program, preset))
                {
                    program.Preset(scratch, time, default, dust);
                    preset = program;
                }

                Graded(
                    drawn.Mesh, program, one, drawn.Places, drawn.Turns, a, b, c, area,
                    scratch, spots, cornerSpots, cornerPlaces, cornerTurns, cornerColours,
                    shadeLevels.AsSpan(one * drawn.Stride, program.Samples));
            }
        });

        if (drawn.Translucent)
        {
            canvas.Stamped();
        }

        InBands(canvas, new Drawing(canvas, drawn, probe: null), again: true, tally: null);
        picture = new GamePicture(canvas.Size, canvas.Size, canvas.Pixels);
        return true;
    }

    /// <summary>
    /// Runs a drawing over the canvas in bands of rows, each on its own thread - the whole drawing, or
    /// the clock's part of it again.
    /// </summary>
    /// <remarks>
    /// A pixel belongs to one band and the triangles are walked in the mesh's order within it, so the
    /// picture is the sequential one to the byte however many threads share it - the depth test never
    /// sees two threads at once. More bands than threads, so a band the model does not reach costs
    /// nothing much and the ones through its middle are shared out.
    /// </remarks>
    private static void InBands(Canvas canvas, Drawing drawing, bool again, LayerTally? tally)
    {
        int size = canvas.Size;
        int bands = Bands(size);
        if (canvas.Threads == 1)
        {
            int[]? counts = tally is null ? null : new int[tally.Shapes * LayerTally.Slots];
            for (var band = 0; band < bands; band++)
            {
                drawing.Band(band, band * BandRows, Math.Min(size, (band + 1) * BandRows), again, counts);
            }

            if (counts is not null)
            {
                tally!.Add(counts);
            }
        }
        else if (tally is null)
        {
            Parallel.For(
                0, bands,
                new ParallelOptions { MaxDegreeOfParallelism = canvas.Threads },
                band => drawing.Band(band, band * BandRows, Math.Min(size, (band + 1) * BandRows), again, counts: null));
        }
        else
        {
            // COUNTED PER THREAD AND ADDED ONCE, so the bands never contend for one array.
            int slots = tally.Shapes * LayerTally.Slots;
            Parallel.For(
                0, bands,
                new ParallelOptions { MaxDegreeOfParallelism = canvas.Threads },
                () => new int[slots],
                (band, _, counts) =>
                {
                    drawing.Band(band, band * BandRows, Math.Min(size, (band + 1) * BandRows), again, counts);
                    return counts;
                },
                tally.Add);
        }
    }

    /// <summary>One shaded triangle's read levels, its program already preset at the canvas's time.</summary>
    private static void Graded(
        SkinnedMesh mesh, ShadeProgram program, int one, ReadOnlySpan<Vector3> positions, ReadOnlySpan<Vector3> normals,
        Vector3 a, Vector3 b, Vector3 c, float area, Span<Vector4> scratch, Span<Vector2> spots,
        Span<Vector2> cornerSpots, Span<Vector3> cornerPlaces, Span<Vector3> cornerTurns, Span<Vector4> cornerColours, Span<float> levels)
    {
        int[] indices = mesh.Indices;
        for (var corner = 0; corner < 3; corner++)
        {
            int vertex = indices[(one * 3) + corner];
            cornerSpots[corner] = mesh.Coordinates[vertex];
            cornerPlaces[corner] = positions[vertex];
            cornerTurns[corner] = normals[vertex];
            cornerColours[corner] = program.UsesVertexColour ? VertexColourOf(mesh, vertex) : default;
        }

        program.Levels(scratch, cornerSpots, cornerPlaces, cornerTurns, a, b, c, area, spots, levels, cornerColours);
    }

    /// <summary>What one drawing was drawn with, kept where its clock's part may be drawn again - see <see cref="Again"/>.</summary>
    /// <param name="Mesh">The mesh.</param>
    /// <param name="Triangles">How many triangles it has.</param>
    /// <param name="Lamp">The light.</param>
    /// <param name="Ink">The colour where there is no skin.</param>
    /// <param name="Palette">The textures, with none at nought.</param>
    /// <param name="Translucent">Whether any triangle is drawn in the second pass.</param>
    /// <param name="Programs">The distinct shade programs.</param>
    /// <param name="Stride">The read levels each shaded triangle has room for.</param>
    /// <param name="Places">The model-space vertices a program reads.</param>
    /// <param name="Turns">The model-space normals a program reads.</param>
    /// <param name="Ticking">Which programs read the clock, by their place in <paramref name="Programs"/> - empty where the drawing keeps nothing.</param>
    /// <param name="Clocked">How many of <see cref="Canvas.Clocked"/> are the clock's - nought where the drawing keeps nothing.</param>
    /// <param name="Eye">The way into the picture in the model's space and the origin's depth - see ShadeProgram.Eye.</param>
    internal sealed record Drawn(
        SkinnedMesh Mesh, int Triangles, Vector3 Lamp, Vector3 Ink, Mipmaps?[] Palette, bool Translucent,
        ShadeProgram[] Programs, int Stride, Vector3[] Places, Vector3[] Turns, bool[] Ticking, int Clocked, Vector4 Eye)
    {
        /// <summary>The game's light the drawing is lit by, or null for the picture's own - see <see cref="Canvas.Light"/>.</summary>
        public SceneLight? Scene { get; init; }

        /// <summary>The sun's shadow map, where the scene's sun casts shadows.</summary>
        public ShadowMap? Shadow { get; init; }
    }

    /// <summary>
    /// The sun's shadow map for this mesh, the canvas's own where it was drawn for the same mesh and sun.
    /// </summary>
    /// <remarks>
    /// KEPT ONLY FOR A MESH STANDING STILL: a pose arrives in the canvas's one buffer, the same array
    /// every frame with new numbers in it, so a map keyed on it could not tell one pose from the next.
    /// </remarks>
    private static ShadowMap? Shadowed(
        Canvas canvas, SkinnedMesh mesh, Vector3[] places, bool still, int triangles, Vector3 direction, int side, Mipmaps?[] palette)
    {
        if (still && canvas.ShadowFor is { } was && ReferenceEquals(was.Mesh, mesh) && was.Direction == direction && was.Side == side
            && canvas.Shadow is not null)
        {
            return canvas.Shadow;
        }

        MaterialBlend[] blends = canvas.Blends;
        int[] wears = canvas.Wears;
        Vector2[] coordinates = mesh.Coordinates;
        bool coordinated = mesh.Coordinated;
        ShadowMap? map = ShadowMap.Build(
            places, mesh.Indices, triangles, direction,
            one => !Translucent(blends[one]),
            one => blends[one] == MaterialBlend.Cutout && coordinated && palette[wears[one]] is { } skin ? (skin, coordinates) : null,
            canvas.Threads,
            side);
        canvas.Shadow = map;
        canvas.ShadowFor = still ? (mesh, direction, side) : null;
        return map;
    }

    /// <summary>Whether a triangle is drawn in the second pass - mixed or added, not solid or cut out.</summary>
    private static bool Translucent(MaterialBlend blend) => blend is MaterialBlend.Alpha or MaterialBlend.Additive;

    /// <summary>The rows in one band - see <see cref="InBands"/>.</summary>
    private const int BandRows = 8;

    /// <summary>How much of a colour a face turned from the lamp keeps: the picture's ambient, on the sRGB value.</summary>
    /// <remarks>Public, with <see cref="Lamp"/> and <see cref="UsualInk"/>, so the graphics card's drawing lights with the same numbers.</remarks>
    public const float Ambient = 0.22f;

    /// <summary>
    /// The picture's own light, in view space - over the viewer's shoulder, the one placement that never leaves a face black: anything pointing at the camera is lit.
    /// </summary>
    public static readonly Vector3 Lamp = Vector3.Normalize(new Vector3(-0.35f, -0.55f, -0.75f));

    /// <summary>The colour a shape is drawn in where it has no texture and the caller names none.</summary>
    public static readonly Vector3 UsualInk = new(0.78f, 0.75f, 0.70f);

    /// <summary>
    /// The ambient as light - the uniform environment the game's specular light is worked out under; see GlossLight.
    /// </summary>
    private static readonly float AmbientLight = MathF.Pow((Ambient + 0.055f) / 1.055f, 2.4f);

    /// <summary>The lamp as light: the rest of a face turned full on to it.</summary>
    private static readonly float LampLight = 1f - AmbientLight;

    /// <summary>
    /// The way to the eye: nearer is lower depth, and the picture is orthographic, so every pixel looks along the same line.
    /// </summary>
    private static readonly Vector3 ToEye = new(0f, 0f, -1f);

    /// <summary>How many bands a picture this size is drawn in.</summary>
    private static int Bands(int size) => (size + BandRows - 1) / BandRows;

    /// <summary>
    /// Runs <paramref name="body"/> over nought to <paramref name="count"/> in slices, on as many threads
    /// as the canvas allows - where there are enough to pay for the hand-off, and in one go where not.
    /// </summary>
    /// <remarks>
    /// THE SAME NUMBERS EITHER WAY: what is worked out per vertex or per triangle depends on that one
    /// alone. Measured on a million-triangle mesh, the vertices and the triangles' rows and levels took
    /// a quarter of a drawing on one thread while the bands were on four.
    /// </remarks>
    private static void Sliced(Canvas canvas, int count, Action<int, int> body)
    {
        const int least = 16384;
        if (canvas.Threads == 1 || count < least * 2)
        {
            body(0, count);
            return;
        }

        int slices = Math.Min(canvas.Threads * 4, (count + least - 1) / least);
        int each = (count + slices - 1) / slices;
        Parallel.For(
            0, slices,
            new ParallelOptions { MaxDegreeOfParallelism = canvas.Threads },
            slice => body(slice * each, Math.Min(count, (slice + 1) * each)));
    }

    /// <summary>
    /// Sorts the triangles into the bands they reach, pass by pass and each band in the mesh's order -
    /// and lists the clock's apart. Returns how many are the clock's.
    /// </summary>
    /// <remarks>
    /// WHY THERE ARE BINS. Every band used to walk every triangle and skip those that did not reach it:
    /// a million triangles in the 96 bands of a 768 square picture is 94 million skips a drawing, and
    /// on that mesh they were most of its time - the bands' share grew with the number of bands, not
    /// with the pixels drawn. Sorted once, a band walks only what reaches it, in the same order, so the
    /// picture is the same to the byte.
    ///
    /// THREE PASSES' WORTH: the solid and cut-out triangles drawn first, the clock's where the canvas
    /// keeps (see Again), and the translucent ones last. A triangle off the picture, too thin to have
    /// an area, or shadow-only is in none.
    /// </remarks>
    private static int Binned(Canvas canvas, int triangles, bool[] ticking)
    {
        int bands = Bands(canvas.Size);
        canvas.Binning(triangles, bands, ticking.Length > 0);
        byte[] kinds = canvas.Kinds;
        int[] tops = canvas.Tops;
        int[] feet = canvas.Feet;
        MaterialBlend[] blends = canvas.Blends;
        int[] shades = canvas.Shades;
        int[] starts = canvas.BinStarts;
        Array.Clear(starts, 0, (3 * bands) + 1);

        var clocked = 0;
        for (var one = 0; one < triangles; one++)
        {
            byte kind;
            MaterialBlend blend = blends[one];
            if (tops[one] > feet[one] || blend == MaterialBlend.ShadowOnly)
            {
                kind = 0;
            }
            else if (blend is MaterialBlend.Alpha or MaterialBlend.Additive)
            {
                kind = 3;
            }
            else if (ticking.Length > 0 && shades[one] >= 0 && ticking[shades[one]])
            {
                kind = 2;
                canvas.Clocked[clocked++] = one;
            }
            else
            {
                kind = 1;
            }

            kinds[one] = kind;
            if (kind == 0)
            {
                continue;
            }

            int row = (kind - 1) * bands;
            int last = feet[one] / BandRows;
            for (int band = tops[one] / BandRows; band <= last; band++)
            {
                starts[row + band + 1]++;
            }
        }

        for (var at = 1; at <= 3 * bands; at++)
        {
            starts[at] += starts[at - 1];
        }

        canvas.Entries(starts[3 * bands]);
        int[] cursors = canvas.BinCursors;
        int[] entries = canvas.BinEntries;
        Array.Copy(starts, cursors, 3 * bands);
        for (var one = 0; one < triangles; one++)
        {
            byte kind = kinds[one];
            if (kind == 0)
            {
                continue;
            }

            int row = (kind - 1) * bands;
            int last = feet[one] / BandRows;
            for (int band = tops[one] / BandRows; band <= last; band++)
            {
                entries[cursors[row + band]++] = one;
            }
        }

        return clocked;
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
    /// Which shade program every triangle runs, from the shape it belongs to, and the distinct programs.
    /// </summary>
    /// <remarks>
    /// THE SAME RANGES <see cref="Worn"/> walks. A program missing a texture is not drawn with - its
    /// shape keeps its plain skin - and a mesh with no coordinates runs none, having nothing for a
    /// program's reads to read at.
    /// </remarks>
    /// <summary>
    /// A vertex's colour as the program reads it: the file's four bytes, nought to one - or white
    /// where the vertex has no colour of its own.
    /// </summary>
    /// <remarks>
    /// WHITE BECAUSE THE GAME SHOWS IT: see SkinnedMesh.Colours - a rope and a book page with no
    /// colour stream, under BasicColour's texture-times-vertex-colour, are drawn with their textures.
    /// </remarks>
    private static Vector4 VertexColourOf(SkinnedMesh mesh, int vertex)
    {
        if (!mesh.ColourAt(vertex))
        {
            return Vector4.One;
        }

        byte[] colours = mesh.Colours;
        int at = vertex * 4;
        return new Vector4(colours[at], colours[at + 1], colours[at + 2], colours[at + 3]) / 255f;
    }

    private static ShadeProgram[] Programmed(
        SkinnedMesh mesh, IReadOnlyList<ShadeProgram?>? shades, int[] into, int triangles)
    {
        Array.Fill(into, -1, 0, triangles);
        if (shades is not { Count: > 0 } || !mesh.Coordinated)
        {
            return [];
        }

        var found = new List<ShadeProgram>();
        for (var shape = 0; shape < mesh.Shapes.Count && shape < shades.Count; shape++)
        {
            if (shades[shape] is not { Bound: true } program || program.Registers > ShadeProgram.MostRegisters)
            {
                continue;
            }

            int at = found.IndexOf(program);
            if (at < 0)
            {
                at = found.Count;
                found.Add(program);
            }

            MeshShape part = mesh.Shapes[shape];
            int from = Math.Clamp(part.From / 3, 0, triangles);
            int upto = Math.Clamp((part.From + part.Count) / 3, from, triangles);
            Array.Fill(into, at, from, upto - from);
        }

        return [.. found];
    }

    /// <summary>
    /// How every triangle blends, from the shape it belongs to - and whether any is not opaque.
    /// </summary>
    /// <remarks>
    /// THE SAME RANGES <see cref="Worn"/> walks, and the same fallback: a triangle outside every
    /// shape, or a list shorter than the shapes, is opaque - exactly what every drawing was before a
    /// material could say otherwise. The answer lets a drawing with nothing translucent skip the
    /// second pass altogether.
    /// </remarks>
    private static bool Blended(
        SkinnedMesh mesh, IReadOnlyList<MaterialBlend>? blends, MaterialBlend[] into, int[] owners, int triangles)
    {
        Array.Fill(into, MaterialBlend.Opaque, 0, triangles);
        if (blends is not { Count: > 0 })
        {
            return false;
        }

        var any = false;
        for (var shape = 0; shape < mesh.Shapes.Count && shape < blends.Count; shape++)
        {
            MaterialBlend blend = blends[shape];
            if (blend == MaterialBlend.Opaque)
            {
                continue;
            }

            MeshShape part = mesh.Shapes[shape];
            int from = Math.Clamp(part.From / 3, 0, triangles);
            int upto = Math.Clamp((part.From + part.Count) / 3, from, triangles);
            Array.Fill(into, blend, from, upto - from);

            // A CUT-OUT SHAPE IS SOLID WHERE IT IS DRAWN, so it needs neither an owner nor the
            // second pass - it rides in the first with the opaque ones. A shadow-only one is not drawn.
            if (blend is MaterialBlend.Cutout or MaterialBlend.ShadowOnly)
            {
                continue;
            }

            Array.Fill(owners, shape, from, upto - from);
            any |= upto > from;
        }

        return any;
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
        private readonly Vector3 _lamp;

        /// <summary>Halfway between the way to the eye and the way to the lamp, and the Fresnel there - one for the whole picture, see <see cref="Glossed"/>.</summary>
        private readonly Vector3 _half;
        private readonly float _fresnel;
        private readonly Vector3 _ink;
        private readonly Mipmaps?[] _palette;
        private readonly int[] _wears;
        private readonly MaterialBlend[] _blends;
        private readonly int[] _owners;
        private readonly int[] _stamps;
        private readonly bool _translucent;
        private readonly ShadeProgram[] _programs;
        private readonly float _time;
        private readonly Vector3 _dust;
        private readonly Vector4 _eye;
        private readonly int[] _shades;
        private readonly float[] _shadeLevels;
        private readonly int _stride;
        private readonly Vector3[] _places;
        private readonly Vector3[] _turns;
        private readonly SkinnedMesh _mesh;

        /// <summary>The winner buffer where the drawing keeps its still part, else null - see <see cref="Again"/>.</summary>
        private readonly int[]? _winners;
        private readonly byte[] _keptPixels;
        private readonly float[] _keptDepth;
        private readonly int[] _keptWinners;
        private readonly int[] _binStarts;
        private readonly int[] _binEntries;
        private readonly int _bands;

        /// <summary>The pixel whose fragments are recorded, or null - see <see cref="PixelProbe"/>.</summary>
        private readonly PixelProbe? _probe;
        private readonly int _probeAt;

        /// <summary>The game's light, or null for the picture's own - see <see cref="Scened"/>.</summary>
        private readonly SceneLight? _scene;
        private readonly ShadowMap? _shadow;

        /// <summary>The way to the eye in the model's space, which is where the scene's lights are.</summary>
        private readonly Vector3 _toEye;

        public Drawing(Canvas canvas, Drawn drawn, PixelProbe? probe)
        {
            SkinnedMesh mesh = drawn.Mesh;
            _probe = probe;
            _probeAt = probe is null ? -1 : (probe.Y * canvas.Size) + probe.X;
            _time = canvas.Time;
            _dust = canvas.Dust;
            _eye = drawn.Eye;
            _programs = drawn.Programs;
            _shades = canvas.Shades;
            _shadeLevels = canvas.ShadeLevels;
            _stride = drawn.Stride;
            _places = drawn.Places;
            _turns = drawn.Turns;
            _mesh = mesh;
            _winners = drawn.Clocked > 0 ? canvas.Winners : null;
            _keptPixels = canvas.KeptPixels;
            _keptDepth = canvas.KeptDepth;
            _keptWinners = canvas.KeptWinners;
            _binStarts = canvas.BinStarts;
            _binEntries = canvas.BinEntries;
            _bands = Bands(canvas.Size);
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
            _lamp = drawn.Lamp;
            _half = Vector3.Normalize(ToEye + _lamp);
            _fresnel = GlossLight.Fresnel(Math.Clamp(Vector3.Dot(ToEye, _half), 0f, 1f));
            _ink = drawn.Ink;
            _palette = drawn.Palette;
            _wears = canvas.Wears;
            _blends = canvas.Blends;
            _owners = canvas.Owners;
            _stamps = canvas.Stamps;
            _translucent = drawn.Translucent;
            _scene = drawn.Scene;
            _shadow = drawn.Scene is { } scene && scene.SunColour != Vector3.Zero ? drawn.Shadow : null;

            // THE VIEW'S THIRD COLUMN IS THE WAY INTO THE PICTURE in model space - the view is a turn,
            // so its inverse is its transpose - and the eye is the other way.
            var into = new Vector3(drawn.Eye.X, drawn.Eye.Y, drawn.Eye.Z);
            _toEye = into.LengthSquared() > 0f ? -Vector3.Normalize(into) : -Vector3.UnitZ;
        }

        /// <summary>Draws every triangle's part that falls in the rows from <paramref name="top"/> up to <paramref name="end"/>.</summary>
        /// <remarks>
        /// TWO PASSES WHEN ANYTHING IS TRANSLUCENT: every opaque triangle first, writing depth, then
        /// the translucent ones in the mesh's order, tested against that depth and writing none -
        /// so a glow behind a wall stays hidden and two glows in front of it both show. Both passes
        /// stay inside the band, so the picture is still the one-threaded one to the byte. The order
        /// among translucent triangles is the file's rather than back to front; additive does not
        /// care, and mixed shapes overlapping themselves are the one place it could show.
        /// </remarks>
        /// <param name="band">Which band, from the top.</param>
        /// <param name="top">The band's first row.</param>
        /// <param name="end">The row after its last.</param>
        /// <param name="again">True to put the kept still part back and draw only the clock's triangles and the translucent ones - see <see cref="Again"/>.</param>
        /// <param name="counts">This thread's counts of how the translucent shapes came out, or null - see <see cref="LayerTally"/>.</param>
        public void Band(int band, int top, int end, bool again, int[]? counts)
        {
            if (again)
            {
                Kept(top, end, back: true);
            }
            else
            {
                // A KEEPING DRAWING DRAWS THE STILL PART FIRST, and keeps it before the clock's.
                Pass(1, band, top, end, counts);
                if (_winners is not null)
                {
                    Kept(top, end, back: false);
                }
            }

            Pass(2, band, top, end, counts);
            if (_translucent)
            {
                Pass(3, band, top, end, counts);
            }
        }

        /// <summary>Draws one pass's triangles that reach the band, in the mesh's order - see <see cref="Binned"/>.</summary>
        private void Pass(int kind, int band, int top, int end, int[]? counts)
        {
            int bin = ((kind - 1) * _bands) + band;
            for (int at = _binStarts[bin], upto = _binStarts[bin + 1]; at < upto; at++)
            {
                int one = _binEntries[at];
                Rasterise(one, Math.Max(_tops[one], top), Math.Min(_feet[one], end - 1), counts);
            }
        }

        /// <summary>Keeps the band's rows of the still part, or puts them back.</summary>
        private void Kept(int top, int end, bool back)
        {
            int from = top * _size;
            int count = (end - top) * _size;
            if (back)
            {
                Array.Copy(_keptPixels, from * 4, _pixels, from * 4, count * 4);
                Array.Copy(_keptDepth, from, _depth, from, count);
                Array.Copy(_keptWinners, from, _winners!, from, count);
            }
            else
            {
                Array.Copy(_pixels, from * 4, _keptPixels, from * 4, count * 4);
                Array.Copy(_depth, from, _keptDepth, from, count);
                Array.Copy(_winners!, from, _keptWinners, from, count);
            }
        }

        private void Rasterise(int one, int top, int foot, int[]? counts)
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

            // ONE TEST PER TRIANGLE, so a drawing nobody probes asks nothing per pixel but one false.
            bool probing = _probe is not null && _probe.Y >= top && _probe.Y <= foot && _probe.X >= least && _probe.X <= most;

            // THE TEXTURE THIS TRIANGLE'S SHAPE WEARS, not the model's. See Palette.
            Mipmaps? skin = _palette[_wears[one]];
            MaterialBlend blend = _blends[one];
            bool skinned = skin is not null;

            // A TIE GOES TO WHICHEVER COMES FIRST IN THE MESH, where the passes are split - see Again.
            bool ranked = _winners is not null && !Translucent(blend);

            // A SHADE PROGRAM WHERE THE TRIANGLE HAS ONE AND IS SOLID, CUT OUT OR MIXED. A cut-out one
            // takes its EDGE from the texture's alpha and its colour from the program - foliage is cut
            // out, and a tree whose leaves are shaded by a vertex-colour graph (VertexColourAO) never
            // ran it while cut-outs were drawn plain. A mixed one takes both from the program, which is
            // what a ground layer's graphs make its alpha of: a mud whose texture has no alpha at all
            // fades by the depth behind it (ParallaxUvSpaceContactFade). Added light stays its texture's.
            int shadeAt = _programs.Length > 0 ? _shades[one] : -1;
            ShadeProgram? program = shadeAt >= 0 && blend is MaterialBlend.Opaque or MaterialBlend.Cutout or MaterialBlend.Alpha ? _programs[shadeAt] : null;
            Span<Vector4> registers = program is null ? default : stackalloc Vector4[program.Registers];
            ReadOnlySpan<float> shadeLevels = program is null
                ? default
                : new ReadOnlySpan<float>(_shadeLevels, one * _stride, program.Samples);
            program?.Preset(registers, _time, _eye, _dust);
            Vector3 p0 = default, p1 = default, p2 = default, n0 = default, n1 = default, n2 = default;
            Vector4 v0 = default, v1 = default, v2 = default;
            bool tinted = program is { UsesVertexColour: true };
            bool scened = _scene is not null;
            if (program is not null || scened)
            {
                p0 = _places[i0];
                p1 = _places[i1];
                p2 = _places[i2];
                n0 = _turns[i0];
                n1 = _turns[i1];
                n2 = _turns[i2];
                if (tinted)
                {
                    v0 = VertexColourOf(_mesh, i0);
                    v1 = VertexColourOf(_mesh, i1);
                    v2 = VertexColourOf(_mesh, i2);
                }
            }

            Vector2 s0 = default;
            Vector2 s1 = default;
            Vector2 s2 = default;
            if (skinned || program is not null)
            {
                s0 = _coordinates[i0];
                s1 = _coordinates[i1];
                s2 = _coordinates[i2];
            }

            // THE TRIANGLE'S OWN ∂p/∂u AND ∂p/∂v where a march reads them - see FixModelTBN.
            if (program is { UsesTangents: true })
            {
                (Vector3 tangent, Vector3 binormal) = ShadeProgram.Spanned(p0, p1, p2, s0, s1, s2);
                ShadeProgram.Frame(registers, tangent, binormal);
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
                    float held = _depth[at];
                    bool here = probing && at == _probeAt;
                    if (away >= held && !(ranked && away == held && one < _winners![at]))
                    {
                        if (here)
                        {
                            _probe!.Add(new ProbeFragment(one, Seen.Under, away, held, 0f));
                        }

                        continue;
                    }

                    if (Translucent(blend))
                    {
                        // ONCE PER SHAPE PER PIXEL - see Canvas.Stamps.
                        int owner = _owners[one];
                        if (_stamps[at] == owner)
                        {
                            if (here)
                            {
                                _probe!.Add(new ProbeFragment(one, Seen.Again, away, held, 0f));
                            }

                            continue;
                        }

                        Spot spot_ = new(first, second, third);
                        if (program is not null)
                        {
                            // THE SOLID DEPTH IS STILL WHAT THE BUFFER HOLDS: the translucent pass
                            // writes none - see Band - so a layer measures against the ground under it.
                            Vector3 mixed = program.Colour(
                                registers,
                                spot_.Of(s0, s1, s2),
                                (first * p0) + (second * p1) + (third * p2),
                                (first * n0) + (second * n1) + (third * n2),
                                shadeLevels,
                                out float cover,
                                out Vector3 mixedSpecular,
                                out float mixedGloss,
                                tinted ? (first * v0) + (second * v1) + (third * v2) : default,
                                held);

                            // THE PROGRAM'S ALPHA WHERE ITS GRAPHS SET ONE, the texture's where they
                            // do not - a mixed shape whose graphs only colour it is drawn as it was.
                            if (!program.HasAlpha && !float.IsNegativeInfinity(cover))
                            {
                                cover = skinned ? Sample4(skin!, spot_.Of(s0, s1, s2), level).W : 0.5f;
                            }

                            if (counts is not null)
                            {
                                LayerTally.Count(counts, owner, cover, away, held);
                            }

                            if (!(cover > 0f))
                            {
                                if (here)
                                {
                                    _probe!.Add(new ProbeFragment(one, float.IsNegativeInfinity(cover) ? Seen.Discarded : Seen.Clear, away, held, 0f));
                                }

                                continue;
                            }

                            _stamps[at] = owner;
                            Vector3 lit = scened
                                ? Scened(program, mixed, mixedSpecular, mixedGloss, (first * p0) + (second * p1) + (third * p2), (first * n0) + (second * n1) + (third * n2))
                                : Lit(program, mixed, mixedSpecular, mixedGloss, (first * f0) + (second * f1) + (third * f2));
                            Over(at, blend, new Vector4(lit, cover));
                            if (here)
                            {
                                _probe!.Add(new ProbeFragment(one, blend == MaterialBlend.Additive ? Seen.Added : Seen.Mixed, away, held, cover));
                            }

                            continue;
                        }

                        _stamps[at] = owner;
                        Vector4 plain = skinned ? Sample4(skin!, spot_.Of(s0, s1, s2), level) : new Vector4(_ink, 0.5f);
                        Over(at, blend, plain);
                        if (counts is not null)
                        {
                            LayerTally.Count(counts, owner, plain.W, away, held);
                        }

                        if (here)
                        {
                            _probe!.Add(new ProbeFragment(one, blend == MaterialBlend.Additive ? Seen.Added : Seen.Mixed, away, held, plain.W));
                        }

                        continue;
                    }

                    Vector3 colour = _ink;
                    Vector3 specular = default;
                    float gloss = 0f;
                    if (program is not null)
                    {
                        // A CUT-OUT SHAPE IS CUT ON THE ALPHA ITS GRAPHS LEAVE where they set one - the
                        // engine's AlphaTestClipping clips on albedo_color.a - and on its texture's where
                        // they do not. Either way before depth is written - see the plain cut-out below.
                        bool cut = blend == MaterialBlend.Cutout;
                        if (cut && !program.HasAlpha && skinned
                            && Sample4(skin!, (first * s0) + (second * s1) + (third * s2), level).W < CutoutAlpha)
                        {
                            if (here)
                            {
                                _probe!.Add(new ProbeFragment(one, Seen.Cut, away, held, 0f));
                            }

                            continue;
                        }

                        colour = program.Colour(
                            registers,
                            (first * s0) + (second * s1) + (third * s2),
                            (first * p0) + (second * p1) + (third * p2),
                            (first * n0) + (second * n1) + (third * n2),
                            shadeLevels,
                            out float alpha,
                            out specular,
                            out gloss,
                            tinted ? (first * v0) + (second * v1) + (third * v2) : default);
                        if ((cut && program.HasAlpha && alpha < CutoutAlpha) || float.IsNegativeInfinity(alpha))
                        {
                            if (here)
                            {
                                _probe!.Add(new ProbeFragment(one, float.IsNegativeInfinity(alpha) ? Seen.Discarded : Seen.Cut, away, held, 0f));
                            }

                            continue;
                        }
                    }
                    else if (skinned)
                    {
                        // AFFINE INTERPOLATION IS EXACT HERE. The projection is orthographic, so a
                        // coordinate across the triangle really is linear in screen space - the
                        // perspective correction a game renderer needs would be dividing by a w
                        // that is always one.
                        Vector2 spot = (first * s0) + (second * s1) + (third * s2);
                        if (blend == MaterialBlend.Cutout)
                        {
                            // BEFORE DEPTH IS WRITTEN, so what a dropped pixel leaves open shows
                            // whatever is behind it rather than a hole in the shape of a leaf.
                            Vector4 texel = Sample4(skin!, spot, level);
                            if (texel.W < CutoutAlpha)
                            {
                                if (here)
                                {
                                    _probe!.Add(new ProbeFragment(one, Seen.Cut, away, held, 0f));
                                }

                                continue;
                            }

                            colour = new Vector3(texel.X, texel.Y, texel.Z);
                        }
                        else
                        {
                            colour = Sample(skin!, spot, level);
                        }
                    }

                    _depth[at] = away;
                    if (ranked)
                    {
                        _winners![at] = one;
                    }

                    if (here)
                    {
                        _probe!.Add(new ProbeFragment(one, Seen.Solid, away, held, 0f));
                    }

                    Vector3 shown = scened
                        ? Scened(program, colour, specular, gloss, (first * p0) + (second * p1) + (third * p2), (first * n0) + (second * n1) + (third * n2))
                        : Lit(program, colour, specular, gloss, (first * f0) + (second * f1) + (third * f2));
                    _pixels[at * 4] = Byte(shown.X);
                    _pixels[(at * 4) + 1] = Byte(shown.Y);
                    _pixels[(at * 4) + 2] = Byte(shown.Z);
                    _pixels[(at * 4) + 3] = 255;
                }
            }
        }

        /// <summary>One pixel's colour under the picture's light - the solid pass's and a mixed program's alike.</summary>
        /// <param name="program">The program the colour came from, or null for a texture's or the ink.</param>
        /// <param name="colour">The colour, sRGB nought to one.</param>
        /// <param name="specular">The program's specular colour, linear.</param>
        /// <param name="gloss">The program's gloss.</param>
        /// <param name="facing">The pixel's normal in view space, interpolated.</param>
        private Vector3 Lit(ShadeProgram? program, Vector3 colour, Vector3 specular, float gloss, Vector3 facing)
        {
            Vector3 normal = facing.LengthSquared() > 1e-6f ? Vector3.Normalize(facing) : facing;

            // TWO-SIDED, because the mesh's winding is not established and a single-sided
            // light leaves whole limbs black where the triangles happen to face away.
            float lit = MathF.Abs(Vector3.Dot(normal, _lamp));
            float shade = Ambient + ((1f - Ambient) * lit);

            // A SPECULAR COLOUR IS LIGHT TOO - a metal's albedo is black and its colour is all
            // in it. The glossy program is lit the game's way, the other flat; see Glossed and Flat.
            if (program is { HasSpecular: true })
            {
                if (program.HasGloss)
                {
                    return Glossed(colour, shade, normal, specular, gloss);
                }

                colour = Flat(colour, specular);
            }

            return colour * shade;
        }

        /// <summary>One pixel under the game's light - see <see cref="SceneLight.Shade"/> - sRGB nought to one, like <see cref="Lit"/>.</summary>
        /// <param name="program">The program the colour came from, or null for a texture's or the ink.</param>
        /// <param name="colour">The colour, sRGB nought to one.</param>
        /// <param name="specular">The program's specular colour, linear.</param>
        /// <param name="gloss">The program's gloss.</param>
        /// <param name="place">The pixel's place in model space, interpolated.</param>
        /// <param name="turn">Its normal in model space, interpolated.</param>
        /// <remarks>
        /// TWO-SIDED LIKE THE PICTURE'S OWN LIGHT: a normal turned from the eye is turned round, which
        /// is what a two-sided material does in the game and what keeps a face seen from behind from
        /// going black. A specular colour without a gloss is laid on the albedo as <see cref="Flat"/> lays it.
        /// </remarks>
        private Vector3 Scened(ShadeProgram? program, Vector3 colour, Vector3 specular, float gloss, Vector3 place, Vector3 turn)
        {
            Vector3 normal = turn.LengthSquared() > 1e-12f ? Vector3.Normalize(turn) : _toEye;
            if (Vector3.Dot(normal, _toEye) < 0f)
            {
                normal = -normal;
            }

            var albedo = new Vector3(ShadeProgram.Linear(colour.X), ShadeProgram.Linear(colour.Y), ShadeProgram.Linear(colour.Z));
            bool glossy = program is { HasSpecular: true, HasGloss: true };
            if (program is { HasSpecular: true, HasGloss: false })
            {
                albedo += Vector3.Max(specular - new Vector3(ShadeProgram.Dielectric), Vector3.Zero);
            }

            float sun = _shadow?.Lit(place, normal) ?? 1f;
            Vector3 lit = _scene!.Shade(albedo, place, normal, _toEye, sun, glossy, specular, gloss);
            return new Vector3(ShadeProgram.Srgb(lit.X), ShadeProgram.Srgb(lit.Y), ShadeProgram.Srgb(lit.Z));
        }

        /// <summary>
        /// One pixel lit the game's way: its colour under the picture's shading, and the specular light of the lamp and the environment on top.
        /// </summary>
        /// <remarks>
        /// THE SHADING IS READ AS LIGHT. The picture lights a colour by <see cref="Ambient"/> plus the
        /// rest times the lamp's cosine, on the sRGB value; that is the ambient and the lamp as light
        /// in linear terms, near enough, and the game adds specular light to the diffuse in linear
        /// terms - so the colour under the shading is taken to linear, the specular is added there,
        /// and the sum goes back. With no specular the pixel is what it was. The environment is
        /// <see cref="AmbientLight"/> everywhere and the lamp <see cref="LampLight"/>; see GlossLight.
        /// </remarks>
        private Vector3 Glossed(Vector3 colour, float shade, Vector3 normal, Vector3 specular, float gloss)
        {
            float toEye = Vector3.Dot(normal, ToEye);
            if (toEye < 0f)
            {
                normal = -normal;
                toEye = -toEye;
            }

            float lobe = GlossLight.Lobe(normal, toEye, _lamp, _half, gloss) * LampLight;
            GlossLight.Environment(toEye, gloss, out float bias, out float scale);
            float unscaled = (lobe * _fresnel) + (AmbientLight * bias);
            float scaled = (lobe * (1f - _fresnel)) + (AmbientLight * scale);
            return new Vector3(
                ShadeProgram.Srgb(ShadeProgram.Linear(colour.X * shade) + unscaled + (scaled * specular.X)),
                ShadeProgram.Srgb(ShadeProgram.Linear(colour.Y * shade) + unscaled + (scaled * specular.Y)),
                ShadeProgram.Srgb(ShadeProgram.Linear(colour.Z * shade) + unscaled + (scaled * specular.Z)));
        }

        /// <summary>
        /// One pixel's colour lit flat: its specular colour past a dielectric's laid onto its albedo, both shaded alike.
        /// </summary>
        /// <remarks>
        /// THE CHEAP WAY, AND A DIELECTRIC IS UNCHANGED BY IT: every dielectric the game has writes a
        /// specular colour of 0.04 (see ShadeProgram.Dielectric), which adds nothing here, while a
        /// metal - albedo black, specular its colour - shows that colour as a painted surface would.
        /// No lobe, no environment, no gloss: what is left of the specular is taken for colour.
        /// </remarks>
        private static Vector3 Flat(Vector3 colour, Vector3 specular) => new(
            ShadeProgram.Srgb(ShadeProgram.Linear(colour.X) + MathF.Max(specular.X - ShadeProgram.Dielectric, 0f)),
            ShadeProgram.Srgb(ShadeProgram.Linear(colour.Y) + MathF.Max(specular.Y - ShadeProgram.Dielectric, 0f)),
            ShadeProgram.Srgb(ShadeProgram.Linear(colour.Z) + MathF.Max(specular.Z - ShadeProgram.Dielectric, 0f)));

        /// <summary>
        /// Puts one translucent pixel over what is already at <paramref name="at"/>.
        /// </summary>
        /// <remarks>
        /// IN PREMULTIPLIED TERMS, because the picture is itself laid over a backdrop the overlay
        /// draws and much of it is still empty: an additive glow over nothing has to come out as
        /// light over whatever the backdrop is, not as a black square. Mixed: the source covers its
        /// own alpha's worth. Additive: its colour, weighted by its alpha, is added, and the coverage
        /// grows by the brightest channel of what was added - so a glow over the empty frame is as
        /// opaque as it is bright. UNLIT, both: an effect's light is its own.
        /// </remarks>
        private void Over(int at, MaterialBlend blend, Vector4 source)
        {
            int p = at * 4;
            float da = _pixels[p + 3] * (1f / 255f);
            var dst = new Vector3(_pixels[p], _pixels[p + 1], _pixels[p + 2]) * (1f / 255f) * da;
            float sa = Math.Clamp(source.W, 0f, 1f);
            var colour = new Vector3(source.X, source.Y, source.Z);

            Vector3 premultiplied;
            float alpha;
            if (blend == MaterialBlend.Additive)
            {
                Vector3 added = colour * sa;
                premultiplied = dst + added;
                alpha = MathF.Min(1f, da + MathF.Max(added.X, MathF.Max(added.Y, added.Z)));
            }
            else
            {
                premultiplied = (colour * sa) + (dst * (1f - sa));
                alpha = sa + (da * (1f - sa));
            }

            if (alpha <= 0f)
            {
                return;
            }

            Vector3 straight = premultiplied / alpha;
            _pixels[p] = Byte(straight.X);
            _pixels[p + 1] = Byte(straight.Y);
            _pixels[p + 2] = Byte(straight.Z);
            _pixels[p + 3] = Byte(alpha);
        }
    }

    /// <summary>The three barycentric weights of a pixel, and the coordinate they give.</summary>
    private readonly record struct Spot(float First, float Second, float Third)
    {
        public Vector2 Of(Vector2 s0, Vector2 s1, Vector2 s2) => (First * s0) + (Second * s1) + (Third * s2);
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
    internal static float Level(
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

    /// <summary>
    /// <see cref="Sample"/> with the texture's alpha - only translucent and cut-out triangles pay for the fourth channel.
    /// </summary>
    internal static Vector4 Sample4(Mipmaps skin, Vector2 spot, float level)
    {
        var lower = (int)level;
        Vector4 colour = Texel4(skin[lower], spot);

        float between = level - lower;
        if (between > 0f && lower + 1 < skin.Count)
        {
            colour = Vector4.Lerp(colour, Texel4(skin[lower + 1], spot), between);
        }

        return colour;
    }

    /// <summary><see cref="Texel"/> with alpha: one level read bilinearly at a point, wrapping at every edge.</summary>
    private static Vector4 Texel4(GamePicture level, Vector2 spot)
    {
        float u = spot.X - MathF.Floor(spot.X);
        float v = spot.Y - MathF.Floor(spot.Y);
        if (!(u < 1f))
        {
            u = 0f;
        }

        if (!(v < 1f))
        {
            v = 0f;
        }

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

        Vector4 upper = Vector4.Lerp(
            new Vector4(rgba[a], rgba[a + 1], rgba[a + 2], rgba[a + 3]),
            new Vector4(rgba[b], rgba[b + 1], rgba[b + 2], rgba[b + 3]), fx);
        Vector4 lower = Vector4.Lerp(
            new Vector4(rgba[c], rgba[c + 1], rgba[c + 2], rgba[c + 3]),
            new Vector4(rgba[d], rgba[d + 1], rgba[d + 2], rgba[d + 3]), fx);

        return Vector4.Lerp(upper, lower, fy) * (1f / 255f);
    }

    /// <summary>Twice the signed area of a triangle, flattened onto the screen.</summary>
    private static float Cross(Vector3 a, Vector3 b, Vector3 c)
        => ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));

    private static float Min3(float a, float b, float c) => MathF.Min(a, MathF.Min(b, c));

    private static float Max3(float a, float b, float c) => MathF.Max(a, MathF.Max(b, c));

    private static byte Byte(float said) => (byte)Math.Clamp(said * 255f, 0f, 255f);
}
