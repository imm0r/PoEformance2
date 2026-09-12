using System.Buffers.Binary;
using System.Globalization;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Components;

namespace PoEformance.Game.Ui;

/// <summary>The figure in a DPS text, read the way the game writes it.</summary>
/// <remarks>
/// THE GAME WRITES THE NUMBER IN THE PLAYER'S LOCALE: "DPS: 53.838" on a German client is
/// fifty-three thousand, and "DPS: 53,838" on an English one is the same number. So a separator
/// followed by exactly three digits groups thousands whichever character it is, and only a
/// separator followed by one or two digits is a decimal point. A figure with no digits at all -
/// the dash the panel shows for a skill that deals none - is no figure, rather than zero.
/// </remarks>
public static class DpsText
{
    /// <summary>The number after the label, or null when the text carries none.</summary>
    public static int? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        int colon = text.IndexOf(':', StringComparison.Ordinal);
        ReadOnlySpan<char> figure = colon >= 0 ? text.AsSpan(colon + 1) : text.AsSpan();

        Span<char> compact = stackalloc char[32];
        int length = 0;
        int lastSeparator = -1;
        foreach (char c in figure)
        {
            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            if (length == compact.Length)
            {
                return null;
            }

            if (char.IsAsciiDigit(c))
            {
                compact[length++] = c;
            }
            else if (c is '.' or ',')
            {
                lastSeparator = length;
                compact[length++] = c;
            }
            else
            {
                return null; // a dash, a letter: not a figure
            }
        }

        if (length == 0 || !char.IsAsciiDigit(compact[0]) || !char.IsAsciiDigit(compact[length - 1]))
        {
            return null;
        }

        ReadOnlySpan<char> digits = compact[..length];
        long whole = 0;
        int fractionDigits = 0;
        long fraction = 0;
        int group = 0;
        bool grouped = false;

        // Decided by the tail: what follows the last separator is either a group of three or a
        // fraction, and every separator before it must group.
        bool decimalTail = lastSeparator >= 0 && length - lastSeparator - 1 != 3;

        for (int i = 0; i < length; i++)
        {
            char c = digits[i];
            if (c is '.' or ',')
            {
                if (grouped && group != 3)
                {
                    return null; // "1.23.456" is nothing
                }

                grouped = true;
                group = 0;
                continue;
            }

            if (decimalTail && i > lastSeparator)
            {
                fraction = (fraction * 10) + (c - '0');
                fractionDigits++;
                continue;
            }

            group++;
            whole = (whole * 10) + (c - '0');
            if (whole > int.MaxValue)
            {
                return null;
            }
        }

        if (grouped && !decimalTail && group != 3)
        {
            return null;
        }

        if (fractionDigits > 0)
        {
            // Rounded half up from the first fraction digit - the rest cannot change it.
            long scale = 1;
            for (int i = 0; i < fractionDigits; i++)
            {
                scale *= 10;
            }

            if (fraction * 2 >= scale)
            {
                whole++;
            }
        }

        return whole > int.MaxValue ? null : (int)whole;
    }

    /// <summary>Whether a text is a DPS label at all.</summary>
    public static bool IsDps(string text)
        => text.StartsWith("DPS", StringComparison.OrdinalIgnoreCase);

    /// <summary>For the readouts: the figure as the game wrote it, or a dash.</summary>
    public static string Show(int? figure)
        => figure is int value ? value.ToString(CultureInfo.InvariantCulture) : "-";
}

