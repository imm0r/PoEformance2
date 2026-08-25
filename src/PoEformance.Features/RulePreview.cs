namespace PoEformance.Features;

/// <summary>
/// One radius a rule measures, drawn over the game while it is being built.
/// </summary>
/// <param name="AtCursor">
/// Whether the ring is centred on where the cursor points rather than on the player. Both are
/// circles of world units on the ground; only the centre differs.
/// </param>
/// <param name="Reads">What the fact answers right now.</param>
/// <param name="Holds">Whether the comparison in the rule is satisfied by that answer.</param>
public sealed record PreviewRing(
    bool AtCursor,
    double Radius,
    string Label,
    double? Reads,
    bool Holds);

/// <summary>
/// One leaf of the rule being built, with what it reads and whether it holds right now.
/// </summary>
/// <param name="Known">
/// False when the number behind the comparison could not be read - which the engine treats as
/// "does not hold", but a person debugging needs to see as its own state: "Mana = -, needs
/// >= 2500" is a different problem from "Mana = 1900, needs >= 2500".
/// </param>
public sealed record PreviewFact(string Label, bool Holds, bool Known);

/// <summary>
/// What to draw over the game so a rule can be checked against it.
/// </summary>
/// <remarks>
/// The project's own working rule, applied to its rule engine: verify against the game rather
/// than against yourself. A radius is a number in a text field, and the only honest way to know
/// whether 30 is the right one is to see the circle on the ground with the monsters it is
/// counting inside it.
///
/// Pure, like everything else here: it reads the same facts the engine reads and returns rings
/// as plain data. The overlay decides how a ring looks; this decides what a ring MEANS.
/// </remarks>
public static class RulePreview
{
    /// <summary>Most rings drawn at once, so a large profile cannot fill the screen.</summary>
    public const int MaxRings = 12;

    /// <summary>Most leaves listed at once, so a pathological rule cannot fill the screen.</summary>
    public const int MaxFacts = 16;

    /// <summary>
    /// The rings for one rule's condition, with what each one currently reads.
    /// </summary>
    /// <remarks>
    /// Deduplicated by centre and radius: two conditions asking about the same circle draw one,
    /// and their labels are joined - otherwise a rule that counts monsters and rares in the
    /// same radius paints the same ring twice, in the same place, at the same size.
    /// </remarks>
    public static IReadOnlyList<PreviewRing> Rings(RuleCondition? condition, RuleState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var found = new List<PreviewRing>();
        if (condition is not null)
        {
            Walk(condition, state, found, 0);
        }

        return Merge(found);
    }

    /// <summary>
    /// Every leaf of one rule's condition, with its current reading and verdict.
    /// </summary>
    /// <remarks>
    /// The rings show the two range counters and nothing else, so somebody watching a rule
    /// that will not fire was left guessing WHICH of the other conditions was the one saying
    /// no - "InMap and Mana >= 2500" are exactly as load-bearing as the counter on the ring.
    /// Leaves only, in tree order: the groups' verdicts follow from these, and a reader
    /// reconstructs them faster than a nested readout could show them.
    /// </remarks>
    public static IReadOnlyList<PreviewFact> Facts(RuleCondition? condition, RuleState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var found = new List<PreviewFact>();
        if (condition is not null)
        {
            WalkFacts(condition, state, found, 0);
        }

        return found;
    }

    private static void WalkFacts(RuleCondition node, RuleState state, List<PreviewFact> found, int depth)
    {
        if (depth > RuleCondition.MaxDepth || found.Count >= MaxFacts)
        {
            return;
        }

        if (node.Kind != ConditionKind.Fact)
        {
            foreach (RuleCondition child in node.Children)
            {
                WalkFacts(child, state, found, depth + 1);
            }

            return;
        }

        FactInfo info = RuleFacts.Describe(node.Fact);
        string no = node.Negate ? "not " : string.Empty;
        bool holds = node.Holds(state, QuietTimers, "preview");

        if (node.Fact == RuleFact.EverySeconds)
        {
            found.Add(new PreviewFact($"{no}every {Show(node.Argument)}s", holds, Known: true));
            return;
        }

        string argument = info.Argument switch
        {
            FactArgument.Text => $"({node.Text})",
            FactArgument.Slot => $"(slot {Show(node.Argument)})",
            FactArgument.Distance => $"({Show(node.Argument)}u)",
            FactArgument.Seconds => $"({Show(node.Argument)}s)",
            _ => string.Empty,
        };

        if (info.Shape == FactShape.Flag)
        {
            found.Add(new PreviewFact($"{no}{info.Name}{argument}", holds, Known: true));
            return;
        }

        double? reads = RuleFacts.Answer(node, state);
        found.Add(new PreviewFact(
            $"{info.Name}{argument} = {Show(reads)}, needs {no}{Symbol(node.Compare)} {Show(node.Value)}",
            holds,
            Known: reads is not null));
    }

    private static void Walk(RuleCondition node, RuleState state, List<PreviewRing> found, int depth)
    {
        if (depth > RuleCondition.MaxDepth || found.Count >= MaxRings)
        {
            return;
        }

        if (node.Kind != ConditionKind.Fact)
        {
            foreach (RuleCondition child in node.Children)
            {
                Walk(child, state, found, depth + 1);
            }

            return;
        }

        FactInfo info = RuleFacts.Describe(node.Fact);
        if (info.Argument != FactArgument.Distance)
        {
            return;
        }

        double? reads = RuleFacts.Answer(node, state);

        found.Add(new PreviewRing(
            AtCursor: info.AtCursor,
            Radius: node.Argument,

            // What it reads first, because that is the number somebody is watching change;
            // what it needs after, so the ring explains itself without the editor open.
            Label: $"{info.Name} = {Show(reads)}, needs {(node.Negate ? "not " : string.Empty)}"
                + $"{Symbol(node.Compare)} {Show(node.Value)}",
            Reads: reads,

            // The leaf's own answer INCLUDING its negation, so the ring turns green exactly
            // when the rule is happy - a "not within 30" condition reads the other way round
            // and a ring that ignored that would be lying about the rule it came from.
            Holds: node.Holds(state, QuietTimers, "preview")));
    }

    /// <summary>
    /// Rings never consume an interval.
    /// </summary>
    /// <remarks>
    /// A preview is drawn every frame, and an EverySeconds leaf beside a radius one in the same
    /// group would otherwise have its timer ticked by the drawing - so the rule sharing that
    /// interval never comes round. Its own instance, discarded, is what stops the debug view
    /// changing what it is debugging.
    /// </remarks>
    private static RuleTimers QuietTimers { get; } = new();

    private static List<PreviewRing> Merge(List<PreviewRing> rings)
    {
        var merged = new List<PreviewRing>(rings.Count);
        foreach (PreviewRing ring in rings)
        {
            int at = merged.FindIndex(
                other => other.AtCursor == ring.AtCursor && other.Radius.Equals(ring.Radius));

            if (at < 0)
            {
                merged.Add(ring);
                continue;
            }

            merged[at] = merged[at] with
            {
                Label = merged[at].Label + "  |  " + ring.Label,

                // One of them failing is what the ring should show: the rule needs both.
                Holds = merged[at].Holds && ring.Holds,
            };
        }

        return merged;
    }

    private static string Show(double? value)
        => value is double number
            ? number.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
            : "-";

    /// <summary>The comparison as a reader sees it, not as the enum spells it.</summary>
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
}
