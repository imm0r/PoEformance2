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
/// AND IT IS FAST ENOUGH BECAUSE IT IS NOT PER FRAME. A monster is drawn once when it is picked
/// and again when somebody turns it; the mesh is a few thousand triangles and the frame is a few
/// hundred pixels square. This is not a renderer competing with a GPU, it is a portrait painter.
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

    /// <summary>How much of the frame the model fills, leaving a margin around it.</summary>
    public const float Fill = 0.86f;

    /// <summary>
    /// Draws the mesh, turned about its up axis by <paramref name="turn"/> radians.
    /// </summary>
    /// <param name="mesh">What to draw. An empty one gives an empty picture.</param>
    /// <param name="size">How many pixels each way.</param>
    /// <param name="turn">Rotation about the model's up axis, in radians.</param>
    /// <param name="tilt">Rotation towards the viewer, in radians. Zero looks at it level.</param>
    /// <param name="ink">The colour to shade with, as red, green and blue in 0..1.</param>
    public static GamePicture Of(
        SkinnedMesh? mesh, int size, float turn = 0f, float tilt = 0f, Vector3 ink = default)
    {
        size = Math.Clamp(size, 1, Widest);
        var pixels = new byte[size * size * 4];

        if (mesh is not { Ready: true })
        {
            return new GamePicture(size, size, pixels);
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

        float scale = size * Fill / reach;
        float half = size * 0.5f;

        var depth = new float[size * size];
        Array.Fill(depth, float.MaxValue);

        // The light sits over the viewer's shoulder, which is the one placement that never leaves
        // a face black: anything pointing at the camera is lit.
        Vector3 lamp = Vector3.Normalize(new Vector3(-0.35f, -0.55f, -0.75f));

        Span<Vector3> corner = stackalloc Vector3[3];
        Span<Vector3> facing = stackalloc Vector3[3];

        for (var one = 0; one + 2 < mesh.Indices.Length; one += 3)
        {
            for (var part = 0; part < 3; part++)
            {
                int point = mesh.Indices[one + part];
                Vector3 place = Vector3.Transform(mesh.Positions[point], view);

                corner[part] = new Vector3(
                    (place.X * scale) + half,
                    (place.Y * scale) + half,
                    place.Z);

                facing[part] = Vector3.TransformNormal(mesh.Normals[point], view);
            }

            Triangle(pixels, depth, size, corner, facing, lamp, ink);
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
        Vector3 lamp,
        Vector3 ink)
    {
        float area = Cross(corner[0], corner[1], corner[2]);
        if (MathF.Abs(area) < 1e-6f)
        {
            return;
        }

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

                pixels[(at * 4) + 0] = Byte(ink.X * shade);
                pixels[(at * 4) + 1] = Byte(ink.Y * shade);
                pixels[(at * 4) + 2] = Byte(ink.Z * shade);
                pixels[(at * 4) + 3] = 255;
            }
        }
    }

    /// <summary>Twice the signed area of a triangle, flattened onto the screen.</summary>
    private static float Cross(Vector3 a, Vector3 b, Vector3 c)
        => ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));

    private static float Min3(float a, float b, float c) => MathF.Min(a, MathF.Min(b, c));

    private static float Max3(float a, float b, float c) => MathF.Max(a, MathF.Max(b, c));

    private static byte Byte(float said) => (byte)Math.Clamp(said * 255f, 0f, 255f);
}
