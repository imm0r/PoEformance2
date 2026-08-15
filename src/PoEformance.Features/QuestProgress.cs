using PoEformance.Game.Files;

namespace PoEformance.Features;

/// <summary>One step of a quest, as QuestStates declares it.</summary>
/// <param name="Order">Its place in the quest's sequence.</param>
/// <param name="Present">Flag rows that must be set for this step to be the current one.</param>
/// <param name="Missing">Flag rows that must NOT be set.</param>
/// <param name="Text">The tracker line - what the game itself shows as the objective.</param>
public sealed record QuestStep(
    int Order,
    IReadOnlyList<int> Present,
    IReadOnlyList<int> Missing,
    string Text,
    string Message)
{
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
    public string Objective => Now?.Text ?? string.Empty;
}

/// <summary>What the whole read produced, including why it produced nothing.</summary>
public sealed record QuestOutlook(IReadOnlyList<QuestState> Quests, IReadOnlyList<string> Notes)
{
    /// <summary>Nothing read, and the reasons.</summary>
    public static QuestOutlook Nothing(params string[] notes) => new([], notes);

    /// <summary>Quests with a step in force that is not the last one.</summary>
    public IEnumerable<QuestState> Active => Quests.Where(q => q.Now is not null && !q.Complete);

    /// <summary>
    /// Quests where more than one step holds, which the data should never produce.
    /// </summary>
    /// <remarks>
    /// The count that says whether the READ is right, as opposed to whether it produced
    /// something. A handful is a quest with genuinely parallel steps; most of them is a
    /// condition column not being read, and the two look identical from any single row.
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
            steps.Sort((a, b) => a.Order.CompareTo(b.Order));

            // The LAST step whose conditions hold, not the first. Steps are cumulative - an
            // early one asks for flags a later one also has - so the earliest match is where
            // the character has already been, and the latest is where they are.
            List<QuestStep> holding = [.. steps.Where(s => s.Holds(set))];
            QuestStep? now = holding.Count > 0 ? holding[^1] : null;
            QuestStep? next = now is null
                ? steps.FirstOrDefault()
                : steps.FirstOrDefault(s => s.Order > now.Order);

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
