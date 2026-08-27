using System.Text.Json.Serialization;

namespace PoEformance.Features;

/// <summary>What a node in a condition tree is.</summary>
/// <remarks>
/// One enum for leaves and joins together, rather than a class hierarchy, because this
/// travels as JSON to the config page and back. Polymorphic serialisation under source
/// generation is a fight; a tagged record is not.
///
/// There is no Not member. Negation is a FLAG on every node, so it applies to a leaf and to a
/// group alike, and a graph's Not node folds into the flag of whatever it negates. The
/// reference plugin has Not as a node type, which makes "not" a thing that can be wired to two
/// inputs and then quietly evaluates to false.
/// </remarks>
public enum ConditionKind
{
    /// <summary>A question about the game.</summary>
    Fact,

    /// <summary>Every child must hold.</summary>
    All,

    /// <summary>At least one child must hold.</summary>
    Any,

    /// <summary>
    /// Exactly one child must hold.
    /// </summary>
    /// <remarks>
    /// Exactly one, NOT the parity that chaining `^` gives: with three children, parity says
    /// yes when all three hold, and nobody drawing three boxes into an XOR means that.
    /// </remarks>
    ExactlyOne,
}

/// <summary>How a number is compared against the value in a condition.</summary>
public enum Compare
{
    AtLeast,
    AtMost,
    Above,
    Below,
    Is,
    IsNot,
}

/// <summary>
/// One node of a rule's condition: a question, or a way of joining other questions.
/// </summary>
/// <remarks>
/// The tree IS the stored form. The reference plugin stores a STRING and keeps a node graph
/// beside it that regenerates that string, so the two can disagree and the graph is the copy
/// that loses. Here the tree is what runs, an expression is a way of writing one down, and a
/// graph is a way of drawing one - both convert, neither is authoritative.
/// </remarks>
public sealed record RuleCondition
{
    /// <summary>What this node is.</summary>
    [JsonPropertyName("kind")]
    public ConditionKind Kind { get; init; } = ConditionKind.Fact;

    /// <summary>The joined nodes, for a group. Empty for a fact.</summary>
    [JsonPropertyName("children")]
    public IReadOnlyList<RuleCondition> Children { get; init; } = [];

    /// <summary>What is being asked, for a fact.</summary>
    [JsonPropertyName("fact")]
    public RuleFact Fact { get; init; }

    /// <summary>How the fact's number is tested. Ignored for a fact that answers yes or no.</summary>
    [JsonPropertyName("compare")]
    public Compare Compare { get; init; } = Compare.AtMost;

    /// <summary>What the fact's number is tested against.</summary>
    [JsonPropertyName("value")]
    public double Value { get; init; }

    /// <summary>
    /// The fact's own parameter: a belt slot, a distance, an interval.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Value"/> on purpose. The reference plugin folds both into one
    /// field, which is why its FlaskCharges condition cannot say WHICH slot it means - the
    /// number it was given had already been spent on the comparison.
    /// </remarks>
    [JsonPropertyName("argument")]
    public double Argument { get; init; }

    /// <summary>The fact's own text parameter: a buff name, part of an area name.</summary>
    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    /// <summary>Whether this node's answer is inverted.</summary>
    [JsonPropertyName("negate")]
    public bool Negate { get; init; }

    /// <summary>A leaf asking one question.</summary>
    public static RuleCondition Of(RuleFact fact) => new() { Fact = fact };

    /// <summary>A leaf comparing a number.</summary>
    public static RuleCondition Of(RuleFact fact, Compare compare, double value)
        => new() { Fact = fact, Compare = compare, Value = value };

    /// <summary>A group whose children must all hold.</summary>
    public static RuleCondition All(params RuleCondition[] children)
        => new() { Kind = ConditionKind.All, Children = children };

    /// <summary>A group where one child holding is enough.</summary>
    public static RuleCondition Any(params RuleCondition[] children)
        => new() { Kind = ConditionKind.Any, Children = children };

    /// <summary>Whether this node says anything at all.</summary>
    /// <remarks>
    /// An empty group is the shape a half-built rule has, and it must not fire. Logic would
    /// call an empty "all" vacuously true, which here means a rule somebody was midway
    /// through drawing starts pressing keys - the same reasoning that makes a preload entry
    /// with no path match nothing rather than everything.
    /// </remarks>
    public bool SaysNothing => Kind != ConditionKind.Fact && Children.Count == 0;

    /// <summary>
    /// Whether this condition holds right now.
    /// </summary>
    /// <param name="state">The facts, gathered once for the whole tree.</param>
    /// <param name="timers">Where interval facts keep their last firing.</param>
    /// <param name="key">
    /// Identifies the RULE, so two rules with the same interval keep separate timers. Leaves
    /// are numbered within the walk, so nobody has to invent a timer name - which the
    /// reference plugin's expression form makes the user do, with a copy-pasted rule silently
    /// sharing its original's timer as the reward for forgetting.
    /// </param>
    public bool Holds(RuleState state, RuleTimers timers, string key)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(timers);

