namespace PoEformance.Game.Files;

/// <summary>
/// The sizes a model is drawn at, and which of them a picture of a given width calls for.
/// </summary>
/// <remarks>
/// A LADDER RATHER THAN THE PANE'S OWN WIDTH. Dragging a pane's edge changes its width on every
/// frame, and a render tied to it would redraw the mesh for each of those, at a new size,
/// throwing away the canvas each time. Rungs are crossed rarely, and ImGui scales the quad
/// between them for nothing.
///
/// THEY ROUGHLY DOUBLE ON PURPOSE, because the work grows with the AREA and not the edge: at 384
/// a real rig costs 3.0 ms a frame, at 768 it costs 8.0 and at 1536 it costs 25.1. So stepping
/// down ONE rung while a model is being dragged quarters the work rather than shaving a little
/// off it, and that is the moment a dropped frame is actually felt.
///
/// IT LIVES IN THIS LAYER BECAUSE IT IS ARITHMETIC, and arithmetic inside the overlay cannot be
/// tested: that project is Windows-only and the test project does not reference it. This was
/// written there first, and a field read one line before it was assigned shipped a cap of ZERO -
/// every monster in the book silently lost its portrait, with not even a message, because a cap
/// of zero is narrower than the smallest picture worth drawing. One assertion would have caught
/// it, and there was nowhere to put one. Anything here that is a sum rather than a call into
/// ImGui belongs on this side of the line for that reason.
/// </remarks>
public sealed class PictureLadder
{
    /// <summary>The default cap: the largest rung that still turns smoothly.</summary>
    /// <remarks>
    /// MEASURED, at 8.0 ms a frame while turning against an overlay frame of about 10. One rung
    /// up is 12.5 and two is 25.1, which a drag cannot keep up with. See OverlaySettings.
    /// </remarks>
    public const int Usual = 768;

    /// <summary>The smallest a cap may be set to, and the bottom rung.</summary>
    public const int Smallest = 256;

    private static readonly int[] Rungs = [Smallest, 384, 512, Usual, 1024, 1536, MeshPicture.Widest];

    /// <param name="most">The biggest size to allow, clamped and snapped to a rung.</param>
    public PictureLadder(int most = Usual)
        => Most = Snap(Math.Clamp(most, Smallest, MeshPicture.Widest));

    /// <summary>Every size a model is ever drawn at.</summary>
    public static IReadOnlyList<int> Steps => Rungs;

    /// <summary>The biggest size this ladder allows.</summary>
    public int Most { get; }

    /// <summary>What to draw at for a picture shown this wide, never above the cap.</summary>
    public int For(float side) => Math.Min(Snap(Asked(side)), Most);

    /// <summary>The same, one rung lower: what a model being dragged is drawn at.</summary>
    public int Dragging(float side) => Down(For(side));

    /// <summary>The smallest rung at or above what is asked for, or the top one.</summary>
    public static int Snap(int want)
    {
        foreach (int rung in Rungs)
        {
            if (rung >= want)
            {
                return rung;
            }
        }

        return Rungs[^1];
    }

    /// <summary>One rung down from the one given, or the bottom rung.</summary>
    public static int Down(int from)
    {
        for (int at = Rungs.Length - 1; at > 0; at--)
        {
            if (Rungs[at] <= from)
            {
                return Rungs[at - 1];
            }
        }

        return Rungs[0];
    }

    /// <summary>
    /// A width in pixels as a whole number of them, rounded UP.
    /// </summary>
    /// <remarks>
    /// Up, because a rung is a ceiling: rounding a width of 384.4 down would draw it at 384 and
    /// leave ImGui stretching the picture over the missing half pixel.
    /// </remarks>
    private static int Asked(float side)
        => float.IsFinite(side) ? (int)MathF.Ceiling(Math.Max(side, 0f)) : Smallest;
}
