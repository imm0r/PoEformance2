using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Game.Diagnostics;

/// <summary>
/// Looks for whatever holds the area's ROOM PLACEMENTS, by searching for known addresses.
/// </summary>
/// <remarks>
/// WHY THIS IS A DIFFERENT KIND OF SEARCH from <see cref="RoomProbe"/>, and why it is worth a
/// second attempt at a question that already failed once. That probe asked "does this pointer
/// look like it leads to a room?" - a question about SHAPE, and this project's own record says
/// what shape questions are worth: a matrix invariant that accepted a decoy, a fingerprint that
/// frustum planes and basis blocks both satisfy. This one asks "is this value the address of
/// BonesEntrance_Checkpoint2_St.arm's record?", which nothing can satisfy by accident.
///
/// THE ADDRESSES ARE ALREADY IN HAND. Every loaded file has a FileRecord in the table
/// <see cref="World.PreloadReader"/> walks, and that walk yields the record ADDRESSES, not just
/// the paths. So the .arm files of the current area are a set of perhaps thirty known pointers,
/// and anything in the game that refers to a placed room has to hold one of them - or the path
/// itself. Both are searched for, and a hit names the room it found.
///
/// WHAT IT SWEEPS. AreaInstance's own bytes, which no probe has looked at: the room hunt so far
/// covered the tile struct and the neighbourhood of TerrainMetadata, and the schema maps seven
/// fields of AreaInstance between 0xC4 and 0x8C0 - so most of it is unaccounted for. Every
/// plausible pointer in that window is followed ONE hop and its target searched the same way,
/// because a placement list is far likelier to be a vector hanging off a field than to be
/// inline.
///
/// A DIAGNOSTIC. It reads once per area under --debug, its lines go where the other probes' go,
/// and it draws nothing. Nothing is concluded here: a hit is an address and a name, and a person
/// decides what it means. A MISS IS ALSO A RESULT, and it is reported with the numbers that make
/// it one - how far it swept and how many pointers it followed - because "found nothing" and
/// "looked nowhere" are the two answers that must never read alike.
/// </remarks>
public sealed class RoomPlacementProbe
{
    /// <summary>
    /// How much of AreaInstance to sweep. Its mapped fields end at 0x8C0.
    /// </summary>
    /// <remarks>
    /// WIDENED ONCE THE CONTROL EARNED IT. Eight kilobytes found nothing, and that was worth
    /// nothing until the tile control proved the search looks for the right value - 8 of 8 tile
    /// file pointers ARE record addresses. With the premise confirmed the miss became a real
    /// absence, and widening became the next step rather than a guess. Still one read, and
    /// still inside what a recording will hold.
    /// </remarks>
    public const int SweepBytes = 0x8000;

    /// <summary>How much to read at each followed pointer.</summary>
    private const int WindowBytes = 0x200;

    /// <summary>How much of a vector's ELEMENTS to read. Bigger, because that is the payload.</summary>
    /// <remarks>
    /// A placement record plausibly carries a file pointer, tile coordinates, a size and a
    /// rotation - call it 0x40 bytes - so four kilobytes covers the first sixty-odd placements
    /// of an area. Finding ONE is the whole job; the rest follows from knowing where to look.
    /// </remarks>
    private const int VectorBytes = 0x1000;

    /// <summary>How many vector-shaped triples to follow. A guard, and it was binding.</summary>
    /// <remarks>
    /// RAISED FROM 128, which the first run with a positive control hit exactly. A cap meant as a
    /// guard against a garbage window had become the thing deciding how much of the struct was
    /// searched, and a miss under it says less than it appears to. 512 triples at
    /// <see cref="VectorBytes"/> each is two megabytes of reads for a button somebody presses by
    /// hand - and every one of those reads is far under what a recording holds per read.
    /// </remarks>
    public const int MostVectors = 512;