        int ordinal = 0;
        return Holds(state, timers, key, ref ordinal);
    }

    private bool Holds(RuleState state, RuleTimers timers, string key, ref int ordinal)
    {
        bool answer;
        if (Kind == ConditionKind.Fact)
        {
            answer = RuleFacts.Holds(this, state, timers, $"{key}#{ordinal++}");
        }
        else if (Children.Count == 0)
        {
            answer = false;
        }
        else
        {
            answer = Join(state, timers, key, ref ordinal);
        }

        return answer != Negate;
    }

    private bool Join(RuleState state, RuleTimers timers, string key, ref int ordinal)
    {
        // Every child is walked even once the answer is settled, and the ordinal is what makes
        // that necessary: short-circuiting would leave the leaves after the decision unnumbered
        // this tick and numbered the next, so an interval fact's timer key would move under it.
        // The walk is a few comparisons over a handful of nodes, and correctness of the keys is
        // worth more than skipping them.
        int held = 0;
        foreach (RuleCondition child in Children)
        {
            if (child.Holds(state, timers, key, ref ordinal))
            {
                held++;
            }
        }

        return Kind switch
        {
            ConditionKind.All => held == Children.Count,
            ConditionKind.Any => held > 0,
            ConditionKind.ExactlyOne => held == 1,
            _ => false,
        };
    }

    /// <summary>Applies a limit to how deep and how wide a tree may be.</summary>
    /// <remarks>
    /// A hand-edited file and a graph with a cycle both arrive here as a tree that never ends.
    /// Trimming rather than throwing: a rule the user can see and fix beats a settings file
    /// that refuses to load, and everything below the cut is dropped as a group that says
    /// nothing - which fires nothing.
    /// </remarks>
    public RuleCondition Trimmed(int depth = MaxDepth)
    {
        if (Kind == ConditionKind.Fact)
        {
            return this with { Children = [], Text = Text ?? string.Empty };
        }

        if (depth <= 0)
        {
            return this with { Children = [] };
        }

        var children = new List<RuleCondition>(Math.Min(Children.Count, MaxChildren));
        foreach (RuleCondition child in Children)
        {
            if (children.Count == MaxChildren)
            {
                break;
            }

            children.Add(child.Trimmed(depth - 1));
        }

        return this with { Children = children };
    }

    /// <summary>How deeply groups may nest before the rest is dropped.</summary>
    public const int MaxDepth = 16;

    /// <summary>How many children one group may join.</summary>
    public const int MaxChildren = 64;

    /// <summary>
    /// Two conditions are the same when they ASK the same thing.
    /// </summary>
    /// <remarks>
    /// Written out rather than left to the record, and this is not a nicety. A record compares
    /// a list-typed member by REFERENCE, so two trees built from the same text - one parsed
    /// from the file, one from the editor - are never equal however identical they are. Every
    /// "did this change" check upstream then answers yes on every tick: the config page
    /// republishes rules it was only asked to display, and a save happens on every keystroke.
    /// </remarks>
    public bool Equals(RuleCondition? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (Kind != other.Kind
            || Fact != other.Fact
            || Compare != other.Compare
            || Negate != other.Negate
            || !Value.Equals(other.Value)
            || !Argument.Equals(other.Argument)
            || !string.Equals(Text, other.Text, StringComparison.Ordinal)
            || Children.Count != other.Children.Count)
        {
            return false;
        }

        for (int index = 0; index < Children.Count; index++)
        {
            if (!Children[index].Equals(other.Children[index]))
            {
                return false;
            }
        }

        return true;
    }

    public override int GetHashCode()
    {
        var hash = default(HashCode);
        hash.Add(Kind);
        hash.Add(Fact);
        hash.Add(Compare);
        hash.Add(Negate);
        hash.Add(Value);
        hash.Add(Argument);
        hash.Add(Text, StringComparer.Ordinal);
        foreach (RuleCondition child in Children)
        {
            hash.Add(child);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// Where interval facts remember when they last fired.
/// </summary>
/// <remarks>
/// An object rather than a static dictionary, so an engine's timers belong to that engine.
/// Two of the reference plugin's are shared process-wide, which means a test cannot run twice
/// and two profiles cannot hold a rule of the same name without stealing each other's clock.
/// </remarks>
public sealed class RuleTimers
{
    /// <summary>Timers kept before the oldest are dropped.</summary>
    /// <remarks>
    /// Keys are derived from rule identity and leaf position, so this is bounded by the size
    /// of the profile in practice. It is here because "bounded in practice" is a claim about
    /// configuration, and a long session should not depend on one.
    /// </remarks>
    public const int MaxTimers = 4096;

    private readonly Dictionary<string, long> _fired = new(StringComparer.Ordinal);
    private long _now;

    /// <summary>Moves the clock. Called once per tick, before any rule is evaluated.</summary>
    public void Tick(long nowMs) => _now = nowMs;

    /// <summary>Whether this interval has come round again, and marks it if so.</summary>
    public bool Due(string key, double seconds)
    {
        // Below a tenth of a second an interval is "every tick" with extra steps, and the
        // engine's own rate is the real limit anyway.
        long every = (long)(Math.Max(seconds, 0.1) * 1000);

        if (_fired.TryGetValue(key, out long last) && _now - last < every)
        {
            return false;
        }

        if (_fired.Count >= MaxTimers && !_fired.ContainsKey(key))
        {
            _fired.Clear();
        }

        _fired[key] = _now;
        return true;
    }

    /// <summary>Forgets every interval, so a reload does not inherit the old clock.</summary>
    public void Forget() => _fired.Clear();
}
