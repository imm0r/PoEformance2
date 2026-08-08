using System.Text;
using PoEformance.Game.World;

namespace PoEformance.Features;

/// <summary>What one map on a planned ritual line is worth.</summary>
/// <param name="At">Where it is on the atlas.</param>
/// <param name="First">Its reward, as the short label a row shows.</param>
/// <param name="Second">Its second reward, or empty on the ordinary single-reward node.</param>
public readonly record struct RitualStop((int X, int Y) At, string First, string Second)
{
    /// <summary>Whether this node rolled two rewards.</summary>
    public bool Twice => Second.Length > 0;
}

/// <summary>
/// One way the ritual line could be drawn, and what it would pay.
/// </summary>
/// <param name="From">Where it starts - a map you can enter now, or the line's current end.</param>
/// <param name="Stops">Each map it would take, in order, with what that map rolls.</param>
/// <param name="Worth">
/// The summed value of its rewards, from the user's own weights. Meaningless on its own; it
/// exists to sort one route above another.
/// </param>
public sealed record RitualChain((int X, int Y) From, IReadOnlyList<RitualStop> Stops, int Worth)
{
    /// <summary>
    /// A stable name for this route, so a selection survives the line being re-planned.
    /// </summary>
    /// <remarks>
    /// Built from the positions rather than the names: two maps can share a name, and a route
    /// identified by "Bastille > Headland" would move to a different one the moment the atlas
    /// scrolled up a second Bastille.
    /// </remarks>
    public string Key
    {
        get
        {
            var built = new StringBuilder();
            built.Append(From.X).Append(',').Append(From.Y);
            foreach (RitualStop stop in Stops)
            {
                built.Append('|').Append(stop.At.X).Append(',').Append(stop.At.Y);
            }

            return built.ToString();
        }
    }

    /// <summary>Every reward on the route, both of a two-reward node's included.</summary>
    public IEnumerable<string> Rewards
    {
        get
        {
            foreach (RitualStop stop in Stops)
            {
                yield return stop.First;
                if (stop.Twice)
                {
                    yield return stop.Second;
                }
            }
        }
    }
}

/// <summary>
/// Works out where a ritual line could go and what each route would pay.
/// </summary>
/// <remarks>
/// The ritual line - "Head of the King" - is drawn across several maps, and each map it takes
/// rolls a reward. The rolls are DETERMINISTIC (see <see cref="RitualMods"/>), so every route
/// the line could take can be priced before a single map is picked. That is the whole feature:
/// not "what did I get" but "which way is worth going".
///
/// PURE, and deliberately so. Everything here is a decision - which nodes may join a line, what
/// rank a candidate has, whether a route that cannot be finished should be offered - and none of
/// it can be reached through a live memory read. The reading is <c>RitualLineReader</c>'s job.
///
/// Ported from GameHelper2's Atlas plugin, including the two rules that look like bugs and are
/// not: a blocked node still OCCUPIES its rank among the candidates, and a route that cannot
/// reach full length is not offered at all.
/// </remarks>
public static class RitualPlanner
{
    /// <summary>Most routes worked out in one pass, across all starts together.</summary>
    public const int MostChains = 8_192;

    /// <summary>And the fewest each start is guaranteed, so one branchy map cannot crowd out the rest.</summary>
    public const int LeastPerStart = 32;

    /// <summary>
    /// Every route the line could take, best first.
    /// </summary>
    /// <remarks>
    /// From EVERY eligible start when no line has been begun - which is the useful case, because
    /// it answers "where should I start" rather than "what happens if I start here". Once a line
    /// exists its own end is the only start, and the question becomes where to go next.
    /// </remarks>
    /// <param name="line">The line as read, including the candidate table and the stats.</param>
    /// <param name="blocked">Maps that can never join a line - finished, unique, towers, hideouts.</param>
    /// <param name="starts">Maps that can be entered now. Ignored once the line has been begun.</param>
    /// <param name="pool">The rewards that can roll, already filtered by the player's stats.</param>
    /// <param name="length">How many maps the line takes in total.</param>
    /// <param name="worth">What each reward is worth to this player. Absent means nothing in particular.</param>
    public static IReadOnlyList<RitualChain> Plan(
        RitualLineState line,
        IReadOnlySet<(int X, int Y)> blocked,
        IReadOnlyList<(int X, int Y)> starts,
        IReadOnlyList<RitualMod> pool,
        int length,
        IReadOnlyDictionary<string, int>? worth = null)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(blocked);
        ArgumentNullException.ThrowIfNull(starts);
        ArgumentNullException.ThrowIfNull(pool);