    /// <summary>
    /// How many pointers to follow. A guard on a garbage window, not a view about the struct.
    /// </summary>
    /// <remarks>
    /// A window of nonsense is mostly plausible-looking pointers, and following every one turns
    /// one probe into a walk of the heap - and a recording into something nobody can open.
    ///
    /// RAISED FROM 512, hit exactly on the first run whose control passed, which meant the sweep
    /// reached one slot in eight and reported the miss as though it had covered the window. This
    /// is now ONE PER QWORD of <see cref="SweepBytes"/> - a principled number rather than a round
    /// one, since a slot that is not a pointer costs nothing and no slot can be followed twice.
    /// </remarks>
    public const int MostFollowed = SweepBytes / 8;

    /// <summary>Lines of hits to print before saying how many more there were.</summary>
    private const int MostHits = 24;

    /// <summary>Tile entries to test as a control. A handful settles it either way.</summary>
    private const int ControlTiles = 8;

    /// <summary>Bytes in one TileStruct entry, as the terrain reader has it.</summary>
    private const int TileEntrySize = 0x38;

    /// <summary>Longest tile path read for the control's second question. Same cap the reader uses.</summary>
    private const int MostPathChars = 128;

    private readonly IMemoryReader _reader;
    private readonly int _terrainMetadata;
    private readonly int _tileDetails;
    private readonly int _tgtFile;
    private readonly int _tgtPath;

    public RoomPlacementProbe(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;
        _terrainMetadata = schema.Structs["AreaInstance"].OffsetOf("TerrainMetadata");
        _tileDetails = schema.Structs["TerrainMetadata"].OffsetOf("TileDetailsPtr");
        _tgtFile = schema.Structs["TileStruct"].OffsetOf("TgtFilePtr");

        // OPTIONAL, unlike the three above, and that is deliberate. The path only answers the
        // control's SECOND question - whether a file the pointer missed is in the table at all -
        // so a schema without it should cost that refinement and not the whole hunt. Looked up
        // defensively because OffsetOf throws, and a diagnostic that dies on a moved field
        // presents as a button that does nothing, which is the exact failure this probe is for.
        _tgtPath = schema.Structs.TryGetValue("TgtFile", out StructDef? tgt)
                   && tgt.Field("TgtPath") is { } path
            ? path.Offset
            : -1;
    }

