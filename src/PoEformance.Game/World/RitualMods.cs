using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoEformance.Game.World;

/// <summary>One reward the ritual line can roll onto a map.</summary>
/// <param name="Weight">
/// How likely it is, against the running total of everything before it. Zero never rolls.
/// </param>
/// <param name="Needs">
/// An atlas stat the player must have for this to be in the pool at all, or 0 for always.
/// </param>
/// <param name="Stat">
/// What it grants. Used for ONE thing: a node's second reward may not grant the same stat as its
/// first, and currency rewards share a stat across their whole trio - so a first pick of Exalted
/// Orbs blocks the other two Exalted rows as well.
/// </param>
public readonly record struct RitualMod(int Weight, int Needs, int Stat, string Text);

/// <summary>
/// The rewards a ritual line can roll, and the roll itself.
/// </summary>
/// <remarks>
/// DATA plus one small piece of arithmetic, and the arithmetic is the reverse-engineered part.
/// The game picks a reward by weighted RESERVOIR SAMPLING: it walks the pool in order, keeps a
/// running total, and replaces its choice whenever a fresh number below that total lands under
/// the current row's weight. That is not the same as picking one number and walking a cumulative
/// table - it draws once PER ROW - so getting it wrong produces plausible rewards that are never
/// the right ones.
///
/// The pool ORDER is therefore load-bearing, which is why the file keeps the game's row numbers
/// and this never sorts them.
///
/// Ported from GameHelper2's Atlas plugin, whose author reversed it from the binary. Validated
/// there as exact for the first mod on every logged node, and 6/6 on two-mod nodes.
/// </remarks>
public sealed class RitualMods
{
    /// <summary>The stat that decides whether a node gets a second reward, as a percentage.</summary>
    public const int SecondModChanceStat = 0x670C;

    /// <summary>And the one that lengthens the line beyond its base of five.</summary>
    public const int AdditionalMapsStat = 0x670B;

    /// <summary>How many maps a line takes before any stat lengthens it.</summary>
    public const int BaseLineLength = 5;

    /// <summary>
    /// The fourth seed word of the second-reward coin flip.
    /// </summary>
    /// <remarks>
    /// Appears in that coin flip and NOWHERE else - the mod picks seed their fourth word with
    /// which pick it is (0 or 1). It is what keeps the coin from sharing a stream with the pick
    /// that follows it.
    /// </remarks>
    public const uint SecondModSalt = 0x91DA3AD9u;

    private readonly IReadOnlyList<RitualMod> _all;

    private RitualMods(IReadOnlyList<RitualMod> all) => _all = all;

    /// <summary>Nothing known, which is what a missing file leaves.</summary>
    public static RitualMods Empty { get; } = new([]);

    /// <summary>Every row, in the game's own order.</summary>
    public IReadOnlyList<RitualMod> All => _all;

    /// <summary>How many rewards are described.</summary>
    public int Count => _all.Count;

    /// <summary>
    /// The rows that can actually roll for a player with these atlas stats.
    /// </summary>
    /// <remarks>
    /// Weightless rows are dropped, and conditional ones need their stat. The stat id is checked
    /// BOTH as written and one lower, because the game's own ids are one below the data table's
    /// and which of the two a given field holds has not been pinned down - accepting either is
    /// what the reference does, and being wrong in that direction only ever includes a row that
    /// should have been excluded.
    /// </remarks>
    public List<RitualMod> Available(IReadOnlyDictionary<int, int> stats)
    {
        ArgumentNullException.ThrowIfNull(stats);

        var pool = new List<RitualMod>(_all.Count);
        foreach (RitualMod mod in _all)
        {
            if (mod.Weight <= 0)
            {
                continue;
            }

            if (mod.Needs == 0 || stats.ContainsKey(mod.Needs) || stats.ContainsKey(mod.Needs - 1))
            {
                pool.Add(mod);
            }
        }

        return pool;
    }

