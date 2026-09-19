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
/// a real rig cost 3.0 ms a frame on one thread, at 768 it cost 8.0 and at 1536 it cost 25.1. A
/// cap one rung lower is a quarter of the work, not a little less of it, and the cap is paid on
/// every frame a model moves.
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
    /// <summary>The default cap on what MOVING the model may cost, as a size.</summary>
    /// <remarks>
    /// THE RUNG A 1200 PX PANE PLAYS AT NEAR ENOUGH TO ITS OWN SIZE, which is the pane the live
    /// client showed: at 1024 the picture is stretched by a sixth, where the 512 it used to play
    /// at was stretched by more than two and reported as a blur. What it costs is measured in
    /// MeshPicture: the rasteriser draws in bands on every core, and on four of them a rig-sized
    /// mesh filling the whole frame is 19 ms at 1024 against 9 at 512 - a real monster covers a
    /// third of the frame, and a desktop has more cores than that. It is not a cap on how big the
    /// picture may BE - see <see cref="For"/>. See also OverlaySettings.
    /// </remarks>
    public const int Usual = 1024;

    /// <summary>The smallest a cap may be set to, and the bottom rung.</summary>
    public const int Smallest = 256;

    private static readonly int[] Rungs = [Smallest, 384, 512, 768, Usual, 1536, MeshPicture.Widest];

    /// <param name="most">The biggest size to allow, clamped and snapped to a rung.</param>
    public PictureLadder(int most = Usual)
        => Most = Snap(Math.Clamp(most, Smallest, MeshPicture.Widest));

    /// <summary>Every size a model is ever drawn at.</summary>
    public static IReadOnlyList<int> Steps => Rungs;

    /// <summary>The biggest size this ladder allows.</summary>
    public int Most { get; }

    /// <summary>What to draw at for a picture shown this wide, at rest: the cap does not apply.</summary>
    /// <remarks>
    /// THE CAP DOES NOT APPLY HERE, and that is what the measurement actually said. What was
    /// measured is the cost of a frame WHILE MOVING, and a cost per frame only matters when there
    /// are frames: at rest the picture is drawn ONCE, when the monster changes or the drag ends,
    /// and one slow frame is not something anybody sees. A cap on the resting size buys nothing
    /// and shows as a picture that stops growing with its pane, which is what it was reported as.
    /// </remarks>
    public int For(float side) => Snap(Asked(side));

    /// <summary>
    /// What a model that is moving - dragged, or playing an animation - is drawn at: what the pane asks for, up to the cap.
    /// </summary>
    /// <remarks>
    /// THE CAP, AND NOTHING UNDER IT ANY MORE. This used to be one rung under the cap as well, the
    /// four-times discount that made a drag smooth while the rasteriser ran on one thread - and it
    /// was what the live client saw as a blurred monster on a 1200 px pane, drawn at 512 and
    /// stretched. The rasteriser draws in bands on every core now, and what that bought is spent
    /// here: a moving picture at the pane's own rung. The cap is still somebody's answer to "how
    /// much processor may moving this cost", and this is still the only place it is paid.
    /// </remarks>
    public int Moving(float side) => Math.Min(For(side), Most);

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
