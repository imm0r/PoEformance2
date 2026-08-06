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
public sealed record PreloadFinding(string Name, PreloadWeight Weight, string Path);

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

    /// <summary>What a path means, or null when it is one of the thousands that mean nothing.</summary>
    public static PreloadFinding? Meaning(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

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
        // breach, and "Breach" thirteen times is not a summary of anything.
        var findings = new List<PreloadFinding>();
        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in all)
        {
            if (PreloadMeanings.Meaning(path) is PreloadFinding finding && named.Add(finding.Name))
            {
                findings.Add(finding);
            }
        }

        findings.Sort((a, b) => b.Weight.CompareTo(a.Weight));

        lock (_gate)
        {
            Area = area;
            Looked = true;
            Note = note;
            _all = all;
            _findings = findings;
        }
    }

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
    public string Summary()
    {
        IReadOnlyList<PreloadFinding> findings = Findings;
        return findings.Count == 0 ? string.Empty : string.Join(", ", findings.Select(f => f.Name));
    }
}
