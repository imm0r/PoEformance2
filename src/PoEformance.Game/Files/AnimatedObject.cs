using System.Text;

namespace PoEformance.Game.Files;

/// <summary>Which of the four ways an entry's value is written. See <see cref="AoEntry"/>.</summary>
public enum AoValueKind
{
    /// <summary>Unquoted: a native type, like <c>true</c>, <c>3</c> or <c>0.75</c>.</summary>
    Native,

    /// <summary>Double-quoted, on one line. The usual spelling of a file path.</summary>
    Quoted,

    /// <summary>Single-quoted: a JSON-like payload that may span lines and may contain <c>"</c>.</summary>
    Payload,

    /// <summary>A <c>{ … }</c> block with a script in it, braces nested. Version 3 and up.</summary>
    Script,
}

/// <summary>
/// One <c>key = value</c> line, with whatever was indented underneath it.
/// </summary>
/// <remarks>
/// THE KEY IS NOT UNIQUE - the format's own rule, which is why this is a list everywhere rather
/// than a dictionary. A struct carrying three <c>attached_object</c> entries is three attachments,
/// and a dictionary would silently keep the last.
/// </remarks>
/// <param name="Key">As written. An animation keyframe's key is a TIME, like <c>0.35</c>.</param>
/// <param name="Value">The text between the quotes, or the whole rest of the line when there were none.</param>
/// <param name="Kind">Which of the four spellings it was written in.</param>
/// <param name="Children">Entries indented under this one.</param>
public sealed record AoEntry(
    string Key,
    string Value,
    AoValueKind Kind,
    IReadOnlyList<AoEntry> Children)
{
    /// <summary>This entry and everything under it, in the order the file has them.</summary>
    public IEnumerable<AoEntry> Walk()
    {
        yield return this;
        foreach (AoEntry child in Children)
        {
            foreach (AoEntry deeper in child.Walk())
            {
                yield return deeper;
            }
        }
    }
}

/// <summary>One named block - <c>AnimationController { … }</c> - and what it holds.</summary>
/// <param name="Name">The struct type. Not restricted to the known set - see the remarks on the class.</param>
/// <param name="Entries">Its top-level entries, each with its own children.</param>
/// <param name="Client">Whether it sat inside the file's <c>client { … }</c> block.</param>
public sealed record AoStruct(string Name, IReadOnlyList<AoEntry> Entries, bool Client);

