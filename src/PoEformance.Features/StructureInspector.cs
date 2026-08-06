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
public sealed record StructureRequest(
    bool Enabled = false,
    StructureRoot Root = StructureRoot.Custom,
    ulong Address = 0,
    int Size = 512,
    int Stride = 8,
    string StructName = "",
    int SnapshotSequence = 0,
    int PeekOffset = -1,
    int PeekSequence = 0)
{
    public static StructureRequest Idle { get; } = new();
}

/// <summary>What was found at an address, and what has been moving.</summary>
/// <param name="Live">
/// Rows that differ from the PREVIOUS tick - what is moving right now. A timer, a position,
/// an animation.
/// </param>
/// <param name="SinceBaseline">
/// Rows that differ from the last baseline taken. The discovery workflow: take one, do
/// something in the game, and this is the answer to "which part of this was that".
/// </param>
public sealed record StructureView(
    ulong Address,
    IReadOnlyList<StructureSlot> Slots,
    IReadOnlySet<int> Live,
    IReadOnlySet<int> SinceBaseline,
    IReadOnlyDictionary<int, string> FieldNames,
    PeekResult? Peek,
    int PeekOffset,
    bool HasBaseline,
    string Status)
{
    public static StructureView Empty { get; } = new(
        0, [], new HashSet<int>(), new HashSet<int>(), new Dictionary<int, string>(),
        null, -1, false, "nothing to look at yet");
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

    /// <summary>Pointer candidates checked per tick, so a fresh window cannot burst.</summary>
    private const int MaxChecksPerTick = 128;

    /// <summary>Largest number of verdicts kept before starting over.</summary>
    private const int MaxRemembered = 8192;

    private readonly IMemoryReader _reader;

    /// <summary>Which addresses have something behind them. See <see cref="Verify"/>.</summary>
    private readonly Dictionary<ulong, bool> _readable = [];
    private readonly OffsetSchema _schema;
    private readonly ulong _gameStatesStatic;

    private StructureRequest _request = StructureRequest.Idle;
    private StructureView _view = StructureView.Empty;

    private byte[] _previous = [];
    private byte[] _baseline = [];
    private ulong _baselineAddress;
    private int _servedSnapshot;
    private int _servedPeek;
    private PeekResult? _lastPeek;
    private int _lastPeekOffset = -1;

    public StructureInspector(IMemoryReader reader, OffsetSchema schema, ulong gameStatesStatic)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;
        _schema = schema;
        _gameStatesStatic = gameStatesStatic;
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
        ulong address = request.Root == StructureRoot.Custom ? request.Address : Resolve(request.Root);
        if (address == 0)
        {
            return StructureView.Empty with
            {
                Status = request.Root == StructureRoot.Custom ? "no address" : $"{request.Root} did not resolve",
            };
        }

        int size = Math.Clamp(request.Size, 8, MaxSize);
        int stride = request.Stride == 4 ? 4 : 8;

        var window = new byte[size];
        if (!_reader.TryRead(address, window))
        {
            // The commonest failure by far, and worth naming: an address one digit out, or a
            // pointer that was valid until the area changed.
            return StructureView.Empty with { Status = $"nothing readable at 0x{address:X} for {size} bytes" };
        }

        // Both comparisons only mean anything against the SAME address. After following a
        // pointer every row would otherwise read as changed, which is noise dressed as a
        // finding.
        bool sameAddress = address == _view.Address;
        IReadOnlySet<int> live = sameAddress && _previous.Length == window.Length
            ? new HashSet<int>(StructureProbe.Changes(_previous, window, stride))
            : new HashSet<int>();

        if (request.SnapshotSequence != _servedSnapshot)
        {
            _servedSnapshot = request.SnapshotSequence;
            _baseline = [.. window];
            _baselineAddress = address;
        }

        bool haveBaseline = _baseline.Length > 0 && _baselineAddress == address;
        IReadOnlySet<int> sinceBaseline = haveBaseline
            ? new HashSet<int>(StructureProbe.Changes(_baseline, window, stride))
            : new HashSet<int>();

        if (request.PeekSequence != _servedPeek)
        {
            _servedPeek = request.PeekSequence;
            _lastPeekOffset = request.PeekOffset;
            _lastPeek = request.PeekOffset >= 0 && request.PeekOffset + 8 <= window.Length
                ? PointerPeek.Peek(_reader, BitConverter.ToUInt64(window, request.PeekOffset))
                : null;
        }

        _previous = window;

        List<StructureSlot> slots = Verify(StructureProbe.Describe(
            window, address, stride, _reader.ModuleBase, _reader.ModuleSize));

        return new StructureView(
            address,
            slots,
            live,
            sinceBaseline,
            NamesFor(request.StructName, stride),
            _lastPeek,
            _lastPeekOffset,
            haveBaseline,
            $"{slots.Count} rows at 0x{address:X}");
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
    private List<StructureSlot> Verify(List<StructureSlot> slots)
    {
        Span<byte> probe = stackalloc byte[8];
        int fresh = 0;

        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i].Guess != SlotGuess.Pointer)
            {
                continue;
            }

            ulong target = slots[i].Raw;
            if (!_readable.TryGetValue(target, out bool leadsSomewhere))
            {
                if (fresh >= MaxChecksPerTick)
                {
                    continue;   // left as a candidate; the next tick finishes the job
                }

                fresh++;
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
}
