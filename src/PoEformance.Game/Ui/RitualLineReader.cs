using System.Runtime.InteropServices;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Game.Ui;

/// <summary>
/// The state of the atlas's ritual line, as the game holds it.
/// </summary>
/// <param name="Active">Whether the panel is in ritual-line mode at all. Everything else is only meaningful when it is.</param>
/// <param name="LineId">Seeds the reward roll. Changes when the line is re-rolled.</param>
/// <param name="Pending">Grid positions clicked but not yet confirmed.</param>
/// <param name="Committed">Grid positions already on the line. Its COUNT seeds the roll too.</param>
/// <param name="Candidates">Which nodes may follow which, from the game's own precomputed table.</param>
/// <param name="Stats">The player's atlas stats, by id, for the ones that are not zero.</param>
public sealed record RitualLine(
    bool Active,
    uint LineId,
    IReadOnlyList<(int X, int Y)> Pending,
    IReadOnlyList<(int X, int Y)> Committed,
    IReadOnlyDictionary<(int X, int Y), IReadOnlyList<(int X, int Y)>> Candidates,
    IReadOnlyDictionary<int, int> Stats)
{
    /// <summary>Nothing being drawn - what a closed atlas and an idle one both look like.</summary>
    public static RitualLine Idle { get; } = new(
        false, 0, [], [],
        new Dictionary<(int X, int Y), IReadOnlyList<(int X, int Y)>>(),
        new Dictionary<int, int>());

    /// <summary>Whether a line has been started, by a click or by a commit.</summary>
    public bool Started => Committed.Count > 0 || Pending.Count > 0;

    /// <summary>How long the line may become, from the stats. The pick counter can override it.</summary>
    public int Length
    {
        get
        {
            int extra = Stat(World.RitualMods.AdditionalMapsStat);
            return World.RitualMods.BaseLineLength + Math.Max(0, extra);
        }
    }

    /// <summary>How likely a node is to carry a second reward, as a percentage.</summary>
    public int SecondRewardChance => Stat(World.RitualMods.SecondModChanceStat);

    /// <summary>
    /// One stat, accepting either spelling of its id.
    /// </summary>
    /// <remarks>
    /// The game's own ids run one below the data table's and which of the two this field holds
    /// has not been pinned down - so both are tried, which is what the reference does. Being
    /// wrong in that direction reads a stat that is not there rather than missing one that is.
    /// </remarks>
    public int Stat(int id)
        => Stats.TryGetValue(id, out int value) ? value
            : Stats.TryGetValue(id + 1, out int alternate) ? alternate
                : 0;
}

/// <summary>
/// Reads the ritual line off the atlas panel.
/// </summary>
/// <remarks>
/// EVERY OFFSET HERE IS UNVERIFIED, and doubly so: they hang off the atlas panel element, which
/// is itself unconfirmed, so a wrong panel path makes all of this read rubbish. Check that the
/// atlas draws at all before believing anything from here.
///
/// Ported from GameHelper2's Atlas plugin, where the whole ritual model was reverse-engineered
/// by yokkenUA from live memory plus Ghidra analysis of the two functions that drive it.
///
/// IDLE UNLESS THE LINE IS BEING DRAWN. The mode byte is one read, and it is zero for almost all
/// of a session - the candidate table alone is a couple of hundred kilobytes, so reading it
/// before checking that byte would be the most expensive thing the tool does, for nothing.
/// </remarks>
public sealed class RitualLineReader
{
    /// <summary>Most entries taken from the candidate table. A guard on a length from memory.</summary>
    public const int MostCandidateEntries = 65_536;

    /// <summary>Most positions in one of the line's vectors - a line is a handful of maps.</summary>
    public const int MostLinePositions = 64;

    /// <summary>And most stats, which is the same guard on a different table.</summary>
    public const int MostStats = 8_192;

    private readonly IMemoryReader _reader;
    private readonly UiElementReader _elements;

    private readonly int _mode;
    private readonly int _lineId;
    private readonly int _pending;
    private readonly int _committed;
    private readonly int _tableBegin;
    private readonly int _tableEnd;

