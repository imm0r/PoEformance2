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
/// FOUND BY APPEARING, not by name, because THE GAME DOES NOT NAME IT. Read out of this tool's
/// own interface browser in game, 2026-08, with a map hovered: the panel is the element at child
/// path [22][17][1] - a grandchild of the world screen - with 17 children of its own, "Blooming
/// Field" and content_modifier_layout among them, measuring 658x194 at 771,614. Its StringId is
/// EMPTY, and so is its parent's: the parent is a bare anchor carrying the position the panel
/// pops up at, which is why the panel itself sits at relative 0,0 inside it. So there is no name
/// to match on, and matching on [22][17] instead would be an index into a list that a patch
/// reorders - the exact fragility the HUD is deliberately found by id to avoid, with the same
/// silent failure: the overlay draws across the panel while the readout calls the keep-out
/// healthy.
///
/// What there IS, every tick, is the measurement: the interface parts are re-measured anyway, so
/// the parts on screen while nothing is hovered are a free baseline, and a part that was not
/// there - or was not THERE, at that rectangle - while nothing was hovered is the panel the game
/// just put up. Both halves of that are needed because of how the anchor measures: it claims no
/// extent of its own, so InterfaceReader falls to the bounds of its visible children, which is
/// nothing at all while no panel is up (the part vanishes) and the panel's own rectangle while
/// one is (the part appears, somewhere new each time, since the anchor moves to the hovered map).
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
    private readonly Dictionary<ulong, ScreenRect> _idle = [];
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
                _idle[part.Address] = part.Where;
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
            // SOMEWHERE ELSE COUNTS AS NEW. The anchor the panel hangs in moves to whichever map
            // is hovered, so if it ever measures as a part in its own right while nothing is up,
            // its address alone would say "seen this one" about a rectangle 600 pixels away.
            // What is kept off is this tick's rectangle either way - the list is rebuilt every
            // frame from live positions - so a moved part is one the drawing is already avoiding.
            if (!_idle.TryGetValue(part.Address, out ScreenRect idle) || idle != part.Where)
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