    /// <summary>
    /// One weighted pick from the pool, exactly as the game rolls it.
    /// </summary>
    /// <remarks>
    /// <paramref name="alreadyGranted"/> is the stat the node's FIRST reward granted, and rows
    /// granting it are skipped entirely - no weight added and no number drawn. Skipping them by
    /// drawing and discarding would keep the arithmetic looking right and desynchronise the
    /// stream from the game's on the very next row.
    /// </remarks>
    /// <returns>The chosen reward, or null when the pool is empty.</returns>
    public static RitualMod? Pick(
        uint lineId, uint committed, uint candidate, uint which,
        IReadOnlyList<RitualMod> pool, int alreadyGranted)
    {
        ArgumentNullException.ThrowIfNull(pool);

        RitualRandom random = RitualRandom.Seed(lineId, committed, candidate, which);
        long total = 0;
        RitualMod? chosen = null;

        foreach (RitualMod mod in pool)
        {
            if (alreadyGranted != 0 && mod.Stat == alreadyGranted)
            {
                continue;
            }

            total += mod.Weight;
            if (random.Below((uint)total) < (uint)mod.Weight)
            {
                chosen = mod;
            }
        }

        return chosen;
    }

    /// <summary>Whether a node also gets a second reward.</summary>
    /// <remarks>
    /// Its own stream, seeded with the salt. The chance is an atlas stat, so most players see
    /// this return false every time until they have taken the passive that grants it.
    /// </remarks>
    public static bool RollsTwice(uint lineId, uint committed, uint candidate, int chance)
    {
        if (chance <= 0)
        {
            return false;
        }

        if (chance >= 100)
        {
            return true;
        }

        RitualRandom random = RitualRandom.Seed(lineId, committed, candidate, SecondModSalt);
        return random.Below(100) < (uint)chance;
    }

    /// <summary>Both of a node's rewards. The second is null on the ordinary single-reward node.</summary>
    public static (RitualMod? First, RitualMod? Second) Rewards(
        uint lineId, uint committed, uint candidate, IReadOnlyList<RitualMod> pool, int secondChance)
    {
        RitualMod? first = Pick(lineId, committed, candidate, 0, pool, 0);
        if (first is not { } one)
        {
            return (null, null);
        }

        if (!RollsTwice(lineId, committed, candidate, secondChance))
        {
            return (one, null);
        }

        return (one, Pick(lineId, committed, candidate, 1, pool, one.Stat));
    }

    /// <summary>
    /// Loads the pool, or returns <see cref="Empty"/> when it is missing or unreadable.
    /// </summary>
    /// <remarks>
    /// Never throws. Without it the ritual line is still drawn and still routed; only the
    /// predicted rewards are missing, which is the ordinary state for anybody who has not
    /// updated the file after a league changed the rewards.
    /// </remarks>
    public static RitualMods Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return Empty;
        }

        try
        {
            using FileStream stream = File.OpenRead(path);
            RitualModFile? file = JsonSerializer.Deserialize(stream, RitualModJsonContext.Default.RitualModFile);
            if (file?.Rows is null)
            {
                return Empty;
            }

            var built = new List<RitualMod>(file.Rows.Count);
            foreach (RitualModEntry entry in file.Rows)
            {
                built.Add(new RitualMod(entry.Weight, entry.Needs, entry.Stat, entry.Text ?? string.Empty));
            }

            return new RitualMods(built);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return Empty;
        }
    }
}

/// <summary>The shape of the file on disk.</summary>
public sealed class RitualModFile
{
    [JsonPropertyName("rows")]
    public List<RitualModEntry>? Rows { get; set; }
}

/// <summary>One row of it, in the game's own order.</summary>
public sealed class RitualModEntry
{
    [JsonPropertyName("row")]
    public int Row { get; set; }

    [JsonPropertyName("weight")]
    public int Weight { get; set; }

    [JsonPropertyName("needs")]
    public int Needs { get; set; }

    [JsonPropertyName("stat")]
    public int Stat { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }
}

/// <summary>Source-generated JSON, so the reward pool survives Native AOT.</summary>
[JsonSerializable(typeof(RitualModFile))]
public sealed partial class RitualModJsonContext : JsonSerializerContext;
