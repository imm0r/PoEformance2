using System.Text.Json.Serialization;
using PoEformance.Game.Ui;

namespace PoEformance.Features;

/// <summary>
/// One extra part of the screen the map overlay must keep off, as fractions of the game window.
/// </summary>
/// <remarks>
/// FRACTIONS RATHER THAN PIXELS, because the thing being described is a piece of the screen and
/// the same numbers have to hold when the window is resized, moved to another monitor, or played
/// windowed. Pixels would be a setting that silently stops meaning what it meant.
///
/// AN EXTRA, and the word is doing work. The game's own interface is MEASURED - see
/// <see cref="MapKeepOut"/> - so nothing here describes the orbs or the bars. A zone is for what
/// measurement cannot reach: another overlay parked over the game, a streaming widget, a part of
/// the interface whose element turns out to understate itself.
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
/// What the map overlay stays off: the game's own interface, and anything else somebody adds.
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
/// THE INTERFACE IS MEASURED, NOT DESCRIBED, and that correction is the whole point of this
/// type's shape. It first shipped as a set of hand-dragged boxes with a guessed default, on the
/// belief that nothing in memory named the pieces of the HUD - the reference tool has no
/// equivalent either, and GameHelper2's Radar solves the same problem with a "culling window"
/// the user drags once. That belief was simply wrong: the interface is one UiElement with
/// StringId "HUD" among the UI root's own children, and its parts are its children, each
/// carrying its own position and size. See <see cref="InterfaceReader"/>. So the zones below are no
/// longer where the orbs are; they are empty by default, and what they are for is whatever
/// measurement cannot reach.
///
/// A PART CAN BE SWITCHED OFF BY NAME, which is the one thing measurement needs from a setting.
/// Some of these parts are containers, and a container that reports a rectangle far larger than
/// what it draws would quietly eat the map - the atlas panel has form here, understating itself
/// by 733 pixels on an ultrawide. Naming the parts in the readout and letting one be switched
/// off turns that from a mystery into a click.
/// </remarks>
public sealed record MapKeepOut(
    [property: JsonPropertyName("on")] bool On = true,
    [property: JsonPropertyName("hud")] bool Hud = true,
    [property: JsonPropertyName("hudOff")] IReadOnlyList<string>? HudOff = null,
    [property: JsonPropertyName("zones")] IReadOnlyList<MapKeepOutZone>? Zones = null)
{
    /// <summary>Measure the interface, keep off it, and describe nothing by hand.</summary>
    public static MapKeepOut Default { get; } = new();

    /// <summary>The extra zones somebody drew, empty until somebody draws one.</summary>
    public IReadOnlyList<MapKeepOutZone> ZonesOrEmpty => Zones ?? [];

    /// <summary>The interface parts to ignore, by name. Empty until somebody switches one off.</summary>
    public IReadOnlyList<string> HudOffOrEmpty => HudOff ?? [];

    /// <summary>Whether a measured interface part is honoured.</summary>
    public bool Honours(string part)
        => Hud && !HudOffOrEmpty.Contains(part, StringComparer.Ordinal);

    /// <summary>The same set with one interface part switched on or off by name.</summary>
    public MapKeepOut Honouring(string part, bool on)
    {
        ArgumentNullException.ThrowIfNull(part);

        List<string> off = [.. HudOffOrEmpty.Where(name => !string.Equals(name, part, StringComparison.Ordinal))];
        if (!on)
        {
            off.Add(part);
        }

        return this with { HudOff = off };
    }

    /// <summary>
    /// The extra zones that are on, placed on a window of this size and ready for the region.
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

        foreach (MapKeepOutZone zone in ZonesOrEmpty)
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

        List<MapKeepOutZone> zones = [.. ZonesOrEmpty];
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
        List<MapKeepOutZone> zones = [.. ZonesOrEmpty];
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
        List<MapKeepOutZone> zones = [.. ZonesOrEmpty];
        if (index < 0 || index >= zones.Count)
        {
            return this;
        }

        zones.RemoveAt(index);
        return this with { Zones = zones };
    }
}
