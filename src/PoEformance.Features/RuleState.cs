using PoEformance.Game.Components;
using PoEformance.Game.World;

namespace PoEformance.Features;

/// <summary>
/// Everything a rule may ask about, gathered once per tick.
/// </summary>
/// <remarks>
/// One flat fact sheet rather than a live view of the snapshot, for the same reason the
/// snapshot itself is immutable: every condition in a rule must see the same moment. A tree
/// that read the entity list per leaf could have its monster count change between two branches
/// of the same AND, and the resulting rule would fire on a state that never existed.
///
/// A NUMBER THAT COULD NOT BE ANSWERED IS NULL, NEVER A SENTINEL, and that is the single
/// most load-bearing decision in this type. The reference plugin reports an unreadable life
/// pool as 0 and "no rare monster anywhere" as 9999, so `LifePercent &lt;= 35` fires on a
/// loading screen and `NearestRare &gt;= 100` is satisfied by an empty room. Both read as the
/// feature working. Null makes the comparison say "no" instead - see
/// <see cref="RuleCondition"/>, where that rule lives.
///
/// Counts are the exception and are answered as 0: no monsters nearby is a real reading, not a
/// failure to read.
/// </remarks>
public sealed record RuleState
{
    /// <summary>Nothing known - what a rule sees before the first read lands.</summary>
    public static RuleState Nothing { get; } = new();

    /// <summary>Whether the game is in an area rather than in a menu or a loading screen.</summary>
    public bool InGame { get; init; }

    /// <summary>
    /// Whether the GAME window has focus.
    /// </summary>
    /// <remarks>
    /// The gate every input effect hangs on, and it lives in the state - i.e. in the DECISION -
    /// rather than in the code that presses keys, on the same argument as
    /// <see cref="AutoFlask"/>: keystrokes land wherever focus is, so no future caller should
    /// be able to reach the sending path without passing this.
    /// </remarks>
    public bool GameFocused { get; init; }

    /// <summary>Whether the character is alive and loaded.</summary>
    public bool Alive { get; init; }

    /// <summary>Whether a screen-filling panel - stash, atlas, skill tree - is open.</summary>
    /// <remarks>
    /// Not a copy of "in game": a player reading their passive tree is in the area, and a rule
    /// that presses a key at them is pressing it into the tree's own key handling.
    /// </remarks>
    public bool InPanel { get; init; }

    public bool InTown { get; init; }

    public bool InHideout { get; init; }

    /// <summary>Somewhere with something to fight - neither town nor hideout.</summary>
    public bool InMap => InGame && !InTown && !InHideout;

    /// <summary>Whether the player moved measurably since the previous tick.</summary>
    public bool Moving { get; init; }

    /// <summary>World units per second, measured across the last two ticks.</summary>
    public double? Speed { get; init; }

    public string AreaId { get; init; } = string.Empty;

    public string AreaName { get; init; } = string.Empty;

    /// <summary>The area's monster level, or null when it could not be read.</summary>
    public int? AreaLevel { get; init; }

    /// <summary>The character's level, or null when it could not be read.</summary>
    public int? PlayerLevel { get; init; }

    /// <summary>How long since the area last changed.</summary>
    public double SecondsInArea { get; init; }

    /// <summary>The player's pools, or null when they could not be read.</summary>
    public Vitals? Vitals { get; init; }

    /// <summary>What is on the player, or null when it could not be read.</summary>
    public ActiveBuffs? Buffs { get; init; }

    /// <summary>The belt, or null when it could not be read.</summary>
    public FlaskBelt? Belt { get; init; }

    /// <summary>Live monsters around the player, nearest first.</summary>
    /// <remarks>
    /// Sorted once here rather than per condition: a rule with three distance leaves would
    /// otherwise walk the entity list three times, and the whole tree is evaluated on every
    /// tick of every rule in the profile.
    /// </remarks>
    public IReadOnlyList<NearMonster> Monsters { get; init; } = [];

    /// <summary>How many live monsters are around, by rarity band.</summary>
    public int MonsterCount => Monsters.Count;

    public int RareMonsterCount => Count(ItemRarity.Rare);

