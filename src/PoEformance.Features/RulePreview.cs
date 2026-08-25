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