    /// <summary>
    /// Whether the game refers to a file by the ADDRESS this search looks for.
    /// </summary>
    /// <remarks>
    /// THE POSITIVE CONTROL, and the probe was worthless without it. A sweep that finds nothing
    /// has two completely different meanings - "no room is referred to here" and "rooms are
    /// referred to by something other than a record address" - and the first version reported
    /// the first with no way of ruling out the second. It said "nothing refers to a room file"
    /// as confidently as if it had checked.
    ///
    /// Tiles settle it. Every TileStruct carries TgtFilePtr to its own .tdt, which is a file the
    /// same table holds a record for, so those pointers are a reference the game demonstrably
    /// makes. If they ARE record addresses the search looks for the right kind of value and a
    /// miss is a real absence; if they are not, the premise is wrong and a miss means nothing.
    ///
    /// AND A FAILING CONTROL HAS TWO CAUSES, which the first version could not tell apart and
    /// reported as one. "This tile's file pointer is not a record address I know" means either
    /// the game refers to files by some other object - the interesting answer - OR the file
    /// table walk simply never collected that record, which is a fact about THIS CODE and says
    /// nothing whatever about the game. An area that lists 846 files where another listed 2573
    /// makes the second cause very live, and announcing the first would be the same mistake this
    /// whole probe exists to avoid, one level further up.
    ///
    /// So a failing pointer is asked a second question: is the tile's own PATH among the loaded
    /// files? If it is, the file is in the table and the pointer is genuinely not its record
    /// address. If it is not, the table is incomplete and the control proves nothing - the file
    /// walk is what needs fixing before this search can be believed either way.
    /// </remarks>
    /// <param name="truncated">
    /// Whether the sweep hit a cap. A CONFIRMED PREMISE DOES NOT MAKE A TRUNCATED MISS COMPLETE,
    /// and saying so was this line's own version of the mistake it exists to prevent: the control
    /// proves the search looks for the right VALUE, never that it looked everywhere.
    /// </param>
    private string Control(
        ulong areaInstance, IReadOnlyDictionary<ulong, string> files, bool truncated = false)
    {
        ulong terrain = areaInstance + (ulong)_terrainMetadata;
        ulong first = _reader.ReadPointer(terrain + (ulong)_tileDetails);
        ulong last = _reader.ReadPointer(terrain + (ulong)_tileDetails + 8);
        if (!MemoryReaderExtensions.IsPlausiblePointer(first) || last <= first)
        {
            return "  control: no tile vector, so nothing proves what a file reference looks like";
        }

        // UNSIGNED for the same reason the vector span is, and this one FAILS QUIETLY rather than
        // crashing: a signed cast of a huge pointer difference goes negative, the loop below then
        // runs zero times, and the control reports "no tile carried a file pointer" - a verdict
        // about the game produced by an arithmetic slip.
        int tiles = (int)Math.Min((ulong)ControlTiles, (last - first) / TileEntrySize);
        int pointers = 0;
        int known = 0;
        int listed = 0;
        int unlisted = 0;
        string missing = string.Empty;

        // Built only when a pointer has already missed, because the by-path question is the
        // second one and most runs never ask it. Ordinal because these are engine paths.
        HashSet<string>? paths = null;

        for (int i = 0; i < tiles; i++)
        {
            ulong file = _reader.ReadPointer(first + (ulong)(i * TileEntrySize) + (ulong)_tgtFile);
            if (!MemoryReaderExtensions.IsPlausiblePointer(file))
            {
                continue;
            }

            pointers++;
            if (files.ContainsKey(file))
            {
                known++;
                continue;
            }

            if (_tgtPath < 0)
            {
                continue;
            }

            paths ??= new HashSet<string>(files.Values, StringComparer.OrdinalIgnoreCase);

            string path = _reader.ReadStdWString(file + (ulong)_tgtPath, MostPathChars);
            if (path.Length == 0)
            {
                continue;
            }

            if (paths.Contains(path))
            {
                listed++;
            }
            else
            {
                unlisted++;
                if (missing.Length == 0)
                {
                    missing = path;
                }
            }
        }

        if (pointers == 0)
        {
            return "  control: no tile carried a file pointer, so nothing was proved either way";
        }

        if (known > 0)
        {
            return $"  control: {known} of {pointers} tile file pointers ARE record addresses"
                + (truncated
                    ? " - the search looks for the right value, but it stopped at its limits,"
                        + " so a miss above is only a miss in what it reached"
                    : " - the search looks for the right value, so a miss above is a real absence");
        }

        // THE TABLE IS SHORT, and that is a bug here rather than a finding about the game. Said
        // first because it disqualifies the other reading entirely: a file the walk never saw
        // cannot demonstrate anything about how the game points at files.
        if (unlisted > 0)
        {
            return $"  control: 0 of {pointers} are record addresses AND {unlisted} of their files"
                + $" are not in the {files.Count} this run collected (e.g. {Short(missing)})"
                + " - the FILE TABLE is short, so this search proves nothing until it is fixed";
        }

        return $"  control: 0 of {pointers} tile file pointers are record addresses, though all"
            + " their files ARE loaded - the game refers to a file by some OTHER object,"
            + " and this search cannot work";
    }

