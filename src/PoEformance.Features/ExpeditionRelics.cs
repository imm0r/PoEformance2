using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoEformance.Features;

/// <summary>The English names of the relic mods, by id - data/expedition-relic-mods.json.</summary>
public sealed class RelicModNames
{
    private readonly IReadOnlyDictionary<string, string> _names;

    private RelicModNames(IReadOnlyDictionary<string, string> names) => _names = names;

    /// <summary>No file: every label is the id's short form.</summary>
    public static RelicModNames Empty { get; } = new(new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>How many mods are named.</summary>
    public int Count => _names.Count;

    /// <summary>Reads the file, or returns <see cref="Empty"/> when it is missing or unreadable.</summary>
    public static RelicModNames Load(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return Empty;
        }

        try
        {
            using FileStream stream = File.OpenRead(path);
            RelicModNamesFile? file = JsonSerializer.Deserialize(stream, ExpeditionDataJsonContext.Default.RelicModNamesFile);
            return file?.Names is { Count: > 0 } names
                ? new RelicModNames(new Dictionary<string, string>(names, StringComparer.Ordinal))
                : Empty;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return Empty;
        }
    }

    /// <summary>What to call a mod: its English name, else its id with the family prefix off.</summary>
    public string Label(string mod)
        => _names.TryGetValue(mod, out string? name) && name.Length > 0 ? name : ExpeditionRelics.ShortName(mod);
}

/// <summary>The shape of data/expedition-relic-mods.json.</summary>
public sealed class RelicModNamesFile
{
    [JsonPropertyName("names")]
    public Dictionary<string, string>? Names { get; set; }
}

[JsonSourceGenerationOptions(ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(RelicModNamesFile))]
public sealed partial class ExpeditionDataJsonContext : JsonSerializerContext;

/// <summary>
/// What an expedition relic ("remnant") can carry, by the game's own mod ids.
/// </summary>
/// <remarks>
/// A relic's mods apply to the encounter when the blast chain reaches it, so a relic worth
/// routing through is one whose upsides outweigh its downsides by the player's own weights
/// (<see cref="ExpeditionSettings.RelicWeights"/>). The lists are the plugin's, extracted from
/// Mods.dat (rows 5249-5309 plus the faction and special variants); the weights are stored by
/// these LANGUAGE-INDEPENDENT ids, so a client-language change never breaks them, and the
/// English wording in data/expedition-relic-mods.json is a display layer over the same keys.
///
/// THE LOGBOOK REMNANTS ARE NOT THE GENERIC RELIC ENTITY. Each logbook has one or two of its own
/// - a Sulphite Stalagmite, a Karui Totem - that behave exactly like a relic but are terrain
/// doodads under the tileset, so a scan for ExpeditionRelic misses every one of them. Read out
/// of ExpeditionRelics.dat by the plugin: each resolves to a metadata path, and its ItemTag
/// gates a pool of exactly ONE mod, so the effect is fixed per type. Matched on the path TAIL,
/// the trailing "/Objects/" included, because the same folder holds pure decor that must not
/// become a target.
/// </remarks>
public static class ExpeditionRelics
{
    /// <summary>A logbook's own remnant: the path tail that names it, what it is called, the mod it carries.</summary>
    public static readonly (string PathTail, string Name, string Mod)[] LogbookRemnants =
    [
        ("Logbook_Wastes/Objects/Sulphite", "Sulphite Stalagmite", "ExpeditionRelicUpsideSpecialSulphite"),
        ("Logbook_Wastes/Objects/Totem", "Karui Totem", "ExpeditionRelicUpsideSpecialKaruiTotem"),
        ("Logbook_Peninsula/Objects/GoblinRelic", "Kin Totem", "ExpeditionRelicUpsideSpecialGoblinTotem"),
        ("Logbook_Heath/Objects/HeathHenge", "Runic Henge", "ExpeditionRelicUpsideSpecialRunicHenge"),
        ("Logbook_Prairie/Objects/WispTrap_Wild", "Imprisoned Wild Wisp", "ExpeditionRelicUpsideSpecialAzmeriWisp"),
        ("Logbook_Prairie/Objects/WispTrap_Vivid", "Imprisoned Vivid Wisp", "ExpeditionRelicUpsideSpecialAzmeriWisp"),
        ("Logbook_Prairie/Objects/WispTrap_Primal", "Imprisoned Primal Wisp", "ExpeditionRelicUpsideSpecialAzmeriWisp"),
        ("Logbook_Gully/Objects/DevourerSegment", "Dormant Burrower", "ExpeditionRelicUpsideSpecialDevourerTail"),

        // Listed as remnants, but their mod pool is empty in the dump the plugin read, so they
        // are found WITHOUT a mod rather than not at all.
        ("Logbook_Digsite/Objects/Lighthouse_Destructable", "Precursor Leyline", ""),
        ("Logbook_Reef/Objects/ClamChest", "Overgrown Clam", ""),
    ];

