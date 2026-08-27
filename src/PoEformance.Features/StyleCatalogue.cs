namespace PoEformance.Features;

/// <summary>
/// Every drawn thing the overlay has, with what can be changed about each.
/// </summary>
/// <remarks>
/// THE PROMISE THIS FILE MAKES: everything painted over the game is in here, and what is not
/// in here is not configurable. So the editor is generated from this rather than written by
/// hand, and drawing something new is not finished until it has an entry - which is the only
/// arrangement where "all of it can be changed" stays true rather than becoming true once and
/// then drifting as things get added.
///
/// The boundary is what is drawn ON THE GAME: markers, routes, the layout, the measuring aids.
/// The tool's own windows are not in it - their text has no icon and no line width, and they
/// take the interface theme like every other window. That is a real line rather than a
/// convenient one, and it is drawn here so it can be argued with.
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

        // Off by default in the overlay, but reachable from the kind filter - so they are
        // drawn, so they belong here. An entry for something switched off is cheap; a drawn
        // thing with no entry is the promise this file makes being false.
        new("entity.effect", "Other entities", "Effect", Marker, Rgb(179, 128, 255)),
        new("entity.terrain", "Other entities", "Terrain piece", Marker, Rgb(153, 153, 153)),
        new("entity.other", "Other entities", "Anything else", Marker, Rgb(204, 204, 204)),

        new("entity.outline", "Other entities", "Dot outline", Line, Rgb(0, 0, 0, 204)),
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
        new("map.unwalked", "Map", "Still to walk", StyleTraits.Colour | StyleTraits.Scale, Rgb(120, 150, 190, 140)),

        // The colour here is the HOT end of the ramp; the cool end is derived from it, so one
        // choice moves the whole scale rather than leaving somebody to match two ends by eye.
        // Scale sizes the patch, which is how a picture is made denser or airier.
        new("map.heat", "Map", "Heat map", StyleTraits.Colour | StyleTraits.Scale, Rgb(255, 80, 40, 190)),

        // ── Health bars ─────────────────────────────────────────────────────
        // The BAR takes its colour from the monster's rarity entry above, so that one choice
        // covers the dot and the bar together. What is here is everything the rarity cannot
        // say: the empty part, and the shield.
        new("healthbar", "Health bars", "Monster health bar", StyleTraits.Scale, Rgb(255, 64, 64)),
        new("healthbar.back", "Health bars", "Empty part", StyleTraits.Colour, Rgb(28, 18, 18, 200)),
        new("healthbar.shield", "Health bars", "Energy shield", StyleTraits.Colour, Rgb(140, 200, 255)),

        // ── Projectiles ─────────────────────────────────────────────────────
        // Two colours because there are two questions, and only one of them is "where is my
        // fireball". The other is which of the things crossing the screen is coming AT you,
        // and a single colour for both would answer neither. The trail takes its HUE from the
        // projectile it belongs to, for the same reason; this entry sets how thick it is and,
        // through its alpha, how strongly it shows.
        new("projectile.mine", "Projectiles", "Yours", Marker, Rgb(102, 224, 255)),
        new("projectile.other", "Projectiles", "Everything else's", Marker, Rgb(255, 122, 89)),
        new("projectile.trail", "Projectiles", "Where it came from", Line, Rgb(255, 255, 255, 128)),

        // The trail's HUE, by what the projectile appears to be. The mark itself keeps saying
        // whose it is, so the two together answer both questions at once: a cyan dot on a red
        // trail is your fireball. Which element a name means is a guess and can be wrong - see
        // ProjectileElements - so anything unrecognised keeps the mark's own colour rather than
        // taking one of these and stating something.
        new("projectile.fire", "Projectiles", "Fire trail", StyleTraits.Colour, Rgb(255, 106, 61)),
        new("projectile.cold", "Projectiles", "Cold trail", StyleTraits.Colour, Rgb(102, 179, 255)),
        new("projectile.lightning", "Projectiles", "Lightning trail", StyleTraits.Colour, Rgb(255, 224, 92)),
        new("projectile.chaos", "Projectiles", "Chaos trail", StyleTraits.Colour, Rgb(166, 122, 255)),
        new("projectile.physical", "Projectiles", "Physical trail", StyleTraits.Colour, Rgb(214, 214, 214)),

        // ── What the area loaded ────────────────────────────────────────────
        // The card and the list have separate backings because they are read at different
        // moments: the card interrupts on the way in, while the list sits there while you
        // decide whether to walk further. They are also the pair most likely to be moved out
        // of the way, so the list has its own switch - hiding it does not hide the card.
        // The three weights carry an ICON, which is what the entry card draws as its plate:
        // point one at a picture and the card wears it, leave it empty and the weight's name
        // is written instead. Scale is the plate's width, so one weight can be made to shout.
        new("preload.banner.back", "What the area loaded", "Entry line backing", StyleTraits.Colour, Rgb(14, 20, 28, 215)),
        new("preload.list.back", "What the area loaded", "List backing", StyleTraits.Colour, Rgb(14, 20, 28, 170)),
        // ONE plate rather than three. There used to be one per weight, back when an entry's
        // colour came from a weight; entries now carry their own colour, so what is left for the
        // catalogue is the card's plate itself - its icon, and how wide it is drawn.
        new("preload.card", "What the area loaded", "Entry card plate", Marker, Rgb(255, 217, 102)),

        // ── The atlas ───────────────────────────────────────────────────────
        // A map's own colour comes from the GROUP it is in, and that is set beside the group
        // rather than here: there is one per group and they are somebody's list, not this
        // file's. What is here is what a map with NO group uses, plus the parts no group can
        // colour - the plate, the connections, and the dot on the near end of a route.
        new("atlas.label", "The atlas", "Map name", StyleTraits.Colour, Rgb(220, 225, 235, 255)),
        new("atlas.plate", "The atlas", "Label backing", StyleTraits.Colour, Rgb(0, 0, 0, 204)),
        new("atlas.content", "The atlas", "What is in a map", StyleTraits.Colour, Rgb(184, 194, 214, 255)),
        new("atlas.web", "The atlas", "Connections", Line, Rgb(255, 255, 255, 56)),
        new("atlas.route", "The atlas", "Route", Line, Rgb(255, 255, 255, 140)),
        new("atlas.entry", "The atlas", "Where a route starts", Marker, Rgb(51, 255, 51, 255)),

        // ── Measuring aids ──────────────────────────────────────────────────
        // Switched on with the calibration and browser tools rather than during play, but
        // they are drawn over the game like everything above, so leaving them out would make
        // this file's promise false. They are also the entries most worth changing: their job
        // is to be TOLD APART from what they are measured against, and what that takes
        // depends on the map somebody is standing on.
        new("aid.centre", "Measuring aids", "Screen centre", Line, Rgb(77, 230, 255)),
        new("aid.ground", "Measuring aids", "Ground marker", Line, Rgb(102, 255, 102)),
        new("aid.healthbar", "Measuring aids", "Health-bar marker", Line, Rgb(255, 102, 255)),
        new("aid.link", "Measuring aids", "Line between the two", Line, Rgb(255, 255, 255)),
        new("aid.selected", "Measuring aids", "Selected interface element", Line, Rgb(255, 64, 64)),
        new("aid.hovered", "Measuring aids", "Interface element under the cursor", Line, Rgb(89, 179, 255)),
    ];

    /// <summary>
    /// The keys the drawing code names directly, rather than builds.
    /// </summary>
    /// <remarks>
    /// Named so a mistyped one is a compile error. Spelled out at the point of use it would
    /// be a key the catalogue does not have, which draws WHITE - visible, but only in the
    /// game, and only to somebody who wonders why one marker looks wrong. There is a test
    /// that every one of these is a real entry, so the failure moves to the build.
    /// </remarks>
    public static class Keys
    {
        public const string DotOutline = "entity.outline";
        public const string DotLabel = "entity.label";
        public const string Player = "entity.player";
        public const string PlaceLabel = "poi.label";
        public const string RouteArrow = "route.arrow";
        public const string Terrain = "terrain.outline";
        public const string Unwalked = "map.unwalked";
        public const string Heat = "map.heat";
        public const string HealthBar = "healthbar";
        public const string HealthBarBack = "healthbar.back";
        public const string HealthBarShield = "healthbar.shield";
        public const string ProjectileMine = "projectile.mine";
        public const string ProjectileOther = "projectile.other";
        public const string ProjectileTrail = "projectile.trail";
        public const string PreloadBannerBack = "preload.banner.back";
        public const string PreloadListBack = "preload.list.back";
        public const string PreloadCard = "preload.card";
        public const string AtlasLabel = "atlas.label";
        public const string AtlasPlate = "atlas.plate";
        public const string AtlasContent = "atlas.content";
        public const string AtlasWeb = "atlas.web";
        public const string AtlasRoute = "atlas.route";
        public const string AtlasEntry = "atlas.entry";
        public const string AidCentre = "aid.centre";
        public const string AidGround = "aid.ground";
        public const string AidHealthbar = "aid.healthbar";
        public const string AidLink = "aid.link";
        public const string AidSelected = "aid.selected";
        public const string AidHovered = "aid.hovered";

        /// <summary>All of them, for the test that keeps them honest.</summary>
        public static IReadOnlyList<string> All { get; } =
        [
            DotOutline, DotLabel, Player, PlaceLabel, RouteArrow, Terrain, Unwalked, Heat,
            PreloadBannerBack, PreloadListBack, PreloadCard,
            AtlasLabel, AtlasPlate, AtlasContent, AtlasWeb, AtlasRoute, AtlasEntry,
            HealthBar, HealthBarBack, HealthBarShield,
            ProjectileMine, ProjectileOther, ProjectileTrail,
            AidCentre, AidGround, AidHealthbar, AidLink, AidSelected, AidHovered,
        ];
    }

    /// <summary>The entries, grouped the way an editor shows them.</summary>
    public static IEnumerable<IGrouping<string, StyleEntry>> Grouped()
        => Entries.GroupBy(entry => entry.Group);

    /// <summary>Which editor page each group of entries is shown on.</summary>
    /// <remarks>
    /// The style rows used to live on one page, all of them, and that page had become a wall:
    /// twelve groups of colour pickers, most of them about a feature configured somewhere
    /// else entirely. A feature's looks now sit WITH the feature - the atlas styles on the
    /// atlas tab, the preload styles on the area tab - and what is drawn on the game world
    /// itself, which has no feature tab, gets a page of its own.
    ///
    /// Declared HERE, beside the entries, so the same promise covers it: a test holds that
    /// every group in the catalogue is claimed by exactly one page, which is what keeps a
    /// newly added group from quietly appearing in no editor at all.
    /// </remarks>
    public static class Homes
    {
        /// <summary>On the area page, under the list they draw.</summary>
        public static readonly string[] Area = ["What the area loaded"];

        /// <summary>On the atlas page.</summary>
        public static readonly string[] Atlas = ["The atlas"];

        /// <summary>On the combat page, beside the projectile switches.</summary>
        public static readonly string[] Projectiles = ["Projectiles"];

        /// <summary>
        /// Everything drawn on the game world itself - the markers, the routes, the layout,
        /// the measuring aids. These have no feature tab to live on, so they share one page.
        /// </summary>
        public static readonly string[] Markers =
        [
            "Monsters",
            "Drops",
            "Other entities",
            "Places",
            "Routes",
            "Map",
            "Health bars",
            "Measuring aids",
        ];

        /// <summary>Every page's claim, for the test that keeps the mapping complete.</summary>
        public static IEnumerable<string> All()
            => Area.Concat(Atlas).Concat(Projectiles).Concat(Markers);
    }

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
    /// A rarity that is not one of the four ranks takes the ordinary one, which is what the
    /// overlay drew before any of this existed: an unresolved drop is one frame old at most,
    /// and it is shown rather than hidden because missing a unique costs more than glancing
    /// at a white one. Currency is a rank here even though it is a classification elsewhere,
    /// because it has a colour of its own that people know.
    ///
    /// Folding the rest in rather than building a key for them is deliberate: an unmapped key
    /// draws WHITE to make a forgotten catalogue entry visible, and a rarity nobody chose is
    /// not a forgotten entry.
    /// </remarks>
    public static string ForRarity(string prefix, PoEformance.Game.Components.ItemRarity rarity)
    {
        string rank = rarity switch
        {
            PoEformance.Game.Components.ItemRarity.Magic => "magic",
            PoEformance.Game.Components.ItemRarity.Rare => "rare",
            PoEformance.Game.Components.ItemRarity.Unique => "unique",
            PoEformance.Game.Components.ItemRarity.Currency => "currency",
            _ => "normal",
        };

        return $"{prefix}.{rank}";
    }

    /// <summary>
    /// The key a projectile's trail is coloured from, or empty when its element is unknown.
    /// </summary>
    /// <remarks>
    /// EMPTY rather than a "projectile.unknown" entry, and the difference is the point. An
    /// entry would be a colour, and a colour is a claim - it would say "this one is its own
    /// sort of thing" about a projectile whose name simply did not match a keyword. Empty
    /// sends the trail back to the mark's own colour, which is what it was before anybody
    /// tried to read an element out of a name.
    /// </remarks>
    public static string ForElement(ProjectileElement element) => element switch
    {
        ProjectileElement.Fire => "projectile.fire",
        ProjectileElement.Cold => "projectile.cold",
        ProjectileElement.Lightning => "projectile.lightning",
        ProjectileElement.Chaos => "projectile.chaos",
        ProjectileElement.Physical => "projectile.physical",
        _ => "",
    };

    /// <summary>The key for a place's shape.</summary>
    public static string ForGlyph(PoEformance.Game.World.PoiGlyph glyph)
        => $"poi.{glyph.ToString().ToLowerInvariant()}";

    /// <summary>The key for an entity that is not a monster or a drop.</summary>
    public static string ForKind(PoEformance.Game.World.EntityKind kind) => kind switch
    {
        PoEformance.Game.World.EntityKind.Player => "entity.player",
        PoEformance.Game.World.EntityKind.Monster => "entity.monster.normal",
        PoEformance.Game.World.EntityKind.Chest => "entity.chest",
        PoEformance.Game.World.EntityKind.WorldItem => "entity.item.normal",
        PoEformance.Game.World.EntityKind.Npc => "entity.npc",
        PoEformance.Game.World.EntityKind.Effect => "entity.effect",
        PoEformance.Game.World.EntityKind.Terrain => "entity.terrain",
        _ => "entity.other",
    };

    /// <summary>The key for the nth route, counted from zero.</summary>
    public static string ForRoute(int slot) => $"route.{slot + 1}";
}
