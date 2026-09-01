namespace PoEformance.Features;

/// <summary>Something a rule wants drawn this tick.</summary>
/// <param name="Text">The caption with its placeholders already filled.</param>
/// <param name="Fill">
/// How full a bar is, 0-1, or null when the number it watches could not be read - which the
/// overlay draws as an empty frame rather than as an empty bar, so "unknown" and "at zero" do
/// not look the same on a life bar.
/// </param>
public sealed record RuleDrawing(string RuleId, string RuleName, RuleEffect Effect, string Text, double? Fill);

/// <summary>An audible cue a rule asked for.</summary>
public readonly record struct RuleSound(string RuleId, int Pitch, int Ms);

/// <summary>Input a rule asked to have synthesised.</summary>
/// <param name="Keys">
/// The virtual-key codes, already resolved - through the game's own bindings when the effect
/// asked for a belt slot. Empty for a mouse or scroll effect.
/// </param>
public sealed record RuleInput(string RuleId, RuleEffectKind Kind, IReadOnlyList<ushort> Keys);

/// <summary>What the engine decided on one tick, including why it decided nothing.</summary>
/// <param name="Reason">
/// Always set, and always the BLOCKING condition when nothing happened. Taken from
/// <see cref="AutoFlask"/>, which took it from the AHK tool: "why did nothing happen" is the
/// question actually asked of a rule engine, and a silent no-op cannot answer it.
/// </param>
public sealed record RuleTick(
    IReadOnlyList<RuleDrawing> Drawings,
    IReadOnlyList<RuleSound> Sounds,
    IReadOnlyList<RuleInput> Inputs,
    string Reason)
{
    public static RuleTick Nothing { get; } = new([], [], [], "not started");

    /// <summary>Whether anything at all came of this tick.</summary>
    public bool Quiet => Drawings.Count == 0 && Sounds.Count == 0 && Inputs.Count == 0;
}

/// <summary>
/// Decides what the configured rules do this tick. Pure: it reads no memory and presses
/// nothing.
/// </summary>
/// <remarks>
/// The split that makes the whole thing testable, and the same one auto-flask uses: this takes
/// facts, a clock and a focus flag and returns what SHOULD happen; the composition root turns
/// that into pixels, sounds and keystrokes. Every rule that matters - priorities, cooldowns,
/// the focus gate, the panel gate - is therefore covered by ordinary tests rather than by
/// playing.
///
/// Where this differs from the reference plugin, and why:
///
///   - INPUT IS GATED IN THE DECISION. Being in the game, having focus, and no panel being
///     open are checked here, not by whoever sends the key, so no future caller can reach the
///     sending path around them. The reference plugin checks focus in three of its four input
///     paths and not in the fourth - its plain key press - so a KeyPress rule types into
///     whatever window the player alt-tabbed to, while its own documentation says input only
///     runs while the game is in front.
///
///   - EVERYTHING IS KEYED ON THE RULE'S ID. Cooldowns, timers and the linger clock. The
///     reference plugin keys them on the rule's NAME, which its own add button leaves as "New
///     rule" for every rule somebody adds.
///
///   - THE EVALUATION IS SEPARATE FROM THE DRAWING. The reference plugin evaluates its rules
///     inside its render callback and performs key presses from inside the loop that draws
///     text, so how often a macro fires depends on the frame rate.
/// </remarks>
public sealed class RuleEngine
{
    // Volatile because the config window replaces these from ITS thread while the reader
    // thread is evaluating. One reference assignment means a tick sees either the old set or
    // the new one, never a half-applied mix - so there is no lock on the evaluation path.
    private volatile RuleSettings _settings = RuleSettings.Default;
    private volatile FlaskKeys _keys = new(new Dictionary<int, ushort>(), KeyBindingSource.Unmatched, "not read");

    private readonly RuleTimers _timers = new();

    /// <summary>
    /// The earliest each rule's effect may act again.
    /// </summary>
    /// <remarks>
    /// A DEADLINE, not the moment it last acted, and that is what makes
    /// <see cref="RuleSettings.CooldownJitterMs"/> work rather than quietly collapse. The
    /// spread has to be drawn ONCE, when the effect fires, and held until it fires again.
    /// Drawing it inside the comparison instead would re-roll it on every tick - sixty chances
    /// a second for a low draw to come up - so the wait would settle near the bottom of the
    /// window. Measured: a 1000 ms cooldown spread by 50 comes out averaging 982 ms that way,
    /// still varied, still looking like the feature working, and systematically FASTER than
    /// the number in the field.
    /// </remarks>
    private readonly Dictionary<string, long> _readyAt = new(StringComparer.Ordinal);

