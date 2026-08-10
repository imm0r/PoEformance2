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
/// <param name="Life">
/// The whole health pool, not just the current value. Carried because it comes out of the
/// SAME read - the corpse check needs current health, and the span that holds it holds the
/// maximum beside it, so a health bar costs nothing that was not already being paid for.
/// </param>
/// <param name="EnergyShield">The shield pool, which sits in the same span.</param>
/// <param name="HasLife">
/// Whether the entity carries a Life component at all. Null when the component list could
/// not be read - which is not the same as "absent", and the difference decides whether a
/// live monster stays on the overlay.
/// </param>
/// <param name="HasTargetable">
/// Whether the entity carries a Targetable component at all, on the same terms: null means
/// nobody could look. Different from <paramref name="Targetable"/>, which is what the
/// component SAYS - a boss mid-phase is untargetable and still has one.
/// </param>
public readonly record struct MonsterSigns(
    int? Health,
    bool? Targetable,
    bool IsBoss,
    ItemRarity Rarity = ItemRarity.Unknown,
    Vital Life = default,
    Vital EnergyShield = default,
    bool Friendly = false,
    bool Temporary = false,
    bool? HasLife = null,
    bool? HasTargetable = null)
{
    /// <summary>
    /// Whether this is a passing effect rather than a monster - whichever side it is on.
    /// </summary>
    /// <remarks>
    /// Ground effects are built out of the same components a monster is - they sit in the
    /// entity map, they have a position, some of them carry Life - so anything that decides
    /// "monster" from the path alone draws a flame wall as an enemy and puts a health bar
    /// over it. That is what a screen full of unexplained dots turned out to be.
    ///
    /// TWO RULES, because one of them missed a whole class of them.
    ///
    /// The first is the reference's, including the let-out: a thing that expires on its own
    /// counts as an effect only when it cannot be TARGETED, because some genuinely summoned
    /// monsters expire too and those are worth seeing. An entity with no targetable component
    /// at all falls the same way as an untargetable one, which is deliberate - it has offered
    /// no evidence that it is something you can fight.
    ///
    /// The second is that a monster HAS A LIFE COMPONENT. That is the reference's own test
    /// for whether an entity is a monster at all, and it catches the ones that expire without
    /// saying so. Counted over a recorded map, every monster-path entity that lacked one was
    /// unmistakably not a monster:
    ///
    /// <code>
    ///   LegionnaireSmokeGround   x76   8 components, GroundEffect + ControlZone
    ///   LightningArrow           x36   5 components - a projectile
    ///   Tier3OuterFires          x7    8 components
    ///   AtziriArenaSnakeHeads    x6    5 components - arena scenery
    /// </code>
    ///
    /// All four hostile, all four drawn as enemies. Against 25 names with Life AND Targetable
    /// over the same map, which are the real monsters, and Firewall - the player's own, with
    /// Life and no Targetable - which the friendly rule already handles. Keying on the
    /// GroundEffect component instead would have caught only the first of the four.
    ///
    /// The third is that a FRIENDLY thing nothing can target is one of your own effects
    /// rather than one of your own minions. The overlay already said as much in its own words
    /// - "a minion or a totem is targetable, so it is not an effect and stays drawn" - but it
    /// was reading that off the expiring rule, and a flame wall does not expire by any signal
    /// this can see. Over the recorded map it carries Life 1/1, no DiesAfterTime and no
    /// Targetable component at all, 9,313 sightings of it, and every one was drawn as a dot.
    ///
    /// Only for the friendly ones, and the asymmetry is the argument. For something hostile,
    /// "nothing can target it" is thin evidence and hiding a live monster is the expensive
    /// mistake. For something of your own, it is the difference between a minion - which
    /// enemies target, so it has the component - and a cast effect, and the cost of being
    /// wrong is a dot you wanted. NOT VERIFIED against a minion or a totem: the recording
    /// this comes from has exactly one friendly name in it.
    ///
    /// AND THE DURATION IS NOT ON THE ENTITY, which was worth an evening to establish and is
    /// written down so nobody spends a second one. The game prints a flame wall's duration in
    /// its own tooltip - 10.30 seconds - so "it obviously expires, read that" is the natural
    /// next thought. It is not there to read. Looked at through the entity inspector, on a
    /// wall selected while recording:
    ///
    ///   - no DiesAfterTime component, over 9,313 sightings;
    ///   - a Buffs component carrying nothing at all;
    ///   - a Stats component whose two StatsInternal pointers are the SAME pointer, holding
    ///     exactly one pair: id 13189, value 1. Not a duration, and not a number that changes.
    ///
    /// So the duration belongs to the skill on the player, not to the thing the skill spawns,
    /// and targetability is the only signal on the entity that answers "is this something
    /// anybody fights". Which is what this rule already uses.
    ///
    /// Null means the component list could not be read, and that makes no claim either way:
    /// classifying an unreadable entity as an effect would drop live monsters off the overlay
    /// exactly when the reading is going badly.
    /// </remarks>
    public bool IsEffect => HasLife == false
                            || (Friendly && HasTargetable == false)
                            || (Temporary && Targetable != true);

    /// <summary>
    /// Whether the reader should drop it outright rather than hand it on.
    /// </summary>
    /// <remarks>
    /// Only the hostile ones. A friendly effect is still an effect, but discarding it is a
    /// choice about what to DRAW, and that belongs to whoever is drawing - so it travels on
    /// the entity as <see cref="IsEffect"/> instead of being decided here.
    ///
    /// Keeping the two apart is not decoration: dropping friendly effects from the snapshot
    /// would take your own minions' totems and the inspector's view of them with it, and
    /// leaving the distinction unnamed is what let the flame wall lose its health bar and
    /// keep its dot.
    /// </remarks>
    public bool IsHostileEffect => !Friendly && IsEffect;
}

