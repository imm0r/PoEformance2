using System.Text.Json.Serialization;
using PoEformance.Game.Ui;

namespace PoEformance.Features;

/// <summary>
/// One part of the screen the map overlay must keep off, as fractions of the game window.
/// </summary>
/// <remarks>
/// FRACTIONS RATHER THAN PIXELS, because the thing being described is a piece of the game's
/// interface and the interface is laid out proportionally: the same numbers hold when the
/// window is resized, when the game is moved to another monitor, and when somebody plays
/// windowed. Pixels would be a setting that silently stops meaning what it meant.
/// </remarks>
/// <param name="Name">What it is covering, for the editor's list and the readout.</param>
/// <param name="On">
/// Whether it is honoured. Kept rather than deleted so somebody can see what a zone was doing
/// by switching it off for a moment, which is the only way to check one covers the right thing.
/// </param>
public sealed record MapKeepOutZone(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("left")] float Left,
    [property: JsonPropertyName("top")] float Top,
    [property: JsonPropertyName("right")] float Right,
    [property: JsonPropertyName("bottom")] float Bottom,
    [property: JsonPropertyName("on")] bool On = true)
{
    /// <summary>Where this zone lands on a window of the given size.</summary>
    public ScreenRect Placed(float windowWidth, float windowHeight)
        => new(Left * windowWidth, Top * windowHeight, Right * windowWidth, Bottom * windowHeight);

    /// <summary>The same zone, moved to where somebody dragged it on a window that size.</summary>
    /// <remarks>
    /// CLAMPED TO THE WINDOW on the way in. A zone dragged half off the screen would be stored
    /// as a fraction outside 0..1, which reads as a corrupt settings file the next time
    /// somebody opens it - and describes nothing the region could not have said with an edge
    /// exactly on the boundary.
    /// </remarks>
    public MapKeepOutZone MovedTo(ScreenRect pixels, float windowWidth, float windowHeight)
    {
        if (windowWidth < 1f || windowHeight < 1f || !pixels.IsSane)
        {
            return this;
        }

        static float Part(float value, float of) => Math.Clamp(value / of, 0f, 1f);

        return this with
        {
            Left = Part(pixels.Left, windowWidth),
            Top = Part(pixels.Top, windowHeight),
            Right = Part(pixels.Right, windowWidth),
            Bottom = Part(pixels.Bottom, windowHeight),
        };
    }
}

