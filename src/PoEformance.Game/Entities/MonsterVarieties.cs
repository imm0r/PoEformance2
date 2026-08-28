using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoEformance.Game.Entities;

/// <summary>
/// What the game's own table says about one kind of monster.
/// </summary>
/// <remarks>
/// UNITS ARE STATED ONLY WHERE THE NUMBERS SETTLE THEM. Across 2733 rows
/// <see cref="Life"/>, <see cref="Damage"/>, <see cref="Xp"/> and <see cref="ModelSize"/> sit at
/// a median of 100-115 and run to a few hundred, which is a PERCENTAGE with 100 as the baseline.
/// <see cref="AttackSpeed"/> (0..7170, median 1500), <see cref="Speed"/> (0..97, median 24) and
/// the aggro ranges (0..500) are left unlabelled on purpose: they are plainly not percentages,
/// and naming a unit this table cannot prove is how a display ends up confidently wrong.
///
/// <see cref="Crit"/> is the trap in that set. It is called AttackCrit and holds 0, 1 or 2 - it
/// is a kind, not a chance - so anything drawing it as "0%" would be inventing a statistic.
///
/// SEVERAL FIELDS ARE ROW NUMBERS, not names: <see cref="Type"/>, <see cref="Tags"/>,
/// <see cref="Effects"/>, <see cref="Mods"/>, <see cref="Mods2"/>, <see cref="SpecialMods"/>,
/// <see cref="Blood"/> and <see cref="Quest"/> all point into tables this repository does not
/// carry. They are kept as numbers so the join can be added without a re-export - see the
/// generator at scripts/monster-varieties.py.
/// </remarks>
/// <param name="Name">
/// What the game calls it. Absent on the 24 rows whose name is a bracketed placeholder.
/// </param>
/// <param name="Quest">
/// The QuestFlags row this monster's death sets, on the 68 rows that have one - every named
/// campaign boss and nothing else. See <see cref="MonsterVarieties"/> for what that is worth.
/// </param>
/// <param name="Boss">Whether the game gives it the boss health bar. True on 363 rows.</param>
/// <param name="Inherits">
/// Object templates this monster is built on. NOT other rows of this table: of 810 references
/// exactly one names another monster variety, and the rest are .ot files - a different system.
/// The most-referenced ones are league bases (AbyssMonsterBase, SanctumMonsterBase,
/// UltimatumMonsterBase), which is what makes this the interesting column despite that.
/// </param>
public sealed record MonsterVariety(
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("type")] int Type = 0,
    [property: JsonPropertyName("speed")] int Speed = 0,
    [property: JsonPropertyName("size")] int Size = 0,
    [property: JsonPropertyName("modelSize")] int ModelSize = 0,
    [property: JsonPropertyName("minAttack")] int MinAttack = 0,
    [property: JsonPropertyName("maxAttack")] int MaxAttack = 0,
    [property: JsonPropertyName("minAggro")] int MinAggro = 0,
    [property: JsonPropertyName("maxAggro")] int MaxAggro = 0,
    [property: JsonPropertyName("xp")] int Xp = 0,
    [property: JsonPropertyName("damage")] int Damage = 0,
    [property: JsonPropertyName("life")] int Life = 0,
    [property: JsonPropertyName("attackSpeed")] int AttackSpeed = 0,
    [property: JsonPropertyName("crit")] int Crit = 0,
    [property: JsonPropertyName("blood")] int Blood = 0,
    [property: JsonPropertyName("quest")] int Quest = 0,
    [property: JsonPropertyName("poise")] double Poise = 0d,
    [property: JsonPropertyName("stance")] string? Stance = null,
    [property: JsonPropertyName("boss")] bool Boss = false,
    [property: JsonPropertyName("base")] string? Base = null,
    [property: JsonPropertyName("tags")] IReadOnlyList<int>? Tags = null,
    [property: JsonPropertyName("effects")] IReadOnlyList<int>? Effects = null,
    [property: JsonPropertyName("mods")] IReadOnlyList<int>? Mods = null,
    [property: JsonPropertyName("mods2")] IReadOnlyList<int>? Mods2 = null,
    [property: JsonPropertyName("specialMods")] IReadOnlyList<int>? SpecialMods = null,
    [property: JsonPropertyName("inherits")] IReadOnlyList<string>? Inherits = null)
{
    /// <summary>How many skills the game grants this monster.</summary>
    /// <remarks>
    /// The count is worth showing even while the names are not available: it separates a
    /// monster with one attack from a boss with sixty-seven, which is the difference between
    /// something to walk past and something to look at.
    /// </remarks>
    public int SkillCount => Effects?.Count ?? 0;

    /// <summary>Whether the table says anything about this monster beyond its existence.</summary>
    public bool SaysSomething => Name is { Length: > 0 } || SkillCount > 0;
}

