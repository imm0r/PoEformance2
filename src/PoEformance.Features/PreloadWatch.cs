namespace PoEformance.Features;

/// <summary>How much somebody wants to know before walking in.</summary>
public enum PreloadWeight
{
    /// <summary>Worth knowing. A mechanic, an encounter.</summary>
    Notable,

    /// <summary>Worth going out of your way for.</summary>
    Valuable,

    /// <summary>Worth knowing BEFORE you are in range of it.</summary>
    Dangerous,
}

/// <summary>One thing an area turned out to contain.</summary>
/// <param name="Path">A file that matched - the evidence, so a wrong line can be traced.</param>
/// <param name="Files">
/// How many files matched. THE signal for whether a finding is real.
/// </param>
/// <remarks>
/// The count is here because the first live run listed eight league mechanics in one map,
/// which no map has. Something genuinely in an area drags its whole art set in with it -
/// dozens of files - while a stray reference is one file and looks identical without a count
/// beside it. Rather than curate an ever-growing list of paths to distrust, the number says
/// how much there is to distrust: "Breach 40" and "Delirium 1" need no further explanation.
/// </remarks>
/// <param name="Alert">
/// Whether the rule behind it asked to be announced, as opposed to only listed. Copied onto
/// the finding so that whoever draws the banner needs the findings and nothing else.
/// </param>
public sealed record PreloadFinding(
    string Name, PreloadWeight Weight, string Path, int Files = 0, bool Alert = false);

/// <summary>
/// What a loaded-file path means, for the handful of paths that mean something.
/// </summary>
/// <remarks>
/// The rules themselves live in <see cref="PreloadRule"/> and belong to whoever is playing.
/// What stays here is the one judgement that is not a preference: which paths cannot testify
/// about the area at all.
/// </remarks>
public static class PreloadMeanings
{
    /// <summary>
    /// Paths that cannot testify about the area you are standing in, whatever they contain.
    /// </summary>
    /// <remarks>
    /// Kept deliberately short. A finding's file COUNT is the general defence against noise;
    /// this list is only for paths that are wrong by construction rather than merely weak, so
    /// that it does not quietly grow into a place where real findings go to be lost.
    ///
    /// So far there is one: an Atlas map pin is the icon drawn on the world map SCREEN for
    /// some other map, so its league folder says nothing whatsoever about the ground under
    /// your feet. It is loaded once the Atlas exists and would otherwise report the same
    /// mechanic in every area, forever - which is exactly what the first live run did.
    /// </remarks>
    public static IReadOnlyList<string> CannotBeEvidence { get; } =
    [
        "/WorldMaps/Maps/Doodads/Pins/",
    ];

    /// <summary>
    /// The part of a path worth watching for: the folder the file sits in.
    /// </summary>
    /// <remarks>
    /// What "+ watch" on a row of the raw list turns that row into. A whole path is too
    /// specific to be a rule: the game renames and re-versions individual files between
    /// patches while the folder that names the thing - Ritual, Breach, StrongBoxes - stays
    /// put, which is why every shipped rule is a folder rather than a file.
    ///
    /// Slashes on both sides, so "/Abyss/" cannot match an AbyssalCrystal sitting in some
    /// other folder. A path with no folder falls back to itself: there is nothing better to
    /// use, and a rule somebody can see and delete beats refusing to add one.
    /// </remarks>
    public static string WatchableFragment(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        int file = path.LastIndexOf('/');
        if (file <= 0)
        {
            return path;
        }

        int folder = path.LastIndexOf('/', file - 1);
        return folder < 0 ? path[..(file + 1)] : path[folder..(file + 1)];
    }

