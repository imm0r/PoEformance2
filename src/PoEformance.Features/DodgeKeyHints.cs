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
/// Looks through the game's config for lines that could be the dodge-roll binding, and
/// SUGGESTS them. It does not pick one.
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

    /// <summary>Matches <c>anything=value</c>, which is all a config line is.</summary>
    [GeneratedRegex(@"^([^=\[\]]+?)\s*=\s*(.*)$")]
    private static partial Regex Binding();

    /// <summary>Reads the game's config and returns every line that might be it.</summary>
    /// <remarks>
    /// A missing or unreadable config yields an empty list rather than throwing: the setting
    /// works without this, and a hint that cannot be produced is not an error.
    /// </remarks>
    public static IReadOnlyList<DodgeKeyHint> Find(string? configPath = null)
    {
        string path = configPath ?? FlaskKeyBindings.FindConfigPath();
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllLines(path)) : [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Scans config lines. Split out so it is testable without a file.</summary>
    public static IReadOnlyList<DodgeKeyHint> Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

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
            if (!Mentions(setting))
            {
                continue;
            }

            string value = match.Groups[2].Value.Trim();
            hints.Add(new DodgeKeyHint(section, setting, value, FlaskKeyBindings.ToVirtualKey(value)));
        }

        return hints;
    }

    private static bool Mentions(string setting)
    {
        foreach (string word in Words)
        {
            if (setting.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
