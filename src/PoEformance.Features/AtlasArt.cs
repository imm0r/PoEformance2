namespace PoEformance.Features;

/// <summary>
/// The picture the game draws for a thing a map contains, wherever it can be got.
/// </summary>
/// <remarks>
/// THE DATA HAS A NAME AND NOT A PATH. Every content in <c>atlas-content.json</c> carries the
/// game's own art name - "AtlasIconContentBreach" - because that is all any published version
/// of this table has: the game's table holds the whole path in its icon column, and every
/// project that has republished it kept the last part and shipped extracted pictures alongside.
/// So the folder has to come from somewhere else, and there are two somewheres:
///
/// A FOLDER SOMEBODY FILLED, first, because it always works. Drop
/// <c>AtlasIconContentBreach.png</c> into it and that is the breach icon, no install required,
/// no lookup that can fail. It is also the escape hatch for anything the install cannot give
/// up, and the only route on a machine where the game is not installed.
///
/// THE INSTALL, second, and only where a folder from <see cref="Places"/> turns out to hold the
/// name. Those folders are PROPOSALS: each is tried against the game's own index, which either
/// has the file or does not, so nothing here is drawn on a guess - a wrong folder is a lookup
/// that finds nothing and a content that keeps its written line. What that buys is art that is
/// always the current patch's and that nobody has to install.
///
/// NEVER BLOCKS AND NEVER THROWS. It is asked while a frame is being drawn, several times per
/// map, so every answer is a dictionary hit after the first: a name that has been found is a
/// path, a name that is being looked for is empty, and a name that came to nothing is empty
/// forever after. The art store behind it does its unpacking on its own threads.
/// </remarks>
public sealed class AtlasArt
{
    /// <summary>What a picture may be saved as in the folder, most likely first.</summary>
    /// <remarks>
    /// PNG first because that is what everything that extracts this art writes, and because it
    /// is the one with an alpha channel - these icons are cut out against the atlas rather than
    /// drawn on a tile, and a JPEG of one has a black square round it.
    /// </remarks>
    private static readonly string[] Kinds = [".png", ".jpg", ".jpeg", ".bmp", ".dds"];

    private readonly Dictionary<string, string> _found = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _asked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    // The folder as it was last listed, by name without its extension. Listed once rather than
    // asked per name: sixty content names against one directory read is the same answer either
    // way, and File.Exists per name per kind is five disk questions for every miss.
    private Dictionary<string, string>? _dropped;

    /// <summary>
    /// Where somebody's own pictures go. Beside the tool, next to everything else it keeps.
    /// </summary>
    public string Folder { get; set; } = Path.Combine(AppContext.BaseDirectory, "config", "atlas-icons");

    /// <summary>
    /// The store that unpacks art out of the install, or null to use only the folder.
    /// </summary>
    /// <remarks>
    /// The same store the stash uses, on purpose: one cache on disk, one budget for how much
    /// unpacking a run does, and a picture already fetched for an item is not fetched again.
    /// </remarks>
    public ItemArtStore? Store { get; set; }

    /// <summary>Folders inside the install to try, in order. See the class remarks.</summary>
    public IReadOnlyList<string> Places { get; set; } = [];

    /// <summary>How it is going: pictures in hand, out of the names asked for.</summary>
    /// <remarks>
    /// Reported rather than only used, because a content drawn as its name looks exactly like
    /// a content whose picture has not arrived yet, and both look like the feature being off.
    /// The settings page says which it is.
    ///
    /// ASKED rather than missing, because the difference between "there is no such picture" and
    /// "it is still being unpacked" is not knowable from here - the store answers both with an
    /// empty string. What can be said honestly is how many of the names that came up have a
    /// picture, and that is the number worth showing: it goes up as they arrive and stops.
    /// </remarks>
    public (int Found, int Asked) Tally
    {
        get
        {
            lock (_gate)
            {
                return (_found.Count, _asked.Count);
            }
        }
    }

    /// <summary>
    /// The file holding a content's picture, or an empty string when there is not one yet.
    /// </summary>
    /// <param name="name">The art name, without a folder or an extension.</param>
    public string File(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        lock (_gate)
        {
            if (_found.TryGetValue(name, out string? already))
            {
                return already;
            }

            _asked.Add(name);
        }

        string file = Dropped(name);
        if (file.Length == 0)
        {
            file = Unpacked(name);
        }

        if (file.Length == 0)
        {
            return string.Empty;   // still looking, or nowhere to look - ask again next frame
        }

        lock (_gate)
        {
            _found[name] = file;
        }

        return file;
    }

    /// <summary>
    /// Reads the folder again, and forgets what was in it.
    /// </summary>
    /// <remarks>
    /// For when somebody has just put the pictures there. Without it the answer for a name
    /// asked once and missed is remembered for the rest of the session, and dropping the files
    /// in appears to do nothing until the tool is restarted.
    ///
    /// The install's answers go too. They are cached on disk by the store, so re-asking is
    /// cheap - and the reason to press this is usually that something about the art changed.
    /// </remarks>
    public void LookAgain()
    {
        lock (_gate)
        {
            _found.Clear();
            _asked.Clear();
            _dropped = null;
        }
    }

    /// <summary>What the folder holds for this name, if anything.</summary>
    private string Dropped(string name)
    {
        Dictionary<string, string> listed;
        lock (_gate)
        {
            _dropped ??= List(Folder);
            listed = _dropped;
        }

        return listed.TryGetValue(name, out string? file) ? file : string.Empty;
    }

    /// <summary>
    /// Every picture in the folder, by name without its extension.
    /// </summary>
    /// <remarks>
    /// A LISTING RATHER THAN A LOOKUP PER NAME, and case-insensitively: the art names are
    /// written the way the game's table writes them - "AtlasIconContentBreach" - while a folder
    /// filled by hand or by somebody else's extractor is as likely to hold
    /// "atlasiconcontentbreach.png". On Windows that difference does not matter and on the
    /// machines this is developed on it does, which is exactly the kind of thing that works
    /// everywhere except where it is used.
    /// </remarks>
    private static Dictionary<string, string> List(string folder)
    {
        var listed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return listed;
        }

        try
        {
            foreach (string file in Directory.EnumerateFiles(folder))
            {
                string kind = Path.GetExtension(file);
                if (Array.Exists(Kinds, one => one.Equals(kind, StringComparison.OrdinalIgnoreCase)))
                {
                    // First spelling wins, so a folder holding both a .png and a .dds of one
                    // name settles on the same one every time it is listed.
                    listed.TryAdd(Path.GetFileNameWithoutExtension(file), file);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be read is a folder with nothing in it, as far as this goes.
        }

        return listed;
    }

    /// <summary>What the install has for this name, under whichever proposed folder holds it.</summary>
    /// <remarks>
    /// EVERY FOLDER IS ASKED EVERY TIME until one answers, and that is cheaper than it reads:
    /// the store remembers both its hits and its misses, so after the first frame this is one
    /// dictionary lookup per folder. Stopping early on the first miss would be wrong anyway -
    /// a miss and a not-yet-unpacked look the same from here.
    /// </remarks>
    private string Unpacked(string name)
    {
        if (Store is not { } store || Places.Count == 0)
        {
            return string.Empty;
        }

        foreach (string place in Places)
        {
            string local = store.Local($"{place.TrimEnd('/', '\\')}/{name}");
            if (local.Length > 0)
            {
                return local;
            }
        }

        return string.Empty;
    }
}