    public int UniqueMonsterCount => Count(ItemRarity.Unique);

    public int RareOrUniqueMonsterCount => RareMonsterCount + UniqueMonsterCount;

    /// <summary>Distance to the closest live monster, or null when there is none.</summary>
    public double? NearestMonster => Monsters.Count > 0 ? Monsters[0].Distance : null;

    public double? NearestRareMonster => Nearest(ItemRarity.Rare);

    public double? NearestUniqueMonster => Nearest(ItemRarity.Unique);

    /// <summary>Distance to the closest rare OR unique, or null when there is neither.</summary>
    public double? NearestRareOrUniqueMonster
    {
        get
        {
            foreach (NearMonster monster in Monsters)
            {
                if (monster.Rarity is ItemRarity.Rare or ItemRarity.Unique)
                {
                    return monster.Distance;
                }
            }

            return null;
        }
    }

    /// <summary>Fill level of a pool, 0-100, or null when it could not be read.</summary>
    /// <remarks>
    /// Measured against the UNRESERVED pool by <see cref="Vital.Percent"/>, which is the whole
    /// reason this goes through that type rather than dividing here: with half of mana reserved
    /// by auras a full globe is 50% of the maximum, and every threshold below that would fire
    /// forever.
    /// </remarks>
    public double? Percent(VitalKind kind)
    {
        if (Vitals is not Vitals pools)
        {
            return null;
        }

        int percent = Select(pools, kind).Percent;
        return percent < 0 ? null : percent;
    }

    /// <summary>The raw current value of a pool, or null when it could not be read.</summary>
    public double? Current(VitalKind kind)
        => Vitals is Vitals pools && Select(pools, kind).IsValid ? Select(pools, kind).Current : null;

    /// <summary>Whether a buff or debuff whose name contains this is on the player.</summary>
    /// <remarks>
    /// One lookup for both, because the game keeps one list and this project's reader records
    /// no flag separating them. The reference plugin ships HasBuff and HasDebuff as separate
    /// conditions that call the same code, which reads as a distinction the tool can make.
    /// </remarks>
    public bool HasBuff(string needle) => Buffs?.Has(needle) == true;

    /// <summary>Seconds left on a named buff, or null when it is not on the player.</summary>
    /// <remarks>
    /// Null rather than 0 for an absent buff, so "expiring within 2 seconds" does not fire
    /// forever on a buff nobody has - the same trap as an unreadable pool, one level down.
    /// </remarks>
    public double? BuffTimeLeft(string needle) => Find(needle)?.TimeLeft;

    /// <summary>Stacks on a named buff, or null when it is not on the player.</summary>
    public double? BuffCharges(string needle) => Find(needle)?.Charges;

    /// <summary>Whether the flask in a belt slot is still doing its job.</summary>
    public bool FlaskActive(int slot) => Buffs?.IsFlaskActive(slot) == true;

    /// <summary>Charges held by the flask in a belt slot, or null when the slot is unreadable.</summary>
    /// <remarks>
    /// Per SLOT, which the reference plugin's condition of the same name is not: it returns the
    /// player's single exposed charge count whatever slot is asked for, and its own
    /// documentation says so. This project reads the belt item by item, so the honest answer
    /// is available and the misleading one need not be shipped.
    /// </remarks>
    public double? FlaskCharges(int slot) => Belt?.InSlot(slot)?.Charges;

    /// <summary>Whether pressing that flask's key would actually do anything.</summary>
    /// <remarks>
    /// Null-safe in the direction that does nothing: an unreadable belt answers "no", so a
    /// rule gated on readiness stays quiet rather than pressing keys on a guess.
    /// </remarks>
    public bool FlaskReady(int slot)
        => Belt?.InSlot(slot) is EquippedFlask flask && !flask.IsCharm && flask.CanUse;

    /// <summary>Whether the area's name or id contains this.</summary>
    public bool AreaContains(string needle)
        => needle.Length > 0
           && (AreaName.Contains(needle, StringComparison.OrdinalIgnoreCase)
               || AreaId.Contains(needle, StringComparison.OrdinalIgnoreCase));

