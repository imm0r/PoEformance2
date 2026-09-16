namespace PoEformance.Features;

/// <summary>
/// What a monster resistance profile's NAME says about it.
/// </summary>
/// <remarks>
/// THE NAME IS THE ONLY THING THAT CAN BE READ. Each profile also holds 32 numeric columns -
/// Fire1..Fire5, Cold1..Cold5 and so on - and what the numbered tiers mean is not something this
/// data settles: MinorColdResist reads 30, 30, 30 across the first three while MajorColdResist
/// reads 75, 60, 50, which fits area tiers and several other readings equally well. So
/// MonsterVarieties hands out profile NAMES and this reads what the naming plainly says, which is
/// a different thing from decoding the numbers and is not a step towards it.
///
/// AND WHAT IT PLAINLY SAYS IS A RESISTANCE OR A VULNERABILITY. Over the 19 profiles the table
/// carries, thirteen are {Minor|Major} + element + {Resist|Vuln} and two more are the same with a
/// plural "Resists" over all elements:
///
///     MinorColdResist  MajorColdResist  MinorColdVuln  MajorColdVuln
///     MinorFireResist  MajorFireResist  MinorFireVuln  MajorFireVuln
///     MinorLightningResist  MajorLightningResist  MinorLightningVuln
///     MinorChaosResist  MajorChaosResist
///     MinorAllElementalResists  MajorAllElementalResists
///
/// That distinction is worth surfacing because it reverses what the reader should do: a monster
/// with MinorColdVuln takes MORE cold damage, and a list that showed it beside MajorFireResist
/// under one heading called "resistances" says the opposite of what is true.
///
/// THE OTHER FOUR ARE LEFT ALONE, and that is the whole discipline here: MinionLowAll,
/// MinionFire, MinionCold and MinionLightning carry no Resist or Vuln at all, so which way they
/// point is not something the name says. They come back as null and the caller shows the name as
/// the table spells it, rather than being sorted into a heading that would be a guess.
/// </remarks>
/// <param name="Element">What it is about: Cold, Fire, Lightning, Chaos, AllElemental.</param>
/// <param name="Strength">Minor or Major, as the name spells it.</param>
/// <param name="Vulnerable">Whether this is a weakness rather than a resistance.</param>
public readonly record struct ResistanceName(string Element, string Strength, bool Vulnerable)
{
    private static readonly string[] Strengths = ["Minor", "Major"];

    /// <summary>
    /// Reads a profile name, or null where the name does not say which way it points.
    /// </summary>
    public static ResistanceName? Read(string? name)
    {
        if (name is not { Length: > 0 })
        {
            return null;
        }

        foreach (string strength in Strengths)
        {
            if (!name.StartsWith(strength, StringComparison.Ordinal))
            {
                continue;
            }

            string rest = name[strength.Length..];

            // THE PLURAL IS CHECKED FIRST: "MinorAllElementalResists" also ends with "Resist" once
            // its final s is ignored, and taking the singular would leave an element called
            // "AllElementals".
            if (Ends(rest, "Resists", out string? all))
            {
                return new ResistanceName(all, strength, false);
            }

            if (Ends(rest, "Resist", out string? resisted))
            {
                return new ResistanceName(resisted, strength, false);
            }

            if (Ends(rest, "Vuln", out string? weak))
            {
                return new ResistanceName(weak, strength, true);
            }
        }

        return null;
    }

    /// <summary>The element as a line of text, with how much of it there is.</summary>
    public string Said => $"{Spaced(Element)} ({Strength.ToLowerInvariant()})";

    private static bool Ends(string rest, string suffix, out string element)
    {
        if (rest.Length > suffix.Length && rest.EndsWith(suffix, StringComparison.Ordinal))
        {
            element = rest[..^suffix.Length];
            return true;
        }

        element = string.Empty;
        return false;
    }

    /// <summary>"AllElemental" as "All elemental", which is how somebody would read it aloud.</summary>
    private static string Spaced(string name)
    {
        if (name.Length == 0)
        {
            return name;
        }

        var said = new System.Text.StringBuilder(name.Length + 4);
        said.Append(name[0]);

        for (var at = 1; at < name.Length; at++)
        {
            if (char.IsUpper(name[at]))
            {
                said.Append(' ').Append(char.ToLowerInvariant(name[at]));
                continue;
            }

            said.Append(name[at]);
        }

        return said.ToString();
    }
}
