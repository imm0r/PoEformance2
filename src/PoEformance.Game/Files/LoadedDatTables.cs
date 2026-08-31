using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.World;

namespace PoEformance.Game.Files;

/// <summary>One of the game's content tables, and where it sits in this process.</summary>
/// <param name="Address">The table object - a file record, and the thing a foreign reference names.</param>
/// <param name="Facts">What it says about itself: path, name, row count and row size.</param>
public readonly record struct LoadedDatTable(ulong Address, DatTableFacts Facts);

/// <summary>
/// Every .dat table the game has loaded, reached from a static rather than from luck.
/// </summary>
/// <remarks>
/// THE MISSING HALF OF EVERY DAT ROW THIS TOOL READS. Rows have been reachable for months, but
/// only sideways: a MinimapIcon component holds one, an NPC holds one, a chest holds one - so
/// the tables this project could read were exactly the tables something on screen happened to
/// point at. A table nothing points at - the glossary, the stat descriptions, the base item
/// types - was not far away, it was unreachable.
///
/// It stopped being unreachable when a dat table turned out to BE a loaded-file record (the
/// evidence is on the schema's DatTable). The game already keeps an index of every file it has
/// loaded, hanging off the FileRoot static, and the .dat files in it are the tables themselves,
/// row store and all. So this is the whole route:
///
///     FileRoot -> LoadedFilesRoot -> bucket -> FileRecordSlot.Record -> DatTable
///
/// WHAT DECIDES WHICH RECORDS ARE TABLES is not the name. Matching ".dat" on a path would cost
/// a string read for all eight thousand records to find ninety, and would still believe a file
/// that is named like a table but not parsed as one. <see cref="PointerPeek.DescribeTable"/>
/// asks the structure instead - a row store, two containers that divide exactly, and a by-Id
/// index whose first entry points at the first row - and it reads the path only once a record
/// has passed. For nearly every record that is a single read that fails, because a file that
/// is not a table has nothing plausible where the row store would be.
///
/// EXPENSIVE, AND ONCE, exactly as the preload walk is: several thousand records, and a
/// handful of reads for the ones that look promising. Tables are loaded once and never freed
/// while the game runs, so the answer does not go stale - walk it on a thread when something
/// first needs a table, and keep the list.
/// </remarks>
public sealed class LoadedDatTables
{
    /// <summary>
    /// The one walker of the loaded-file table.
    /// </summary>
    /// <remarks>
    /// A PreloadReader for its <see cref="PreloadReader.Records"/> and nothing else. The class
    /// is named after the question it was written for; the walk down to a file record is the
    /// same walk either way, and duplicating it here would be two copies of a bucket stride.
    /// </remarks>
    private readonly PreloadReader _files;

    private readonly IMemoryReader _reader;
    private readonly DatTableShape? _shape;

    public LoadedDatTables(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;
        _files = new PreloadReader(reader, schema);
        _shape = DatTableShape.From(schema);
    }

    /// <summary>What went wrong, when nothing came back.</summary>
    public string LastError { get; private set; } = string.Empty;

    /// <summary>How many file records were looked at. For judging the cost.</summary>
    public int RecordsWalked { get; private set; }

    /// <summary>The tables found by the last <see cref="Read"/>.</summary>
    public IReadOnlyList<LoadedDatTable> Tables { get; private set; } = [];

    /// <summary>
    /// Walks the file table and keeps every record that turns out to be a dat table.
    /// </summary>
    /// <param name="fileRootStatic">The FileRoot static's address.</param>
    public IReadOnlyList<LoadedDatTable> Read(ulong fileRootStatic)
    {
        LastError = string.Empty;
        RecordsWalked = 0;
        Tables = [];

        if (_shape is not { } shape)
        {
            LastError = "the schema does not describe dat tables";
            return Tables;
        }

        var found = new List<LoadedDatTable>();
        foreach (ulong record in _files.Records(fileRootStatic))
        {
            RecordsWalked++;
            if (PointerPeek.DescribeTable(_reader, record, shape) is { } facts)
            {
                found.Add(new LoadedDatTable(record, facts));
            }
        }

        // The walker's own complaint, not ours: a root that did not resolve and a bucket whose
        // length has drifted both arrive here as an empty list, and the two want opposite work.
        if (_files.LastError.Length > 0)
        {
            LastError = _files.LastError;
        }
        else if (found.Count == 0)
        {
            LastError = $"walked {RecordsWalked} file records and none of them is a dat table";
        }

        Tables = found;
        return Tables;
    }

    /// <summary>
    /// Every loaded table of that name - "KeywordPopups", as dat-schema spells it.
    /// </summary>
    /// <remarks>
    /// A LIST BECAUSE THE GAME LOADS SOME TABLES TWICE. The localised copy of a table carries a
    /// language prefix on its path ("en:Data/Balance/ClientStrings.dat", seen in
    /// session-2026-08-deployed.rec), and both copies are records with the same table name. Which
    /// of them a caller wants is a question about the caller, so this hands over both rather than
    /// picking - see KeywordGlossary, which takes the first that decodes.
    /// </remarks>
    public IReadOnlyList<LoadedDatTable> FindAll(string tableName)
        => [.. Tables.Where(t => string.Equals(t.Facts.Name, tableName, StringComparison.OrdinalIgnoreCase))];
}