    /// <summary>Until when each drawn effect stays up after its condition stopped holding.</summary>
    private readonly Dictionary<string, long> _showUntil = new(StringComparer.Ordinal);

    /// <summary>The earliest input may be synthesised again, or null when none ever has been.</summary>
    /// <remarks>
    /// Null rather than 0, which is the same trap the removed entity alerts recorded having
    /// fallen into with a different sentinel - kept explicit here even though the deadline form
    /// no longer walks into it, because "nothing has fired yet" is worth being able to see. It
    /// was a TIMESTAMP, and against a clock that starts near zero - a test, a freshly booted
    /// machine - the gap since "never" measured as no gap at all, and the session's first input
    /// was silently swallowed.
    /// </remarks>
    private long? _inputReadyAt;

    /// <summary>The last tick's outcome, for the status readout and the config page.</summary>
    public RuleTick LastTick { get; private set; } = RuleTick.Nothing;

    /// <summary>
    /// Which rule to draw ranges for while it is being built, or empty for none.
    /// </summary>
    /// <remarks>
    /// NOT a setting, and not written to the file: it names whichever rule the editor happens
    /// to have open, which is meaningless the moment the window is shut. Volatile because the
    /// config window sets it from its own thread while the reader is evaluating.
    /// </remarks>
    public string PreviewRuleId
    {
        get => _previewRuleId;
        set => _previewRuleId = value ?? string.Empty;
    }

    private volatile string _previewRuleId = string.Empty;

    /// <summary>The ranges to draw over the game, from the last tick. Empty when off.</summary>
    public IReadOnlyList<PreviewRing> LastPreview { get; private set; } = [];

    /// <summary>Every leaf of the previewed rule with its current verdict. Empty when off.</summary>
    public IReadOnlyList<PreviewFact> LastPreviewFacts { get; private set; } = [];

    /// <summary>How many times any rule has acted since the engine started.</summary>
    public int Acted { get; private set; }

    /// <summary>The rules currently loaded.</summary>
    public RuleSettings Settings => _settings;

    /// <summary>Swaps in a new configuration while running.</summary>
    /// <remarks>
    /// Deliberately does NOT clear the cooldowns: they are kept under rule ids, which survive
    /// an edit, so a rule that just fired keeps its cooldown while somebody types in the field
    /// next to it. Clearing them would hand every rule in the profile a free re-fire on each
    /// keystroke - and on an armed input rule that means a burst of keystrokes into the game.
    /// </remarks>
    public void Configure(RuleSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings.Normalised();
    }

    /// <summary>Takes what was read from the file, including what could not be.</summary>
    public void Configure(RuleLoad load)
    {
        LoadNote = load.Note;
        Configure(load.Settings);
    }

    /// <summary>
    /// What the file could not give us, for the status readout. Empty when it read cleanly.
    /// </summary>
    /// <remarks>
    /// Kept for the session rather than cleared on the next <see cref="Configure(RuleSettings)"/>,
    /// and that is deliberate: the config page republishes the settings on every keystroke, so
    /// clearing it there would wipe the one message explaining why a rule somebody is looking
    /// for is not in the list - within a second of them reading it.
    ///
    /// Volatile for the same reason as <see cref="PreviewRuleId"/>: written on the thread that
    /// loads, read on the one that builds the config page's view.
    /// </remarks>
    public string LoadNote
    {
        get => _loadNote;
        set => _loadNote = value ?? string.Empty;
    }

    private volatile string _loadNote = string.Empty;

    /// <summary>Tells the engine which key the game has bound to each flask slot.</summary>
    public void Bind(FlaskKeys keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        _keys = keys;
    }

    /// <summary>Forgets every cooldown, timer and lingering caption.</summary>
    public void Forget()
    {
        _timers.Forget();
        _readyAt.Clear();
        _showUntil.Clear();
        _inputReadyAt = null;
        LastTick = RuleTick.Nothing;
    }

    /// <summary>
    /// Where the spread on each cooldown comes from.
    /// </summary>
    /// <remarks>
    /// An instance rather than <see cref="Random.Shared"/>, on the same argument as
    /// <see cref="RuleTimers"/> and <see cref="RuleHistory"/>: a test and a live engine in one
    /// process must not draw from the same sequence, and a seeded one makes "the wait landed in
    /// the window" an assertion rather than a sample. Not thread-safe, and does not need to be -
    /// every draw happens inside <see cref="Evaluate"/>, which only the reader thread calls.
    /// </remarks>
    private readonly Random _random;

