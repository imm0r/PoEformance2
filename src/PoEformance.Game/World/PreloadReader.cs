using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Game.World;

/// <summary>
/// Which files the game loaded for the area you are standing in.
/// </summary>
/// <remarks>
/// WHAT AN AREA CONTAINS, BEFORE YOU HAVE WALKED IT. The game loads an encounter's assets when
/// the area is generated, not when you reach it - so the file list already names the breach,
/// the essence, the boss, and every unique monster in the map while you are still at the
/// entrance. Nothing else in the tool can answer that: entities exist only within the awake
/// radius, so a radar cannot see the far half of a map at all.
///
/// The trick is one field. Every loaded file records the value the area-change counter held
/// when it was loaded, so the files whose count EQUALS the counter now are exactly the ones
/// THIS area brought in - everything else is the menu, the character, the last three zones.
/// Counts below three are skipped because the first areas of a session drag the whole game in
/// with them.
///
/// EXPENSIVE, AND ONCE. Sixteen buckets of a few thousand slots, a pointer to follow and a
/// string to read for each. That is a whole read budget and then some, which is why it runs on
/// its own thread when an area changes and never on a tick.
/// </remarks>
public sealed class PreloadReader
{
    private readonly IMemoryReader _reader;
    private readonly int _bucketCount;
    private readonly int _bucketSize;
    private readonly int _bucketCapacity;
    private readonly int _slotSize;
    private readonly int _slotRecord;
    private readonly int _recordName;
    private readonly int _recordCount;
    private readonly int _ignoreFirstAreas;

    /// <summary>
    /// Most slots walked in one bucket.
    /// </summary>
    /// <remarks>
    /// A guard on a length that comes from game memory, not a view about how many files an
    /// area has. A bad read makes the vector's end pointer arbitrary, and the difference
    /// between "a hundred thousand slots" and "walk until the process falls over" is this
    /// number.
    /// </remarks>
    public const int MostSlotsPerBucket = 200_000;

    /// <summary>Longest path taken seriously. Anything longer is a bad read, not a file.</summary>
    public const int LongestPath = 256;

    public PreloadReader(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;

        StructDef root = schema.Structs["LoadedFilesRoot"];
        StructDef bucket = schema.Structs["LoadedFilesBucket"];
        StructDef slot = schema.Structs["FileRecordSlot"];
        StructDef record = schema.Structs["FileRecord"];

        _bucketCount = (int)root.Constants["BucketCount"];
        _bucketSize = (int)root.Constants["BucketSize"];
        _bucketCapacity = bucket.OffsetOf("Capacity");
        _slotSize = (int)slot.Constants["Size"];
        _slotRecord = slot.OffsetOf("Record");
        _recordName = record.OffsetOf("Name");
        _recordCount = record.OffsetOf("AreaChangeCount");
        _ignoreFirstAreas = (int)record.Constants["IgnoreFirstAreas"];
    }

    /// <summary>What went wrong, when nothing came back.</summary>
    public string LastError { get; private set; } = string.Empty;

    /// <summary>How many slots were looked at last time. For judging the cost.</summary>
    public int SlotsWalked { get; private set; }

    /// <summary>
    /// The area-change counter's current value.
    /// </summary>
    /// <remarks>
    /// Read separately from the file list because it is the thing the list is COMPARED
    /// against, and reading it after the walk would race a zone change: files loaded for the
    /// area you just left would match a counter that had already moved on.
    /// </remarks>
    public int AreaChangeCount(ulong counterStatic)
        => _reader.TryRead(counterStatic, out int count) ? count : 0;

    /// <summary>
    /// Every file this area loaded, by path.
    /// </summary>
    /// <param name="fileRootStatic">The FileRoot static's address.</param>
    /// <param name="areaChangeCount">What the counter reads now - see <see cref="AreaChangeCount"/>.</param>
    /// <remarks>
    /// Returns an empty set rather than throwing on anything unexpected, and says why in
    /// <see cref="LastError"/>. This walks several thousand pointers that came out of game
    /// memory; treating each of them as certainly valid is how a feature takes the process
    /// down on the one patch where a struct moved.
    /// </remarks>
    public HashSet<string> Read(ulong fileRootStatic, int areaChangeCount)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        SlotsWalked = 0;
        LastError = string.Empty;

        if (areaChangeCount <= _ignoreFirstAreas)
        {
            // The first areas of a session load the whole game with them, so every file in
            // memory would match. Saying so beats returning three thousand paths.
            LastError = $"only {areaChangeCount} areas loaded so far - the list is still the whole game";
            return found;
        }

        ulong root = _reader.ReadPointer(fileRootStatic);
        if (!MemoryReaderExtensions.IsPlausiblePointer(root))
        {
            LastError = "the file root did not resolve";
            return found;
        }

        for (int i = 0; i < _bucketCount; i++)
        {
            WalkBucket(root + (ulong)(i * _bucketSize), areaChangeCount, found);
        }

        if (found.Count == 0 && LastError.Length == 0)
        {
            LastError = $"walked {SlotsWalked} slots and matched nothing at count {areaChangeCount}";
        }

        return found;
    }

    /// <summary>Walks one bucket's vector, adding whatever belongs to this area.</summary>
    private void WalkBucket(ulong bucket, int areaChangeCount, HashSet<string> into)
    {
        // From its own offset. Reading an int at the bucket's start would take the low half
        // of the vector's FIRST POINTER and call it a capacity - which passes the check
        // almost always, and would have made this gate meaningless rather than wrong-looking.
        if (!_reader.TryRead(bucket + (ulong)_bucketCapacity, out int capacity) || capacity <= 0)
        {
            return;   // an empty bucket is ordinary, not a failure
        }

        ulong first = _reader.ReadPointer(bucket);
        ulong last = _reader.ReadPointer(bucket + sizeof(ulong));

        if (!MemoryReaderExtensions.IsPlausiblePointer(first) || last <= first)
        {
            return;
        }

        long slots = (long)(last - first) / _slotSize;
        if (slots <= 0)
        {
            return;
        }

        if (slots > MostSlotsPerBucket)
        {
            LastError = $"a bucket claimed {slots} slots - the layout has drifted";
            return;
        }

        for (long i = 0; i < slots; i++)
        {
            SlotsWalked++;
            ulong record = _reader.ReadPointer(first + (ulong)(i * _slotSize) + (ulong)_slotRecord);
            if (!MemoryReaderExtensions.IsPlausiblePointer(record))
            {
                continue;   // an empty slot - a hash table is mostly empty
            }

            if (!_reader.TryRead(record + (ulong)_recordCount, out int loadedAt) || loadedAt != areaChangeCount)
            {
                continue;
            }

            string path = _reader.ReadStdWString(record + (ulong)_recordName, LongestPath);
            if (path.Length > 0)
            {
                into.Add(Tidy(path));
            }
        }
    }

    /// <summary>
    /// The path as it should be remembered.
    /// </summary>
    /// <remarks>
    /// Everything after an '@' is dropped. The game appends a variant marker to some records
    /// - the same file turns up several times with different suffixes - and keeping them
    /// would list one encounter three times under names that differ by nothing anybody cares
    /// about.
    /// </remarks>
    public static string Tidy(string path)
    {
        int at = path.IndexOf('@', StringComparison.Ordinal);
        return at >= 0 ? path[..at] : path;
    }
}
