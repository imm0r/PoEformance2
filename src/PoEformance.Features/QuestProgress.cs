using PoEformance.Game.Files;

namespace PoEformance.Features;

/// <summary>One step of a quest, as QuestStates declares it.</summary>
/// <param name="Order">
/// Its place in the quest's sequence, COUNTING DOWN: the last state of a quest is Order 0.
/// </param>
/// <param name="Present">Flag rows that must be set for this step to be the current one.</param>
/// <param name="Missing">Flag rows that must NOT be set.</param>
/// <param name="Text">
/// The long form: what to do and usually WHERE. "The Devourer lives underground in a Mud
/// Burrow. Find it."
/// </param>
/// <param name="Message">
/// The short form, and the one the game's own quest panel renders.
/// </param>
/// <remarks>
/// WHICH IS WHICH WAS MEASURED, by showing both and holding the window next to the game.
/// Message matched the panel word for word on two quests - "Find the Red Vale" and "Search for
/// the meaning of the Runes etched into the Tree of Souls" - while Text was the longer sentence
/// in every case. The schema's names do not say this; the two columns are just a string each.
/// </remarks>
public sealed record QuestStep(
    int Order,
    IReadOnlyList<int> Present,
    IReadOnlyList<int> Missing,
    string Text,
    string Message)
{
    /// <summary>The objective as the game words it, falling back to the long form.</summary>
    public string Line => Message.Length > 0 ? Message : Text;

    /// <summary>The long form, when it says more than the line above it.</summary>
    public string Detail
        => Text.Length > 0 && !string.Equals(Text, Line, StringComparison.Ordinal) ? Text : string.Empty;

    /// <summary>Whether this step's conditions hold for a given set of flags.</summary>
    public bool Holds(IReadOnlySet<int> set)
    {
        ArgumentNullException.ThrowIfNull(set);
        return Present.All(set.Contains) && !Missing.Any(set.Contains);
    }
}

/// <summary>A quest and where the character stands in it.</summary>
/// <param name="Now">The step whose conditions hold, or null when none does.</param>
/// <param name="Next">The step after it, which is what completing the current one leads to.</param>
/// <param name="Holding">
/// EVERY step whose conditions hold, not just the chosen one.
/// </param>
/// <remarks>
/// The data intends exactly one: a later step asks for flags an earlier one does not have, and
/// FlagsMissing rules out the ones already passed. So more than one holding is not an
/// ambiguity to resolve quietly - it is the sign that a condition is not being read, and it is
/// carried here rather than hidden so the window can say so.
/// </remarks>
public sealed record QuestState(
    int Row,
    string Id,
    string Name,
    int Act,
    QuestStep? Now,
    QuestStep? Next,
    IReadOnlyList<QuestStep> Steps,
    IReadOnlyList<QuestStep> Holding)
{
    /// <summary>True when the last step of the quest is the one in force.</summary>
    public bool Complete => Now is not null && Next is null && Steps.Count > 0;

    /// <summary>What to do, in the game's own words, or an empty string when it has none.</summary>
    public string Objective => Now?.Line ?? string.Empty;

    /// <summary>The fuller sentence behind the objective, which usually says where to go.</summary>
    public string Detail => Now?.Detail ?? string.Empty;

    /// <summary>How many steps sit behind the current one.</summary>
    public int Passed => Now is null ? 0 : Math.Max(At, 0);

    /// <summary>Where the current step sits in the sequence, or -1.</summary>
    private int At
    {
        get
        {
            for (var i = 0; i < Steps.Count; i++)
            {
                if (ReferenceEquals(Steps[i], Now))
                {
                    return i;
                }
            }

            return -1;
        }
    }

    /// <summary>
    /// The current step and everything after it - what is actually left to do.
    /// </summary>
    /// <remarks>
    /// A PATH, NOT A CHECKLIST, and the difference is worth stating because the number invites
    /// the wrong reading. QuestStates is a state MACHINE: a quest with branches carries a state
    /// per branch, so The Runeseeker has 87 of them and most are variations of "Search the
    /// region for more Runestones" for the different regions it can be done in. The remaining
    /// count is therefore an upper bound on the states still ahead, not a tally of distinct
    /// things to go and do - which is exactly why the steps are shown in the game's own words
    /// rather than counted down to a number.
    /// </remarks>
    public IReadOnlyList<QuestStep> Remaining
        => Now is null || At < 0 ? Steps : [.. Steps.Skip(At)];
}