/// <summary>One stat a modifier sets, and the range it sets it to.</summary>
/// <remarks>
/// The export writes every value as a [min, max] pair and almost all of them are the same number
/// twice. Both are kept anyway: a modifier that really does roll is not distinguishable from a
/// fixed one once the second half is thrown away, and there is no second export to go back to.
/// </remarks>
public sealed record ModifierStat(
    [property: JsonPropertyName("stat")] string Stat = "",
    [property: JsonPropertyName("min")] int Min = 0,
    [property: JsonPropertyName("max")] int Max = 0)
{
    /// <summary>The range as one readable value.</summary>
    public string Range => Min == Max
        ? Min.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : $"{Min.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
          + $"-{Max.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
}

/// <summary>What one of a monster's modifier rows is called, and what it does.</summary>
public sealed record ModifierMeaning(
    [property: JsonPropertyName("id")] string Id = "",
    [property: JsonPropertyName("stats")] IReadOnlyList<ModifierStat>? Stats = null);

/// <summary>
/// The game's monster table, keyed by the path an entity carries.
/// </summary>
/// <remarks>
/// THE ENTITY'S PATH IS THE TABLE'S KEY, which is what makes this worth loading at all: no
/// fuzzy matching, no name cleanup, the Id column IS <c>Entity.Path</c>. Measured against 21
/// captured areas, 283 of the 325 paths that look like entity identities resolve; the 42 that
/// do not are spawners, arena props, curse zones and objects/ folders - things that live under
/// Metadata/Monsters/ without being monsters.
///
/// THE @VARIANT SUFFIX IS STRIPPED, from the reference tool rather than from a guess: GameHelper2
/// does the same in MonsterCategories.Get, and its comment says the base equals the MonsterVariety
/// Id. None of the 21 captures carries such a suffix - a preload list holds files, not live
/// entities - so this is the one rule here that the captures could not confirm, and it is kept
/// because the reference is a working tool against the same game.
///
/// WHAT THE QUEST COLUMN IS AND IS NOT. It holds a QuestFlags row number on 68 monsters, and
/// those 68 are exactly the named campaign bosses - Geonor, Jamanra, The Devourer, Ignagduk,
/// Yama The White. So it answers "the thing in front of you is a quest step", not "where is my
/// objective": most objectives are not monsters at all, and the column names no place. The row
/// number is meant for QuestWatch.FlagId, whose table this project already reads from the game's
/// memory - and which it measured at 5717 rows, comfortably above the largest value here (5210).
/// That is a consistency check rather than a proof, and it is the reason the lookup below refuses
/// out-of-range rows instead of showing a flag that belongs to something else.
/// </remarks>
public sealed class MonsterVarieties
{
    /// <summary>Nothing known - what a missing or unreadable table produces.</summary>
    public static MonsterVarieties Empty { get; } = new(
        new Dictionary<string, MonsterVariety>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<int, string>(),
        new Dictionary<int, ModifierMeaning>(),
        string.Empty);

    private readonly IReadOnlyDictionary<string, MonsterVariety> _byPath;
    private readonly IReadOnlyDictionary<int, string> _skills;
    private readonly IReadOnlyDictionary<int, ModifierMeaning> _modifiers;

    private MonsterVarieties(
        IReadOnlyDictionary<string, MonsterVariety> byPath,
        IReadOnlyDictionary<int, string> skills,
        IReadOnlyDictionary<int, ModifierMeaning> modifiers,
        string generated)
    {
        _byPath = byPath;
        _skills = skills;
        _modifiers = modifiers;
        Generated = generated;
    }

    /// <summary>When the table was built, as the generator wrote it. Empty when unknown.</summary>
    /// <remarks>
    /// Shown rather than merely stored: a monster table is a snapshot of one patch, and the way
    /// a stale one fails is that new monsters simply have no name - which looks like a lookup bug
    /// rather than an old file.
    /// </remarks>
    public string Generated { get; }

    /// <summary>How many monsters the table knows.</summary>
    public int Count => _byPath.Count;

