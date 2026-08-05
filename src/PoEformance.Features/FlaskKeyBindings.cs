using System.Text.RegularExpressions;

namespace PoEformance.Features;

/// <summary>Where the flask keys came from, so a wrong key is diagnosable.</summary>
public enum KeyBindingSource
{
    /// <summary>Read from the game's own config file.</summary>
    GameConfig,

    /// <summary>The game's config was not found or held no flask bindings.</summary>
    Defaults,
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
/// One deliberate divergence from the reference: a SINGLE digit is read as the digit key,
/// never as a code. The reference resolves "1" to VK 1, the left mouse button. Both
/// readings are defensible for an ambiguous file, but only one of them can be acted on -
/// there is no keystroke that stands in for a mouse button - and the game writes 49 when it
/// means a code for the 1 key. So the reading that can actually be pressed wins, and
/// "UseFlask3=3" keeps meaning the 3 key.
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

    /// <summary>The game's config file, in the user's Documents folder.</summary>
    public static string DefaultConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "My Games", "Path of Exile 2", "poe2_production_Config.ini");

    /// <summary>Loads the bindings, falling back to the defaults with a reason.</summary>
    public static FlaskKeys Load(string? configPath = null)
    {
        string path = configPath ?? DefaultConfigPath;

        string[] lines;
        try
        {
            if (!File.Exists(path))
            {
                return new FlaskKeys(Defaults, KeyBindingSource.Defaults, $"not found: {path}");
            }

            lines = File.ReadAllLines(path);
        }
        catch (IOException error)
        {
            return new FlaskKeys(Defaults, KeyBindingSource.Defaults, error.Message);
        }
        catch (UnauthorizedAccessException error)
        {
            return new FlaskKeys(Defaults, KeyBindingSource.Defaults, error.Message);
        }

        return Parse(lines, path);
    }

    /// <summary>Parses config lines into slot-to-key bindings.</summary>
    public static FlaskKeys Parse(IEnumerable<string> lines, string source = "config")
    {
        ArgumentNullException.ThrowIfNull(lines);

        var bound = new Dictionary<int, ushort>(Defaults);
        int found = 0;

        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == ';')
            {
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
            bound[slot] = key;
            found++;
        }

        return found > 0
            ? new FlaskKeys(bound, KeyBindingSource.GameConfig, $"{found} bindings from {source}")
            : new FlaskKeys(Defaults, KeyBindingSource.Defaults, $"no flask bindings in {source}");
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
        string value = FirstToken(rawValue).Trim('"').Trim();
        value = StripInputPrefix(value).ToUpperInvariant();
        if (value.Length == 0)
        {
            return 0;
        }

        if (IsAllDigits(value) && int.TryParse(value, out int code))
        {
            // A lone digit is the digit KEY ("UseFlask3=3"). Read as a code it would be a
            // mouse button or a control key, neither of which can use a flask - see the
            // class remarks for why this one case diverges from the reference.
            return value.Length == 1 ? (ushort)value[0] : FromVirtualKeyCode(code);
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
