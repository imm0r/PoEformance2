using PoEformance.Game.Components;

namespace PoEformance.Game.World;

/// <summary>What one monster's memory says about whether it is still alive.</summary>
/// <param name="Health">
/// Current health, or null when the Life component is absent - which is NOT the same as
/// zero. A component that could not be found tells us nothing; a component that reads zero
/// tells us the monster is dead, and conflating the two is how live monsters disappear.
/// </param>
/// <param name="Targetable">Null when the Targetable component is absent, same reasoning.</param>
/// <param name="IsBoss">
/// Unique or boss rarity. Exempt from the targetable rule: bosses have legitimate
/// multi-second untargetable phases, and hiding one mid-fight is worse than showing a corpse.
/// </param>
/// <param name="Rarity">
/// How rare the monster is, on the same 0-3 scale items use. <see cref="ItemRarity.Unknown"/>
/// when it could not be read, which is not the same as Normal - "no answer" and "an ordinary
/// monster" want different treatment on a radar.
/// </param>
public readonly record struct MonsterSigns(
    int? Health, bool? Targetable, bool IsBoss, ItemRarity Rarity = ItemRarity.Unknown);

/// <summary>
/// Decides which monsters are corpses, so the overlay stops marking them.
/// </summary>
/// <remarks>
/// PORTED FROM THE AHK TOOL's <c>_FilterStaleRadarEntities</c>, which arrived at this after
/// several wrong answers that are worth not repeating:
///
///   - Health alone is not enough. A corpse can keep reading HP above zero indefinitely
///     while its Targetable byte goes to 0, which is exactly the case that leaves red dots
///     scattered over a cleared screen.
///   - Targetable alone is not enough either. Bosses go untargetable during phase
///     transitions, so the rule has to exempt them or they vanish at the worst moment.
///   - A signal the reference tried and REMOVED: "untargetable for N seconds since first
///     seen". It read as dead for every live monster, because its reader samples a rotating
///     subset and an entity's first sighting can be seconds before its second. This port
///     reads every entity every frame, so a duration measured from the first UNTARGETABLE
///     reading - not from first sight - is meaningful here in a way it was not there.
///
/// Deliberately NOT ported: the entity Flags validity bit. The schema marks that offset
/// unverified on the 0.5 client, and a wrong bit there would hide every entity at once.
/// Health and targetable are both verified, and between them they cover this.
/// </remarks>
public sealed class CorpseFilter
{
    /// <summary>How long a monster must read untargetable before it counts as a corpse.</summary>
    /// <remarks>
    /// Not zero, because the byte flickers during the death animation and around phase
    /// changes; short, because the alternative is a dot sitting on a corpse. The reference
    /// counts 10 consecutive readings instead of milliseconds, which amounts to the same
    /// thing at its sampling rate.
    /// </remarks>
    public int UntargetableMs { get; init; } = 400;

    /// <summary>Drop tracking for an entity not seen for this long - it left the area.</summary>
    private const int ForgetMs = 10_000;

    private readonly Dictionary<ulong, (long Since, long Seen)> _untargetable = [];
    private long _lastPrune;

    /// <summary>Number of monsters currently being timed - for the diagnostic readout.</summary>
    public int Tracking => _untargetable.Count;

    /// <summary>True when this monster should be treated as a corpse.</summary>
    public bool IsCorpse(ulong address, MonsterSigns signs, long nowMs)
    {
        Prune(nowMs);

        // Dead outright. Nothing to time, and the tracking entry can go.
        if (signs.Health is int health && health <= 0)
        {
            _untargetable.Remove(address);
            return true;
        }

        if (signs.Targetable is not bool targetable)
        {
            return false; // no signal - keep it, an unreadable component is not a death
        }

        if (targetable)
        {
            _untargetable.Remove(address);
            return false;
        }

        if (signs.IsBoss)
        {
            return false; // untargetable phases are part of the fight
        }

        if (!_untargetable.TryGetValue(address, out (long Since, long Seen) tracked))
        {
            // First untargetable reading. Start the clock rather than deciding now: the
            // byte dips during the death animation, and a monster that is merely mid-blink
            // would otherwise flicker off the overlay.
            _untargetable[address] = (nowMs, nowMs);
            return false;
        }

        _untargetable[address] = (tracked.Since, nowMs);
        return nowMs - tracked.Since >= UntargetableMs;
    }

    /// <summary>Forgets entities that have left, so a long session cannot grow the map.</summary>
    /// <remarks>
    /// By last-seen rather than on area change, which also handles the game reusing an
    /// address for a new entity: a live monster reads targetable and clears its entry on
    /// the very first frame, so a stale timer cannot outlive its owner.
    /// </remarks>
    private void Prune(long nowMs)
    {
        if (nowMs - _lastPrune < 1000)
        {
            return;
        }

        _lastPrune = nowMs;
        foreach ((ulong address, (long _, long seen)) in _untargetable)
        {
            if (nowMs - seen > ForgetMs)
            {
                _untargetable.Remove(address);
            }
        }
    }
}
