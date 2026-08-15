using System.Text.Json;
using System.Text.Json.Serialization;
using PoEformance.Game.Files;

namespace PoEformance.Features;

/// <summary>One column of a .dat table, as the community schema declares it.</summary>
public sealed record DatColumn(string Name, string Type, bool Array)
{
    /// <summary>Where this column starts in a row. Filled in by the layout pass.</summary>
    public int Offset { get; set; }

    /// <summary>How wide it is.</summary>
    public int Width => Array ? DatColumns.ArrayWidth : DatColumns.WidthOf(Type);
}

/// <summary>
/// Column layouts for the quest tables, and the arithmetic that turns them into offsets.
/// </summary>
/// <remarks>
/// The offsets are RECOMPUTED here rather than stored in the data file, which is the point:
/// rows are packed with no alignment padding, so an offset is nothing but the sum of the
/// widths in front of it, and a corrected width has to move every column after it. Storing
/// the answers would let the two drift apart silently.
///
/// The width table is the one <c>scripts/dat-offsets.ps1</c> verified - the only combination
/// of sixteen that reproduces every dat-row offset this project had already found the hard
/// way. Two of them were guessed wrong first, and both are noted there.
/// </remarks>
public static class DatColumns
{
    /// <summary>A count and an offset, for an array of anything.</summary>
    public const int ArrayWidth = 16;

    /// <summary>How wide a column of each type is, or 0 for a type nobody has priced.</summary>
    public static int WidthOf(string? type) => type switch
    {
        "bool" or "i8" or "u8" => 1,
        "i16" or "u16" => 2,
        "i32" or "u32" or "f32" or "enumrow" or "rid" => 4,
        // In the file an offset into the variable-length section; in memory a pointer.
        "string" => 8,
        // A row of ANOTHER table: two 64-bit words. Which is which is resolved by asking.
        "foreignrow" => 16,
        // A row of the SAME table - no table reference to store, so half the width.
        "row" => 8,
        _ => 0,
    };

    /// <summary>Assigns each column its offset and returns the row size.</summary>
    public static int Layout(IReadOnlyList<DatColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        var at = 0;
        foreach (DatColumn column in columns)
        {
            column.Offset = at;
            int width = column.Width;
            if (width == 0)
            {
                // A type this table does not price makes every offset after it a guess, so
                // the layout is abandoned rather than continued with a hole in it.
                return 0;
            }

            at += width;
        }

        return at;
    }
}

/// <summary>The vendored column layouts, as they sit in data/quest-tables.json.</summary>
public sealed class QuestTableLayouts
{
    [JsonPropertyName("tables")]
    public Dictionary<string, List<DatColumn>> Tables { get; init; } = [];

    /// <summary>Reads the layouts, or null when the file is missing or unreadable.</summary>
    public static QuestTableLayouts? Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), QuestTableJsonContext.Default.QuestTableLayouts);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The offset of a named column, or -1 when the table or column is unknown.</summary>
    public int OffsetOf(string table, string column)
    {
        if (!Tables.TryGetValue(table, out List<DatColumn>? columns))
        {
            return -1;
        }

        DatColumns.Layout(columns);
        return columns.FirstOrDefault(c => c.Name == column)?.Offset ?? -1;
    }

    /// <summary>The row size the column list computes, or 0 when it cannot be computed.</summary>
    public int RowSizeOf(string table)
        => Tables.TryGetValue(table, out List<DatColumn>? columns) ? DatColumns.Layout(columns) : 0;
}

/// <summary>Source-generated JSON, because the app ships Native AOT.</summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(QuestTableLayouts))]
public sealed partial class QuestTableJsonContext : JsonSerializerContext;

/// <summary>A table that was found and opened, with the check that it is the right shape.</summary>
/// <param name="Where">The path it came from, for the readout.</param>
/// <param name="Expected">The row size the column list computes.</param>
public sealed record LoadedTable(DatFile File, string Where, int Expected)
{
    /// <summary>
    /// True when the file's own row size matches the one the columns compute.
    /// </summary>
    /// <remarks>
    /// The whole reason the file is preferred over a declared layout: a .dat says how big its
    /// rows are, by where it puts the separator. Disagreement means the column list is for a
    /// different version of the table, and every field read through it would be silently
    /// wrong - so it is reported and the table is not used.
    /// </remarks>
    public bool Agrees => Expected > 0 && File.RowSize == Expected;

    /// <summary>One line about what was found, whether or not it agreed.</summary>
    public string Say => Agrees
        ? $"{Where}: {File.Rows} rows of {File.RowSize} bytes"
        : $"{Where}: {File.Rows} rows of {File.RowSize} bytes, but the column list computes {Expected} - NOT USED";
}

/// <summary>
/// Finds and opens the three quest tables out of the game install.
/// </summary>
/// <remarks>
/// THE PATHS ARE PROBED, not assumed. The only quest-table path this project has ever seen is
/// the one the client holds in memory - "Data/Balance/QuestFlags.dat" - and whether the other
/// two sit beside it, or directly under Data/, is not something a recording can answer. So
/// every candidate is asked of the bundle index, which costs a hash lookup each, and the one
/// that exists is reported. A wrong guess then reads as "not found here, looked in these
/// places" instead of as a feature that does nothing.
/// </remarks>
public static class QuestTables
{
    /// <summary>Where a table might live, in the order they are tried.</summary>
    public static IEnumerable<string> Candidates(string table)
    {
        ArgumentException.ThrowIfNullOrEmpty(table);

        yield return $"data/{table.ToLowerInvariant()}.datc64";
        yield return $"data/{table.ToLowerInvariant()}.dat";
        yield return $"data/balance/{table.ToLowerInvariant()}.datc64";
        yield return $"data/balance/{table.ToLowerInvariant()}.dat";
    }

    /// <summary>Opens one table, saying where it looked when it fails.</summary>
    public static (LoadedTable? Table, string Why) Open(
        GameFiles files, QuestTableLayouts layouts, string table)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(layouts);

        int expected = layouts.RowSizeOf(table);
        foreach (string path in Candidates(table))
        {
            if (!files.Has(path))
            {
                continue;
            }

            DatFile? parsed = DatFile.Parse(files.Read(path));
            if (parsed is null)
            {
                return (null, $"{path} is in the install but did not parse as a .dat");
            }

            var loaded = new LoadedTable(parsed, path, expected);
            return loaded.Agrees ? (loaded, loaded.Say) : (null, loaded.Say);
        }

        return (null, $"{table}: not found - looked in {string.Join(", ", Candidates(table))}");
    }
}