/// <summary>
/// An Animated Object (<c>.ao</c>) file, as the game writes it.
/// </summary>
/// <remarks>
/// A TEXT FILE, which is the whole reason this is worth having. The monster tables hand out
/// <c>.ao</c> paths in MonsterVarieties' AOFiles column, and until this existed the only thing
/// that could be said about one was its name. It is UTF-16 with a byte order mark, a pseudo-OOP
/// list of struct definitions, and it says what a monster is MADE of: which animation controller,
/// which attached objects, which effect packs, and - the question this was written for - what its
/// animations are called.
///
/// TWO REFERENCES, AND THEY DISAGREE ON COVERAGE. zao's <c>libpoe/poe/format/ao.cpp</c> is a real
/// parser against real files, and its live path reads only the version, the extends chain and each
/// struct's NAME - every struct body is kept as raw lines and the structured half of it is behind
/// an <c>#if 0</c>. Its abandoned patterns are still the best evidence for the entry shape, and
/// they are what the reading of children here follows:
///
///     \t(\S+) = (.*)            a top-level entry
///     \t\t(\S+) = (.*)          a child
///     \t\t([0-9.,]+) = (.*)     an animation keyframe, whose key is a time
///
/// The format diagram covers what that parser does not: the optional <c>abstract</c> line, the
/// version 3 <c>client { … }</c> block, and the fourth value spelling, a braced script. So this
/// takes the shape from the diagram and the entry rules from the parser, and neither alone.
///
/// SCANNED CHARACTER-WISE RATHER THAN BY LINE, which the diagram asks for in as many words, and
/// for a reason that bites immediately: a single-quoted payload spans lines and may contain a
/// double quote, and a script block nests braces. A line-based reader gets both wrong, and gets
/// them wrong SILENTLY - it would read half a payload as a value and the rest as garbage entries.
///
/// AND NOTHING IS DROPPED QUIETLY. Anything this cannot make sense of becomes a line in
/// <see cref="Faults"/> with the text that caused it, because the first use of this reader is to
/// find out what is in these files - and a reader that silently skipped what surprised it would
/// report a tidy, wrong answer. A file with faults still returns everything it did read.
///
/// THE STRUCT NAMES ARE NOT RESTRICTED to the dozen the diagram lists. An unknown name is data
/// about the format, not an error in the file, and refusing it would turn the one interesting
/// case - a struct nobody has seen - into a parse failure.
/// </remarks>
/// <param name="Version">The format version off the first line.</param>
/// <param name="Abstract">Whether the file declared itself abstract.</param>
/// <param name="Extends">The <c>.ao</c> files it inherits from, in order. May be the literal "nothing".</param>
/// <param name="Structs">Every struct, those inside a <c>client</c> block included and marked.</param>
/// <param name="Faults">What could not be read, and the text that caused it. Empty is a clean parse.</param>
public sealed record AnimatedObject(
    int Version,
    bool Abstract,
    IReadOnlyList<string> Extends,
    IReadOnlyList<AoStruct> Structs,
    IReadOnlyList<string> Faults)
{
    /// <summary>Nothing read - a missing file, or one that is not an .ao at all.</summary>
    public static AnimatedObject None { get; } = new(0, false, [], [], []);

    /// <summary>Whether anything was read. A file with faults can still be worth looking at.</summary>
    public bool Ready => Version > 0 || Structs.Count > 0 || Extends.Count > 0;

    /// <summary>Every entry in every struct, flattened, with the struct it came from.</summary>
    public IEnumerable<(AoStruct Struct, AoEntry Entry)> Entries()
    {
        foreach (AoStruct one in Structs)
        {
            foreach (AoEntry entry in one.Entries)
            {
                foreach (AoEntry deeper in entry.Walk())
                {
                    yield return (one, deeper);
                }
            }
        }
    }

    /// <summary>The structs of one type, of which there may be several.</summary>
    public IEnumerable<AoStruct> Named(string name)
        => Structs.Where(one => string.Equals(one.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads a file's bytes, or hands back <see cref="None"/> where there is nothing to read.
    /// </summary>
    /// <remarks>
    /// The decode is <see cref="StatDescriptionFiles.Decode"/>, which already knows the three
    /// shapes the game's text files come in. zao's reader takes UTF-16 and only UTF-16; sharing
    /// the decode here costs nothing and means one place is wrong if any of them is.
    /// </remarks>
    public static AnimatedObject Read(byte[]? content)
        => content is not { Length: > 0 } ? None : Parse(StatDescriptionFiles.Decode(content));

    /// <summary>
    /// Reads one out of an open install, by path. Null install or missing file gives None.
    /// </summary>
    /// <remarks>
    /// AN EXTENSION IS ADDED WHERE THERE IS NONE, and this was measured rather than guessed: the
    /// first survey over a real install asked for 3231 files and found 1687, and what was missing
    /// was the whole inheritance chain. The game writes an extends line WITHOUT the extension -
    ///
    ///     extends "Metadata/Monsters/Skeletons/Basic/SkeletonBasic"
    ///     extends "Metadata/Parent"
    ///
    /// - while an attached_object carries its ".ao" in full. Half of every walk was landing on
    /// nothing, and the half it lost was the base files, where what monsters have in COMMON lives.
    ///
    /// THE PATH AS WRITTEN IS STILL TRIED FIRST, so nothing that used to resolve stops resolving:
    /// this only adds a second attempt where the first found nothing and the name carries no
    /// extension at all. An index lookup is a hash of the path, so the failed attempt is free.
    /// </remarks>
    public static AnimatedObject Read(GameFiles? files, string? path)
    {
        if (files is null || string.IsNullOrWhiteSpace(path))
        {
            return None;
        }

        string said = path.Replace('\\', '/').Trim();
        if (files.Read(said) is { Length: > 0 } content)
        {
            return Read(content);
        }

        return Bare(said) ? Read(files.Read(said + Suffix)) : None;
    }

    /// <summary>What an .ao file is called, for the paths the game writes without one.</summary>
    public const string Suffix = ".ao";

    /// <summary>Whether a path's last segment carries no extension at all.</summary>
    private static bool Bare(string path)
    {
        int slash = path.LastIndexOf('/');
        return path.IndexOf('.', slash + 1) < 0;
    }

    /// <summary>Reads the text of one. Public so the parser can be tested without an install.</summary>
    public static AnimatedObject Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return None;
        }

        var scan = new AoScanner(text);
        return scan.Run();
    }
}

