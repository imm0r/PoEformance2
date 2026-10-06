using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoEformance.Features;

/// <summary>How the price on a recipe row is tinted, so the row worth taking reads at a glance.</summary>
public enum RunecraftColourMode
{
    /// <summary>One neutral colour for every price.</summary>
    Off,

    /// <summary>Green, yellow or red against the MEDIAN of the rows on screen - a relative call.</summary>
    Relative,

    /// <summary>Green, yellow or red against fixed Exalted thresholds - see <see cref="RunecraftSettings.GoodFrom"/>.</summary>
    Absolute,
}

/// <summary>
/// What the Runeshape Combinations overlay draws, and how.
/// </summary>
/// <remarks>
/// PORTED FROM yokkenUA's RunecraftHelper for GameHelper2: while the in-game Runeshape
/// Combinations panel is open, the poe.ninja price of every offered reward is written onto its
/// row, just before the reward's name, in Exalted. The row's own text - the name in the client's
/// language - is left to the game; only the price is added. The same record carries the map
/// labels every monolith in the area gets, and the rune-chain valuation (<see cref="RuneChain"/>)
/// that puts a second figure beside the price: what the rune a recipe propagates is worth.
///
/// OFF BY DEFAULT, like everything that needs the network: the prices come from poe.ninja and
/// nowhere else, and the price switch on the Stash tab is the one that lets them be fetched at
/// all. The Runecraft tab says so when this is on and that is off.
/// </remarks>
/// <param name="Enabled">Whether prices are written on the panel at all.</param>
/// <param name="ColourMode">How a price is tinted.</param>
/// <param name="XOffset">
/// Pixels to slide the price sideways from its place before the reward's name. Negative moves
/// it left. For a client language whose names run further left than the room allows, or a
/// letterboxed display whose bars the layout does not know.
/// </param>
/// <param name="TextScale">
/// How big the price is written, against half the row's height. Zero means "as the row" - the
/// unset value every zero in these files is, see <see cref="Writing"/>.
/// </param>
/// <param name="FrameBest">Frame the most valuable row in green, icons to edge, so the answer needs no reading.</param>
/// <param name="GoodFrom">In Absolute mode, the Exalted a reward is green at or above.</param>
/// <param name="BadBelow">In Absolute mode, the Exalted a reward is red below.</param>
/// <param name="ShowUnpriced">Write a mark on the rows nothing could price, rather than nothing.</param>
/// <param name="MapLabels">
/// Write each monolith's best price on the map, at the monolith - what every monolith in the
/// area can pay, before anybody walks to it. Hidden while the Runeshape panel is open, since the
/// panel's own rows say it better then.
/// </param>
/// <param name="MapSockets">Put the hole count before the price on the map: "[5] 49 ex".</param>
/// <param name="ListMinEx">On the tab's monolith list, hide offers worth less than this. 0 shows all.</param>
/// <param name="ChainEnabled">
/// Value the rune a recipe would propagate from the gold socket, beside its reward - see
/// <see cref="RuneChain"/>. The panel then names that rune on each row and frames the strongest
/// in amber; the tab's offers carry the chain value and the joint one.
/// </param>
/// <param name="ChainBaseEx">Expected drop value of one pack of runic monsters, in Exalted - the calibration knob.</param>
/// <param name="ChainWeights">The loot multiplier per rune. Null in the file means the tier-list defaults.</param>
/// <param name="MapRune">
/// Name the committed rune on the map label where it says more than the price: in place of
/// the price on a monolith whose player gave up reward for the rune, beside it on one sealed
/// by a reroll.
/// </param>
/// <param name="MapScout">
/// Write the runes a monolith could still propagate - the best few worth it - on a line above
/// its price, so the map says which monoliths can seed a strong chain.
/// </param>
public sealed record RunecraftSettings(
    [property: JsonPropertyName("enabled")] bool Enabled = false,
    [property: JsonPropertyName("colourMode")] RunecraftColourMode ColourMode = RunecraftColourMode.Relative,
    [property: JsonPropertyName("xOffset")] float XOffset = 0f,
    [property: JsonPropertyName("textScale")] float TextScale = 0f,
    [property: JsonPropertyName("frameBest")] bool FrameBest = true,
    [property: JsonPropertyName("goodFrom")] float GoodFrom = 5f,
    [property: JsonPropertyName("badBelow")] float BadBelow = 0.5f,
    [property: JsonPropertyName("showUnpriced")] bool ShowUnpriced = false,
    [property: JsonPropertyName("mapLabels")] bool MapLabels = true,
    [property: JsonPropertyName("mapSockets")] bool MapSockets = true,
    [property: JsonPropertyName("listMinEx")] float ListMinEx = 0f,
    [property: JsonPropertyName("chain")] bool ChainEnabled = true,
    [property: JsonPropertyName("chainBaseEx")] float ChainBaseEx = RuneChain.DefaultBaseEx,
    IReadOnlyList<RuneWeight>? ChainWeights = null,
    [property: JsonPropertyName("mapRune")] bool MapRune = true,
    [property: JsonPropertyName("mapScout")] bool MapScout = false)
{
    public static RunecraftSettings Default { get; } = new();

    /// <summary>The loot multiplier per rune, by name. The tier-list defaults when the file has none.</summary>
    [JsonPropertyName("chainWeights")]
    public IReadOnlyList<RuneWeight> ChainWeights { get; init; } = ChainWeights ?? RuneChain.DefaultWeights;

    /// <summary>
    /// Equal when every setting is, the weights row by row - a list compares by reference on
    /// its own, which would make a saved file never equal what was written.
    /// </summary>
    public bool Equals(RunecraftSettings? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return Enabled == other.Enabled && ColourMode == other.ColourMode && XOffset.Equals(other.XOffset)
               && TextScale.Equals(other.TextScale) && FrameBest == other.FrameBest && GoodFrom.Equals(other.GoodFrom)
               && BadBelow.Equals(other.BadBelow) && ShowUnpriced == other.ShowUnpriced && MapLabels == other.MapLabels
               && MapSockets == other.MapSockets && ListMinEx.Equals(other.ListMinEx) && ChainEnabled == other.ChainEnabled
               && ChainBaseEx.Equals(other.ChainBaseEx) && MapRune == other.MapRune && MapScout == other.MapScout
               && ChainWeights.SequenceEqual(other.ChainWeights);
    }

    public override int GetHashCode()
    {
        var hash = default(HashCode);
        hash.Add(Enabled);
        hash.Add(ColourMode);
        hash.Add(XOffset);
        hash.Add(TextScale);
        hash.Add(FrameBest);
        hash.Add(GoodFrom);
        hash.Add(BadBelow);
        hash.Add(ShowUnpriced);
        hash.Add(MapLabels);
        hash.Add(MapSockets);
        hash.Add(ListMinEx);
        hash.Add(ChainEnabled);
        hash.Add(ChainBaseEx);
        hash.Add(MapRune);
        hash.Add(MapScout);
        foreach (RuneWeight weight in ChainWeights)
        {
            hash.Add(weight);
        }

        return hash.ToHashCode();
    }

    /// <summary>How far the price may be slid either way, in pixels.</summary>
    public const float FurthestOffset = 600f;

    /// <summary>The smallest and largest writing worth allowing, as multiples of the row.</summary>
    public const float SmallestText = 0.5f;

    public const float LargestText = 3f;

    /// <summary>How big to write, with zero meaning "as the row".</summary>
    public float Writing => TextScale <= 0f ? 1f : Math.Clamp(TextScale, SmallestText, LargestText);

    /// <summary>The most a rune's multiplier may be set to.</summary>
    public const float LargestMult = 10f;

    /// <summary>Keeps every value inside what can be drawn - a hand-edited file is the ordinary source.</summary>
    public RunecraftSettings Normalised() => this with
    {
        XOffset = float.IsFinite(XOffset) ? Math.Clamp(XOffset, -FurthestOffset, FurthestOffset) : 0f,
        TextScale = float.IsFinite(TextScale) && TextScale > 0f ? Math.Clamp(TextScale, SmallestText, LargestText) : 0f,
        GoodFrom = float.IsFinite(GoodFrom) && GoodFrom > 0f ? GoodFrom : Default.GoodFrom,
        BadBelow = float.IsFinite(BadBelow) && BadBelow >= 0f ? BadBelow : Default.BadBelow,
        ListMinEx = float.IsFinite(ListMinEx) && ListMinEx >= 0f ? ListMinEx : 0f,
        ChainBaseEx = float.IsFinite(ChainBaseEx) && ChainBaseEx >= 0f ? ChainBaseEx : RuneChain.DefaultBaseEx,
        ChainWeights = NormalisedWeights(ChainWeights),
    };

    /// <summary>
    /// The weights with unnamed and repeated runes dropped and the multipliers clamped - the
    /// SAME list back when nothing needed changing, so a compiled table keyed on it stays good.
    /// </summary>
    private static IReadOnlyList<RuneWeight> NormalisedWeights(IReadOnlyList<RuneWeight> weights)
    {
        var clean = true;
        for (int i = 0; i < weights.Count && clean; i++)
        {
            RuneWeight weight = weights[i];
            clean = weight.Rune is { Length: > 0 }
                    && float.IsFinite(weight.LootMult) && weight.LootMult >= 0f && weight.LootMult <= LargestMult;
            for (int j = 0; j < i && clean; j++)
            {
                clean = !string.Equals(weights[j].Rune, weight.Rune, StringComparison.Ordinal);
            }
        }

        if (clean)
        {
            return weights;
        }

        var kept = new List<RuneWeight>(weights.Count);
        foreach (RuneWeight weight in weights)
        {
            if (weight.Rune is not { Length: > 0 } || kept.Exists(k => string.Equals(k.Rune, weight.Rune, StringComparison.Ordinal)))
            {
                continue;
            }

            float mult = float.IsFinite(weight.LootMult) ? Math.Clamp(weight.LootMult, 0f, LargestMult) : 1f;
            kept.Add(mult.Equals(weight.LootMult) ? weight : weight with { LootMult = mult });
        }

        return kept;
    }
}

/// <summary>Loads and saves the Runecraft settings next to the executable.</summary>
public static class RunecraftStore
{
    public static string DefaultPath { get; } =
        Path.Combine(AppContext.BaseDirectory, "config", "runecraft.json");

    /// <summary>Reads the settings, or the defaults when there is no file or it is unreadable.</summary>
    public static RunecraftSettings Load(string? path = null)
    {
        string from = path ?? DefaultPath;
        try
        {
            if (!File.Exists(from))
            {
                return RunecraftSettings.Default;
            }

            using FileStream stream = File.OpenRead(from);
            return JsonSerializer.Deserialize(stream, RunecraftJsonContext.Default.RunecraftSettings)?.Normalised()
                   ?? RunecraftSettings.Default;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return RunecraftSettings.Default;
        }
    }

    /// <summary>Writes the settings. Returns false rather than throwing into a render loop.</summary>
    public static bool Save(RunecraftSettings settings, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string to = path ?? DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.WriteAllText(to, JsonSerializer.Serialize(settings, RunecraftJsonContext.Default.RunecraftSettings));
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
[JsonSerializable(typeof(RunecraftSettings))]
public sealed partial class RunecraftJsonContext : JsonSerializerContext;