    /// <summary>The beneficial mods: loot first, then density, then the faction and special ones.</summary>
    public static readonly string[] Upsides =
    [
        "ExpeditionRelicUpsideItemQuantityChest",
        "ExpeditionRelicUpsideItemQuantityMonster",
        "ExpeditionRelicUpsideItemRarityChest",
        "ExpeditionRelicUpsideItemRarityMonster",
        "ExpeditionRelicUpsideIncreasedArtifactsChest",
        "ExpeditionRelicUpsideIncreasedArtifactsMonster",
        "ExpeditionRelicUpsideExpeditionLogbookQuantityMonster",
        "ExpeditionRelicUpsidePackSize",
        "ExpeditionRelicUpsideMagicMonsterChance",
        "ExpeditionRelicUpsideRareMonsterChance",
        "ExpeditionRelicUpsideElitesDuplicated",
        "ExpeditionRelicUpsideExperience",
        "ExpeditionRelicUpsideMissingLife",
        "ExpeditionRelicUpsideItemRarityMonsterEzomyte",
        "ExpeditionRelicUpsideExperienceKarui",
        "ExpeditionRelicUpsideMagicRareMonsterChanceGoblin",
        "ExpeditionRelicUpsideCorruptedDropChanceVaal",
        "ExpeditionRelicUpsidePreventWeaponDrops",
        "ExpeditionRelicUpsidePreventArmourDrops",
        "ExpeditionRelicUpsidePreventJewelleryDrops",
        "ExpeditionRelicUpsideSpecialDevourerTail",
        "ExpeditionRelicUpsideSpecialRunicHenge",
        "ExpeditionRelicUpsideSpecialAzmeriWisp",
        "ExpeditionRelicUpsideSpecialGoblinTotem",
        "ExpeditionRelicUpsideSpecialSulphite",
        "ExpeditionRelicUpsideSpecialKaruiTotem",
    ];

    /// <summary>The dangerous mods: immunities, penetration, added damage, crit, defence, ailments, buffs.</summary>
    public static readonly string[] Downsides =
    [
        "ExpeditionRelicDownsideImmunePhysicalDamage",
        "ExpeditionRelicDownsideImmuneFireDamage",
        "ExpeditionRelicDownsideImmuneColdDamage",
        "ExpeditionRelicDownsideImmuneLightningDamage",
        "ExpeditionRelicDownsideImmuneChaosDamage",
        "ExpeditionRelicDownsideFirePenetration",
        "ExpeditionRelicDownsideColdPenetration",
        "ExpeditionRelicDownsideLightningPenetration",
        "ExpeditionRelicDownsideChaosPenetration",
        "ExpeditionRelicDownsideDamageAsFire",
        "ExpeditionRelicDownsideDamageAsCold",
        "ExpeditionRelicDownsideDamageAsLightning",
        "ExpeditionRelicDownsideDamageAsChaos",
        "ExpeditionRelicDownsideAlwaysCrit",
        "ExpeditionRelicDownsideCannotBeCrit",
        "ExpeditionRelicDownsideCriticalAgainstFullLife",
        "ExpeditionRelicDownsideAvoidDamage",
        "ExpeditionRelicDownsideHitsCannotBeEvaded",
        "ExpeditionRelicDownsideCannotBeLeechedFrom",
        "ExpeditionRelicDownsideResistancesAndMaxResistances",
        "ExpeditionRelicDownsideArmourBreak",
        "ExpeditionRelicDownsideGrantNoFlaskCharges",
        "ExpeditionRelicDownsideElementalAilmentChance",
        "ExpeditionRelicDownsideBleedOnHitBleedDuration",
        "ExpeditionRelicDownsideAllDamagePoisonsPoisonDuration",
        "ExpeditionRelicDownsideElitesRandomCurseOnHit",
        "ExpeditionRelicDownsideImmuneToCurses",
        "ExpeditionRelicDownsideIncreasedLife",
        "ExpeditionRelicDownsideIncreasedDamage",
        "ExpeditionRelicDownsideIncreasedSpeed",
        "ExpeditionRelicDownsideDamageAttackCastMovementSpeedLowLife",
        "ExpeditionRelicDownsideRegenerateLifeEveryFourSeconds",
    ];

    private static readonly string[] Prefixes =
    [
        "ExpeditionRelicUpside", "ExpeditionRelicDownside", "ExpeditionRelicModifier", "ExpeditionRelic",
    ];

    /// <summary>Whether a path is one of the per-logbook remnants, and what it propagates.</summary>
    public static bool TryMatchLogbookRemnant(string path, out string name, out string mod)
    {
        name = string.Empty;
        mod = string.Empty;
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        foreach ((string tail, string known, string carried) in LogbookRemnants)
        {
            if (path.EndsWith(tail, StringComparison.OrdinalIgnoreCase))
            {
                name = known;
                mod = carried;
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a mod id is a beneficial one.</summary>
    public static bool IsUpside(string mod)
        => mod is { Length: > 0 } && mod.Contains("Upside", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a mod id is a dangerous one.</summary>
    public static bool IsDownside(string mod)
        => mod is { Length: > 0 } && mod.Contains("Downside", StringComparison.OrdinalIgnoreCase);

    /// <summary>The id with its family prefix off: "ItemQuantityChest" for the chest-quantity upside.</summary>
    public static string ShortName(string mod)
    {
        if (string.IsNullOrEmpty(mod))
        {
            return string.Empty;
        }

        foreach (string prefix in Prefixes)
        {
            if (mod.StartsWith(prefix, StringComparison.Ordinal))
            {
                return mod[prefix.Length..];
            }
        }

        return mod;
    }

    /// <summary>
    /// A relic's routing value from its mods: the weighted upsides less the weighted downsides.
    /// </summary>
    /// <remarks>
    /// A mod with no weight contributes nothing, so a relic becomes a target only when the
    /// player has said what its upsides are worth; positive routes the blast through it, zero or
    /// less ignores it - never sought, never avoided.
    /// </remarks>
    public static double NetWeight(IReadOnlyList<string> mods, IReadOnlyDictionary<string, float> weights)
    {
        ArgumentNullException.ThrowIfNull(mods);
        ArgumentNullException.ThrowIfNull(weights);

        double net = 0;
        foreach (string mod in mods)
        {
            if (!weights.TryGetValue(mod, out float weight) || weight == 0f)
            {
                continue;
            }

            if (IsUpside(mod))
            {
                net += weight;
            }
            else if (IsDownside(mod))
            {
                net -= weight;
            }
        }

        return net;
    }
}