/// <summary>
/// The character scanner behind <see cref="AnimatedObject.Parse"/>.
/// </summary>
/// <remarks>
/// A HAND-WRITTEN SCANNER AND NOT A REGEX, for the same reason the query grammar is one: this
/// ships Native AOT, where a regex is either interpreted or source-generated, and the shape here
/// is small enough that the scanner is the shorter of the two anyway. It also keeps the file in
/// one pass over one string with no allocation per line.
/// </remarks>
internal sealed class AoScanner(string text)
{
    /// <summary>How many faults are kept before the rest are counted rather than quoted.</summary>
    private const int MostFaults = 64;

    /// <summary>How much of an offending line a fault quotes.</summary>
    private const int FaultWidth = 120;

    private readonly string _text = text;
    private int _at;
    private readonly List<string> _faults = [];
    private int _extraFaults;

    public AnimatedObject Run()
    {
        int version = Version();
        bool abstracted = Word("abstract");

        var extends = new List<string>();
        while (Extends() is { } one)
        {
            extends.Add(one);
        }

        var structs = new List<AoStruct>();
        while (true)
        {
            SkipTrivia();
            if (Done)
            {
                break;
            }

            // THE CLIENT BLOCK HOLDS STRUCTS, NOT ENTRIES - version 3 and up, and it appears at
            // most once. Its contents are flattened into the same list rather than kept apart,
            // because what is IN them is the same kind of thing; the flag says where they sat.
            if (Word("client"))
            {
                Client(structs);
                continue;
            }

            if (Struct(client: false, structs) is { } read)
            {
                structs.Add(read);
                continue;
            }

            // Nothing matched and nothing was consumed: take the line so the loop cannot spin.
            Fault("not a struct", TakeLine());
        }

        if (_extraFaults > 0)
        {
            _faults.Add($"… and {_extraFaults.ToString(System.Globalization.CultureInfo.InvariantCulture)} more");
        }

        return new AnimatedObject(version, abstracted, extends, structs, _faults);
    }

    private bool Done => _at >= _text.Length;

    private int Version()
    {
        SkipTrivia();
        if (!Word("version"))
        {
            Fault("no version line", Peek());
            return 0;
        }

        SkipSpaces();
        int from = _at;
        while (!Done && char.IsAsciiDigit(_text[_at]))
        {
            _at++;
        }

        if (from == _at)
        {
            Fault("version without a number", Peek());
            return 0;
        }

        return int.TryParse(_text.AsSpan(from, _at - from), out int read) ? read : 0;
    }

    /// <summary>One <c>extends "…"</c> line, or null when the next thing is not one.</summary>
    private string? Extends()
    {
        if (!Word("extends"))
        {
            return null;
        }

        SkipSpaces();
        if (!Done && _text[_at] == '"')
        {
            return Until('"');
        }

        // A malformed extends is worth a fault rather than a silent skip: it is the line that
        // decides which OTHER file a monster is built from, so losing one loses a whole branch.
        Fault("extends without a quoted path", TakeLine());
        return null;
    }

    private void Client(List<AoStruct> into)
    {
        SkipTrivia();
        if (!Take('{'))
        {
            Fault("client without a brace", Peek());
            return;
        }

        while (true)
        {
            SkipTrivia();
            if (Done)
            {
                Fault("client block never closed", string.Empty);
                return;
            }

            if (Take('}'))
            {
                return;
            }

            if (Struct(client: true, into) is { } read)
            {
                into.Add(read);
                continue;
            }

            Fault("not a struct inside client", TakeLine());
        }
    }