/// <summary>What the whole read produced, including why it produced nothing.</summary>
public sealed record QuestOutlook(IReadOnlyList<QuestState> Quests, IReadOnlyList<string> Notes)
{
    /// <summary>Nothing read, and the reasons.</summary>
    public static QuestOutlook Nothing(params string[] notes) => new([], notes);

    /// <summary>Quests with a step in force that is not the last one.</summary>
    public IEnumerable<QuestState> Active => Quests.Where(q => q.Now is not null && !q.Complete);

    /// <summary>
    /// Quests where more than one step holds at once.
    /// </summary>
    /// <remarks>
    /// ORDINARY, as it turns out, and kept because it took a screenshot of it to work out why.
    /// Most states declare only the flags that must be PRESENT and none that must be absent,
    /// so every step the character has passed goes on holding - The Runeseeker had three at
    /// once, all with one present flag and no missing one. The furthest along is the answer,
    /// which is what the progression order decides. Still counted, because a sudden jump in
    /// this number is what a mis-read condition column would look like.
    /// </remarks>
    public IEnumerable<QuestState> Ambiguous => Quests.Where(q => q.Holding.Count > 1);
}

/// <summary>
/// What a character still has to do, by joining the flags they have set to the quest states.
/// </summary>
/// <remarks>
/// THE JOIN IS DIRECT, and that is the whole reason this is possible at all. QuestStates
/// declares two arrays of references into QuestFlags - the flags that must be present and the
/// flags that must be absent for a step to be current - and the set read out of the game is a
/// bitset indexed by QuestFlags ROW NUMBER. The foreign key and the bit index are the same
/// number, so there is no name matching and nothing to get wrong in between.
///
/// This is the state machine the game's own quest tracker runs on, so the answer is the same
/// answer the tracker gives - for every quest at once rather than the tracked one, and
/// readable from a recording afterwards.
///
/// WHAT IT CANNOT DO, said here because the feature invites the assumption: the Text column is
/// what GGG wrote, and nothing derives more than that. "Find the Hooded One" is as specific as
/// the data gets; which room he is in is not in any table.
/// </remarks>
public static class QuestProgress
{
    /// <summary>Most steps a quest is believed to have, as a guard on a bad layout.</summary>
    public const int MostSteps = 256;

