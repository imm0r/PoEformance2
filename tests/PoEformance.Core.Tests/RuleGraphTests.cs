using PoEformance.Features;
using PoEformance.Game.Components;

namespace PoEformance.Core.Tests;

/// <summary>
/// The node editor's boxes and wires, read as a condition.
/// </summary>
/// <remarks>
/// The half-wired cases are the ones that matter. A finished graph is easy; a graph somebody is
/// midway through rewiring is the state the editor spends most of its time in, and every one of
/// those has to come out as a condition that fires NOTHING rather than as one that fires.
/// </remarks>
public class RuleGraphTests
{
    private static GraphNode Fact(string id, RuleFact fact) => new(id, GraphNodeKind.Fact, 0, 0) { Fact = fact };

    private static GraphNode Box(string id, GraphNodeKind kind) => new(id, kind, 0, 0);

    private static readonly RuleState Playing = new() { InGame = true, GameFocused = true, Alive = true };

    private static bool Holds(RuleGraph graph, RuleState? state = null)
        => graph.ToCondition().Holds(state ?? Playing, new RuleTimers(), "rule");

    [Fact]
    public void ReadsAWiredGraphAsItsCondition()
    {
        var graph = new RuleGraph(
            [Fact("a", RuleFact.InGame), Fact("b", RuleFact.Alive), Box("and", GraphNodeKind.All), Box("out", GraphNodeKind.Output)],
            [new GraphLink("a", "and"), new GraphLink("b", "and"), new GraphLink("and", "out")]);

        Assert.Equal(RuleExpression.Parse("InGame && Alive").Condition, graph.ToCondition());
        Assert.True(Holds(graph));
        Assert.False(Holds(graph, Playing with { Alive = false }));
    }

    [Fact]
    public void ANotBoxFoldsIntoWhatItInverts()
    {
        // Which is why the condition tree has no Not at all: negation is a flag on a node, so
        // it cannot be wired to two inputs and then quietly mean nothing.
        var graph = new RuleGraph(
            [Fact("a", RuleFact.InTown), Box("not", GraphNodeKind.Not), Box("out", GraphNodeKind.Output)],
            [new GraphLink("a", "not"), new GraphLink("not", "out")]);

        RuleCondition condition = graph.ToCondition();

        Assert.Equal(ConditionKind.Fact, condition.Kind);
        Assert.True(condition.Negate);
        Assert.True(Holds(graph));
    }

    [Fact]
    public void ANotWiredToNothingInvertsNothing()
    {
        // The trap this exists to avoid: an empty group is false, so "not empty group" would
        // be TRUE - a box somebody has just dropped on the canvas firing the rule.
        var graph = new RuleGraph(
            [Box("not", GraphNodeKind.Not), Box("out", GraphNodeKind.Output)],
            [new GraphLink("not", "out")]);

        Assert.False(Holds(graph));
    }

    [Fact]
    public void ABoxNobodyWiredUpCostsNothing()
    {
        var graph = new RuleGraph(
            [Fact("a", RuleFact.InGame), Fact("loose", RuleFact.InTown), Box("out", GraphNodeKind.Output)],
            [new GraphLink("a", "out")]);

        Assert.True(Holds(graph));
        Assert.Equal(RuleFact.InGame, graph.ToCondition().Fact);
    }

    [Fact]
    public void AGraphWithNoOutputFiresNothing()
    {
        var graph = new RuleGraph([Fact("a", RuleFact.InGame)], []);

        Assert.True(graph.ToCondition().SaysNothing);
        Assert.False(Holds(graph));
    }

    [Fact]
    public void AWireLoopedBackOnItselfDoesNotHang()
    {
        var graph = new RuleGraph(
            [Box("x", GraphNodeKind.All), Box("y", GraphNodeKind.All), Box("out", GraphNodeKind.Output)],
            [new GraphLink("x", "y"), new GraphLink("y", "x"), new GraphLink("y", "out")]);

        Assert.False(Holds(graph));
    }

    [Fact]
    public void OneBoxMayFeedTwoBranches()
    {
        // A diamond is not a cycle. The walk has to put a node back on the table on the way
        // out, or the second branch reads its shared input as a loop and drops it.
        var graph = new RuleGraph(
            [
                Fact("shared", RuleFact.InGame),
                Box("left", GraphNodeKind.All),
                Box("right", GraphNodeKind.All),
                Box("join", GraphNodeKind.All),
                Box("out", GraphNodeKind.Output),
            ],
            [
                new GraphLink("shared", "left"),
                new GraphLink("shared", "right"),
                new GraphLink("left", "join"),
                new GraphLink("right", "join"),
                new GraphLink("join", "out"),
            ]);

        Assert.True(Holds(graph));
        Assert.Equal(2, graph.ToCondition().Children.Count);
    }

    [Theory]
    [InlineData("InGame && Alive")]
    [InlineData("InGame || InTown")]
    [InlineData("InGame && (InTown || InHideout)")]
    [InlineData("!(InGame && Alive)")]
    [InlineData("LifePercent <= 35")]
    [InlineData("HasBuff(\"frozen\") && !(NearestRareMonster <= 45)")]
    [InlineData("exactlyOne(InTown, InHideout, InMap)")]
    public void DrawingATreeAndReadingItBackGivesTheSameTree(string text)
    {
        // What makes the text box and the canvas two views of one rule rather than two stores.
        RuleCondition original = RuleExpression.Parse(text).Condition!;

        Assert.Equal(original, RuleGraph.From(original).ToCondition());
    }