/// <summary>
/// What the corpse check SAW this read, so a screen full of dots can be explained.
/// </summary>
/// <remarks>
/// The whole rule hangs on one byte, and there are exactly three ways it can leave dots on
/// cleared ground - which look identical on screen and want completely different fixes:
///
///   - <paramref name="Targetable"/> high while the ground is clear: the byte says those
///     monsters are alive. Either its offset has drifted or it no longer means what it did,
///     and no amount of timing will help.
///   - <paramref name="Unreadable"/> high: the component is not being found at all, so the
///     filter is being asked to decide with no evidence and correctly refuses to.
///   - <paramref name="Untargetable"/> high while dots remain: the byte is right and the
///     clock is not running out - which is a fault in the timing, not in the reading.
///
/// Counted rather than reasoned about because the first two were each guessed wrong once.
///
/// Over the HOSTILE, non-effect monsters only, because those are the ones a leftover dot
/// would be. Counting everything made this useless in exactly the situation it was built
/// for: a Firewall build puts twenty of its own walls on the ground, none of which carries
/// a Targetable component, and the readout announced that the component was "mostly not
/// being found" every time the player cast. A counter that cries wolf is one nobody reads.
/// </remarks>
/// <param name="Tracking">Monsters whose untargetable clock is currently running.</param>
public readonly record struct CorpseSigns(int Targetable, int Untargetable, int Unreadable, int Tracking)
{
    /// <summary>Monsters the check looked at this read.</summary>
    public int Seen => Targetable + Untargetable + Unreadable;

    /// <summary>The finding in one line, said as what it would mean.</summary>
    public string Describe()
    {
        if (Seen == 0)
        {
            return "no monsters to judge";
        }

        string counts = $"{Seen} judged: {Targetable} targetable, {Untargetable} not, {Unreadable} unreadable"
                        + $"; {Tracking} on the clock";

        if (Unreadable > Targetable + Untargetable)
        {
            return $"{counts}  -  the Targetable component is mostly NOT BEING FOUND";
        }

        return counts;
    }
}

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
    /// <param name="identity">
    /// What identifies the MONSTER across readings - its Render component, not an entity
    /// address.
    /// </param>
    /// <remarks>
    /// The distinction is load-bearing, and getting it wrong looks exactly like this filter
    /// not working. The game wears several entities on one monster and the reader keeps only
    /// one of them, chosen by whichever the entity-map walk reaches first - which shifts as
    /// the tree rebalances. Timed against an entity, a flip in that choice restarts the clock
    /// below, it never reaches <see cref="UntargetableMs"/>, and the screen keeps its dots.
    /// </remarks>
    public bool IsCorpse(ulong identity, MonsterSigns signs, long nowMs)
    {
        Prune(nowMs);

        // Dead outright. Nothing to time, and the tracking entry can go.
        if (signs.Health is int health && health <= 0)
        {
            _untargetable.Remove(identity);
            return true;
        }

        if (signs.Targetable is not bool targetable)
        {
            return false; // no signal - keep it, an unreadable component is not a death
        }

        if (targetable)
        {
            _untargetable.Remove(identity);
            return false;
        }

        if (signs.IsBoss)
        {
            return false; // untargetable phases are part of the fight
        }

        if (!_untargetable.TryGetValue(identity, out (long Since, long Seen) tracked))
        {
            // First untargetable reading. Start the clock rather than deciding now: the
            // byte dips during the death animation, and a monster that is merely mid-blink
            // would otherwise flicker off the overlay.
            _untargetable[identity] = (nowMs, nowMs);
            return false;
        }

        _untargetable[identity] = (tracked.Since, nowMs);
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
