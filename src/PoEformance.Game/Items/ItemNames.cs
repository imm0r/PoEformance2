using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoEformance.Game.Items;

/// <summary>What one stat is, and how the game words it.</summary>
/// <param name="Id">Its own name - <c>maximum_life</c>. Always known.</param>
/// <param name="Text">
/// The line the game writes, with <c>{0}</c> where the number goes. Empty for the many stats
/// the game never shows.
/// </param>
/// <param name="Argument">Which placeholder this stat fills, for the lines built from two.</param>
public readonly record struct StatMeaning(string Id, string Text, int Argument)
{
    /// <summary>This stat with its value in it, or the raw id when there is no wording.</summary>
    /// <remarks>
    /// FALLS BACK TO THE ID rather than to nothing. An unworded stat is still a fact about the
    /// item, and "maximum_life 79" is a great deal more use than a blank row - especially to
    /// somebody reverse-engineering, which is what this tool is for.
    /// </remarks>
    public string Say(int value)
    {
        if (Text.Length == 0)
        {
            return $"{Id} {value}";
        }

        // Only this stat's own placeholder is filled. A line built from two stats keeps the
        // other's marker, because guessing at it would put this number in the wrong half.
        //
        // THE PLACEHOLDERS CARRY A FORMAT: "{0:+d}" means show the sign, which is how "+79 to
        // maximum Life" gets its plus. Replacing the bare "{0}" alone leaves a thousand of the
        // game's own lines untouched and reading as though the table were missing them.
        string plain = $"{{{Argument}}}";
        if (Text.Contains(plain, StringComparison.Ordinal))
        {
            return Text.Replace(plain, value.ToString(), StringComparison.Ordinal);
        }

        return Text
            .Replace($"{{{Argument}:+d}}", value >= 0 ? $"+{value}" : value.ToString(), StringComparison.Ordinal)
            .Replace($"{{{Argument}:d}}", value.ToString(), StringComparison.Ordinal)
            .Replace($"{{{Argument}:-d}}", value.ToString(), StringComparison.Ordinal);
    }
}

/// <summary>What one mod is called.</summary>
/// <param name="Name">The affix - "of the Bear". Empty when the table has not heard of it.</param>
/// <param name="Kind">"prefix", "suffix" or "unique". Empty when unknown.</param>
public readonly record struct ModMeaning(string Name, string Kind);

/// <summary>
/// Turns the numbers on an item into what the game says about it.
/// </summary>
/// <remarks>
/// An item in memory is a metadata path, a list of mod ids and a list of (stat row, value)
/// pairs. None of that is readable, and all of it is knowledge about a game that changes every
/// league - so it is DATA, extracted from the game's own tables by the AHK tool's scripts and
/// shipped here.
///
/// THE STAT KEY IS A ROW NUMBER, not an id - which is why this table is keyed by number and why
/// it has 27,000 entries: it has to cover every row, not just the ones an item can carry. It is
/// the row number PLUS ONE, and <see cref="Stat"/> is where that is dealt with and explained.
/// </remarks>
public sealed class ItemNames
{
    /// <summary>What memory's key has to be shifted by to index the table. See <see cref="Stat"/>.</summary>
    private const int TableOffset = 1;

    private readonly IReadOnlyDictionary<int, StatMeaning> _stats;
    private readonly IReadOnlyDictionary<string, StatMeaning> _worded;
    private readonly IReadOnlyDictionary<string, ModMeaning> _mods;
    private readonly IReadOnlyDictionary<string, string> _bases;
    private readonly IReadOnlyDictionary<string, string> _uniques;

    // The game's own halves of the join, handed in after construction because they arrive from
    // two different places at two different moments - see Learn. Volatile because the reader
    // thread installs them while the interface may be asking.
    private Components.StatTable? _live;
    private Components.StatDescriptions? _sentences;
    private BaseItemTable? _baseItems;
    private ModTable? _modNames;

    private ItemNames(
        IReadOnlyDictionary<int, StatMeaning> stats,
        IReadOnlyDictionary<string, StatMeaning> worded,
        IReadOnlyDictionary<string, ModMeaning> mods,
        IReadOnlyDictionary<string, string> bases,
        IReadOnlyDictionary<string, string> uniques)
    {
        _stats = stats;
        _worded = worded;
        _mods = mods;
        _bases = bases;
        _uniques = uniques;
    }

