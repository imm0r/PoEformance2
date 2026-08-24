using System.Text.Json.Serialization;

namespace PoEformance.Features;

/// <summary>What a rule does when it holds.</summary>
/// <remarks>
/// Split three ways on purpose - drawn, heard, sent - because those three have completely
/// different safety properties. Drawing is free and reversible. A sound is a nuisance at worst.
/// SENDING INPUT goes out to whatever window has focus, cannot be taken back, and is the reason
/// the engine gates it in the decision rather than in the code that presses keys.
/// </remarks>
public enum RuleEffectKind
{
    /// <summary>Formatted text over the game.</summary>
    Text,

    /// <summary>A bar filled to a number the rule is watching.</summary>
    Bar,

    /// <summary>An audible cue.</summary>
    Sound,

    /// <summary>Press and release a key.</summary>
    KeyPress,

    /// <summary>Hold a key down. Pair with <see cref="KeyUp"/>.</summary>
    KeyDown,

    /// <summary>Let a held key up.</summary>
    KeyUp,

    /// <summary>Press several keys in order.</summary>
    KeySequence,

    MouseLeftClick,
    MouseRightClick,
    MouseLeftDown,
    MouseLeftUp,
    MouseRightDown,
    MouseRightUp,
    ScrollUp,
    ScrollDown,
}

/// <summary>Where the key an effect presses comes from.</summary>
/// <remarks>
/// Taken from the AHK tool's macro engine, which binds an output to a flask SLOT and resolves
/// the key live, rather than from the reference plugin, which stores the letter. The difference
/// only shows up when somebody rebinds their flasks: the stored letter goes on pressing what
/// used to be flask 2, and the only symptom is a tool that appears to do nothing. This project
/// already reads the game's own bindings for auto-flask - see <see cref="FlaskKeyBindings"/> -
/// so the slot is available and the letter need not be a second source of truth.
/// </remarks>
public enum KeySource
{
    /// <summary>The key named in the effect.</summary>
    Named,

    /// <summary>Whatever the game has bound to that belt slot, read from its own config.</summary>
    FlaskSlot,
}

