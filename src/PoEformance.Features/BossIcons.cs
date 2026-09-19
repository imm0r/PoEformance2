using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoEformance.Features;

/// <summary>
/// Which of the sheet's boss pictures belongs to a boss arena.
/// </summary>
/// <remarks>
/// WHY THE ARENA AND NOT THE MONSTER, which is where this started. The obvious place to hang a
/// boss's own icon is the boss, and it is the wrong place: the monster is not in the entity
/// list until the area around it loads, and by then the walk there has already been decided.
/// The GROUND says it from the first frame - an endgame map is generated at random and a boss
/// room is not - so the arena found in the tiles is what wears the picture. See TerrainLandmarks.
///
/// NOTHING IN THE GAME LINKS THE TWO, which is why this file exists at all. MonsterVarieties
/// has no minimap-icon column (checked against poe-tool-dev/dat-schema: of 1152 tables exactly
/// one references MinimapIcons, and it is a Legion table). The game attaches the boss picture
/// to a marker entity of its own, path fragment "bossroomminimapicon" - which is dropped as
/// noise here and in the AHK tool, and would in any case only turn up once the player is close
/// enough for the game to list it, which is the same "too late" as the monster.
///
/// SO THE NAMES ARE THE JOINT, and they carry more than they look like they do. The icons are
/// called <c>IgnagdukBossActive</c>, <c>IsleOfKinBossActive</c>, <c>G4_3_1_BossActive</c>; the
/// areas are called <c>G4_3_1</c>; the tiles sit under <c>.../GrimTangle/feature/BossArena_01</c>.
/// Measured against the sheet's own name table, all eight G4_* boss icons are exactly an area
/// id plus "Boss" - so a derived candidate that is only accepted WHEN THE SHEET CARRIES IT
/// resolves those without anybody writing them down. <see cref="Candidates"/> is that list, in
/// the order it should be tried; whoever draws decides which of them the sheet actually has.
///
/// WHY GUESSING FROM THE MONSTER'S PATH IS NOT AMONG THEM, measured rather than assumed: of the
/// 37 boss-icon families in the sheet, Kaazuli, Xyclucian, Zicoatl, Rootdredge, SnakePit and
/// three others match NO monster path in the shipped monster table, while "Ignagduk" matches
/// seven - her minions included. A rule with that hit rate is a rule that draws the wrong boss.
///
/// WHAT IS LEFT OVER goes in data/boss-icons.json, by hand, once somebody has seen which boss
/// stood in which arena. Unresolved arenas are collected by <see cref="NoteMissing"/> so the
/// file can be filled from what was actually played rather than from what sounds right.
/// </remarks>
public sealed class BossIcons
{
    /// <summary>What an icon family's picture is called while the boss is still alive.</summary>
    public const string ActiveSuffix = "Active";

    /// <summary>And once it is not. Both are the game's own spelling - see assets/icon-names.tsv.</summary>
    public const string InactiveSuffix = "Inactive";

    /// <summary>The extension every tile path carries, and which a hand-written key may not.</summary>
    private const string TileExtension = ".tdt";

    /// <summary>
    /// Path segments that name no boss, so no candidate is built from them.
    /// </summary>
    /// <remarks>
    /// Short on purpose. A derived candidate costs nothing when it is wrong - it is dropped
    /// unless the sheet carries a picture under exactly that name, and the sheet's names are
    /// boss-specific - so this only holds the words that are both generic AND plausible as the
    /// front of a real family name. Everything else filters itself out.
    /// </remarks>
    private static readonly string[] Generic =
    [
        "metadata", "terrain", "tiles", "tile", "features", "feature", "base", "arena", "boss",
        "bossroom", "arenas", "maps", "map", "league",
    ];

