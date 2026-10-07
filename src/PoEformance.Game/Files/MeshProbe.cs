namespace PoEformance.Game.Files;

/// <summary>What became of one triangle's fragment at a probed pixel - see <see cref="PixelProbe"/>.</summary>
public enum Seen : byte
{
    /// <summary>Written, colour and depth: the nearest solid surface so far.</summary>
    Solid,

    /// <summary>Behind what the depth buffer already held, so not drawn at all.</summary>
    Under,

    /// <summary>A translucent shape that had already blended into this pixel - see MeshPicture.Canvas.Stamps.</summary>
    Again,

    /// <summary>Cut out: its alpha was under the cut-out line.</summary>
    Cut,

    /// <summary>Dropped by its own shade program - a contact fade over a surface that lies above it.</summary>
    Discarded,

    /// <summary>Translucent with no alpha left: blended as nothing.</summary>
    Clear,

    /// <summary>Mixed over what was behind it, by <see cref="ProbeFragment.Cover"/>.</summary>
    Mixed,

    /// <summary>Added to what was behind it.</summary>
    Added,
}

/// <summary>One triangle's fragment at a probed pixel.</summary>
/// <param name="Triangle">The triangle, by its place in the mesh.</param>
/// <param name="Seen">What became of it.</param>
/// <param name="Depth">Its own depth along the view, in the model's units - nearer is less.</param>
/// <param name="Behind">What the depth buffer held when it arrived: the nearest solid surface drawn before it, or <see cref="float.MaxValue"/> where none was.</param>
/// <param name="Cover">How much of it covered what was behind, for a translucent one; nought otherwise.</param>
public readonly record struct ProbeFragment(int Triangle, Seen Seen, float Depth, float Behind, float Cover);

/// <summary>
/// Every fragment one pixel of a drawing was made of, in the order the drawing met them.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS. A layer of mud drawn over everything and a layer of mud that fades as the game
/// fades it look different only where the ground under it is - and a picture says nothing about
/// what is under its own pixels. The fragments say it all: which surface won, what lay beneath it,
/// how far beneath, and what alpha a mixed layer came out with. The same question the interface
/// browser answers for the game's UI with F8, asked of a picture.
///
/// RECORDED BY THE DRAWING ITSELF, at the points where it decides, not by a second walk over the
/// triangles that would have to agree with the first. One pixel lies in one band and a band is drawn
/// by one thread, so the list needs no lock. A drawing that keeps its still part and draws the
/// clock's part again (MeshPicture.Again) probes nothing: set this on the canvas and draw it whole.
/// </remarks>
public sealed class PixelProbe
{
    private readonly List<ProbeFragment> _fragments = [];

    /// <param name="x">The pixel's column, from the left.</param>
    /// <param name="y">The pixel's row, from the top.</param>
    public PixelProbe(int x, int y)
    {
        X = x;
        Y = y;
    }

    /// <summary>The pixel's column, from the left.</summary>
    public int X { get; }

    /// <summary>The pixel's row, from the top.</summary>
    public int Y { get; }

    /// <summary>Every fragment the pixel met, in the drawing's order: the solid pass's, then the translucent pass's.</summary>
    public IReadOnlyList<ProbeFragment> Fragments => _fragments;

    /// <summary>The depth the pixel was left with: the solid surface shown, or <see cref="float.MaxValue"/> where none was.</summary>
    public float Final { get; internal set; } = float.MaxValue;

    /// <summary>Whether a drawing has been through it since it was set.</summary>
    public bool Drawn { get; internal set; }

    internal void Add(ProbeFragment fragment) => _fragments.Add(fragment);

    internal void Clear()
    {
        _fragments.Clear();
        Final = float.MaxValue;
        Drawn = false;
    }
}

/// <summary>
/// How every translucent shape of a drawing came out, pixel by pixel: how many it reached, how many
/// had a solid surface behind and how far behind, and what alpha it was mixed with.
/// </summary>
/// <remarks>
/// THE OTHER HALF OF <see cref="PixelProbe"/>. One pixel says what happened there; this says
/// whether it happened everywhere. A ground layer that should fade where the ground meets it and
/// shows at full alpha on nine pixels in ten is a number here before it is an impression.
///
/// IN BINS, NOT IN SAMPLES, because a room's mud reaches hundreds of thousands of pixels and a
/// median would have to keep them all. Counted per thread and added up once the bands are done,
/// so the drawing's threads never wait on each other for it.
/// </remarks>
public sealed class LayerTally
{
    /// <summary>Slots per shape: fragments, with a solid behind, discarded, then the cover bins and the gap bins.</summary>
    internal const int Slots = 3 + CoverBins + GapBins;