    private readonly int _stride;
    private readonly int _mostCandidates;
    private readonly int _statsFirst;
    private readonly int _statsSecond;
    private readonly int _statsHolder;
    private readonly int _statsBegin;
    private readonly int _statsEnd;
    private readonly int _statsStride;
    private readonly int _statsValue;

    private readonly int _modsChild;
    private readonly int[] _counterPath;
    private readonly uint _counterFingerprint;
    private readonly uint _visibleMask;
    private readonly int _text;
    private readonly int _flags;

    // The candidate table is fixed for an atlas, and big, so it is kept until the vector that
    // backs it moves. Keyed by BOTH ends: the allocator reuses a base address, and a table
    // recognised by its start alone would be served from a different atlas's entries.
    private ulong _cachedBegin;
    private ulong _cachedEnd;
    private Dictionary<(int X, int Y), IReadOnlyList<(int X, int Y)>>? _cached;

    public RitualLineReader(IMemoryReader reader, OffsetSchema schema, UiElementReader elements)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(elements);
        _reader = reader;
        _elements = elements;

        StructDef ritual = schema.Structs["AtlasRitual"];
        _mode = ritual.OffsetOf("LineMode");
        _lineId = ritual.OffsetOf("LineId");
        _pending = ritual.OffsetOf("PendingVector");
        _committed = ritual.OffsetOf("CommittedVector");
        _tableBegin = ritual.OffsetOf("CandidateTableBegin");
        _tableEnd = ritual.OffsetOf("CandidateTableEnd");

        _stride = (int)ritual.Constants["CandidateEntryStride"];
        _mostCandidates = (int)ritual.Constants["MaxCandidates"];
        _statsFirst = (int)ritual.Constants["StatsFirstHop"];
        _statsSecond = (int)ritual.Constants["StatsSecondHop"];
        _statsHolder = (int)ritual.Constants["StatsHolder"];
        _statsBegin = (int)ritual.Constants["StatsBegin"];
        _statsEnd = (int)ritual.Constants["StatsEnd"];
        _statsStride = (int)ritual.Constants["StatsStride"];
        _statsValue = (int)ritual.Constants["StatsValueOffset"];

        _modsChild = (int)schema.Structs["AtlasNode"].Constants["RitualModsChild"];

        _counterPath =
        [
            (int)ritual.Constants["PickCounterPath0"],
            (int)ritual.Constants["PickCounterPath1"],
            (int)ritual.Constants["PickCounterPath2"],
        ];
        _counterFingerprint = (uint)ritual.Constants["PickCounterFingerprint"];
        _text = (int)ritual.Constants["TextOffset"];

