using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Features;

/// <summary>A place worth starting from, by name rather than by address.</summary>
/// <remarks>
/// Typing a hex address is how you continue an investigation, not how you begin one. These
/// are the handful of structures every other one hangs off, and they are resolved fresh each
/// tick because all of them move when the area changes.
/// </remarks>
public enum StructureRoot
{
    /// <summary>Whatever address was typed in. What following a pointer leaves you on.</summary>
    Custom,
    InGameState,
    AreaInstance,
    WorldData,
    LocalPlayer,
    UiRoot,
}

/// <summary>What the dissector wants to look at. Published by the window, read by the reader.</summary>
/// <remarks>
/// State rather than commands, exactly as <see cref="UiTreeRequest"/> does it, with sequence
/// numbers for the things that happen ONCE. Taking a baseline is the clearest example: served
/// every tick it would re-baseline continuously and nothing would ever look changed - which is
/// precisely the opposite of what it is for.
/// </remarks>
/// <param name="Compared">
/// Further addresses to read BESIDE the first one, at the same size and stride, so the same
/// structure can be looked at in several instances at once. Plain addresses rather than roots:
/// they are places pinned by hand or handed over by the entity browser, and a root resolves
/// to exactly one place by definition.
/// </param>
public sealed record StructureRequest(
    bool Enabled = false,
    StructureRoot Root = StructureRoot.Custom,
    ulong Address = 0,
    int Size = 512,
    int Stride = 8,
    string StructName = "",
    int SnapshotSequence = 0,
    int PeekOffset = -1,
    int PeekSequence = 0,
    IReadOnlyList<ulong>? Compared = null)
{
    public static StructureRequest Idle { get; } = new();
}

/// <summary>One place being read, and what has been moving in it.</summary>
/// <param name="Live">
/// Rows that differ from the PREVIOUS tick - what is moving right now. A timer, a position,
/// an animation.
/// </param>
/// <param name="SinceBaseline">
/// Rows that differ from the last baseline taken. The discovery workflow: take one, do
/// something in the game, and this is the answer to "which part of this was that".
/// </param>
/// <param name="Text">
/// The text a row holds, where it holds any - whether stored in the row itself or behind a
/// pointer. Most of this game's structures are half strings, and without this they read as a
/// pointer and two meaningless numbers.
/// </param>
/// <param name="Peek">
/// What is behind this place's pointer at the peeked row. Per place rather than shared: the
/// row is the same row in each, but what it LEADS TO is the answer being asked for, and with
/// two monsters open it is the fastest way to tell a row apart - "Skeleton" beside "Zombie"
/// names a row in one click.
/// </param>
public sealed record StructureColumn(
    ulong Address,
    IReadOnlyList<StructureSlot> Slots,
    IReadOnlySet<int> Live,
    IReadOnlySet<int> SinceBaseline,
    IReadOnlyDictionary<int, string> Text,
    PeekResult? Peek,
    bool HasBaseline,
    string Status)
{
    public static StructureColumn Empty { get; } = new(
        0, [], new HashSet<int>(), new HashSet<int>(), new Dictionary<int, string>(),
        null, false, "nothing to look at yet");
}

/// <summary>What was found at every place open, and where they disagree.</summary>
/// <param name="Columns">
/// The places, in the order asked for. The FIRST is the one the root, the peek and the
/// single-place view all mean, and there is always at least one.
/// </param>
/// <param name="Differing">
/// Rows that do not read the same in every place. Empty with one place open, because a
/// structure agrees with itself.
/// </param>
public sealed record StructureView(
    IReadOnlyList<StructureColumn> Columns,
    IReadOnlyDictionary<int, string> FieldNames,
    IReadOnlySet<int> Differing,
    int PeekOffset,
    string Status)
{
    public static StructureView Empty { get; } = new(
        [], new Dictionary<int, string>(), new HashSet<int>(), -1, "nothing to look at yet");

    /// <summary>The first place. Everything a single-place view asks for comes from here.</summary>
    public StructureColumn Primary => Columns.Count > 0 ? Columns[0] : StructureColumn.Empty;

    public ulong Address => Primary.Address;

    public IReadOnlyList<StructureSlot> Slots => Primary.Slots;

    public IReadOnlySet<int> Live => Primary.Live;

    public IReadOnlySet<int> SinceBaseline => Primary.SinceBaseline;

    public IReadOnlyDictionary<int, string> Text => Primary.Text;

    public PeekResult? Peek => Primary.Peek;

    public bool HasBaseline => Primary.HasBaseline;
}

