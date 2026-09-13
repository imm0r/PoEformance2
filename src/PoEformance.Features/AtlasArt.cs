namespace PoEformance.Features;

/// <summary>
/// The picture the game draws for a thing a map contains, wherever it can be got.
/// </summary>
/// <remarks>
/// THE DATA HAS A NAME AND NOT A PATH. Every content in <c>atlas-content.json</c> carries the
/// game's own art name - "AtlasIconContentBreach" - because that is all any published version
/// of this table has: the game's table holds the whole path in its icon column, and every
/// project that has republished it kept the last part and shipped extracted pictures alongside.
///
/// SO THE INSTALL IS ASKED WHAT IT CALLS ITS OWN FILES. The index's blob of spelled-out paths
/// holds every one of them; walking it once turns a name into a path, for whatever the current
/// patch happens to be. That is the whole point: no folder list kept by hand, nothing to correct
/// when a league moves a file, and nobody has to install a pile of extracted pictures. See
/// <c>BundleIndex.Look</c> for what that walk costs and why it takes the whole list at once.
///
/// A FOLDER SOMEBODY FILLED comes FIRST all the same, and it is not vestigial: it works with no
/// install at all, it is how a picture the install will not give up gets drawn anyway, and it is
/// the one way to override what the game ships. Drop <c>AtlasIconContentBreach.png</c> in and
/// that is the breach icon.
///
/// NEVER BLOCKS AND NEVER THROWS. It is asked while a frame is being drawn, several times per
/// map, so every answer is a dictionary hit: the walk happens once on a background task, and
/// until it comes back a name simply has no picture and its content draws as words. The art
/// store behind it does its unpacking on its own threads too.
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

    /// <summary>
    /// What the install calls the files whose names are handed to it, or null when there is no
    /// install to ask.
    /// </summary>
    /// <remarks>
    /// Handed in rather than reached for, because opening an install belongs to whoever owns
    /// this machine's copy of the game - and because a walk of four million paths must be
    /// something a test can stand in for with a dictionary.
    /// </remarks>
    public Func<IReadOnlyCollection<string>, Dictionary<string, string>>? Names { get; set; }

    /// <summary>
    /// Every art name that will ever be asked for, so the install is walked ONCE.
    /// </summary>
    /// <remarks>
    /// The walk is the expensive part - the blob of paths unpacks to tens of megabytes - so
    /// asking per name would pay it per name. Set this to the whole list the data knows about
    /// and the first question answers all of them.
    /// </remarks>
    public IReadOnlyCollection<string> Wanted { get; set; } = [];

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
    /// THE INSTALL'S ANSWER IS KEPT, and that is a deliberate narrowing of what this button used
    /// to claim. The walk releases the blob it read - tens of megabytes that a second walk would
    /// have to re-read the whole index to rebuild - so there is one walk per session, and a
    /// button that quietly produced no pictures at all would be worse than one that only does
    /// what its name says. A patched game is a restarted tool anyway: the install is opened once,
    /// when the tool starts.
    /// </remarks>
    public void LookAgain()
    {
        lock (_gate)
        {
            _asked.Clear();
            _dropped = null;

            // Only what the FOLDER answered is forgotten. An install path already resolved is
            // still the right path, and dropping it would ask the store to unpack it again.
            var kept = new List<string>();
            foreach ((string name, string file) in _found)
            {
                if (_paths is null || !_paths.ContainsKey(name))
                {
                    kept.Add(name);
                }
            }

            foreach (string name in kept)
            {
                _found.Remove(name);
            }
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

    /// <summary>What the install has for this name, once it has said where its art lives.</summary>
    /// <remarks>
    /// The walk is asked for on the FIRST question and never again, on a background task - it is
    /// tens of megabytes unpacked and over four million paths, which is not something to do on
    /// the thread drawing frames. Until it comes back this answers nothing and the content draws
    /// as words, which is the same thing that happens on a machine with no install.
    /// </remarks>
    private string Unpacked(string name)
    {
        if (Store is not { } store)
        {
            return string.Empty;
        }

        Dictionary<string, string>? paths = Asked();
        return paths is not null && paths.TryGetValue(name, out string? path)
            ? store.Local(path)
            : string.Empty;
    }

    // What the install calls each wanted name, once the walk has finished. Null until then.
    private Dictionary<string, string>? _paths;
    private bool _walking;

    /// <summary>
    /// The install's answer, starting the one walk that produces it if nobody has yet.
    /// </summary>
    /// <remarks>
    /// A FLAG RATHER THAN A TASK KEPT AROUND. Nothing waits for this and nothing cancels it: the
    /// question is asked again on the next frame anyway, so the only thing needed is that the
    /// walk is not started twice.
    /// </remarks>
    private Dictionary<string, string>? Asked()
    {
        lock (_gate)
        {
            if (_paths is not null)
            {
                return _paths;
            }

            if (_walking || Names is null || Wanted.Count == 0)
            {
                return null;
            }

            _walking = true;
        }

        _ = Task.Run(() =>
        {
            Dictionary<string, string> found;
            try
            {
                found = Names?.Invoke(Wanted) ?? [];
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException
                                                 or UnauthorizedAccessException or NotSupportedException)
            {
                // An install being patched under us, or one this cannot unpack. Either way the
                // pictures do not arrive and the atlas keeps its words - worth not crashing over.
                found = [];
            }

            lock (_gate)
            {
                _paths = found;
            }
        });

        return null;
    }
}
