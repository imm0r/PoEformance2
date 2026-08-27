namespace PoEformance.Features;

/// <summary>What a rule can ask about.</summary>
/// <remarks>
/// Stored and sent BY NAME, never by number - the settings file is meant to be hand-editable
/// and the config page shows the name, and neither survives an ordinal that shifts the next
/// time this list gains a member in the middle.
///
/// Two of the reference plugin's conditions are deliberately missing. HasDebuff and
/// DebuffTimeLeft call the same code as their buff counterparts there - the game keeps one
/// list and this project's reader records nothing that separates the two - so shipping them
/// would advertise a distinction the tool cannot make. Stat and HasStat are missing because
/// nothing here reads the player's stat block yet; a condition that always answers zero is
/// worse than one that is not offered.
/// </remarks>
public enum RuleFact
{
    InGame,
    GameFocused,
    InTown,
    InHideout,
    InMap,
    InPanel,
    Alive,
    Moving,

    LifePercent,
    ManaPercent,
    EnergyShieldPercent,
    Life,
    Mana,
    EnergyShield,

    AreaLevel,
    PlayerLevel,
    SecondsInArea,
    Speed,

    MonsterCount,
    MonsterCountWithin,
    MonsterCountAtCursor,
    RareMonsterCount,
    UniqueMonsterCount,
    RareOrUniqueMonsterCount,
    RareOrUniqueCountWithin,
    RareOrUniqueCountAtCursor,
    NearestMonster,
    NearestMonsterAtCursor,
    NearestRareMonster,
    NearestUniqueMonster,
    NearestRareOrUniqueMonster,

    HasBuff,
    BuffTimeLeft,
    BuffCharges,
    AreaContains,

    FlaskActive,
    FlaskReady,
    FlaskCharges,

    EverySeconds,
}

/// <summary>Whether a fact answers yes-or-no, or a number that gets compared.</summary>
public enum FactShape
{
    Flag,
    Number,
}

/// <summary>The parameter a fact takes, if any.</summary>
public enum FactArgument
{
    None,

    /// <summary>Part of a buff or area name, matched loosely.</summary>
    Text,

    /// <summary>A belt slot, 1-5.</summary>
    Slot,

    /// <summary>A radius in world units.</summary>
    Distance,

    /// <summary>An interval in seconds.</summary>
    Seconds,
}

/// <summary>One fact: what it is called, what shape it has, and what it takes.</summary>
/// <param name="Unit">
/// What the number means, for the editor to put beside the field. Empty for a flag.
/// </param>
/// <param name="AtCursor">
/// Whether the fact measures from where the CURSOR points rather than from the player.
/// </param>
/// <remarks>
/// AtCursor is here rather than inferred from the argument kind, which is how it started: both
/// centres take a radius in world units now, so nothing about the ARGUMENT distinguishes them
/// any more. It was the argument kind while the cursor radius was in pixels - and that pairing
/// was the bug, not the shortcut.
/// </remarks>
public sealed record FactInfo(
    RuleFact Fact,
    string Name,
    FactShape Shape,
    FactArgument Argument,
    string Unit,
    string Help,
    bool AtCursor = false);

