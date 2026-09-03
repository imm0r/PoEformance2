namespace PoEformance.Game.Ui;

/// <summary>
/// Chooses which of a set of labels can be written without landing on each other.
/// </summary>
/// <remarks>
/// WHAT THIS REPLACES IS A NUMBER NOBODY CAN PICK. The first attempt at keeping the room names
/// readable was a threshold in tiles, and the first real area showed why that cannot work: a
/// zone is built from ONE module repeated, so nearly every room is the same size. At nine tiles
/// the map was solid text; at ten, four labels survived. There is no setting between those two
/// pictures, because there is no room between them.
///
/// Space on screen is the honest constraint, and it is the one that moves with the zoom: at a
/// distance only the few names that fit are written, and zooming in makes room for the rest
/// without anybody touching a slider.
///
/// FIRST OFFERED WINS, so the caller's order IS the priority - see TerrainRooms.Ranked, which
/// offers the rarest room first. A packer that decided importance for itself would be deciding
/// it from geometry, which knows nothing about what the labels say.
///
/// Quadratic in what is KEPT rather than in what is offered, which is what makes it affordable
/// per frame: a screen holds a few dozen labels, and every candidate past that is rejected by
/// the first overlap it hits.
/// </remarks>
public static class LabelPacking
{
    /// <summary>
    /// Picks the labels that fit, in the order they were offered.
    /// </summary>
    /// <param name="candidates">Where each label wants to go, most important first.</param>
    /// <param name="kept">
    /// Filled with the indices that fit. Cleared first, and supplied by the caller so a per-frame
    /// packing costs no allocation.
    /// </param>
    /// <param name="padding">
    /// How much clear space each label keeps around it. Zero packs labels edge to edge, which is
    /// legible only in the sense that no glyph sits on another.
    /// </param>
    public static void Keep(
        IReadOnlyList<ScreenRect> candidates, List<int> kept, float padding = 2f)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(kept);

        kept.Clear();

        for (int i = 0; i < candidates.Count; i++)
        {
            ScreenRect want = Grown(candidates[i], padding);

            bool free = true;
            for (int j = 0; j < kept.Count && free; j++)
            {
                free = !want.Overlaps(candidates[kept[j]]);
            }

            if (free)
            {
                kept.Add(i);
            }
        }
    }

    /// <summary>
    /// The rectangle a label claims, which is bigger than the label by its padding.
    /// </summary>
    /// <remarks>
    /// Grown on ONE side of the comparison rather than both. Padding is the gap between two
    /// labels, and growing each of them by it would leave twice that - which reads as a map
    /// with far fewer names on it than there is room for.
    /// </remarks>
    private static ScreenRect Grown(ScreenRect rect, float padding)
        => padding <= 0f
            ? rect
            : new ScreenRect(
                rect.Left - padding, rect.Top - padding,
                rect.Right + padding, rect.Bottom + padding);
}