/// <summary>
/// Reads each skill's DPS off the Skills panel while it is open, and remembers it.
/// </summary>
/// <remarks>
/// THE PANEL IS THE ONLY SOURCE. The game computes a skill's DPS for the Skills window and the
/// gem tooltip and writes it nowhere else - not on the skill object, not in the actor - so it is
/// read as the TEXT it is: a row per skill, with "DPS: 53.838" on it. That fixes what the
/// feature can and cannot do: the figure is as fresh as the last time the panel was open, and
/// it is remembered so the bar keeps showing it after the panel shuts.
///
/// WHERE THE ROWS ARE, from this tool's own interface browser (0.5.5): the panel is the open
/// left panel, ImportantUiElements.LeftPanelPtr, and its rows hang under a list at a child path
/// from it (schema SkillPanel.ListPath). Each row's first child is a header whose children are
/// four frames, the skill's icon, its name, "Level:", the level, a frame, and a block holding
/// the DPS text. The path is a GUESS THE READER CHECKS - the list it reaches must show a row
/// with a DPS text on it - and failing that the panel is searched for such a list, once, so a
/// patch that moves the list costs a search rather than a silence.
///
/// WHICH SKILL A ROW IS FOR is the question that decides everything, and it is answered by
/// POINTER rather than by name: the skill bar's slots carry the address of the skill object they
/// show (see SkillBarReader), and a row built for the same skill is expected to carry the same
/// address somewhere on itself, its header or one of the header's children - the icon being the
/// likeliest, as the slot is an icon too. It is FOUND BY CONTENT: first at the slot's own offset
/// on each of those elements, then anywhere in the first 0x600 bytes of each, against the set
/// of the player's skill objects. Where it turned up becomes the rule for the rows after it and
/// is reported, so a join that failed says so in the readout rather than looking like a panel
/// that was never opened.
///
/// REMEMBERED BY THE SKILL'S LASTING KEY, see <see cref="PlayerSkills"/>, and published as a
/// dictionary that is replaced rather than changed once handed out, so a snapshot holding it
/// never sees a write.
/// </remarks>
public sealed class SkillPanelReader
{
    /// <summary>How long a reading of the rows stands while the panel is open.</summary>
    public const long RefreshMs = 500;

    /// <summary>How long before the panel is searched for the list again after a search found nothing.</summary>
    public const long SearchAgainMs = 5000;

    /// <summary>How much of a row's elements is searched for the skill pointer.</summary>
    public const int HuntBytes = 0x600;

    /// <summary>How often a row is searched for its skill before it is left alone until the table changes.</summary>
    private const int MostHunts = 3;

    /// <summary>Most children looked at, at each level. A bound on a count read out of memory.</summary>
    private const int MostChildren = 64;

    /// <summary>How deep, and how far, the panel is searched for the list when the path misses.</summary>
    private const int SearchDepth = 8;
    private const int SearchBudget = 1500;

    /// <summary>What makes an element a candidate for the list: a row per gem, so at least this many children.</summary>
    private const int FewestRows = 8;

    private const int RowCarrier = -2;
    private const int HeaderCarrier = -1;
    private const long Never = long.MinValue;

    private readonly IMemoryReader _reader;
    private readonly UiElementReader _elements;
    private readonly int _leftPanel;
    private readonly int _text;
    private readonly int _stringId;
    private readonly int _skillAt;
    private readonly int _skillAtBefore;
    private readonly int _mostRows;
    private readonly int[] _listPath;
    private readonly byte[] _hunt = new byte[HuntBytes];
    private readonly Dictionary<ulong, Row> _rows = [];

    private Dictionary<ulong, int> _dps = [];
    private ulong _panel;
    private ulong _list;
    private string _panelName = string.Empty;
    private long _refreshedAt = Never;
    private long _searchedAt = Never;
    private int _skillsVersion = -1;
    private int _ruleCarrier;
    private int _ruleOffset;
    private string _ruleNote = "none";
    private string _note = "not seen yet";
    private int _notedRows = -1;
    private int _notedDps = -1;
    private string _notedRule = string.Empty;

    /// <summary>What was found on one row, kept so a refresh costs two string reads and not a search.</summary>
    private sealed class Row
    {
        public ulong Header;
        public ulong Name;
        public ulong Dps;
        public ulong Skill;
        public int Hunts;
        public int HuntedVersion = -1;
        public string LastName = string.Empty;
        public List<ulong> HeaderChildren = [];
    }