/// <summary>
/// Serves the memory dissector from the reader thread.
/// </summary>
/// <remarks>
/// The window says where to look; this looks, and publishes what it found. Same division as
/// the interface browser and for the same reason - the renderer must not be the thing issuing
/// reads - but with one addition that is the whole point of the tool: it remembers the
/// PREVIOUS window of bytes, so it can report what moved.
///
/// Two kinds of movement, because they answer different questions. What changed since the
/// last tick shows what is live at all. What changed since a baseline the user took shows
/// what one particular action did - and that is the only way to find a field whose meaning is
/// a verb, since nothing about its bytes says "health" until something takes some away.
///
/// And a third comparison, across PLACES rather than across time: several addresses read at
/// once, so two instances of one structure can be held side by side. What they share is the
/// kind, what they do not is the instance - see <see cref="StructureProbe.Differences"/>.
/// </remarks>
public sealed class StructureInspector
{
    /// <summary>Largest window served in one go.</summary>
    /// <remarks>
    /// Not a performance limit - a read of a few kilobytes is nothing. It is a limit on how
    /// much can be WRONG at once: an address typed one digit off asks for a window over
    /// unmapped memory, and a modest request fails cleanly instead of dragging the reader
    /// through a megabyte of nothing.
    /// </remarks>
    public const int MaxSize = 8192;

    /// <summary>How many places can be held side by side.</summary>
    /// <remarks>
    /// Every place is a full window read every tick, on the thread that also drives the world
    /// read and auto-flask, so this is a real cost and not a formality. Six is well past what
    /// the comparison needs: two instances answer "instance or kind" outright, and a third
    /// settles a coincidence. Past that the screen runs out before the reader does.
    /// </remarks>
    public const int MaxColumns = 6;

    /// <summary>Pointer candidates checked per tick, so a fresh window cannot burst.</summary>
    /// <remarks>Shared across the places, not granted to each - the reader has one budget.</remarks>
    private const int MaxChecksPerTick = 128;

    /// <summary>Largest number of verdicts kept before starting over.</summary>
    private const int MaxRemembered = 8192;

    private readonly IMemoryReader _reader;

    /// <summary>Longest string shown. A path is the longest this game holds.</summary>
    private const int MaxTextChars = 200;

    /// <summary>Which addresses have something behind them. See <see cref="Verify"/>.</summary>
    private readonly Dictionary<ulong, bool> _readable = [];

    /// <summary>The text at an address, or empty when there is none. See <see cref="TextIn"/>.</summary>
    private readonly Dictionary<ulong, string> _textAt = [];
    private readonly OffsetSchema _schema;
    private readonly ulong _gameStatesStatic;

    /// <summary>Where a dat table keeps its path and rows, so a peek can name one.</summary>
    private readonly DatTableShape? _tables;

    /// <summary>What each place used to say, one entry per place, in the order asked for.</summary>
    private readonly List<Tracked> _tracked = [];

    private StructureRequest _request = StructureRequest.Idle;
    private StructureView _view = StructureView.Empty;

    private int _servedSnapshot;
    private int _servedPeek;
    private int _lastPeekOffset = -1;

