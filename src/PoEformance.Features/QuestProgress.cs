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
/// <param name="Places">
/// Where the step points, by name - the world-map pins it carries.
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
    string Message,
    IReadOnlyList<string> Places)
{
    /// <summary>The places this step points at, as one line, or empty when it names none.</summary>
    public string Where => Places.Count == 0 ? string.Empty : string.Join(", ", Places);

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

/// <summary>
/// One stretch of a quest's route: an objective, and every state that words it the same way.
/// </summary>
/// <param name="States">
/// How many states carry this line. More than one means the quest branches here.
/// </param>
/// <param name="Places">Every place any of those states points at, de-duplicated.</param>
/// <remarks>
/// THIS IS WHAT MAKES A STATE MACHINE READ AS A ROUTE. QuestStates carries a state per branch,
/// so The Runeseeker has 87 of them and most are the same sentence for the different regions
/// the quest can be done in. Listed one per line that is a wall; folded, it is one line with
/// the regions named beside it, which is both shorter AND says more - the sentence never
/// changed, the place is the part that did.
///
/// Only CONSECUTIVE states fold, so the order of the route is never rearranged to make the
/// folding look better. Two stretches that happen to share a sentence with something else in
/// between stay two stretches, which is the honest reading: the quest really does come back to
/// it.
/// </remarks>
public sealed record QuestLeg(string Line, string Detail, int States, IReadOnlyList<string> Places)
{
    /// <summary>The places as one line, or empty when it names none.</summary>
    public string Where => Places.Count == 0 ? string.Empty : string.Join(", ", Places);

    /// <summary>True when more than one state words this objective the same way.</summary>
    public bool Branches => States > 1;
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

    /// <summary>What is left to do, with the branch states folded into one line each.</summary>
    public IReadOnlyList<QuestLeg> Route => Fold(Remaining);

    /// <summary>
    /// Folds runs of states that word their objective identically into one leg each.
    /// </summary>
    /// <remarks>
    /// States with NO words at all are dropped rather than folded. They cannot be shown - there
    /// is nothing to show - and left in they would split a run of identical lines in two, so a
    /// quest with a wordless state in the middle of its branches would fold into two legs
    /// saying the same sentence.
    /// </remarks>
    public static IReadOnlyList<QuestLeg> Fold(IReadOnlyList<QuestStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        var made = new List<QuestLeg>();
        List<QuestStep>? run = null;

        foreach (QuestStep step in steps)
        {
            if (step.Line.Length == 0)
            {
                continue;
            }

            if (run is not null && string.Equals(run[0].Line, step.Line, StringComparison.Ordinal))
            {
                run.Add(step);
                continue;
            }

            if (run is not null)
            {
                made.Add(Leg(run));
            }

            run = [step];
        }

        if (run is not null)
        {
            made.Add(Leg(run));
        }

        return made;
    }

    private static QuestLeg Leg(List<QuestStep> run)
    {
        // Order-preserving and de-duplicated. The places are the part that DIFFERS across a
        // fold - the same sentence for a different region - so losing them to the fold would
        // throw away the only thing the folded states were not agreeing about.
        var places = new List<string>();
        foreach (QuestStep step in run)
        {
            foreach (string place in step.Places)
            {
                if (!places.Contains(place))
                {
                    places.Add(place);
                }
            }
        }

        // The first state that HAS a long form, not the first state's. Branch states routinely
        // leave it empty on some of their number and fill it on others.
        string detail = run.Select(s => s.Detail).FirstOrDefault(d => d.Length > 0) ?? string.Empty;

        return new QuestLeg(run[0].Line, detail, run.Count, places);
    }
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
    /// <param name="pins">
    /// MapPins, for the "where" of a step. Optional: without it the steps still read, they
    /// simply name no place, which is better than not reading them at all.
    /// </param>
    public static QuestOutlook Read(
        LoadedTable quests,
        LoadedTable states,
        LoadedTable flags,
        QuestTableLayouts layouts,
        IReadOnlyCollection<int> setFlags,
        LoadedTable? pins = null)
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
        int statePins = layouts.OffsetOf("QuestStates", "MapPinsKeys");
        int statePin = layouts.OffsetOf("QuestStates", "MapPinsKey");
        int pinName = layouts.OffsetOf("MapPins", "Name");

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
                stateMessage < 0 ? string.Empty : states.File.Text(row, stateMessage),
                Places(states.File, row, statePins, statePin, pins, pinName));

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
        foreach ((int quest, List<QuestStep> read) in byQuest)
        {
            // DESCENDING, because Order counts DOWN. Read off the game with the flags shown:
            // "Finding the Forge" runs order 4 "Speak to Renly in Clearfell Encampment", 3
            // "Travel to Ogham Village and find Renly's tools", 2 "Find Renly's tools", 1
            // "Bring the tools back to Renly", 0 "Quest Complete" - and "The Hunt Begins" the
            // same way. Sorting the other way ran every quest's progress backwards: a finished
            // quest reported its completion state as CURRENT with an earlier step as "then",
            // and a quest genuinely in progress reported itself finished and was hidden, which
            // is why two quests the game's own tracker listed were missing from this window.
            //
            // STABLE, and by OrderByDescending rather than List.Sort for that reason alone.
            // Branch states share an Order - they are alternatives at the same stage - and
            // List.Sort is an introsort, so it puts ties in an arbitrary relative order. Two
            // things downstream depend on that order not being arbitrary: which of several
            // holding steps is picked as the current one, and which states end up adjacent for
            // the route to fold. Row order is as good a tiebreak as any; being the SAME one
            // every time is the part that matters.
            List<QuestStep> steps = [.. read.OrderByDescending(s => s.Order)];

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

    /// <summary>
    /// The places a step points at, by name.
    /// </summary>
    /// <remarks>
    /// BOTH PIN COLUMNS, because a state may use either: QuestStates carries MapPinsKeys as an
    /// array and MapPinsKey as a single reference, and which one a state fills in is not
    /// something to assume. Names are de-duplicated - a state that lists the same place twice
    /// would otherwise say so on screen.
    ///
    /// MapPins carries NO coordinates. Name, WorldArea and Act is all there is, so a pin
    /// answers WHICH PLACE and not which point - which is what a pin on a world map is. This
    /// cannot become a marker in the world; it is the sentence "and it is over there".
    /// </remarks>
    private static IReadOnlyList<string> Places(
        DatFile states, int row, int arrayAt, int singleAt, LoadedTable? pins, int nameAt)
    {
        if (pins is null || nameAt < 0)
        {
            return [];
        }

        var rows = new List<int>();
        if (arrayAt >= 0)
        {
            rows.AddRange(Rows(states.References(row, arrayAt), pins.File.Rows));
        }

        if (singleAt >= 0)
        {
            int one = states.Reference(row, singleAt).RowIn(pins.File.Rows);
            if (one >= 0)
            {
                rows.Add(one);
            }
        }

        var named = new List<string>(rows.Count);
        foreach (int pin in rows.Distinct())
        {
            string name = pins.File.Text(pin, nameAt);
            if (name.Length > 0 && !named.Contains(name))
            {
                named.Add(name);
            }
        }

        return named;
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