/// <summary>One thing a rule does.</summary>
/// <param name="Kind">Which of the three sorts of effect this is.</param>
/// <param name="Text">
/// What a text effect says, with placeholders - see <see cref="RuleText"/>. Also the label on
/// a bar.
/// </param>
/// <param name="Watching">Which number a bar is filled by.</param>
/// <param name="CooldownMs">
/// Shortest gap between two firings of THIS effect. Ignored for drawing, which is not an
/// event: text shows for as long as its condition holds.
/// </param>
public sealed record RuleEffect(
    [property: JsonPropertyName("kind")] RuleEffectKind Kind = RuleEffectKind.Text,
    [property: JsonPropertyName("text")] string Text = "Triggered",
    [property: JsonPropertyName("watching")] RuleFact Watching = RuleFact.LifePercent,
    [property: JsonPropertyName("cooldownMs")] int CooldownMs = 2000)
{
    /// <summary>Where on screen, as a share of the viewport.</summary>
    [JsonPropertyName("x")]
    public float X { get; init; } = 0.5f;

    [JsonPropertyName("y")]
    public float Y { get; init; } = 0.35f;

    /// <summary>How big a bar is, as a share of the viewport.</summary>
    [JsonPropertyName("width")]
    public float Width { get; init; } = 0.22f;

    [JsonPropertyName("height")]
    public float Height { get; init; } = 0.025f;

    /// <summary>Text size, as a multiple of the overlay's own.</summary>
    [JsonPropertyName("scale")]
    public float Scale { get; init; } = 1.0f;

    /// <summary>The colour, as #rrggbb or #rrggbbaa.</summary>
    /// <remarks>
    /// A string rather than four floats, because this is what the config page's colour input
    /// hands back and what somebody editing the file by hand can read. The overlay parses it
    /// once per frame, which is a handful of hex digits.
    /// </remarks>
    [JsonPropertyName("colour")]
    public string Colour { get; init; } = "#33ff40";

    [JsonPropertyName("backgroundColour")]
    public string BackgroundColour { get; init; } = "#0f0f0fbf";

    /// <summary>
    /// How long a drawn effect stays up after its condition stops holding.
    /// </summary>
    /// <remarks>
    /// Without this a rule fired by an interval or by a single event is drawn for ONE FRAME and
    /// is, in practice, invisible. The reference plugin has no equivalent, which is why its own
    /// example rules all hang off conditions that stay true for a while.
    /// </remarks>
    [JsonPropertyName("lingerMs")]
    public int LingerMs { get; init; } = 400;

    /// <summary>Whether the key comes from the effect or from the game's own bindings.</summary>
    [JsonPropertyName("keySource")]
    public KeySource KeySource { get; init; } = KeySource.Named;

    /// <summary>The key to press, by name - "Q", "1", "F5", "Space".</summary>
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    /// <summary>The belt slot whose key to press, when the source is the game's bindings.</summary>
    [JsonPropertyName("slot")]
    public int Slot { get; init; }

    /// <summary>The keys a sequence presses, in order, comma-separated.</summary>
    [JsonPropertyName("keys")]
    public string Keys { get; init; } = string.Empty;

    /// <summary>Pitch of a sound cue, in hertz.</summary>
    [JsonPropertyName("pitch")]
    public int Pitch { get; init; } = 900;

    /// <summary>Length of a sound cue.</summary>
    [JsonPropertyName("soundMs")]
    public int SoundMs { get; init; } = 120;

    /// <summary>Whether this effect draws rather than acts.</summary>
    public bool Draws => Kind is RuleEffectKind.Text or RuleEffectKind.Bar;

    /// <summary>
    /// Whether this effect synthesises input.
    /// </summary>
    /// <remarks>
    /// The question the engine asks before it will let a rule fire at all outside the game, so
    /// it is one property rather than a condition repeated at each call site - a new effect
    /// kind added to the enum and forgotten here would otherwise be one that bypasses the
    /// focus gate.
    /// </remarks>
    public bool Sends => Kind is not (RuleEffectKind.Text or RuleEffectKind.Bar or RuleEffectKind.Sound);

    /// <summary>Brings every value into a range the overlay and the sender can use.</summary>
    /// <remarks>
    /// Applied on load and on every change from the page, on the same argument as
    /// <see cref="AutoFlaskSettings.Normalised"/>: the file is meant to be hand-editable, so it
    /// can arrive holding a scale of 400 or a slot of -3, and none of that should reach a
    /// renderer or a key sender.
    /// </remarks>
    public RuleEffect Normalised() => this with
    {
        Text = Text ?? string.Empty,
        Key = (Key ?? string.Empty).Trim(),
        Keys = (Keys ?? string.Empty).Trim(),
        Colour = Colours.Clean(Colour, "#33ff40"),
        BackgroundColour = Colours.Clean(BackgroundColour, "#0f0f0fbf"),

        // Positions may sit slightly outside the viewport - somebody parking a readout at the
        // very edge is legitimate - but not so far that it can never be found again.
        X = Math.Clamp(X, -0.5f, 1.5f),
        Y = Math.Clamp(Y, -0.5f, 1.5f),
        Width = Math.Clamp(Width, 0.01f, 1f),
        Height = Math.Clamp(Height, 0.002f, 1f),
        Scale = Math.Clamp(Scale, 0.25f, 8f),
        LingerMs = Math.Clamp(LingerMs, 0, 60_000),
        CooldownMs = Math.Clamp(CooldownMs, 0, 600_000),
        Slot = Math.Clamp(Slot, 0, AutoFlaskSettings.SlotCount),

        // Console.Beep's own range, clamped here so a bad value is a quiet cue rather than an
        // exception thrown out of the middle of a tick.
        Pitch = Math.Clamp(Pitch, 37, 32_767),
        SoundMs = Math.Clamp(SoundMs, 1, 5_000),
    };
}

/// <summary>Turning the placeholders in an effect's text into what they stand for.</summary>
/// <remarks>
/// Every placeholder is a fact from the catalogue, spelled the way the editor spells it, so
/// there is no second vocabulary to learn or to keep in step: anything a condition can ask
/// about, a caption can show. The reference plugin has ten hard-coded replacements.
///
/// A fact that cannot be answered shows as "-" rather than as 0, for the reason the whole
/// state carries nulls: a caption reading "Life 0%" on a loading screen is a bug report.
/// </remarks>
public static class RuleText
{
    /// <summary>Longest caption rendered, so a placeholder loop cannot grow one without end.</summary>
    public const int MaxLength = 512;

