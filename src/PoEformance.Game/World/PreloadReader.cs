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
/// The trick is one field. Every loaded file is STAMPED with an area-change count, so the files
/// carrying the NEWEST stamp are the ones this area is using, and everything older belongs to
/// the menu, the character and the zones before this one.
///
/// "The count that LOADED it, never re-stamped" is how that was first written down here, and a
/// recorded session says otherwise. In one capture the counter stood at 115; most records read
/// 1 or 2 - the whole game, dragged in at startup - but Stats.dat, Mods.dat, BaseItemTypes.dat,
/// Languages.dat and Acts.dat all read 115, and none of those was loaded by the map being stood
/// in. So the stamp is closer to "the area that last wanted this file": area-specific art gets
/// it on the way in, and the core tables get it because every area touches them.
///
/// The feature is unharmed and the reason is worth knowing. Nothing acts on the file COUNT; what
/// acts is a short list of paths that mean something, and no rule matches a balance table. But
/// the raw list is bigger than "what this area brought in" suggests, and anybody reading it - or
/// counting it - should expect the game's own furniture in there.
///
/// The newest stamp is found in the table rather than read from the game's area-change
/// counter, and BOTH are read anyway. That combination was arrived at the hard way, twice, in
/// opposite directions.
///
/// First the counter was wrong: the static resolved to another variable and reported 188 while
/// every record held a number two orders of magnitude smaller. The records won, because the
/// paths they hand back are real game files anybody can check, and taking the newest stamp from
/// the table made the feature independent of the static altogether.
///
/// Then, once the static was fixed and read 13, the table was wrong: no record admitted to more
/// than 2, because the stamp was being read one field past where it lives. So "the table cannot
/// be wrong" was too strong - the table is only as right as the offset it is read at, and that
/// offset is exactly as patchable as a byte pattern.
///
/// Neither number is trustworthy alone, and that is the point of keeping both. The stamp still
/// decides, because it is what the records are actually filtered by; the counter is carried
/// beside it as the only independent witness to how many areas there have really been. Every
/// time one of them has drifted, the disagreement is what showed it.
///
/// Stamps below three are skipped: the first areas of a session drag the whole game in with
/// them, so everything in memory would match.
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
    /// <remarks>
    /// Slots that led to a RECORD, since the walk goes through <see cref="RecordsIn"/>, which
    /// skips the empty ones - a hash table is mostly empty, and counting holes told nobody
    /// anything. "Walked 4000 slots and matched nothing" now means four thousand real files
    /// were looked at, which is the number that makes the sentence worth reading.
    /// </remarks>
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

        ulong root = _reader.ReadPointer(fileRootStatic);
        if (!MemoryReaderExtensions.IsPlausiblePointer(root))
        {
            LastError = "the file root did not resolve";
            return found;
        }

        // THE TABLE KNOWS ITS OWN GENERATION. Files carry the area-change count of the area
        // that wanted them, so the newest stamp in the table IS the current area - which means
        // the walk needs no counter to tell it which area it is in, and that turned out to
        // matter: a mis-derived static once reported 188 against
        // a table holding numbers two orders of magnitude smaller. The counter is read all the
        // same, one field below, because the reverse has happened too - see the type comment.
        //
        // Cheaper as well. The old shape read a name for every record whose count matched a
        // guess; this reads counts first - one int each - and only fetches strings for the
        // records that turned out to be the newest.
        Newest = HighestCount(root);
        Counter = areaChangeCount;

        if (Newest <= _ignoreFirstAreas)
        {
            // The first areas of a session load the whole game with them, so every file in
            // memory would match. Saying so beats returning three thousand paths - but only
            // when nothing worse has already been said: a table that could not be walked also
            // arrives here with a newest of zero, and "no areas loaded yet" would bury it.
            if (LastError.Length == 0)
            {
                // ...and only when it is TRUE. The counter is the one thing here that knows
                // how many areas there have really been, and if it says a dozen while no
                // record admits to more than two, then the records are not being read where
                // the stamp lives. That is what happened: a counter of 13 against a newest of
                // 2, and a sweep found 13 sitting one field earlier. The early-session wording
                // fits that state perfectly and explains it completely wrongly, which is worse
                // than saying nothing - so it only gets used when the counter agrees that the
                // session really is that young.
                LastError = RecordsSeen == 0
                    ? "the walk reached no records at all - the file root or the bucket layout"
                        + " is wrong, so the stamp offset is not the problem"
                    : Counter > _ignoreFirstAreas + 1
                        ? $"the newest stamp in the table is {Newest} but the counter says {Counter} - "
                            + "the stamp field has probably moved; try 'find the count field'"
                        : $"only {Newest} areas loaded so far - the list is still the whole game";
            }

            return found;
        }

        for (int i = 0; i < _bucketCount; i++)
        {
            WalkBucket(root + (ulong)(i * _bucketSize), Newest, found);
        }

        if (found.Count == 0 && LastError.Length == 0)
        {
            LastError = $"walked {SlotsWalked} slots and matched nothing at count {Newest}";
        }

        return found;
    }

    /// <summary>
    /// How many records the last walk of the table saw at all, whatever they held.
    /// </summary>
    /// <remarks>
    /// THE NUMBER THAT TELLS TWO FAILURES APART, and it was being computed and thrown away.
    /// A newest stamp of zero has two completely different causes wanting opposite fixes: the
    /// stamp sits at a different offset (records exist, their counts read wrong), or the walk
    /// never reached a record at all (the root or the bucket layout is wrong, and the stamp
    /// offset is irrelevant). Without this, both printed "the stamp field has probably moved"
    /// and sent somebody to a sweep that cannot help.
    /// </remarks>
    public int RecordsSeen { get; private set; }

    /// <summary>The newest area-change stamp anywhere in the table - the current area's.</summary>
    public int Newest { get; private set; }

    /// <summary>
    /// What the area-change counter static read, kept only as a cross-check.
    /// </summary>
    /// <remarks>
    /// Never decides which files come back - it only ever gets compared against
    /// <see cref="Newest"/>. Kept because a static that disagrees with the data it is supposed
    /// to describe is a finding, whichever of the two turns out to be wrong, and both of them
    /// have been. Shown next to the stamp in the window for that reason.
    /// </remarks>
    public int Counter { get; private set; }

    /// <summary>Reads every record's stamp and returns the newest.</summary>
    private int HighestCount(ulong root)
    {
        int newest = 0;
        RecordsSeen = 0;
        for (int b = 0; b < _bucketCount; b++)
        {
            foreach (ulong record in RecordsIn(root + (ulong)(b * _bucketSize)))
            {
                RecordsSeen++;
                if (_reader.TryRead(record + (ulong)_recordCount, out int loadedAt)
                    && loadedAt > newest && loadedAt < MostPlausibleCount)
                {
                    newest = loadedAt;
                }
            }
        }

        return newest;
    }

    /// <summary>
    /// Beyond this, a stamp is a bad read rather than an area count.
    /// </summary>
    /// <remarks>
    /// The newest stamp decides which files are shown, so ONE garbage record with a huge
    /// value would take the whole list with it - every real file would be older than it and
    /// nothing would match. A session does not reach a hundred thousand areas.
    /// </remarks>
    public const int MostPlausibleCount = 100_000;

    /// <summary>Walks one bucket's vector, adding whatever belongs to this area.</summary>
    /// <remarks>
    /// THROUGH <see cref="RecordsIn"/> RATHER THAN BESIDE IT, and that is the root-cause fix for
    /// the capacity bug rather than the bug itself. This method used to carry its own copy of the
    /// whole vector walk - the same capacity gate, the same pointer checks, the same slot maths -
    /// so removing the bad gate from RecordsIn fixed HighestCount and left this one still
    /// throwing the table away. Two walkers of one structure means every fix has to be made
    /// twice, and the second one is the one that gets forgotten. The file already says so on
    /// <see cref="Records"/>: one walker rather than two.
    /// </remarks>
    private void WalkBucket(ulong bucket, int areaChangeCount, HashSet<string> into)
    {
        foreach (ulong record in RecordsIn(bucket))
        {
            SlotsWalked++;

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

        // "Walked and found nothing" and "never started" both used to come back as a row of
        // zeros, which is the one confusion every diagnostic in this project has had to be
        // taught not to make.
        LastError = string.Empty;

        ulong root = _reader.ReadPointer(fileRootStatic);
        if (!MemoryReaderExtensions.IsPlausiblePointer(root))
        {
            LastError = "the file root did not resolve, so nothing was swept";
            return new PreloadSweep(0, 0, 0, [], [], [], _recordCount);
        }

        Span<byte> window = stackalloc byte[SweepBytes];

        // Evenly across the buckets rather than filling from the first. Bucket zero holds
        // the game's core data files - loaded at startup, all stamped with the same early
        // count - so a sample taken in order reports one value and misses every area file
        // there is. That is exactly what it did the first time it ran.
        int perBucket = Math.Max(1, mostRecords / _bucketCount);

        for (int b = 0; b < _bucketCount; b++)
        {
            int fromThisBucket = 0;
            foreach (ulong record in RecordsIn(root + (ulong)(b * _bucketSize)))
            {
                slots++;
                if (fromThisBucket >= perBucket)
                {
                    break;
                }

                fromThisBucket++;

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

    /// <summary>
    /// Every file record in the table, whichever area loaded it.
    /// </summary>
    /// <remarks>
    /// The walk without the question this class is named after. What is loaded is a stamp
    /// filter away from what THIS AREA loaded, and the other caller wants the opposite: a dat
    /// table is a file record (see the schema's DatTable), and the tables worth finding -
    /// KeywordPopups, Stats, BaseItemTypes - are the ones the game dragged in at startup and
    /// never re-stamped. So they are exactly what <see cref="Read"/> filters away.
    ///
    /// Public so there is one walker rather than two. The bucket stride, the slot size and
    /// the guard on a drifted length are all offsets that can move, and a second copy of them
    /// would move separately - which is the failure this returns nothing for, silently, in
    /// front of somebody.
    /// </remarks>
    /// <summary>
    /// The path one record carries, for a caller walking <see cref="Records"/> by address.
    /// </summary>
    /// <remarks>
    /// Exposed rather than duplicated, for the reason <see cref="Records"/> is public: the name
    /// offset can drift, and a second copy of it would drift separately. Empty when the record
    /// does not read as a path, which the caller decides what to do about.
    /// </remarks>
    public string NameOf(ulong record)
        => MemoryReaderExtensions.IsPlausiblePointer(record)
            ? _reader.ReadStdWString(record + (ulong)_recordName, LongestPath)
            : string.Empty;

    public IEnumerable<ulong> Records(ulong fileRootStatic)
    {
        LastError = string.Empty;
        ulong root = _reader.ReadPointer(fileRootStatic);
        if (!MemoryReaderExtensions.IsPlausiblePointer(root))
        {
            LastError = "the file root did not resolve";
            yield break;
        }

        for (int b = 0; b < _bucketCount; b++)
        {
            foreach (ulong record in RecordsIn(root + (ulong)(b * _bucketSize)))
            {
                yield return record;
            }
        }
    }

    /// <summary>
    /// How many of the slots PAST the last bucket also look like buckets.
    /// </summary>
    /// <remarks>
    /// THE CONSTANT, CHECKED - and it holds. BucketCount is 0x10 because GameHelper2 says so
    /// (GameOffsets/Objects/LoadedFilesOffset.cs, LoadedFilesRootObject.TotalCount), and every
    /// walk of this table - the preload alerts and the dat-table route alike - covers sixteen
    /// buckets on that number. If the real count were larger, none of them would be wrong in a
    /// way anything would notice: the walk would simply never see the rest of the files, and
    /// "not in the file table" would come to mean "not in the part we look at".
    ///
    /// A recording cannot settle that. A capture holds only the bytes that were read, and nothing
    /// had ever read past the last bucket - not one fixture in this repo contains a single byte
    /// there. So it takes the game, and this is the one read that asks: a bucket is a begin/end
    /// pair a slot-size apart with a plausible count between them.
    ///
    /// RUN 2026-09-01 ON A LIVE CLIENT: nothing past the last bucket looks like one. Sixteen is
    /// the whole table, which also means the walk's coverage is not the reason a table is missing
    /// from it. Kept because the answer is a property of a build, not of the game forever.
    ///
    /// Returns how many of the next <paramref name="probe"/> slots pass that test. ANY non-zero
    /// result means BucketCount has become too small and every walk of this table is partial.
    /// </remarks>
    public int BucketsBeyondTheCount(ulong fileRootStatic, int probe = 8)
    {
        ulong root = _reader.ReadPointer(fileRootStatic);
        if (!MemoryReaderExtensions.IsPlausiblePointer(root))
        {
            return 0;
        }

        var looksLikeOne = 0;
        for (int b = _bucketCount; b < _bucketCount + probe; b++)
        {
            ulong bucket = root + (ulong)(b * _bucketSize);
            ulong first = _reader.ReadPointer(bucket);
            ulong last = _reader.ReadPointer(bucket + sizeof(ulong));

            if (!MemoryReaderExtensions.IsPlausiblePointer(first) || last <= first)
            {
                continue;
            }

            long slots = (long)(last - first) / _slotSize;
            if (slots > 0 && slots <= MostSlotsPerBucket && (last - first) % (ulong)_slotSize == 0)
            {
                looksLikeOne++;
            }
        }

        return looksLikeOne;
    }

    /// <summary>Every record a bucket points at, however many that turns out to be.</summary>
    /// <summary>
    /// What each of the sixteen buckets actually holds - the level below "no slots walked".
    /// </summary>
    /// <remarks>
    /// THE SAME FAILURE ONE LEVEL DOWN. <see cref="RecordsIn"/> gives up in four places and
    /// three of them are silent: an unreadable or non-positive capacity, a begin that is not a
    /// pointer or an end at or before it, and a slot count of zero. So "0 slots walked" is
    /// itself three different faults wearing one number, exactly as "no files matched" was.
    ///
    /// This prints the root and every bucket's three fields, so the answer is read rather than
    /// inferred: sixteen empty buckets mean the root points at something that is not the table,
    /// while one plausible bucket among empties means the stride is wrong. It reads 16 x 24
    /// bytes and is meant to be pressed once.
    /// </remarks>
    public IReadOnlyList<string> DescribeBuckets(ulong fileRootStatic)
    {
        ulong root = _reader.ReadPointer(fileRootStatic);
        if (!MemoryReaderExtensions.IsPlausiblePointer(root))
        {
            return [$"the file root static at {fileRootStatic:X} holds {root:X}, which is not a pointer"];
        }

        var lines = new List<string>(_bucketCount + 3) { $"root at {root:X}, {_bucketCount} buckets of 0x{_bucketSize:X}" };
        int usable = 0;
        int odd = 0;

        for (int b = 0; b < _bucketCount; b++)
        {
            ulong bucket = root + (ulong)(b * _bucketSize);
            bool readCapacity = _reader.TryRead(bucket + (ulong)_bucketCapacity, out int capacity);
            ulong first = _reader.ReadPointer(bucket);
            ulong last = _reader.ReadPointer(bucket + sizeof(ulong));

            long slots = MemoryReaderExtensions.IsPlausiblePointer(first) && last > first
                ? (long)(last - first) / _slotSize
                : 0;

            // SLOTS ALONE, because the capacity is not a count - see RecordsIn. Requiring it
            // to be positive made this very readout print "every bucket is empty" above sixteen
            // rows each reporting nine hundred slots: a verdict its own evidence refuted, in the
            // diagnostic written to stop exactly that.
            if (slots > 0)
            {
                usable++;
            }

            // ONLY WHERE THERE IS SOMETHING TO CONTRADICT. An empty bucket reading zero is
            // ordinary; a bucket holding nine hundred slots and claiming a negative capacity is
            // the finding, and mixing the two in one count buries it.
            if (slots > 0 && readCapacity && capacity <= 0)
            {
                odd++;
            }

            lines.Add(
                $"  bucket {b,2}  capacity {(readCapacity ? capacity.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unreadable"),-10}"
                + $"  begin {first:X16}  end {last:X16}  {slots} slots");
        }

        // THE VERDICT THE ROWS ADD UP TO, said rather than left to be counted by eye. Sixteen
        // empty buckets is a different fault from one full bucket among fifteen empties, and a
        // person reading sixteen hex rows at four in the morning should not have to spot it.
        lines.Add(usable == 0
            ? "  no bucket holds slots - the root points at something that is not the file table"
            : $"  {usable} of {_bucketCount} buckets hold slots");

        // Said because it is the finding, not because anything depends on it: a capacity that
        // cannot be a count is how this field was caught pretending to be one.
        if (odd > 0)
        {
            lines.Add($"  {odd} capacities are not counts - that field is a pointer half, and nothing gates on it");
        }

        return lines;
    }

    private IEnumerable<ulong> RecordsIn(ulong bucket)
    {
        // NO CAPACITY GATE, and its removal is the fix for a table thrown away whole. The field
        // at +0x18 is not a count: measured live it reads -843513840, -843186160, -842858480 and
        // on down, every value ending 0010 and stepping by a constant 0x50000 - the low half of
        // a POINTER, one allocation per bucket. The old gate refused anything not positive, so
        // sixteen intact buckets holding fifteen thousand slots became "no files matched".
        //
        // WHY IT WORKED UNTIL IT DID NOT: the sign of a pointer's low half is a coin flip per
        // allocation. The same build read this table an hour earlier in another session; nothing
        // was patched. The bug had been latent since the walk was written and was simply not hit.
        //
        // NOTHING IS LOST. The vector's own begin and end are what the walk actually uses, and
        // they are checked below - a plausible begin, an end past it, a slot count in range.
        // Those gate the data being read; the capacity gated a field nobody reads.
        ulong first = _reader.ReadPointer(bucket);
        ulong last = _reader.ReadPointer(bucket + sizeof(ulong));
        if (!MemoryReaderExtensions.IsPlausiblePointer(first) || last <= first)
        {
            yield break;
        }

        long slots = (long)(last - first) / _slotSize;
        if (slots <= 0)
        {
            yield break;
        }

        if (slots > MostSlotsPerBucket)
        {
            // Reported HERE because this walk now runs first - the newest stamp is found
            // before anything is collected, so a bucket that never yields would otherwise
            // fail as "only 0 areas loaded so far", which points at the wrong thing entirely.
            LastError = $"a bucket claimed {slots} slots - the layout has drifted";
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