    /// <summary>Words a tile is named for the ROOM by, rather than for the boss in it.</summary>
    /// <remarks>
    /// Taken off the end one at a time by <see cref="WithoutDecoration"/>. "Boss" is not among
    /// them: it is what every family in the sheet ends with, so taking it off would only mean
    /// putting it straight back on.
    /// </remarks>
    private static readonly string[] Decorations =
    [
        "room", "arena", "floor", "tile", "combined", "donut", "fight", "encounter",
    ];

    private readonly Dictionary<string, string> _byArea;
    private readonly Dictionary<string, string> _byTile;
    private readonly Dictionary<string, string> _named;
    private readonly HashSet<string> _skipped;
    private readonly HashSet<string> _missing = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _pending = [];
    private readonly string _log;
    private readonly string[] _comment;
    private bool _loaded;
    private int _revision;

    private BossIcons(
        Dictionary<string, string> byArea,
        Dictionary<string, string> byTile,
        Dictionary<string, string>? named = null,
        HashSet<string>? skipped = null,
        string[]? comment = null,
        string? log = null,
        string source = "")
    {
        _byArea = byArea;
        _byTile = byTile;
        _named = named ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _skipped = skipped ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _comment = comment ?? [];
        _log = log ?? LogPath;
        Source = source;
    }

    /// <summary>Nothing written down - which is the ordinary case, and not a broken install.</summary>
    public static BossIcons Empty { get; } = Blank(null);

    /// <summary>An empty pair of tables, collecting into a log of its own.</summary>
    private static BossIcons Blank(string? log, string source = "") => new(
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        log: log,
        source: source);

    /// <summary>Where the arenas nothing could name are collected.</summary>
    public static string LogPath
        => Path.Combine(AppContext.BaseDirectory, "logs", "boss-arenas.tsv");

    /// <summary>
    /// The file this was read from, and the one <see cref="Remember"/> writes back to.
    /// </summary>
    /// <remarks>
    /// KEPT RATHER THAN LOOKED UP AGAIN, because there is more than one data folder in a working
    /// tree - the repository's and the one beside a built exe - and a table loaded from one and
    /// saved to the other is the kind of mistake that looks like the write silently failing.
    /// Empty when nothing was loaded, which is what makes the write refuse rather than invent a
    /// place to put somebody's afternoon of work.
    /// </remarks>
    public string Source { get; }

    /// <summary>How many pairs were written down.</summary>
    public int Count => _byArea.Count + _byTile.Count;

    /// <summary>How many arenas have turned up that no picture could be found for.</summary>
    public int MissingCount => _missing.Count;

    /// <summary>
    /// Bumped whenever an entry is added, so a cache of what an arena resolved to can tell.
    /// </summary>
    /// <remarks>
    /// The one thing a written entry has to reach is the marker that is on screen while it is
    /// being written. Resolving is cached per landmark and emptied when the area changes (see
    /// PoiLayer), which without this means the entry somebody just filled in does nothing until
    /// they leave the arena they filled it in for - and then looks broken rather than late.
    /// </remarks>
    public int Revision => _revision;