    /// <summary>How many live monsters are within a distance of the player.</summary>
    public int MonsterCountWithin(double distance) => CountWithin(distance, null);

    /// <summary>How many live rares and uniques are within a distance of the player.</summary>
    public int RareOrUniqueCountWithin(double distance) => CountWithin(distance, RareOrUnique);

    /// <summary>
    /// Gathers the facts from one snapshot.
    /// </summary>
    /// <param name="snapshot">The world as of the last read.</param>
    /// <param name="focused">Whether the game window has focus.</param>
    /// <param name="track">
    /// Where the facts that need a previous tick come from - time in area, and movement. Passed
    /// in rather than kept in statics, so two engines (or a test and a game) never share them.
    /// </param>
    /// <param name="nowMs">A monotonic clock.</param>
    public static RuleState From(WorldSnapshot snapshot, bool focused, RuleHistory track, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(track);

        if (!snapshot.InGame)
        {
            // Deliberately keeps the focus flag: a rule may legitimately watch for the game
            // being in front while sitting in a menu, and nothing else here can be answered.
            track.Reset(nowMs);
            return new RuleState { GameFocused = focused };
        }

        WorldEntity? player = snapshot.Player;
        RuleMovement movement = track.Observe(snapshot.AreaHash, player, nowMs);

        return new RuleState
        {
            InGame = true,
            GameFocused = focused,
            Alive = snapshot.PlayerVitals is Vitals pools && !pools.IsDeadOrUnloaded,
            InPanel = snapshot.InAPanel,
            InTown = snapshot.Area.IsTown,
            InHideout = snapshot.Area.IsHideout,
            Moving = movement.Moving,
            Speed = movement.Speed,
            AreaId = snapshot.Area.Id,
            AreaName = snapshot.Area.Name,
            AreaLevel = snapshot.AreaLevel > 0 ? snapshot.AreaLevel : null,
            PlayerLevel = snapshot.PlayerLevel > 0 ? snapshot.PlayerLevel : null,
            SecondsInArea = movement.SecondsInArea,
            Vitals = snapshot.PlayerVitals,
            Buffs = snapshot.PlayerBuffs,
            Belt = snapshot.FlaskBelt,
            Monsters = NearbyMonsters(snapshot, player),
        };
    }

    /// <summary>
    /// The live monsters around the player, nearest first.
    /// </summary>
    /// <remarks>
    /// The health-bar layer's filter, and the damage meter's: friendly things are the player's
    /// own minions and totems, an effect wearing a monster's components is not something
    /// anybody is fighting, and an unreadable pool is excluded rather than counted - a
    /// component that did not resolve would otherwise read as a monster at full health.
    ///
    /// Nothing filters remembered entities here because nothing that MOVES is ever remembered
    /// (see <see cref="EntityMemory"/>), so a monster cannot arrive from that half of the list.
    /// </remarks>
    private static List<NearMonster> NearbyMonsters(WorldSnapshot snapshot, WorldEntity? player)
    {
        var found = new List<NearMonster>();
        if (player is not WorldEntity at)
        {
            return found;
        }

        foreach (WorldEntity entity in snapshot.Entities)
        {
            if (entity.Kind != EntityKind.Monster
                || entity.IsFriendly
                || entity.IsEffect
                || !entity.Life.IsValid
                || entity.Life.Current <= 0)
            {
                continue;
            }

            float dx = entity.WorldX - at.WorldX;
            float dy = entity.WorldY - at.WorldY;
            found.Add(new NearMonster(MathF.Sqrt((dx * dx) + (dy * dy)), entity.Rarity));
        }

        found.Sort(static (left, right) => left.Distance.CompareTo(right.Distance));
        return found;
    }

    private int Count(ItemRarity rarity)
    {
        int total = 0;
        foreach (NearMonster monster in Monsters)
        {
            if (monster.Rarity == rarity)
            {
                total++;
            }
        }

        return total;
    }

    private int CountWithin(double distance, Func<ItemRarity, bool>? matches)
    {
        int total = 0;
        foreach (NearMonster monster in Monsters)
        {
            // Sorted nearest first, so the first one out of range ends the walk.
            if (monster.Distance > distance)
            {
                break;
            }

            if (matches is null || matches(monster.Rarity))
            {
                total++;
            }
        }

        return total;
    }

