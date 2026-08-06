using System.Text.RegularExpressions;

namespace PoEformance.Features;

/// <summary>Where the flask keys came from, so a wrong key is diagnosable.</summary>
/// <remarks>
/// The two fallbacks are separated because they mean opposite things. An EMPTY or missing
/// config means the game has no saved bindings either, so it is running its own 1-5 defaults
/// and matching them is correct. Content that could not be interpreted means something is
/// in there and this failed to read it - the only case worth a warning, because it is the
/// one where the tool might be pressing the wrong key.
/// </remarks>
public enum KeyBindingSource
{
    /// <summary>Read from the game's own config file.</summary>
    GameConfig,

    /// <summary>The config is missing or empty, so the game's own defaults apply.</summary>
    GameDefaults,

    /// <summary>The config has content, but no flask binding could be read from it.</summary>
    Unmatched,
}

/// <summary>The key each flask slot is bound to, and where that came from.</summary>
public sealed record FlaskKeys(
    IReadOnlyDictionary<int, ushort> BySlot,
    KeyBindingSource Source,
    string Detail);

/// <summary>
/// Finds out which key actually uses each flask, by reading the game's own config.
/// </summary>
/// <remarks>
/// The alternative is assuming slot N sits on number key N. That happens to be the default
/// binding, which makes the assumption look correct right up until someone rebinds - and
/// then the only symptom is that nothing happens, with no indication that the tool is
/// pressing a key the game does not associate with a flask. Reading the binding removes a
/// silent failure mode rather than adding a convenience.
///
/// PORTED FROM THE AHK TOOL (LoadFlaskHotkeysFromConfig / TryParseFlaskBindingLine /
/// NormalizeConfigKeyToSend), which learned this file's shape against the real game. Two
/// things there are not guessable and were both got wrong before checking it:
///
///   - The key spelling varies (flask1, flask_2, UseFlask3, Input_flask_4_primary), and the
///     underscore forms need a second, looser pattern because \b does not fire between a
///     word character and an underscore.
///   - A NUMERIC value is a decimal VIRTUAL-KEY CODE, not a digit. "use_bound_skill4=81" is
///     Q, not the 8 and 1 keys.
///
/// This port once diverged from the reference on ONE point, reading a single digit as the
/// digit key rather than as a code - on the argument that a code there would be a mouse
/// button, which cannot use a flask anyway. A real config settled it against that reading:
/// the same file holds <c>use_flask_in_slot1=49</c> and <c>use_bound_skill1=1</c>, and the
/// second is the primary skill on LEFT MOUSE. The game writes decimal codes throughout,
/// single digits included, and 0 is how it writes "not bound at all".
///
/// The divergence was not merely wrong, it was wrong in the direction this class exists to
/// prevent. An unbound flask slot reads 0, which it turned into the ZERO KEY - so the tool
/// would press a key the player never bound, silently. Reading numbers as codes gives the
/// safe answer instead: a mouse button and an unbound slot both come back unusable, which is
/// visible and does nothing.
/// </remarks>
public static partial class FlaskKeyBindings
{
    /// <summary>Slot N on number key N - the game's default, and the fallback.</summary>
    public static IReadOnlyDictionary<int, ushort> Defaults { get; } = new Dictionary<int, ushort>
    {
        [1] = 0x31, [2] = 0x32, [3] = 0x33, [4] = 0x34, [5] = 0x35,
    };

