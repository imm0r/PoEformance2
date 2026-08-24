using System.Text.Json.Serialization;

namespace PoEformance.Features;

/// <summary>What a box in the node editor is.</summary>
public enum GraphNodeKind
{
    /// <summary>A question about the game.</summary>
    Fact,

    /// <summary>Every wire into this box must hold.</summary>
    All,

    /// <summary>One wire into this box holding is enough.</summary>
    Any,

    /// <summary>Exactly one wire into this box must hold.</summary>
    ExactlyOne,

    /// <summary>Inverts the one wire into it.</summary>
    Not,

    /// <summary>The rule's answer. Exactly one per graph.</summary>
    Output,
}

/// <summary>One box in the node editor.</summary>
/// <param name="X">Where the box sits on the canvas. Layout only - it changes no answer.</param>
public sealed record GraphNode(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] GraphNodeKind Kind,
    [property: JsonPropertyName("x")] float X,
    [property: JsonPropertyName("y")] float Y)
{
    /// <summary>What a fact box asks. Ignored by every other kind.</summary>
    [JsonPropertyName("fact")]
    public RuleFact Fact { get; init; }

    [JsonPropertyName("compare")]
    public Compare Compare { get; init; } = Compare.AtMost;

    [JsonPropertyName("value")]
    public double Value { get; init; }

    [JsonPropertyName("argument")]
    public double Argument { get; init; }

    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    /// <summary>Whether this box's own answer is inverted, without wiring a Not to it.</summary>
    [JsonPropertyName("negate")]
    public bool Negate { get; init; }

    /// <summary>Whether wires may end at this box.</summary>
    public bool TakesInput => Kind != GraphNodeKind.Fact;

    /// <summary>Whether wires may start at this box.</summary>
    public bool GivesOutput => Kind != GraphNodeKind.Output;
}

/// <summary>One wire, from a box's output to another box's input.</summary>
public readonly record struct GraphLink(
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] string To);

