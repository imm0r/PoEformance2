using System.Text.Json;
using System.Text.Json.Serialization;
using PoEformance.Game.Files;

namespace PoEformance.Features;

/// <summary>What a kind of ground does to whoever stands in it.</summary>
public enum GroundHarm
{
    /// <summary>Nothing in the data settles it. Never drawn as safe OR as dangerous.</summary>
    Unclear,

    /// <summary>It damages, slows or drains. 44 of the table's 53 rows.</summary>
    Harmful,

    /// <summary>It grants something - Consecration, Haste, an oasis. Six rows.</summary>
    Helpful,
}

/// <summary>One row of the game's GroundEffectTypes table.</summary>
/// <param name="Row">The row index, which is what the component carries.</param>
/// <param name="Id">The game's own name for this kind of ground.</param>
/// <param name="Buffs">
/// How many of the two BuffDefinition columns are set. Kept for the diagnostic, and NOT a signal
/// about damage - every one of the 53 rows applies at least one buff, so counting them
/// discriminates nothing. That was the first idea and it was wrong; <paramref name="Harm"/> is
/// the answer it was reaching for.
/// </param>
/// <param name="HasStat">
/// Whether the row names a Stat. Only ever true when the row came from the INSTALL - and worth
/// less than it looks: the Stat column resolves to names like `ground_fire_art_variation`, so it
/// picks the visual, not the damage.
/// </param>
/// <param name="Buff">The name of the buff this kind applies, as the game shows it.</param>
/// <param name="Harm">Whether standing in it hurts. See data/ground-effect-types.json for how.</param>
/// <param name="Description">The game's own sentence about it, when the buff carries one.</param>
public sealed record GroundEffectType(
    int Row, string Id, int Buffs, bool HasStat, string Buff = "",
    GroundHarm Harm = GroundHarm.Unclear, string Description = "")
{
    /// <summary>What to show when the row is known: the game's name, else the bare index.</summary>
    public string Caption => Id.Length > 0 ? Id : $"type {Row}";

    /// <summary>
    /// The readable line for a label: the kind, and what it puts on you.
    /// </summary>
    /// <remarks>
    /// THE BUFF NAME IS THE USEFUL HALF and the Id is the searchable one, so both are shown. A
    /// row called `CrownOfThorns` means nothing to a player; the buff it applies is called
    /// "Sacred Ashes", which is the phrase on their own screen when they are standing in it.
    /// </remarks>
    public string Describe => Buff.Length > 0 && Buff != Id ? $"{Caption} - {Buff}" : Caption;

    /// <summary>
    /// The sentence the game itself shows while somebody stands in this, when there is one.
    /// </summary>
    /// <remarks>
    /// THE MOST USEFUL STRING IN THE WHOLE CHAIN, and it costs nothing to carry: "You are taking
    /// Physical and Fire Damage over time" is the game's own words, already translated, already
    /// exact about which damage types. No amount of reverse engineering would have produced a
    /// better sentence than the one the game was going to show anyway.
    /// </remarks>
    public string Says => Description;
}

/// <summary>The vendored copy of the table, for when there is no install to read.</summary>
/// <remarks>
/// Its own record rather than reusing <see cref="GroundEffectType"/> because the file carries
/// only what a name needs: the install has the Stat column and this does not, and a type built
/// from here must not claim to know something it never read.
/// </remarks>
public sealed record VendoredGroundType(
    [property: JsonPropertyName("row")] int Row,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("buff")] string Buff,
    [property: JsonPropertyName("buffs")] int Buffs,
    [property: JsonPropertyName("harm")] string Harm = "",
    [property: JsonPropertyName("description")] string Description = "")
{
    /// <summary>The harm word as the enum, defaulting to Unclear for anything unrecognised.</summary>
    /// <remarks>
    /// UNCLEAR IS THE FALLBACK ON PURPOSE. A typo in the file, or a word added later that this
    /// build has never heard of, must not silently become "harmless" - that is the one wrong
    /// answer that would get somebody killed rather than merely confused.
    /// </remarks>
    public GroundHarm AsHarm => Harm.ToLowerInvariant() switch
    {
        "harmful" => GroundHarm.Harmful,
        "helpful" => GroundHarm.Helpful,
        _ => GroundHarm.Unclear,
    };
}