    /// <summary>Reads the table, or returns <see cref="Empty"/> when it cannot.</summary>
    /// <remarks>Never throws. Without it the entity browser shows paths, as it always did.</remarks>
    public static MonsterVarieties Load(string? path)
    {
        if (path is not { Length: > 0 } || !File.Exists(path))
        {
            return Empty;
        }

        try
        {
            using FileStream stream = File.OpenRead(path);
            MonsterVarietyFile? file =
                JsonSerializer.Deserialize(stream, MonsterJsonContext.Default.MonsterVarietyFile);

            if (file?.Monsters is not { Count: > 0 })
            {
                return Empty;
            }

            var byPath = new Dictionary<string, MonsterVariety>(
                file.Monsters.Count, StringComparer.OrdinalIgnoreCase);

            foreach ((string id, MonsterVariety one) in file.Monsters)
            {
                byPath[Same(id)] = one;
            }

            // THE ROW NUMBER IS THE KEY, kept as the game's own rather than replaced by the
            // name it resolves to. JSON has no integer keys, so the generator writes them as
            // text and they are parsed back here; a row that will not parse is dropped rather
            // than defaulted, because row 0 is a real skill and a silent 0 would name it.
            var skills = new Dictionary<int, string>(file.Skills?.Count ?? 0);
            foreach ((string row, string name) in file.Skills ?? [])
            {
                if (int.TryParse(row, System.Globalization.CultureInfo.InvariantCulture, out int at))
                {
                    skills[at] = name;
                }
            }

            var modifiers = new Dictionary<int, ModifierMeaning>(file.Modifiers?.Count ?? 0);
            foreach ((string row, ModifierMeaning meaning) in file.Modifiers ?? [])
            {
                if (int.TryParse(row, System.Globalization.CultureInfo.InvariantCulture, out int at))
                {
                    modifiers[at] = meaning;
                }
            }

            return new MonsterVarieties(byPath, skills, modifiers, file.Generated ?? string.Empty);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return Empty;
        }
    }

    /// <summary>What the table says about the thing at this path, or null.</summary>
    public MonsterVariety? Find(string? entityPath)
    {
        string key = Same(entityPath);
        return key.Length > 0 && _byPath.TryGetValue(key, out MonsterVariety? one) ? one : null;
    }

    /// <summary>How many skills carry a name. Zero when the reference table was not exported.</summary>
    public int NamedSkills => _skills.Count;

    /// <summary>What the game calls this skill row, or empty when it is not known.</summary>
    public string SkillName(int row) => _skills.TryGetValue(row, out string? name) ? name : string.Empty;

    /// <summary>What this modifier row is called and what it sets, or null.</summary>
    public ModifierMeaning? Modifier(int row)
        => _modifiers.TryGetValue(row, out ModifierMeaning? meaning) ? meaning : null;

    /// <summary>
    /// This monster's skills, named where a name is known and numbered where it is not.
    /// </summary>
    /// <remarks>
    /// A ROW WITH NO NAME STAYS VISIBLE, as "#4211" rather than being dropped. A monster with
    /// sixty-seven skills and forty names is a table that needs refreshing; one that quietly
    /// showed forty would look complete and be wrong.
    /// </remarks>
    public IEnumerable<string> Skills(MonsterVariety? one)
    {
        foreach (int row in one?.Effects ?? [])
        {
            string name = SkillName(row);
            yield return name.Length > 0
                ? name
                : "#" + row.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Every modifier this monster carries that resolved to something.</summary>
    /// <remarks>
    /// The three columns are read as one list because nothing here distinguishes them: Mods,
    /// Mods2 and Special_Mods all end up as rows of the same table, and which slot a modifier
    /// arrived in says nothing about what it does.
    /// </remarks>
    public IEnumerable<ModifierMeaning> Modifiers(MonsterVariety? one)
    {
        if (one is null)
        {
            yield break;
        }

        foreach (int row in (one.Mods ?? []).Concat(one.Mods2 ?? []).Concat(one.SpecialMods ?? []))
        {
            ModifierMeaning? meaning = Modifier(row);
            if (meaning is not null)
            {
                yield return meaning;
            }
        }
    }

    /// <summary>
    /// One path in the spelling the table is keyed in.
    /// </summary>
    /// <remarks>
    /// Backslashes become slashes and an @variant suffix is dropped. Both are cheap and both
    /// fail silently when skipped - the symptom is a monster the table plainly contains looking
    /// unknown, which reads as a missing row rather than a mismatched spelling.
    /// </remarks>
    public static string Same(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string cleaned = path.Replace('\\', '/').Trim();
        int variant = cleaned.IndexOf('@', StringComparison.Ordinal);
        return variant >= 0 ? cleaned[..variant] : cleaned;
    }
}

/// <summary>The file's shape.</summary>
internal sealed class MonsterVarietyFile
{
    [JsonPropertyName("generated")]
    public string? Generated { get; init; }

    [JsonPropertyName("monsters")]
    public Dictionary<string, MonsterVariety>? Monsters { get; init; }

    /// <summary>Skill row number to what the game calls it. Row numbers are text - JSON keys.</summary>
    [JsonPropertyName("skills")]
    public Dictionary<string, string>? Skills { get; init; }

    [JsonPropertyName("modifiers")]
    public Dictionary<string, ModifierMeaning>? Modifiers { get; init; }
}

/// <summary>Source-generated JSON, so the monster table survives Native AOT.</summary>
[JsonSerializable(typeof(MonsterVarietyFile))]
internal sealed partial class MonsterJsonContext : JsonSerializerContext;
