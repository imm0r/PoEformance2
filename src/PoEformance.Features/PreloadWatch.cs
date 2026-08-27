namespace PoEformance.Features;

/// <summary>
/// The one judgement about a loaded path that is not a preference.
/// </summary>
/// <remarks>
/// What is left of this after the exact-path rewrite is a helper for the ADD button, and that
/// is the whole of it. The list of paths that "cannot testify" went with the fragments: it
/// existed because a fragment matched things an area merely mentions - an Atlas map pin naming
/// a mechanic in every area, forever - and nothing matches now unless somebody put the exact
/// path in the list on purpose.
/// </remarks>
public static class PreloadMeanings
{
    /// <summary>
    /// A first guess at what to call a path, for the row somebody just clicked add on.
    /// </summary>
    /// <remarks>
    /// The file's own name, with the extension taken off. It is a starting point rather than an
    /// answer - the name is editable in the list, and half of these read like asset filenames
    /// because that is what they are.
    /// </remarks>
    public static string Suggest(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        int cut = path.LastIndexOf('/');
        string file = cut >= 0 && cut < path.Length - 1 ? path[(cut + 1)..] : path;

        int dot = file.LastIndexOf('.');
        return dot > 0 ? file[..dot] : file;
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
    private IReadOnlyList<PreloadAlertEntry> _found = [];
    private IReadOnlyList<string> _all = [];
    private IReadOnlyList<PreloadAlertEntry> _watching = [];

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

    /// <summary>Which of the watched paths this area turned out to load, in list order.</summary>
    public IReadOnlyList<PreloadAlertEntry> Found
    {
        get
        {
            lock (_gate)
            {
                return _found;
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

    /// <summary>The curated list. Replaceable while running.</summary>
    public IReadOnlyList<PreloadAlertEntry> Watching
    {
        get
        {
            lock (_gate)
            {
                return _watching;
            }
        }
    }

    /// <summary>
    /// Takes a new list and re-reads the area against it.
    /// </summary>
    /// <remarks>
    /// Re-read from the paths already in hand rather than by walking memory again. The walk
    /// costs a whole read budget and the raw list cannot change while you stand in an area - so
    /// adding an entry shows its line immediately, which is the difference between an editor
    /// somebody trusts and one they have to reload an area to test.
    /// </remarks>
    public void Watch(IEnumerable<PreloadAlertEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        lock (_gate)
        {
            _watching = PreloadAlertStore.Tidy([.. entries]);
            _found = PreloadAlerts.Found(_watching, _all);
        }
    }

    /// <summary>
    /// Adds one path to the list, unless it is already being watched.
    /// </summary>
    /// <returns>Whether it was added, so a caller can say why nothing happened.</returns>
    /// <remarks>
    /// Adding is one click on a row of a list of thousands, so the same row gets clicked twice.
    /// A repeat would otherwise draw the same line twice while only one of the two could be
    /// edited - and the delete button would appear not to work.
    /// </remarks>
    public bool Add(PreloadAlertEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.SaysNothing)
        {
            return false;
        }

        lock (_gate)
        {
            if (_watching.Any(known =>
                    known.Path.Equals(entry.Path, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            _watching = [.. _watching, entry];
            _found = PreloadAlerts.Found(_watching, _all);
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
            _found = PreloadAlerts.Found(_watching, all);
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
            _found = [];
        }
    }

    /// <summary>One line naming what is here, or an empty string when nothing is.</summary>
    public string Summary()
    {
        IReadOnlyList<PreloadAlertEntry> found = Found;
        return found.Count == 0 ? string.Empty : string.Join(", ", found.Select(entry => entry.Shown));
    }
}
