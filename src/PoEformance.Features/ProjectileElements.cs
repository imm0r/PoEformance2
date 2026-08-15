namespace PoEformance.Features;

/// <summary>What kind of damage a projectile looks like it carries.</summary>
public enum ProjectileElement
{
    /// <summary>Nothing in the name said. Drawn in its ordinary colour.</summary>
    Unknown,
    Physical,
    Fire,
    Cold,
    Lightning,
    Chaos,
}

/// <summary>
/// Guesses a projectile's element from what the game calls it.
/// </summary>
/// <remarks>
/// A GUESS FROM THE NAME, and it is worth being blunt about that, because everything else here
/// is read from the game and this is not.
///
/// THE GAME'S OWN FILES DO NOT ANSWER THIS. <c>Projectiles.dat</c> on the 0.5 client carries
/// Id, the art files, the loop and impact animations, a speed and an InheritsFrom - and no
/// element of any kind. Nor is there a join to be made: the only tables that reference
/// Projectiles are DelveDynamite, MonsterProjectileAttack and MonsterProjectileSpell, none of
/// which covers a player's skills. The element belongs to the SKILL, and nothing links the
/// entity the skill spawns back to it. So the choice is a name heuristic or nothing, and a
/// wrong colour on a trail costs a great deal less than a wrong dot on a monster.
///
/// WORD BY WORD, never as a plain substring, and that is the same lesson the noise filter
/// wrote down about "/ao/". "ice" inside Sacrifice, Slice, Practice, Device and Justice would
/// paint half the game blue. Names here are PascalCase, so they split into words cleanly and a
/// word has to BEGIN with a keyword to count - Iceshot does, Sacrifice does not.
///
/// THE LAST MATCH WINS, which is not arbitrary either. The game's own skill list has
/// FireballIncusionFire, FireballIncursionChaos and FireballIncusionLightning in it: one base
/// name with the variant's element stuck on the end. Taking the first match calls two of those
/// three fire.
///
/// The keywords are deliberately short and literal - each one NAMES an element or is an
/// unmistakable synonym. Anything else is <see cref="ProjectileElement.Unknown"/>, which draws
/// in the ordinary colour rather than in a colour somebody made up. The projectile tab prints
/// what each path matched to, so a gap is something you can see rather than something you have
/// to suspect.
/// </remarks>
public static class ProjectileElements
{
    private static readonly (ProjectileElement Element, string[] Words)[] Definitions =
    [
        (ProjectileElement.Fire,
            ["fire", "flame", "flaming", "burn", "blaz", "incend", "magma", "molten", "ember",
             "inferno", "pyro", "ignit", "scorch"]),
        (ProjectileElement.Cold,
            ["ice", "icicle", "frost", "cold", "freez", "frozen", "chill", "glaci", "snow",
             "rime", "winter"]),
        (ProjectileElement.Lightning,
            ["lightning", "shock", "spark", "electro", "thunder", "volt", "galvan", "static"]),
        (ProjectileElement.Chaos,
            ["chaos", "poison", "toxic", "caustic", "venom", "virulen", "plague", "wither",
             "contagion"]),
        (ProjectileElement.Physical,
            ["physical", "phys", "bleed", "impale", "splinter", "shrapnel"]),
    ];

    /// <summary>Every element that has a colour of its own, for the editor and the tests.</summary>
    public static IEnumerable<ProjectileElement> Known => Definitions.Select(d => d.Element);

    /// <summary>What the name of this metadata path suggests it is.</summary>
    public static ProjectileElement Of(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        // The last segment only. The folders above it belong to whoever files the thing -
        // Metadata/Monsters/FireMonsters/objects/IceBolt is a cold projectile in a fire
        // monster's directory, and the name is the half that is about the projectile.
        int slash = path.LastIndexOf('/');
        ReadOnlySpan<char> name = slash >= 0 ? path.AsSpan(slash + 1) : path;

        ProjectileElement found = ProjectileElement.Unknown;
        foreach (Range word in Words(name))
        {
            ProjectileElement said = ElementOf(name[word]);
            if (said != ProjectileElement.Unknown)
            {
                found = said;
            }
        }

        return found;
    }

    /// <summary>Which element a single word begins with, if any.</summary>
    private static ProjectileElement ElementOf(ReadOnlySpan<char> word)
    {
        foreach ((ProjectileElement element, string[] words) in Definitions)
        {
            foreach (string keyword in words)
            {
                if (word.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return element;
                }
            }
        }

        return ProjectileElement.Unknown;
    }

    /// <summary>
    /// Splits a PascalCase name into its words, dropping everything that is not a letter.
    /// </summary>
    /// <remarks>
    /// A run of capitals stays one word, so an all-caps name is not shredded into letters.
    /// Digits and the <c>@70</c> level suffix the game appends end a word rather than joining
    /// the next one to it.
    /// </remarks>
    private static List<Range> Words(ReadOnlySpan<char> name)
    {
        var words = new List<Range>();
        int start = -1;
        for (int i = 0; i <= name.Length; i++)
        {
            bool letter = i < name.Length && char.IsAsciiLetter(name[i]);
            bool breaks = !letter
                          || (i > 0 && char.IsAsciiLetterUpper(name[i]) && !char.IsAsciiLetterUpper(name[i - 1]));

            if (start >= 0 && breaks)
            {
                words.Add(new Range(start, i));
                start = -1;
            }

            if (letter && start < 0)
            {
                start = i;
            }
        }

        return words;
    }
}
