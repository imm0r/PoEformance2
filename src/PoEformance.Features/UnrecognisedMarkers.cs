using PoEformance.Game.World;

namespace PoEformance.Features;

/// <summary>
/// Collects the icon names the classifier could not place, so none is lost.
/// </summary>
/// <remarks>
/// EVERY ONE OF THESE IS A CANDIDATE KEYWORD. A marker draws as the unrecognised shape when
/// the game's own icon name matches none of the words <see cref="PoiGlyphs"/> knows - and the
/// way to fix that is to add a word, which needs the name. The map prints it beside the
/// marker now, but only while somebody is looking at that marker, in that area, on that run.
/// Three of them turned up in one small area and were nearly lost that way.
///
/// So it is written down instead. The file is not a log of sightings but a SET of names: the
/// same "GuildStash" turns up in every hideout ever entered, and a line per sighting would
/// bury the twenty names worth reading under thousands of repeats.
///
/// APPENDED, never rewritten, for the reason <see cref="PreloadAlerts"/> gives for its own
/// log: the point of the file is what has turned up over a league, and a run that opened it
/// for writing would answer the question by destroying it. Names already in it are loaded
/// once at startup and never written again.
///
/// AN EXAMPLE PATH TRAVELS WITH EACH NAME. "MapVaalSideArea" says little on its own;
/// Metadata/Terrain/Missions/VaalSideAreas/... says what the thing is, which is the question
/// somebody adding a keyword is actually asking. One example rather than all of them - the
/// paths repeat far more than the names do.
/// </remarks>
public sealed class UnrecognisedMarkers
{
    private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _pending = [];
    private readonly string _file;
    private bool _loaded;

    /// <param name="path">Where to collect. Defaults to <see cref="LogPath"/>.</param>
    public UnrecognisedMarkers(string? path = null) => _file = path ?? LogPath;

    /// <summary>Where the collected names go.</summary>
    public static string LogPath
        => Path.Combine(AppContext.BaseDirectory, "logs", "unrecognised-markers.tsv");

    /// <summary>How many distinct names have been collected, this run and before it.</summary>
    public int Count => _known.Count;

    /// <summary>
    /// Notes every unrecognised marker in a snapshot, and returns how many names were new.
    /// </summary>
    /// <remarks>
    /// CALLED EVERY FRAME and written to almost never, which is the shape this has to have: a
    /// name is new once in the life of an install, so the cost that matters is the one paid
    /// when it is not. That cost is a set lookup per place the game has marked - a dozen or so
    /// entities, not the thousands in the snapshot - and nothing else at all.
    ///
    /// Not tied to entering an area, which was the obvious place for it: the entities are not
    /// all there yet when the area loads, and the marker somebody walks past a minute later is
    /// exactly the one worth catching.
    /// </remarks>
    public int Note(WorldSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Load();

        int found = 0;
        foreach (WorldEntity entity in snapshot.Entities)
        {
            // The game has to have marked it, and the classifier has to have given up on it.
            // A place with no icon of its own was classified from its path and has no name
            // here to collect.
            if (!entity.IsPlace
                || entity.MapIcon.Length == 0
                || PoiGlyphs.For(entity.MapIcon, entity.Poi) != PoiGlyph.Marker
                || !_known.Add(entity.MapIcon))
            {
                continue;
            }

            _pending.Add(Line(entity.MapIcon, entity.Path, snapshot.Area, DateTimeOffset.Now));
            found++;
        }

        if (found > 0)
        {
            Flush();
        }

        return found;
    }

    /// <summary>One row: the name, an example path, where it was seen, and when.</summary>
    /// <remarks>
    /// Tab-separated and unquoted, because the next thing anybody does with this is grep it or
    /// paste a column into a keyword list. Tabs cannot occur in any of these - they are
    /// metadata paths and identifiers out of the game's own data.
    /// </remarks>
    private static string Line(string icon, string path, AreaInfo area, DateTimeOffset when)
        => string.Join(
            '\t',
            icon,
            path,
            area.Name.Length > 0 ? area.Name : "?",
            when.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// Reads the names already collected, once.
    /// </summary>
    /// <remarks>
    /// So that a name recorded last week is not recorded again this week. A missing file is
    /// the ordinary first-run case and not a problem; an unreadable one costs the deduplication
    /// and nothing else, which is worth far less than refusing to collect at all.
    /// </remarks>
    private void Load()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        try
        {
            if (!File.Exists(_file))
            {
                return;
            }

            foreach (string line in File.ReadLines(_file))
            {
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                int tab = line.IndexOf('\t', StringComparison.Ordinal);
                _known.Add(tab > 0 ? line[..tab] : line);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Collected names are a convenience; the tool reading the game is not.
        }
    }

    /// <summary>
    /// Appends what is pending, and keeps it pending when it could not be written.
    /// </summary>
    /// <remarks>
    /// A failure is swallowed and RETRIED rather than dropped: this runs on the render thread,
    /// a file open in an editor is not a reason to end a frame, and the name is worth just as
    /// much a second later. It stays in the set either way, so a retry cannot double it up.
    /// </remarks>
    private void Flush()
    {
        try
        {
            string file = _file;
            string folder = Path.GetDirectoryName(file)!;
            Directory.CreateDirectory(folder);

            if (!File.Exists(file))
            {
                File.WriteAllLines(file, Header);
            }

            File.AppendAllLines(file, _pending);
            _pending.Clear();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Kept for the next one. See the remarks.
        }
    }

    private static readonly string[] Header =
    [
        "# Icon names the marker classifier did not recognise, one per line:",
        "# name<TAB>an example metadata path<TAB>area first seen in<TAB>date",
        "#",
        "# Each is a candidate keyword for PoiGlyphs.FromName and PointsOfInterest.FromIcon.",
        "# Written by the tool as it meets them; names already here are never repeated.",
    ];
}
