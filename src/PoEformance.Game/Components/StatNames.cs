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
/// holds that index PLUS ONE. Established against a live character and its own character
/// sheet, every line of it, rather than argued about:
///
/// <code>
///   memory  shifted by -1                            the sheet said
///        1  level                                    Level 96
///      239  maximum_life                        1    1          - a chaos inoculation build
///      240  maximum_mana                     5415    5415
///      235  armour                           1011    1011
///      299  fire_damage_resistance_%           73    73%
///      298  cold_damage_resistance_%           75    75%
///      300  lightning_damage_resistance_%      75    75%
///      301  chaos_damage_resistance_%          -7    -7%
///     2032  uncapped_fire_damage_resistance_%  73    (73%)
///     2033  uncapped_cold_damage_resistance_%  82    (82%)
///     2034  uncapped_lightning_..._%           78    (78%)
///      566  intelligence                      566    566
///      569  dexterity                          50    50
/// </code>
///
/// Eleven numbers, exact, including the three uncapped resistances the sheet prints in
/// brackets and a maximum life of ONE. Read straight, id 1 is "item_drop_slots" with a value
/// of 96 and the resistances become fire damage modifiers - individually plausible,
/// collectively nonsense, which is exactly how an off-by-one hides.
///
/// The values above all come from ONE of the entity's two stat bags. The same stat sits in
/// both with different numbers, and reading the merged list is what made this look wrong at
/// first: their mana came back as 4,580 and their fire resistance as 55%, both from the other
/// bag, against a sheet saying 5,415 and 73%. So the bag is carried alongside every pair.
///
/// HOW FAR THE SHIFT IS VERIFIED, which matters because it is one number standing in for a
/// whole table. Every reading above is in the range 1 to 2034, and one more lands at 4290:
/// base_body_armour_physical_damage_reduction_rating = 778 against an armour total of 1011,
/// which is what a body armour contributing most of it looks like. Beyond that it is
/// EXTRAPOLATION. A row inserted anywhere in the game's table shifts everything after it, so
/// a high id can be off by a different amount than a low one, and nothing here would say so.
/// A flame wall's single stat, id 13189, resolves to
/// freeze_as_though_damage_+%_final_with_main_hand - which is not a thing a flame wall has,
/// and is either drift of exactly that kind or a stat that is genuinely there for reasons
/// nobody has chased.
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