    /// <summary>A <c>Name { … }</c> block. The brace may sit on the name's line or the next.</summary>
    /// <param name="client">Whether this sits inside the file's client block.</param>
    /// <param name="inside">
    /// Where a struct found INSIDE this one is put - flat beside it rather than under it, the same
    /// way a client block's structs are. It lands in the list before its parent does, because the
    /// parent is not finished until its closing brace; nothing reads these in order.
    /// </param>
    private AoStruct? Struct(bool client, List<AoStruct> inside)
    {
        SkipTrivia();

        // A STRUCT NAME HAS TO START WITH A LETTER, which an entry key does not - the reference
        // spells struct names [A-Za-z0-9]+ while its entry keys are \S+. Without this, a line of
        // rubbish between two structs is taken as a nameless struct and the NEXT struct's opening
        // brace is read as its own, so one bad line quietly eats the struct after it.
        int was = _at;
        if (Name() is not { Length: > 0 } name || !char.IsLetter(name[0]))
        {
            _at = was;
            return null;
        }

        SkipTrivia();
        if (!Take('{'))
        {
            Fault($"struct {name} without a brace", Peek());
            return new AoStruct(name, [], client);
        }

        // Read the entries flat, each with the column its key began at, then hang them off one
        // another by that column. Indentation is a LINE fact and the entries are read by
        // character, so the two are kept apart and joined once at the end.
        var flat = new List<(int Indent, AoEntry Entry)>();
        while (true)
        {
            SkipTrivia();
            if (Done)
            {
                Fault($"struct {name} never closed", string.Empty);
                break;
            }

            if (Take('}'))
            {
                break;
            }

            int indent = Column();
            if (Entry() is { } entry)
            {
                flat.Add((indent, entry));
                continue;
            }

            // A BRACE WHERE AN ENTRY WAS EXPECTED, which the first survey over a real install
            // turned up on 40 of 1687 files - every one of its faults this same shape. Two things
            // produce it and both are honoured here rather than one of them being picked:
            //
            //   1. A VALUE WHOSE BRACE IS ON THE NEXT LINE. "key =" reads as an entry with an
            //      empty native value, and the "{" then has no key in front of it. The script
            //      belongs to that entry, so it is attached to it.
            //   2. A STRUCT INSIDE A STRUCT. "Sub {" reads as a key with no "=" after it, and the
            //      brace is the block's own. It joins the file's struct list, the way the ones in
            //      a client block do.
            //
            // Neither reference describes either, which is what the fault was for: they are what
            // the files say and the diagram does not. Anything that is still neither keeps its
            // fault, so a third shape cannot hide inside the fix for the first two.
            if (!Done && _text[_at] == '{')
            {
                if (flat.Count > 0 && flat[^1].Entry is { Kind: AoValueKind.Native, Value.Length: 0 })
                {
                    (int at, AoEntry waiting) = flat[^1];
                    flat[^1] = (at, waiting with { Value = Script(), Kind = AoValueKind.Script });
                    continue;
                }

                if (Nested(client) is { } inner)
                {
                    inside.Add(inner);
                    continue;
                }
            }

            Fault($"not an entry in {name}", TakeLine());
        }

        return new AoStruct(name, Nest(flat), client);
    }

    /// <summary>
    /// A struct written inside another, read from its opening brace back.
    /// </summary>
    /// <remarks>
    /// THE NAME WAS ALREADY EATEN by the entry attempt that failed, so it is recovered from the
    /// text rather than re-read: everything from the start of this line up to the brace. Reading
    /// it forwards would mean un-consuming a token, which this scanner cannot do.
    /// </remarks>
    private AoStruct? Nested(bool client)
    {
        int line = _text.LastIndexOf('\n', Math.Max(0, _at - 1)) + 1;
        string name = _text[line.._at].Trim();

        if (name.Length == 0 || !char.IsLetter(name[0]))
        {
            return null;
        }

        foreach (char c in name)
        {
            if (char.IsWhiteSpace(c))
            {
                return null;
            }
        }

        _at++;
        var flat = new List<(int Indent, AoEntry Entry)>();
        while (true)
        {
            SkipTrivia();
            if (Done)
            {
                Fault($"nested struct {name} never closed", string.Empty);
                break;
            }

            if (Take('}'))
            {
                break;
            }

            int indent = Column();
            if (Entry() is not { } entry)
            {
                Fault($"not an entry in nested {name}", TakeLine());
                continue;
            }

            flat.Add((indent, entry));
        }

        return new AoStruct(name, Nest(flat), client);
    }

    /// <summary>One <c>key = value</c>, without its children - those are hung on by indentation.</summary>
    private AoEntry? Entry()
    {
        if (Name() is not { Length: > 0 } key)
        {
            return null;
        }

        SkipSpaces();
        if (!Take('='))
        {
            return null;
        }

        SkipSpaces();
        (string value, AoValueKind kind) = Value();
        return new AoEntry(key, value, kind, []);
    }

