using System.Text.RegularExpressions;

namespace PoEformance.Features;

/// <summary>One line in the game's config that MIGHT be the dodge-roll binding.</summary>
/// <param name="Setting">The config key as written, e.g. <c>Input_dodge_roll</c>.</param>
/// <param name="Value">The value as written, so a mouse button is visible as one.</param>
/// <param name="Key">The virtual-key code it maps to, or 0 when it is not one we can send.</param>
public sealed record DodgeKeyHint(string Section, string Setting, string Value, ushort Key)
{
    /// <summary>A readable one-liner for the settings page.</summary>
    public string Describe()
        => $"{Setting} = {Value}"
           + (Key == 0 ? " (not a key this can send)" : $" -> {FlaskKeyBindings.Describe(Key)}");
}

/// <summary>
/// Looks through the game's config for lines that could be the bindings this tool presses - the
/// dodge roll, and the movement keys the steering holds - and SUGGESTS them. It picks none.
/// </summary>
/// <remarks>
/// WHY THIS IS A SUGGESTION AND NOT A READING, which is the whole point of the class. The flask
/// keys are read from the game outright, and that is safe because their config spelling was
/// established against a real file - four different spellings of it, in fact. NOBODY HAS
/// ESTABLISHED WHAT THIS GAME CALLS THE DODGE ROLL. Picking a plausible-looking line and
/// pressing whatever it names would be a guess wearing the clothes of a measurement, and the
/// failure mode is the ugly one: a key the player never bound, sent during a fight, with no
/// symptom beyond the character doing something unexpected.
///
/// The AHK tool settles this the same way and is worth following here: it never reads a dodge
/// binding either - every one of its action hotkeys is a key the person chose. So the setting is
/// the source of truth, and this exists only to save somebody opening the ini themselves.
///
/// If a future session establishes the real spelling against a live config, this becomes a
/// reader and <c>EvasionSettings.DodgeKey</c> becomes the override it is currently not.
///
/// THE MOVEMENT KEYS ARE HERE FOR THE SAME REASON ONE STEP ON. Steering a roll holds W, A, S or
/// D, so those became the second set of keys the tool can press - and a rebound one fails
/// silently rather than loudly: the tool holds a key the game ignores, the roll follows the
/// cursor instead, and the status line still reports the direction it meant to take.
/// </remarks>
public static partial class DodgeKeyHints
{
    /// <summary>Words that make a config line worth showing. Deliberately broad.</summary>
    /// <remarks>
    /// Broad because this only ever SHOWS things: a false positive costs a line on a settings
    /// page, while a miss costs somebody the one line they were looking for. "dash" and "evade"
    /// are in because other games call it that and nobody here knows which word this one uses.
    /// </remarks>
    private static readonly string[] Words = ["dodge", "roll", "dash", "evade"];

    /// <summary>Words that make a config line look like a MOVEMENT binding.</summary>
    /// <remarks>
    /// Steering a roll holds movement keys, so those are the second set of keys this tool can
    /// press and they get the same treatment for the same reason: W A S D is the shipped layout
    /// and a rebound one fails SILENTLY, holding a key the game ignores while the roll follows
    /// the cursor - which looks exactly like steering that chose a different direction.
    ///
    /// Narrower than the dodge list on purpose. "up" and "down" alone would match the mouse
    /// wheel, every panel scroll and half the interface bindings in the file, and a hint list
    /// that long is one nobody reads.
    /// </remarks>
    private static readonly string[] MovementWords =
        ["move_up", "move_down", "move_left", "move_right", "moveup", "movedown", "moveleft",
         "moveright", "forward", "backward", "strafe", "walk"];

    /// <summary>Matches <c>anything=value</c>, which is all a config line is.</summary>
    [GeneratedRegex(@"^([^=\[\]]+?)\s*=\s*(.*)$")]
    private static partial Regex Binding();

    /// <summary>Reads the game's config and returns every line that might be it.</summary>
    /// <remarks>
    /// A missing or unreadable config yields an empty list rather than throwing: the setting
    /// works without this, and a hint that cannot be produced is not an error.
    /// </remarks>
    public static IReadOnlyList<DodgeKeyHint> Find(string? configPath = null)
        => Read(configPath, Words);

    /// <summary>The same, for the movement keys the steering holds.</summary>
    public static IReadOnlyList<DodgeKeyHint> FindMovement(string? configPath = null)
        => Read(configPath, MovementWords);

    private static IReadOnlyList<DodgeKeyHint> Read(string? configPath, string[] words)
    {
        string path = configPath ?? FlaskKeyBindings.FindConfigPath();
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllLines(path), words) : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Scans config lines. Split out so it is testable without a file.</summary>
    public static IReadOnlyList<DodgeKeyHint> Parse(IEnumerable<string> lines, string[]? words = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        words ??= Words;

        var hints = new List<DodgeKeyHint>();
        string section = string.Empty;

        foreach (string line in lines)
        {
            string trimmed = line.Trim().TrimStart('﻿').Trim();
            if (trimmed.Length == 0 || trimmed[0] == ';')
            {
                continue;
            }

            if (trimmed[0] == '[' && trimmed[^1] == ']')
            {
                section = trimmed[1..^1];
                continue;
            }

            Match match = Binding().Match(trimmed);
            if (!match.Success)
            {
                continue;
            }

            string setting = match.Groups[1].Value.Trim();
            if (!Mentions(setting, words))
            {
                continue;
            }

            string value = match.Groups[2].Value.Trim();
            hints.Add(new DodgeKeyHint(section, setting, value, FlaskKeyBindings.ToVirtualKey(value)));
        }

        return hints;
    }

    /// <summary>
    /// Whether a setting name contains one of the words AS A WORD.
    /// </summary>
    /// <remarks>
    /// THE BOUNDARY IS NOT PEDANTRY: "roll" is a substring of "SCROLL", so a plain
    /// <c>Contains</c> lists every wheel and panel binding in the game's config alongside the
    /// dodge. This list exists to save somebody reading the ini, and a list that reproduces half
    /// of it does not. Found by a test rather than by reading the file, which is the honest way
    /// round to admit it.
    ///
    /// A word ends where a letter stops, so the underscores the config separates its names with
    /// count as boundaries and "Input_dodge_roll" still matches. Digits do too - a "roll2" is the
    /// same binding with a number on it.
    /// </remarks>
    private static bool Mentions(string setting, string[] words)
    {
        foreach (string word in words)
        {
            for (int at = 0; (at = setting.IndexOf(word, at, StringComparison.OrdinalIgnoreCase)) >= 0; at++)
            {
                int end = at + word.Length;
                if ((at == 0 || !char.IsLetter(setting[at - 1]))
                    && (end == setting.Length || !char.IsLetter(setting[end])))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