/// <summary>
/// The one description of what a rule may ask, and the only place that answers it.
/// </summary>
/// <remarks>
/// A catalogue rather than four parallel switch statements, on the same argument as
/// <see cref="StyleCatalogue"/>: the evaluator, the expression parser, the expression printer
/// and the config page's editor all need to know which facts exist, what each is called and
/// what it takes. Four copies of that drift, and the way they drift is that a fact is
/// parseable but not editable, or editable but evaluates to nothing.
///
/// Adding a fact is therefore: a member above, a row here, and a case in
/// <see cref="Answer"/>. The build fails until the last of those exists, because the switch is
/// exhaustive over the enum.
/// </remarks>
public static class RuleFacts
{
    /// <summary>Every fact, in the order the editor lists them.</summary>
    public static IReadOnlyList<FactInfo> All { get; } =
    [
        new(RuleFact.InGame, "InGame", FactShape.Flag, FactArgument.None, "", "In an area rather than a menu or a loading screen."),
        new(RuleFact.GameFocused, "GameFocused", FactShape.Flag, FactArgument.None, "", "The game window has keyboard focus."),
        new(RuleFact.InTown, "InTown", FactShape.Flag, FactArgument.None, "", "Standing in a town."),
        new(RuleFact.InHideout, "InHideout", FactShape.Flag, FactArgument.None, "", "Standing in a hideout."),
        new(RuleFact.InMap, "InMap", FactShape.Flag, FactArgument.None, "", "Somewhere with something to fight."),
        new(RuleFact.InPanel, "InPanel", FactShape.Flag, FactArgument.None, "", "A stash, atlas or skill tree is open over the game."),
        new(RuleFact.Alive, "Alive", FactShape.Flag, FactArgument.None, "", "The character is alive and loaded."),
        new(RuleFact.Moving, "Moving", FactShape.Flag, FactArgument.None, "", "The player moved measurably since the last read."),

        new(RuleFact.LifePercent, "LifePercent", FactShape.Number, FactArgument.None, "%", "Life as a share of the UNRESERVED pool."),
        new(RuleFact.ManaPercent, "ManaPercent", FactShape.Number, FactArgument.None, "%", "Mana as a share of the UNRESERVED pool - reservations are why this is not a share of the maximum."),
        new(RuleFact.EnergyShieldPercent, "EnergyShieldPercent", FactShape.Number, FactArgument.None, "%", "Energy shield as a share of the unreserved pool."),
        new(RuleFact.Life, "Life", FactShape.Number, FactArgument.None, "", "Life the globe currently holds."),
        new(RuleFact.Mana, "Mana", FactShape.Number, FactArgument.None, "", "Mana the globe currently holds."),
        new(RuleFact.EnergyShield, "EnergyShield", FactShape.Number, FactArgument.None, "", "Energy shield currently held."),

        new(RuleFact.AreaLevel, "AreaLevel", FactShape.Number, FactArgument.None, "", "The area's monster level."),
        new(RuleFact.PlayerLevel, "PlayerLevel", FactShape.Number, FactArgument.None, "", "The character's level."),
        new(RuleFact.SecondsInArea, "SecondsInArea", FactShape.Number, FactArgument.None, "s", "Time since the area last changed."),
        new(RuleFact.Speed, "Speed", FactShape.Number, FactArgument.None, "u/s", "How fast the player is moving, in world units per second."),

        new(RuleFact.MonsterCount, "MonsterCount", FactShape.Number, FactArgument.None, "", "Live monsters ANYWHERE the game is listing them - a bubble a long way past the screen, not what is in range of a skill. For that use MonsterCountWithin."),
        new(RuleFact.MonsterCountWithin, "MonsterCountWithin", FactShape.Number, FactArgument.Distance, "", "Live monsters within a radius of the player, in world units. For scale: a melee swing reaches about 20, and 120 is roughly a screen away."),
        new(RuleFact.MonsterCountAtCursor, "MonsterCountAtCursor", FactShape.Number, FactArgument.Distance, "", "Live monsters within a radius of where the CURSOR points, in world units - the question a skill placed where you aim actually asks. Answers 0 while the pointer is off the game.", AtCursor: true),
        new(RuleFact.RareMonsterCount, "RareMonsterCount", FactShape.Number, FactArgument.None, "", "Live rare monsters anywhere the game is listing them."),
        new(RuleFact.UniqueMonsterCount, "UniqueMonsterCount", FactShape.Number, FactArgument.None, "", "Live unique monsters anywhere the game is listing them."),
        new(RuleFact.RareOrUniqueMonsterCount, "RareOrUniqueMonsterCount", FactShape.Number, FactArgument.None, "", "Live rares and uniques together, anywhere they are listed."),
        new(RuleFact.RareOrUniqueCountWithin, "RareOrUniqueCountWithin", FactShape.Number, FactArgument.Distance, "", "Live rares and uniques within a radius of the player, in world units."),
        new(RuleFact.RareOrUniqueCountAtCursor, "RareOrUniqueCountAtCursor", FactShape.Number, FactArgument.Distance, "", "Live rares and uniques within a radius of where the cursor points, in world units.", AtCursor: true),
        new(RuleFact.NearestMonster, "NearestMonster", FactShape.Number, FactArgument.None, "u", "Distance to the closest live monster. No monster at all is no answer, so every comparison says no."),
        new(RuleFact.NearestMonsterAtCursor, "NearestMonsterAtCursor", FactShape.Number, FactArgument.None, "u", "World units from where the cursor points to the nearest live monster - 'am I aiming at anything'. No answer while the pointer is off the game.", AtCursor: true),
        new(RuleFact.NearestRareMonster, "NearestRareMonster", FactShape.Number, FactArgument.None, "u", "Distance to the closest live rare."),
        new(RuleFact.NearestUniqueMonster, "NearestUniqueMonster", FactShape.Number, FactArgument.None, "u", "Distance to the closest live unique."),
        new(RuleFact.NearestRareOrUniqueMonster, "NearestRareOrUniqueMonster", FactShape.Number, FactArgument.None, "u", "Distance to the closest live rare or unique."),

        new(RuleFact.HasBuff, "HasBuff", FactShape.Flag, FactArgument.Text, "", "A buff or debuff whose name contains this is on the player."),
        new(RuleFact.BuffTimeLeft, "BuffTimeLeft", FactShape.Number, FactArgument.Text, "s", "Seconds left on that buff. A buff nobody has is no answer, not zero."),
        new(RuleFact.BuffCharges, "BuffCharges", FactShape.Number, FactArgument.Text, "", "Stacks on that buff."),
        new(RuleFact.AreaContains, "AreaContains", FactShape.Flag, FactArgument.Text, "", "The area's name or id contains this."),

        new(RuleFact.FlaskActive, "FlaskActive", FactShape.Flag, FactArgument.Slot, "", "The flask in that belt slot is still doing its job."),
        new(RuleFact.FlaskReady, "FlaskReady", FactShape.Flag, FactArgument.Slot, "", "Pressing that flask's key would actually do something."),
        new(RuleFact.FlaskCharges, "FlaskCharges", FactShape.Number, FactArgument.Slot, "", "Charges held by the flask in that belt slot."),

        new(RuleFact.EverySeconds, "EverySeconds", FactShape.Flag, FactArgument.Seconds, "", "True once per interval, on the first tick at or after each one."),
    ];