    /// <summary>How the alpha is binned: nought, under a quarter, a half, three quarters, nearly whole, whole.</summary>
    public const int CoverBins = 6;

    /// <summary>How the gap to the solid behind is binned, in the model's units: under 1, 3, 10, 30, 100, and past it.</summary>
    public const int GapBins = 6;

    private const int FragmentsAt = 0;
    private const int BehindAt = 1;
    private const int DiscardedAt = 2;
    private const int CoverAt = 3;
    private const int GapAt = CoverAt + CoverBins;

    private int[] _counts = [];

    /// <summary>How many shapes the last drawing counted.</summary>
    public int Shapes { get; private set; }

    /// <summary>The upper edge of each gap bin but the last.</summary>
    public static IReadOnlyList<float> GapEdges { get; } = [1f, 3f, 10f, 30f, 100f];

    /// <summary>What one shape came to in the last drawing.</summary>
    public LayerCount Of(int shape)
    {
        if ((uint)shape >= (uint)Shapes)
        {
            return default;
        }

        ReadOnlySpan<int> at = _counts.AsSpan(shape * Slots, Slots);
        return new LayerCount(
            at[FragmentsAt],
            at[BehindAt],
            at[DiscardedAt],
            at.Slice(CoverAt, CoverBins).ToArray(),
            at.Slice(GapAt, GapBins).ToArray());
    }

    /// <summary>Room for this many shapes, every count nought.</summary>
    internal void Reset(int shapes)
    {
        Shapes = Math.Max(0, shapes);
        if (_counts.Length != Shapes * Slots)
        {
            _counts = new int[Shapes * Slots];
        }
        else
        {
            Array.Clear(_counts);
        }
    }

    /// <summary>One thread's counts added in.</summary>
    internal void Add(int[] local)
    {
        lock (_counts)
        {
            for (var one = 0; one < local.Length && one < _counts.Length; one++)
            {
                _counts[one] += local[one];
            }
        }
    }

    /// <summary>Counts one translucent fragment into a thread's own counts.</summary>
    /// <param name="local">The thread's counts, <see cref="Slots"/> per shape.</param>
    /// <param name="shape">The shape it belongs to.</param>
    /// <param name="cover">The alpha it was mixed with, or negative infinity where its program discarded it.</param>
    /// <param name="depth">Its own depth.</param>
    /// <param name="behind">The depth of the solid surface behind it, or <see cref="float.MaxValue"/>.</param>
    internal static void Count(int[] local, int shape, float cover, float depth, float behind)
    {
        int at = shape * Slots;
        if ((uint)(at + Slots) > (uint)local.Length)
        {
            return;
        }

        local[at + FragmentsAt]++;
        if (behind < float.MaxValue)
        {
            local[at + BehindAt]++;
            float gap = behind - depth;
            int bin = gap < 1f ? 0 : gap < 3f ? 1 : gap < 10f ? 2 : gap < 30f ? 3 : gap < 100f ? 4 : 5;
            local[at + GapAt + bin]++;
        }

        if (float.IsNegativeInfinity(cover))
        {
            local[at + DiscardedAt]++;
            return;
        }

        int part = !(cover > 0f) ? 0 : cover < 0.25f ? 1 : cover < 0.5f ? 2 : cover < 0.75f ? 3 : cover < 0.99f ? 4 : 5;
        local[at + CoverAt + part]++;
    }
}

/// <summary>What one translucent shape came to - see <see cref="LayerTally"/>.</summary>
/// <param name="Fragments">Pixels it reached in front of the solid surface there.</param>
/// <param name="Behind">Of those, how many had a solid surface behind them at all.</param>
/// <param name="Discarded">How many its own program dropped.</param>
/// <param name="Cover">The rest by alpha - see <see cref="LayerTally.CoverBins"/>.</param>
/// <param name="Gaps">Those with a solid behind, by how far behind - see <see cref="LayerTally.GapEdges"/>.</param>
public readonly record struct LayerCount(int Fragments, int Behind, int Discarded, IReadOnlyList<int> Cover, IReadOnlyList<int> Gaps);