    public RuleEngine()
        : this(new Random())
    {
    }

    /// <summary>For tests: a source whose draws are known in advance.</summary>
    public RuleEngine(Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        _random = random;
    }

    /// <summary>
    /// A cooldown with its random spread, drawn once for one firing.
    /// </summary>
    /// <remarks>
    /// Centred on the configured wait, so the average cadence stays the one that was asked for -
    /// a spread that only ever delayed would make every rule slower than its own field says.
    /// A cooldown of 0 is returned untouched: it means "as often as allowed", and a spread
    /// there would be inventing a cooldown rather than varying one.
    /// </remarks>
    private long Spread(int cooldownMs, int jitterMs)
    {
        if (cooldownMs <= 0 || jitterMs <= 0)
        {
            return cooldownMs;
        }

        int half = jitterMs / 2;
        return Math.Max(0, cooldownMs + _random.Next(-half, half + 1));
    }

    /// <summary>
    /// The typing floor, staggered - upwards only.
    /// </summary>
    /// <remarks>
    /// Never below the configured gap, because that gap is a SAFETY floor rather than a
    /// preference: it is what stops six freely-firing rules becoming a stream of keystrokes,
    /// and a spread that could dip under it would be this setting quietly raising the tool's
    /// top speed. It is staggered at all because a rule with no cooldown of its own is paced
    /// by this and nothing else, which would put the exact regularity straight back.
    /// </remarks>
    private long Stagger(int gapMs, int jitterMs)
        => gapMs <= 0 || jitterMs <= 0 ? gapMs : gapMs + _random.Next(0, jitterMs + 1);

    /// <summary>Decides what the rules do now.</summary>
    public RuleTick Evaluate(RuleState state, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(state);

        RuleTick tick = Decide(state, nowMs);
        LastTick = tick;

        // Alongside the decision rather than inside it, and computed on this thread rather than
        // when the overlay asks: the renderer draws far more often than a snapshot arrives, so
        // asking there would re-read the same facts sixty times a second - and the numbers on
        // the rings would be a different moment from the one the rule acted on.
        (LastPreview, LastPreviewFacts) = Previewed(state);
        return tick;
    }

    /// <summary>What the rule being edited measures and asks, or nothing when nothing is edited.</summary>
    private (IReadOnlyList<PreviewRing> Rings, IReadOnlyList<PreviewFact> Facts) Previewed(RuleState state)
    {
        string id = _previewRuleId;
        if (id.Length == 0 || _settings.Current is not RuleProfile profile)
        {
            return ([], []);
        }

        foreach (RuleGroup group in profile.Groups)
        {
            foreach (Rule rule in group.Rules)
            {
                if (string.Equals(rule.Id, id, StringComparison.Ordinal))
                {
                    // Whether the rule is ENABLED does not matter here. A rule is switched off
                    // for most of the time it is being built, and refusing to show its ranges
                    // then would take the tool away exactly when it is wanted.
                    return (RulePreview.Rings(rule.Condition, state), RulePreview.Facts(rule.Condition, state));
                }
            }
        }

        return ([], []);
    }

    private RuleTick Decide(RuleState state, long nowMs)
    {
        RuleSettings settings = _settings;
        if (!settings.Enabled)
        {
            return new RuleTick([], [], [], "off");
        }

        if (settings.Current is not RuleProfile profile)
        {
            return new RuleTick([], [], [], "no profile");
        }

        _timers.Tick(nowMs);

        List<Rule> firing = Firing(profile, state);
        var drawings = new List<RuleDrawing>();
        var sounds = new List<RuleSound>();
        var inputs = new List<RuleInput>();
        var blocked = new List<string>();

        foreach (Rule rule in firing)
        {
            for (int index = 0; index < rule.Effects.Count; index++)
            {
                Act(rule, index, state, settings, nowMs, drawings, sounds, inputs, blocked);
            }
        }

        // Captions from rules that have STOPPED holding, for as long as each asked to linger.
        // Added after the firing ones so a rule that is still true wins its own slot rather
        // than being drawn twice.
        Lingering(profile, state, nowMs, firing, drawings);

        if (drawings.Count > 0 || sounds.Count > 0 || inputs.Count > 0)
        {
            Acted += sounds.Count + inputs.Count;
            return new RuleTick(drawings, sounds, inputs, Describe(firing, blocked));
        }

        return new RuleTick([], [], [], blocked.Count > 0
            ? string.Join(", ", blocked)
            : $"watching {Rules(profile)} rules");
    }

