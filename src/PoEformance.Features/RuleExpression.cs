using System.Globalization;
using System.Text;

namespace PoEformance.Features;

/// <summary>What came of reading an expression: a tree, or where it went wrong.</summary>
/// <param name="Column">
/// Where the trouble is, counted from 1. The point of returning this rather than throwing: the
/// config page can put a caret under the character, and "expected ')'" fifteen characters into
/// a long condition is otherwise a puzzle.
/// </param>
public readonly record struct ExpressionResult(RuleCondition? Condition, string Error, int Column)
{
    public bool Ok => Condition is not null;

    public static ExpressionResult Success(RuleCondition condition) => new(condition, string.Empty, 0);

    public static ExpressionResult Failure(string error, int column) => new(null, error, column);
}

/// <summary>
/// Reads and writes a rule's condition as text.
/// </summary>
/// <remarks>
/// A HAND-WRITTEN parser, and that is the point rather than an indulgence. The reference plugin
/// compiles its conditions with System.Linq.Dynamic.Core, which builds an expression tree and
/// calls Compile() on it - runtime code generation, which Native AOT does not have. This
/// project ships AOT (see docs/architecture.md, "Deployment"), so that library cannot come
/// along; and the alternative it would force - falling back to an interpreter only under AOT -
/// means the shipped build runs different code from the one tested.
///
/// The grammar it accepts is a subset of what the reference plugin took, chosen so that
/// conditions written for that plugin mostly paste in unchanged:
///
///     expression  := or
///     or          := and ( ("||" | "or") and )*
///     and         := unary ( ("&amp;&amp;" | "and") unary )*
///     unary       := ("!" | "not") unary | primary
///     primary     := "(" or ")"
///                  | "exactlyOne" "(" or ("," or)+ ")"
///                  | fact [ compare number ]
///     fact        := Name [ "(" (string | number) ")" ]
///     compare     := "&gt;=" | "&lt;=" | "&gt;" | "&lt;" | "==" | "!=" | "="
///
/// What it deliberately does NOT accept is arithmetic. `HealthPercent * 2 &lt; Mana` parses in
/// the reference plugin and there is no way to draw it in a node graph, so a rule written that
/// way could be opened in the editor and silently lost. The two forms describe the same set of
/// conditions here, in both directions.
/// </remarks>
public static class RuleExpression
{
    /// <summary>The name of the exactly-one form, which has no operator spelling.</summary>
    public const string ExactlyOneName = "exactlyOne";

    /// <summary>Longest expression accepted, so a pasted file cannot become a long parse.</summary>
    public const int MaxLength = 8000;