    /// <summary>Nothing known, which is what a missing file leaves.</summary>
    public static ItemNames Empty { get; } = new(
        new Dictionary<int, StatMeaning>(),
        new Dictionary<string, StatMeaning>(StringComparer.Ordinal),
        new Dictionary<string, ModMeaning>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>How many stats, mods, base types and uniques are described.</summary>
    public (int Stats, int Mods, int Bases, int Uniques) Counts
        => (_stats.Count, _mods.Count, _bases.Count, _uniques.Count);

    /// <summary>
    /// What a stat means, from the key AS MEMORY HOLDS IT. Unknown keys keep their number.
    /// </summary>
    /// <remarks>
    /// THE KEY IS ONE-BASED AND THE TABLE IS NOT, which is the whole reason this is not a plain
    /// dictionary lookup. A StatPair in memory holds the Stats.dat row index PLUS ONE; the
    /// shipped table is keyed by the row index itself, because that is what the extractor writes.
    /// Reading one as the other is off by exactly one row - the worst kind of wrong, because
    /// every line still reads like a real mod, just the neighbouring one.
    ///
    /// WHAT IT LOOKED LIKE: a Dense Medium Mana Flask of the Constant, whose two explicit mods
    /// the game words "42% increased Recovery rate" and "25% increased Charges gained", listed
    /// here as "+42 seconds of Recovery" and "25% increased Amount Recovered". Rows 18 and 382
    /// against the correct 17 and 381: both still flask stats, both still plausible, both the
    /// row after the right one. The mod NAMES were right throughout - they are keyed by the
    /// mod's own id, which does not shift - so nothing about the item looked broken.
    ///
    /// The same shift is in <see cref="PoEformance.Game.Components.StatNames"/>, established
    /// there against a live character sheet, and the AHK tool applies it too: "In-memory
    /// StatPair keys are 1-based Stats.dat row indices" - ahk/TreeView_StatsFormatting.ahk,
    /// ResolveStatDisplayName. Its stat_name_map.tsv is byte-identical to the table this file is
    /// built from, so both tools index the same rows the same way.
    ///
    /// NOT A STALE TABLE, which is what it looked like first and reads the same from the symptom:
    /// a league inserting one row near the front would put these two out by exactly one as well.
    /// Three things rule it out. The AHK tool ships a BYTE-IDENTICAL stat table, applies the
    /// shift, and words this flask the way the game does. All twelve of the live-verified
    /// readings recorded in StatNames still land on their own rows in this file. And this lookup
    /// has never applied the shift at all, so there was no working state for a patch to break.
    /// Regenerating the tables would have changed nothing.
    /// </remarks>
    public StatMeaning Stat(int memoryKey)
    {
        if (memoryKey < TableOffset)
        {
            return new StatMeaning($"stat #{memoryKey}", string.Empty, 0);
        }

        int row = memoryKey - TableOffset;

        // THE GAME'S OWN TABLE ANSWERS FIRST, and on this table that is a correctness fix rather
        // than a tidiness one. item-stats.json is keyed by ROW INDEX, and an index is a position:
        // measured 2026-09-15 against the 148 rows a capture could name, ten agreed with the
        // shipped table and 135 did not, the first disagreement at index 4678 - just above the
        // highest reading anyone had ever verified, which is exactly why the file looked sound.
        // A wrong answer here is a plausible one, a neighbouring stat rather than a blank.
        if (Volatile.Read(ref _live)?.Of(row) is { Length: > 0 } id)
        {
            // AND THE WORDING IS LOOKED UP BY THE ID, never by the row, whichever side supplies
            // it. That is what lets the file keep contributing after the indexes have drifted:
            // its sentences are still right, it is only their numbering that went stale, so they
            // are re-keyed onto the one thing the game does not renumber.
            if (Volatile.Read(ref _sentences)?.Of(id) is { Length: > 0 } said)
            {
                // The game's .csd keeps only single-stat lines, so the hole is always {0}.
                return new StatMeaning(id, said, 0);
            }

            return _worded.TryGetValue(id, out StatMeaning known) && known.Text.Length > 0
                ? known with { Id = id }
                : new StatMeaning(id, string.Empty, 0);
        }

        return _stats.TryGetValue(row, out StatMeaning found)
            ? found
            : new StatMeaning($"stat #{memoryKey}", string.Empty, 0);
    }

    /// <summary>
    /// Puts the game's own tables in front of the shipped ones, one half at a time.
    /// </summary>
    /// <remarks>
    /// TWO HALVES FROM TWO PLACES AT TWO MOMENTS, which is why each is taken on its own and a
    /// null leaves the other alone. Stats.dat is reached by a walk of the loader's file list -
    /// the entity browser already pays for that walk once a session, so this rides along rather
    /// than adding a second - and the sentences come off the install's own .csd files, on the
    /// background task that walks the bundle index. Neither knows about the other, and a session
    /// that gets only one is better off than a session that gets neither.
    /// </remarks>
    /// <param name="table">The game's Stats.dat, or null to leave what is there.</param>
    /// <param name="sentences">The game's stat descriptions, or null to leave what is there.</param>
    /// <param name="baseItems">The game's BaseItemTypes.dat, or null to leave what is there.</param>
    /// <param name="modNames">The game's Mods.dat, or null to leave what is there.</param>
    public void Learn(
        Components.StatTable? table = null,
        Components.StatDescriptions? sentences = null,
        BaseItemTable? baseItems = null,
        ModTable? modNames = null)
    {
        if (table is not null)
        {
            Volatile.Write(ref _live, table);
        }

        if (sentences is { Count: > 0 })
        {
            Volatile.Write(ref _sentences, sentences);
        }

        if (baseItems is { Named: > 0 })
        {
            Volatile.Write(ref _baseItems, baseItems);
        }

        if (modNames is { Named: > 0 })
        {
            Volatile.Write(ref _modNames, modNames);
        }
    }

    /// <summary>
    /// Where a stat's name and sentence are coming from, for an interface that has to say which.
    /// </summary>
    /// <remarks>
    /// SAID OUT LOUD for the same reason StatNames says it: the two are indistinguishable on
    /// screen until one of them is wrong, and the one that is wrong is wrong plausibly.
    /// </remarks>
    public string StatSource
    {
        get
        {
            string names = Volatile.Read(ref _live) is { } live
                ? $"the game ({live.Facts.Rows} rows of Stats.dat, {live.Named} read so far)"
                : _stats.Count > 0
                    ? $"data/item-stats.json ({_stats.Count} rows) - right only while somebody re-exports it"
                    : "nowhere";

            string words = Volatile.Read(ref _sentences) is { Count: > 0 } said
                ? said.Source
                : _worded.Count > 0
                    ? $"data/item-stats.json ({_worded.Count} sentences, re-keyed by stat id)"
                    : "nowhere";

            string kinds = Volatile.Read(ref _baseItems) is { Named: > 0 } types
                ? $"the game ({types.Named} of {types.Facts.Rows} rows of BaseItemTypes.dat)"
                : _bases.Count > 0
                    ? $"data/item-names.json ({_bases.Count} base types)"
                    : "nowhere";

            string affixes = Volatile.Read(ref _modNames) is { Named: > 0 } affixTable
                ? $"the game ({affixTable.Named} of {affixTable.Facts.Rows} rows of Mods.dat)"
                : _mods.Count > 0
                    ? $"data/item-names.json ({_mods.Count} mods)"
                    : "nowhere";

            return $"names from {names}; sentences from {words}; base types from {kinds};"
                + $" affixes from {affixes}";
        }
    }

    /// <summary>What a mod is called. Unknown mods keep their id, which is readable enough.</summary>
    public ModMeaning Mod(string? id)
    {
        if (id is not { Length: > 0 })
        {
            return new ModMeaning(string.Empty, string.Empty);
        }

        // The game first, for the same reason as the base types: a mod is keyed by its own id,
        // which the game does not renumber, so the shipped list does not go wrong - it goes short
        // the moment a league adds an affix.
        if (Volatile.Read(ref _modNames)?.Of(id) is { } live)
        {
            return new ModMeaning(live.Name, live.Kind);
        }

        return _mods.TryGetValue(id, out ModMeaning found)
            ? found
            : new ModMeaning(string.Empty, string.Empty);
    }

    /// <summary>What an item's base type is called - "Advanced Dualstring Bow".</summary>
    /// <remarks>
    /// Falls back to the last part of the metadata path, which is close to the name and is
    /// always available - a league adding a base type gets an ugly row rather than a blank one.
    /// </remarks>
    public string Base(string? path)
    {
        if (path is not { Length: > 0 })
        {
            return string.Empty;
        }

        // THE GAME FIRST, and here that is a completeness fix rather than a correctness one. A
        // base type is keyed by its own metadata path, which the game does not renumber, so the
        // shipped list does not go WRONG the way the row-keyed stat tables did - it goes SHORT.
        // The live client carries 5496 rows against the file's 5055, and every one of those four
        // hundred odd came out as the tail of its own path.
        if (Volatile.Read(ref _baseItems)?.Of(path) is { Length: > 0 } named)
        {
            return named;
        }

        if (_bases.TryGetValue(path, out string? known))
        {
            return known;
        }

        int slash = path.LastIndexOf('/');
        return slash >= 0 && slash + 1 < path.Length ? path[(slash + 1)..] : path;
    }

    /// <summary>
    /// What a unique is called, from the ItemVisualIdentity id the item carries.
    /// </summary>
    /// <remarks>
    /// THE ID RATHER THAN THE DISPLAYED NAME, and that is the whole point of going this way.
    /// The id - <c>FourUniquePinnacle1</c> - is an engine identifier and stays English on a
    /// localised client, where the name the game paints does not; and price sites only know the
    /// English one. So a German client prices its uniques correctly.
    ///
    /// It also tells apart uniques that SHARE a base type, which the metadata path cannot:
    /// Morior Invictus and Tabula Rasa are both <c>FourBodyStrDexInt1</c> to a path lookup.
    ///
    /// Empty for an id the table has not heard of, which is a unique added since it was
    /// extracted - the item keeps its base name rather than acquiring a wrong one.
    /// </remarks>
    public string Unique(string? iviId)
        => iviId is { Length: > 0 } && _uniques.TryGetValue(iviId, out string? known) ? known : string.Empty;

    /// <summary>
    /// Loads the tables, or returns <see cref="Empty"/> when they are missing or unreadable.
    /// </summary>
    /// <remarks>
    /// Never throws. Without them the items still list - with raw paths, mod ids and stat
    /// numbers - which is worse to read and just as true.
    /// </remarks>
    public static ItemNames Load(string? statsPath, string? namesPath, string? uniquesPath = null)
    {
        var stats = new Dictionary<int, StatMeaning>();
        var worded = new Dictionary<string, StatMeaning>(StringComparer.Ordinal);
        var mods = new Dictionary<string, ModMeaning>(StringComparer.OrdinalIgnoreCase);
        var bases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var uniques = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            if (statsPath is { Length: > 0 } && File.Exists(statsPath))
            {
                using FileStream stream = File.OpenRead(statsPath);
                ItemStatFile? file = JsonSerializer.Deserialize(stream, ItemNameJsonContext.Default.ItemStatFile);
                foreach ((string key, ItemStatEntry entry) in file?.Stats ?? [])
                {
                    if (!int.TryParse(key, out int row))
                    {
                        continue;
                    }

                    var meaning = new StatMeaning(
                        entry.Id ?? string.Empty, entry.Text ?? string.Empty, entry.Argument);
                    stats[row] = meaning;

                    // THE SAME ROWS FILED UNDER THE ONE KEY A PATCH DOES NOT MOVE. The file's
                    // numbering goes stale; its sentences do not, so they are kept reachable by
                    // id for the rows the game itself can name. First wins, which only matters
                    // for the handful of ids the export lists twice.
                    if (meaning.Id.Length > 0 && meaning.Text.Length > 0)
                    {
                        worded.TryAdd(meaning.Id, meaning);
                    }
                }
            }

            if (namesPath is { Length: > 0 } && File.Exists(namesPath))
            {
                using FileStream stream = File.OpenRead(namesPath);
                ItemNameFile? file = JsonSerializer.Deserialize(stream, ItemNameJsonContext.Default.ItemNameFile);
                foreach ((string id, ItemModEntry entry) in file?.Mods ?? [])
                {
                    mods[id] = new ModMeaning(entry.Name ?? string.Empty, entry.Kind ?? string.Empty);
                }

                foreach ((string path, string name) in file?.Bases ?? [])
                {
                    bases[path] = name;
                }
            }

            if (uniquesPath is { Length: > 0 } && File.Exists(uniquesPath))
            {
                // Tab-separated rather than JSON because that is how the AHK tool's extractor
                // writes it, and a second format for two columns would only be a second thing
                // to regenerate every league.
                foreach (string line in File.ReadLines(uniquesPath))
                {
                    if (line.Length == 0 || line[0] == '#')
                    {
                        continue;
                    }

                    int tab = line.IndexOf('\t', StringComparison.Ordinal);
                    if (tab > 0 && tab + 1 < line.Length)
                    {
                        uniques[line[..tab]] = line[(tab + 1)..].TrimEnd();
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return Empty;
        }

        return new ItemNames(stats, worded, mods, bases, uniques);
    }
}

/// <summary>The shape of the stat file.</summary>
public sealed class ItemStatFile
{
    [JsonPropertyName("stats")]
    public Dictionary<string, ItemStatEntry>? Stats { get; set; }
}

/// <summary>One stat in it.</summary>
public sealed class ItemStatEntry
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("arg")]
    public int Argument { get; set; }
}

/// <summary>The shape of the name file.</summary>
public sealed class ItemNameFile
{
    [JsonPropertyName("mods")]
    public Dictionary<string, ItemModEntry>? Mods { get; set; }

    [JsonPropertyName("bases")]
    public Dictionary<string, string>? Bases { get; set; }
}

/// <summary>One mod in it.</summary>
public sealed class ItemModEntry
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("kind")]
    public string? Kind { get; set; }
}

/// <summary>Source-generated JSON, so the item tables survive Native AOT.</summary>
[JsonSerializable(typeof(ItemStatFile))]
[JsonSerializable(typeof(ItemNameFile))]
public sealed partial class ItemNameJsonContext : JsonSerializerContext;