    /// <summary>
    /// Loads the file, or returns <see cref="Empty"/> when it is missing or unreadable.
    /// </summary>
    /// <remarks>
    /// Never throws, for the reason <see cref="Game.World.LandmarkNames"/> gives: without this
    /// the arena is still found, still routed to and still marked - it wears the ordinary boss
    /// shape instead of the boss's own picture. Ending a run over a decoration is the wrong trade.
    /// </remarks>
    public static BossIcons Load(string path, string? log = null)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Blank(log, path);
            }

            BossIconFile? read = JsonSerializer.Deserialize(
                File.ReadAllText(path), BossIconJson.Default.BossIconFile);

            if (read is null)
            {
                return Blank(log, path);
            }

            var byArea = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach ((string area, string family) in read.Areas ?? [])
            {
                byArea[area] = family;
            }

            // Keyed WITHOUT the extension on both sides, so a hand-written key that leaves the
            // .tdt off matches a path out of memory that carries it. The paths were typed by
            // hand next to paths that came out of the game, and the two agree on the letters
            // rather than always on their punctuation.
            var byTile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach ((string tile, string family) in read.Tiles ?? [])
            {
                byTile[WithoutExtension(tile)] = family;
            }

            var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach ((string family, string called) in read.Names ?? [])
            {
                named[family] = called;
            }

            // The block at the top of the file is the whole documentation of its format, and a
            // write that dropped it would take the instructions out of the file the moment
            // somebody used it the way they were instructed to.
            return new BossIcons(
                byArea,
                byTile,
                named,
                new HashSet<string>(read.Skip ?? [], StringComparer.OrdinalIgnoreCase),
                read.Comment ?? [],
                log,
                path);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return Blank(log, path);
        }
    }

    /// <summary>
    /// The icon families that might belong to one arena, best first.
    /// </summary>
    /// <remarks>
    /// CANDIDATES RATHER THAN AN ANSWER, because the question "is there a picture called that"
    /// belongs to whoever holds the sheet, and this layer does not. A caller walks the list and
    /// takes the first name the sheet carries; a list that resolves to nothing is an arena
    /// nobody has named yet, which draws the way it always did.
    ///
    /// THE ORDER IS BY HOW SPECIFIC THE KEY IS, not by how likely it is to hit. What is written
    /// down beats what is derived, because somebody looked. The tile beats the area, because an
    /// area can hold two arenas and the tile names THIS one. The area id beats a path segment,
    /// because it is the game's own name for the instance while a segment is a folder somebody
    /// filed the art under.
    ///
    /// Built per arena when an area's landmarks change rather than per frame - see the cache in
    /// the layer that draws them.
    /// </remarks>
    /// <param name="areaId">The area's id as the game gives it: "G4_3_1", "MapBluff".</param>
    /// <param name="tilePath">The arena tile's own file, as <see cref="Game.World.TerrainLandmark.Path"/> carries it.</param>
    public IReadOnlyList<string> Candidates(string areaId, string tilePath)
    {
        ArgumentNullException.ThrowIfNull(areaId);
        ArgumentNullException.ThrowIfNull(tilePath);

        var found = new List<string>(8);

        if (_byTile.TryGetValue(WithoutExtension(tilePath), out string? byTile))
        {
            Offer(found, byTile);
        }

        if (_byArea.TryGetValue(areaId, out string? byArea))
        {
            Offer(found, byArea);
        }

        // The tile's own file, which is where "Plantaton Boss" comes from in the first place -
        // twice, because a tile is as often named for the ROOM as for the boss in it, and
        // IsleOfKinBossRoom_01 has to be able to reach IsleOfKinBoss.
        string file = FileName(tilePath);
        Offer(found, FamilyOf(file));
        Offer(found, FamilyOf(WithoutDecoration(file)));

        // AN AREA ID IS TAKEN VERBATIM, and that is not a shortcut - it is the one thing
        // FamilyOf must not touch. "G4_3_1" put through the instance-number trim comes out as
        // "G4", because the id's own digits look exactly like the "_01" on the end of a tile
        // file; and squashing its underscores would lose them, while the sheet keeps them.
        //
        // Both spellings, because the sheet carries both: G4_3_1_BossActive has an underscore
        // before Boss and G4_4_2BossActive does not, and nothing outside the art tells them
        // apart. Neither costs anything - a candidate the sheet has no picture for is dropped.
        if (areaId.Length > 0)
        {
            if (areaId.EndsWith("Boss", StringComparison.OrdinalIgnoreCase))
            {
                Offer(found, areaId);
            }
            else
            {
                Offer(found, areaId + "_Boss");
                Offer(found, areaId + "Boss");
            }
        }

        foreach (string segment in Segments(tilePath))
        {
            Offer(found, FamilyOf(segment));
            Offer(found, FamilyOf(WithoutDecoration(segment)));
        }

        return found;
    }

    /// <summary>The picture to look for, given whether the arena has been cleared.</summary>
    public static string Named(string family, bool cleared)
    {
        ArgumentNullException.ThrowIfNull(family);
        return family + (cleared ? InactiveSuffix : ActiveSuffix);
    }

    /// <summary>
    /// What the game calls the boss a picture is of, or empty where nobody said.
    /// </summary>
    /// <remarks>
    /// KEYED BY THE FAMILY AND NOT BY THE AREA, because that is where the name belongs: Saphira
    /// is the boss of Grimhaven AND of Epitaph, and a name written against each area would be
    /// the same sentence twice with two chances of disagreeing with itself. One family, one
    /// name, however many arenas point at it.
    /// </remarks>
    public string NameOf(string family)
    {
        ArgumentNullException.ThrowIfNull(family);
        return _named.TryGetValue(family, out string? called) ? called : string.Empty;
    }

    /// <summary>The family written down for an area, or empty. What the plan counts.</summary>
    public string FamilyFor(string areaId)
    {
        ArgumentNullException.ThrowIfNull(areaId);
        return _byArea.TryGetValue(areaId, out string? family) ? family : string.Empty;
    }

    /// <summary>Whether somebody has said this area has no boss picture to make.</summary>
    /// <remarks>
    /// The list the ToDo list is worked off is the game's own set of endgame maps, and a good
    /// third of it - the six hideouts, the seven towers, the hubs - has no boss in it at all.
    /// Guessing which from the tags would be this project's oldest mistake in a new place, so
    /// the answer comes from whoever went and looked, one tick at a time. See BossIconPlan.
    /// </remarks>
    public bool Skipped(string areaId)
    {
        ArgumentNullException.ThrowIfNull(areaId);
        return _skipped.Contains(areaId);
    }

    /// <summary>The arena tiles collected for an area that nothing could name a picture for.</summary>
    /// <remarks>
    /// Read back out of the log <see cref="NoteMissing"/> writes, which is what makes the tile
    /// field fillable from a map played an hour ago rather than only from the one on screen.
    /// </remarks>
    public IReadOnlyList<string> Unnamed(string areaId)
    {
        ArgumentNullException.ThrowIfNull(areaId);
        Load();

        var tiles = new List<string>();
        foreach (string seen in _missing)
        {
            int tab = seen.IndexOf('\t', StringComparison.Ordinal);
            if (tab > 0 && string.Equals(seen[..tab], areaId, StringComparison.OrdinalIgnoreCase))
            {
                tiles.Add(seen[(tab + 1)..]);
            }
        }

        tiles.Sort(StringComparer.OrdinalIgnoreCase);
        return tiles;
    }

    /// <summary>
    /// Writes one boss down: the areas it is the boss of, its arena tile, its picture and its name.
    /// </summary>
    /// <remarks>
    /// THIS IS THE OTHER HALF OF THE EXPORT, and the reason it is worth having at all. Rendering
    /// a portrait out of the model gives a picture that nothing points at: somebody still has to
    /// know that the file called WifeMonsterMapActive.png belongs to the arena in Grimhaven and
    /// to the one in Epitaph, and that the thing standing in both is called Saphira. That is
    /// three facts, and the only moment anybody has all three is while they are standing in the
    /// room looking at the model. So they are asked for there and written here, in the same
    /// click that writes the pictures - see MonsterPortrait.
    ///
    /// SEVERAL AREAS, ONE FAMILY. A boss is the boss of as many maps as it is the boss of; the
    /// caller splits the field on commas and hands them over together, so the pair of entries
    /// cannot be written half way.
    ///
    /// THE TILE IS OPTIONAL and the areas are not, which is the opposite of how it was first
    /// built. A tile path is the more precise key of the two - it names THIS arena rather than
    /// everything in the area - but it is also the one nobody can type from memory, and an
    /// entry with only a tile covers exactly the maps that arena has already been seen in.
    /// </remarks>
    /// <param name="areas">The area ids this boss stands in. Empty entries are dropped.</param>
    /// <param name="tile">Its arena tile's path, or empty when only the area is known.</param>
    /// <param name="family">The picture's family name - the export's own stem.</param>
    /// <param name="boss">What the game calls it, or empty.</param>
    /// <param name="said">What happened, in a sentence, for the window that asked.</param>
    public bool Remember(
        IReadOnlyList<string> areas, string tile, string family, string boss, out string said)
    {
        ArgumentNullException.ThrowIfNull(areas);
        ArgumentNullException.ThrowIfNull(tile);
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(boss);

        family = family.Trim();
        tile = tile.Trim();
        boss = boss.Trim();

        if (family.Length == 0)
        {
            said = "there is no picture name to file the entry under";
            return false;
        }

        var wanted = new List<string>(areas.Count);
        foreach (string area in areas)
        {
            string trimmed = area.Trim();
            if (trimmed.Length > 0 && !wanted.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                wanted.Add(trimmed);
            }
        }

        if (wanted.Count == 0 && tile.Length == 0)
        {
            said = "an entry needs an area id or a tile path";
            return false;
        }

        foreach (string area in wanted)
        {
            _byArea[area] = family;

            // Writing the entry is the answer to "this one still needs doing", so a tick that
            // was put on it to get it out of the way comes off again by itself.
            _skipped.Remove(area);
        }

        if (tile.Length > 0)
        {
            _byTile[WithoutExtension(tile)] = family;
        }

        if (boss.Length > 0)
        {
            _named[family] = boss;
        }

        _revision++;
        return Save(out said);
    }

    /// <summary>Ticks an area off as having no boss picture to make, or puts it back.</summary>
    public bool Skip(string areaId, bool skip, out string said)
    {
        ArgumentNullException.ThrowIfNull(areaId);
        areaId = areaId.Trim();

        if (areaId.Length == 0)
        {
            said = "there is no area to tick off";
            return false;
        }

        bool moved = skip ? _skipped.Add(areaId) : _skipped.Remove(areaId);
        if (!moved)
        {
            said = string.Empty;
            return true;
        }

        _revision++;
        return Save(out said);
    }

    /// <summary>
    /// Writes the table back over the file it was read from, comment block and all.
    /// </summary>
    /// <remarks>
    /// THROUGH A TEMPORARY FILE, because this is a curated file somebody has been filling in
    /// for an evening and the write happens while a game is running: a crash or a full disk
    /// half way through a direct write leaves a truncated JSON file, which loads as nothing at
    /// all. Renaming over the original is atomic enough that the worst case is the old file.
    ///
    /// SORTED, so that a diff of it shows what was added rather than where the dictionary
    /// happened to put it. The file is in git and read by people.
    /// </remarks>
    public bool Save(out string said)
    {
        if (Source.Length == 0)
        {
            said = "there is no file to write to - data/boss-icons.json was never loaded";
            return false;
        }

        try
        {
            var file = new BossIconFile
            {
                Comment = _comment.Length > 0 ? _comment : null,
                Areas = Sorted(_byArea),
                Tiles = Sorted(_byTile),
                Names = _named.Count > 0 ? Sorted(_named) : null,
                Skip = _skipped.Count > 0 ? [.. _skipped.Order(StringComparer.OrdinalIgnoreCase)] : null,
            };

            string temporary = Source + ".writing";
            File.WriteAllText(temporary, JsonSerializer.Serialize(file, BossIconJson.Default.BossIconFile));
            File.Move(temporary, Source, overwrite: true);

            said = $"wrote {Path.GetFileName(Source)}: {_byArea.Count} areas, {_byTile.Count} tiles";
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or NotSupportedException or JsonException)
        {
            said = $"could not write {Source}: {exception.Message}";
            return false;
        }
    }

    /// <summary>One dictionary in an order a person reading the diff would have chosen.</summary>
    private static Dictionary<string, string> Sorted(Dictionary<string, string> pairs)
    {
        var order = new Dictionary<string, string>(pairs.Count, StringComparer.OrdinalIgnoreCase);
        foreach (string key in pairs.Keys.Order(StringComparer.OrdinalIgnoreCase))
        {
            order[key] = pairs[key];
        }

        return order;
    }

    /// <summary>
    /// Writes down an arena no picture could be found for, and says whether it was new.
    /// </summary>
    /// <remarks>
    /// THE ONLY WAY THE CURATED FILE EVER GETS FILLED. An arena that resolves to nothing is
    /// silent by design - it draws the shape it always drew - so without this the pair that is
    /// missing is known only to whoever happened to be looking at that marker, in that area,
    /// on that run. The same trap <see cref="UnrecognisedMarkers"/> was written for, and the
    /// same answer: a SET of them, appended, with the area id and the tile path that a pair in
    /// data/boss-icons.json is keyed by.
    ///
    /// Not the boss's name, because this does not know it - that is the part a person supplies
    /// after standing in the room.
    /// </remarks>
    public bool NoteMissing(string areaId, string tilePath, string name)
    {
        ArgumentNullException.ThrowIfNull(areaId);
        ArgumentNullException.ThrowIfNull(tilePath);
        ArgumentNullException.ThrowIfNull(name);
        Load();

        if (tilePath.Length == 0 || !_missing.Add($"{areaId}\t{tilePath}"))
        {
            return false;
        }

        _pending.Add(string.Join(
            '\t',
            areaId.Length > 0 ? areaId : "?",
            tilePath,
            name.Length > 0 ? name : "?",
            DateTimeOffset.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)));

        Flush();
        return true;
    }

    /// <summary>The last part of a path, without its extension.</summary>
    private static string FileName(string path)
    {
        int slash = path.LastIndexOf('/');
        string last = slash >= 0 && slash < path.Length - 1 ? path[(slash + 1)..] : path;
        int dot = last.LastIndexOf('.');
        return dot > 0 ? last[..dot] : last;
    }

    private static string WithoutExtension(string path)
        => path.EndsWith(TileExtension, StringComparison.OrdinalIgnoreCase)
            ? path[..^TileExtension.Length]
            : path;

    /// <summary>
    /// The FOLDERS a tile sits under, minus the ones that name no boss.
    /// </summary>
    /// <remarks>
    /// The file on the end is left out because it is offered above, with its extension
    /// handled - passed through here it would become a candidate with ".tdt" in the middle
    /// of it, which is not wrong so much as noise in a list somebody may have to read.
    /// </remarks>
    private static IEnumerable<string> Segments(string path)
    {
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (!Array.Exists(Generic, word => string.Equals(word, parts[i], StringComparison.OrdinalIgnoreCase)))
            {
                yield return parts[i];
            }
        }
    }

    /// <summary>
    /// A name with the words that describe the PLACE rather than the boss taken off the end.
    /// </summary>
    /// <remarks>
    /// A tile is as often named for the room as for what is in it - IsleOfKinBossRoom,
    /// Yama_Arena, MausoleumBoss_ArenaFloor - while the sheet files its art under the boss.
    /// Stripped one word at a time because they stack, and offered ALONGSIDE the unstripped
    /// name rather than instead of it: both are only ever accepted if the sheet carries them,
    /// so the cost of trying one more is a dictionary miss.
    /// </remarks>
    private static string WithoutDecoration(string name)
    {
        string left = name.TrimEnd('_', ' ', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9');

        bool cut = true;
        while (cut && left.Length > 0)
        {
            cut = false;
            foreach (string word in Decorations)
            {
                if (left.EndsWith(word, StringComparison.OrdinalIgnoreCase))
                {
                    left = left[..^word.Length].TrimEnd('_', ' ', '-');
                    cut = true;
                    break;
                }
            }
        }

        return left;
    }

    /// <summary>
    /// A name turned into the family a picture would be filed under.
    /// </summary>
    /// <remarks>
    /// Three things happen, and each is something the real names do: the separators go, because
    /// tiles are written Plantaton_Boss_01 and icons IsleOfKinBoss; a trailing instance number
    /// goes with them; and "Boss" is put on the end when it is not already there, because that
    /// is what every family in the sheet ends with. An empty result is dropped by the caller.
    /// </remarks>
    private static string FamilyOf(string name)
    {
        string trimmed = name.Trim().TrimEnd('_', ' ', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        string squashed = trimmed.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);

        if (squashed.Length == 0)
        {
            return string.Empty;
        }

        return squashed.EndsWith("Boss", StringComparison.OrdinalIgnoreCase) ? squashed : squashed + "Boss";
    }

    /// <summary>Adds a candidate unless it is empty or already offered.</summary>
    private static void Offer(List<string> found, string family)
    {
        if (family.Length == 0)
        {
            return;
        }

        foreach (string already in found)
        {
            if (string.Equals(already, family, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        found.Add(family);
    }

    /// <summary>Reads the arenas already collected, once, so last week's is not written again.</summary>
    private void Load()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        try
        {
            if (!File.Exists(_log))
            {
                return;
            }

            foreach (string line in File.ReadLines(_log))
            {
                string[] parts = line.Split('\t');
                if (parts.Length >= 2 && parts[0].Length > 0 && !line.StartsWith('#'))
                {
                    _missing.Add($"{parts[0]}\t{parts[1]}");
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Costs the deduplication and nothing else: a repeated line is far cheaper than
            // refusing to collect.
        }
    }

    /// <summary>Appends what is pending, and keeps it pending when it could not be written.</summary>
    private void Flush()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_log)!);
            bool fresh = !File.Exists(_log);
            using StreamWriter writer = File.AppendText(_log);
            if (fresh)
            {
                writer.WriteLine(
                    "# Boss arenas with no picture in the sheet: area\ttile\tname\tfirst seen.");
                writer.WriteLine(
                    "# Add the pair to data/boss-icons.json once you have seen which boss stands there.");
            }

            foreach (string line in _pending)
            {
                writer.WriteLine(line);
            }

            _pending.Clear();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Kept for the next flush rather than dropped - a locked log is usually a text
            // editor somebody has it open in, which is over in a moment.
        }
    }
}