    /// <summary>The common spellings: flask1, flask_2, UseFlask3.</summary>
    [GeneratedRegex(@"\b(?:use)?flask[_\s-]*([1-5])\b[^=]*=\s*(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex FlaskBinding();

    /// <summary>
    /// The fallback for underscore-wrapped keys such as Input_flask_4_primary, where the
    /// pattern above cannot match: regex word boundaries do not fire around underscores.
    /// </summary>
    [GeneratedRegex(@"^([^=]*flask[^=]*?([1-5])[^=]*)=\s*(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex FlaskBindingLoose();

    /// <summary>The game's config file, under a given Documents folder.</summary>
    public static string ConfigUnder(string documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        return Path.Combine(documents, "My Games", "Path of Exile 2", "poe2_production_Config.ini");
    }

    /// <summary>Where the config would be if Documents is where Windows reports it.</summary>
    public static string DefaultConfigPath
        => ConfigUnder(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));

    /// <summary>
    /// Everywhere the config is worth looking, best guess first.
    /// </summary>
    /// <remarks>
    /// One path was not enough, and the case that proved it is ordinary rather than exotic:
    /// with OneDrive backing up Documents the game writes to
    /// <c>OneDrive\Dokumente\My Games\...</c> - redirected, AND named in the user's own
    /// language - while the folder Windows reports can still be the plain English one. The
    /// tool then quietly used the game's default flask keys with nothing on screen to say why.
    ///
    /// So the Documents folder is not guessed at by name. Every immediate child of each
    /// likely root is tried, which finds Dokumente, Documenti and Documentos without this
    /// having to know any of them. Enumerating can fail on a folder we may not read, and that
    /// is no reason to stop looking elsewhere.
    /// </remarks>
    public static IEnumerable<string> CandidateConfigPaths()
    {
        yield return DefaultConfigPath;

        foreach (string root in Roots())
        {
            yield return ConfigUnder(root);

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(root);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string child in children)
            {
                yield return ConfigUnder(child);
            }
        }

        static IEnumerable<string> Roots()
        {
            foreach (string variable in (string[])["OneDrive", "OneDriveConsumer", "OneDriveCommercial"])
            {
                string value = Environment.GetEnvironmentVariable(variable) ?? string.Empty;
                if (value.Length > 0 && Directory.Exists(value))
                {
                    yield return value;
                }
            }

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (home.Length > 0 && Directory.Exists(home))
            {
                yield return home;
            }
        }
    }

    /// <summary>The first candidate that exists, or the default path when none does.</summary>
    public static string FindConfigPath()
    {
        foreach (string candidate in CandidateConfigPaths())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return DefaultConfigPath;
    }

    /// <summary>Loads the bindings, falling back to the defaults with a reason.</summary>
    public static FlaskKeys Load(string? configPath = null)
    {
        string path = configPath ?? FindConfigPath();

        string[] lines;
        try
        {
            if (!File.Exists(path))
            {
                return new FlaskKeys(Defaults, KeyBindingSource.GameDefaults, $"no config at {path}");
            }

            lines = File.ReadAllLines(path);
        }
        catch (IOException error)
        {
            return new FlaskKeys(Defaults, KeyBindingSource.Unmatched, error.Message);
        }
        catch (UnauthorizedAccessException error)
        {
            return new FlaskKeys(Defaults, KeyBindingSource.Unmatched, error.Message);
        }

        return Parse(lines, path);
    }

    /// <summary>
    /// Parses config lines into slot-to-key bindings.
    /// </summary>
    /// <remarks>
    /// The file holds MORE THAN ONE set of key bindings and only one of them is live. A
    /// player using WASD movement is bound by <c>[WASD_ACTION_KEYS]</c>; everyone else by
    /// <c>[ACTION_KEYS]</c>, and the game says which in <c>user_input_mode</c>. The two
    /// genuinely differ - in the config that turned this up they disagree about the character
    /// panel and half the skill keys - so reading whichever comes last is a coin toss, and
    /// exactly the silent wrong-key failure this class exists to remove.
    /// </remarks>
    public static FlaskKeys Parse(IEnumerable<string> lines, string source = "config")
    {
        ArgumentNullException.ThrowIfNull(lines);

        var bySection = new Dictionary<string, Dictionary<int, ushort>>(StringComparer.OrdinalIgnoreCase);
        string section = string.Empty;
        string inputMode = string.Empty;
        int found = 0;
        int content = 0;

        foreach (string line in lines)
        {
            // The byte-order mark counts as a character but not as content, so a file
            // holding nothing but a BOM must still read as empty.
            string trimmed = line.Trim().TrimStart('﻿').Trim();
            if (trimmed.Length == 0 || trimmed[0] == ';')
            {
                continue;
            }

            content++;

            if (trimmed[0] == '[' && trimmed[^1] == ']')
            {
                section = trimmed[1..^1];
                continue;
            }

            if (trimmed.StartsWith("user_input_mode=", StringComparison.OrdinalIgnoreCase))
            {
                inputMode = trimmed["user_input_mode=".Length..].Trim();
                continue;
            }

            if (!TryParseLine(trimmed, out int slot, out ushort key))
            {
                continue;
            }

            // Recorded even when the key is 0 - a flask bound to a mouse button is a slot
            // this CANNOT use, and saying so beats leaving the default number key in place
            // and pressing something the player never bound. A slot with no line at all
            // keeps its default, which is correct: that is what the game is using too.
            if (!bySection.TryGetValue(section, out Dictionary<int, ushort>? keys))
            {
                keys = [];
                bySection[section] = keys;
            }

            keys[slot] = key;
            found++;
        }

        if (found > 0)
        {
            (string chosen, Dictionary<int, ushort> live) = Choose(bySection, inputMode);
            var bound = new Dictionary<int, ushort>(Defaults);
            foreach ((int slot, ushort key) in live)
            {
                bound[slot] = key;
            }

            string where = chosen.Length > 0 ? $"{source} [{chosen}]" : source;
            return new FlaskKeys(bound, KeyBindingSource.GameConfig, $"{live.Count} bindings from {where}");
        }

        // An empty config is not a failure. The game saves this file when a setting is
        // CHANGED, so a player who never rebound anything has nothing in it - and the game
        // is then running the same 1-5 defaults this falls back to. Reporting that as a
        // parse failure sends the reader looking for a bug that is not there.
        return content == 0
            ? new FlaskKeys(Defaults, KeyBindingSource.GameDefaults, $"{source} is empty")
            : new FlaskKeys(Defaults, KeyBindingSource.Unmatched, $"{content} lines in {source}, no flask binding among them");
    }

    /// <summary>
    /// Picks the binding set the game is actually using.
    /// </summary>
    /// <remarks>
    /// The mode decides, and the fallbacks matter as much as the rule: an unfamiliar mode, a
    /// file with no sections at all, or a section that names no flask must still yield the
    /// bindings that ARE there rather than nothing.
    /// </remarks>
    private static (string Section, Dictionary<int, ushort> Keys) Choose(
        Dictionary<string, Dictionary<int, ushort>> bySection, string inputMode)
    {
        string wanted = inputMode.Equals("wasd", StringComparison.OrdinalIgnoreCase)
            ? "WASD_ACTION_KEYS"
            : "ACTION_KEYS";

        if (bySection.TryGetValue(wanted, out Dictionary<int, ushort>? keys) && keys.Count > 0)
        {
            return (wanted, keys);
        }

        // Whatever is there. A config written by an older game version, or lines outside any
        // section, still say more than the defaults do.
        foreach ((string section, Dictionary<int, ushort> other) in bySection)
        {
            if (other.Count > 0)
            {
                return (section, other);
            }
        }

        return (string.Empty, []);
    }

    /// <summary>
    /// Reads one config line. True when it binds a flask slot at all - the key may still be
    /// 0, meaning the line was understood but names something this cannot send.
    /// </summary>
    private static bool TryParseLine(string line, out int slot, out ushort key)
    {
        slot = 0;
        key = 0;

        Match match = FlaskBinding().Match(line);
        string? value = match.Success ? match.Groups[2].Value : null;
        string? slotText = match.Success ? match.Groups[1].Value : null;

        if (value is null)
        {
            Match loose = FlaskBindingLoose().Match(line);
            if (!loose.Success)
            {
                return false;
            }

            slotText = loose.Groups[2].Value;
            value = loose.Groups[3].Value;
        }

        slot = int.Parse(slotText!);
        key = ToVirtualKey(value);
        return slot is >= 1 and <= 5;
    }

    /// <summary>
    /// Turns a config key name into a virtual-key code, or 0 when it is not one we can send.
    /// </summary>
    /// <remarks>
    /// Returning 0 for anything unrecognised is deliberate. A flask bound to a mouse button
    /// or a key this cannot map must NOT silently fall back to the number key - that would
    /// press something the player never bound, which is worse than not acting. The caller
    /// reports the slot as unusable, which is at least visible.
    /// </remarks>
    public static ushort ToVirtualKey(string rawValue)
    {
        ArgumentNullException.ThrowIfNull(rawValue);

        // The value can carry an alternate binding or a comment: "DIK_1, DIK_NUMPAD1".
        // Take the first token, THEN strip quotes - the other order leaves a stray quote
        // glued to the key name.
        string token = FirstToken(rawValue).Trim('"').Trim();
        string value = StripInputPrefix(token).ToUpperInvariant();
        if (value.Length == 0)
        {
            return 0;
        }

        // A prefix makes it a NAME, and DIK_1 names the 1 key. Without one it is a number,
        // and a number is a virtual-key code even when it is a single digit - see the class
        // remarks for the real config that settled that, and for what the old reading cost.
        bool named = value.Length != token.Length;

        if (IsAllDigits(value) && int.TryParse(value, out int code))
        {
            return named && value.Length == 1 ? value[0] : FromVirtualKeyCode(code);
        }

        // Single digits and letters are their own virtual-key codes.
        if (value.Length == 1 && value[0] is >= '0' and <= '9' or >= 'A' and <= 'Z')
        {
            return value[0];
        }

        if (value.StartsWith("NUMPAD", StringComparison.Ordinal)
            && value.Length == 7
            && value[6] is >= '0' and <= '9')
        {
            return (ushort)(0x60 + (value[6] - '0'));
        }

        if (value.Length is 2 or 3 && value[0] == 'F' && int.TryParse(value[1..], out int f) && f is >= 1 and <= 24)
        {
            return (ushort)(0x6F + f);
        }

        return value switch
        {
            "SPACE" => 0x20,
            "TAB" => 0x09,
            "RETURN" or "ENTER" => 0x0D,
            "BACK" or "BACKSPACE" => 0x08,
            "ESCAPE" or "ESC" => 0x1B,
            "INSERT" => 0x2D,
            "DELETE" => 0x2E,
            "HOME" => 0x24,
            "END" => 0x23,
            "PRIOR" or "PAGEUP" => 0x21,
            "NEXT" or "PAGEDOWN" => 0x22,

            // Mouse buttons and bare modifiers land here on purpose: this sends keyboard
            // input, and a lone Shift could not use a flask anyway.
            _ => 0,
        };
    }

    /// <summary>A readable name for a virtual-key code, for the UI.</summary>
    public static string Describe(ushort key) => key switch
    {
        0 => "unbound",
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)key).ToString(),
        >= 0x60 and <= 0x69 => $"Numpad {key - 0x60}",
        >= 0x70 and <= 0x87 => $"F{key - 0x6F}",
        0x20 => "Space",
        0x09 => "Tab",
        0x0D => "Enter",
        0x08 => "Backspace",
        0x1B => "Esc",
        0x2D => "Insert",
        0x2E => "Delete",
        0x24 => "Home",
        0x23 => "End",
        0x21 => "Page Up",
        0x22 => "Page Down",
        _ => $"0x{key:X2}",
    };

