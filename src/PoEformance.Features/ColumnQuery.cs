using System.Globalization;
using System.Text;

namespace PoEformance.Features;

/// <summary>What one part of a query asks.</summary>
public enum QueryKind
{
    /// <summary>A bare word, matched against whatever the table is searched by.</summary>
    Word,

    /// <summary>A named field holding a value: <c>tag:undead</c>.</summary>
    Value,

    /// <summary>A named field inside a range of numbers: <c>life&gt;120</c>, <c>life 120..260</c>.</summary>
    Number,

    /// <summary>Every child has to hold.</summary>
    All,

    /// <summary>Any child may hold.</summary>
    Any,

    /// <summary>The one child must not hold.</summary>
    Not,
}

/// <summary>One part of a query, and the parts under it.</summary>
public sealed record QueryTerm
{
    /// <summary>What this part asks.</summary>
    public QueryKind Kind { get; init; }

    /// <summary>Which field it asks about, on <see cref="QueryKind.Value"/> and Number.</summary>
    public string Field { get; init; } = string.Empty;

    /// <summary>The word it is looking for, on <see cref="QueryKind.Word"/> and Value.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>The bottom of the range, on <see cref="QueryKind.Number"/>.</summary>
    public double Least { get; init; }

    /// <summary>The top of it.</summary>
    public double Most { get; init; }

    /// <summary>
    /// Whether the bottom of the range is outside it - "greater than" rather than "at least".
    /// </summary>
    /// <remarks>
    /// SAID HERE RATHER THAN NUDGED INTO THE NUMBER, which is the trap this was written after
    /// falling into: "life &gt; 500" as <c>500 + double.Epsilon</c> is not greater than 500 at all,
    /// because Epsilon is the smallest DENORMAL and adding it to 500 gives back exactly 500. The
    /// comparison silently became "at least 500", the round-trip test agreed with itself, and only
    /// the rows holding exactly 500 could ever have told anybody.
    /// </remarks>
    public bool OpenLeast { get; init; }

    /// <summary>And whether the top of it is.</summary>
    public bool OpenMost { get; init; }

    /// <summary>The parts under this one, on All, Any and Not.</summary>
    public IReadOnlyList<QueryTerm> Children { get; init; } = [];
}

/// <summary>What came of reading a query: a tree, or where it went wrong.</summary>
/// <param name="Column">
/// Where the trouble is, counted from 1 - so that a caret can be drawn under the character rather
/// than the whole box turning red. The same reason <see cref="ExpressionResult"/> carries one.
/// </param>
public readonly record struct QueryResult(QueryTerm? Term, string Error, int Column)
{
    /// <summary>Whether it read.</summary>
    public bool Ok => Term is not null;

    /// <summary>A query that took everything, because there was nothing to read.</summary>
    public static QueryResult Everything { get; } = new(null, string.Empty, 0);

    internal static QueryResult Success(QueryTerm term) => new(term, string.Empty, 0);

    internal static QueryResult Failure(string error, int column) => new(null, error, column);
}

/// <summary>One value a field holds, and how many of the rows in play carry it.</summary>
public readonly record struct Facet(string Value, int Count);

/// <summary>What a query can ask of a table.</summary>
/// <remarks>
/// THREE QUESTIONS AND NO MORE, which is what keeps this engine from being about monsters: a word
/// somewhere in the row, a named field holding a value, a named field inside a range. Everything
/// the grammar offers is built out of those three and the three ways of joining them.
/// </remarks>
public interface IQuerySource
{
    /// <summary>How many rows there are.</summary>
    int Rows { get; }

    /// <summary>The rows whose searchable text holds this word.</summary>
    void Words(string word, RowSet into);

    /// <summary>The rows where this field holds this value, or false where there is no such field.</summary>
    bool Value(string field, string value, RowSet into);

    /// <summary>The rows where this field is in this range, or false where there is no such field.</summary>
    bool Number(string field, double least, double most, RowSet into);
}