    private static readonly Dictionary<string, FactInfo> ByName = Build();

    /// <summary>
    /// What a fact is called and what it takes.
    /// </summary>
    /// <remarks>
    /// Indexed rather than searched, which is only safe because <see cref="Build"/> checks that
    /// the table is in enum order and refuses to load otherwise. A row inserted in the middle
    /// of one and not the other would otherwise describe every later fact as its neighbour -
    /// a rule about mana evaluating life, with nothing to see but a wrong answer.
    /// </remarks>
    public static FactInfo Describe(RuleFact fact) => All[(int)fact];

    /// <summary>Finds a fact by the name used in expressions and in the editor.</summary>
    public static FactInfo? Find(string name)
        => name is not null && ByName.TryGetValue(name, out FactInfo? info) ? info : null;

    /// <summary>
    /// Whether one leaf holds.
    /// </summary>
    /// <remarks>
    /// A flag answers itself. A NUMBER goes through <see cref="Satisfies"/>, and the whole
    /// point of that is what happens when the number is not known: the comparison says no,
    /// whichever way round it was written. An unreadable life pool must not satisfy "below
    /// 35", and an empty room must not satisfy "the nearest rare is at least 100 away".
    /// </remarks>
    internal static bool Holds(RuleCondition leaf, RuleState state, RuleTimers timers, string key)
    {
        FactInfo info = Describe(leaf.Fact);

        // The one fact that is a side effect: reaching it consumes the interval. Which means
        // the order of a group's children decides how often a timer advances - written down
        // rather than hidden, because it is the difference between "every 5 seconds while
        // fighting" and "every 5 seconds, and it happened to be quiet".
        if (leaf.Fact == RuleFact.EverySeconds)
        {
            return timers.Due(key, leaf.Argument);
        }

        return info.Shape == FactShape.Flag
            ? Flag(leaf, state)
            : Satisfies(Answer(leaf, state), leaf.Compare, leaf.Value);
    }

    /// <summary>Whether a number satisfies a comparison. An absent number never does.</summary>
    public static bool Satisfies(double? actual, Compare compare, double value)
    {
        if (actual is not double number)
        {
            return false;
        }

        return compare switch
        {
            Compare.AtLeast => number >= value,
            Compare.AtMost => number <= value,
            Compare.Above => number > value,
            Compare.Below => number < value,

            // Equality on a double looks wrong and is not: every number here is a count, a
            // percentage or a level - integers travelling in a double - and the editor offers
            // "is" only where that is true. A distance compared for exact equality is legal
            // and answers no, which is the honest result of asking it.
            Compare.Is => number.Equals(value),
            Compare.IsNot => !number.Equals(value),
            _ => false,
        };
    }