/// <summary>
/// The file's shape, including the comment block - which is READ so that it can be written back.
/// </summary>
/// <remarks>
/// The block at the top of data/boss-icons.json is the only documentation of the format, and the
/// tool now writes the file itself. A DTO that ignored the comment, as this one did while the
/// file was only ever read, would quietly delete the instructions the first time somebody
/// followed them.
/// </remarks>
internal sealed class BossIconFile
{
    [JsonPropertyName("comment")]
    public string[]? Comment { get; init; }

    [JsonPropertyName("areas")]
    public Dictionary<string, string>? Areas { get; init; }

    [JsonPropertyName("tiles")]
    public Dictionary<string, string>? Tiles { get; init; }

    /// <summary>What the game calls each family's boss, for the label on its marker.</summary>
    [JsonPropertyName("names")]
    public Dictionary<string, string>? Names { get; init; }

    /// <summary>Areas somebody has looked at and found no boss picture to make.</summary>
    [JsonPropertyName("skip")]
    public string[]? Skip { get; init; }
}

/// <summary>Source-generated so the file still loads under Native AOT.</summary>
/// <remarks>
/// INDENTED, because the file is written by the tool and read by people - it is in git, and a
/// one-line JSON file makes every addition to it the same single changed line. The two sections
/// that ship empty are always written; the two that were added later are left out until they
/// hold something, so a file nobody has written to still looks like the one that ships.
/// </remarks>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BossIconFile))]
internal sealed partial class BossIconJson : JsonSerializerContext;
