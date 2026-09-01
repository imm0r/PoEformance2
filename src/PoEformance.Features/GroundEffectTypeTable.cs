using PoEformance.Game.Files;

namespace PoEformance.Features;

/// <summary>One row of the game's GroundEffectTypes table.</summary>
/// <param name="Row">The row index, which is what the component carries.</param>
/// <param name="Id">The game's own name for this kind of ground.</param>
/// <param name="Buffs">
/// How many of the two BuffDefinition columns are set. THE CLOSEST THING TO AN ANSWER about
/// damage that this table gives without a second lookup: a type that applies no buff at all
/// cannot be doing anything to anybody, and a type that applies two is doing something twice.
/// It does not say the buff is harmful - a shrine's ground applies a buff you want - so this
/// counts rather than judges.
/// </param>
/// <param name="HasStat">Whether the row names a Stat, which is the other half of the same story.</param>
public sealed record GroundEffectType(int Row, string Id, int Buffs, bool HasStat)
{
    /// <summary>What to show when the row is known: the game's name, else the bare index.</summary>
    public string Caption => Id.Length > 0 ? Id : $"type {Row}";
}

/// <summary>
/// The game's GroundEffectTypes table, read out of the install.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS. The GroundEffect component says almost nothing: every ground effect in every
/// recording this project has is the same generic entity path, `VisibleServerGroundEffect`, so
/// the path cannot tell a burning patch from the glow under a league object. The one field that
/// distinguishes them is a row index at +0x48 - constant on 72 of 72 entities across their whole
/// lives - and the row it indexes is in the game's own data files, not in memory.
///
/// WHAT THE ROW BUYS. `Id` names the kind of ground, which turns an overlay label from the
/// useless generic path into something a person can act on. `BuffDefinition1` and
/// `BuffDefinition2` are where damage actually lives: a ground effect hurts because of the buff
/// its TYPE applies, which is why no amount of reading the component ever produced a hazard bit.
/// Resolving those buffs to their own names is a further step and a further table; this one
/// counts them, because "applies no buff at all" is already a strong statement and costs nothing.
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

    /// <summary>Opens the table out of a game install, saying why when it cannot.</summary>
    /// <param name="files">The opened game archive, or null when there is no install.</param>
    /// <param name="layoutPath">Where data/ground-tables.json is.</param>
    public static GroundEffectTypeTable Load(GameFiles? files, string? layoutPath)
    {
        if (files is null)
        {
            return None("no game install was opened, so ground effect types cannot be named");
        }

        if (layoutPath is null || !File.Exists(layoutPath))
        {
            return None($"the column layout is missing: {layoutPath ?? "(no path)"}");
        }

        QuestTableLayouts? layouts = QuestTableLayouts.Load(layoutPath);
        if (layouts is null)
        {
            return None($"{layoutPath} did not parse as a column layout");
        }

        // Id is the string column the fallback check reads. It is the only column here that CAN
        // be checked from the bytes alone, which is exactly what makes it the one worth naming.
        (LoadedTable? table, string why) = QuestTables.Open(files, layouts, Table, arrayColumn: null, "Id");
        return table is null ? None(why) : new GroundEffectTypeTable(table, layouts, why);
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