    /// <summary>
    /// Joins the tables to a set of flag rows.
    /// </summary>
    /// <param name="setFlags">QuestFlags row numbers the character has set.</param>
    public static QuestOutlook Read(
        LoadedTable quests,
        LoadedTable states,
        LoadedTable flags,
        QuestTableLayouts layouts,
        IReadOnlyCollection<int> setFlags)
    {
        ArgumentNullException.ThrowIfNull(quests);
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(flags);
        ArgumentNullException.ThrowIfNull(layouts);
        ArgumentNullException.ThrowIfNull(setFlags);

        int questId = layouts.OffsetOf("Quest", "Id");
        int questName = layouts.OffsetOf("Quest", "Name");
        int questAct = layouts.OffsetOf("Quest", "Act");
        int stateQuest = layouts.OffsetOf("QuestStates", "Quest");
        int stateOrder = layouts.OffsetOf("QuestStates", "Order");
        int statePresent = layouts.OffsetOf("QuestStates", "FlagsPresent");
        int stateMissing = layouts.OffsetOf("QuestStates", "FlagsMissing");
        int stateText = layouts.OffsetOf("QuestStates", "Text");
        int stateMessage = layouts.OffsetOf("QuestStates", "Message");

        if (questId < 0 || questName < 0 || stateQuest < 0 || statePresent < 0 || stateText < 0)
        {
            return QuestOutlook.Nothing("the vendored column list does not name the columns this needs");
        }

        var set = setFlags.ToHashSet();
        var byQuest = new Dictionary<int, List<QuestStep>>();
        var unresolved = 0;

        for (var row = 0; row < states.File.Rows; row++)
        {
            int quest = states.File.Reference(row, stateQuest).RowIn(quests.File.Rows);
            if (quest < 0)
            {
                unresolved++;
                continue;
            }

            var step = new QuestStep(
                states.File.I32(row, stateOrder),
                Rows(states.File.References(row, statePresent), flags.File.Rows),
                stateMissing < 0 ? [] : Rows(states.File.References(row, stateMissing), flags.File.Rows),
                states.File.Text(row, stateText),
                stateMessage < 0 ? string.Empty : states.File.Text(row, stateMessage));

            if (!byQuest.TryGetValue(quest, out List<QuestStep>? steps))
            {
                byQuest[quest] = steps = [];
            }

            if (steps.Count < MostSteps)
            {
                steps.Add(step);
            }
        }

        var made = new List<QuestState>(byQuest.Count);
        foreach ((int quest, List<QuestStep> steps) in byQuest)
        {
            // DESCENDING, because Order counts DOWN. Read off the game with the flags shown:
            // "Finding the Forge" runs order 4 "Speak to Renly in Clearfell Encampment", 3
            // "Travel to Ogham Village and find Renly's tools", 2 "Find Renly's tools", 1
            // "Bring the tools back to Renly", 0 "Quest Complete" - and "The Hunt Begins" the
            // same way. Sorting the other way ran every quest's progress backwards: a finished
            // quest reported its completion state as CURRENT with an earlier step as "then",
            // and a quest genuinely in progress reported itself finished and was hidden, which
            // is why two quests the game's own tracker listed were missing from this window.
            steps.Sort((a, b) => b.Order.CompareTo(a.Order));

            // The LAST step whose conditions hold, in progression order. Steps are cumulative -
            // an early one asks for flags a later one also has - so the earliest match is where
            // the character has already been, and the furthest is where they are.
            List<QuestStep> holding = [.. steps.Where(s => s.Holds(set))];
            QuestStep? now = holding.Count > 0 ? holding[^1] : null;

            // By POSITION rather than by comparing Order, so the direction lives in the sort
            // above and in one place only.
            int at = now is null ? -1 : steps.IndexOf(now);
            QuestStep? next = at < 0
                ? steps.FirstOrDefault()
                : at + 1 < steps.Count ? steps[at + 1] : null;

            made.Add(new QuestState(
                quest,
                quests.File.Text(quest, questId),
                quests.File.Text(quest, questName),
                questAct < 0 ? 0 : quests.File.I32(quest, questAct),
                now,
                next,
                steps,
                holding));
        }

        made.Sort((a, b) => a.Act != b.Act ? a.Act.CompareTo(b.Act) : string.CompareOrdinal(a.Name, b.Name));

        var notes = new List<string> { quests.Say, states.Say, flags.Say };
        if (unresolved > 0)
        {
            // Worth saying rather than swallowing: a quest reference that resolves to no row
            // is the first symptom of the two halves of a foreign reference being the other
            // way round in the file than they are in memory.
            notes.Add($"{unresolved} of {states.File.Rows} states named no quest row");
        }

        return new QuestOutlook(made, notes);
    }

    /// <summary>The flag rows an array column names, dropping the ones that name nothing.</summary>
    private static IReadOnlyList<int> Rows(IReadOnlyList<DatReference> references, int rows)
    {
        var made = new List<int>(references.Count);
        foreach (DatReference reference in references)
        {
            int row = reference.RowIn(rows);
            if (row >= 0)
            {
                made.Add(row);
            }
        }

        return made;
    }

    /// <summary>The Id of a flag row, for showing a condition by name.</summary>
    public static string FlagId(LoadedTable flags, QuestTableLayouts layouts, int row)
    {
        ArgumentNullException.ThrowIfNull(flags);
        ArgumentNullException.ThrowIfNull(layouts);

        int at = layouts.OffsetOf("QuestFlags", "Id");
        return at < 0 ? string.Empty : flags.File.Text(row, at);
    }
}