/// <summary>
/// Reads and writes a table's filter as text.
/// </summary>
/// <remarks>
/// A HAND-WRITTEN PARSER, for the reason <see cref="RuleExpression"/> gives at length and which
/// applies here word for word: this project ships Native AOT, which has no runtime code generation,
/// so a library that compiles an expression tree cannot come along.
///
/// THE GRAMMAR IS AN ESCALATION AND NEVER A GATE. A bare word is a term, so typing "undead" does
/// exactly what the search box has always done - the fields and the comparisons are there for
/// whoever wants them, and nobody has to learn anything to keep using what worked. That is not a
/// nicety: a viewer that must be learned before it answers anything is a viewer that gets opened
/// once.
///
///     query      := or
///     or         := and ( ("or" | "||") and )*
///     and        := term ( ("and" | "&amp;&amp;")? term )*      -- juxtaposition means and
///     term       := ("not" | "!") term | "(" or ")" | predicate
///     predicate  := field ":" value                    -- tag:undead, skill:fire
///                 | field compare number               -- life &gt; 120
///                 | field number ".." number           -- life 120..260, what a drag writes
///                 | word                               -- free text, as it always was
///     compare    := "&gt;=" | "&lt;=" | "&gt;" | "&lt;" | "=" | "!="
///
/// IT ROUND-TRIPS: <c>Parse(Write(x))</c> gives back a tree that matches the same rows as x. Which
/// is what will let the facet rail and this box be two views of ONE filter rather than two stores
/// to keep in step - the same duality the rule editor's graph and text already have.
///
/// WHAT IT DELIBERATELY WILL NOT DO is arithmetic, for the same reason as there: an expression that
/// cannot be drawn in the rail is one that can be silently lost the moment somebody uses the rail.
/// </remarks>
public static class ColumnQuery
{
    /// <summary>Longest query accepted, so that a pasted file cannot become a long parse.</summary>
    public const int MaxLength = 400;

    /// <summary>How deep the brackets may go.</summary>
    private const int MostDepth = 16;

    /// <summary>
    /// A field name as a query names it: lower case, and no spaces.
    /// </summary>
    /// <remarks>
    /// A SPACE SEPARATES TWO TERMS AND ALWAYS WILL, so a column headed "atk spd" cannot be named in
    /// a query the way its header spells it. Rather than rename the columns to suit the parser -
    /// the header is what somebody reads, the query is what they type - both ends normalise here,
    /// and "atkspd", "Atk Spd" and "ATKSPD" all reach the same column.
    /// </remarks>
    public static string Field(string? name)
    {
        if (name is not { Length: > 0 })
        {
            return string.Empty;
        }

        Span<char> made = name.Length <= 64 ? stackalloc char[name.Length] : new char[name.Length];
        var at = 0;

        foreach (char one in name)
        {
            if (!char.IsWhiteSpace(one))
            {
                made[at++] = char.ToLowerInvariant(one);
            }
        }

        return new string(made[..at]);
    }

    /// <summary>
    /// The query with this field and value added, or taken back out if it was already there.
    /// </summary>
    /// <remarks>
    /// WHAT A CLICK IN THE FACET RAIL DOES, and the reason the rail and the box are one filter
    /// rather than two: the click does not keep a list of its own anywhere, it edits the text.
    /// Whatever it produces, somebody can read, change by hand, and clear by emptying the box.
    ///
    /// ONLY THE TOP LEVEL IS TOUCHED. A query somebody has written brackets and an "or" into means
    /// something particular, and a click that reached inside it to add a term would change what
    /// they wrote rather than adding to it. So a click adds one more thing that must ALSO hold -
    /// "(tag:undead or tag:beast)" and a click on caster gives "(tag:undead or tag:beast)
    /// tag:caster" - and only ever removes a term that is sitting at the top by itself.
    /// </remarks>
    public static string Toggle(string? query, string field, string value)
    {
        List<QueryTerm>? terms = Top(query);
        if (terms is null)
        {
            return query ?? string.Empty;
        }

        string key = Field(field);
        int at = terms.FindIndex(one => Says(one, key, value));

        if (at >= 0)
        {
            terms.RemoveAt(at);
        }
        else
        {
            terms.Add(new QueryTerm { Kind = QueryKind.Value, Field = key, Text = value });
        }

        return Rebuild(terms);
    }

