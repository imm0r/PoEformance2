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
/// <param name="ThresholdPercent">Fires at or below this fill level of the USABLE pool.</param>
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

    /// <summary>The configured flasks.</summary>
    public IReadOnlyList<FlaskRule> Rules { get; }

    /// <summary>Master switch, so the feature can be off without unloading it.</summary>
    public bool Enabled { get; set; }

    /// <summary>The last tick's outcome, for display.</summary>
    public FlaskTick LastTick { get; private set; } = new([], "not started");

    public AutoFlask(IReadOnlyList<FlaskRule> rules, bool enabled = false)
    {
        ArgumentNullException.ThrowIfNull(rules);
        Rules = rules;
        Enabled = enabled;
    }

    /// <summary>
    /// Decides which flasks to use now.
    /// </summary>
    /// <param name="vitals">The player's pools, or null when they could not be read.</param>
    /// <param name="gameFocused">Whether the GAME window has focus - see the class remarks.</param>
    /// <param name="nowMs">A monotonic clock, e.g. Environment.TickCount64.</param>
    public FlaskTick Evaluate(Vitals? vitals, bool gameFocused, long nowMs)
    {
        FlaskTick tick = Decide(vitals, gameFocused, nowMs);
        LastTick = tick;
        return tick;
    }

    private FlaskTick Decide(Vitals? vitals, bool gameFocused, long nowMs)
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

            Vital vital = Select(pools, rule.Vital);
            int percent = vital.Percent;
            if (percent < 0)
            {
                blocked.Add($"{rule.Name}: no {rule.Vital.ToString().ToLowerInvariant()} pool");
                continue;
            }

            if (percent > rule.ThresholdPercent)
            {
                continue; // above threshold is the normal case, not worth reporting
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
            return new FlaskTick(used, string.Join(", ", used.Select(u => $"{u.Rule.Name} at {u.Percent}%")));
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

    /// <summary>The defaults: a life flask on 1 and a mana flask on 2.</summary>
    /// <remarks>
    /// Thresholds sit where a flask still helps rather than where it is already too late,
    /// and OFF by default - a tool that starts pressing keys the first time it runs is not
    /// one anybody can trust.
    /// </remarks>
    public static IReadOnlyList<FlaskRule> DefaultRules { get; } =
    [
        new FlaskRule("Life flask", VitalKind.Life, ThresholdPercent: 65, Key: 0x31),  // '1'
        new FlaskRule("Mana flask", VitalKind.Mana, ThresholdPercent: 30, Key: 0x32),  // '2'
    ];
}
