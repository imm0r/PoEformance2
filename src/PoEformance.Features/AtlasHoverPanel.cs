using PoEformance.Game.Ui;

namespace PoEformance.Features;

/// <summary>
/// Finds the panel the game puts up while the cursor is on a map, so the atlas overlay can keep
/// off THAT rather than switch itself off.
/// </summary>
/// <remarks>
/// WHAT THIS REPLACES. Hovering a map made the whole atlas overlay disappear - every label, every
/// route, every line, across the entire screen - because the game draws its own panel about that
/// map (its name, its biome, what is in it) and anything drawn over that panel is worse than
/// nothing. That was the right answer while the overlay had no way to keep off part of the
/// screen. It now has one, and it is the same one the orbs, the title bar and the search box get:
/// the panel is measured and the drawing goes round it.
///
/// FOUND BY APPEARING, not by name, and that is deliberate rather than lazy. The interface parts
/// are re-measured every read tick anyway, so the parts on screen while nothing is hovered are a
/// free baseline - and a part that is only there while the cursor is on a map is the panel the
/// game just put up. Naming it would mean hard-coding a StringId nobody here has read yet, and
/// the failure of a wrong name is silent: the overlay would draw across the panel while the
/// readout claimed the keep-out was healthy.
///
/// The rectangle itself needs no extra work: the panel is an ordinary interface part, so it is
/// already in the keep-out list this was handed. Finding it only answers whether the fallback is
/// needed - and <see cref="Shown"/> is there so the readout can NAME the part, which is what
/// turns "the overlay draws over the tooltip" into a line somebody can read instead of another
/// round of screenshots.
///
/// FAILS TOWARDS THE OLD BEHAVIOUR, like every other unreadable answer in this tool - except that
/// here the safe direction is the other way round. Drawing across the game's own panel is the
/// thing being fixed, so a frame where the panel cannot be accounted for hides the overlay just
/// as before. Nothing gets worse than it was; it only gets better when the panel is measured.
/// </remarks>
public sealed class AtlasHoverPanel
{
    private readonly HashSet<ulong> _idle = [];
    private readonly List<InterfacePart> _shown = [];
    private bool _open;
    private bool _settled;

    /// <summary>The parts that came up with the hover - the game's panel, and its trimmings.</summary>
    public IReadOnlyList<InterfacePart> Shown => _shown;

    /// <summary>Whether the game's panel is accounted for, so the overlay may go on drawing.</summary>
    public bool Found => _shown.Count > 0;

    /// <summary>
    /// Whether a baseline exists at all: what the atlas screen looks like with nothing hovered.
    /// </summary>
    /// <remarks>
    /// Reported because it is the one honest reason for hiding that is not a failure. Opening the
    /// atlas with the cursor already on a map - which is what pressing the key with a still mouse
    /// does - gives this nothing to compare against, so it hides until the cursor leaves a node
    /// once. That is a frame or two of the old behaviour, not a broken state, and a readout that
    /// could not tell the two apart would send somebody hunting for a bug in the measurement.
    /// </remarks>
    public bool Settled => _settled;

    /// <summary>
    /// One tick's reading: what is on screen, and whether a map is under the cursor.
    /// </summary>
    /// <param name="open">
    /// Whether the atlas is open. The baseline is per-screen - the HUD's parts are nothing like
    /// the atlas screen's - so opening or closing the atlas throws it away rather than letting
    /// every piece of the new screen read as something the hover brought up.
    /// </param>
    /// <param name="hovering">Whether the cursor is on a map right now.</param>
    /// <param name="keptOut">
    /// The interface parts the overlay is ACTUALLY keeping off this tick, after the master switch
    /// and the per-part switches have had their say. Actually rather than measured, because a
    /// part somebody switched off is not covering anything as far as the drawing is concerned:
    /// finding the panel there and going on drawing would put the labels straight across it.
    /// </param>
    public void Look(bool open, bool hovering, IReadOnlyList<InterfacePart> keptOut)
    {
        ArgumentNullException.ThrowIfNull(keptOut);

        if (open != _open)
        {
            _open = open;
            _settled = false;
            _idle.Clear();
        }

        _shown.Clear();

        if (!hovering)
        {
            // The baseline, taken fresh every tick nothing is hovered: the atlas screen's own
            // furniture comes and goes with what the player is doing - the pin editor, the
            // search box, a region's buttons - and a baseline taken once would report every one
            // of those as the panel the hover brought up.
            _idle.Clear();
            foreach (InterfacePart part in keptOut)
            {
                _idle.Add(part.Address);
            }

            _settled = open;
            return;
        }

        if (!_settled)
        {
            return;
        }

        foreach (InterfacePart part in keptOut)
        {
            if (!_idle.Contains(part.Address))
            {
                _shown.Add(part);
            }
        }
    }

    /// <summary>What happened, in one line, for the readout.</summary>
    public string Describe(ScreenRect? hovered)
    {
        if (hovered is not ScreenRect map)
        {
            return "no map under the cursor";
        }

        string on = $"on the map at {map.Left:F0},{map.Top:F0} ({map.Width:F0}x{map.Height:F0})";

        if (Found)
        {
            return $"{on} - the game's panel is {string.Join(", ", _shown.Select(Named))}, kept off";
        }

        return _settled
            ? $"{on} - NO part of the interface came up with it, so the overlay hides instead"
            : $"{on} - nothing hovered has been seen yet on this screen, so the overlay hides";
    }

    private static string Named(InterfacePart part)
        => $"{part.Label} at {part.Where.Left:F0},{part.Where.Top:F0}"
           + $" ({part.Where.Width:F0}x{part.Where.Height:F0})";
}