    /// <summary>The query with this field held to a range, replacing whatever range it had.</summary>
    /// <remarks>What a drag across a column's histogram does. See <see cref="Toggle"/>.</remarks>
    public static string Ranged(string? query, string field, double least, double most)
    {
        List<QueryTerm>? terms = Top(query);
        if (terms is null)
        {
            return query ?? string.Empty;
        }

        string key = Field(field);
        terms.RemoveAll(one => one.Kind == QueryKind.Number && string.Equals(Field(one.Field), key, StringComparison.Ordinal));

        terms.Add(new QueryTerm
        {
            Kind = QueryKind.Number,
            Field = key,
            Least = Math.Min(least, most),
            Most = Math.Max(least, most),
        });

        return Rebuild(terms);
    }

    /// <summary>The query with every top-level term naming this field taken out.</summary>
    public static string Drop(string? query, string field)
    {
        List<QueryTerm>? terms = Top(query);
        if (terms is null)
        {
            return query ?? string.Empty;
        }

        string key = Field(field);
        terms.RemoveAll(one => one.Kind is QueryKind.Value or QueryKind.Number
            && string.Equals(Field(one.Field), key, StringComparison.Ordinal));

        return Rebuild(terms);
    }

    /// <summary>Whether a term sitting at the top of the query says exactly this.</summary>
    public static bool Holds(QueryTerm? term, string field, string value)
    {
        string key = Field(field);
        foreach (QueryTerm one in Conjuncts(term))
        {
            if (Says(one, key, value))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The range a top-level term puts on this field, where one does.</summary>
    public static (double Least, double Most)? RangeOf(QueryTerm? term, string field)
    {
        string key = Field(field);
        foreach (QueryTerm one in Conjuncts(term))
        {
            if (one.Kind == QueryKind.Number && string.Equals(Field(one.Field), key, StringComparison.Ordinal))
            {
                return (one.Least, one.Most);
            }
        }

        return null;
    }

    /// <summary>Turns text into a query, or says where it stopped making sense.</summary>
    public static QueryResult Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return QueryResult.Everything;
        }

        if (text.Length > MaxLength)
        {
            return QueryResult.Failure($"The query is longer than {MaxLength} characters.", MaxLength);
        }

        var parser = new Parser(text);
        return parser.Run();
    }

    /// <summary>Writes a query back out as text.</summary>
    public static string Write(QueryTerm? term)
    {
        if (term is null)
        {
            return string.Empty;
        }

        var text = new StringBuilder(64);
        Write(term, text, false);
        return text.ToString();
    }

    /// <summary>
    /// Fills <paramref name="into"/> with the rows the query matches.
    /// </summary>
    /// <remarks>
    /// A NULL QUERY TAKES EVERYTHING, which is the empty box and the commonest case by far.
    /// </remarks>
    /// <returns>False where the query names a field the table has not got, which says which.</returns>
    public static bool Run(QueryTerm? term, IQuerySource source, RowSet into, out string error)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(into);

        error = string.Empty;

        if (term is null)
        {
            into.All();
            return true;
        }

