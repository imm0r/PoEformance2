using System.Globalization;

namespace PoEformance.Features;

/// <summary>
/// Turning what somebody types in the editor into a virtual-key code, and back.
/// </summary>
/// <remarks>
/// Both directions, from one table. The editor has to show a saved binding as a name, and a
/// one-way lookup is how a tool ends up displaying "81" where the user typed "Q".
///
/// A BARE NUMBER IS A VIRTUAL-KEY CODE, not the digits it looks like. That is not an invention
/// here: the game's own key bindings store them that way, so 81 in poe2_production_Config.ini
/// is Q. The AHK tool's history records reading such a value as the two keys 8 and 1, which
/// looks correct - a rule with a key in it, firing - right up until nothing happens in game.
/// Digits therefore have to be written as digits ("1") and codes are only reachable through the
/// explicit "vk81" spelling, so nothing is ambiguous in either direction.
/// </remarks>
public static class RuleKeys
{
    /// <summary>Most keys one sequence may press.</summary>
    /// <remarks>
    /// A sequence is sent inside one tick, so this is a bound on how long a tick can be made
    /// to take by editing a text field.
    /// </remarks>
    public const int MaxSequence = 16;

    private static readonly (string Name, ushort Code)[] Named =
    [
        ("Space", 0x20),
        ("Tab", 0x09),
        ("Enter", 0x0D),
        ("Escape", 0x1B),
        ("Backspace", 0x08),
        ("Delete", 0x2E),
        ("Insert", 0x2D),
        ("Home", 0x24),
        ("End", 0x23),
        ("PageUp", 0x21),
        ("PageDown", 0x22),
        ("Up", 0x26),
        ("Down", 0x28),
        ("Left", 0x25),
        ("Right", 0x27),
        ("Shift", 0xA0),
        ("RightShift", 0xA1),
        ("Ctrl", 0xA2),
        ("RightCtrl", 0xA3),
        ("Alt", 0xA4),
        ("RightAlt", 0xA5),
        ("CapsLock", 0x14),
        ("Numpad0", 0x60),
        ("Numpad1", 0x61),
        ("Numpad2", 0x62),
        ("Numpad3", 0x63),
        ("Numpad4", 0x64),
        ("Numpad5", 0x65),
        ("Numpad6", 0x66),
        ("Numpad7", 0x67),
        ("Numpad8", 0x68),
        ("Numpad9", 0x69),
    ];

    private static readonly Dictionary<string, ushort> ByName = Build();

    /// <summary>Every name the editor offers, so its dropdown and this table cannot drift.</summary>
    public static IReadOnlyList<string> Names { get; } = BuildNames();

    /// <summary>
    /// The virtual-key code for a name, or 0 when there is none.
    /// </summary>
    /// <remarks>
    /// Zero rather than an exception, and every caller treats zero as "this effect can do
    /// nothing" and stays quiet - the same rule auto-flask applies to an unmapped slot. A
    /// mistyped key must not be able to press something else.
    /// </remarks>
    public static ushort Code(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return 0;
        }

        string key = name.Trim();

        // A single letter or digit IS that key: 'Q' is 0x51 and '1' is 0x31, which is what the
        // virtual-key codes for those already are.
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]))
        {
            return char.ToUpperInvariant(key[0]);
        }

        if (key.Length > 2
            && key.StartsWith("vk", StringComparison.OrdinalIgnoreCase)
            && ushort.TryParse(key.AsSpan(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort raw))
        {
            return raw <= 0xFF ? raw : (ushort)0;
        }

        return ByName.TryGetValue(key, out ushort found) ? found : (ushort)0;
    }

    /// <summary>What to call a virtual-key code, or empty when nothing is bound.</summary>
    public static string Name(ushort code)
    {
        if (code == 0)
        {
            return string.Empty;
        }

        if ((code >= 'A' && code <= 'Z') || (code >= '0' && code <= '9'))
        {
            return ((char)code).ToString();
        }

        foreach ((string name, ushort known) in Named)
        {
            if (known == code)
            {
                return name;
            }
        }

        if (code is >= 0x70 and <= 0x87)
        {
            return "F" + (code - 0x70 + 1).ToString(CultureInfo.InvariantCulture);
        }

        // Something the game bound that this table has no name for. Round-trips through
        // Code() rather than being lost, which is what keeps a rebound key editable.
        return "vk" + code.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Reads the comma-separated keys of a sequence.</summary>
    /// <remarks>
    /// Anything unrecognised is DROPPED rather than failing the whole sequence: "Q, W, Wat, E"
    /// presses the three keys that exist. The alternative - refusing the lot - turns one typo
    /// into a macro that silently does nothing at all, which is harder to notice and harder to
    /// find.
    /// </remarks>
    public static IReadOnlyList<ushort> Sequence(string? keys)
    {
        if (string.IsNullOrWhiteSpace(keys))
        {
            return [];
        }

        var codes = new List<ushort>();
        foreach (string part in keys.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            ushort code = Code(part);
            if (code != 0)
            {
                codes.Add(code);
            }

            if (codes.Count == MaxSequence)
            {
                break;
            }
        }

        return codes;
    }

    private static Dictionary<string, ushort> Build()
    {
        var map = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, ushort code) in Named)
        {
            map[name] = code;
        }

        // The spellings people reach for, kept as aliases rather than as rows: Name() must
        // hand back one name per key, and a second row would make which one arbitrary.
        map["Return"] = 0x0D;
        map["Esc"] = 0x1B;
        map["Control"] = 0xA2;
        map["LeftShift"] = 0xA0;
        map["LeftCtrl"] = 0xA2;
        map["LeftAlt"] = 0xA4;

        // F1 through F24 - the run is contiguous, so the table would only be a longer way of
        // writing this.
        for (int number = 1; number <= 24; number++)
        {
            map["F" + number.ToString(CultureInfo.InvariantCulture)] = (ushort)(0x70 + number - 1);
        }

        return map;
    }

    private static List<string> BuildNames()
    {
        var names = new List<string>();
        for (char letter = 'A'; letter <= 'Z'; letter++)
        {
            names.Add(letter.ToString());
        }

        for (char digit = '0'; digit <= '9'; digit++)
        {
            names.Add(digit.ToString());
        }

        for (int number = 1; number <= 12; number++)
        {
            names.Add("F" + number.ToString(CultureInfo.InvariantCulture));
        }

        foreach ((string name, _) in Named)
        {
            names.Add(name);
        }

        return names;
    }
}
