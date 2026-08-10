using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoEformance.Game.World;

/// <summary>What one atlas content id means.</summary>
/// <param name="Name">The short name, where the game has one. Empty for a bare effect.</param>
/// <param name="Description">
/// The line the game shows on the node. A <c>{0}</c> in it is where the node's own number goes -
/// "Contains {0} additional Shrines" is one entry covering every count of them.
/// </param>
/// <param name="Icon">The game's own art name for it, for anybody drawing icons later.</param>
public readonly record struct AtlasContent(string Name, string Description, string Icon)
{
    /// <summary>Where a magnitude goes, in the wordings that have room for one.</summary>
    public const string Placeholder = "{0}";

    /// <summary>The best single label: the name when there is one, the description otherwise.</summary>
    public string Label => Name.Length > 0 ? Name : Description;

    /// <summary>
    /// The label with this node's own number written into it.
    /// </summary>
    /// <remarks>
    /// THE WORDING DECIDES whether a number is shown at all, which is the whole rule and the
    /// one this project got wrong: a magnitude was appended to everything as "x3", so every
    /// content on the atlas ended in the magnitude of a plain effect - "Area contains Abysses
    /// x64" - because a plain effect carries 1, and 1 is written as 64.
    /// </remarks>
    public string Say(uint raw)
    {
        string label = Label;
        return label.Contains(Placeholder, StringComparison.Ordinal)
            ? label.Replace(
                Placeholder,
                AtlasContentNames.MagnitudeOf(raw).ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal)
            : label;
    }
}

/// <summary>
/// The meaning of the numbers an atlas node carries.
/// </summary>
/// <remarks>
/// DATA, not code, for the same reason the landmark names and the offsets are: which id is
/// "Breach" is knowledge about a game that changes every league, and it belongs in a file that
/// can be corrected without a rebuild. Ported from GameHelper2's AtlasMapNodeContent - 69
/// badges, 43 effects and 41 older tokens, all 112 of its entries.
///
/// THREE TABLES, kept apart because the game keeps them apart. Badges hang off the node as
/// objects, effects arrive as tokens in a vector, and the legacy tokens are the ones an older
/// client wrote. An id can appear in two of them with different wording - 0x6157 does - so
/// merging them would relabel one of the two.
///
/// A LOOKUP MASKS THE ID. Only the low sixteen bits identify the content; the high word carries
/// a magnitude ("3 additional Shrines") and, on some tokens, flags. So the number on the node
/// is not the number in the table, and looking one up unmasked finds nothing.
/// </remarks>
public sealed class AtlasContentNames
{
    private readonly IReadOnlyDictionary<uint, AtlasContent> _badges;
    private readonly IReadOnlyDictionary<uint, AtlasContent> _effects;
    private readonly IReadOnlyDictionary<uint, AtlasContent> _tokens;

    private AtlasContentNames(
        IReadOnlyDictionary<uint, AtlasContent> badges,
        IReadOnlyDictionary<uint, AtlasContent> effects,
        IReadOnlyDictionary<uint, AtlasContent> tokens)
    {
        _badges = badges;
        _effects = effects;
        _tokens = tokens;
    }

    /// <summary>Nothing known, which is what a missing file leaves.</summary>
    public static AtlasContentNames Empty { get; } = new(
        new Dictionary<uint, AtlasContent>(),
        new Dictionary<uint, AtlasContent>(),
        new Dictionary<uint, AtlasContent>());

    /// <summary>How many meanings are known, across all three tables.</summary>
    public int Count => _badges.Count + _effects.Count + _tokens.Count;

    /// <summary>What identifies a content: the low half of whatever the node carried.</summary>
    public static uint IdOf(uint raw) => raw & 0xFFFF;

    /// <summary>
    /// The high half is a magnitude in SIXTY-FOURTHS, not a count.
    /// </summary>
    /// <remarks>
    /// Which is why a plain effect - one of anything - arrives as 64 rather than as 1, and why
    /// this project spent a while showing "Area contains Abysses x64" on every node of the
    /// atlas. A binary effect ("always", "doubles") carries 100.
    /// </remarks>
    public const uint MagnitudeUnit = 64;