        _visibleMask = (uint)schema.Structs["AtlasPanel"].Constants["VisibleMask"];
        _flags = schema.Structs["UiElementBase"].OffsetOf("Flags");
    }

    /// <summary>
    /// Reads the line, or <see cref="RitualLine.Idle"/> when none is being drawn.
    /// </summary>
    /// <remarks>
    /// The mode byte is checked first and nothing else is read when it is zero, which is almost
    /// always. That one read is the whole cost of having this feature switched on.
    /// </remarks>
    public RitualLine Read(ulong panel)
    {
        if (panel == 0 || !_reader.TryRead(panel + (ulong)_mode, out byte mode) || mode == 0)
        {
            return RitualLine.Idle;
        }

        uint lineId = _reader.Read<uint>(panel + (ulong)_lineId);
        List<(int X, int Y)> pending = Positions(panel + (ulong)_pending);
        List<(int X, int Y)> committed = Positions(panel + (ulong)_committed);

        return new RitualLine(true, lineId, pending, committed, CandidateTable(panel), Stats(panel));
    }

    /// <summary>
    /// A vector of grid positions - a pair of int32 each.
    /// </summary>
    /// <remarks>
    /// Length checked against the STRIDE as well as a maximum: a vector whose byte count is not
    /// a whole number of entries is a torn read of one being resized, not a short line.
    /// </remarks>
    private List<(int X, int Y)> Positions(ulong vector)
    {
        var found = new List<(int X, int Y)>();

        ulong first = _reader.ReadPointer(vector);
        ulong last = _reader.ReadPointer(vector + 8);
        if (!MemoryReaderExtensions.IsPlausiblePointer(first) || last <= first)
        {
            return found;
        }

        ulong bytes = last - first;
        if (bytes % 8 != 0 || bytes > 8 * (ulong)MostLinePositions)
        {
            return found;
        }

        for (ulong i = 0; i < bytes / 8; i++)
        {
            int x = _reader.Read<int>(first + (i * 8));
            int y = _reader.Read<int>(first + (i * 8) + 4);
            found.Add((x, y));
        }

        return found;
    }

    /// <summary>
    /// The game's precomputed table of which nodes may follow which.
    /// </summary>
    /// <remarks>
    /// Read WHOLE, in one go, and parsed here. It is a few hundred kilobytes for a real atlas
    /// and reading it field by field would be tens of thousands of round trips into another
    /// process - the one place in this tool where a single big read is plainly right.
    ///
    /// An atlas that is still loading has the vector allocated and its entries zeroed, and every
    /// slot then parses as the empty-slot sentinel. That is never cached: the pointers do not
    /// change afterwards, so the emptiness would stick for the rest of the session.
    /// </remarks>
    private Dictionary<(int X, int Y), IReadOnlyList<(int X, int Y)>> CandidateTable(ulong panel)
    {
        var table = new Dictionary<(int X, int Y), IReadOnlyList<(int X, int Y)>>();

        ulong begin = _reader.ReadPointer(panel + (ulong)_tableBegin);
        ulong end = _reader.ReadPointer(panel + (ulong)_tableEnd);
        if (!MemoryReaderExtensions.IsPlausiblePointer(begin) || end <= begin)
        {
            return table;
        }

        if (begin == _cachedBegin && end == _cachedEnd && _cached is not null)
        {
            return _cached;
        }

        ulong bytes = end - begin;
        if (bytes % (ulong)_stride != 0 || bytes > (ulong)_stride * (ulong)MostCandidateEntries)
        {
            return table;
        }

        var buffer = new byte[bytes];
        if (!_reader.TryRead(begin, buffer))
        {
            return table;
        }

        ReadOnlySpan<int> words = MemoryMarshal.Cast<byte, int>(buffer);
        int perEntry = _stride / 4;
        int entries = (int)(bytes / (ulong)_stride);
        bool anything = false;

        for (int e = 0; e < entries; e++)
        {
            int at = e * perEntry;
            var node = (X: words[at], Y: words[at + 1]);

            var following = new List<(int X, int Y)>(_mostCandidates);
            for (int c = 0; c < _mostCandidates; c++)
            {
                // Three ints per candidate - x, y and one this does not use.
                int cx = words[at + 2 + (c * 3)];
                int cy = words[at + 3 + (c * 3)];
                if (cx == 0 && cy == 0)
                {
                    continue;
                }

                following.Add((cx, cy));
            }

            if (following.Count > 0)
            {
                anything = true;
            }

            table[node] = following;
        }

        if (anything)
        {
            _cachedBegin = begin;
            _cachedEnd = end;
            _cached = table;
        }

        return table;
    }

    /// <summary>
    /// The player's atlas stats, three hops off the panel.
    /// </summary>
    /// <remarks>
    /// These decide which rewards are in the pool, how long the line may be and whether a node
    /// can carry a second reward - so a failure here does not break the prediction, it silently
    /// makes it the prediction for a player with no atlas passives. Worth knowing when a
    /// predicted reward is wrong and everything else looks right.
    /// </remarks>
    private Dictionary<int, int> Stats(ulong panel)
    {
        var stats = new Dictionary<int, int>();

        ulong first = _reader.ReadPointer(panel + (ulong)_statsFirst);
        if (!MemoryReaderExtensions.IsPlausiblePointer(first))
        {
            return stats;
        }

        ulong second = _reader.ReadPointer(first + (ulong)_statsSecond);
        if (!MemoryReaderExtensions.IsPlausiblePointer(second))
        {
            return stats;
        }

        ulong holder = _reader.ReadPointer(second + (ulong)_statsHolder);
        if (!MemoryReaderExtensions.IsPlausiblePointer(holder))
        {
            return stats;
        }

        ulong begin = _reader.ReadPointer(holder + (ulong)_statsBegin);
        ulong end = _reader.ReadPointer(holder + (ulong)_statsEnd);
        if (!MemoryReaderExtensions.IsPlausiblePointer(begin) || end <= begin)
        {
            return stats;
        }

        ulong bytes = end - begin;
        if (bytes % (ulong)_statsStride != 0 || bytes > (ulong)_statsStride * (ulong)MostStats)
        {
            return stats;
        }

        for (ulong e = 0; e < bytes / (ulong)_statsStride; e++)
        {
            ulong at = begin + (e * (ulong)_statsStride);
            int value = _reader.Read<int>(at + (ulong)_statsValue);
            if (value != 0)
            {
                stats[_reader.Read<int>(at)] = value;
            }
        }

        return stats;
    }

    /// <summary>
    /// What the game has actually rolled onto a map, or empty when it has rolled nothing.
    /// </summary>
    /// <remarks>
    /// THE GROUND TRUTH, and the only thing that can say whether the whole prediction model is
    /// right for this client. The node element points at a text element that already holds the
    /// game's own wording - translated, and both mods of a two-reward map together in one block.
    ///
    /// Read for the maps on a drawn line only, which is a handful, so the cost does not matter.
    /// </remarks>
    public string RolledOn(ulong node)
    {
        if (node == 0)
        {
            return string.Empty;
        }

        ulong text = _reader.ReadPointer(node + (ulong)_modsChild);
        return MemoryReaderExtensions.IsPlausiblePointer(text)
            ? _reader.ReadStdWString(text + (ulong)_text)
            : string.Empty;
    }

    /// <summary>
    /// How many maps the game itself says can still be picked, or -1 when it cannot be read.
    /// </summary>
    /// <remarks>
    /// THE AUTHORITATIVE number, which is why it is worth a separate walk: the length worked out
    /// from stats is a reconstruction, and this is what the game is showing on screen. It is
    /// read as the first run of digits in the label, so it holds in any language.
    ///
    /// Its own child path and its own fingerprint, both unverified like everything else here.
    /// Failing is ordinary - the header is only up while a line is being drawn.
    /// </remarks>
    public int PicksLeft(ulong uiRoot)
    {
        ulong at = uiRoot;
        foreach (int index in _counterPath)
        {
            if (at == 0)
            {
                return -1;
            }

            at = _elements.Child(at, index);
        }

        if (at == 0 || !_reader.TryRead(at + (ulong)_flags, out uint flags))
        {
            return -1;
        }

        if ((flags & ~_visibleMask) != (_counterFingerprint & ~_visibleMask) || (flags & _visibleMask) == 0)
        {
            return -1;
        }

        string label = _reader.ReadStdWString(at + (ulong)_text);
        return FirstNumberIn(label);
    }

    /// <summary>
    /// The first run of digits in a label, or -1 when there is none worth believing.
    /// </summary>
    /// <remarks>
    /// Public so it can be tested: the label is localised, so parsing it by position or by
    /// splitting on words would work in English and nowhere else, and this is the one part of
    /// the walk that can be checked without the game.
    /// </remarks>
    public static int FirstNumberIn(string? label)
    {
        if (string.IsNullOrEmpty(label))
        {
            return -1;
        }

        int number = 0;
        bool started = false;
        foreach (char c in label)
        {
            if (c is >= '0' and <= '9')
            {
                number = (number * 10) + (c - '0');
                started = true;
            }
            else if (started)
            {
                break;
            }
        }

        // A line is a handful of maps. Anything else is a label this is not looking at.
        return started && number > 0 && number <= 30 ? number : -1;
    }
}