    /// <summary>
    /// Sweeps AreaInstance for anything that refers to one of the area's room files.
    /// </summary>
    /// <param name="areaInstance">The AreaInstance struct's address.</param>
    /// <param name="rooms">
    /// The .arm files of this area, by the ADDRESS of their FileRecord. That address is the
    /// thing being searched for, so this is the whole input that makes the search unambiguous.
    /// </param>
    /// <param name="files">
    /// EVERY loaded file by record address, for the control below - the tiles refer to .tdt
    /// files, not to rooms, so proving the search's premise needs more than the rooms.
    /// </param>
    public IReadOnlyList<string> Probe(
        ulong areaInstance,
        IReadOnlyDictionary<ulong, string> rooms,
        IReadOnlyDictionary<ulong, string>? files = null)
    {
        ArgumentNullException.ThrowIfNull(rooms);

        if (!MemoryReaderExtensions.IsPlausiblePointer(areaInstance))
        {
            return ["placements: no area instance"];
        }

        if (rooms.Count == 0)
        {
            // Not a failure of the sweep, and it must not read as one: with no addresses to
            // search for, the sweep cannot say anything either way.
            return ["placements: no .arm files loaded, so there is nothing to search for"];
        }

        var window = new byte[SweepBytes];
        if (!_reader.TryRead(areaInstance, window))
        {
            return [$"placements: AreaInstance unreadable at {areaInstance:X} for {SweepBytes} bytes"];
        }

        var hits = new List<string>();
        var followed = new HashSet<ulong>();
        int follows = 0;
        int vectors = 0;
        int more = 0;

        Look(window, areaInstance, "", rooms, hits, ref more);
        vectors += Vectors(window, "AreaInstance", rooms, followed, hits, ref more, vectors);

        for (int at = 0; at + 8 <= window.Length; at += 8)
        {
            ulong target = BitConverter.ToUInt64(window, at);
            if (!MemoryReaderExtensions.IsPlausiblePointer(target)
                || rooms.ContainsKey(target)
                || !followed.Add(target))
            {
                continue;
            }

            if (follows >= MostFollowed)
            {
                break;
            }

            follows++;
            var inner = new byte[WindowBytes];
            if (_reader.TryRead(target, inner))
            {
                string via = $"AreaInstance+0x{at:X4} -> ";
                Look(inner, target, via, rooms, hits, ref more);

                // AND ONE HOP FURTHER, but only through a VECTOR. A placement list is a vector
                // hanging off a field, so its elements are two hops out and the first version
                // could never reach them. Following every pointer at this depth instead would
                // be sixteen thousand reads of the heap; following the triples that look like
                // a vector is a handful, and it is the shape the thing being looked for HAS.
                vectors += Vectors(
                    inner, via.TrimEnd(' ', '-', '>'), rooms, followed, hits, ref more, vectors);
            }
        }

        var lines = new List<string>(hits.Count + 2)
        {
            $"placements: {rooms.Count} .arm records, swept {SweepBytes} bytes of AreaInstance,"
            + $" followed {follows} pointers and {vectors} vectors",
        };

        lines.AddRange(hits);

        if (more > 0)
        {
            lines.Add($"  and {more} more");
        }

        // WHETHER THE SWEEP RAN OUT OF BUDGET, and it must be said rather than left to a reader
        // who happens to know what the caps are. The first run to get a positive control
        // reported "followed 512 pointers and 128 vectors" - which ARE the two caps exactly -
        // above a line calling the miss a real absence. The numbers were printed and the fact
        // they were limits was not, so the verdict read as covering the whole struct when it
        // covered as much of it as the budget reached.
        bool truncated = follows >= MostFollowed || vectors >= MostVectors;

        if (hits.Count == 0)
        {
            // THE MISS, WITH ITS NUMBERS. Without them this line is indistinguishable from a
            // probe that never ran, which is the failure the ground layer already paid for.
            lines.Add(truncated
                ? "  nothing refers to a room file in what was reached - not by record address,"
                    + " not by path"
                : "  nothing refers to a room file - not by record address, not by path");
        }

        if (truncated)
        {
            lines.Add($"  STOPPED AT ITS LIMITS ({MostFollowed} pointers, {MostVectors} vectors)"
                + " - the rest of the window was never followed");
        }

        // AND WHETHER THE MISS IS WORTH ANYTHING. Always, not only on a miss: a hit is worth
        // more when the control agrees, and a control that fails while something was found is
        // itself a finding about what was found.
        lines.Add(Control(areaInstance, files ?? rooms, truncated));

        return lines;
    }