        var chains = new List<RitualChain>();
        if (pool.Count == 0 || line.Candidates.Count == 0)
        {
            return chains;
        }

        bool drawing = line.Committed.Count > 0;

        // How many maps are already spoken for. Before the first pick, the start itself is one
        // of them - so a five-map line has four more to find, not five.
        int taken = drawing ? line.Committed.Count : 1;
        int togo = Math.Max(0, length - taken);
        if (togo == 0)
        {
            return chains;
        }

        List<(int X, int Y)> roots = drawing
            ? [line.Committed[^1]]
            : [.. starts.Where(start => !blocked.Contains(start)).OrderBy(at => at.X).ThenBy(at => at.Y)];

        if (roots.Count == 0)
        {
            return chains;
        }

        // Every start shares the line's id and pool, and a roll depends only on how many maps
        // are taken and which candidate a node is - so the same rolls repeat across starts by
        // the thousand. Remembered, the whole enumeration rolls a few dozen times.
        var rolled = new Dictionary<(uint Taken, uint Rank), (string First, string Second)>();
        (string First, string Second) Roll(uint takenSoFar, uint rank)
        {
            if (!rolled.TryGetValue((takenSoFar, rank), out (string First, string Second) already))
            {
                (RitualMod? first, RitualMod? second) =
                    RitualMods.Rewards(line.LineId, takenSoFar, rank, pool, line.SecondRewardChance);

                already = (
                    first is { } one ? RitualRewards.Short(one.Text) : string.Empty,
                    second is { } two ? RitualRewards.Short(two.Text) : string.Empty);
                rolled[(takenSoFar, rank)] = already;
            }

            return already;
        }

        int perStart = Math.Max(LeastPerStart, MostChains / roots.Count);

        foreach ((int X, int Y) root in roots)
        {
            var walked = new HashSet<(int X, int Y)>(drawing ? line.Committed : [root]);
            var stops = new List<RitualStop>();
            int fromThisStart = 0;

            Walk(root, 0);

            void Walk((int X, int Y) at, int depth)
            {
                if (chains.Count >= MostChains || fromThisStart >= perStart)
                {
                    return;
                }

                if (depth == togo)
                {
                    // Only a FULL-LENGTH route is offered. The game refuses a click that would
                    // strand the line short of its length, so a shorter branch is not a worse
                    // route - it is one nobody can walk.
                    fromThisStart++;
                    chains.Add(new RitualChain(root, [.. stops], Value(stops, worth)));
                    return;
                }

                if (!line.Candidates.TryGetValue(at, out IReadOnlyList<(int X, int Y)>? next))
                {
                    return;
                }

                // The rank a candidate has is its place among those not already on the line,
                // ordered by position - and a BLOCKED node still holds its place. Skipping it
                // before ranking would shift every candidate after it and roll the wrong
                // rewards for all of them.
                List<(int X, int Y)> ranked =
                    [.. next.Where(one => !walked.Contains(one)).OrderBy(one => one.X).ThenBy(one => one.Y)];

                var soFar = (uint)walked.Count;
                for (int rank = 0; rank < ranked.Count; rank++)
                {
                    (int X, int Y) candidate = ranked[rank];
                    if (blocked.Contains(candidate))
                    {
                        continue;
                    }

                    (string first, string second) = Roll(soFar, (uint)rank);
                    if (first.Length == 0)
                    {
                        continue;
                    }

                    walked.Add(candidate);
                    stops.Add(new RitualStop(candidate, first, second));

                    Walk(candidate, depth + 1);

                    stops.RemoveAt(stops.Count - 1);
                    walked.Remove(candidate);

                    if (chains.Count >= MostChains || fromThisStart >= perStart)
                    {
                        return;
                    }
                }
            }

            if (chains.Count >= MostChains)
            {
                break;
            }
        }