    public StructureInspector(IMemoryReader reader, OffsetSchema schema, ulong gameStatesStatic)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;
        _schema = schema;
        _gameStatesStatic = gameStatesStatic;
        _tables = DatTableShape.From(schema);
    }

    /// <summary>The newest reading. Never blocks, never null, never partially built.</summary>
    public StructureView View => Volatile.Read(ref _view);

    /// <summary>The structures the schema knows, for naming the rows of a known one.</summary>
    public IEnumerable<string> KnownStructures => _schema.Structs.Keys.Order(StringComparer.Ordinal);

    /// <summary>Tells the reader where to look. Called from the render thread.</summary>
    public void Request(StructureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Volatile.Write(ref _request, request);
    }

    /// <summary>Reads one window. Called on the reader thread, beside the world read.</summary>
    public void Service()
    {
        StructureRequest request = Volatile.Read(ref _request);
        if (!request.Enabled)
        {
            return;
        }

        try
        {
            Volatile.Write(ref _view, Build(request));
        }
        catch (Exception exception)
        {
            // A stale view beats a dead servicing thread - and every address in this tool is
            // one the user chose, so pointing it at nonsense is an ordinary thing to do.
            Volatile.Write(ref _view, View with { Status = $"read failed: {exception.Message}" });
        }
    }

    /// <summary>Where a named root currently lives, or 0 when it does not resolve.</summary>
    public ulong Resolve(StructureRoot root)
    {
        if (root == StructureRoot.Custom)
        {
            return 0;
        }

        GameChainAddresses chain = GameChain.Resolve(_reader, _schema, _gameStatesStatic);
        return root switch
        {
            StructureRoot.InGameState => chain.InGameState,
            StructureRoot.AreaInstance => chain.AreaInstance,
            StructureRoot.WorldData => chain.WorldData,
            StructureRoot.LocalPlayer => chain.PlayerEntity,
            StructureRoot.UiRoot => chain.UiRoot,
            _ => 0,
        };
    }

    private StructureView Build(StructureRequest request)
    {
        int size = Math.Clamp(request.Size, 8, MaxSize);
        int stride = request.Stride == 4 ? 4 : 8;
        List<ulong> addresses = Places(request);

        // Both of these happen ONCE per press rather than every tick, and both apply to every
        // place at the same instant - which is what makes them comparable at all. A mark taken
        // per place as its turn came round would be a set of marks from different moments.
        bool marking = request.SnapshotSequence != _servedSnapshot;
        _servedSnapshot = request.SnapshotSequence;

        bool peeking = request.PeekSequence != _servedPeek;
        _servedPeek = request.PeekSequence;
        if (peeking)
        {
            _lastPeekOffset = request.PeekOffset;
        }

        // One budget for the whole tick, handed round the places in turn. Granting each its
        // own would let six places cost six times the reads on the thread that also drives
        // auto-flask - and the point of the cap is the total.
        int pointerBudget = MaxChecksPerTick;
        int textBudget = MaxChecksPerTick;

        var windows = new byte[addresses.Count][];
        var columns = new List<StructureColumn>(addresses.Count);

        for (int i = 0; i < addresses.Count; i++)
        {
            windows[i] = [];
            columns.Add(Read(
                request, i, addresses[i], size, stride, marking, peeking,
                ref pointerBudget, ref textBudget, out windows[i]));
        }

        // Only the places that read contribute; see Differences. With one place open this is
        // empty, which is the honest answer rather than a comparison of a thing with itself.
        var differing = new HashSet<int>(StructureProbe.Differences(windows, stride));

        return new StructureView(
            columns,
            NamesFor(request.StructName, stride),
            differing,
            _lastPeekOffset,
            columns.Count > 1
                ? $"{columns[0].Status} - {columns.Count} places, {differing.Count} rows differ"
                : columns[0].Status);
    }

    /// <summary>The addresses to read this tick: the root or the typed one, then the pinned.</summary>
    private List<ulong> Places(StructureRequest request)
    {
        var addresses = new List<ulong>(1 + (request.Compared?.Count ?? 0))
        {
            request.Root == StructureRoot.Custom ? request.Address : Resolve(request.Root),
        };

        if (request.Compared is null)
        {
            return addresses;
        }

        foreach (ulong address in request.Compared)
        {
            if (addresses.Count >= MaxColumns)
            {
                break;
            }

            addresses.Add(address);
        }

        return addresses;
    }

    /// <summary>Reads one place and says what moved in it.</summary>
    private StructureColumn Read(
        StructureRequest request,
        int index,
        ulong address,
        int size,
        int stride,
        bool marking,
        bool peeking,
        ref int pointerBudget,
        ref int textBudget,
        out byte[] window)
    {
        Tracked tracked = TrackedAt(index);
        window = [];

        if (address == 0)
        {
            tracked.Forget();
            return StructureColumn.Empty with
            {
                // Only the first place can have a root behind it. A pinned address that came
                // out zero is just an address that is zero, and blaming the root for it would
                // send someone looking at the game state instead of at the pointer they
                // followed.
                Status = index == 0 && request.Root != StructureRoot.Custom
                    ? $"{request.Root} did not resolve"
                    : "no address",
            };
        }

        var bytes = new byte[size];
        if (!_reader.TryRead(address, bytes))
        {
            // The commonest failure by far, and worth naming: an address one digit out, or a
            // pointer that was valid until the area changed. One place failing must not take
            // the others with it - the rest of the comparison is still worth having.
            tracked.Forget();
            return StructureColumn.Empty with
            {
                Address = address,
                Status = $"nothing readable at 0x{address:X} for {size} bytes",
            };
        }

        // Both comparisons only mean anything against the SAME address. After following a
        // pointer every row would otherwise read as changed, which is noise dressed as a
        // finding.
        bool sameAddress = tracked.Address == address;
        IReadOnlySet<int> live = sameAddress && tracked.Previous.Length == bytes.Length
            ? new HashSet<int>(StructureProbe.Changes(tracked.Previous, bytes, stride))
            : new HashSet<int>();

        if (marking)
        {
            tracked.Baseline = [.. bytes];
            tracked.BaselineAddress = address;
        }

        bool haveBaseline = tracked.Baseline.Length > 0 && tracked.BaselineAddress == address;
        IReadOnlySet<int> sinceBaseline = haveBaseline
            ? new HashSet<int>(StructureProbe.Changes(tracked.Baseline, bytes, stride))
            : new HashSet<int>();

        if (peeking)
        {
            // The NEXT eight bytes go with it. A foreign reference is a row followed by its
            // table, so handing the peek both halves is what turns "dat row, Id X" into which
            // table it is a row of and which row - the one question a row cannot answer about
            // itself. Zero when the window ends here, which the peek reads as "no neighbour".
            tracked.Peek = request.PeekOffset >= 0 && request.PeekOffset + 8 <= bytes.Length
                ? PointerPeek.Peek(
                    _reader,
                    BitConverter.ToUInt64(bytes, request.PeekOffset),
                    _tables,
                    request.PeekOffset + 16 <= bytes.Length
                        ? BitConverter.ToUInt64(bytes, request.PeekOffset + 8)
                        : 0)
                : null;
        }

        tracked.Previous = bytes;
        tracked.Address = address;
        window = bytes;

        List<StructureSlot> slots = Verify(
            StructureProbe.Describe(bytes, address, stride, _reader.ModuleBase, _reader.ModuleSize),
            ref pointerBudget);

        return new StructureColumn(
            address,
            slots,
            live,
            sinceBaseline,
            TextIn(bytes, slots, ref textBudget),
            tracked.Peek,
            haveBaseline,
            $"{slots.Count} rows at 0x{address:X}");
    }

    /// <summary>What the place at this position used to say, created the first time it is asked for.</summary>
    private Tracked TrackedAt(int index)
    {
        while (_tracked.Count <= index)
        {
            _tracked.Add(new Tracked());
        }

        return _tracked[index];
    }

    /// <summary>
    /// Demotes pointer-shaped values that do not actually point anywhere.
    /// </summary>
    /// <remarks>
    /// The shape of a value can only ever say "this COULD be an address", and in a real
    /// structure that is wrong often enough to matter: two small integers sharing a row make
    /// one. Seen live at a row reading 4 and 531 - a perfectly ordinary pair of fields, and
    /// together a number above four gigabytes, which is all the shape test can ask.
    ///
    /// Guessing harder does not fix it, because the two are genuinely indistinguishable by
    /// their bytes. Reading does: an address either has something behind it or it does not.
    /// That is the whole difference between a "go" button that leads somewhere and one that
    /// leads to "nothing readable", which is what makes the descent worth trusting.
    ///
    /// Cached by target, since whether an address is mapped almost never changes, and capped
    /// per tick so a screen full of new candidates cannot turn into a burst of reads on the
    /// thread that also drives auto-flask.
    /// </remarks>
    private List<StructureSlot> Verify(List<StructureSlot> slots, ref int budget)
    {
        Span<byte> probe = stackalloc byte[8];

        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i].Guess != SlotGuess.Pointer)
            {
                continue;
            }

            ulong target = slots[i].Raw;
            if (!_readable.TryGetValue(target, out bool leadsSomewhere))
            {
                if (budget <= 0)
                {
                    continue;   // left as a candidate; the next tick finishes the job
                }

                budget--;
                leadsSomewhere = _reader.TryRead(target, probe);

                // Wholesale rather than evicting one at a time: the verdicts are cheap to
                // rebuild, and an area change invalidates every address here at once anyway.
                if (_readable.Count >= MaxRemembered)
                {
                    _readable.Clear();
                }

                _readable[target] = leadsSomewhere;
            }

            if (!leadsSomewhere)
            {
                // "Bytes" rather than anything more definite: what it really is remains
                // unknown, and only the claim that it is an address has been withdrawn.
                slots[i] = slots[i] with { Guess = SlotGuess.Bytes };
            }
        }

        return slots;
    }

    /// <summary>
    /// The text each row holds, wherever it is stored.
    /// </summary>
    /// <remarks>
    /// Three ways a row can carry text, and they cost different amounts:
    ///
    /// - a SHORT string lives in the row, so showing it costs nothing at all;
    /// - a LONG one leaves a header in the row and its characters elsewhere, so it costs one
    ///   read - cached, because a string's contents rarely change and the same rows are
    ///   re-read every tick while the window is open;
    /// - a bare POINTER to characters, with no header, which is how the data tables store
    ///   their names. Only a read can tell, and it shares the same cache.
    /// </remarks>
    private IReadOnlyDictionary<int, string> TextIn(byte[] window, List<StructureSlot> slots, ref int budget)
    {
        var found = new Dictionary<int, string>();

        foreach (StructureSlot slot in slots)
        {
            TextCandidate candidate = TextProbe.At(window, slot.Offset);

            if (candidate.Shape == TextShape.Inline)
            {
                found[slot.Offset] = candidate.Text;
                continue;
            }

            // A header and a bare pointer are the same job from here: characters at an
            // address, fetched once and remembered.
            ulong at = candidate.Shape == TextShape.Heap ? candidate.Address
                : slot.Guess == SlotGuess.Pointer ? slot.Raw
                : 0;

            if (at == 0)
            {
                continue;
            }

            if (!_textAt.TryGetValue(at, out string? text))
            {
                if (budget <= 0)
                {
                    continue;   // the next tick finishes the rest
                }

                budget--;
                text = Fetch(at, candidate.Shape == TextShape.Heap ? candidate.Length : 0);

                if (_textAt.Count >= MaxRemembered)
                {
                    _textAt.Clear();
                }

                _textAt[at] = text;
            }

            if (text.Length > 0)
            {
                found[slot.Offset] = text;
            }
        }

        return found;
    }

    /// <summary>Reads characters at an address, wide first - which is how this game stores them.</summary>
    private string Fetch(ulong address, int length)
    {
        string wide = _reader.ReadUnicodeString(address, length > 0 ? Math.Min(length, MaxTextChars) : MaxTextChars);
        if (TextProbe.LooksLikeText(wide))
        {
            return wide;
        }

        // Only worth trying when there was no header saying how long it is: a std::wstring is
        // wide by definition, so plain bytes there would mean the header was misread.
        if (length > 0)
        {
            return string.Empty;
        }

        string plain = _reader.ReadUtf8(address, MaxTextChars);
        return TextProbe.LooksLikeText(plain) ? plain : string.Empty;
    }

    /// <summary>
    /// The schema's names for the rows of a known structure.
    /// </summary>
    /// <remarks>
    /// Laying a known layout over the bytes is how you tell "this is the struct I meant" from
    /// "this is eight bytes off" in one glance - the misaligned case names every field and
    /// none of them make sense. Several fields can share a row, so they are joined rather
    /// than one winning.
    /// </remarks>
    private IReadOnlyDictionary<int, string> NamesFor(string structName, int stride)
    {
        var names = new Dictionary<int, string>();
        if (structName.Length == 0 || !_schema.Structs.TryGetValue(structName, out StructDef? definition))
        {
            return names;
        }

        foreach (FieldDef field in definition.Fields)
        {
            int row = field.Offset / stride * stride;
            string label = field.Offset == row ? field.Name : $"{field.Name}(+0x{field.Offset - row:X})";
            names[row] = names.TryGetValue(row, out string? existing) ? $"{existing}, {label}" : label;
        }

        return names;
    }

    /// <summary>What one place used to say, so what it says now can be compared against it.</summary>
    /// <remarks>
    /// Per place rather than one set of bytes for the whole window, and the address is carried
    /// with them: a place that moved - because a pointer was followed, or because the slot now
    /// holds a different monster - has nothing worth comparing, and comparing anyway would
    /// light every row of it on arrival.
    /// </remarks>
    private sealed class Tracked
    {
        public ulong Address { get; set; }

        public byte[] Previous { get; set; } = [];

        public byte[] Baseline { get; set; } = [];

        public ulong BaselineAddress { get; set; }

        public PeekResult? Peek { get; set; }

        /// <summary>Drops everything remembered, for a place that no longer reads.</summary>
        public void Forget()
        {
            Address = 0;
            Previous = [];
            Baseline = [];
            BaselineAddress = 0;
            Peek = null;
        }
    }
}
