using System.Text.Json;
using System.Text.Json.Serialization;
using PoEformance.Game.Components;

namespace PoEformance.Features;

/// <summary>What the user decides about the overlay.</summary>
/// <remarks>
/// Separate from the flask settings and in its own file, one section per concern: the point
/// of a settings file is that a person can open it, and a single bag of everything stops
/// being that as soon as it has two features in it.
/// </remarks>
public sealed record OverlaySettings(
    [property: JsonPropertyName("minLootRarity")] ItemRarity MinLootRarity,
    [property: JsonPropertyName("showTerrain")] bool ShowTerrain = true,
    [property: JsonPropertyName("terrainColour")] string TerrainColour = "#96C8FF",
    [property: JsonPropertyName("terrainThickness")] int TerrainThickness = 1,

    // The dark rim around the layout's line. ON by default: the line is one colour, the
    // ground under it is every colour, and without the rim it vanishes on whichever ground
    // happens to match - see TerrainOutline.Rim.
    [property: JsonPropertyName("terrainRim")] bool TerrainRim = true,
    [property: JsonPropertyName("hideNoise")] bool HideNoise = true,
    [property: JsonPropertyName("rememberOutOfRange")] bool RememberOutOfRange = true,
    [property: JsonPropertyName("showPoi")] bool ShowPoi = true,
    [property: JsonPropertyName("poiLabels")] bool PoiLabels = true,
    [property: JsonPropertyName("poiRoutes")] bool PoiRoutes = true,
    [property: JsonPropertyName("poiArrows")] bool PoiArrows = true,
    [property: JsonPropertyName("dotLabels")] bool DotLabels = false,
    [property: JsonPropertyName("healthBarsOnlyWhenHurt")] bool HealthBarsOnlyWhenHurt = false,
    [property: JsonPropertyName("hideBehindPanels")] bool HideBehindPanels = true,

    // The same question for the tool's OWN windows, and its own key rather than a second use
    // of the one above: hiding a marker layer costs nothing, while hiding a window takes its
    // controls with it - so somebody may well want the one and not the other. Only the windows
    // actually lying over an open panel go; see WindowChrome.Covered.
    [property: JsonPropertyName("hideWindowsBehindPanels")] bool HideWindowsBehindPanels = true,

    // Where the game's own interface sits, so the map overlay stays off it. Its own object
    // rather than four numbers out here, for the same reason the interface style below is:
    // a settings file stops being readable at the point everything shares one level. Null
    // until somebody drags a zone, so an untouched file gains no key and the default stays
    // where a release can correct it. See MapKeepOut - including why it is a setting at all.
    [property: JsonPropertyName("mapKeepOut")] MapKeepOut? MapKeepOut = null,

    // The layout's room names, and which rooms somebody pinned - its own object beside the
    // keep-out zones, and null until somebody touches it, for exactly the same reasons.
    [property: JsonPropertyName("rooms")] RoomSettings? Rooms = null,

    // What the ground under those rooms IS, which is a level above their file names. Its own
    // object beside them, and null until touched, for the same reasons.
    [property: JsonPropertyName("ground")] GroundSettings? Ground = null,

    // The projectile marks. On by default, because unlike the effect and terrain debug
    // layers this is a playing feature: it costs nothing extra to read - a projectile is
    // an entity the reader already walks past - and what it draws is small and brief.
    [property: JsonPropertyName("showProjectiles")] bool ShowProjectiles = true,
    [property: JsonPropertyName("projectileTrails")] bool ProjectileTrails = true,
    [property: JsonPropertyName("projectilePaths")] bool ProjectilePaths = false,
    [property: JsonPropertyName("projectilesMineOnly")] bool ProjectilesMineOnly = false,

    // The effect debug layer and the read behind it. OFF, unlike the projectiles above, because
    // this one costs: KeepEffects undoes the rule that stops a Firewall build covering its own
    // screen in enemy markers, and it only pays off with the visual entities read as well.
    //
    // SAVED AT ALL because they were not, and that was a real hole rather than an omission. The
    // three switches sat one tab away from the projectile ones, which have always persisted, so
    // the difference read as a bug - and it IS one for the job these are for: a recording can
    // only contain reads the running build performed, so anything needing hostile effects in the
    // file has to have this on BEFORE the process starts, and a session-only switch cannot be.
    [property: JsonPropertyName("showEffects")] bool ShowEffects = false,
    [property: JsonPropertyName("effectPaths")] bool EffectPaths = true,
    [property: JsonPropertyName("keepEffects")] bool KeepEffects = false,

    // The Stash tab's four switches. ALL FOUR REACH THE NETWORK - poe2db for pictures,
    // poe.ninja and the trade site for prices, and the game's own currency exchange - which is
    // why every one of them ships off and why the stores that own them say "off until somebody
    // says otherwise".
    //
    // SAYING SO ONCE IS WHAT THAT SENTENCE MEANS. Until now it had to be said again on every
    // launch, which is not a policy about outbound requests - a tool that asks poe.ninja the
    // moment you tick a box asks it just the same the second time you tick it - it was simply
    // four switches nobody had wired to the file.
    [property: JsonPropertyName("stashItemArt")] bool StashItemArt = false,
    [property: JsonPropertyName("stashPrices")] bool StashPrices = false,
    [property: JsonPropertyName("stashExchange")] bool StashExchange = false,
    [property: JsonPropertyName("stashTrade")] bool StashTrade = false,

    // How the tool's OWN windows are drawn, as its own object rather than three more fields
    // out here: it is a different subject from what the overlay draws on the game, and a
    // settings file stops being readable at exactly the point everything shares one level.
    [property: JsonPropertyName("interface")] InterfaceStyle? Interface = null,
    [property: JsonPropertyName("windows")] IReadOnlyDictionary<string, WindowRule>? Windows = null,

    // Which of the tools window's tabs are off the bar, by page id - ids rather than labels
    // for the reason the window rules key on ids: a label is wording, and a reworded tab
    // must not come back. Null until somebody hides one, so an untouched file gains no key.
    [property: JsonPropertyName("hiddenTabs")] IReadOnlyList<string>? HiddenTabs = null,

    // Which classes of noise are LET THROUGH, by name, and null until somebody lets one through -
    // the same bargain as the hidden tabs above. By name so a kind added to the enum later
    // cannot silently turn a saved choice into a different one.
    //
    // The Effects tab is the only thing that edits this, and it edits one: the engine's own
    // /fx/ and /mat/ nodes, which are what a wave would arrive as. Session-only until now, which
    // made it the same hole as the switches beside it.
    [property: JsonPropertyName("noiseOff")] IReadOnlyList<string>? NoiseOff = null,

    // What the entity browser leaves out of its list: metadata paths for a whole kind, and
    // kind-plus-place for one entity. Both last, because hiding clutter is a decision made
    // once - see EntityHiding, and EntitySpot for why a single one is not keyed on its id.
    [property: JsonPropertyName("hiddenEntities")] IReadOnlyList<string>? HiddenEntities = null,
    [property: JsonPropertyName("hiddenEntitySpots")] IReadOnlyList<EntitySpot>? HiddenEntitySpots = null,

    // The wealth tracker. OFF by default, and both halves separately: counting the purse is a
    // sweep of every inventory every few seconds, and the corner panel is a dead spot on the
    // screen. Neither should arrive uninvited on somebody who never opened the page.
    //
    // The RECORD itself is not here - it lives in its own file, because it is data rather than
    // a preference and it outgrows a settings file within a week. See WealthHistory.
    [property: JsonPropertyName("wealthWatch")] bool WealthWatch = false,
    [property: JsonPropertyName("wealthPanel")] bool WealthPanel = false,

    // Which stretch both wealth views report on, in minutes. One setting for the two of them:
    // two figures on screen labelled differently and answering the same question is how
    // somebody ends up believing the smaller one.
    [property: JsonPropertyName("wealthWindowMinutes")] int WealthWindowMinutes = 60)
{
    /// <summary>How the tool's own windows look. The defaults until somebody says otherwise.</summary>
    /// <remarks>
    /// Null until it is changed, like the window rules below, so an untouched settings file
    /// gains no key at all and the defaults stay where they can be corrected in a release.
    /// </remarks>
    public InterfaceStyle InterfaceOrDefault => Interface ?? InterfaceStyle.Default;

    /// <summary>Where the game's interface is, as edited or as it ships.</summary>
    public MapKeepOut MapKeepOutOrDefault => MapKeepOut ?? Features.MapKeepOut.Default;

    /// <summary>What was decided about the room names, or the defaults.</summary>
    public RoomSettings RoomsOrDefault => Rooms ?? RoomSettings.Default;

    /// <summary>What was decided about the ground-type names, or the defaults.</summary>
    public GroundSettings GroundOrDefault => Ground ?? GroundSettings.Default;

    /// <summary>The hidden tab ids, empty until somebody hides one.</summary>
    public IReadOnlyList<string> HiddenTabsOrEmpty => HiddenTabs ?? [];

    /// <summary>The noise classes let through, empty until somebody lets one through.</summary>
    public IReadOnlyList<string> NoiseOffOrEmpty => NoiseOff ?? [];

    /// <summary>The entity kinds the browser leaves out, empty until somebody hides one.</summary>
    public IReadOnlyList<string> HiddenEntitiesOrEmpty => HiddenEntities ?? [];

    /// <summary>The single entities the browser leaves out, empty until somebody hides one.</summary>
    public IReadOnlyList<EntitySpot> HiddenEntitySpotsOrEmpty => HiddenEntitySpots ?? [];

    /// <summary>What each window was told, by the window's own id. Empty until somebody says.</summary>
    /// <remarks>
    /// Only the windows somebody has actually pinned down are kept, so the file stays a record
    /// of decisions rather than a list of every window that has ever existed - which is also
    /// what lets a renamed or retired window disappear without leaving a stale entry behind.
    /// </remarks>
    public IReadOnlyDictionary<string, WindowRule> WindowsOrEmpty => Windows ?? NoWindows;

    private static readonly Dictionary<string, WindowRule> NoWindows = [];

    /// <summary>
    /// Magic and above, and the layout shown. Path of Exile 2 drops normal-rarity items
    /// faster than they can be read, so marking all of them buries the drops worth walking
    /// to; the layout is the reason to look at the map at all.
    /// </summary>
    public static OverlaySettings Default { get; } = new(ItemRarity.Magic);

    /// <summary>
    /// Takes the fields the configuration page owns, and keeps the rest.
    /// </summary>
    /// <remarks>
    /// TWO THINGS WRITE THIS FILE and only one of them knows every field. The page sends the
    /// four settings it shows; deserialising that into a whole record gives the others their
    /// DEFAULTS, and saving it then quietly resets every switch made on the overlay itself.
    /// Nothing about that failure is visible - the page did what it was asked, the file is
    /// valid, and the user's choices are simply gone the next time they look.
    ///
    /// So the page's values are merged in rather than swapped for. Anything it does not show
    /// is not its to overwrite.
    /// </remarks>
    public OverlaySettings MergeFromPage(OverlaySettings sent)
    {
        ArgumentNullException.ThrowIfNull(sent);

        return this with
        {
            MinLootRarity = sent.MinLootRarity,
            ShowTerrain = sent.ShowTerrain,
            TerrainColour = sent.TerrainColour,
            TerrainThickness = sent.TerrainThickness,
            TerrainRim = sent.TerrainRim,
        };
    }

    /// <summary>Keeps the value inside the range the overlay understands.</summary>
    /// <remarks>
    /// Currency is not a threshold - it is a classification for drops that carry no rarity
    /// at all, and it is always shown. Allowing it to be SELECTED as the minimum would read
    /// as "currency only" while actually meaning "rarity 5 and above", which is nothing.
    /// </remarks>
    public OverlaySettings Normalised()
    {
        ItemRarity rarity = MinLootRarity is ItemRarity.Normal or ItemRarity.Magic
            or ItemRarity.Rare or ItemRarity.Unique
            ? MinLootRarity
            : Default.MinLootRarity;

        return this with
        {
            MinLootRarity = rarity,
            TerrainColour = ParseColour(TerrainColour) == 0 ? Default.TerrainColour : TerrainColour,

            // Left null when it is null, rather than filled in with the defaults: an untouched
            // file gains no key, and the defaults keep coming from the code where a correction
            // can still reach somebody who never opened the sliders.
            Interface = Interface?.Normalised(),
            Rooms = Rooms?.Normalised(),
            Ground = Ground?.Normalised(),

            // Capped low: this thickens the line in TEXTURE pixels, and past a few the
            // outline stops being a boundary and becomes a filled shape.
            TerrainThickness = Math.Clamp(TerrainThickness, 1, 6),
        };
    }

    /// <summary>
    /// A colour as ImGui packs it - ABGR, alpha first in the high byte.
    /// </summary>
    /// <remarks>
    /// Takes <c>#RRGGBB</c> (opaque) or <c>#AARRGGBB</c>. Returns 0 for anything
    /// unparseable, which the caller treats as "use the default" rather than drawing an
    /// invisible line. ImGui's byte order is the reverse of the #RRGGBB a page sends, and
    /// getting that backwards produces a colour that looks deliberate and is wrong.
    ///
    /// A fully transparent colour parses to 0 as well, so it reads as "not chosen" rather
    /// than as a request to draw nothing. Drawing nothing is what the visibility switch is
    /// for, and it says so where somebody looking for a missing marker would look.
    /// </remarks>
    public static uint ParseColour(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        ReadOnlySpan<char> text = value.AsSpan().Trim().TrimStart('#');
        if (text.Length is not (6 or 8)
            || !uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out uint argb))
        {
            return 0;
        }

        uint a = text.Length == 8 ? (argb >> 24) & 0xFF : 0xFF;
        uint r = (argb >> 16) & 0xFF;
        uint g = (argb >> 8) & 0xFF;
        uint b = argb & 0xFF;
        return (a << 24) | (b << 16) | (g << 8) | r;
    }

    /// <summary>
    /// The same colour with less of it: scales the alpha and leaves the colour alone.
    /// </summary>
    /// <remarks>
    /// HERE rather than in each drawing class, which is where it started - three private
    /// copies of the same bit-shifting, each having to get the byte order right on its own.
    /// It is also the half of a fade a test can reach: the overlay is Windows-only and the
    /// tests are not, so a copy living beside the drawing is a copy nothing checks.
    ///
    /// MULTIPLIES rather than sets, so a colour somebody deliberately made half transparent
    /// stays relatively that way while a banner fades the whole thing out.
    /// </remarks>
    public static uint Fade(uint colour, float by)
    {
        if (by >= 1f)
        {
            return colour;
        }

        uint alpha = (uint)Math.Clamp(((colour >> 24) & 0xFF) * by, 0f, 255f);
        return (colour & 0x00FF_FFFF) | (alpha << 24);
    }

    /// <summary>Writes an ImGui colour out as <c>#RRGGBB</c>, dropping the alpha.</summary>
    /// <remarks>
    /// For the configuration page, whose colour control is the browser's own and accepts
    /// exactly six hex digits: handed eight, it silently shows black. The page cannot set
    /// alpha, so it loses nothing it could have shown; a colour with alpha comes from the
    /// in-game style editor, and it keeps it there.
    /// </remarks>
    public static string FormatPageColour(uint packed)
    {
        uint b = (packed >> 16) & 0xFF;
        uint g = (packed >> 8) & 0xFF;
        uint r = packed & 0xFF;
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    /// <summary>Writes an ImGui colour back out as <c>#AARRGGBB</c>.</summary>
    /// <remarks>
    /// The other direction, for whatever a colour picker produced. Always eight digits: a
    /// picker that can set alpha needs somewhere to put it, and a file where some entries
    /// carry alpha and some do not is harder to read than one where they all do.
    /// </remarks>
    public static string FormatColour(uint packed)
    {
        uint a = (packed >> 24) & 0xFF;
        uint b = (packed >> 16) & 0xFF;
        uint g = (packed >> 8) & 0xFF;
        uint r = packed & 0xFF;
        return $"#{a:X2}{r:X2}{g:X2}{b:X2}";
    }
}

/// <summary>Loads and saves the overlay settings next to the executable.</summary>
public static class OverlaySettingsStore
{
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "config", "overlay.json");

    /// <summary>Reads the settings, falling back to the defaults on any problem.</summary>
    public static OverlaySettings Load(string? path = null)
    {
        string file = path ?? DefaultPath;
        try
        {
            if (!File.Exists(file))
            {
                return OverlaySettings.Default;
            }

            using FileStream stream = File.OpenRead(file);
            OverlaySettings? loaded = JsonSerializer.Deserialize(stream, OverlayJsonContext.Default.OverlaySettings);
            return loaded?.Normalised() ?? OverlaySettings.Default;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return OverlaySettings.Default;
        }
    }

    /// <summary>Writes the settings, returning false when it could not.</summary>
    public static bool Save(OverlaySettings settings, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string file = path ?? DefaultPath;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using FileStream stream = File.Create(file);
            JsonSerializer.Serialize(stream, settings, OverlayJsonContext.Default.OverlaySettings);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>Source-generated JSON, so settings survive Native AOT.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(OverlaySettings))]
public sealed partial class OverlayJsonContext : JsonSerializerContext;
