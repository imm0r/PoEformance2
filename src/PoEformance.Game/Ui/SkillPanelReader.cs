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
/// gem tooltip and writes it nowhere else that either reference or this tool has found - not on
/// the skill object, not in the actor - so it is read as the TEXT it is: a row per skill, with
/// "DPS: 53.838" on it, and remembered so the bar keeps showing it after the panel shuts.
///
/// THE ROWS ARE READ WHILE THE PANEL IS SHUT TOO, which is an experiment with a readout rather
/// than a claim. The first build read them only while the panel was open, so a figure was as
/// fresh as the last look at the panel and a restart of the tool showed nothing until the panel
/// was opened once - and whether the game keeps a hidden panel's text current is a question only
/// the game can answer. So the list found while the panel was open is kept, its rows are read on
/// the same clock while it is shut, a shut panel the pointer still names is tried by the path
/// (not searched: the pointer may as well name the character sheet), and the readout says what
/// was found: how many rows still read, the first row's text, and whether the text CHANGED while
/// hidden and when. Changed means live figures for free; unchanged means the snapshot it always
/// was, now recovered after a restart without opening the panel. A hidden reading that parses to
/// nothing or to zero is not remembered, in case a shut panel blanks its rows.
///
/// WHERE THE ROWS ARE, from this tool's own interface browser (0.5.5): the panel is the open
/// left panel, ImportantUiElements.LeftPanelPtr, and its rows hang under a list at a child path
/// from it (schema SkillPanel.ListPath). Each row's first child is a header whose children are
/// four frames, the skill's icon, its name, "Level:", the level, a frame, and a block holding
/// the DPS text. The path is a GUESS THE READER CHECKS - the list it reaches must show a row
/// with a DPS text on it - and failing that the panel is searched for such a list, once, so a
/// patch that moves the list costs a search rather than a silence.
///
/// WHICH SKILL A ROW IS FOR is the question that decides everything, and it has two answers,
/// tried in order. By POINTER: the skill bar's slots carry the address of the skill object they
/// show (see SkillBarReader), and a row built for the same skill might carry the same address
/// on itself, its header or one of the header's children. It is looked for BY CONTENT - at the
/// slot's own offset on each of those elements, then anywhere in the first 0x600 bytes of each,
/// against the set of the player's skill objects - and where it turns up becomes the rule for
/// the rows after it. IN GAME IT TURNED UP NOWHERE (0.5.5, 2026-09-12: four rows, no pointer
/// within 0x600 bytes of any of them), which is why the second answer exists. By NAME: the row
/// prints the skill's displayed name, the skill's dat row carries exactly that string, and
/// PlayerSkills reads it for every skill object - so the name on the row names the object.
/// Both are reported, so a join that failed says so in the readout rather than looking like a
/// panel that was never opened.
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
    private string _rowNames = string.Empty;
    private int _notedRows = -1;
    private int _notedByPointer = -1;
    private int _notedByName = -1;
    private int _notedDps = -1;
    private string _notedRule = string.Empty;
    private string _notedRowNames = string.Empty;
    private readonly List<string> _names = [];

    // The shut panel's experiment: when the rows were last read open, when they were first read
    // shut without ever having been open, how often their text changed while shut and when last,
    // and the first row's text as it stands - all for the readout.
    private bool _wasOpen;
    private long _lastOpenMs = Never;
    private long _firstHiddenMs = Never;
    private long _hiddenChangedAt = Never;
    private int _hiddenChanges;
    private string _firstDps = string.Empty;
    private ulong _searchedPanel;
    private ulong _namedPanel;
    private string _named = string.Empty;

    /// <summary>How many of the rows' names the readout quotes.</summary>
    private const int NamesQuoted = 4;

    /// <summary>How much of the first row's text the readout quotes.</summary>
    private const int TextQuoted = 24;

    /// <summary>What was found on one row, kept so a refresh costs two string reads and not a search.</summary>
    private sealed class Row
    {
        public ulong Header;
        public ulong Name;
        public ulong Dps;
        public ulong Skill;
        public bool ByName;
        public int Hunts;
        public int HuntedVersion = -1;
        public string LastName = string.Empty;
        public string LastDps = string.Empty;
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
    /// Reads the rows when the clock says so: off the open panel, or off the kept list while the
    /// panel is shut. Otherwise what was read stands.
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
        bool element = _elements.IsUiElement(panel);
        bool open = element && _elements.IsVisible(panel);

        // On the clock, when the table changes, and the moment the panel opens or shuts.
        if (_refreshedAt != Never && nowMs - _refreshedAt < RefreshMs
            && skills.Version == _skillsVersion && open == _wasOpen)
        {
            return;
        }

        _refreshedAt = nowMs;
        _skillsVersion = skills.Version;
        _wasOpen = open;

        ulong list = open ? ResolveList(panel, nowMs, search: true) : 0;
        if (list != 0)
        {
            ReadOpen(list, nowMs, skills);
            return;
        }

        ReadShut(panel, element, open, nowMs, skills);
    }

    /// <summary>The rows off the open panel, and the note that says how they were joined.</summary>
    private void ReadOpen(ulong list, long nowMs, PlayerSkills skills)
    {
        _lastOpenMs = nowMs;
        (int read, int byPointer, int byName) = ReadRows(list, skills, nowMs, hidden: false);

        // The names as the rows spell them, quoted, so a readout shows a stray space or a
        // marker the game put in the text - the difference between a name that matches and
        // one that only looks as if it should.
        string rowNames = _names.Count == 0 ? string.Empty : "\"" + string.Join("\" \"", _names) + "\"";
        if (!string.Equals(rowNames, _rowNames, StringComparison.Ordinal))
        {
            _rowNames = rowNames;
        }

        if (read != _notedRows || byPointer != _notedByPointer || byName != _notedByName
            || _dps.Count != _notedDps || !ReferenceEquals(_ruleNote, _notedRule)
            || !ReferenceEquals(_rowNames, _notedRowNames))
        {
            _notedRows = read;
            _notedByPointer = byPointer;
            _notedByName = byName;
            _notedDps = _dps.Count;
            _notedRule = _ruleNote;
            _notedRowNames = _rowNames;
            _note = $"{read} rows read, {byPointer} by pointer ({_ruleNote}), {byName} by name; rows {_rowNames}";
        }
    }

    /// <summary>
    /// The rows off the kept list while the panel is shut - or while another left panel is
    /// open in its place - and the note that says what the pointer names and how the text behaves.
    /// </summary>
    private void ReadShut(ulong panel, bool element, bool open, long nowMs, PlayerSkills skills)
    {
        ulong list = _list;
        if (list != 0 && !_elements.IsUiElement(list))
        {
            Forget();
            list = 0;
        }

        // After a restart nothing is kept, but the pointer may still name the shut panel: the
        // path is tried on it, and only the path - the pointer may as well name the character
        // sheet, and a search of that twice a second is a walk of a panel per tick for nothing.
        if (list == 0 && element && !open)
        {
            list = ResolveList(panel, nowMs, search: false);
        }

        int read = 0;
        if (list != 0)
        {
            if (_lastOpenMs == Never && _firstHiddenMs == Never)
            {
                _firstHiddenMs = nowMs;
            }

            (read, _, _) = ReadRows(list, skills, nowMs, hidden: true);
        }

        if (panel != _namedPanel)
        {
            _namedPanel = panel;
            _named = NameOf(panel);
        }

        string where = panel == 0 ? "pointer null"
            : !element ? "pointer not an element"
            : open ? $"\"{_named}\" open, no rows on it"
            : $"\"{_named}\" hidden";

        string rows;
        if (list == 0)
        {
            rows = "no list kept";
        }
        else
        {
            string text = _hiddenChanges > 0
                ? $"changed {_hiddenChanges}× while hidden, last {Age(nowMs - _hiddenChangedAt)} ago"
                : _lastOpenMs != Never
                    ? $"unchanged since it shut {Age(nowMs - _lastOpenMs)} ago"
                    : $"unchanged since first read {Age(nowMs - _firstHiddenMs)} ago";
            rows = $"{read} rows still read, first \"{_firstDps}\", text {text}";
        }

        string note = $"{where}; {rows}";
        if (!string.Equals(note, _note, StringComparison.Ordinal))
        {
            _note = note;
        }
    }

    private static string Age(long ms)
    {
        long seconds = Math.Max(0, ms) / 1000;
        return seconds < 60
            ? seconds.ToString(CultureInfo.InvariantCulture) + " s"
            : (seconds / 60).ToString(CultureInfo.InvariantCulture) + " min";
    }

    /// <summary>
    /// The list, from the cache, from the path, or - when asked - from a search of the panel.
    /// </summary>
    /// <remarks>
    /// A panel WITHOUT a list leaves the kept one alone: the left panel is also the character
    /// sheet and the quest log, and opening one of those used to throw the Skills list away,
    /// which is the list the shut-panel reading lives on.
    /// </remarks>
    private ulong ResolveList(ulong panel, long nowMs, bool search)
    {
        if (_list != 0 && _panel == panel)
        {
            if (_elements.IsUiElement(_list))
            {
                return _list;
            }

            Forget();
        }

        ulong guess = panel;
        foreach (int step in _listPath)
        {
            guess = _elements.Child(guess, step);
            if (guess == 0)
            {
                break;
            }
        }

        ulong found = guess != 0 && LooksLikeTheList(guess) ? guess : 0;
        if (found == 0 && search)
        {
            // Searching the same panel twice a second for a list it does not have would be a
            // walk of a panel per tick; another panel is searched at once.
            if (_searchedPanel == panel && _searchedAt != Never && nowMs - _searchedAt < SearchAgainMs)
            {
                return 0;
            }

            _searchedPanel = panel;
            _searchedAt = nowMs;
            found = Search(panel);
        }

        if (found != 0 && (found != _list || panel != _panel))
        {
            _panel = panel;
            _panelName = NameOf(panel);
            _list = found;
            _rows.Clear();
        }

        return found;
    }

    /// <summary>Drops the kept list, when it stopped being an element.</summary>
    private void Forget()
    {
        _list = 0;
        _rows.Clear();
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

    /// <summary>
    /// Reads every showing row: its DPS, and which skill it is for. Returns how many were read
    /// and how many were matched each way.
    /// </summary>
    /// <param name="hidden">
    /// Whether the panel is shut: then a change of a row's text is the experiment's finding and
    /// is counted, and a figure is remembered only when it is a positive number.
    /// </param>
    private (int Read, int ByPointer, int ByName) ReadRows(ulong list, PlayerSkills skills, long nowMs, bool hidden)
    {
        int read = 0;
        int byPointer = 0;
        int byName = 0;
        _names.Clear();
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
                parts.ByName = false;
                parts.Hunts = 0;
            }

            if (parts.Skill != 0 && !skills.Contains(parts.Skill))
            {
                parts.Skill = 0;
                parts.ByName = false;
                parts.Hunts = 0;
            }

            if (parts.Skill == 0 && skills.Count > 0
                && (parts.Hunts < MostHunts || parts.HuntedVersion != skills.Version))
            {
                parts.Hunts = parts.HuntedVersion == skills.Version ? parts.Hunts + 1 : 1;
                parts.HuntedVersion = skills.Version;
                parts.Skill = Hunt(row, parts, skills);
                parts.ByName = false;
            }

            // The name, where no pointer answered: the row prints the skill's displayed name,
            // and the skill's dat row spells it the same - give or take the spaces the
            // interface pads a text with, which the trim is for.
            if (parts.Skill == 0 && name.Length > 0)
            {
                ReadOnlySpan<char> trimmed = name.AsSpan().Trim();
                parts.Skill = skills.ByName(trimmed.Length == name.Length ? name : trimmed.ToString());
                parts.ByName = parts.Skill != 0;
            }

            string dpsText = _reader.ReadStdWString(parts.Dps + (ulong)_text);
            if (!string.Equals(dpsText, parts.LastDps, StringComparison.Ordinal))
            {
                // Counted from the second reading of a row on: the first only establishes
                // what the text was. A change seen while the panel is shut is the finding.
                if (hidden && parts.LastDps.Length > 0)
                {
                    _hiddenChanges++;
                    _hiddenChangedAt = nowMs;
                }

                parts.LastDps = dpsText;
            }

            if (read == 0)
            {
                ReadOnlySpan<char> shown = dpsText.AsSpan().Trim();
                _firstDps = shown.Length <= TextQuoted ? shown.ToString() : string.Concat(shown[..(TextQuoted - 1)], "…");
            }

            int? dps = DpsText.Parse(dpsText);
            read++;
            if (_names.Count < NamesQuoted)
            {
                _names.Add(name);
            }

            if (parts.Skill != 0)
            {
                if (parts.ByName)
                {
                    byName++;
                }
                else
                {
                    byPointer++;
                }

                if (dps is int value && (!hidden || value > 0))
                {
                    Remember(skills.KeyOf(parts.Skill), value);
                }
            }
        }

        return (read, byPointer, byName);
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
