using PoEformance.Game.Components;

namespace PoEformance.Features;

/// <summary>Which pool a rule watches.</summary>
public enum VitalKind
{
    Life,
    Mana,
    EnergyShield,
}

/// <summary>One flask: what it watches, when it fires, and which key uses it.</summary>
/// <param name="Key">Virtual-key code of the flask's hotkey.</param>
/// <param name="ThresholdPercent">
/// Fires at or below this fill level of the USABLE pool. Ignored when the rule triggers on
/// a buff instead (<see cref="TriggerBuff"/>).
/// </param>
/// <param name="CooldownMs">
/// Minimum gap between uses of THIS flask. Not a cosmetic limit: recovery is not
/// instantaneous, so without it the pool is still low on the next tick and the whole flask
/// belt drains in a fraction of a second.
/// </param>
public sealed record FlaskRule(
    string Name,
    VitalKind Vital,
    int ThresholdPercent,
    ushort Key,
    int CooldownMs = 1500)
{
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Belt slot 1-5, so the flask's own buff can be recognised. 0 disables that check.
    /// </summary>
    /// <remarks>
    /// Worth configuring even though the key already identifies the flask: the game reports
    /// an active flask buff by SLOT, not by key, so without this the engine cannot tell
    /// whether this particular flask is already doing its job.
    /// </remarks>
    public int Slot { get; init; }

    /// <summary>
    /// Don't re-use while this flask's own effect is still running.
    /// </summary>
    /// <remarks>
    /// The real charge saver. A cooldown is a guess at the duration; the buff is the fact,
    /// and duration varies with the flask's modifiers. Only meaningful with a
    /// <see cref="Slot"/>.
    /// </remarks>
    public bool SkipWhileActive { get; init; } = true;

    /// <summary>
    /// Fire when a buff or debuff whose name contains this is present, instead of on a
    /// vital threshold. Empty means threshold-triggered.
    /// </summary>
    /// <remarks>
    /// This is what makes utility flasks work: bleed and freeze removal are not low-life
    /// events, they are "this specific thing is on me right now" events, and a life
    /// threshold either fires far too late or not at all.
    /// </remarks>
    public string TriggerBuff { get; init; } = string.Empty;
}

/// <summary>A flask the engine decided to use, and the reading that triggered it.</summary>
public sealed record FlaskUse(FlaskRule Rule, int Percent);

/// <summary>What the engine did on one tick, including why it did nothing.</summary>
/// <param name="Reason">
/// Human-readable, always set. Ported from the AHK tool's habit of naming the blocking
/// condition: "why did nothing happen" is the question actually asked in practice, and a
/// silent no-op cannot answer it.
/// </param>
public sealed record FlaskTick(IReadOnlyList<FlaskUse> Used, string Reason);

/// <summary>
/// Decides when to use flasks. Pure: it reads no memory and presses no keys.
/// </summary>
/// <remarks>
/// The whole point of the split is that the DECISION is testable without a game. This class
/// takes vitals, a clock and a focus flag, and returns which flasks should fire; the caller
/// does the pressing. Every rule that matters - thresholds, cooldowns, the dead guard, the
/// focus gate - is therefore covered by ordinary unit tests instead of by playing.
///
/// The focus gate deserves its own note, because it is a safety property rather than a
/// feature: keystrokes go to whatever window has focus. Firing while the player has alt-
/// tabbed would type flask keys into a browser, a chat window or a text editor. So input is
/// gated on the GAME being foreground, and the gate lives here, in the decision, rather
/// than being left to whoever sends the keys.
/// </remarks>
public sealed class AutoFlask
{
    private readonly Dictionary<string, long> _lastUsed = [];

    // Volatile because the config window changes these from ITS thread while the reader
    // thread is evaluating. Replacing the whole rule list in one reference assignment
    // means a tick sees either the old set or the new one, never a half-applied mix - no
    // lock on the evaluation path.
    private volatile IReadOnlyList<FlaskRule> _rules;
    private volatile bool _enabled;

    /// <summary>The configured flasks.</summary>
    public IReadOnlyList<FlaskRule> Rules => _rules;

    /// <summary>Master switch, so the feature can be off without unloading it.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>The last tick's outcome, for display.</summary>
    public FlaskTick LastTick { get; private set; } = new([], "not started");

