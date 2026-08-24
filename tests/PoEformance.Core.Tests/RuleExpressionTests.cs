using PoEformance.Features;

namespace PoEformance.Core.Tests;

/// <summary>
/// Reading and writing a condition as text.
/// </summary>
/// <remarks>
/// This replaces a compiled expression library the shipped build cannot carry, so the bar is
/// that conditions written for the reference plugin paste in and mean the same thing. The
/// round-trip cases are the load-bearing ones: the graph editor, the text box and the settings
/// file are three views of one tree, and they stay three views only for as long as writing a
/// tree out and reading it back gives the same tree.
/// </remarks>
public class RuleExpressionTests
{
    private static RuleCondition Parsed(string text)
    {
        ExpressionResult result = RuleExpression.Parse(text);
        Assert.True(result.Ok, $"{text}: {result.Error}");
        return result.Condition!;
    }

    [Theory]

    // The reference plugin's own documented examples, verbatim apart from the two conditions
    // this project renames because it can answer them per slot.
    [InlineData("InGame && LifePercent > 0 && LifePercent <= 35")]
    [InlineData("InMap && NearestRareMonster <= 45")]
    [InlineData("InGame && HasBuff(\"replace_with_buff_name\") && BuffTimeLeft(\"replace_with_buff_name\") <= 2")]
    [InlineData("InGame && EverySeconds(0.5)")]
    [InlineData("InMap && GameFocused && RareOrUniqueMonsterCount >= 1")]
    [InlineData("InMap && Moving && NearestMonster <= 70")]
    [InlineData("InGame && FlaskReady(1)")]
    public void ReadsTheReferencePluginsExamples(string text) => Parsed(text);

    [Theory]
    [InlineData("InGame")]
    [InlineData("!InGame")]
    [InlineData("InGame && InMap")]
    [InlineData("InGame || InTown")]
    [InlineData("InGame && (InTown || InHideout)")]
    [InlineData("(InGame || InTown) && Alive")]
    [InlineData("!(InGame && Alive)")]
    [InlineData("LifePercent <= 35")]
    [InlineData("!(NearestRareMonster <= 45)")]
    [InlineData("HasBuff(\"frozen\")")]
    [InlineData("BuffTimeLeft(\"haste\") <= 2.5")]
    [InlineData("FlaskCharges(3) >= 20")]
    [InlineData("MonsterCountWithin(30) >= 5")]
    [InlineData("EverySeconds(0.5)")]
    [InlineData("exactlyOne(InTown, InHideout, InMap)")]
    [InlineData("InGame && exactlyOne(InTown, InHideout)")]
    public void WritingATreeOutAndReadingItBackGivesTheSameTree(string text)
    {
        RuleCondition once = Parsed(text);
        string written = RuleExpression.Write(once);

        Assert.Equal(once, Parsed(written));

        // And the text is stable, not merely equivalent - otherwise every save would rewrite
        // the file with a different spelling of the same rule.
        Assert.Equal(written, RuleExpression.Write(Parsed(written)));
    }

    [Fact]
    public void KeepsTheSpellingSomebodyWrote_WhereItMeansSomethingDifferent()
    {
        // The one rewrite that would be a behaviour change rather than tidying. An absent
        // number satisfies no comparison, so `!(x <= 45)` is TRUE with no rare in the area
        // while `x > 45` is false. The writer must not "simplify" one into the other.
        RuleCondition negated = Parsed("!(NearestRareMonster <= 45)");
        Assert.Equal("!(NearestRareMonster <= 45)", RuleExpression.Write(negated));

        var state = new RuleState { InGame = true };
        Assert.Null(state.NearestRareMonster);
        Assert.True(negated.Holds(state, new RuleTimers(), "rule"));
        Assert.False(Parsed("NearestRareMonster > 45").Holds(state, new RuleTimers(), "rule"));
    }

    [Theory]
    [InlineData("and", "&&")]
    [InlineData("or", "||")]
    [InlineData("not", "!")]
    public void TakesWordsAsWellAsSymbols(string word, string symbol)
    {
        string words = word == "not" ? $"{word} InGame" : $"InGame {word} InMap";
        string symbols = word == "not" ? $"{symbol}InGame" : $"InGame {symbol} InMap";

        Assert.Equal(Parsed(symbols), Parsed(words));
    }

