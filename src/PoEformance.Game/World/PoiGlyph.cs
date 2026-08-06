namespace PoEformance.Game.World;

/// <summary>The shape a marker is drawn as.</summary>
/// <remarks>
/// Separate from <see cref="PoiKind"/ > on purpose, because the two answer different questions.
/// The kind decides BEHAVIOUR - whether a place is routed to, listed, filtered. The glyph only
/// decides what it looks like, and there are far more things worth telling apart by eye than
/// there are behaviours: a breach and a ritual are both "a mechanic is here" and route
/// identically, while being the two markers you most want to distinguish at a glance.
/// </remarks>
public enum PoiGlyph
{
    /// <summary>A diamond. The game marked this and nothing more specific is known.</summary>
    Marker,

    /// <summary>An arch: a way out of the area.</summary>
    Portal,

    /// <summary>A ring with a centre: the waypoint network.</summary>
    Waypoint,

    /// <summary>A flag on a pole.</summary>
    Checkpoint,

    /// <summary>A box with a lid.</summary>
    Chest,

    /// <summary>A box that is locked, and fights back.</summary>
    Strongbox,

    /// <summary>A burst: something that radiates while you stand in it.</summary>
    Shrine,

    /// <summary>A hexagon with spokes: a hole opening into somewhere else.</summary>
    Breach,

    /// <summary>Concentric circles: an altar you fight around.</summary>
    Ritual,

    /// <summary>A pentagon: a placed marker, waiting to go off.</summary>
    Expedition,

    /// <summary>An outlined diamond.</summary>
    Essence,

    /// <summary>Overlapping circles: the fog.</summary>
    Delirium,

    /// <summary>A figure.</summary>
    Npc,

    /// <summary>A bar over a dot, the way the game asks for attention.</summary>
    Quest,

    /// <summary>A spiked star. Something large lives here.</summary>
    Boss,
}

/// <summary>
/// Chooses the shape a place is drawn as.
/// </summary>
/// <remarks>
/// The game's own icon NAME decides wherever there is one, which is the same principle the
/// kinds already follow: MinimapIcons.dat says "Breach" or "Ritual" or "ExpeditionMarker", and
/// that is the game answering rather than this guessing from a metadata path. The kind is the
/// fallback for entities the game does not mark - a terrain boss arena has no icon at all,
/// because at the moment it is found there is nothing standing in it.
///
/// Names are matched loosely and the list is deliberately short. There are hundreds of icon
/// names, most of them one encounter's reward chest, and enumerating them is a list that goes
/// stale every league; an unrecognised name still draws as a marker, which is honest - the
/// game marked something and this does not know what.
/// </remarks>
public static class PoiGlyphs
{
    /// <summary>The shape for a place, from the game's icon name where there is one.</summary>
    public static PoiGlyph For(string icon, PoiKind kind)
    {
        ArgumentNullException.ThrowIfNull(icon);

        PoiGlyph named = FromName(icon);
        return named != PoiGlyph.Marker ? named : FromKind(kind);
    }

    /// <summary>The shape a game icon name asks for, or <see cref="PoiGlyph.Marker"/>.</summary>
    private static PoiGlyph FromName(string icon)
    {
        // WHAT IT IS beats which mechanic it belongs to, which is why the containers come
        // first: "RewardChestExpedition" is a chest, and a chest is a thing you walk over and
        // open. Knowing it belongs to the expedition is not news by the time you are standing
        // in one.
        if (Has(icon, "strongbox"))
        {
            return PoiGlyph.Strongbox;
        }

        if (Has(icon, "chest"))
        {
            return PoiGlyph.Chest;
        }

        if (Has(icon, "breach"))
        {
            return PoiGlyph.Breach;
        }

        if (Has(icon, "ritual"))
        {
            return PoiGlyph.Ritual;
        }

        if (Has(icon, "expedition"))
        {
            return PoiGlyph.Expedition;
        }

        if (Has(icon, "essence"))
        {
            return PoiGlyph.Essence;
        }

        if (Has(icon, "delirium") || Has(icon, "mirror"))
        {
            return PoiGlyph.Delirium;
        }

        if (Has(icon, "shrine"))
        {
            return PoiGlyph.Shrine;
        }

        if (Has(icon, "waypoint"))
        {
            return PoiGlyph.Waypoint;
        }

        if (Has(icon, "checkpoint"))
        {
            return PoiGlyph.Checkpoint;
        }

        if (Has(icon, "transition") || Has(icon, "portal") || Has(icon, "entrance") || Has(icon, "exit"))
        {
            return PoiGlyph.Portal;
        }

        if (Has(icon, "quest") || Has(icon, "objective"))
        {
            return PoiGlyph.Quest;
        }

        if (Has(icon, "npc") || Has(icon, "vendor") || Has(icon, "master"))
        {
            return PoiGlyph.Npc;
        }

        return PoiGlyph.Marker;
    }

    /// <summary>The shape for a place the game does not mark itself.</summary>
    private static PoiGlyph FromKind(PoiKind kind) => kind switch
    {
        PoiKind.AreaTransition => PoiGlyph.Portal,
        PoiKind.Waypoint => PoiGlyph.Waypoint,
        PoiKind.Checkpoint => PoiGlyph.Checkpoint,
        PoiKind.Chest => PoiGlyph.Chest,
        PoiKind.Shrine => PoiGlyph.Shrine,
        PoiKind.Npc => PoiGlyph.Npc,
        PoiKind.Quest => PoiGlyph.Quest,
        PoiKind.BossArena => PoiGlyph.Boss,
        _ => PoiGlyph.Marker,
    };

    private static bool Has(string text, string word)
        => text.Contains(word, StringComparison.OrdinalIgnoreCase);
}