    /// <summary>The four spellings a value comes in. See <see cref="AoValueKind"/>.</summary>
    private (string Value, AoValueKind Kind) Value()
    {
        if (Done)
        {
            return (string.Empty, AoValueKind.Native);
        }

        return _text[_at] switch
        {
            '\'' => (Until('\''), AoValueKind.Payload),
            '"' => (Until('"'), AoValueKind.Quoted),
            '{' => (Script(), AoValueKind.Script),

            // THE REST OF THE LINE, VERBATIM, which is what the reference does - its pattern for a
            // value is (.*) to the line's end. So a trailing // comment lands inside an unquoted
            // value, and that is left alone rather than trimmed: a value is a number or a bool
            // here, and a rule that stripped a // would eventually strip one out of a path.
            //
            // OR THE CLOSING BRACE, WHICHEVER COMES FIRST, and this is where the two references
            // disagree. Taking the whole line makes "Foo { x = 1 }" unreadable - the value eats
            // the brace and the struct is never closed - while the diagram says in as many words
            // that the file should be read token-wise rather than by line. A brace cannot be part
            // of a number or a bool, so stopping at one costs nothing and settles the conflict in
            // favour of the reference that states a rule rather than the one that implies it.
            _ => (Native(), AoValueKind.Native),
        };
    }

    /// <summary>An unquoted value: to the end of the line, or to a closing brace before it.</summary>
    private string Native()
    {
        int from = _at;
        while (!Done && _text[_at] != '\n' && _text[_at] != '}')
        {
            _at++;
        }

        string said = _text[from.._at].TrimEnd('\r').Trim();

        // The newline is consumed, the brace is not - the brace belongs to whoever opened it.
        if (!Done && _text[_at] == '\n')
        {
            _at++;
        }

        return said;
    }

    /// <summary>
    /// A braced script, kept whole. Nested braces are counted; quoted ones are not.
    /// </summary>
    /// <remarks>
    /// THE QUOTES HAVE TO BE HONOURED while counting, because the whole point of these blocks is
    /// that they call things with file paths in them - <c>PlayEffect("…/foo.ao")</c> - and a brace
    /// inside such a string would otherwise close the block early and turn the remainder of the
    /// file into faults.
    /// </remarks>
    private string Script()
    {
        int from = _at;
        var depth = 0;

        while (!Done)
        {
            char c = _text[_at];

            if (c is '"' or '\'')
            {
                _at++;
                while (!Done && _text[_at] != c)
                {
                    _at++;
                }

                if (!Done)
                {
                    _at++;
                }

                continue;
            }

            _at++;

            if (c == '{')
            {
                depth++;
                continue;
            }

            if (c == '}' && --depth == 0)
            {
                return _text[from.._at];
            }
        }

        Fault("script block never closed", Quote(_text.AsSpan(from)));
        return _text[from..];
    }

    /// <summary>Hangs each entry off the last one indented less than it.</summary>
    /// <remarks>
    /// A TAB AND A SPACE COUNT THE SAME, one each. The game writes these files with a tool and
    /// indents them with tabs, and treating a tab as some number of columns would only matter for
    /// a file that mixed the two - at which point the nesting it intended is anybody's guess.
    /// </remarks>
    private static IReadOnlyList<AoEntry> Nest(List<(int Indent, AoEntry Entry)> flat)
    {
        if (flat.Count == 0)
        {
            return [];
        }

        var roots = new List<AoEntry>();

        // Each level's open entry, with the children collected for it so far. An entry is a record
        // and its Children list is fixed at construction, so the list is built first and the entry
        // rebuilt around it when the level closes.
        var open = new List<(int Indent, AoEntry Entry, List<AoEntry> Kids)>();

        void Close(int downTo)
        {
            while (open.Count > 0 && open[^1].Indent >= downTo)
            {
                (int _, AoEntry entry, List<AoEntry> kids) = open[^1];
                open.RemoveAt(open.Count - 1);

                AoEntry done = entry with { Children = kids };
                if (open.Count > 0)
                {
                    open[^1].Kids.Add(done);
                }
                else
                {
                    roots.Add(done);
                }
            }
        }

        foreach ((int indent, AoEntry entry) in flat)
        {
            Close(indent);
            open.Add((indent, entry, []));
        }

        Close(int.MinValue);
        return roots;
    }

    /// <summary>How far into its line the scanner is, which is the entry's indentation.</summary>
    private int Column()
    {
        int line = _text.LastIndexOf('\n', Math.Max(0, _at - 1));
        return _at - (line + 1);
    }