/// <summary>data/ground-effect-types.json as it sits on disk.</summary>
public sealed class VendoredGroundTypes
{
    [JsonPropertyName("types")]
    public List<VendoredGroundType> Types { get; init; } = [];

    /// <summary>Reads the vendored rows, or null when the file is missing or unreadable.</summary>
    public static VendoredGroundTypes? Load(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(
                File.ReadAllText(path), GroundTypeJsonContext.Default.VendoredGroundTypes);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Source-generated JSON, because the app ships Native AOT.</summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(VendoredGroundTypes))]
public sealed partial class GroundTypeJsonContext : JsonSerializerContext;

/// <summary>
/// The game's GroundEffectTypes table, read out of the install.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS. The GroundEffect component says almost nothing on its own: every ground effect
/// in every recording this project has is the same generic entity path,
/// `VisibleServerGroundEffect`, so the path cannot tell burning ground from a consecrated patch.
/// The one field that distinguishes them is a row index at +0x48, and the row it indexes lives in
/// the game's own data files rather than in memory.
///
/// WHAT THE ROW BUYS, and it is the whole feature: the game's name for the kind of ground, the
/// name of the buff it applies, the SENTENCE the game shows while somebody stands in it, and
/// whether that sentence describes damage. Damage is a property of the type expressed as its
/// buff - which is why no amount of reading the component itself ever produced a hazard bit, and
/// is worth recording as the reason that search kept failing rather than as a dead end.
///
/// LOADED THROUGH <see cref="QuestTables"/>, whose name is the only quest-specific thing about
/// it: it takes a layout and a table name, probes the paths a table can live at, parses, and
/// checks the row size against what the column list computes - falling back to reading a string
/// column when the size disagrees, which is how a layout that is one patch stale still gets used
/// for the part of the row that is still right. Duplicating that here to get a better-fitting
/// name would mean two copies of the only code that decides whether a table can be trusted.
///
/// FROM THE INSTALL AND NOT FROM THE PROCESS, which is worth saying now that
/// <see cref="Game.Files.LoadedDatTables"/> can reach any loaded table from FileRoot. That route
/// is the better one for most questions and is the wrong one for this: the resident copy of a
/// table carries the fixed-size rows and NOT the variable-length section, and `Id` is a string,
/// which lives in exactly the half that is missing. See the remarks on
/// <see cref="Game.Files.DatFile"/>, which was written for the same reason.
///
/// The two do have something to say to each other, and it is a cross-check worth remembering:
/// the resident table knows its own row COUNT and row SIZE, so a running game can confirm the
/// 64 bytes this vendored layout computes without parsing a file at all.
///
/// MISSING IS ORDINARY. No install found, a renamed table, a layout that stopped matching - all
/// of them end with <see cref="Why"/> saying so and every lookup returning null. The ring is
/// still drawn; it simply goes back to showing the entity path, which is where this started.
/// </remarks>
public sealed class GroundEffectTypeTable
{
    /// <summary>The table name as the game's files spell it.</summary>
    public const string Table = "GroundEffectTypes";

    /// <summary>A table that could not be opened, carrying the reason.</summary>
    public static GroundEffectTypeTable None(string why) => new(null, null, why ?? "not loaded");

    private readonly Dictionary<int, GroundEffectType> _rows = [];

    private GroundEffectTypeTable(LoadedTable? table, QuestTableLayouts? layouts, string why)
    {
        Why = why;
        if (table is null || layouts is null)
        {
            return;
        }

        // ASKED FOR, NEVER WRITTEN DOWN HERE. Rows are packed with no padding, so an offset is
        // only the sum of the widths in front of it - a corrected width has to move every column
        // after it, and a copy of the answers in this file would drift away from the column list
        // silently. That is the whole reason the layout is data.
        int idAt = layouts.OffsetOf(Table, "Id");
        int statAt = layouts.OffsetOf(Table, "Stat");
        int buff1At = layouts.OffsetOf(Table, "BuffDefinition1");
        int buff2At = layouts.OffsetOf(Table, "BuffDefinition2");
        if (idAt < 0 || statAt < 0 || buff1At < 0 || buff2At < 0)
        {
            Why = $"{table.Where}: the column layout does not name Id, Stat and both BuffDefinitions";
            return;
        }

        DatFile file = table.File;
        for (var row = 0; row < file.Rows; row++)
        {
            _rows[row] = new GroundEffectType(
                row,
                file.Text(row, idAt),
                (file.Reference(row, buff1At).IsNothing ? 0 : 1) + (file.Reference(row, buff2At).IsNothing ? 0 : 1),
                !file.Reference(row, statAt).IsNothing);
        }

        Why = $"{table.Where}: {file.Rows} rows of {file.RowSize} bytes"
            + (table.Agrees ? string.Empty : $" (the column list computes {table.Expected})");
    }