    /// <summary>Fills the placeholders in a caption.</summary>
    public static string Fill(string text, RuleState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (string.IsNullOrEmpty(text) || !text.Contains('{', StringComparison.Ordinal))
        {
            return text ?? string.Empty;
        }

        var built = new System.Text.StringBuilder(text.Length + 16);
        int at = 0;
        while (at < text.Length && built.Length < MaxLength)
        {
            int open = text.IndexOf('{', at);
            if (open < 0)
            {
                built.Append(text, at, text.Length - at);
                break;
            }

            int close = text.IndexOf('}', open + 1);
            if (close < 0)
            {
                built.Append(text, at, text.Length - at);
                break;
            }

            built.Append(text, at, open - at);
            built.Append(Value(text[(open + 1)..close], state));
            at = close + 1;
        }

        return built.Length > MaxLength ? built.ToString(0, MaxLength) : built.ToString();
    }

    /// <summary>What one placeholder stands for.</summary>
    private static string Value(string name, RuleState state)
    {
        switch (name)
        {
            case "AreaName":
                return state.AreaName;
            case "AreaId":
                return state.AreaId;
        }

        if (RuleFacts.Find(name) is not FactInfo info)
        {
            // Left as written, brackets and all. A caption showing "{Helth}" says plainly that
            // the name is wrong, where an empty string looks like a fact that answered nothing.
            return "{" + name + "}";
        }

        if (info.Shape == FactShape.Flag)
        {
            return RuleFacts.Holds(
                new RuleCondition { Fact = info.Fact }, state, EmptyTimers, "caption")
                ? "yes"
                : "no";
        }

        double? answer = RuleFacts.Answer(new RuleCondition { Fact = info.Fact }, state);
        return answer is double number
            ? number.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
            : "-";
    }

    /// <summary>
    /// Captions never consume an interval.
    /// </summary>
    /// <remarks>
    /// An EverySeconds placeholder would otherwise tick a timer every frame it is drawn, and
    /// the rule sharing that interval would then never come round. Its own instance, discarded,
    /// is what stops a caption from having a side effect.
    /// </remarks>
    private static RuleTimers EmptyTimers { get; } = new();
}

/// <summary>Reading the colours the config page and the settings file use.</summary>
public static class Colours
{
    /// <summary>Reads #rrggbb or #rrggbbaa into packed RGBA.</summary>
    public static bool TryParse(string? text, out uint rgba)
    {
        rgba = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        ReadOnlySpan<char> digits = text.AsSpan().Trim();
        if (digits.Length > 0 && digits[0] == '#')
        {
            digits = digits[1..];
        }

        if (digits.Length is not (6 or 8))
        {
            return false;
        }

        uint value = 0;
        foreach (char digit in digits)
        {
            int nibble = digit switch
            {
                >= '0' and <= '9' => digit - '0',
                >= 'a' and <= 'f' => digit - 'a' + 10,
                >= 'A' and <= 'F' => digit - 'A' + 10,
                _ => -1,
            };

            if (nibble < 0)
            {
                return false;
            }

            value = (value << 4) | (uint)nibble;
        }

        // Six digits means fully opaque. Defaulting the other way would draw every colour
        // written the short way as invisible, which reads exactly like the feature not working.
        rgba = digits.Length == 6 ? (value << 8) | 0xFF : value;
        return true;
    }

    /// <summary>Reads a colour, or hands back the fallback when it is not one.</summary>
    public static uint ToRgba(string? text, uint fallback)
        => TryParse(text, out uint rgba) ? rgba : fallback;

    /// <summary>Normalises a colour string, or hands back the fallback when it is not one.</summary>
    public static string Clean(string? text, string fallback)
        => TryParse(text, out uint rgba)
            ? "#" + rgba.ToString("x8", System.Globalization.CultureInfo.InvariantCulture)
            : fallback;
}