    /// <summary>A bare name: letters, digits and the punctuation these files use in keys.</summary>
    /// <remarks>
    /// WIDER THAN THE REFERENCE'S <c>[A-Za-z0-9]+</c> for struct names, because the same routine
    /// reads entry KEYS, and those are its <c>\S+</c>: <c>attached_slaved_animation_object</c> has
    /// underscores and an animation keyframe's key is a time like <c>0.35</c>. Stopping at the
    /// separators rather than listing what is allowed is what keeps a key nobody predicted whole.
    /// </remarks>
    private string? Name()
    {
        int from = _at;
        while (!Done && !char.IsWhiteSpace(_text[_at]) && _text[_at] is not ('{' or '}' or '='))
        {
            _at++;
        }

        return _at > from ? _text[from.._at] : null;
    }

    /// <summary>The text up to the closing mark, which is stepped over. Quotes are not kept.</summary>
    private string Until(char mark)
    {
        _at++;
        int from = _at;
        while (!Done && _text[_at] != mark)
        {
            _at++;
        }

        string said = _text[from..Math.Min(_at, _text.Length)];
        if (Done)
        {
            Fault($"no closing {mark}", Quote(said));
            return said;
        }

        _at++;
        return said;
    }

    /// <summary>
    /// Matches a bare word and steps over it, or leaves the position where the trivia ended.
    /// </summary>
    /// <remarks>
    /// THE TRIVIA SKIP IS NOT REWOUND, and the first version of this got that wrong. Saving the
    /// position before skipping and restoring it on a miss un-skips the comments too, so the next
    /// caller walks them again - and an unterminated comment, which faults as it is skipped,
    /// faulted once per caller. Three copies of one complaint about one comment. Skipping trivia
    /// is idempotent and a failed match consumes nothing, so there is nothing to restore.
    /// </remarks>
    private bool Word(string word)
    {
        SkipTrivia();

        if (_at + word.Length > _text.Length
            || !_text.AsSpan(_at, word.Length).SequenceEqual(word)
            || (_at + word.Length < _text.Length
                && !char.IsWhiteSpace(_text[_at + word.Length])
                && _text[_at + word.Length] != '{'))
        {
            return false;
        }

        _at += word.Length;
        return true;
    }

    private bool Take(char c)
    {
        if (Done || _text[_at] != c)
        {
            return false;
        }

        _at++;
        return true;
    }

    private void SkipSpaces()
    {
        while (!Done && (_text[_at] == ' ' || _text[_at] == '\t'))
        {
            _at++;
        }
    }

    /// <summary>Whitespace and both kinds of comment, in any order, in one pass.</summary>
    private void SkipTrivia()
    {
        while (!Done)
        {
            if (char.IsWhiteSpace(_text[_at]))
            {
                _at++;
                continue;
            }

            if (_text[_at] != '/' || _at + 1 >= _text.Length)
            {
                return;
            }

            if (_text[_at + 1] == '/')
            {
                while (!Done && _text[_at] != '\n')
                {
                    _at++;
                }

                continue;
            }

            if (_text[_at + 1] != '*')
            {
                return;
            }

            int close = _text.IndexOf("*/", _at + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                Fault("comment never closed", string.Empty);
                _at = _text.Length;
                return;
            }

            _at = close + 2;
        }
    }

    /// <summary>Consumes to the end of the line and hands back what was on it.</summary>
    private string TakeLine()
    {
        int from = _at;
        while (!Done && _text[_at] != '\n')
        {
            _at++;
        }

        string said = _text[from.._at].TrimEnd('\r');
        if (!Done)
        {
            _at++;
        }

        return said;
    }

    /// <summary>The line the scanner is on, without consuming it. For faults.</summary>
    private string Peek()
    {
        int end = _text.IndexOf('\n', _at);
        return Quote(_text.AsSpan(_at, (end < 0 ? _text.Length : end) - _at));
    }

    private static string Quote(ReadOnlySpan<char> said)
    {
        said = said.Trim();
        return said.Length <= FaultWidth ? said.ToString() : string.Concat(said[..FaultWidth], "…");
    }

    private void Fault(string why, string what)
    {
        if (_faults.Count >= MostFaults)
        {
            _extraFaults++;
            return;
        }

        var said = new StringBuilder(why.Length + what.Length + 8);
        said.Append(why);
        if (what.Length > 0)
        {
            said.Append(": ").Append(what);
        }

        _faults.Add(said.ToString());
    }
}