    /// <summary>The rules whose conditions hold, after priority has had its say.</summary>
    private List<Rule> Firing(RuleProfile profile, RuleState state)
    {
        var firing = new List<Rule>();
        foreach (RuleGroup group in profile.Groups)
        {
            if (!group.AppliesIn(state))
            {
                continue;
            }

            foreach (Rule rule in group.Rules)
            {
                if (rule.Enabled && !rule.Condition.SaysNothing && rule.Condition.Holds(state, _timers, rule.Id))
                {
                    firing.Add(rule);
                }
            }
        }

        if (firing.Count < 2)
        {
            return firing;
        }

        // Highest priority first, and a stable sort so two rules of equal priority keep the
        // order the profile lists them in - otherwise which of two captions is drawn on top
        // changes between ticks for no reason anybody can see.
        firing.Sort(static (left, right) => right.Priority.CompareTo(left.Priority));

        int top = firing[0].Priority;
        bool suppresses = false;
        foreach (Rule rule in firing)
        {
            if (rule.Priority == top && !rule.AllowLower)
            {
                suppresses = true;
                break;
            }
        }

        if (suppresses)
        {
            firing.RemoveAll(rule => rule.Priority < top);
        }

        return firing;
    }

    private void Act(
        Rule rule,
        int index,
        RuleState state,
        RuleSettings settings,
        long nowMs,
        List<RuleDrawing> drawings,
        List<RuleSound> sounds,
        List<RuleInput> inputs,
        List<string> blocked)
    {
        RuleEffect effect = rule.Effects[index];

        // Captions and cues alike: an effect that makes itself noticed while the player is in
        // another application is doing it in that application, and a beep is the half of that
        // nobody can ignore.
        if (!effect.Sends && !state.GameFocused && !settings.NoticeInBackground)
        {
            blocked.Add($"{rule.Name}: game not focused");
            return;
        }

        if (effect.Draws)
        {
            drawings.Add(Draw(rule, effect, state));
            _showUntil[Key(rule, index)] = nowMs + effect.LingerMs;
            return;
        }

        // From here on the effect DOES something outside this process, so every gate applies.
        if (effect.Sends && Refuse(state) is string refusal)
        {
            blocked.Add($"{rule.Name}: {refusal}");
            return;
        }

        string key = Key(rule, index);
        if (_readyAt.TryGetValue(key, out long ready) && nowMs < ready)
        {
            blocked.Add($"{rule.Name}: cooling down");
            return;
        }

        if (effect.Sends && _inputReadyAt is long allowed && nowMs < allowed)
        {
            // Not stamped as acted: the rule did not get its turn, and stamping would make it
            // sit out its own cooldown for something the engine did rather than something it
            // did. The very next tick after the gap can fire it.
            blocked.Add($"{rule.Name}: input too soon");
            return;
        }

        if (effect.Kind == RuleEffectKind.Sound)
        {
            _readyAt[key] = nowMs + Spread(effect.CooldownMs, settings.CooldownJitterMs);
            sounds.Add(new RuleSound(rule.Id, effect.Pitch, effect.SoundMs));
            return;
        }

        IReadOnlyList<ushort> codes = Codes(effect);
        if (Needs(effect.Kind) && codes.Count == 0)
        {
            // Nothing to press. Reported rather than skipped silently, because an unbound key
            // and a condition that never holds look identical from outside and the fix is
            // completely different.
            blocked.Add($"{rule.Name}: no key to press");
            return;
        }

        _readyAt[key] = nowMs + Spread(effect.CooldownMs, settings.CooldownJitterMs);
        _inputReadyAt = nowMs + Stagger(settings.MinInputGapMs, settings.CooldownJitterMs);
        inputs.Add(new RuleInput(rule.Id, effect.Kind, codes));
    }

