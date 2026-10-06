using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoEformance.Features;

/// <summary>What a reward marker of one type is worth to the route, in Exalted.</summary>
/// <param name="Icon">The marker's MinimapIcon name - "RewardChestCurrency".</param>
/// <param name="Weight">Its routing weight. 0 ignores the type.</param>
public sealed record RewardWeight(
    [property: JsonPropertyName("icon")] string Icon,
    [property: JsonPropertyName("weight")] float Weight);

/// <summary>What one relic mod is worth, as a plain magnitude; the sign comes from the mod's family.</summary>
/// <param name="Mod">The game's mod id - "ExpeditionRelicUpsideItemQuantityChest".</param>
/// <param name="Weight">A positive magnitude: added for an upside, subtracted for a downside.</param>
public sealed record RelicWeight(
    [property: JsonPropertyName("mod")] string Mod,
    [property: JsonPropertyName("weight")] float Weight);

/// <summary>One "this object explodes" rule: a path fragment and the radius it clears around itself.</summary>
/// <param name="PathContains">A case-insensitive fragment of the entity path - the leaf, since the tileset prefix differs per map.</param>
/// <param name="Radius">What its own blast takes, in grid cells, centred on the OBJECT.</param>
/// <param name="Enabled">Whether the rule is in force.</param>
public sealed record PropRule(
    [property: JsonPropertyName("pathContains")] string PathContains,
    [property: JsonPropertyName("radius")] float Radius,
    [property: JsonPropertyName("enabled")] bool Enabled = true);