    /// <summary>How many rows were read. Zero means the table was not usable.</summary>
    public int Rows => _rows.Count;

    /// <summary>Where the table came from, or why there is none - for a readout.</summary>
    /// <remarks>
    /// A LABEL THAT SILENTLY FALLS BACK to the generic path looks identical to one that resolved
    /// a row whose name happens to be empty. This is the difference, and it is the first thing
    /// anybody will want when the names do not appear.
    /// </remarks>
    public string Why { get; }

    /// <summary>Reads the table, preferring the install and falling back to the vendored copy.</summary>
    /// <param name="files">The opened game archive, or null when there is no install.</param>
    /// <param name="layoutPath">Where data/ground-tables.json is.</param>
    /// <param name="vendoredPath">Where data/ground-effect-types.json is.</param>
    /// <remarks>
    /// THE INSTALL WINS WHEN THERE IS ONE, because it cannot go stale: a patch that adds a row
    /// is in it the day it ships. The vendored copy exists so the feature is not dead on a
    /// machine that only replays recordings - which is every machine the tests run on, and is
    /// why the resolution can be tested at all rather than only its failure modes.
    /// </remarks>
    public static GroundEffectTypeTable Load(GameFiles? files, string? layoutPath, string? vendoredPath = null)
    {
        QuestTableLayouts? layouts = layoutPath is not null && File.Exists(layoutPath)
            ? QuestTableLayouts.Load(layoutPath)
            : null;

        if (files is not null && layouts is not null)
        {
            // Id is the string column the fallback check reads. It is the only column here that
            // CAN be checked from the bytes alone, which makes it the one worth naming.
            (LoadedTable? table, string why) =
                QuestTables.Open(files, layouts, Table, arrayColumn: null, "Id");
            if (table is not null)
            {
                return new GroundEffectTypeTable(table, layouts, why);
            }

            return Vendored(vendoredPath, $"the install has no usable table ({why}); using the vendored copy");
        }

        string reason = files is null
            ? "no game install was opened"
            : $"the column layout is missing or unreadable: {layoutPath ?? "(no path)"}";
        return Vendored(vendoredPath, $"{reason}; using the vendored copy");
    }

    /// <summary>The table as this project ships it, when the install could not supply one.</summary>
    private static GroundEffectTypeTable Vendored(string? path, string why)
    {
        VendoredGroundTypes? shipped = VendoredGroundTypes.Load(path);
        if (shipped is null || shipped.Types.Count == 0)
        {
            return None($"{why} - and {path ?? "(no path)"} is missing or empty, so nothing can be named");
        }

        var table = new GroundEffectTypeTable(null, null, $"{why}: {shipped.Types.Count} rows");
        foreach (VendoredGroundType row in shipped.Types)
        {
            // HasStat is FALSE rather than unknown, and that is deliberate: the vendored file
            // does not carry the Stat column, so claiming one would be inventing a reading.
            table._rows[row.Row] = new GroundEffectType(
                row.Row, row.Id, row.Buffs, false, row.Buff, row.AsHarm, row.Description);
        }

        return table;
    }

    /// <summary>The row the component pointed at, or null when it is not in the table.</summary>
    /// <remarks>
    /// OUT OF RANGE IS A REAL ANSWER, not an error to swallow: it means the offset is wrong, the
    /// table moved, or the value was never a row index at all - and the honest response is to
    /// show nothing rather than to name the wrong kind of ground with confidence.
    /// </remarks>
    public GroundEffectType? Find(int? row)
        => row is { } at && _rows.TryGetValue(at, out GroundEffectType? found) ? found : null;

    /// <summary>Every row, for a diagnostic that has to show what the table actually contains.</summary>
    public IReadOnlyList<GroundEffectType> All => [.. _rows.Values.OrderBy(r => r.Row)];
}
