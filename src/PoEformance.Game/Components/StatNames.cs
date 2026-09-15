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
/// AND THE DRIFT ABOVE 4290 WAS MEASURED, 2026-09-15, rather than left feared. Reading the ids the
/// game itself holds (tests/fixtures/session-2026-09-statnames.rec, StatTableSessionTests): of the
/// 148 rows a capture covers, the June extract still agreed on TEN, and the first disagreement was
/// at index 4678 - just above 4290, the highest reading anybody had checked. Every verification lay
/// below the break, which is why the file looked sound while half the table named the wrong stat.
///
/// RE-EXPORTED FROM THE CURRENT CLIENT IT AGREES EXACTLY - 27281 rows against 27281, all 148 of
/// them. So the file is fixable and was simply old. What it is not is self-maintaining: it is right
/// only while somebody remembers, and when it stops being right nothing says so, because a stale
/// name is a real stat's name one row along.
///
/// SO THE GAME ANSWERS FIRST NOW. StatTable reads a row's id out of Stats.dat, Learn puts it in
/// front of this file, and Source says which of the two spoke. The file stays behind it for a
/// session that never reaches the table.
///
/// A name is a LABEL and never a fact: an id with no row is left as its number rather than
/// guessed at, and the table drifts with the game, so a name that stops making sense means
/// the table needs extracting again, not that the reading is wrong.
/// </remarks>
public sealed class StatNames
{
    /// <summary>What memory's id has to be shifted by to index the table. See the remarks.</summary>
    private const uint TableOffset = 1;

    /// <summary>Nothing known, which is what a missing file leaves.</summary>
    /// <remarks>
    /// A FRESH ONE EVERY TIME rather than a shared singleton, now that <see cref="Learn"/> exists:
    /// a session with no data file still learns from the game, and a shared Empty would carry one
    /// session's table into everything else holding the same instance.
    /// </remarks>
    public static StatNames Empty => new([]);

    private readonly Dictionary<uint, string> _names;

    // The game's own table, once something has walked the loader's file list to it. Volatile
    // because the reader thread sets it while the interface is drawing names out of the file.
    private StatTable? _live;

    private StatNames(Dictionary<uint, string> names) => _names = names;

    /// <summary>How many names the FILE holds.</summary>
    public int Count => _names.Count;

    /// <summary>Whether the game's own table is answering, rather than the shipped file.</summary>
    public bool FromGame => Volatile.Read(ref _live) is not null;

    /// <summary>
    /// Where a name comes from, for an interface that has to say which.
    /// </summary>
    /// <remarks>
    /// The two are indistinguishable on screen until one of them is wrong, and the one that is
    /// wrong is wrong PLAUSIBLY - a neighbouring stat, never a blank. So the source is said out
    /// loud rather than left to be worked out from a name that looks fine.
    /// </remarks>
    public string Source => Volatile.Read(ref _live) is { } live
        ? $"the game ({live.Facts.Rows} rows of Stats.dat, {live.Named} read so far)"
        : _names.Count > 0
            ? $"data/stat_name_map.tsv ({_names.Count} entries) - right only while somebody re-exports it"
            : "nowhere - no file and the game's table has not been reached";

    /// <summary>
    /// Puts the game's own Stats.dat in front of the file.
    /// </summary>
    /// <remarks>
    /// IN FRONT OF rather than instead of, so a session that never reaches the table is no worse
    /// off than before. Which of the two answered is what <see cref="Source"/> says.
    /// </remarks>
    public void Learn(StatTable? table) => Volatile.Write(ref _live, table);

    /// <summary>The game's name for a stat id as it appears IN MEMORY, or null.</summary>
    /// <remarks>
    /// THE KEY IS ONE-BASED AND THE TABLE IS NOT - see the remarks on the class. The shift is
    /// applied here and nowhere else, so the live table and the file are indexed the same way.
    /// </remarks>
    public string? Of(uint memoryId)
    {
        if (memoryId < TableOffset)
        {
            return null;
        }

        uint index = memoryId - TableOffset;
        return Volatile.Read(ref _live)?.Of(index)
            ?? (_names.TryGetValue(index, out string? name) ? name : null);
    }

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