    /// <summary>
    /// Reports every reference to a room in one window: a record address, or the path itself.
    /// </summary>
    /// <remarks>
    /// BOTH FORMS, because which one the game uses is exactly what is unknown. A placement that
    /// points at the FileRecord shows as an address; one that carries the path inline shows as
    /// UTF-16 text ending ".arm". Searching for only the first would report "nothing found"
    /// about a structure sitting in the window in plain sight.
    /// </remarks>
    private static void Look(
        byte[] window,
        ulong at,
        string via,
        IReadOnlyDictionary<ulong, string> rooms,
        List<string> hits,
        ref int more)
    {
        for (int i = 0; i + 8 <= window.Length; i += 8)
        {
            ulong value = BitConverter.ToUInt64(window, i);
            if (rooms.TryGetValue(value, out string? name))
            {
                Add(hits, ref more, $"  {via}+0x{i:X4}  record of {Short(name)}");
            }
        }

        // ".arm" as the game stores a path: UTF-16, so every second byte is zero.
        for (int i = 0; i + 8 <= window.Length; i++)
        {
            if (window[i] == (byte)'.' && window[i + 1] == 0
                && window[i + 2] == (byte)'a' && window[i + 3] == 0
                && window[i + 4] == (byte)'r' && window[i + 5] == 0
                && window[i + 6] == (byte)'m' && window[i + 7] == 0)
            {
                Add(hits, ref more, $"  {via}+0x{i:X4}  the text \".arm\" at {at + (ulong)i:X}");
            }
        }
    }

    /// <summary>
    /// Follows every vector-shaped triple in a window and searches its elements.
    /// </summary>
    /// <remarks>
    /// THE SHAPE THE ANSWER WOULD HAVE. Three consecutive slots reading begin, end and
    /// end-of-storage, with begin a plausible pointer, end past it and capacity no smaller - a
    /// std::vector, which is how this game stores every list this project has ever found. That
    /// is a structural fingerprint, and this file's own doc says what those are worth on their
    /// own; it is used here only to decide WHERE TO READ, and what is searched for at the other
    /// end is still a known record address. A false triple costs one read and finds nothing.
    /// </remarks>
    private int Vectors(
        byte[] window,
        string via,
        IReadOnlyDictionary<ulong, string> rooms,
        HashSet<ulong> followed,
        List<string> hits,
        ref int more,
        int alreadyFound)
    {
        // UNSIGNED, and that is the whole of a crash this probe died of in the field. The cap was
        // a long and the span was compared as one - but end and begin are POINTERS, so their
        // difference is unsigned and a garbage triple spanning 2^63 or more turns NEGATIVE on the
        // cast. A negative number is not greater than the cap, so the one check meant to reject
        // that triple waved it through, and the truncating cast below then asked for an array of
        // negative length: "OverflowException: Arithmetic operation resulted in an overflow".
        //
        // Whether any three consecutive qwords in AreaInstance happen to look like this is a fact
        // about the area's heap, which is why it crashed in one area and not the one before it.
        const ulong MostSpan = 4UL * 1024 * 1024;
        int here = 0;

        for (int i = 0; i + 24 <= window.Length; i += 8)
        {
            if (alreadyFound + here >= MostVectors)
            {
                break;
            }

            ulong begin = BitConverter.ToUInt64(window, i);
            ulong end = BitConverter.ToUInt64(window, i + 8);
            ulong capacity = BitConverter.ToUInt64(window, i + 16);

            if (!MemoryReaderExtensions.IsPlausiblePointer(begin)
                || end <= begin
                || capacity < end
                || end - begin > MostSpan
                || !followed.Add(begin))
            {
                continue;
            }

            here++;

            // Safe to narrow only because the span is now capped BEFORE the cast: the guard above
            // proves it is between 1 and MostSpan, so this cannot be negative or truncate.
            var elements = new byte[(int)Math.Min((ulong)VectorBytes, end - begin)];
            if (_reader.TryRead(begin, elements))
            {
                Look(elements, begin, $"{via}+0x{i:X4} vector -> ", rooms, hits, ref more);
            }
        }

        return here;
    }

    private static void Add(List<string> hits, ref int more, string line)
    {
        if (hits.Count < MostHits)
        {
            hits.Add(line);
        }
        else
        {
            more++;
        }
    }

    /// <summary>The room's file name, since a full path leaves no room for the address.</summary>
    private static string Short(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash >= 0 && slash < path.Length - 1 ? path[(slash + 1)..] : path;
    }
}