    /// <summary>
    /// Which decimal virtual-key codes this is willing to send.
    /// </summary>
    /// <remarks>
    /// Everything outside these ranges - mouse buttons above all - returns 0 rather than a
    /// near-miss. Mouse buttons genuinely appear in this file, and there is no keystroke
    /// that stands in for one.
    /// </remarks>
    private static ushort FromVirtualKeyCode(int code) => code switch
    {
        >= 0x30 and <= 0x39 => (ushort)code,   // 0-9
        >= 0x41 and <= 0x5A => (ushort)code,   // A-Z
        >= 0x60 and <= 0x6F => (ushort)code,   // numpad digits and operators
        >= 0x70 and <= 0x87 => (ushort)code,   // F1-F24
        >= 0x21 and <= 0x28 => (ushort)code,   // page up/down, end, home, arrows
        0x08 or 0x09 or 0x0D or 0x1B or 0x20 => (ushort)code,
        0x2D or 0x2E => (ushort)code,          // insert, delete
        0x90 or 0x91 => (ushort)code,          // num lock, scroll lock
        >= 0xBA and <= 0xC0 or >= 0xDB and <= 0xDE => (ushort)code, // OEM punctuation
        _ => 0,
    };

    /// <summary>The value up to the first separator, so an alternate or comment drops off.</summary>
    private static string FirstToken(string raw)
    {
        string value = raw.Trim();
        int cut = value.IndexOfAny([',', ';', ' ', '\t']);
        return cut >= 0 ? value[..cut] : value;
    }

    /// <summary>Strips the input-layer prefixes the game writes.</summary>
    private static string StripInputPrefix(string value)
    {
        foreach (string prefix in (string[])["DIK_", "VK_", "KEY_"])
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return value[prefix.Length..];
            }
        }

        return value;
    }

    private static bool IsAllDigits(string value)
    {
        foreach (char c in value)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return value.Length > 0;
    }
}