    /// <summary>
    /// Why input must not be synthesised right now, or null when it may be.
    /// </summary>
    /// <remarks>
    /// Three conditions, none of them a preference:
    ///
    ///   - NOT IN THE GAME. A key pressed on a loading screen or in a menu goes somewhere, and
    ///     none of those places is the character.
    ///   - THE GAME DOES NOT HAVE FOCUS. Keystrokes land wherever focus is, which during an
    ///     alt-tab is a browser, a chat window or somebody's text editor.
    ///   - A PANEL IS OPEN. A stash, an atlas or a skill tree has its own key handling, and
    ///     the keys a combat rule presses do different things inside one.
    /// </remarks>
    private static string? Refuse(RuleState state)
    {
        if (!state.InGame)
        {
            return "not in game";
        }

        if (!state.GameFocused)
        {
            return "game not focused";
        }

        return state.InPanel ? "a panel is open" : null;
    }

    private void Lingering(
        RuleProfile profile,
        RuleState state,
        long nowMs,
        List<Rule> firing,
        List<RuleDrawing> drawings)
    {
        if (_showUntil.Count == 0)
        {
            return;
        }

        var acting = new HashSet<string>(StringComparer.Ordinal);
        foreach (Rule rule in firing)
        {
            acting.Add(rule.Id);
        }

        foreach (RuleGroup group in profile.Groups)
        {
            foreach (Rule rule in group.Rules)
            {
                if (acting.Contains(rule.Id))
                {
                    continue;
                }

                for (int index = 0; index < rule.Effects.Count; index++)
                {
                    RuleEffect effect = rule.Effects[index];
                    if (!effect.Draws)
                    {
                        continue;
                    }

                    string key = Key(rule, index);
                    if (!_showUntil.TryGetValue(key, out long until))
                    {
                        continue;
                    }

                    if (nowMs >= until)
                    {
                        _showUntil.Remove(key);
                        continue;
                    }

                    // Drawn from the CURRENT facts rather than from the ones that fired it: a
                    // caption showing a life percentage should show what life is now, and
                    // freezing it would make a lingering readout indistinguishable from a
                    // stalled one.
                    drawings.Add(Draw(rule, effect, state));
                }
            }
        }
    }

    private static RuleDrawing Draw(Rule rule, RuleEffect effect, RuleState state)
    {
        double? fill = null;
        if (effect.Kind == RuleEffectKind.Bar)
        {
            double? watched = RuleFacts.Answer(new RuleCondition { Fact = effect.Watching }, state);

            // Percentages fill their own bar; anything else has no natural full mark, so it is
            // shown against 100 and a bar of monsters is simply a bar that rarely moves. Null
            // travels as null - see the parameter's own note.
            fill = watched is double number ? Math.Clamp(number / 100d, 0d, 1d) : null;
        }

        return new RuleDrawing(rule.Id, rule.Name, effect, RuleText.Fill(effect.Text, state), fill);
    }

    /// <summary>The keys an effect presses, resolved through the game's bindings when asked.</summary>
    private IReadOnlyList<ushort> Codes(RuleEffect effect)
    {
        if (effect.Kind == RuleEffectKind.KeySequence)
        {
            return RuleKeys.Sequence(effect.Keys);
        }

        if (!Needs(effect.Kind))
        {
            return [];
        }

        if (effect.KeySource == KeySource.FlaskSlot)
        {
            // Live from the game's own config, never stored here. A rebound flask therefore
            // changes what this rule presses without anybody editing the rule - which is the
            // whole reason the binding is not copied into the effect.
            return _keys.BySlot.TryGetValue(effect.Slot, out ushort bound) && bound != 0 ? [bound] : [];
        }

        ushort code = RuleKeys.Code(effect.Key);
        return code == 0 ? [] : [code];
    }

    private static bool Needs(RuleEffectKind kind)
        => kind is RuleEffectKind.KeyPress or RuleEffectKind.KeyDown
            or RuleEffectKind.KeyUp or RuleEffectKind.KeySequence;

    /// <summary>What a cooldown and a linger are remembered under.</summary>
    /// <remarks>
    /// The rule's id and the effect's place in its list, so two key presses in one rule keep
    /// their own cooldowns and neither moves when the rule is renamed.
    /// </remarks>
    private static string Key(Rule rule, int index)
        => rule.Id + "#" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Describe(List<Rule> firing, List<string> blocked)
    {
        var names = new List<string>(firing.Count);
        foreach (Rule rule in firing)
        {
            names.Add(rule.Name);
        }

        string acting = names.Count > 0 ? string.Join(", ", names) : "nothing";
        return blocked.Count > 0 ? $"{acting} ({string.Join(", ", blocked)})" : acting;
    }

    private static int Rules(RuleProfile profile)
    {
        int total = 0;
        foreach (RuleGroup group in profile.Groups)
        {
            total += group.Rules.Count;
        }

        return total;
    }
}
