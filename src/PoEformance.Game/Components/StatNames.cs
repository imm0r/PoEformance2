namespace PoEformance.Game.Components;

/// <summary>
/// Turns the numbers in an entity's Stats component into the game's own stat names.
/// </summary>
/// <remarks>
/// The Stats component holds (id, value) pairs and nothing else - no names, no units - so a
/// hundred of them read as a wall of numbers. The names come out of the game's own Stats
/// table, extracted to a TSV by the AHK tool's poe_tools.py, 27,004 of them.
///
/// THE IDS ARE OFF BY ONE, and that is the whole reason this type has a comment. The table's
/// key is the CSV's 0-based row index and its header claims that is what memory holds; memory
/// holds that index PLUS ONE. Established against a live character rather than argued about,
/// and by more than one coincidence:
///
/// <code>
///   memory  value                shifted by -1
///        1     96   level                                 - the game's own UI said Level 96
///      118    309   base_maximum_life
///      122    991   base_maximum_mana
///      236   4000   movement_velocity_+permyriad          - 4000 permyriad, i.e. +40%
///      240   4580   maximum_mana
///      298     75   cold_damage_resistance_%              - at the cap
///      299     55   fire_damage_resistance_%              - not at the cap
///      300     75   lightning_damage_resistance_%         - at the cap
///      301     -7   chaos_damage_resistance_%
///      146     -7   base_chaos_damage_resistance_%        - the same -7, from another stat
///     2035     -7   uncapped_chaos_damage_resistance_%    - and again from a third
/// </code>
///
/// Read straight, id 1 is "item_drop_slots" with a value of 96 and 298 is a fire damage
/// modifier - a set of numbers that is individually plausible and collectively nonsense,
/// which is exactly how an off-by-one hides.
///
/// A name is a LABEL and never a fact: an id with no row is left as its number rather than
/// guessed at, and the table drifts with the game, so a name that stops making sense means
/// the table needs extracting again, not that the reading is wrong.
/// </remarks>
public sealed class StatNames
{
    /// <summary>What memory's id has to be shifted by to index the table. See the remarks.</summary>
    private const uint TableOffset = 1;

    public static StatNames Empty { get; } = new([]);

    private readonly Dictionary<uint, string> _names;

    private StatNames(Dictionary<uint, string> names) => _names = names;

    /// <summary>How many names were loaded.</summary>
    public int Count => _names.Count;

    /// <summary>The game's name for a stat id as it appears IN MEMORY, or null.</summary>
    public string? Of(uint memoryId)
        => memoryId >= TableOffset && _names.TryGetValue(memoryId - TableOffset, out string? name)
            ? name
            : null;

    /// <summary>Loads the TSV. A missing file is not an error - the ids still read.</summary>
    public static StatNames Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return Empty;
        }

        var names = new Dictionary<uint, string>();
        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            int tab = line.IndexOf('\t', StringComparison.Ordinal);
            if (tab <= 0 || !uint.TryParse(line.AsSpan(0, tab), out uint id))
            {
                continue;
            }

            names[id] = line[(tab + 1)..].Trim();
        }

        return new StatNames(names);
    }
}
