namespace PoEformance.Features;

/// <summary>
/// Every drawn thing the overlay has, with what can be changed about each.
/// </summary>
/// <remarks>
/// THE PROMISE THIS FILE MAKES: what is not listed here is not configurable. So the editor is
/// generated from this rather than written by hand, and adding something to the overlay is not
/// finished until it has an entry - which is the only arrangement where "all of it can be
/// changed" stays true rather than becoming true once and drifting.
///
/// The keys are stored in the user's style file, so they are permanent. Renaming one silently
/// discards whatever was set for it, which is the kind of loss nobody notices until they go
/// looking for a colour they chose months ago.
///
/// Defaults live here rather than in the file, so a colour that turns out badly can be
/// corrected in a release for everybody who never changed it.
/// </remarks>
public static class StyleCatalogue
{
    /// <summary>Packs a colour the way ImGui wants it - ABGR, with alpha.</summary>
    private static uint Rgb(byte r, byte g, byte b, byte a = 230)
        => ((uint)a << 24) | ((uint)b << 16) | ((uint)g << 8) | r;

    private const StyleTraits Marker = StyleTraits.Colour | StyleTraits.Scale | StyleTraits.Icon;
    private const StyleTraits Line = StyleTraits.Colour | StyleTraits.Width;

    /// <summary>Everything, in the order an editor should show it.</summary>
    public static IReadOnlyList<StyleEntry> Entries { get; } =
    [
        // ── Entities ────────────────────────────────────────────────────────
        new("entity.monster.normal", "Monsters", "Ordinary monster", Marker, Rgb(255, 64, 64)),
        new("entity.monster.magic", "Monsters", "Magic monster", Marker, Rgb(140, 166, 255)),
        new("entity.monster.rare", "Monsters", "Rare monster", Marker, Rgb(255, 242, 89)),
        new("entity.monster.unique", "Monsters", "Unique monster", Marker, Rgb(255, 128, 38)),

        new("entity.item.normal", "Drops", "Ordinary drop", Marker, Rgb(217, 217, 217)),
        new("entity.item.magic", "Drops", "Magic drop", Marker, Rgb(115, 140, 255)),
        new("entity.item.rare", "Drops", "Rare drop", Marker, Rgb(255, 242, 89)),
        new("entity.item.unique", "Drops", "Unique drop", Marker, Rgb(255, 140, 51)),
        new("entity.item.currency", "Drops", "Currency", Marker, Rgb(230, 191, 140)),

        new("entity.chest", "Other entities", "Chest", Marker, Rgb(255, 217, 51)),
        new("entity.npc", "Other entities", "NPC", Marker, Rgb(153, 230, 153)),
        new("entity.player", "Other entities", "Player", Marker, Rgb(77, 255, 77)),
        new("entity.outline", "Other entities", "Dot outline", Line, Rgb(0, 0, 0, 160)),
        new("entity.label", "Other entities", "Dot label", StyleTraits.Colour, Rgb(217, 217, 217)),

        // ── Places ──────────────────────────────────────────────────────────
        new("poi.marker", "Places", "Unrecognised marker", Marker, Rgb(217, 217, 242)),
        new("poi.portal", "Places", "Area transition", Marker, Rgb(115, 242, 255)),
        new("poi.waypoint", "Places", "Waypoint", Marker, Rgb(140, 191, 255)),
        new("poi.checkpoint", "Places", "Checkpoint", Marker, Rgb(153, 217, 153)),
        new("poi.chest", "Places", "Chest", Marker, Rgb(255, 217, 102)),
        new("poi.strongbox", "Places", "Strongbox", Marker, Rgb(255, 191, 77)),
        new("poi.shrine", "Places", "Shrine", Marker, Rgb(128, 255, 204)),
        new("poi.breach", "Places", "Breach", Marker, Rgb(191, 128, 255)),
        new("poi.ritual", "Places", "Ritual", Marker, Rgb(255, 102, 153)),
        new("poi.expedition", "Places", "Expedition", Marker, Rgb(230, 191, 128)),
        new("poi.essence", "Places", "Essence", Marker, Rgb(140, 217, 255)),
        new("poi.delirium", "Places", "Delirium", Marker, Rgb(217, 217, 255)),
        new("poi.npc", "Places", "NPC", Marker, Rgb(255, 242, 179)),
        new("poi.quest", "Places", "Quest objective", Marker, Rgb(255, 204, 64)),
        new("poi.boss", "Places", "Boss arena", Marker, Rgb(255, 102, 89)),
        new("poi.label", "Places", "Place name", StyleTraits.Colour, Rgb(230, 230, 240)),

        // ── Routes ──────────────────────────────────────────────────────────
        new("route.1", "Routes", "First route", Line, Rgb(89, 255, 191)),
        new("route.2", "Routes", "Second route", Line, Rgb(255, 140, 217)),
        new("route.3", "Routes", "Third route", Line, Rgb(255, 191, 77)),
        new("route.4", "Routes", "Fourth route", Line, Rgb(140, 204, 255)),
        new("route.5", "Routes", "Fifth route", Line, Rgb(204, 255, 102)),
        new("route.arrow", "Routes", "Direction arrows", StyleTraits.Scale, Rgb(255, 255, 255)),

        // ── The map itself ──────────────────────────────────────────────────
        new("terrain.outline", "Map", "Area layout", Line, Rgb(150, 200, 255)),
    ];

    /// <summary>The entries, grouped the way an editor shows them.</summary>
    public static IEnumerable<IGrouping<string, StyleEntry>> Grouped()
        => Entries.GroupBy(entry => entry.Group);

    /// <summary>The entry for a key, or null when there is none.</summary>
    public static StyleEntry? Find(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Entries.FirstOrDefault(entry => entry.Key == key);
    }

    /// <summary>The catalogue's colour for a key, or white when the key is unknown.</summary>
    /// <remarks>
    /// White rather than nothing: an unknown key means a drawn thing was given one that is not
    /// in the catalogue, and a glaring colour is how that gets noticed instead of silently
    /// drawing in black on a dark map.
    /// </remarks>
    public static uint Fallback(string key) => Find(key)?.Fallback ?? 0xFFFFFFFF;

    /// <summary>The key for a monster or drop of a given rarity.</summary>
    /// <remarks>
    /// Built rather than looked up, so a rarity that gains a value does not silently fall back
    /// to one colour for everything - it produces a key the catalogue does not have, which is
    /// visible.
    /// </remarks>
    public static string ForRarity(string prefix, PoEformance.Game.Components.ItemRarity rarity)
        => $"{prefix}.{rarity.ToString().ToLowerInvariant()}";

    /// <summary>The key for a place's shape.</summary>
    public static string ForGlyph(PoEformance.Game.World.PoiGlyph glyph)
        => $"poi.{glyph.ToString().ToLowerInvariant()}";

    /// <summary>The key for the nth route, counted from zero.</summary>
    public static string ForRoute(int slot) => $"route.{slot + 1}";
}