/// <summary>
/// A rule's condition, drawn as boxes and wires.
/// </summary>
/// <remarks>
/// A LAYOUT AND A SECOND WAY IN, not a second copy of the rule. The condition tree on the rule
/// is what runs; this converts to it and from it, and what it adds is where the boxes sit.
///
/// The reference plugin has this the other way round: its graph is the source, its condition
/// STRING is regenerated from the graph on every edit, and a rule whose text somebody adjusted
/// loses that adjustment the moment a box is dragged. Here both directions are conversions of
/// one stored tree, so neither view can overwrite the other with a stale copy.
/// </remarks>
public sealed record RuleGraph(
    [property: JsonPropertyName("nodes")] IReadOnlyList<GraphNode> Nodes,
    [property: JsonPropertyName("links")] IReadOnlyList<GraphLink> Links)
{
    /// <summary>Most boxes one graph may hold.</summary>
    public const int MaxNodes = 256;

    /// <summary>Horizontal gap between the columns an automatic layout produces.</summary>
    private const float ColumnWidth = 220f;

    /// <summary>Vertical gap between boxes in a column.</summary>
    private const float RowHeight = 90f;

    /// <summary>
    /// Reads the graph as a condition tree.
    /// </summary>
    /// <remarks>
    /// From the output backwards, following wires to their sources - which is what makes a box
    /// nobody wired up cost nothing rather than quietly joining the rule. A graph with no
    /// output, or a cycle in it, reads as a group that says nothing, and a group that says
    /// nothing fires nothing.
    /// </remarks>
    public RuleCondition ToCondition()
    {
        GraphNode? output = null;
        foreach (GraphNode node in Nodes)
        {
            if (node.Kind == GraphNodeKind.Output)
            {
                output = node;
                break;
            }
        }

        if (output is null)
        {
            return new RuleCondition { Kind = ConditionKind.All };
        }

        var walking = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyList<RuleCondition> answer = InputsOf(output.Id, walking, 0);

        // The output takes one wire. Several is a graph somebody is midway through rewiring,
        // and joining them with an implicit AND would be this tool deciding what they meant.
        return answer.Count == 1
            ? answer[0]
            : new RuleCondition { Kind = ConditionKind.All, Children = answer.Count == 0 ? [] : answer };
    }

    private IReadOnlyList<RuleCondition> InputsOf(string nodeId, HashSet<string> walking, int depth)
    {
        var inputs = new List<RuleCondition>();
        if (depth > RuleCondition.MaxDepth)
        {
            return inputs;
        }

        foreach (GraphLink link in Links)
        {
            if (!string.Equals(link.To, nodeId, StringComparison.Ordinal))
            {
                continue;
            }

            if (Compile(link.From, walking, depth) is RuleCondition condition)
            {
                inputs.Add(condition);
            }

            if (inputs.Count == RuleCondition.MaxChildren)
            {
                break;
            }
        }

        return inputs;
    }

    private RuleCondition? Compile(string nodeId, HashSet<string> walking, int depth)
    {
        // A wire looped back to something already on this path. Dropping the branch rather
        // than failing the graph: the editor can still be opened, the cycle can still be seen,
        // and the rule fires nothing in the meantime.
        if (!walking.Add(nodeId))
        {
            return null;
        }

        try
        {
            GraphNode? found = Find(nodeId);
            if (found is not GraphNode node)
            {
                return null;
            }

            if (node.Kind == GraphNodeKind.Fact)
            {
                return new RuleCondition
                {
                    Fact = node.Fact,
                    Compare = node.Compare,
                    Value = node.Value,
                    Argument = node.Argument,
                    Text = node.Text ?? string.Empty,
                    Negate = node.Negate,
                };
            }

            IReadOnlyList<RuleCondition> inputs = InputsOf(nodeId, walking, depth + 1);

            if (node.Kind == GraphNodeKind.Not)
            {
                // Folded into the flag on whatever it inverts, which is why there is no Not in
                // the condition tree at all. A Not with no input, or with several, inverts
                // nothing: an empty group, which is false, so the Not reads as true - and that
                // would be a half-wired graph firing a rule.
                return inputs.Count == 1
                    ? inputs[0] with { Negate = !inputs[0].Negate }
                    : new RuleCondition { Kind = ConditionKind.All };
            }

            return new RuleCondition
            {
                Kind = node.Kind switch
                {
                    GraphNodeKind.Any => ConditionKind.Any,
                    GraphNodeKind.ExactlyOne => ConditionKind.ExactlyOne,
                    _ => ConditionKind.All,
                },
                Children = inputs,
                Negate = node.Negate,
            };
        }
        finally
        {
            // Off the path on the way out, so a box feeding two different branches is compiled
            // for each of them - that is a diamond, not a cycle.
            walking.Remove(nodeId);
        }
    }

    private GraphNode? Find(string id)
    {
        foreach (GraphNode node in Nodes)
        {
            if (string.Equals(node.Id, id, StringComparison.Ordinal))
            {
                return node;
            }
        }

        return null;
    }

    /// <summary>Drops the boxes that reach no output, and the wires to nowhere.</summary>
    /// <remarks>
    /// What the editor's tidy-up button does. Kept here rather than in the page because the
    /// answer to "does this box take part" is the same walk that compiles the graph, and two
    /// implementations of that would eventually disagree about which boxes matter.
    /// </remarks>
    public RuleGraph Pruned()
    {
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (GraphNode node in Nodes)
        {
            if (node.Kind == GraphNodeKind.Output)
            {
                keep.Add(node.Id);
                Reaching(node.Id, keep, 0);
            }
        }

        var nodes = new List<GraphNode>();
        foreach (GraphNode node in Nodes)
        {
            if (keep.Contains(node.Id))
            {
                nodes.Add(node);
            }
        }

        var links = new List<GraphLink>();
        foreach (GraphLink link in Links)
        {
            if (keep.Contains(link.From) && keep.Contains(link.To))
            {
                links.Add(link);
            }
        }

        return new RuleGraph(nodes, links);
    }

    private void Reaching(string nodeId, HashSet<string> keep, int depth)
    {
        if (depth > MaxNodes)
        {
            return;
        }

        foreach (GraphLink link in Links)
        {
            if (string.Equals(link.To, nodeId, StringComparison.Ordinal) && keep.Add(link.From))
            {
                Reaching(link.From, keep, depth + 1);
            }
        }
    }

    /// <summary>
    /// Draws a graph for a condition tree.
    /// </summary>
    /// <remarks>
    /// What opens the node editor on a rule that was typed rather than drawn. Laid out in
    /// columns by depth so a fresh graph is readable without dragging; once it is saved, the
    /// stored positions are used instead and this is not consulted again.
    /// </remarks>
    public static RuleGraph From(RuleCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);

        var nodes = new List<GraphNode>();
        var links = new List<GraphLink>();
        var rows = new Dictionary<int, int>();
        int next = 0;

        string root = Draw(condition, nodes, links, rows, ref next, depth: 1);

        var output = new GraphNode(
            Id: "n" + next.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Kind: GraphNodeKind.Output,
            X: 0,
            Y: 0);
        nodes.Add(output);
        links.Add(new GraphLink(root, output.Id));

        return Placed(new RuleGraph(nodes, links));
    }

    private static string Draw(
        RuleCondition condition,
        List<GraphNode> nodes,
        List<GraphLink> links,
        Dictionary<int, int> rows,
        ref int next,
        int depth)
    {
        string id = "n" + next.ToString(System.Globalization.CultureInfo.InvariantCulture);
        next++;

        rows.TryGetValue(depth, out int row);
        rows[depth] = row + 1;

        if (condition.Kind == ConditionKind.Fact)
        {
            nodes.Add(new GraphNode(id, GraphNodeKind.Fact, 0, 0)
            {
                Fact = condition.Fact,
                Compare = condition.Compare,
                Value = condition.Value,
                Argument = condition.Argument,
                Text = condition.Text,
                Negate = condition.Negate,
            });
            return id;
        }

        nodes.Add(new GraphNode(
            id,
            condition.Kind switch
            {
                ConditionKind.Any => GraphNodeKind.Any,
                ConditionKind.ExactlyOne => GraphNodeKind.ExactlyOne,
                _ => GraphNodeKind.All,
            },
            0,
            0)
        {
            Negate = condition.Negate,
        });

        foreach (RuleCondition child in condition.Children)
        {
            if (nodes.Count >= MaxNodes)
            {
                break;
            }

            links.Add(new GraphLink(Draw(child, nodes, links, rows, ref next, depth + 1), id));
        }

        return id;
    }

    /// <summary>Puts every box in a column by how far it is from the output.</summary>
    private static RuleGraph Placed(RuleGraph graph)
    {
        var depths = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (GraphNode node in graph.Nodes)
        {
            if (node.Kind == GraphNodeKind.Output)
            {
                graph.Depths(node.Id, 0, depths, 0);
            }
        }

        int deepest = 0;
        foreach (int depth in depths.Values)
        {
            deepest = Math.Max(deepest, depth);
        }

        var used = new Dictionary<int, int>();
        var placed = new List<GraphNode>(graph.Nodes.Count);
        foreach (GraphNode node in graph.Nodes)
        {
            int depth = depths.TryGetValue(node.Id, out int found) ? found : deepest;
            used.TryGetValue(depth, out int row);
            used[depth] = row + 1;

            // Deepest on the left, output on the right - the direction the wires run, and the
            // one everybody's node editor uses.
            placed.Add(node with
            {
                X = (deepest - depth) * ColumnWidth,
                Y = row * RowHeight,
            });
        }

        return graph with { Nodes = placed };
    }

    private void Depths(string nodeId, int depth, Dictionary<string, int> depths, int guard)
    {
        if (guard > MaxNodes)
        {
            return;
        }

        // The LONGEST way back to the output, so a box feeding two branches sits left of both
        // rather than being drawn on top of a wire that passes it.
        if (depths.TryGetValue(nodeId, out int already) && already >= depth)
        {
            return;
        }

        depths[nodeId] = depth;
        foreach (GraphLink link in Links)
        {
            if (string.Equals(link.To, nodeId, StringComparison.Ordinal))
            {
                Depths(link.From, depth + 1, depths, guard + 1);
            }
        }
    }
}
