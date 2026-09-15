using System.Collections.Concurrent;
using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Files;

namespace PoEformance.Game.Components;

/// <summary>
/// The game's own Stats.dat, read a row at a time.
/// </summary>
/// <remarks>
/// WHAT THIS RETIRES. data/stat_name_map.tsv is 27000 lines of "row index, stat name" extracted
/// from a dump of this very table, and a stat id is a POSITION in it - so a league that inserts a
/// row moves every name after it and the file is wrong from that patch on. Measured 2026-09-15
/// against the 148 rows a capture could name: TEN agreed and 135 did not, the first disagreement at
/// index 4678, and 134 of the file's names turned up elsewhere in the game's table - moved by 1, 2,
/// 3, 6, 7 or 9. Several insertions, not one.
///
/// THE PROJECT PREDICTED THIS AND COULD NOT SETTLE IT. StatNames records that every live-verified
/// reading lies between 1 and 2034 with one at 4290, and says in as many words that beyond that it
/// is EXTRAPOLATION - "a high id can be off by a different amount than a low one, and nothing here
/// would say so". The first drift is at 4678, just above the highest check. Every verification was
/// below the break, which is exactly why the file looked sound.
///
/// REACHED WITHOUT ANYTHING ON SCREEN, which is what makes it usable for the entity browser rather
/// than only for the atlas. Stats.dat is in the resource loader's own file table (measured: 27281
/// rows, DatTableSurvey055Tests), unlike the six tables the atlas work reaches by dat reference, so
/// LoadedDatTables finds it in any area.
///
/// A ROW AT A TIME AND CACHED. Reading all 27281 ids is 27281 string reads for the handful an
/// entity actually carries, so nothing is read until it is asked for and nothing is read twice.
/// </remarks>
public sealed class StatTable
{
    /// <summary>What the table is called in the loader's file list.</summary>
    public const string TableName = "Stats";

    /// <summary>Longest stat id taken seriously. The longest in the game is well under this.</summary>
    private const int MostChars = 128;

    private readonly IMemoryReader _reader;
    private readonly int _id;

    // Read once per row and never again. Concurrent because the reader thread fills it while the
    // interface may be walking what it has - a lock would serialise a draw behind a memory read.
    private readonly ConcurrentDictionary<long, string> _names = new();

    private StatTable(IMemoryReader reader, DatTableFacts facts, int idOffset)
    {
        _reader = reader;
        Facts = facts;
        _id = idOffset;
    }

    /// <summary>What the table said about itself.</summary>
    public DatTableFacts Facts { get; }

    /// <summary>How many names have actually been read so far.</summary>
    public int Named => _names.Count;

    /// <summary>
    /// Finds Stats.dat in a file-table walk that has already run.
    /// </summary>
    /// <returns>The table, or null when the walk did not find it or it does not look like Stats.</returns>
    public static StatTable? From(LoadedDatTables tables, IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(tables);

        foreach (LoadedDatTable found in tables.FindAll(TableName))
        {
            if (Over(reader, found.Facts, schema) is { } table)
            {
                return table;
            }
        }

        return null;
    }

    /// <summary>
    /// The same reader over facts that came from somewhere other than the file-table walk.
    /// </summary>
    /// <remarks>
    /// THE ATLAS REACHES THIS TABLE THE OTHER WAY - a content row's Stats array is a dat foreign
    /// reference, so the table travels with it and needs no walk at all. Which route found it does
    /// not change what a row says, so both end up here.
    /// </remarks>
    /// <returns>The reader, or null when the facts do not describe a table rows can be read from.</returns>
    public static StatTable? Over(IMemoryReader reader, DatTableFacts facts, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(schema);

        // Rows too few or too narrow to hold a string pointer are not this table however
        // confidently it named itself - the walk reads its path out of game memory.
        return facts.Rows > 0 && facts.RowSize >= sizeof(ulong)
            ? new StatTable(reader, facts, schema.Structs["StatsRow"].OffsetOf("IdPtr"))
            : null;
    }

    /// <summary>
    /// The game's name for one row - <c>map_num_extra_shrines</c> - or null when there is none.
    /// </summary>
    /// <param name="index">
    /// The ROW INDEX, zero-based. A stat id in memory is this plus one; applying that shift is
    /// StatNames' business and deliberately not this class's, so there is one place it lives.
    /// </param>
    public string? Of(long index)
    {
        if (index < 0 || index >= Facts.Rows)
        {
            return null;
        }

        if (_names.TryGetValue(index, out string? known))
        {
            return known.Length > 0 ? known : null;
        }

        ulong at = _reader.ReadPointer(Facts.RowsBegin + (ulong)(index * Facts.RowSize) + (ulong)_id);
        string name = MemoryReaderExtensions.IsPlausiblePointer(at)
            ? _reader.ReadUnicodeString(at, MostChars)
            : string.Empty;

        // The empty string is cached too: a row whose id will not read is a row that will not read
        // the next time either, and re-asking it on every draw is the cost this cache exists to stop.
        _names[index] = name;
        return name.Length > 0 ? name : null;
    }
}