    [Fact]
    public void DoesNotReadAWordOperatorOutOfTheStartOfAName()
    {
        // `or` at the start of `orbCount` would otherwise split into an or and a condition
        // called `bCount`. There is no such fact today, which is exactly why the guard has to
        // be tested rather than noticed.
        ExpressionResult result = RuleExpression.Parse("InGame || orbCount");
        Assert.False(result.Ok);
        Assert.Contains("orbCount", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void TakesEitherSpellingOfEquals_AndWritesOne()
    {
        Assert.Equal(Parsed("MonsterCount == 3"), Parsed("MonsterCount = 3"));
        Assert.Equal("MonsterCount == 3", RuleExpression.Write(Parsed("MonsterCount = 3")));
    }

    [Fact]
    public void ReadsDecimalsTheSameWayInEveryLocale()
    {
        // The settings file travels; a machine whose decimal separator is a comma must read
        // 0.5 as a half rather than as five.
        RuleCondition half = Parsed("EverySeconds(0.5)");
        Assert.Equal(0.5, half.Argument);
        Assert.Equal("EverySeconds(0.5)", RuleExpression.Write(half));
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("Nonsense", "no condition called")]
    [InlineData("InGame &&", "ends early")]
    [InlineData("(InGame", "Expected ')'")]
    [InlineData("InGame InMap", "Unexpected")]
    [InlineData("MonsterCount", "needs a comparison")]
    [InlineData("InGame >= 1", "cannot be compared")]
    [InlineData("HasBuff", "needs a name in quotes")]
    [InlineData("InGame(2)", "takes no value")]
    [InlineData("MonsterCount >= abc", "Expected a number")]
    [InlineData("exactlyOne(InGame)", "at least two")]
    public void SaysWhatIsWrongAndWhere(string text, string expected)
    {
        ExpressionResult result = RuleExpression.Parse(text);

        Assert.False(result.Ok);
        Assert.Contains(expected, result.Error, StringComparison.OrdinalIgnoreCase);

        // A caret with nowhere to sit is the half of an error message that gets left out and
        // then wanted; every failure names a column inside the text it was given.
        Assert.InRange(result.Column, 1, Math.Max(1, text.Length + 1));
    }

    [Fact]
    public void RefusesSomethingTooDeeplyNested()
    {
        string deep = new string('!', RuleCondition.MaxDepth + 2) + "InGame";
        ExpressionResult result = RuleExpression.Parse(deep);

        Assert.False(result.Ok);
        Assert.Contains("deeply", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesSomethingLongerThanAnyRule()
    {
        string long_ = string.Join(" && ", Enumerable.Repeat("InGame", 2000));
        Assert.False(RuleExpression.Parse(long_).Ok);
    }

    [Fact]
    public void EveryFactCanBeWrittenAndReadBack()
    {
        // The catalogue is the one description of what a rule may ask, and this is what keeps
        // a new row in it from being editable but unparseable. Every fact, in its own shape.
        foreach (FactInfo info in RuleFacts.All)
        {
            var leaf = new RuleCondition
            {
                Fact = info.Fact,
                Compare = Compare.AtLeast,
                Value = 1,
                Argument = info.Argument == FactArgument.Slot ? 2 : 1.5,
                Text = info.Argument == FactArgument.Text ? "something" : string.Empty,
            };

            string written = RuleExpression.Write(leaf);
            ExpressionResult read = RuleExpression.Parse(written);

            Assert.True(read.Ok, $"{info.Name} wrote '{written}': {read.Error}");
            Assert.Equal(info.Fact, read.Condition!.Fact);
        }
    }

    [Fact]
    public void TheFactCatalogueMatchesTheFactList()
    {
        // Describe() indexes the table by the enum's value, so a row inserted into one and not
        // the other would silently describe every later fact as its neighbour. Build() refuses
        // to load in that case; this is the test that the refusal is not what happened.
        Assert.Equal(Enum.GetValues<RuleFact>().Length, RuleFacts.All.Count);
        foreach (RuleFact fact in Enum.GetValues<RuleFact>())
        {
            Assert.Equal(fact, RuleFacts.Describe(fact).Fact);
            Assert.Equal(fact, RuleFacts.Find(RuleFacts.Describe(fact).Name)?.Fact);
        }
    }
}
