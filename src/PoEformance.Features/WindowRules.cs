using System.Text.Json.Serialization;

namespace PoEformance.Features;

/// <summary>
/// How one overlay window behaves once it is where somebody wants it.
/// </summary>
/// <remarks>
/// TWO SEPARATE THINGS, though click-through implies the first. A LOCKED window still takes
/// the mouse - its buttons work, it just cannot be dragged out of place by a stray click,
/// which is what happens to a window parked over the middle of the screen. A CLICK-THROUGH
/// one is not there as far as the mouse is concerned: the click lands on the game behind it,
/// which is what a readout wants and what anything with a button in it does not.
///
/// Per window rather than one switch for the overlay, because the answer differs per window
/// by its nature: a readout somebody glances at wants both, and the window they are picking
/// routes in wants neither.
/// </remarks>
/// <param name="Locked">Cannot be dragged. Still clickable.</param>
/// <param name="ClickThrough">The mouse does not see it at all; clicks reach the game.</param>
public sealed record WindowRule(
    [property: JsonPropertyName("locked")] bool Locked = false,
    [property: JsonPropertyName("clickThrough")] bool ClickThrough = false)
{
    /// <summary>Draggable and clickable, which is what a window starts as.</summary>
    public static WindowRule Free { get; } = new();

    /// <summary>Whether this says anything at all, so the settings need not store it.</summary>
    public bool Anything => Locked || ClickThrough;
}