    [Fact]
    public void ADrawnGraphIsLaidOutLeftToRight()
    {
        RuleGraph graph = RuleGraph.From(RuleExpression.Parse("InGame && Alive").Condition!);

        GraphNode output = graph.Nodes.Single(node => node.Kind == GraphNodeKind.Output);
        GraphNode leaf = graph.Nodes.First(node => node.Kind == GraphNodeKind.Fact);

        Assert.True(leaf.X < output.X, "facts sit left of the output they feed");

        // And nothing is drawn on top of anything else in the same column.
        foreach (IGrouping<float, GraphNode> column in graph.Nodes.GroupBy(node => node.X))
        {
            Assert.Equal(column.Count(), column.Select(node => node.Y).Distinct().Count());
        }
    }

    [Fact]
    public void PruningDropsWhatReachesNoOutput()
    {
        var graph = new RuleGraph(
            [Fact("a", RuleFact.InGame), Fact("loose", RuleFact.InTown), Box("out", GraphNodeKind.Output)],
            [new GraphLink("a", "out"), new GraphLink("loose", "nowhere")]);

        RuleGraph pruned = graph.Pruned();

        Assert.Equal(2, pruned.Nodes.Count);
        Assert.DoesNotContain(pruned.Nodes, node => node.Id == "loose");
        Assert.Single(pruned.Links);

        // And pruning is a tidy-up, never a change of meaning.
        Assert.Equal(graph.ToCondition(), pruned.ToCondition());
    }

    [Fact]
    public void AGraphSurvivesBeingWrittenToTheSettingsFile()
    {
        // The graph rides along on the rule so that reopening the editor shows the boxes where
        // they were left. That only works if it serialises.
        RuleCondition condition = RuleExpression.Parse("InGame && LifePercent <= 35").Condition!;
        var rule = new Rule("id", "Test", condition, [new RuleEffect()])
        {
            Graph = RuleGraph.From(condition),
        };

        var settings = new RuleSettings(false, "Default", [new RuleProfile("Default", [new RuleGroup("G", [rule])])]);

        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            Assert.True(RuleSettingsStore.Save(settings, path));
            RuleSettings loaded = RuleSettingsStore.Load(path);

            Rule read = loaded.Profiles[0].Groups[0].Rules[0];
            Assert.Equal(condition, read.Condition);
            Assert.NotNull(read.Graph);
            Assert.Equal(condition, read.Graph!.ToCondition());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ARuleDrawnOnTheCanvasEvaluatesWhatWasDrawn()
    {
        // The bug this exists for, reported from a real session: the canvas saved a wired-up
        // graph faithfully and the rule never fired. Nothing derived a condition from the
        // boxes, so a rule built there kept the empty condition it was created with - which
        // says nothing, and a condition that says nothing fires nothing. Every part of the
        // feature looked correct: the graph came back, the wires were right, the effect was
        // configured, and the status line said it was watching.
        var graph = new RuleGraph(
            [
                Fact("a", RuleFact.InMap),
                new GraphNode("b", GraphNodeKind.Fact, 0, 0)
                {
                    Fact = RuleFact.MonsterCount,
                    Compare = Compare.AtLeast,
                    Value = 3,
                },
                Box("and", GraphNodeKind.All),
                Box("out", GraphNodeKind.Output),
            ],
            [new GraphLink("a", "and"), new GraphLink("b", "and"), new GraphLink("and", "out")]);

        Rule drawn = new Rule("id", "Flamewall", new RuleCondition { Kind = ConditionKind.All }, [])
        {
            Graph = graph,
        }.Normalised();

        Assert.False(drawn.Condition.SaysNothing);
        Assert.Equal("InMap && MonsterCount >= 3", RuleExpression.Write(drawn.Condition));

        var state = new RuleState
        {
            InGame = true,
            GameFocused = true,
            Monsters = [new NearMonster(10, ItemRarity.Normal), new NearMonster(20, ItemRarity.Normal), new NearMonster(30, ItemRarity.Rare)],
        };

        Assert.True(drawn.Condition.Holds(state, new RuleTimers(), "rule"));
        Assert.False(drawn.Condition.Holds(state with { Monsters = [] }, new RuleTimers(), "rule"));
    }

    [Fact]
    public void ATypedRuleKeepsItsOwnCondition()
    {
        // The other half of the same rule: with no graph, the condition stands on its own and
        // nothing overwrites it. Deriving from an absent graph would empty every typed rule.
        RuleCondition typed = RuleExpression.Parse("InTown").Condition!;

        Assert.Equal(typed, new Rule("id", "Typed", typed, []).Normalised().Condition);
    }

    [Fact]
    public void TheDrawnConditionSurvivesTheSettingsFile()
    {
        // End to end, because the derivation lives in Normalised() and the file goes through
        // it twice - once on save, once on load.
        RuleCondition condition = RuleExpression.Parse("InMap && MonsterCountWithin(30) >= 3").Condition!;
        var rule = new Rule("id", "Flamewall", new RuleCondition { Kind = ConditionKind.All }, [new RuleEffect()])
        {
            Graph = RuleGraph.From(condition),
        };

        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            Assert.True(RuleSettingsStore.Save(
                new RuleSettings(true, "P", [new RuleProfile("P", [new RuleGroup("G", [rule])])]), path));

            Rule loaded = RuleSettingsStore.Load(path).Profiles[0].Groups[0].Rules[0];
            Assert.Equal(condition, loaded.Condition);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ARuleWithNoIdGetsOneRatherThanBeingDropped()
    {
        // A hand-written file should not have to invent GUIDs to be loadable, and everything
        // about a rule - its cooldown, its timers - is remembered under that id.
        Rule rule = new Rule(string.Empty, " ", RuleCondition.Of(RuleFact.InGame), []).Normalised();

        Assert.NotEqual(string.Empty, rule.Id);
        Assert.Equal("Rule", rule.Name);
    }
}