/// <summary>
/// The expedition route planner's settings.
/// </summary>
/// <remarks>
/// PORTED FROM yokkenUA's RunecraftHelper. The planner lays the explosive chain for the player:
/// from the detonator through every monolith and beneficial relic worth the walk, bridging the
/// gaps, grabbing reward markers on the way, spending the spare charges where they pay. Most of
/// what is here is the WEIGHING - what a marker, a relic mod or a cheap monolith is worth
/// against a charge - because that is the part no two players agree on.
///
/// THE WEIGHT TABLES ARE SINGLE, not named profiles: the plugin kept several per feature and
/// switched between them, and the switching UI was half of its planner window. One table each,
/// editable, with a reset, covers the ordinary case and keeps the window to its job.
/// </remarks>
/// <param name="Enabled">Whether the planner runs at all - the scan, the tab, the map drawing.</param>
/// <param name="ShowRings">Draw each planned charge's blast ring on the map, so overlapping coverage is visible.</param>
/// <param name="ShowSpine">Draw the line the player walks - detonator to anchors - under the charges.</param>
/// <param name="ShowProps">Draw the exploding props and the area each clears.</param>
/// <param name="ShowGates">Paint the hole each shut path-blocker punches in the walkable grid.</param>
/// <param name="RunKey">A virtual-key code that re-plans, or 0 for none. See <see cref="RunKeys"/>.</param>
/// <param name="ManualTotal">The charge total to plan with when neither the controller nor the counter reads.</param>
/// <param name="MonolithMinEx">
/// Joint value - recipe plus propagated rune - that makes a monolith worth a DETOUR. Under it
/// the monolith stays a pickup, captured when a blast covers it. 0 makes every priced one an
/// anchor.
/// </param>
/// <param name="MonolithMinSockets">
/// Holes that make a monolith an anchor whatever its recipe is worth: it spawns one wave per
/// socket and every wave carries every rune propagated before it, so the big one is where the
/// chain cashes out. 0 switches this off.
/// </param>
/// <param name="MinMarkersPerSpare">On a Grand expedition, how many markers one blast must cover before a spare charge is spent on them.</param>
/// <param name="MarkerWhite">Routing weight of a white reward flag (normal expeditions, by pole height).</param>
/// <param name="MarkerMagic">Of a magic one.</param>
/// <param name="MarkerGold">Of a gold one.</param>
/// <param name="MarkerLogbook">Of the tall two-triangle logbook flag.</param>
/// <param name="RewardWeights">On a Grand expedition, the weight per reward type by icon. Types absent weigh 1.</param>
/// <param name="RelicWeights">The weight per relic mod. A relic is a target when its net is positive.</param>
/// <param name="AvoidMods">
/// Mods no blast may reach: a relic carrying one is routed round, never harvested, and an
/// anchor only takeable by setting it off is skipped. A hard rule, where the weights are a
/// trade - "Monsters Immune to Lightning" is not worth any amount of quantity to a lightning build.
/// </param>
/// <param name="PropRules">The exploding props, as path fragment and radius.</param>
/// <param name="TraceFile">Write the planner's decision trace to config/expedition-plan.txt on every run.</param>
public sealed record ExpeditionSettings(
    [property: JsonPropertyName("enabled")] bool Enabled = false,
    [property: JsonPropertyName("showRings")] bool ShowRings = true,
    [property: JsonPropertyName("showSpine")] bool ShowSpine = false,
    [property: JsonPropertyName("showProps")] bool ShowProps = false,
    [property: JsonPropertyName("showGates")] bool ShowGates = false,
    [property: JsonPropertyName("runKey")] int RunKey = 0,
    [property: JsonPropertyName("manualTotal")] int ManualTotal = 15,
    [property: JsonPropertyName("monolithMinEx")] float MonolithMinEx = 0f,
    [property: JsonPropertyName("monolithMinSockets")] int MonolithMinSockets = 7,
    [property: JsonPropertyName("minMarkersPerSpare")] int MinMarkersPerSpare = 2,
    [property: JsonPropertyName("markerWhite")] int MarkerWhite = 10,
    [property: JsonPropertyName("markerMagic")] int MarkerMagic = 30,
    [property: JsonPropertyName("markerGold")] int MarkerGold = 60,
    [property: JsonPropertyName("markerLogbook")] int MarkerLogbook = 100,
    IReadOnlyList<RewardWeight>? RewardWeights = null,
    IReadOnlyList<RelicWeight>? RelicWeights = null,
    IReadOnlyList<string>? AvoidMods = null,
    IReadOnlyList<PropRule>? PropRules = null,
    [property: JsonPropertyName("traceFile")] bool TraceFile = false)
{
    /// <summary>The reward types the plugin found worth more than one Exalted; everything else weighs 1.</summary>
    public static IReadOnlyList<RewardWeight> DefaultRewardWeights { get; } =
    [
        new("RewardChestCurrencyRare", 40f),
        new("RewardChestCurrency", 25f),
        new("RewardChestArmour", 10f),
        new("RewardChestWeapons", 10f),
    ];

    /// <summary>The props measured so far, each its own radius: the barrel family, the Oil Derrick, the Faridun explosive.</summary>
    /// <remarks>
    /// The Derrick is worth noticing: 112 cells around the object is wider than a Grand charge can
    /// even be placed, so the one charge that reaches it clears more ground than the hop that got
    /// there. Logbook_Basin carries it and the Faridun one at 74, so one zone can hold several.
    /// </remarks>
    public static IReadOnlyList<PropRule> DefaultPropRules { get; } =
    [
        new("ExplodingFill", 55f),
        new("Objects/OilWell", 112f),
        new("Objects/FaridunExplosive", 74f),
    ];

    // Below the two tables on purpose: static initialisers run in textual order, and a Default
    // built above them would carry null lists.
    public static ExpeditionSettings Default { get; } = new();

    /// <summary>What a reward type weighs when no row names it.</summary>
    public const float DefaultRewardWeight = 1f;

    /// <summary>The most a charge total can be set to by hand.</summary>
    public const int MostCharges = 64;

    [JsonPropertyName("rewardWeights")]
    public IReadOnlyList<RewardWeight> RewardWeights { get; init; } = RewardWeights ?? DefaultRewardWeights;

    [JsonPropertyName("relicWeights")]
    public IReadOnlyList<RelicWeight> RelicWeights { get; init; } = RelicWeights ?? [];

    [JsonPropertyName("avoidMods")]
    public IReadOnlyList<string> AvoidMods { get; init; } = AvoidMods ?? [];

    [JsonPropertyName("propRules")]
    public IReadOnlyList<PropRule> PropRules { get; init; } = PropRules ?? DefaultPropRules;

    /// <summary>Whether a relic mod is one no blast may reach.</summary>
    public bool Avoids(string mod)
    {
        foreach (string avoided in AvoidMods)
        {
            if (string.Equals(avoided, mod, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A reward type's weight: its row, else the catch-all 1.</summary>
    public float RewardWeightOf(string icon)
    {
        foreach (RewardWeight weight in RewardWeights)
        {
            if (string.Equals(weight.Icon, icon, StringComparison.Ordinal))
            {
                return weight.Weight;
            }
        }

        return DefaultRewardWeight;
    }

    /// <summary>The radius a prop clears, by the first enabled rule its path matches, or 0 for none.</summary>
    public float PropRadiusFor(string path)
    {
        foreach (PropRule rule in PropRules)
        {
            if (rule.Enabled && rule.PathContains.Length > 0 && rule.Radius > 0f
                && path.Contains(rule.PathContains, StringComparison.OrdinalIgnoreCase))
            {
                return rule.Radius;
            }
        }

        return 0f;
    }

    /// <summary>Keeps every value inside what the planner can use.</summary>
    public ExpeditionSettings Normalised() => this with
    {
        ManualTotal = Math.Clamp(ManualTotal, 1, MostCharges),
        MonolithMinEx = float.IsFinite(MonolithMinEx) && MonolithMinEx >= 0f ? MonolithMinEx : 0f,
        MonolithMinSockets = Math.Clamp(MonolithMinSockets, 0, 16),
        MinMarkersPerSpare = Math.Clamp(MinMarkersPerSpare, 1, 3),
        MarkerWhite = Math.Clamp(MarkerWhite, 0, 10_000),
        MarkerMagic = Math.Clamp(MarkerMagic, 0, 10_000),
        MarkerGold = Math.Clamp(MarkerGold, 0, 10_000),
        MarkerLogbook = Math.Clamp(MarkerLogbook, 0, 10_000),
        RunKey = RunKey is >= 0 and <= 255 ? RunKey : 0,
        RewardWeights = CleanRewards(RewardWeights),
        RelicWeights = CleanRelics(RelicWeights),
        AvoidMods = CleanAvoid(AvoidMods),
        PropRules = CleanProps(PropRules),
    };

    public bool Equals(ExpeditionSettings? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return Enabled == other.Enabled && ShowRings == other.ShowRings && ShowSpine == other.ShowSpine
               && ShowProps == other.ShowProps && ShowGates == other.ShowGates && RunKey == other.RunKey
               && ManualTotal == other.ManualTotal && MonolithMinEx.Equals(other.MonolithMinEx)
               && MonolithMinSockets == other.MonolithMinSockets && MinMarkersPerSpare == other.MinMarkersPerSpare
               && MarkerWhite == other.MarkerWhite && MarkerMagic == other.MarkerMagic && MarkerGold == other.MarkerGold
               && MarkerLogbook == other.MarkerLogbook && TraceFile == other.TraceFile
               && RewardWeights.SequenceEqual(other.RewardWeights) && RelicWeights.SequenceEqual(other.RelicWeights)
               && AvoidMods.SequenceEqual(other.AvoidMods, StringComparer.Ordinal)
               && PropRules.SequenceEqual(other.PropRules);
    }

    public override int GetHashCode()
    {
        var hash = default(HashCode);
        hash.Add(Enabled);
        hash.Add(ShowRings);
        hash.Add(ShowSpine);
        hash.Add(ShowProps);
        hash.Add(ShowGates);
        hash.Add(RunKey);
        hash.Add(ManualTotal);
        hash.Add(MonolithMinEx);
        hash.Add(MonolithMinSockets);
        hash.Add(MinMarkersPerSpare);
        hash.Add(MarkerWhite);
        hash.Add(MarkerMagic);
        hash.Add(MarkerGold);
        hash.Add(MarkerLogbook);
        hash.Add(TraceFile);
        foreach (RewardWeight weight in RewardWeights)
        {
            hash.Add(weight);
        }

        foreach (RelicWeight weight in RelicWeights)
        {
            hash.Add(weight);
        }

        foreach (string mod in AvoidMods)
        {
            hash.Add(mod, StringComparer.Ordinal);
        }

        foreach (PropRule rule in PropRules)
        {
            hash.Add(rule);
        }

        return hash.ToHashCode();
    }

    private static IReadOnlyList<RewardWeight> CleanRewards(IReadOnlyList<RewardWeight> rows)
    {
        var clean = true;
        for (int i = 0; i < rows.Count && clean; i++)
        {
            clean = rows[i].Icon is { Length: > 0 } && float.IsFinite(rows[i].Weight) && rows[i].Weight >= 0f;
            for (int j = 0; j < i && clean; j++)
            {
                clean = !string.Equals(rows[j].Icon, rows[i].Icon, StringComparison.Ordinal);
            }
        }

        if (clean)
        {
            return rows;
        }

        var kept = new List<RewardWeight>(rows.Count);
        foreach (RewardWeight row in rows)
        {
            if (row.Icon is not { Length: > 0 } || kept.Exists(k => string.Equals(k.Icon, row.Icon, StringComparison.Ordinal)))
            {
                continue;
            }

            float weight = float.IsFinite(row.Weight) ? Math.Max(0f, row.Weight) : DefaultRewardWeight;
            kept.Add(weight.Equals(row.Weight) ? row : row with { Weight = weight });
        }

        return kept;
    }

    private static IReadOnlyList<RelicWeight> CleanRelics(IReadOnlyList<RelicWeight> rows)
    {
        var clean = true;
        for (int i = 0; i < rows.Count && clean; i++)
        {
            clean = rows[i].Mod is { Length: > 0 } && float.IsFinite(rows[i].Weight) && rows[i].Weight > 0f;
            for (int j = 0; j < i && clean; j++)
            {
                clean = !string.Equals(rows[j].Mod, rows[i].Mod, StringComparison.Ordinal);
            }
        }

        if (clean)
        {
            return rows;
        }

        var kept = new List<RelicWeight>(rows.Count);
        foreach (RelicWeight row in rows)
        {
            if (row.Mod is not { Length: > 0 } || !float.IsFinite(row.Weight) || row.Weight <= 0f
                || kept.Exists(k => string.Equals(k.Mod, row.Mod, StringComparison.Ordinal)))
            {
                continue;
            }

            kept.Add(row);
        }

        return kept;
    }

    private static IReadOnlyList<string> CleanAvoid(IReadOnlyList<string> mods)
    {
        var clean = true;
        for (int i = 0; i < mods.Count && clean; i++)
        {
            clean = mods[i] is { Length: > 0 };
            for (int j = 0; j < i && clean; j++)
            {
                clean = !string.Equals(mods[j], mods[i], StringComparison.Ordinal);
            }
        }

        if (clean)
        {
            return mods;
        }

        var kept = new List<string>(mods.Count);
        foreach (string mod in mods)
        {
            if (mod is { Length: > 0 } && !kept.Contains(mod, StringComparer.Ordinal))
            {
                kept.Add(mod);
            }
        }

        return kept;
    }

    private static IReadOnlyList<PropRule> CleanProps(IReadOnlyList<PropRule> rows)
    {
        var clean = true;
        foreach (PropRule row in rows)
        {
            if (row.PathContains is null || !float.IsFinite(row.Radius) || row.Radius < 0f)
            {
                clean = false;
                break;
            }
        }

        if (clean)
        {
            return rows;
        }

        var kept = new List<PropRule>(rows.Count);
        foreach (PropRule row in rows)
        {
            kept.Add(new PropRule(row.PathContains ?? string.Empty, float.IsFinite(row.Radius) ? Math.Max(0f, row.Radius) : 0f, row.Enabled));
        }

        return kept;
    }
}

/// <summary>The keys the planner's Run can be bound to, by virtual-key code, with their names.</summary>
/// <remarks>
/// A short list on purpose: a key the game itself uses is survivable only because the planner
/// listens during the placement phase, before the detonator is pressed, where there is nothing
/// to fight - and even so nobody wants the hotkey on a letter. Function keys, the numpad and
/// the modifiers are what is offered. Off by default, since no default is safe on every keyboard.
/// </remarks>
public static class RunKeys
{
    public static readonly (int Code, string Name)[] Choices =
    [
        (0, "off"),
        (0x70, "F1"), (0x71, "F2"), (0x72, "F3"), (0x73, "F4"), (0x74, "F5"), (0x75, "F6"),
        (0x76, "F7"), (0x77, "F8"), (0x78, "F9"), (0x79, "F10"), (0x7A, "F11"), (0x7B, "F12"),
        (0xA0, "Left Shift"), (0xA1, "Right Shift"), (0xA2, "Left Ctrl"), (0xA3, "Right Ctrl"),
        (0xA4, "Left Alt"), (0x09, "Tab"), (0x20, "Space"),
        (0x2D, "Insert"), (0x2E, "Delete"), (0x24, "Home"), (0x23, "End"), (0x21, "Page Up"), (0x22, "Page Down"),
        (0x60, "Numpad 0"), (0x61, "Numpad 1"), (0x62, "Numpad 2"), (0x63, "Numpad 3"), (0x64, "Numpad 4"),
        (0x65, "Numpad 5"), (0x66, "Numpad 6"), (0x67, "Numpad 7"), (0x68, "Numpad 8"), (0x69, "Numpad 9"),
        (0x6A, "Numpad *"), (0x6B, "Numpad +"), (0x6D, "Numpad -"), (0x6F, "Numpad /"),
    ];

    /// <summary>The key's name, or its code for one not in the list.</summary>
    public static string Name(int code)
    {
        foreach ((int known, string name) in Choices)
        {
            if (known == code)
            {
                return name;
            }
        }

        return $"key 0x{code:X2}";
    }

    /// <summary>Where a code sits in <see cref="Choices"/>, or 0 (off) for one not listed.</summary>
    public static int IndexOf(int code)
    {
        for (int i = 0; i < Choices.Length; i++)
        {
            if (Choices[i].Code == code)
            {
                return i;
            }
        }

        return 0;
    }
}

/// <summary>Loads and saves the planner's settings next to the executable.</summary>
public static class ExpeditionStore
{
    public static string DefaultPath { get; } =
        Path.Combine(AppContext.BaseDirectory, "config", "expedition.json");

    /// <summary>Where a decision trace goes when the setting asks for one.</summary>
    public static string TracePath { get; } =
        Path.Combine(AppContext.BaseDirectory, "config", "expedition-plan.txt");

    public static ExpeditionSettings Load(string? path = null)
    {
        string from = path ?? DefaultPath;
        try
        {
            if (!File.Exists(from))
            {
                return ExpeditionSettings.Default;
            }

            using FileStream stream = File.OpenRead(from);
            return JsonSerializer.Deserialize(stream, ExpeditionJsonContext.Default.ExpeditionSettings)?.Normalised()
                   ?? ExpeditionSettings.Default;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return ExpeditionSettings.Default;
        }
    }

    public static bool Save(ExpeditionSettings settings, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string to = path ?? DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.WriteAllText(to, JsonSerializer.Serialize(settings, ExpeditionJsonContext.Default.ExpeditionSettings));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}

/// <summary>Source-generated JSON, so the settings survive Native AOT.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ExpeditionSettings))]
public sealed partial class ExpeditionJsonContext : JsonSerializerContext;