    /// <summary>Turns text into a condition tree.</summary>
    public static ExpressionResult Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ExpressionResult.Failure("The condition is empty.", 1);
        }

        if (text.Length > MaxLength)
        {
            return ExpressionResult.Failure($"The condition is longer than {MaxLength} characters.", MaxLength);
        }

        var parser = new Parser(text);
        return parser.Run();
    }

    /// <summary>Writes a condition tree back out as text.</summary>
    /// <remarks>
    /// Round-trips: <c>Parse(Write(x))</c> gives back a tree that evaluates as <c>x</c> does.
    /// Which is what makes the graph editor and the text box two views of one thing rather
    /// than two stores that have to be kept in step.
    /// </remarks>
    public static string Write(RuleCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var text = new StringBuilder();
        Write(condition, text, Precedence.Top);
        return text.ToString();
    }

    private enum Precedence
    {
        Top,
        Or,
        And,
        Unary,
    }

    private static void Write(RuleCondition condition, StringBuilder text, Precedence outer)
    {
        if (condition.Negate)
        {
            // Always as `!(...)`, never folded into the comparison, and this is not tidiness.
            // `!(NearestRare <= 45)` and `NearestRare > 45` differ exactly when the distance is
            // unknown: the first is TRUE with no rare in the area, the second is false, because
            // an absent number satisfies no comparison. Rewriting one as the other would change
            // what a saved rule does.
            text.Append('!');
            Write(condition with { Negate = false }, text, Precedence.Unary);
            return;
        }

        switch (condition.Kind)
        {
            case ConditionKind.Fact:
                WriteFact(condition, text, outer);
                return;

            case ConditionKind.ExactlyOne:
                text.Append(ExactlyOneName).Append('(');
                WriteList(condition.Children, text, ", ");
                text.Append(')');
                return;

            case ConditionKind.All:
            case ConditionKind.Any:
                WriteGroup(condition, text, outer);
                return;

            default:
                text.Append("false");
                return;
        }
    }

    private static void WriteGroup(RuleCondition condition, StringBuilder text, Precedence outer)
    {
        if (condition.Children.Count == 0)
        {
            // Nothing joined is a rule half-written, and it fires nothing. `false` says so in
            // the one word the parser reads back as the same thing.
            text.Append("false");
            return;
        }

        if (condition.Children.Count == 1)
        {
            Write(condition.Children[0], text, outer);
            return;
        }

        bool all = condition.Kind == ConditionKind.All;
        Precedence own = all ? Precedence.And : Precedence.Or;
        bool brackets = own < outer;

        if (brackets)
        {
            text.Append('(');
        }

        WriteList(condition.Children, text, all ? " && " : " || ", own);

        if (brackets)
        {
            text.Append(')');
        }
    }

    private static void WriteList(
        IReadOnlyList<RuleCondition> children,
        StringBuilder text,
        string separator,
        Precedence inner = Precedence.Top)
    {
        for (int index = 0; index < children.Count; index++)
        {
            if (index > 0)
            {
                text.Append(separator);
            }

            Write(children[index], text, inner);
        }
    }

    private static void WriteFact(RuleCondition leaf, StringBuilder text, Precedence outer)
    {
        FactInfo info = RuleFacts.Describe(leaf.Fact);
        bool compared = info.Shape == FactShape.Number;

        // A comparison is two things joined, so it needs brackets exactly where a group would.
        bool brackets = compared && outer >= Precedence.Unary;
        if (brackets)
        {
            text.Append('(');
        }

        text.Append(info.Name);

        if (info.Argument != FactArgument.None)
        {
            text.Append('(');
            if (info.Argument == FactArgument.Text)
            {
                text.Append('"').Append(Escape(leaf.Text)).Append('"');
            }
            else
            {
                text.Append(Number(leaf.Argument));
            }

            text.Append(')');
        }

        if (compared)
        {
            text.Append(' ').Append(Symbol(leaf.Compare)).Append(' ').Append(Number(leaf.Value));
        }

        if (brackets)
        {
            text.Append(')');
        }
    }

    private static string Symbol(Compare compare) => compare switch
    {
        Compare.AtLeast => ">=",
        Compare.AtMost => "<=",
        Compare.Above => ">",
        Compare.Below => "<",
        Compare.Is => "==",
        Compare.IsNot => "!=",
        _ => ">=",
    };

    /// <summary>Invariant culture, always - the settings file must read the same in every locale.</summary>
    private static string Number(double value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Escape(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal);

    private sealed class Parser(string text)
    {
        private readonly string _text = text;
        private int _at;

        public ExpressionResult Run()
        {
            ExpressionResult result = ParseOr(0);
            if (!result.Ok)
            {
                return result;
            }

            SkipSpace();
            return _at < _text.Length
                ? ExpressionResult.Failure($"Unexpected '{_text[_at]}'.", _at + 1)
                : result;
        }

        private ExpressionResult ParseOr(int depth)
        {
            ExpressionResult left = ParseAnd(depth);
            if (!left.Ok)
            {
                return left;
            }

            List<RuleCondition>? joined = null;
            while (TakeWord("||", "or"))
            {
                ExpressionResult right = ParseAnd(depth);
                if (!right.Ok)
                {
                    return right;
                }

                joined ??= [left.Condition!];
                joined.Add(right.Condition!);
            }

            return joined is null
                ? left
                : ExpressionResult.Success(new RuleCondition { Kind = ConditionKind.Any, Children = joined });
        }

        private ExpressionResult ParseAnd(int depth)
        {
            ExpressionResult left = ParseUnary(depth);
            if (!left.Ok)
            {
                return left;
            }

            List<RuleCondition>? joined = null;
            while (TakeWord("&&", "and"))
            {
                ExpressionResult right = ParseUnary(depth);
                if (!right.Ok)
                {
                    return right;
                }

                joined ??= [left.Condition!];
                joined.Add(right.Condition!);
            }

            return joined is null
                ? left
                : ExpressionResult.Success(new RuleCondition { Kind = ConditionKind.All, Children = joined });
        }

        private ExpressionResult ParseUnary(int depth)
        {
            if (depth > RuleCondition.MaxDepth)
            {
                return ExpressionResult.Failure("The condition nests too deeply.", _at + 1);
            }

            SkipSpace();
            if (TakeWord("!", "not"))
            {
                ExpressionResult inner = ParseUnary(depth + 1);
                if (!inner.Ok)
                {
                    return inner;
                }

                RuleCondition condition = inner.Condition!;

                // Two negations are not a no-op to write down, but they are one to evaluate,
                // so they collapse here rather than growing a wrapper per `!`.
                return ExpressionResult.Success(condition with { Negate = !condition.Negate });
            }

            return ParsePrimary(depth);
        }

        private ExpressionResult ParsePrimary(int depth)
        {
            SkipSpace();
            if (_at >= _text.Length)
            {
                return ExpressionResult.Failure("The condition ends early.", _at + 1);
            }

            if (_text[_at] == '(')
            {
                _at++;
                ExpressionResult inner = ParseOr(depth + 1);
                if (!inner.Ok)
                {
                    return inner;
                }

                return Expect(')') ? inner : ExpressionResult.Failure("Expected ')'.", _at + 1);
            }

            int start = _at;
            string word = TakeName();
            if (word.Length == 0)
            {
                return ExpressionResult.Failure($"Expected a condition, found '{_text[_at]}'.", _at + 1);
            }

            if (word.Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                // What an empty group writes itself as, so a round trip does not lose one.
                return ExpressionResult.Success(new RuleCondition { Kind = ConditionKind.All });
            }

            if (word.Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                // Not offered by the editor, but accepted: it is what somebody writes to park
                // a rule while testing an effect, and refusing it is a puzzle rather than help.
                return ExpressionResult.Success(new RuleCondition { Kind = ConditionKind.All, Negate = true });
            }

            if (word.Equals(ExactlyOneName, StringComparison.OrdinalIgnoreCase))
            {
                return ParseExactlyOne(depth);
            }

            FactInfo? found = RuleFacts.Find(word);
            if (found is not FactInfo info)
            {
                return ExpressionResult.Failure($"There is no condition called '{word}'.", start + 1);
            }

            return ParseFact(info);
        }

        private ExpressionResult ParseExactlyOne(int depth)
        {
            SkipSpace();
            if (!Expect('('))
            {
                return ExpressionResult.Failure($"Expected '(' after {ExactlyOneName}.", _at + 1);
            }

            var children = new List<RuleCondition>();
            while (true)
            {
                ExpressionResult child = ParseOr(depth + 1);
                if (!child.Ok)
                {
                    return child;
                }

                children.Add(child.Condition!);
                SkipSpace();
                if (!Expect(','))
                {
                    break;
                }
            }

            if (!Expect(')'))
            {
                return ExpressionResult.Failure("Expected ')'.", _at + 1);
            }

            return children.Count < 2
                ? ExpressionResult.Failure($"{ExactlyOneName} needs at least two conditions.", _at)
                : ExpressionResult.Success(new RuleCondition
                {
                    Kind = ConditionKind.ExactlyOne,
                    Children = children,
                });
        }

        private ExpressionResult ParseFact(FactInfo info)
        {
            var leaf = new RuleCondition { Fact = info.Fact };

            SkipSpace();
            bool opened = _at < _text.Length && _text[_at] == '(';

            if (info.Argument == FactArgument.None)
            {
                if (opened)
                {
                    return ExpressionResult.Failure($"{info.Name} takes no value.", _at + 1);
                }
            }
            else
            {
                if (!opened)
                {
                    // Names what is wanted rather than that something is. "HasBuff needs a
                    // value in brackets" leaves somebody guessing whether the buff name is
                    // quoted, which is the one thing they got wrong.
                    return ExpressionResult.Failure(
                        info.Argument == FactArgument.Text
                            ? $"{info.Name} needs a name in quotes, as {info.Name}(\"frozen\")."
                            : $"{info.Name} needs a number in brackets, as {info.Name}(2).",
                        _at + 1);
                }

                _at++;
                ExpressionResult argument = ParseArgument(info, ref leaf);
                if (!argument.Ok)
                {
                    return argument;
                }

                if (!Expect(')'))
                {
                    return ExpressionResult.Failure("Expected ')'.", _at + 1);
                }
            }

            SkipSpace();
            Compare? compare = TakeCompare();

            if (info.Shape == FactShape.Flag)
            {
                return compare is null
                    ? ExpressionResult.Success(leaf)
                    : ExpressionResult.Failure($"{info.Name} answers yes or no, so it cannot be compared.", _at);
            }

            if (compare is not Compare op)
            {
                return ExpressionResult.Failure(
                    $"{info.Name} is a number, so it needs a comparison such as '<= 50'.", _at + 1);
            }

            SkipSpace();
            if (!TakeNumber(out double value))
            {
                return ExpressionResult.Failure("Expected a number.", _at + 1);
            }

            return ExpressionResult.Success(leaf with { Compare = op, Value = value });
        }

        private ExpressionResult ParseArgument(FactInfo info, ref RuleCondition leaf)
        {
            SkipSpace();

            if (info.Argument == FactArgument.Text)
            {
                if (!TakeString(out string value))
                {
                    return ExpressionResult.Failure($"{info.Name} needs a name in quotes.", _at + 1);
                }

                leaf = leaf with { Text = value };
                return ExpressionResult.Success(leaf);
            }

            if (!TakeNumber(out double number))
            {
                return ExpressionResult.Failure($"{info.Name} needs a number.", _at + 1);
            }

            leaf = leaf with { Argument = number };
            return ExpressionResult.Success(leaf);
        }

        private void SkipSpace()
        {
            while (_at < _text.Length && char.IsWhiteSpace(_text[_at]))
            {
                _at++;
            }
        }

        private bool Expect(char expected)
        {
            SkipSpace();
            if (_at < _text.Length && _text[_at] == expected)
            {
                _at++;
                return true;
            }

            return false;
        }

        /// <summary>Takes either spelling of an operator, symbols or word.</summary>
        private bool TakeWord(string symbol, string word)
        {
            SkipSpace();
            if (_at + symbol.Length <= _text.Length
                && string.CompareOrdinal(_text, _at, symbol, 0, symbol.Length) == 0)
            {
                _at += symbol.Length;
                return true;
            }

            int end = _at + word.Length;
            if (end <= _text.Length
                && string.Compare(_text, _at, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0

                // `or` must not match the start of a fact name. Without this, `orbCount` would
                // read as an `or` followed by a condition called `bCount`.
                && (end == _text.Length || !IsNameCharacter(_text[end])))
            {
                _at = end;
                return true;
            }

            return false;
        }

        private string TakeName()
        {
            int start = _at;
            while (_at < _text.Length && IsNameCharacter(_text[_at]))
            {
                _at++;
            }

            return _text[start.._at];
        }

        private Compare? TakeCompare()
        {
            SkipSpace();
            if (_at >= _text.Length)
            {
                return null;
            }

            char first = _text[_at];
            char second = _at + 1 < _text.Length ? _text[_at + 1] : '\0';

            switch (first)
            {
                case '>':
                    _at += second == '=' ? 2 : 1;
                    return second == '=' ? Compare.AtLeast : Compare.Above;
                case '<':
                    _at += second == '=' ? 2 : 1;
                    return second == '=' ? Compare.AtMost : Compare.Below;
                case '=':
                    // `=` as well as `==`, because that is what gets typed and refusing it
                    // teaches nothing. Everything written back out uses `==`.
                    _at += second == '=' ? 2 : 1;
                    return Compare.Is;
                case '!' when second == '=':
                    _at += 2;
                    return Compare.IsNot;
                default:
                    return null;
            }
        }

        private bool TakeNumber(out double value)
        {
            SkipSpace();
            int start = _at;
            if (_at < _text.Length && (_text[_at] == '-' || _text[_at] == '+'))
            {
                _at++;
            }

            while (_at < _text.Length && (char.IsAsciiDigit(_text[_at]) || _text[_at] == '.'))
            {
                _at++;
            }

            // Invariant culture, so a settings file written on a German machine reads the same
            // on an English one. A comma is a separator here, never a decimal point.
            return double.TryParse(
                _text[start.._at], NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private bool TakeString(out string value)
        {
            value = string.Empty;
            SkipSpace();
            if (_at >= _text.Length || _text[_at] != '"')
            {
                return false;
            }

            _at++;
            var built = new StringBuilder();
            while (_at < _text.Length)
            {
                char current = _text[_at++];
                if (current == '"')
                {
                    value = built.ToString();
                    return true;
                }

                if (current == '\\' && _at < _text.Length)
                {
                    built.Append(_text[_at++]);
                    continue;
                }

                built.Append(current);
            }

            return false;
        }

        private static bool IsNameCharacter(char character)
            => char.IsAsciiLetterOrDigit(character) || character == '_';
    }
}
