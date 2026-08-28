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
/// EVERY FIELD HERE THAT IS A ROW NUMBER STAYS ONE, even the resolved ones. <see cref="Type"/>,
/// <see cref="Tags"/>, <see cref="Effects"/>, <see cref="Mods"/>, <see cref="Mods2"/> and
/// <see cref="SpecialMods"/> now have tables to resolve against, and the names live beside them
/// on <see cref="MonsterVarieties"/> rather than replacing them - the number is the game's own
/// identity, and the next table keyed on it joins without another export.
/// <see cref="Quest"/> is the last one with nowhere to point, and always will be: QuestFlags is
/// read from the game's own memory rather than shipped, so it resolves at runtime or not at all.
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
/// The defensive block the game hangs on a monster's TYPE rather than on the monster.
/// </summary>
/// <remarks>
/// THIS IS WHERE ARMOUR ACTUALLY LIVES. MonsterVarieties has a MonsterArmour column of its own
/// and it is filled on 16 rows of 2734 - reading it and concluding the game does not store
/// monster armour would be wrong twice over. The real values sit one join away, on the type:
/// Armour on 44% of the types in use, Evasion on 30%, EnergyShieldFromLife on 23%.
///
/// <see cref="Resistances"/> stays a row number, resolved through
/// <see cref="MonsterVarieties.ResistancesOf"/> - and it is a profile NAME rather than a
/// percentage, because the numbers behind it are 32 columns of tiers this export cannot explain.
/// </remarks>
/// <param name="Id">
/// What the type is called - LumberingDead, YamaBoss, IgnagdukBogWitch. It names the monster,
/// which is what makes the alignment of this table checkable at all.
/// </param>
public sealed record MonsterKind(
    [property: JsonPropertyName("id")] string Id = "",
    [property: JsonPropertyName("armour")] int Armour = 0,
    [property: JsonPropertyName("evasion")] int Evasion = 0,
    [property: JsonPropertyName("energyShield")] int EnergyShield = 0,
    [property: JsonPropertyName("spread")] int Spread = 0,
    [property: JsonPropertyName("summoned")] bool Summoned = false,
    [property: JsonPropertyName("resistances")] IReadOnlyList<int>? Resistances = null);

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
        new Dictionary<int, string>(),
        new Dictionary<int, MonsterKind>(),
        new Dictionary<int, string>(),
        new Dictionary<int, string>(),
        string.Empty);

    private readonly IReadOnlyDictionary<string, MonsterVariety> _byPath;
    private readonly IReadOnlyDictionary<int, string> _skills;
    private readonly IReadOnlyDictionary<int, ModifierMeaning> _modifiers;
    private readonly IReadOnlyDictionary<int, string> _tags;
    private readonly IReadOnlyDictionary<int, MonsterKind> _types;
    private readonly IReadOnlyDictionary<int, string> _blood;
    private readonly IReadOnlyDictionary<int, string> _resistances;

    private MonsterVarieties(
        IReadOnlyDictionary<string, MonsterVariety> byPath,
        IReadOnlyDictionary<int, string> skills,
        IReadOnlyDictionary<int, ModifierMeaning> modifiers,
        IReadOnlyDictionary<int, string> tags,
        IReadOnlyDictionary<int, MonsterKind> types,
        IReadOnlyDictionary<int, string> blood,
        IReadOnlyDictionary<int, string> resistances,
        string generated)
    {
        _byPath = byPath;
        _skills = skills;
        _modifiers = modifiers;
        _tags = tags;
        _types = types;
        _blood = blood;
        _resistances = resistances;
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

            var tags = new Dictionary<int, string>(file.Tags?.Count ?? 0);
            foreach ((string row, string name) in file.Tags ?? [])
            {
                if (int.TryParse(row, System.Globalization.CultureInfo.InvariantCulture, out int at))
                {
                    tags[at] = name;
                }
            }

            var types = new Dictionary<int, MonsterKind>(file.Types?.Count ?? 0);
            foreach ((string row, MonsterKind kind) in file.Types ?? [])
            {
                if (int.TryParse(row, System.Globalization.CultureInfo.InvariantCulture, out int at))
                {
                    types[at] = kind;
                }
            }

            return new MonsterVarieties(
                byPath,
                skills,
                modifiers,
                tags,
                types,
                Rows(file.Blood),
                Rows(file.Resistances),
                file.Generated ?? string.Empty);
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

    /// <summary>A row-number-keyed map, as the generator wrote it: JSON has no integer keys.</summary>
    /// <remarks>
    /// A row that will not parse is dropped rather than defaulted. Row 0 is a real blood type
    /// (plain Blood, which 1092 monsters carry), so a silent 0 would name it.
    /// </remarks>
    private static Dictionary<int, string> Rows(Dictionary<string, string>? from)
    {
        var got = new Dictionary<int, string>(from?.Count ?? 0);
        foreach ((string row, string name) in from ?? [])
        {
            if (int.TryParse(row, System.Globalization.CultureInfo.InvariantCulture, out int at))
            {
                got[at] = name;
            }
        }

        return got;
    }

    /// <summary>What the game calls this monster's blood, or empty when it is not known.</summary>
    /// <remarks>
    /// Not decoration: NoBlood is why a corpse cannot be raised or exploded, and the 56 rows
    /// separate Blood from BonesNew, GhostBlood, SandBlood and Stone - which is the difference
    /// between a thing that leaves a corpse and one that does not.
    /// </remarks>
    public string BloodName(MonsterVariety? one)
        => one is not null && _blood.TryGetValue(one.Blood, out string? name) ? name : string.Empty;

    /// <summary>
    /// The resistance profiles this monster's TYPE carries, by name.
    /// </summary>
    /// <remarks>
    /// NAMES ONLY, and that is deliberate. Each profile also holds 32 numeric columns -
    /// Fire1..Fire5, Cold1..Cold5 and so on, some of them arrays - and what the numbered tiers
    /// mean is not something the export settles: MinorColdResist reads 30, 30, 30 across the
    /// first three and MajorColdResist reads 75, 60, 50, which fits area tiers and several other
    /// readings equally well. "MajorFireResist" already says what a reader wants; a number here
    /// under a guessed label would be the same mistake as calling AttackSpeed a percentage.
    /// </remarks>
    public IEnumerable<string> ResistancesOf(MonsterVariety? one)
    {
        foreach (int row in Kind(one)?.Resistances ?? [])
        {
            string name = _resistances.TryGetValue(row, out string? got) ? got : string.Empty;
            yield return name.Length > 0
                ? name
                : "#" + row.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>How many tags carry a name. Zero when the reference table was not exported.</summary>
    public int NamedTags => _tags.Count;

    /// <summary>
    /// What the game tags this monster as, named where a name is known.
    /// </summary>
    /// <remarks>
    /// This is the column GameHelper2 reads to decide whether a thing is a Beast: humanoid,
    /// human, undead, construct, beast and demon all live here, and a monster may carry several.
    ///
    /// It is NOT a way to tell which league mechanic an area has. The table carries
    /// delve_monster, blight_monster, legion_monster, incursion_monster and breach_monster_fire
    /// among others, and not one monster in the game carries any of them - they are PoE1 rows
    /// left behind, the same trap Data/Balance/PreloadGroups.dat turned out to be. Only sanctum
    /// (77 monsters), expedition (30), azmeri (23), affliction (19), precursor (12) and
    /// sanctified (10) are real.
    /// </remarks>
    public IEnumerable<string> TagsOf(MonsterVariety? one)
    {
        foreach (int row in one?.Tags ?? [])
        {
            string name = Tag(row);
            yield return name.Length > 0
                ? name
                : "#" + row.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>What the game calls this tag row, or empty when it is not known.</summary>
    public string Tag(int row) => _tags.TryGetValue(row, out string? name) ? name : string.Empty;

    /// <summary>The defensive block for this monster's type, or null.</summary>
    public MonsterKind? Kind(MonsterVariety? one)
        => one is not null && _types.TryGetValue(one.Type, out MonsterKind? kind) ? kind : null;

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

    [JsonPropertyName("tags")]
    public Dictionary<string, string>? Tags { get; init; }

    [JsonPropertyName("types")]
    public Dictionary<string, MonsterKind>? Types { get; init; }

    [JsonPropertyName("blood")]
    public Dictionary<string, string>? Blood { get; init; }

    [JsonPropertyName("resistances")]
    public Dictionary<string, string>? Resistances { get; init; }
}

/// <summary>Source-generated JSON, so the monster table survives Native AOT.</summary>
[JsonSerializable(typeof(MonsterVarietyFile))]
internal sealed partial class MonsterJsonContext : JsonSerializerContext;