    private double? Nearest(ItemRarity rarity)
    {
        foreach (NearMonster monster in Monsters)
        {
            if (monster.Rarity == rarity)
            {
                return monster.Distance;
            }
        }

        return null;
    }

    private ActiveBuff? Find(string needle)
    {
        if (Buffs is not ActiveBuffs on || string.IsNullOrWhiteSpace(needle))
        {
            return null;
        }

        // The strongest match rather than the first: several stacks of the same effect are
        // separate entries, and "how long is this on me for" means the one that lasts longest.
        ActiveBuff? best = null;
        foreach (ActiveBuff buff in on.All)
        {
            if (buff.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                && (best is not ActiveBuff held || buff.TimeLeft > held.TimeLeft))
            {
                best = buff;
            }
        }

        return best;
    }

    private static bool RareOrUnique(ItemRarity rarity) => rarity is ItemRarity.Rare or ItemRarity.Unique;

    private static Vital Select(Vitals pools, VitalKind kind) => kind switch
    {
        VitalKind.Life => pools.Life,
        VitalKind.Mana => pools.Mana,
        VitalKind.EnergyShield => pools.EnergyShield,
        _ => default,
    };
}

/// <summary>One live monster, reduced to what a rule can ask about.</summary>
/// <remarks>
/// Not a <see cref="WorldEntity"/>: a rule never follows an entity back into memory, and
/// carrying the whole record would let a future condition do exactly that on an address that
/// may since have been freed.
/// </remarks>
public readonly record struct NearMonster(double Distance, ItemRarity Rarity);

/// <summary>What the previous tick has to say about this one.</summary>
public readonly record struct RuleMovement(bool Moving, double? Speed, double SecondsInArea);

/// <summary>
/// The little that cannot be answered from one snapshot: time in area, and movement.
/// </summary>
/// <remarks>
/// An object rather than statics, which is what the reference plugin uses. Statics make two
/// instances impossible - a test and a live engine in the same process share one area clock -
/// and they make "why did this fire" untestable, because the answer depends on whatever ran
/// before.
/// </remarks>
public sealed class RuleHistory
{
    /// <summary>
    /// Below this, the player counts as standing still.
    /// </summary>
    /// <remarks>
    /// World units per second. Not zero: a stationary character's position wobbles by a
    /// fraction of a unit between reads, so an exact test reports constant motion.
    /// </remarks>
    public const double StillBelow = 1.0;

    private uint _area;
    private long _areaAt;
    private bool _hasArea;
    private float _lastX;
    private float _lastY;
    private long _lastAt;
    private bool _hasPosition;

    /// <summary>Notes where the player is now, and says what changed since last time.</summary>
    public RuleMovement Observe(uint area, WorldEntity? player, long nowMs)
    {
        if (!_hasArea || area != _area)
        {
            _area = area;
            _areaAt = nowMs;
            _hasArea = true;

            // A new area is a teleport, not a sprint. Keeping the last position would report a
            // speed of several thousand for one tick, and every "is moving" rule would fire on
            // arrival - including the ones meant to notice standing still.
            _hasPosition = false;
        }

        double inArea = Math.Max(0, nowMs - _areaAt) / 1000d;

        if (player is not WorldEntity at)
        {
            _hasPosition = false;
            return new RuleMovement(false, null, inArea);
        }

        double? speed = null;
        if (_hasPosition && nowMs > _lastAt)
        {
            float dx = at.WorldX - _lastX;
            float dy = at.WorldY - _lastY;
            speed = MathF.Sqrt((dx * dx) + (dy * dy)) / ((nowMs - _lastAt) / 1000d);
        }

        _lastX = at.WorldX;
        _lastY = at.WorldY;
        _lastAt = nowMs;
        _hasPosition = true;

        return new RuleMovement(speed >= StillBelow, speed, inArea);
    }

    /// <summary>Forgets everything - leaving the game is not a pause in the same area.</summary>
    public void Reset(long nowMs)
    {
        _hasArea = false;
        _hasPosition = false;
        _areaAt = nowMs;
    }
}