    public AutoFlask(IReadOnlyList<FlaskRule> rules, bool enabled = false)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = rules;
        _enabled = enabled;
    }

    /// <summary>Swaps in a new configuration while running.</summary>
    /// <remarks>
    /// Deliberately does NOT clear the cooldown history: the rules are keyed by name, so a
    /// flask that just fired keeps its cooldown across an edit. Otherwise every keystroke
    /// typed into a threshold field would hand the belt a free re-fire.
    /// </remarks>
    public void Configure(IReadOnlyList<FlaskRule> rules, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = rules;
        _enabled = enabled;
    }

    /// <summary>
    /// Decides which flasks to use now.
    /// </summary>
    /// <param name="vitals">The player's pools, or null when they could not be read.</param>
    /// <param name="gameFocused">Whether the GAME window has focus - see the class remarks.</param>
    /// <param name="nowMs">A monotonic clock, e.g. Environment.TickCount64.</param>
    /// <param name="belt">
    /// The equipped flasks. Optional: when unknown, charge checking is skipped rather than
    /// blocking every rule - a belt that could not be read must not disable the feature.
    /// </param>
    public FlaskTick Evaluate(
        Vitals? vitals, bool gameFocused, long nowMs, ActiveBuffs? buffs = null, FlaskBelt? belt = null)
    {
        FlaskTick tick = Decide(vitals, gameFocused, nowMs, buffs ?? ActiveBuffs.None, belt ?? FlaskBelt.Empty);
        LastTick = tick;
        return tick;
    }

    private FlaskTick Decide(Vitals? vitals, bool gameFocused, long nowMs, ActiveBuffs buffs, FlaskBelt belt)
    {
        if (!Enabled)
        {
            return new FlaskTick([], "disabled");
        }

        if (!gameFocused)
        {
            // Not a nicety: keystrokes land wherever focus is.
            return new FlaskTick([], "game not focused");
        }

        if (vitals is not Vitals pools)
        {
            return new FlaskTick([], "vitals unreadable");
        }

        if (pools.IsDeadOrUnloaded)
        {
            // At zero life every threshold reads as the worst possible emergency, so this
            // guard is what stops the belt draining into a corpse or a loading screen.
            return new FlaskTick([], "dead or not loaded");
        }

        var used = new List<FlaskUse>();
        var blocked = new List<string>();

        foreach (FlaskRule rule in Rules)
        {
            if (!rule.Enabled)
            {
                continue;
            }

            int percent;
            if (rule.TriggerBuff.Length > 0)
            {
                // Buff-triggered: the vital is irrelevant, the debuff being present is the
                // whole condition.
                if (!buffs.Has(rule.TriggerBuff))
                {
                    continue;
                }

                percent = -1;
            }
            else
            {
                Vital vital = Select(pools, rule.Vital);
                percent = vital.Percent;
                if (percent < 0)
                {
                    blocked.Add($"{rule.Name}: no {rule.Vital.ToString().ToLowerInvariant()} pool");
                    continue;
                }

                if (percent > rule.ThresholdPercent)
                {
                    continue; // above threshold is the normal case, not worth reporting
                }
            }

            // The flask is already doing its job - a charge spent here buys nothing.
            if (rule.SkipWhileActive && rule.Slot > 0 && buffs.IsFlaskActive(rule.Slot))
            {
                blocked.Add($"{rule.Name}: already active");
                continue;
            }

            // Not enough charges. The GAME would simply ignore the press - nothing
            // triggers, no charge is spent, no in-game cooldown starts - so this gate is
            // not about protecting the game from us. It protects OUR OWN state: firing
            // anyway would stamp _lastUsed below and record a use that never happened,
            // and this rule would then sit out its cooldown. If the charges come back
            // during that window, the flask stays unused for no reason. Skipping without
            // stamping means the very next tick after a refill can fire.
            if (rule.Slot > 0 && belt.InSlot(rule.Slot) is EquippedFlask flask)
            {
                // Charms trigger themselves; there is no key that uses one, so a rule aimed
                // at a charm slot would send keystrokes that can never have an effect.
                if (flask.IsCharm)
                {
                    blocked.Add($"{rule.Name}: slot {rule.Slot} holds a charm (self-triggering)");
                    continue;
                }

                if (!flask.CanUse)
                {
                    blocked.Add($"{rule.Name}: {flask.Charges}/{flask.ChargesPerUse} charges");
                    continue;
                }
            }

            if (_lastUsed.TryGetValue(rule.Name, out long last) && nowMs - last < rule.CooldownMs)
            {
                blocked.Add($"{rule.Name}: cooling down");
                continue;
            }

            _lastUsed[rule.Name] = nowMs;
            used.Add(new FlaskUse(rule, percent));
        }

        if (used.Count > 0)
        {
            return new FlaskTick(used, string.Join(", ", used.Select(u =>
                u.Percent < 0 ? $"{u.Rule.Name} (buff)" : $"{u.Rule.Name} at {u.Percent}%")));
        }

        return new FlaskTick([], blocked.Count > 0 ? string.Join(", ", blocked) : Describe(pools));
    }

    /// <summary>The idle readout: the numbers being watched, so "nothing happened" is legible.</summary>
    private static string Describe(Vitals pools)
        => $"watching (life {Show(pools.Life)}, mana {Show(pools.Mana)}, es {Show(pools.EnergyShield)})";

    private static string Show(Vital vital) => vital.Percent < 0 ? "-" : $"{vital.Percent}%";

    private static Vital Select(Vitals pools, VitalKind kind) => kind switch
    {
        VitalKind.Life => pools.Life,
        VitalKind.Mana => pools.Mana,
        VitalKind.EnergyShield => pools.EnergyShield,
        _ => default,
    };

    // There is deliberately no DefaultRules here. The defaults live in AutoFlaskSettings,
    // which is what the user edits and what gets written to disk; a second set of defaults
    // in the engine would be a second source of truth that quietly disagrees with the file.
}