    /// <summary>Whether a path is one that says nothing about where you are standing.</summary>
    public static bool CannotTestify(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        foreach (string blind in CannotBeEvidence)
        {
            if (path.Contains(blind, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// What a path means under a set of rules, or null when it means nothing.
    /// </summary>
    /// <remarks>
    /// The FIRST matching rule wins, so a list can put something specific above the thing it
    /// is a kind of. Everything else about the order is the user's, including the ways it can
    /// be got wrong - a rule matching "/Maps/" above everything else will happily report every
    /// area as one finding, and that is visible in the window rather than silently prevented.
    /// </remarks>
    public static PreloadFinding? Meaning(string path, IReadOnlyList<PreloadRule> rules)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(rules);

        if (CannotTestify(path))
        {
            return null;
        }

        foreach (PreloadRule rule in rules)
        {
            if (rule.Matches(path))
            {
                return new PreloadFinding(rule.Name, rule.Weight, path, 0, rule.Alert);
            }
        }

        return null;
    }
}

/// <summary>
/// What the area you are standing in turned out to contain.
/// </summary>
/// <remarks>
/// Held between area changes rather than recomputed, because the walk that produces it is
/// expensive enough to be worth doing exactly once. The raw list is kept alongside the
/// findings on purpose: the findings are only as good as the list of meanings, and the way
/// that list grows is somebody looking at what an area actually loaded when the tool had
/// nothing to say about it.
/// </remarks>
public sealed class PreloadWatch
{
    private readonly object _gate = new();
    private IReadOnlyList<PreloadFinding> _findings = [];
    private IReadOnlyList<string> _all = [];
    private IReadOnlyList<PreloadRule> _rules = DefaultPreloadRules.Rules;

    /// <summary>The area these belong to.</summary>
    public uint Area { get; private set; }

    /// <summary>Whether an area has been looked at at all.</summary>
    public bool Looked { get; private set; }

    /// <summary>What went wrong, when nothing came back.</summary>
    public string Note { get; private set; } = string.Empty;

    /// <summary>
    /// What a sweep of the records found, when one was asked for.
    /// </summary>
    /// <remarks>
    /// Plain lines rather than a structure, because nothing reads this - a person does, once,
    /// on the day the walk comes back empty and the question is which of four things moved.
    /// </remarks>
    public IReadOnlyList<string> Sweep { get; private set; } = [];

    /// <summary>Records what a sweep found, for whoever is looking at an empty list.</summary>
    public void Swept(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        lock (_gate)
        {
            Sweep = [.. lines];
        }
    }

    /// <summary>What this area contains that is worth a line.</summary>
    public IReadOnlyList<PreloadFinding> Findings
    {
        get
        {
            lock (_gate)
            {
                return _findings;
            }
        }
    }

    /// <summary>Every path this area loaded - the raw material, and the way the list grows.</summary>
    public IReadOnlyList<string> All
    {
        get
        {
            lock (_gate)
            {
                return _all;
            }
        }
    }

    /// <summary>What a path has to contain to be worth a line. Replaceable while running.</summary>
    public IReadOnlyList<PreloadRule> Rules
    {
        get
        {
            lock (_gate)
            {
                return _rules;
            }
        }
    }

    /// <summary>
    /// Takes a new set of rules and re-reads the area under them.
    /// </summary>
    /// <remarks>
    /// Re-read from the paths already in hand rather than by walking memory again. The walk
    /// costs a whole read budget and the raw list cannot change while you stand in an area -
    /// so adding a rule shows its finding immediately, which is the difference between an
    /// editor somebody trusts and one they have to reload an area to test.
    /// </remarks>
    public void UseRules(IEnumerable<PreloadRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        lock (_gate)
        {
            _rules = [.. rules];
            _findings = Read(_all, _rules);
        }
    }

    /// <summary>
    /// Adds one rule, unless an indistinguishable one is already there.
    /// </summary>
    /// <returns>Whether it was added, so a caller can say why nothing happened.</returns>
    /// <remarks>
    /// Adding is a two-click operation from a list of thousands of paths, so the same thing
    /// gets added twice. Matched on the FRAGMENT rather than the name: two rules with the same
    /// fragment produce one finding whatever they are called, and the second is dead weight
    /// that only shows up as a line the delete button appears not to remove.
    /// </remarks>
    public bool AddRule(PreloadRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (rule.SaysNothing)
        {
            return false;
        }

        lock (_gate)
        {
            if (_rules.Any(known =>
                    known.PathContains.Equals(rule.PathContains, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            _rules = [.. _rules, rule];
            _findings = Read(_all, _rules);
            return true;
        }
    }

    /// <summary>Takes a fresh file list and works out what it means.</summary>
    public void Took(uint area, IEnumerable<string> paths, string note = "")
    {
        ArgumentNullException.ThrowIfNull(paths);

        List<string> all = [.. paths];
        all.Sort(StringComparer.Ordinal);

        lock (_gate)
        {
            Area = area;
            Looked = true;
            Note = note;
            _all = all;
            _findings = Read(all, _rules);
        }
    }

    /// <summary>What a list of loaded paths turns out to contain, under a set of rules.</summary>
    private static IReadOnlyList<PreloadFinding> Read(
        IReadOnlyList<string> all, IReadOnlyList<PreloadRule> rules)
    {
        // Deduplicated by NAME rather than by path: an area loads a dozen files for one
        // breach, and "Breach" thirteen times is not a summary of anything. The dozen is not
        // thrown away though - it is counted, because how MANY files a mechanic dragged in is
        // the difference between it being here and it being mentioned somewhere.
        var findings = new List<PreloadFinding>();
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string path in all)
        {
            if (PreloadMeanings.Meaning(path, rules) is not PreloadFinding finding)
            {
                continue;
            }

            if (byName.TryGetValue(finding.Name, out int at))
            {
                PreloadFinding seen = findings[at];

                // Prefer evidence that is not an item definition. An item says the thing CAN
                // exist - a pinnacle key is defined whether or not the mechanic is in this
                // map - so quoting one as the reason a finding appeared points at the wrong
                // thing. When every match is an item the count stays low and says so.
                findings[at] = seen with
                {
                    Path = IsItemDefinition(seen.Path) && !IsItemDefinition(path) ? path : seen.Path,
                    Files = seen.Files + 1,
                };
                continue;
            }

            byName[finding.Name] = findings.Count;
            findings.Add(finding with { Files = 1 });
        }

        // Strongest first within a weight, so the line that is actually worth reading leads.
        findings.Sort((a, b) => a.Weight != b.Weight
            ? b.Weight.CompareTo(a.Weight)
            : b.Files.CompareTo(a.Files));

        return findings;
    }

    /// <summary>Whether a path defines an item rather than showing something in the world.</summary>
    private static bool IsItemDefinition(string path)
        => path.Contains("Metadata/Items/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Forgets the area, so a fresh one is looked at again.</summary>
    public void Forget()
    {
        lock (_gate)
        {
            Area = 0;
            Looked = false;
            Note = string.Empty;
            _all = [];
            _findings = [];
        }
    }

    /// <summary>One line naming what is here, or an empty string when nothing is.</summary>
    /// <remarks>
    /// Weak findings are marked rather than dropped. A single matching file is usually a
    /// passing reference and not the mechanic being here, but "usually" is not "always", and
    /// a summary that silently deletes the marginal case is worse than one that flags it -
    /// the whole point of the raw list is that somebody can go and look.
    /// </remarks>
    public string Summary()
    {
        IReadOnlyList<PreloadFinding> findings = Findings;
        return findings.Count == 0
            ? string.Empty
            : string.Join(", ", findings.Select(f => f.Files <= 1 ? f.Name + "?" : f.Name));
    }

    /// <summary>
    /// The findings worth saying out loud on the way in.
    /// </summary>
    /// <remarks>
    /// Two gates, and they answer different questions. The rule's own flag is "do I care about
    /// this at all" - strongboxes are in most areas, so a banner naming them teaches the eye to
    /// skip the line that also says Ultimatum. The file count is "is it really here": a single
    /// matching file is usually a mention somewhere, and a banner is the one place where the
    /// marginal case should stay quiet, because unlike the window it cannot be gone back to.
    /// </remarks>
    public IReadOnlyList<PreloadFinding> Announceable(int minFiles)
        => [.. Findings.Where(finding => finding.Alert && finding.Files >= Math.Max(1, minFiles))];

    /// <summary>The one line a banner says, or empty when there is nothing worth one.</summary>
    /// <remarks>
    /// The file counts are left out here and kept in the list. A banner is read in the moment
    /// you walk in, with the fight already starting; "Breach, Ritual" is that sentence, and
    /// "Breach 43 files, Ritual 12 files" is a table being read aloud.
    /// </remarks>
    public string Announcement(int minFiles)
    {
        IReadOnlyList<PreloadFinding> worth = Announceable(minFiles);
        return worth.Count == 0 ? string.Empty : string.Join(", ", worth.Select(finding => finding.Name));
    }
}