    /// <summary>
    /// Delirious, the one token whose high half is not all magnitude.
    /// </summary>
    /// <remarks>
    /// Its top two bits are flags, so they are masked off before the division. Left unmasked a
    /// delirious map reads as tens of thousands of per cent.
    /// </remarks>
    public const uint DeliriousId = 0x685A;

    /// <summary>
    /// How much of it there is - 3 in "3 additional Shrines".
    /// </summary>
    /// <remarks>
    /// Zero for the many contents that are simply present or absent, and the caller should
    /// treat zero as "no number to show" rather than as the number nought.
    /// </remarks>
    public static uint MagnitudeOf(uint raw)
        => IdOf(raw) == DeliriousId
            ? ((raw >> 16) & 0x3FFF) / MagnitudeUnit
            : (raw >> 16) / MagnitudeUnit;

    /// <summary>What a badge id means, or null when this table has never heard of it.</summary>
    public AtlasContent? Badge(uint raw) => Look(_badges, raw);

    /// <summary>What a content token means, effects first and the older wording behind it.</summary>
    public AtlasContent? Effect(uint raw) => Look(_effects, raw) ?? Look(_tokens, raw);

    private static AtlasContent? Look(IReadOnlyDictionary<uint, AtlasContent> table, uint raw)
        => table.TryGetValue(IdOf(raw), out AtlasContent found) ? found : null;

    /// <summary>
    /// Loads the file, or returns <see cref="Empty"/> when it is missing or unreadable.
    /// </summary>
    /// <remarks>
    /// Never throws. An atlas with unnamed contents is worth having; a tool that will not start
    /// because somebody edited a comma into the wrong place is not.
    /// </remarks>
    public static AtlasContentNames Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return Empty;
        }

        try
        {
            using FileStream stream = File.OpenRead(path);
            AtlasContentFile? file = JsonSerializer.Deserialize(stream, AtlasContentJsonContext.Default.AtlasContentFile);
            if (file is null)
            {
                return Empty;
            }

            return new AtlasContentNames(
                Table(file.Badges),
                Table(file.Effects),
                Words(file.LegacyTokens));
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return Empty;
        }
    }

    /// <summary>Turns one table of the file into something keyed by number.</summary>
    /// <remarks>
    /// The keys are written as hex strings because that is how every id in this game is
    /// discussed, read and reported - a file of decimals would be correct and unreadable, and
    /// unreadable data does not get corrected.
    /// </remarks>
    private static Dictionary<uint, AtlasContent> Table(Dictionary<string, AtlasContentEntry>? entries)
    {
        var built = new Dictionary<uint, AtlasContent>();
        foreach ((string key, AtlasContentEntry entry) in entries ?? [])
        {
            if (Parse(key) is uint id)
            {
                built[id] = new AtlasContent(entry.Name ?? string.Empty, entry.Description ?? string.Empty, entry.Icon ?? string.Empty);
            }
        }

        return built;
    }

    /// <summary>The same, for the older table that is a bare id-to-line map.</summary>
    private static Dictionary<uint, AtlasContent> Words(Dictionary<string, string>? entries)
    {
        var built = new Dictionary<uint, AtlasContent>();
        foreach ((string key, string line) in entries ?? [])
        {
            if (Parse(key) is uint id)
            {
                built[id] = new AtlasContent(string.Empty, line, string.Empty);
            }
        }

        return built;
    }

    private static uint? Parse(string key)
    {
        string text = key.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? key[2..] : key;
        return uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out uint id) ? id : null;
    }
}

/// <summary>The shape of the file on disk.</summary>
public sealed class AtlasContentFile
{
    [JsonPropertyName("badges")]
    public Dictionary<string, AtlasContentEntry>? Badges { get; set; }

    [JsonPropertyName("effects")]
    public Dictionary<string, AtlasContentEntry>? Effects { get; set; }

    [JsonPropertyName("legacyTokens")]
    public Dictionary<string, string>? LegacyTokens { get; set; }
}

/// <summary>One entry of it.</summary>
public sealed class AtlasContentEntry
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }
}

/// <summary>Source-generated JSON, so the atlas data survives Native AOT.</summary>
[JsonSerializable(typeof(AtlasContentFile))]
public sealed partial class AtlasContentJsonContext : JsonSerializerContext;