        return Walk(term, source, into, ref error);
    }

    private static bool Walk(QueryTerm term, IQuerySource source, RowSet into, ref string error)
    {
        switch (term.Kind)
        {
            case QueryKind.Word:
                into.None();
                source.Words(term.Text, into);
                return true;

            case QueryKind.Value:
                into.None();
                if (!source.Value(term.Field, term.Text, into))
                {
                    error = $"There is no column called '{term.Field}'.";
                    return false;
                }

                return true;

            case QueryKind.Number:
                into.None();

                // AN OPEN END BECOMES THE NEXT NUMBER THERE IS, which is what BitIncrement is for:
                // the source is asked for a closed range, and the smallest step that certainly
                // excludes the bound is one representable double rather than any chosen epsilon.
                if (!source.Number(
                        term.Field,
                        term.OpenLeast ? Math.BitIncrement(term.Least) : term.Least,
                        term.OpenMost ? Math.BitDecrement(term.Most) : term.Most,
                        into))
                {
                    error = $"There is no column of numbers called '{term.Field}'.";
                    return false;
                }

                return true;

            case QueryKind.Not:
                if (!Walk(term.Children[0], source, into, ref error))
                {
                    return false;
                }

                into.Not();
                return true;

            default:
                return Joined(term, source, into, ref error);
        }
    }

    private static bool Joined(QueryTerm term, IQuerySource source, RowSet into, ref string error)
    {
        if (term.Children.Count == 0)
        {
            into.All();
            return true;
        }

        if (!Walk(term.Children[0], source, into, ref error))
        {
            return false;
        }

        // ONE SCRATCH SET FOR THE WHOLE RUN OF CHILDREN rather than one each: a query of six terms
        // is six sets otherwise, made and thrown away on every keystroke.
        RowSet? scratch = null;

        for (var at = 1; at < term.Children.Count; at++)
        {
            scratch ??= new RowSet(source.Rows);

            if (!Walk(term.Children[at], source, scratch, ref error))
            {
                return false;
            }

            if (term.Kind == QueryKind.Any)
            {
                into.Or(scratch);
            }
            else
            {
                into.And(scratch);
            }
        }

        return true;
    }

    /// <summary>
    /// The query's top-level terms, ready to be edited, or null where it cannot be read.
    /// </summary>
    /// <remarks>
    /// NULL FOR A QUERY THAT DOES NOT PARSE, and every caller hands the text straight back when it
    /// gets one. Somebody halfway through typing has a broken query on screen most of the time, and
    /// a facet click that silently rewrote it into something that parses would throw away what they
    /// were in the middle of writing.
    /// </remarks>
    private static List<QueryTerm>? Top(string? query)
    {
        QueryResult parsed = Parse(query);
        return !parsed.Ok && parsed.Error.Length > 0 ? null : Conjuncts(parsed.Term);
    }

    private static List<QueryTerm> Conjuncts(QueryTerm? term) => term switch
    {
        null => [],
        { Kind: QueryKind.All } => [.. term.Children],
        _ => [term],
    };

    private static string Rebuild(List<QueryTerm> terms) => terms.Count switch
    {
        0 => string.Empty,
        1 => Write(terms[0]),
        _ => Write(new QueryTerm { Kind = QueryKind.All, Children = terms }),
    };

    private static bool Says(QueryTerm term, string field, string value)
        => term.Kind == QueryKind.Value
            && string.Equals(Field(term.Field), field, StringComparison.Ordinal)
            && string.Equals(term.Text, value, StringComparison.OrdinalIgnoreCase);

    private static void Write(QueryTerm term, StringBuilder text, bool inside)
    {
        switch (term.Kind)
        {
            case QueryKind.Word:
                text.Append(term.Text);
                return;

            case QueryKind.Value:
                text.Append(term.Field).Append(':').Append(term.Text);
                return;

            case QueryKind.Number:
                Number(term, text);
                return;

            case QueryKind.Not:
                text.Append("not ");
                Write(term.Children[0], text, true);
                return;

            default:
                Join(term, text, inside);
                return;
        }
    }

    private static void Join(QueryTerm term, StringBuilder text, bool inside)
    {
        // BRACKETS ONLY WHERE THEY CHANGE THE READING, which is what makes this worth round-tripping
        // at all: a writer that brackets everything turns a query somebody typed by hand into one
        // they cannot recognise as theirs the next time the rail writes it back.
        bool brackets = inside && term.Children.Count > 1;

        if (brackets)
        {
            text.Append('(');
        }

        for (var at = 0; at < term.Children.Count; at++)
        {
            if (at > 0)
            {
                text.Append(term.Kind == QueryKind.Any ? " or " : " ");
            }

            Write(term.Children[at], text, true);
        }

        if (brackets)
        {
            text.Append(')');
        }
    }

    private static void Number(QueryTerm term, StringBuilder text)
    {
        text.Append(term.Field);

        bool below = double.IsNegativeInfinity(term.Least);
        bool above = double.IsPositiveInfinity(term.Most);

        if (below && above)
        {
            // A range that takes everything is the field on its own, which parses back as a word -
            // so it is written as the one comparison that cannot be mistaken for one.
            text.Append(">=").Append(Figure(double.MinValue));
            return;
        }

        if (below)
        {
            text.Append(term.OpenMost ? "<" : "<=").Append(Figure(term.Most));
            return;
        }

        if (above)
        {
            text.Append(term.OpenLeast ? ">" : ">=").Append(Figure(term.Least));
            return;
        }

        if (term.Least.Equals(term.Most))
        {
            text.Append('=').Append(Figure(term.Least));
            return;
        }

        text.Append(' ').Append(Figure(term.Least)).Append("..").Append(Figure(term.Most));
    }

    private static string Figure(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private sealed class Parser(string text)
    {
        private readonly string _text = text;
        private int _at;

        public QueryResult Run()
        {
            QueryResult result = Or(0);
            if (!result.Ok)
            {
                return result;
            }

            Space();
            return _at < _text.Length
                ? QueryResult.Failure($"Unexpected '{_text[_at]}'.", _at + 1)
                : result;
        }

        private QueryResult Or(int depth)
        {
            QueryResult left = And(depth);
            if (!left.Ok)
            {
                return left;
            }

            List<QueryTerm>? joined = null;
            while (Word("||", "or"))
            {
                QueryResult right = And(depth);
                if (!right.Ok)
                {
                    return right;
                }

                joined ??= [left.Term!];
                joined.Add(right.Term!);
            }

            return joined is null
                ? left
                : QueryResult.Success(new QueryTerm { Kind = QueryKind.Any, Children = joined });
        }

        private QueryResult And(int depth)
        {
            QueryResult left = Unary(depth);
            if (!left.Ok)
            {
                return left;
            }

            List<QueryTerm>? joined = null;

            while (true)
            {
                // "and" IS OPTIONAL, which is the point: two terms side by side mean both, so
                // "undead boss" reads the way somebody types it into a search box.
                Word("&&", "and");

                Space();
                if (_at >= _text.Length || _text[_at] == ')' || Ahead("or") || Ahead("||"))
                {
                    break;
                }

                QueryResult right = Unary(depth);
                if (!right.Ok)
                {
                    return right;
                }

                joined ??= [left.Term!];
                joined.Add(right.Term!);
            }

            return joined is null
                ? left
                : QueryResult.Success(new QueryTerm { Kind = QueryKind.All, Children = joined });
        }

        private QueryResult Unary(int depth)
        {
            Space();

            if (Word("!", "not"))
            {
                QueryResult inner = Unary(depth);
                return inner.Ok
                    ? QueryResult.Success(new QueryTerm { Kind = QueryKind.Not, Children = [inner.Term!] })
                    : inner;
            }

            if (_at < _text.Length && _text[_at] == '(')
            {
                if (depth >= MostDepth)
                {
                    return QueryResult.Failure("The brackets are nested too deeply.", _at + 1);
                }

                _at++;
                QueryResult inner = Or(depth + 1);
                if (!inner.Ok)
                {
                    return inner;
                }

                Space();
                if (_at >= _text.Length || _text[_at] != ')')
                {
                    return QueryResult.Failure("Expected ')'.", _at + 1);
                }

                _at++;
                return inner;
            }

            return Predicate();
        }

        private QueryResult Predicate()
        {
            Space();
            int start = _at;
            string word = Name();

            if (word.Length == 0)
            {
                return QueryResult.Failure(
                    _at < _text.Length ? $"Unexpected '{_text[_at]}'." : "The query ends early.",
                    _at + 1);
            }

            if (_at < _text.Length && _text[_at] == ':')
            {
                _at++;
                string value = Name();
                return value.Length == 0
                    ? QueryResult.Failure($"'{word}:' has nothing after it.", _at + 1)
                    : QueryResult.Success(
                        new QueryTerm { Kind = QueryKind.Value, Field = word, Text = value });
            }

            if (Compare() is { Length: > 0 } compare)
            {
                if (!Figure(out double value))
                {
                    return QueryResult.Failure($"'{word}{compare}' needs a number after it.", _at + 1);
                }

                return QueryResult.Success(Between(word, compare, value));
            }

            // A NUMBER STRAIGHT AFTER A NAME IS A RANGE, which is the form a drag across a histogram
            // writes: "life 120..260". Anything else is two words, and two words are two terms.
            int back = _at;
            Space();
            if (Figure(out double least) && Ahead("..") && Skip("..") && Figure(out double most))
            {
                return QueryResult.Success(new QueryTerm
                {
                    Kind = QueryKind.Number,
                    Field = word,
                    Least = Math.Min(least, most),
                    Most = Math.Max(least, most),
                });
            }

            _at = back;
            _ = start;
            return QueryResult.Success(new QueryTerm { Kind = QueryKind.Word, Text = word });
        }

        private static QueryTerm Between(string field, string compare, double value) => compare switch
        {
            ">" => Range(field, value, double.PositiveInfinity) with { OpenLeast = true },
            ">=" => Range(field, value, double.PositiveInfinity),
            "<" => Range(field, double.NegativeInfinity, value) with { OpenMost = true },
            "<=" => Range(field, double.NegativeInfinity, value),
            "!=" => new QueryTerm
            {
                Kind = QueryKind.Not,
                Children = [Range(field, value, value)],
            },
            _ => Range(field, value, value),
        };

        private static QueryTerm Range(string field, double least, double most)
            => new() { Kind = QueryKind.Number, Field = field, Least = least, Most = most };

        private string Name()
        {
            Space();
            int start = _at;

            while (_at < _text.Length && Part(_text[_at]))
            {
                _at++;
            }

            return _text[start.._at];
        }

        /// <summary>
        /// What may be part of a word.
        /// </summary>
        /// <remarks>
        /// UNDERSCORES AND SLASHES BELONG TO WORDS, because the things somebody searches for in this
        /// table are written with them: monster_base_block_%, Metadata/Monsters/Zombie. The percent
        /// sign too - 207 of the stat ids carry one.
        ///
        /// AND THE HASH, which is not decoration: a modifier row that resolves to nothing is indexed
        /// under its own row number as "#4211", and a click on it in the facet rail writes
        /// "mod:#4211" into the box. Without this, the rail could offer a value the parser could not
        /// read back - which is the one way these two views of the filter could disagree.
        /// </remarks>
        private static bool Part(char one)
            => char.IsLetterOrDigit(one) || one is '_' or '/' or '-' or '\'' or '%' or '+' or '#';

        private string Compare()
        {
            Space();

            foreach (string one in (string[])[">=", "<=", "!=", ">", "<", "="])
            {
                if (Ahead(one))
                {
                    _at += one.Length;
                    return one;
                }
            }

            return string.Empty;
        }

        private bool Figure(out double value)
        {
            Space();
            int start = _at;

            if (_at < _text.Length && (_text[_at] == '-' || _text[_at] == '+'))
            {
                _at++;
            }

            while (_at < _text.Length && (char.IsDigit(_text[_at]) || _text[_at] == '.'))
            {
                // A SECOND DOT IS THE RANGE OPERATOR AND NOT PART OF THE NUMBER: in "120..260" the
                // number ends at the first of the two dots.
                if (_text[_at] == '.' && _at + 1 < _text.Length && _text[_at + 1] == '.')
                {
                    break;
                }

                _at++;
            }

            if (_at > start
                && double.TryParse(_text[start.._at], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }

            _at = start;
            value = 0d;
            return false;
        }

        private bool Word(string symbol, string word)
        {
            Space();

            if (Ahead(symbol))
            {
                _at += symbol.Length;
                return true;
            }

            if (!Ahead(word))
            {
                return false;
            }

            // "notorious" IS NOT "not": a word only counts as a keyword when a word ends there.
            int after = _at + word.Length;
            if (after < _text.Length && Part(_text[after]))
            {
                return false;
            }

            _at = after;
            return true;
        }

        private bool Ahead(string what)
            => _at + what.Length <= _text.Length
                && string.Compare(_text, _at, what, 0, what.Length, StringComparison.OrdinalIgnoreCase) == 0;

        private bool Skip(string what)
        {
            _at += what.Length;
            return true;
        }

        private void Space()
        {
            while (_at < _text.Length && char.IsWhiteSpace(_text[_at]))
            {
                _at++;
            }
        }
    }
}