    private static bool Flag(RuleCondition leaf, RuleState state) => leaf.Fact switch
    {
        RuleFact.InGame => state.InGame,
        RuleFact.GameFocused => state.GameFocused,
        RuleFact.InTown => state.InTown,
        RuleFact.InHideout => state.InHideout,
        RuleFact.InMap => state.InMap,
        RuleFact.InPanel => state.InPanel,
        RuleFact.Alive => state.Alive,
        RuleFact.Moving => state.Moving,
        RuleFact.HasBuff => state.HasBuff(leaf.Text),
        RuleFact.AreaContains => state.AreaContains(leaf.Text),
        RuleFact.FlaskActive => state.FlaskActive(Slot(leaf)),
        RuleFact.FlaskReady => state.FlaskReady(Slot(leaf)),
        _ => false,
    };

    /// <summary>The number a fact answers, or null when it cannot be answered.</summary>
    public static double? Answer(RuleCondition leaf, RuleState state)
    {
        ArgumentNullException.ThrowIfNull(leaf);
        ArgumentNullException.ThrowIfNull(state);

        return leaf.Fact switch
        {
            RuleFact.LifePercent => state.Percent(VitalKind.Life),
            RuleFact.ManaPercent => state.Percent(VitalKind.Mana),
            RuleFact.EnergyShieldPercent => state.Percent(VitalKind.EnergyShield),
            RuleFact.Life => state.Current(VitalKind.Life),
            RuleFact.Mana => state.Current(VitalKind.Mana),
            RuleFact.EnergyShield => state.Current(VitalKind.EnergyShield),

            RuleFact.AreaLevel => state.AreaLevel,
            RuleFact.PlayerLevel => state.PlayerLevel,
            RuleFact.SecondsInArea => state.SecondsInArea,
            RuleFact.Speed => state.Speed,

            RuleFact.MonsterCount => state.MonsterCount,
            RuleFact.RareMonsterCount => state.RareMonsterCount,
            RuleFact.UniqueMonsterCount => state.UniqueMonsterCount,
            RuleFact.RareOrUniqueMonsterCount => state.RareOrUniqueMonsterCount,
            RuleFact.NearestMonster => state.NearestMonster,
            RuleFact.NearestRareMonster => state.NearestRareMonster,
            RuleFact.NearestUniqueMonster => state.NearestUniqueMonster,
            RuleFact.NearestRareOrUniqueMonster => state.NearestRareOrUniqueMonster,

            RuleFact.BuffTimeLeft => state.BuffTimeLeft(leaf.Text),
            RuleFact.BuffCharges => state.BuffCharges(leaf.Text),
            RuleFact.FlaskCharges => state.FlaskCharges(Slot(leaf)),

            RuleFact.MonsterCountWithin => state.MonsterCountWithin(leaf.Argument),
            RuleFact.RareOrUniqueCountWithin => state.RareOrUniqueCountWithin(leaf.Argument),
            RuleFact.MonsterCountAtCursor => state.MonsterCountAtCursor(leaf.Argument),
            RuleFact.RareOrUniqueCountAtCursor => state.RareOrUniqueCountAtCursor(leaf.Argument),
            RuleFact.NearestMonsterAtCursor => state.NearestMonsterAtCursor,

            // Every flag, plus the interval fact. Asking a flag for a number is a caller
            // mistake rather than a state of the game, and the answer that keeps it quiet is
            // the same one an unreadable pool gives.
            _ => null,
        };
    }

    private static int Slot(RuleCondition leaf) => (int)Math.Round(leaf.Argument);

    private static Dictionary<string, FactInfo> Build()
    {
        // Case-insensitive so a hand-written expression need not match the editor's casing.
        var map = new Dictionary<string, FactInfo>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < All.Count; index++)
        {
            FactInfo info = All[index];
            if ((int)info.Fact != index)
            {
                throw new InvalidOperationException(
                    $"RuleFacts.All is out of step with RuleFact at index {index}: found {info.Fact}.");
            }

            map[info.Name] = info;
        }

        if (All.Count != Enum.GetValues<RuleFact>().Length)
        {
            throw new InvalidOperationException("RuleFacts.All does not describe every RuleFact.");
        }

        return map;
    }
}
