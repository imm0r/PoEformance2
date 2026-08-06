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
public sealed record PreloadFinding(string Name, PreloadWeight Weight, string Path, int Files = 0);

/// <summary>
/// What a loaded-file path means, for the handful of paths that mean something.
/// </summary>
/// <remarks>
/// The file list runs to a few thousand paths and almost all of it is scenery. What makes the
/// feature useful is the short list of paths that ARE worth a line, and that list is data
/// rather than code: it grows every league, from whoever is looking at the raw list when
/// something new turns up.
///
/// MATCHED LOOSELY, on a fragment of the path. The full paths carry variant suffixes and
/// league-specific folders that change between patches, while the fragment that names the
/// thing - "Breach", "Expedition", "Ritual" - does not. A fragment that stops matching is a
/// line missing from a list; a full path that stops matching is the same, with more work to
/// find out why.
/// </remarks>
public static class PreloadMeanings
{
    /// <summary>
    /// What to look for, longest fragment first.
    /// </summary>
    /// <remarks>
    /// Order matters where one fragment contains another - the first match wins, so anything
    /// more specific has to come before the thing it is a kind of.
    /// </remarks>
    public static IReadOnlyList<PreloadFinding> Known { get; } =
    [
        // League mechanics. The reason to look at this before entering an area at all.
        new("Breach", PreloadWeight.Valuable, "/Breach/"),
        new("Expedition", PreloadWeight.Valuable, "/Expedition/"),
        new("Ritual", PreloadWeight.Valuable, "/Ritual/"),
        new("Delirium", PreloadWeight.Valuable, "/Delirium/"),
        new("Essence", PreloadWeight.Valuable, "/Essence/"),
        new("Ultimatum", PreloadWeight.Valuable, "/Ultimatum/"),
        new("Legion", PreloadWeight.Valuable, "/Legion/"),
        new("Abyss", PreloadWeight.Valuable, "/Abyss/"),
        new("Harvest", PreloadWeight.Valuable, "/Harvest/"),
        new("Strongbox", PreloadWeight.Notable, "StrongBoxes/"),
        new("Shrine", PreloadWeight.Notable, "/Shrines/"),

        // Things that make a map harder rather than richer. Worth knowing at the entrance
        // rather than at the moment one lands on you.
        new("Rogue exile", PreloadWeight.Dangerous, "/ExileLeague"),
        new("Beyond demon", PreloadWeight.Dangerous, "/BeyondDemons/"),
    ];

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

    /// <summary>What a path means, or null when it is one of the thousands that mean nothing.</summary>
    public static PreloadFinding? Meaning(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        foreach (string blind in CannotBeEvidence)
        {
            if (path.Contains(blind, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        foreach (PreloadFinding known in Known)
        {
            if (path.Contains(known.Path, StringComparison.OrdinalIgnoreCase))
            {
                return known with { Path = path };
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

    /// <summary>Takes a fresh file list and works out what it means.</summary>
    public void Took(uint area, IEnumerable<string> paths, string note = "")
    {
        ArgumentNullException.ThrowIfNull(paths);

        List<string> all = [.. paths];
        all.Sort(StringComparer.Ordinal);

        // Deduplicated by NAME rather than by path: an area loads a dozen files for one
        // breach, and "Breach" thirteen times is not a summary of anything. The dozen is not
        // thrown away though - it is counted, because how MANY files a mechanic dragged in is
        // the difference between it being here and it being mentioned somewhere.
        var findings = new List<PreloadFinding>();
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string path in all)
        {
            if (PreloadMeanings.Meaning(path) is not PreloadFinding finding)
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

        lock (_gate)
        {
            Area = area;
            Looked = true;
            Note = note;
            _all = all;
            _findings = findings;
        }
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
}
