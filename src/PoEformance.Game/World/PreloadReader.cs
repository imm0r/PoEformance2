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

    /// <summary>How far into a record the sweep looks for the count field.</summary>
    public const int SweepBytes = 0x100;

    /// <summary>What a sweep of the records found.</summary>
    /// <param name="Records">Slots that led to something readable.</param>
    /// <param name="Named">Records whose name read as a plausible path - the struct's own check.</param>
    /// <param name="Samples">A few names, so "plausible" can be judged rather than trusted.</param>
    /// <param name="CountAt">
    /// Offsets that hold the counter's value, and how many records agree. The answer, when
    /// there is one: a field the whole table agrees on is the field.
    /// </param>
    /// <param name="NearbyValues">
    /// What the current <see cref="Chosen"/> offset actually holds, most common first. This is
    /// what says whether the offset is wrong or the COMPARISON is - a table where nearly every
    /// record reads 187 against a counter of 188 is not a drifted offset.
    /// </param>
    public readonly record struct PreloadSweep(
        int Slots,
        int Records,
        int Named,
        IReadOnlyList<string> Samples,
        IReadOnlyList<(int Offset, int Agreeing)> CountAt,
        IReadOnlyList<(int Value, int Records)> NearbyValues,
        int Chosen);

    /// <summary>
    /// Looks for the field that holds the area-change count, instead of assuming one.
    /// </summary>
    /// <remarks>
    /// THE ANSWER TO "IT WALKED THOUSANDS OF SLOTS AND MATCHED NOTHING". That sentence has
    /// several causes and they want opposite fixes: the record struct could have moved, the
    /// count could sit at a different offset, or the game could increment its counter more
    /// than once per area so that the files carry a number NEAR the counter rather than equal
    /// to it. Guessing between them from one number is hopeless.
    ///
    /// So this reads a window of each record ONCE and asks two questions of it: does the name
    /// read as a path - which says whether the struct base is right at all - and which offsets
    /// in the window hold the counter's value. An offset the whole table agrees on is the
    /// field, found rather than guessed; and the values at the CURRENT offset say whether the
    /// problem was ever an offset in the first place.
    ///
    /// This is the drift scanner the architecture promises, in the one place it was needed
    /// first. Slow and deliberate - it exists to be pressed once.
    /// </remarks>
    public PreloadSweep Sweep(ulong fileRootStatic, int areaChangeCount, int mostRecords = 4_000)
    {
        var samples = new List<string>();
        var agreeing = new Dictionary<int, int>();
        var atChosen = new Dictionary<int, int>();
        int slots = 0;
        int records = 0;
        int named = 0;

        ulong root = _reader.ReadPointer(fileRootStatic);
        if (!MemoryReaderExtensions.IsPlausiblePointer(root))
        {
            return new PreloadSweep(0, 0, 0, [], [], [], _recordCount);
        }

        Span<byte> window = stackalloc byte[SweepBytes];

        for (int b = 0; b < _bucketCount && records < mostRecords; b++)
        {
            foreach (ulong record in RecordsIn(root + (ulong)(b * _bucketSize)))
            {
                slots++;
                if (records >= mostRecords)
                {
                    break;
                }

                // Shrinking on failure rather than demanding the whole window. A record near
                // the end of a mapped page cannot supply 0x100 bytes, and ReadProcessMemory
                // refuses the WHOLE range when any of it is unmapped - so asking for one size
                // and giving up would skip exactly the records at page ends.
                int have = SweepBytes;
                while (have >= sizeof(int) && !_reader.TryRead(record, window[..have]))
                {
                    have /= 2;
                }

                if (have < sizeof(int))
                {
                    continue;
                }

                records++;

                string path = _reader.ReadStdWString(record + (ulong)_recordName, LongestPath);
                if (LooksLikeAPath(path))
                {
                    named++;
                    if (samples.Count < 5)
                    {
                        samples.Add(path);
                    }
                }

                // Every four-byte slot in the window that happens to hold the counter. The
                // real field is the one thousands of records agree on; a stray match is one.
                for (int at = 0; at + sizeof(int) <= have; at += sizeof(int))
                {
                    if (BitConverter.ToInt32(window[at..]) == areaChangeCount)
                    {
                        agreeing[at] = agreeing.GetValueOrDefault(at) + 1;
                    }
                }

                if (_recordCount + sizeof(int) <= have)
                {
                    int here = BitConverter.ToInt32(window[_recordCount..]);
                    atChosen[here] = atChosen.GetValueOrDefault(here) + 1;
                }
            }
        }

        return new PreloadSweep(
            slots,
            records,
            named,
            samples,
            [.. agreeing.OrderByDescending(e => e.Value).Take(6).Select(e => (e.Key, e.Value))],
            [.. atChosen.OrderByDescending(e => e.Value).Take(6).Select(e => (e.Key, e.Value))],
            _recordCount);
    }

    /// <summary>Every record a bucket points at, however many that turns out to be.</summary>
    private IEnumerable<ulong> RecordsIn(ulong bucket)
    {
        if (!_reader.TryRead(bucket + (ulong)_bucketCapacity, out int capacity) || capacity <= 0)
        {
            yield break;
        }

        ulong first = _reader.ReadPointer(bucket);
        ulong last = _reader.ReadPointer(bucket + sizeof(ulong));
        if (!MemoryReaderExtensions.IsPlausiblePointer(first) || last <= first)
        {
            yield break;
        }

        long slots = (long)(last - first) / _slotSize;
        if (slots <= 0 || slots > MostSlotsPerBucket)
        {
            yield break;
        }

        for (long i = 0; i < slots; i++)
        {
            ulong record = _reader.ReadPointer(first + (ulong)(i * _slotSize) + (ulong)_slotRecord);
            if (MemoryReaderExtensions.IsPlausiblePointer(record))
            {
                yield return record;
            }
        }
    }

    /// <summary>
    /// Whether a string reads as one of the game's file paths.
    /// </summary>
    /// <remarks>
    /// The check that says whether the RECORD is right, separately from the count. A table
    /// full of readable paths with no matching counts is a count-offset problem; a table of
    /// gibberish is a struct problem, and the two want completely different work.
    /// </remarks>
    public static bool LooksLikeAPath(string text)
        => text.Length >= 4 && text.Contains('/', StringComparison.Ordinal)
            && !text.Any(char.IsControl);

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