    public SkillPanelReader(IMemoryReader reader, OffsetSchema schema, UiElementReader elements)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(elements);
        _reader = reader;
        _elements = elements;

        _leftPanel = schema.Structs["ImportantUiElements"].OffsetOf("LeftPanelPtr");

        StructDef ui = schema.Structs["UiElementBase"];
        _text = ui.OffsetOf("TextPtr");
        _stringId = ui.OffsetOf("StringIdPtr");

        StructDef slot = schema.Structs["SkillBarSlot"];
        _skillAt = slot.OffsetOf("ActiveSkillPtr");
        _skillAtBefore = (int)slot.Constants["ActiveSkillPtrBeforeShift"];

        StructDef panel = schema.Structs["SkillPanel"];
        _mostRows = (int)panel.Constants["MostRows"];
        _listPath =
        [
            (int)panel.Constants["ListPath0"],
            (int)panel.Constants["ListPath1"],
            (int)panel.Constants["ListPath2"],
            (int)panel.Constants["ListPath3"],
            (int)panel.Constants["ListPath4"],
        ];
    }

    /// <summary>The DPS last shown per skill, by the skill's lasting key. Never written once handed out.</summary>
    public IReadOnlyDictionary<ulong, int> Dps => _dps;

    /// <summary>The list the rows hang under, as last resolved, or 0 - for the readouts.</summary>
    public ulong Element => _list;

    /// <summary>The open left panel's own id, as last seen - for the readouts.</summary>
    public string PanelName => _panelName;

    /// <summary>How many rows were read last time, and where a row keeps its skill - for the readouts.</summary>
    public string Note => _note;

    /// <summary>
    /// Reads the rows when the panel is open and the clock says so. Otherwise what was read stands.
    /// </summary>
    /// <param name="uiRoot">The interface root the left panel pointer hangs off.</param>
    /// <param name="nowMs">A monotonic clock, for pacing the reads.</param>
    /// <param name="skills">The player's skill table, which is what a row's pointer is matched against.</param>
    public void Read(ulong uiRoot, long nowMs, PlayerSkills skills)
    {
        ArgumentNullException.ThrowIfNull(skills);

        if (uiRoot == 0)
        {
            return;
        }

        ulong panel = _reader.ReadPointer(uiRoot + (ulong)_leftPanel);
        if (!_elements.IsUiElement(panel) || !_elements.IsVisible(panel))
        {
            _refreshedAt = Never;
            return;
        }

        if (_refreshedAt != Never && nowMs - _refreshedAt < RefreshMs && skills.Version == _skillsVersion)
        {
            return;
        }

        _refreshedAt = nowMs;
        _skillsVersion = skills.Version;

        ulong list = ResolveList(panel, nowMs);
        int read = list == 0 ? 0 : ReadRows(list, skills);

        if (read != _notedRows || _dps.Count != _notedDps || !ReferenceEquals(_ruleNote, _notedRule))
        {
            _notedRows = read;
            _notedDps = _dps.Count;
            _notedRule = _ruleNote;
            _note = list == 0
                ? $"open ({_panelName}), no rows found"
                : $"{read} rows read, skill at {_ruleNote}";
        }
    }

    /// <summary>The list, from the cache, from the path, or from a search of the panel.</summary>
    private ulong ResolveList(ulong panel, long nowMs)
    {
        if (_list != 0 && _panel == panel && _elements.IsUiElement(_list))
        {
            return _list;
        }

        if (_panel != panel)
        {
            _panel = panel;
            _panelName = NameOf(panel);
            _searchedAt = Never;
        }

        _list = 0;
        _rows.Clear();

        ulong guess = panel;
        foreach (int step in _listPath)
        {
            guess = _elements.Child(guess, step);
            if (guess == 0)
            {
                break;
            }
        }

        if (guess != 0 && LooksLikeTheList(guess))
        {
            _list = guess;
            return _list;
        }

        // The left panel is also the character sheet and the quest log, and searching those
        // twice a second for a list they do not have would be a walk of a panel per tick.
        if (_searchedAt != Never && nowMs - _searchedAt < SearchAgainMs)
        {
            return 0;
        }

        _searchedAt = nowMs;
        _list = Search(panel);
        return _list;
    }

    /// <summary>Whether an element's visible children are rows: one of the first few carries a DPS text.</summary>
    private bool LooksLikeTheList(ulong element)
    {
        List<ulong> children = _elements.Children(element, _mostRows);
        if (children.Count < 2)
        {
            return false;
        }

        int looked = 0;
        foreach (ulong child in children)
        {
            if (!_elements.IsShowingItself(child))
            {
                continue;
            }

            if (Locate(child).Dps != 0)
            {
                return true;
            }

            if (++looked >= 4)
            {
                break;
            }
        }

        return false;
    }

    /// <summary>A bounded breadth-first search of the panel's visible elements for the list.</summary>
    private ulong Search(ulong panel)
    {
        var queue = new Queue<(ulong Element, int Depth)>();
        queue.Enqueue((panel, 0));
        int budget = SearchBudget;

        while (queue.Count > 0 && budget-- > 0)
        {
            (ulong element, int depth) = queue.Dequeue();
            List<ulong> children = _elements.Children(element, _mostRows);

            if (children.Count >= FewestRows && LooksLikeTheList(element))
            {
                return element;
            }

            if (depth >= SearchDepth)
            {
                continue;
            }

            foreach (ulong child in children)
            {
                if (_elements.IsShowingItself(child))
                {
                    queue.Enqueue((child, depth + 1));
                }
            }
        }

        return 0;
    }

    /// <summary>Reads every showing row: its DPS, and which skill it is for. Returns how many were read.</summary>
    private int ReadRows(ulong list, PlayerSkills skills)
    {
        int read = 0;
        foreach (ulong row in _elements.Children(list, _mostRows))
        {
            if (!_elements.IsShowingItself(row))
            {
                continue;
            }

            if (!_rows.TryGetValue(row, out Row? parts))
            {
                parts = Locate(row);
                _rows[row] = parts;
            }

            if (parts.Dps == 0)
            {
                continue;
            }

            // A row is re-used for whatever gem takes its place, so the name is what says the
            // skill behind it may have changed.
            string name = parts.Name != 0 ? _reader.ReadStdWString(parts.Name + (ulong)_text) : string.Empty;
            if (!string.Equals(name, parts.LastName, StringComparison.Ordinal))
            {
                parts.LastName = name;
                parts.Skill = 0;
                parts.Hunts = 0;
            }

            if (parts.Skill != 0 && !skills.Contains(parts.Skill))
            {
                parts.Skill = 0;
                parts.Hunts = 0;
            }

            if (parts.Skill == 0 && skills.Count > 0
                && (parts.Hunts < MostHunts || parts.HuntedVersion != skills.Version))
            {
                parts.Hunts = parts.HuntedVersion == skills.Version ? parts.Hunts + 1 : 1;
                parts.HuntedVersion = skills.Version;
                parts.Skill = Hunt(row, parts, skills);
            }

            int? dps = DpsText.Parse(_reader.ReadStdWString(parts.Dps + (ulong)_text));
            read++;

            if (parts.Skill != 0 && dps is int value)
            {
                Remember(skills.KeyOf(parts.Skill), value);
            }
        }

        return read;
    }

    /// <summary>Finds a row's header, its name text and its DPS text - once per row.</summary>
    private Row Locate(ulong row)
    {
        var parts = new Row();
        ulong header = _elements.Child(row, 0);
        if (!_elements.IsUiElement(header))
        {
            return parts;
        }

        parts.Header = header;
        parts.HeaderChildren = _elements.Children(header, MostChildren);

        foreach (ulong child in parts.HeaderChildren)
        {
            string text = _reader.ReadStdWString(child + (ulong)_text);
            if (text.Length == 0)
            {
                continue;
            }

            if (DpsText.IsDps(text))
            {
                parts.Dps = child;
            }
            else if (parts.Name == 0)
            {
                parts.Name = child;
            }
        }

        if (parts.Dps != 0)
        {
            return parts;
        }

        // One level further: the browser found the text inside a block of the header's, not
        // among the header's own children.
        foreach (ulong child in parts.HeaderChildren)
        {
            foreach (ulong grandchild in _elements.Children(child, MostChildren))
            {
                if (DpsText.IsDps(_reader.ReadStdWString(grandchild + (ulong)_text)))
                {
                    parts.Dps = grandchild;
                    return parts;
                }
            }
        }

        return parts;
    }

    /// <summary>The skill object a row is for, found by content - see the remarks on the class.</summary>
    private ulong Hunt(ulong row, Row parts, PlayerSkills skills)
    {
        // The rule the rows before it established, first.
        if (_ruleOffset != 0)
        {
            ulong carrier = CarrierOf(row, parts, _ruleCarrier);
            if (carrier != 0)
            {
                ulong pointer = _reader.ReadPointer(carrier + (ulong)_ruleOffset);
                if (skills.Contains(pointer))
                {
                    return pointer;
                }
            }
        }

        // Then the slot's own offsets on every element the row is made of.
        for (int carrier = RowCarrier; carrier < parts.HeaderChildren.Count; carrier++)
        {
            ulong element = CarrierOf(row, parts, carrier);
            if (element == 0)
            {
                continue;
            }

            ulong at = _reader.ReadPointer(element + (ulong)_skillAt);
            if (skills.Contains(at))
            {
                return Ruled(carrier, _skillAt, at);
            }

            ulong before = _reader.ReadPointer(element + (ulong)_skillAtBefore);
            if (skills.Contains(before))
            {
                return Ruled(carrier, _skillAtBefore, before);
            }
        }

        // Then anywhere in each of them.
        for (int carrier = RowCarrier; carrier < parts.HeaderChildren.Count; carrier++)
        {
            ulong element = CarrierOf(row, parts, carrier);
            if (element == 0 || !_reader.TryRead(element, _hunt))
            {
                continue;
            }

            for (int at = 0; at + 8 <= HuntBytes; at += 8)
            {
                ulong pointer = BinaryPrimitives.ReadUInt64LittleEndian(_hunt.AsSpan(at));
                if (skills.Contains(pointer))
                {
                    return Ruled(carrier, at, pointer);
                }
            }
        }

        return 0;
    }

    private ulong Ruled(int carrier, int offset, ulong pointer)
    {
        if (carrier != _ruleCarrier || offset != _ruleOffset)
        {
            _ruleCarrier = carrier;
            _ruleOffset = offset;
            string where = carrier switch
            {
                RowCarrier => "row",
                HeaderCarrier => "header",
                _ => $"header child {carrier}",
            };
            _ruleNote = $"{where}+0x{offset:X}";
        }

        return pointer;
    }

    private static ulong CarrierOf(ulong row, Row parts, int carrier)
        => carrier switch
        {
            RowCarrier => row,
            HeaderCarrier => parts.Header,
            _ => carrier < parts.HeaderChildren.Count ? parts.HeaderChildren[carrier] : 0,
        };

    /// <summary>Records a figure, replacing the published dictionary rather than writing into it.</summary>
    private void Remember(ulong key, int dps)
    {
        if (key == 0 || (_dps.TryGetValue(key, out int known) && known == dps))
        {
            return;
        }

        var next = new Dictionary<ulong, int>(_dps) { [key] = dps };
        _dps = next;
    }

    /// <summary>An element's StringId, or empty when it has none or is not an element.</summary>
    private string NameOf(ulong element)
        => _elements.IsUiElement(element)
            ? _reader.ReadStdWString(element + (ulong)_stringId)
            : string.Empty;
}
