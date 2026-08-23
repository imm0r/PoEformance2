namespace PoEformance.Features;

/// <summary>One of a pin's flag conditions, and whether this character meets it.</summary>
/// <param name="Column">Which column it came from, because their meanings are not known apart.</param>
/// <param name="Rows">The QuestFlags rows it names.</param>
/// <param name="Held">Those of them the character has set.</param>
public sealed record PinCondition(string Column, IReadOnlyList<int> Rows, IReadOnlyList<int> Held)
{
    /// <summary>True when every flag this condition names is set.</summary>
    public bool Met => Rows.Count > 0 && Held.Count == Rows.Count;

    /// <summary>The flags it names that are NOT set - what would still have to happen.</summary>
    public IEnumerable<int> Wanting => Rows.Where(row => !Held.Contains(row));
}

/// <summary>A world-map pin and how its conditions stand.</summary>
/// <param name="Name">Its display name - "Mud Burrow", "The Red Vale".</param>
public sealed record MapPin(int Row, string Id, string Name, int Act, IReadOnlyList<PinCondition> Conditions)
{
    /// <summary>Conditions that name at least one flag. The empty ones say nothing either way.</summary>
    public IEnumerable<PinCondition> Real => Conditions.Where(c => c.Rows.Count > 0);

    /// <summary>True when EVERY condition that names flags is met.</summary>
    public bool All => Real.Any() && Real.All(c => c.Met);

    /// <summary>True when AT LEAST ONE is.</summary>
    public bool Any => Real.Any(c => c.Met);

    /// <summary>True when the pin declares no flag conditions at all.</summary>
    public bool Unconditional => !Real.Any();
}

/// <summary>
/// The world-map pins, read against the flags a character has set.
/// </summary>
/// <remarks>
/// WHAT THE COLUMNS MEAN IS NOT KNOWN, and this is built so that it can be found out rather
/// than assumed. MapPins carries five flag columns - QuestFlags1, QuestFlags2 and QuestFlags3
/// as arrays, QuestFlag1 and QuestFlag2 as single references - and unlike QuestStates, whose
/// two are named FlagsPresent and FlagsMissing, nothing here says which way any of them points.
/// They could all be required, they could be alternatives, one of them could be an exclusion.
///
/// So no rule is baked in. Every condition is carried with the flags it names and the ones the
/// character holds, and BOTH readings are offered as counts - "every condition met" and "any
/// condition met". Held against the pins the game actually draws, whichever count matches is
/// the answer, and it takes one look rather than an argument.
///
/// The useful half either way is the INVERSE: for a pin that is not showing, the flags it is
/// waiting on, by name. That is not visible in the game at all - the map simply has nothing
/// there - and it is the same join the quest steps use.
/// </remarks>
public static class MapPinProgress
{
    /// <summary>The flag columns, in the order MapPins declares them.</summary>
    /// <remarks>
    /// Named rather than discovered because the column list is vendored anyway, and a column
    /// that a schema refresh renames should drop out with the rest of its layout rather than
    /// be guessed at from its neighbours.
    /// </remarks>
    public static IReadOnlyList<string> FlagColumns { get; } =
        ["QuestFlags1", "QuestFlags2", "QuestFlags3", "QuestFlag1", "QuestFlag2"];

    /// <summary>Most pins read, as a guard on a layout that has drifted into nonsense.</summary>
    public const int MostPins = 20_000;

    /// <summary>Reads every pin with its conditions resolved against a set of flags.</summary>
    public static IReadOnlyList<MapPin> Read(
        LoadedTable pins,
        LoadedTable flags,
        QuestTableLayouts layouts,
        IReadOnlyCollection<int> setFlags)
    {
        ArgumentNullException.ThrowIfNull(pins);
        ArgumentNullException.ThrowIfNull(flags);
        ArgumentNullException.ThrowIfNull(layouts);
        ArgumentNullException.ThrowIfNull(setFlags);

        int idAt = layouts.OffsetOf("MapPins", "Id");
        int nameAt = layouts.OffsetOf("MapPins", "Name");
        int actAt = layouts.OffsetOf("MapPins", "Act");
        if (idAt < 0 || nameAt < 0)
        {
            return [];
        }

        // Where each flag column sits, and whether it is an array - a single reference is one
        // row and an array is many, and reading one as the other finds nothing rather than
        // something wrong, which is the safer half but still no use.
        var columns = new List<(string Name, int At, bool Array)>();
        foreach (string column in FlagColumns)
        {
            int at = layouts.OffsetOf("MapPins", column);
            if (at >= 0)
            {
                columns.Add((column, at, layouts.IsArray("MapPins", column)));
            }
        }

        var set = setFlags.ToHashSet();
        var made = new List<MapPin>(Math.Min(pins.File.Rows, MostPins));

        for (var row = 0; row < pins.File.Rows && made.Count < MostPins; row++)
        {
            var conditions = new List<PinCondition>(columns.Count);
            foreach ((string name, int at, bool array) in columns)
            {
                List<int> rows = [];
                if (array)
                {
                    foreach (Game.Files.DatReference reference in pins.File.References(row, at))
                    {
                        int flag = reference.RowIn(flags.File.Rows);
                        if (flag >= 0)
                        {
                            rows.Add(flag);
                        }
                    }
                }
                else
                {
                    int flag = pins.File.Reference(row, at).RowIn(flags.File.Rows);
                    if (flag >= 0)
                    {
                        rows.Add(flag);
                    }
                }

                conditions.Add(new PinCondition(name, rows, [.. rows.Where(set.Contains)]));
            }

            made.Add(new MapPin(
                row,
                pins.File.Text(row, idAt),
                pins.File.Text(row, nameAt),
                actAt < 0 ? 0 : pins.File.I32(row, actAt),
                conditions));
        }

        return made;
    }
}