        // Best first, and by the route's own text when two are worth the same, so the list does
        // not reshuffle itself between frames.
        return [.. chains.OrderByDescending(chain => chain.Worth).ThenBy(chain => chain.Key, StringComparer.Ordinal)];
    }

    /// <summary>What a route is worth, from the user's weights. Zero when they have set none.</summary>
    private static int Value(List<RitualStop> stops, IReadOnlyDictionary<string, int>? worth)
    {
        if (worth is null || worth.Count == 0)
        {
            return 0;
        }

        int total = 0;
        foreach (RitualStop stop in stops)
        {
            if (worth.TryGetValue(stop.First, out int first))
            {
                total += first;
            }

            if (stop.Twice && worth.TryGetValue(stop.Second, out int second))
            {
                total += second;
            }
        }

        return total;
    }
}

/// <summary>
/// The part of a read ritual line the planning actually needs.
/// </summary>
/// <remarks>
/// Its own type rather than the reader's, so the planner can be handed a made-up atlas. The
/// reader's <c>RitualLine</c> converts to it, and everything worth getting wrong lives on this
/// side of that line.
/// </remarks>
/// <param name="LineId">Seeds the rolls.</param>
/// <param name="Committed">The maps already on the line, in order.</param>
/// <param name="Candidates">Which map may follow which.</param>
/// <param name="SecondRewardChance">How likely a second reward is, as a percentage.</param>
public sealed record RitualLineState(
    uint LineId,
    IReadOnlyList<(int X, int Y)> Committed,
    IReadOnlyDictionary<(int X, int Y), IReadOnlyList<(int X, int Y)>> Candidates,
    int SecondRewardChance);

/// <summary>
/// Shortens a reward to something that fits on a row.
/// </summary>
/// <remarks>
/// The game's own wording is a sentence - "Contains 4 additional packs of Monsters" - and a
/// route is five of them side by side. Unrecognised text passes through unchanged, so a league
/// that adds a reward gets a long row rather than a blank one.
/// </remarks>
public static class RitualRewards
{
    private static readonly System.Text.RegularExpressions.Regex Currency =
        new(@"^(\d+) (.+?Orbs?.*)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The short form of one reward.</summary>
    public static string Short(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        System.Text.RegularExpressions.Match currency = Currency.Match(text);
        if (currency.Success)
        {
            return $"{currency.Groups[2].Value} x{currency.Groups[1].Value}";
        }

        if (text.StartsWith("Omen of ", StringComparison.OrdinalIgnoreCase))
        {
            return "Omen: " + text["Omen of ".Length..];
        }

        if (text.StartsWith("Contains a very rare Unique", StringComparison.OrdinalIgnoreCase))
        {
            return "Very Rare Unique";
        }

        if (text.StartsWith("Contains ", StringComparison.OrdinalIgnoreCase))
        {
            return text["Contains ".Length..];
        }

        if (text.Contains("additional pack", StringComparison.OrdinalIgnoreCase))
        {
            return "+Monster Packs";
        }

        if (text.Contains("no Cost the first time", StringComparison.OrdinalIgnoreCase))
        {
            return "+Free Reroll";
        }

        if (text.Contains("additional Favour reroll", StringComparison.OrdinalIgnoreCase))
        {
            return "+1 Reroll";
        }

        if (text.Contains("reduced Tribute", StringComparison.OrdinalIgnoreCase))
        {
            return "-Reroll Cost";
        }

        if (text.Contains("increased Tribute", StringComparison.OrdinalIgnoreCase))
        {
            return "+25% Tribute";
        }

        if (text.Contains("increased number of Favours", StringComparison.OrdinalIgnoreCase))
        {
            return "+Favours";
        }

        return text;
    }

    /// <summary>Every reward the pool can roll, shortened and sorted - the list to choose from.</summary>
    public static IReadOnlyList<string> Everything(RitualMods mods)
    {
        ArgumentNullException.ThrowIfNull(mods);

        var all = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RitualMod mod in mods.All)
        {
            if (mod.Weight > 0 && mod.Text.Length > 0)
            {
                all.Add(Short(mod.Text));
            }
        }

        return [.. all];
    }
}