/// <summary>
/// The parts of the screen the game's own interface owns, which the map overlay stays off.
/// </summary>
/// <remarks>
/// WHAT THIS IS FOR. The game's large map is drawn across the ENTIRE window - it has no frame
/// and no viewport - and the interface is drawn on top of it: both orbs, the flask and skill
/// bars, the experience strip, an open inventory. Everything this tool draws on that map goes
/// through the same projection, so without this it lands on the interface too, and a terrain
/// outline over the life orb hides the one number a player is actually watching. The overlay
/// sits above the game and cannot be painted under anything, so the only way to be underneath
/// the interface is to not be there at all.
///
/// WHY THESE ARE SETTINGS AND NOT MEASUREMENTS, which is the honest part. Nothing in the game's
/// memory that this project has found names the pieces of the HUD: <c>ImportantUiElements</c>
/// carries the panels and the maps and stops there, and the reference tool has no equivalent
/// either - GameHelper2's Radar solves exactly this problem with a rectangle the user drags
/// once and it remembers (its "culling window"). So this is the same bargain, with the shape
/// generalised from one rectangle to a few, and the numbers below are eyeballed from the game
/// rather than read out of it. They are marked as such, they are editable, and if an element
/// that measures the HUD is ever identified they should be replaced by it.
///
/// WHY THE DEFAULT IS ONE BAND rather than the HUD's real silhouette. Every part of PoE2's
/// interface that sits over the map runs along the BOTTOM edge - the orbs at the two corners,
/// the flasks and skills between them, the experience strip under all of it - so one band
/// across the bottom covers the lot, and its only guessed number is where the top of it goes.
/// A default carved into the orb-shaped and bar-shaped pieces would keep more of the map, but
/// it would be four guesses instead of one and every one of them wrong on a different aspect
/// ratio. The editor is there to carve it for the screen it is actually on.
///
/// An open panel is a different matter and is NOT listed here: those the tool can measure, so
/// they are added to the region at draw time from <c>PanelArea</c>.
/// </remarks>
public sealed record MapKeepOut(
    [property: JsonPropertyName("on")] bool On = true,
    [property: JsonPropertyName("zones")] IReadOnlyList<MapKeepOutZone>? Zones = null)
{
    /// <summary>
    /// Where the interface sits until somebody says otherwise: one band across the bottom.
    /// </summary>
    /// <remarks>
    /// The top edge is the tallest thing down there, which is the orbs - measured off a
    /// screenshot of the game at 16:9, where their upper arc starts a little under four
    /// fifths of the way down. Deliberately a whisker generous: a band a few pixels too tall
    /// costs a strip of map nobody was reading, and one a few pixels too short puts an outline
    /// across the orb it was supposed to clear.
    /// </remarks>
    public static MapKeepOut Default { get; } = new(
        On: true,
        Zones: [new MapKeepOutZone("interface (bottom of the screen)", 0f, 0.80f, 1f, 1f)]);

    /// <summary>The zones as edited, or the default set for a file that has never said.</summary>
    /// <remarks>
    /// A file that has said "none" gets none: an EMPTY list is somebody having deleted every
    /// zone, which is a decision, while a MISSING one is a file written before this existed.
    /// Folding the two together would make "I want the whole window" impossible to save.
    /// </remarks>
    public IReadOnlyList<MapKeepOutZone> ZonesOrDefault => Zones ?? Default.Zones ?? [];

    /// <summary>
    /// The zones that are on, placed on a window of this size and ready for the region.
    /// </summary>
    /// <remarks>
    /// Empty when the whole thing is switched off, which is what makes the switch mean "draw
    /// over everything again" without the caller knowing anything about zones.
    /// </remarks>
    public List<ScreenRect> Blocking(float windowWidth, float windowHeight)
    {
        List<ScreenRect> blocking = [];
        if (!On || windowWidth < 1f || windowHeight < 1f)
        {
            return blocking;
        }

        foreach (MapKeepOutZone zone in ZonesOrDefault)
        {
            if (!zone.On)
            {
                continue;
            }

            ScreenRect rect = zone.Placed(windowWidth, windowHeight);
            if (rect.IsSane && rect.HasArea)
            {
                blocking.Add(rect);
            }
        }

        return blocking;
    }

    /// <summary>The same set with one zone replaced, for the editor.</summary>
    public MapKeepOut With(int index, MapKeepOutZone zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        List<MapKeepOutZone> zones = [.. ZonesOrDefault];
        if (index < 0 || index >= zones.Count)
        {
            return this;
        }

        zones[index] = zone;
        return this with { Zones = zones };
    }

    /// <summary>The same set with one more zone in the middle of the screen to drag off.</summary>
    /// <remarks>
    /// Placed AT THE CENTRE rather than at an edge, and that is not arbitrary: a new zone lands
    /// where it is impossible to miss. One created at the edge it is probably destined for is a
    /// zone somebody adds, cannot see, and adds again.
    /// </remarks>
    public MapKeepOut Plus()
    {
        List<MapKeepOutZone> zones = [.. ZonesOrDefault];
        if (zones.Count >= ScreenRegion.MostKeptOut)
        {
            return this;
        }

        zones.Add(new MapKeepOutZone($"zone {zones.Count + 1}", 0.4f, 0.4f, 0.6f, 0.6f));
        return this with { Zones = zones };
    }

    /// <summary>The same set without one zone.</summary>
    public MapKeepOut Less(int index)
    {
        List<MapKeepOutZone> zones = [.. ZonesOrDefault];
        if (index < 0 || index >= zones.Count)
        {
            return this;
        }

        zones.RemoveAt(index);
        return this with { Zones = zones };
    }
}
