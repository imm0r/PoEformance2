using System.Text;

namespace PoEformance.Game.World;

/// <summary>Where one field's value came from, and whether the sources agree.</summary>
public enum Agreement
{
    /// <summary>Only the file has it. Losing the file loses the value.</summary>
    FileOnly,

    /// <summary>Only the game has it. Nothing in the file to compare against.</summary>
    GameOnly,

    /// <summary>Both have it and they say the same thing.</summary>
    Agree,

    /// <summary>Both have it and they disagree. The interesting case.</summary>
    Differ,

    /// <summary>Neither has it.</summary>
    Neither,
}

/// <summary>One map, as the game has it and as the shipped files have it.</summary>
/// <param name="Id">The engine id, which is the key on both sides.</param>
/// <param name="GameUnique">
/// WorldAreas.IsUniqueMapArea. There is no file column beside it any more: data/atlas-maps.json's
/// "type": "unique" was removed once the game's answer was in force, so there is nothing to
/// compare and this is simply what is true.
/// </param>
/// <param name="GameName">The name from WorldAreas, TRIMMED - one row ends in a space.</param>
/// <param name="FileName">The name from data/atlas-maps.json.</param>
/// <param name="GameTags">The game's own tag ids - map, map_tower, swamp_biome.</param>
/// <param name="FileTags">The curated words - tower, arbiter, expedition.</param>
/// <param name="Rating">What data/atlas-ratings.json says, once resolved through the file.</param>
/// <param name="OnAtlas">Whether a node with this id was seen on the atlas this session.</param>
/// <param name="AtlasMap">
/// Whether EndgameMaps.dat names this area at all - which is what makes it a map the atlas can
/// send anybody to, as against one of the 269 areas the file carries that it cannot.
/// </param>
public sealed record MapDataRow(
    string Id,
    string GameName,
    string FileName,
    bool InGame,
    bool InFile,
    bool GameUnique,
    IReadOnlyList<string> GameTags,
    IReadOnlyList<string> FileTags,
    int? Rating,
    bool OnAtlas,
    bool AtlasMap = false)
{
    /// <summary>How the two names compare.</summary>
    public Agreement Name => Compare(
        InGame && GameName.Length > 0,
        InFile && FileName.Length > 0,
        string.Equals(GameName, FileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether losing data/atlas-maps.json would cost this map its rating.
    /// </summary>
    /// <remarks>
    /// THE QUESTION THE WHOLE REPORT EXISTS FOR. A rating is written by display NAME and resolved
    /// to ids through that file at load, so the file's name column is the ratings' lookup table.
    /// Where the game supplies the same name, the lookup could be rebuilt from memory and the
    /// file's line is redundant; where it does not, deleting the line silently drops a rating.
    /// </remarks>
    public bool RatingNeedsTheFile => Rating is not null && (!InGame || GameName.Length == 0);

    private static Agreement Compare(bool game, bool file, bool same)
        => (game, file) switch
        {
            (true, true) => same ? Agreement.Agree : Agreement.Differ,
            (true, false) => Agreement.GameOnly,
            (false, true) => Agreement.FileOnly,
            _ => Agreement.Neither,
        };
}

/// <summary>One line of the ratings file, and what became of it.</summary>
public sealed record RatingRow(string Name, int Rating, IReadOnlyList<string> Ids, bool FromGameToo)
{
    /// <summary>Whether the line found a map at all.</summary>
    public bool Resolves => Ids.Count > 0;
}

/// <summary>
/// What the tool reads out of the game beside what it ships in a file, so a person can check it.
/// </summary>
/// <remarks>
/// WHY A REPORT AND NOT A SWITCH. Moving the atlas off data/atlas-maps.json replaces values a
/// person can open in an editor with values read out of memory at runtime, and the honest cost of
/// that is that nobody can see them any more. This is the instrument that keeps them visible: one
/// row per map, both sources side by side, and a verdict per field.
///
/// THE RATINGS ARE THE PART THAT CAN BREAK QUIETLY. data/atlas-ratings.json is written by display
/// NAME and resolved to ids at load through data/atlas-maps.json's English names - never through
/// the game's, which are translated, so the ratings are already safe on a German client. What they
/// are not safe from is the name column going away: that column IS their lookup table. So the
/// report answers, per rating, whether the game supplies the same name - and therefore whether the
/// line could survive the file being cut.
///
/// UNIQUE IS NO LONGER A COMPARISON, because there is nothing left to compare it with: the file's
/// "type": "unique" key was removed once the game's IsUniqueMapArea was in force. What the report
/// shows now is what is true, and whether the table it comes from has been read yet.
///
/// IT IS BUILT ON THE READER THREAD AND PUBLISHED WHOLE. Everything here is immutable, so the
/// interface can read a finished report without locking and without touching the catalogue.
/// </remarks>
/// <param name="UniqueFromGame">
/// Whether WorldAreas has been read, which is what makes the unique column mean anything. Until
/// then nothing is unique - not because the maps are ordinary, but because nobody has asked.
/// </param>
/// <param name="AtlasMaps">
/// How many DISTINCT maps EndgameMaps.dat names - the real size of the atlas's map pool. Nought
/// until that table has been read.
/// </param>
public sealed record MapDataReport(
    IReadOnlyList<MapDataRow> Maps,
    IReadOnlyList<RatingRow> Ratings,
    string Source,
    bool UniqueFromGame = false,
    int AtlasMaps = 0)
{
    /// <summary>Nothing read yet, which is what a session before the first atlas gets.</summary>
    public static MapDataReport Empty { get; } = new([], [], "the atlas has not been read yet");

    /// <summary>Maps the game knows.</summary>
    public int InGame => Count(row => row.InGame);

    /// <summary>Maps the file knows.</summary>
    public int InFile => Count(row => row.InFile);

    /// <summary>Maps where a name exists on both sides and they differ.</summary>
    public int NamesDiffer => Count(row => row.Name == Agreement.Differ);

    /// <summary>How many maps the game calls unique. Nought until the table has been read.</summary>
    public int Uniques => Count(row => row.GameUnique);

    /// <summary>Ratings that resolved to no map at all. A typo, or a renamed map.</summary>
    public int RatingsUnresolved => Ratings.Count(rating => !rating.Resolves);

    /// <summary>Ratings that would be lost if the file's name column went away.</summary>
    public int RatingsNeedingTheFile => Ratings.Count(rating => rating.Resolves && !rating.FromGameToo);

    /// <summary>Whether anything has been read at all.</summary>
    public bool Anything => Maps.Count > 0 || Ratings.Count > 0;

    /// <summary>
    /// Entries the file carries that the atlas can never send anybody to.
    /// </summary>
    /// <remarks>
    /// THE NUMBER THAT NAMES THE MISTAKE. data/atlas-maps.json is a copy of WorldAreas with tags
    /// added, and WorldAreas is every area in the game - so most of what the file calls an atlas
    /// map is a hideout, a campaign zone, a Sanctum floor or the login screen. Nought while
    /// EndgameMaps has not been read, which is not the same as "there are none".
    /// </remarks>
    public int NotAtlasMaps => AtlasMaps == 0 ? 0 : Count(row => row.InFile && !row.AtlasMap);


    /// <summary>
    /// Builds the report from what the game said and what the files say.
    /// </summary>
    /// <param name="catalogue">The WorldAreas walk, or null before one has happened.</param>
    /// <param name="names">data/atlas-maps.json.</param>
    /// <param name="ratings">data/atlas-ratings.json, already resolved.</param>
    /// <param name="onAtlas">Map ids seen as a node this session. Ids only, never names.</param>
    /// <param name="endgame">
    /// EndgameMaps.dat, or null before it has been read. This is the table that says which areas
    /// are ATLAS maps - data/atlas-maps.json never could, because it is a copy of WorldAreas.
    /// </param>
    public static MapDataReport Build(
        WorldAreaCatalogue? catalogue,
        AtlasMapNames names,
        AtlasRatings ratings,
        IReadOnlyCollection<string>? onAtlas = null,
        EndgameMapCatalogue? endgame = null)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(ratings);

        IReadOnlyDictionary<string, WorldArea> areas = catalogue?.All
            ?? new Dictionary<string, WorldArea>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(onAtlas ?? [], StringComparer.OrdinalIgnoreCase);

        // Every id either side knows, so a map missing from one of them is a row rather than a gap.
        var ids = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        ids.UnionWith(areas.Keys);
        ids.UnionWith(names.All.Keys);

        var maps = new List<MapDataRow>(ids.Count);
        foreach (string id in ids)
        {
            bool inGame = areas.TryGetValue(id, out WorldArea? area);
            bool inFile = names.All.TryGetValue(id, out AtlasMapInfo? info);

            maps.Add(new MapDataRow(
                id,
                inGame ? area!.Name.Trim() : string.Empty,
                inFile ? info!.Name : string.Empty,
                inGame,
                inFile,
                inGame && area!.IsUnique,
                inGame ? area!.Tags : [],
                inFile ? info!.Tags : [],
                ratings.Of(id),
                seen.Contains(id),
                endgame?.Holds(id) ?? false));
        }

        // A rating survives the file only if the GAME supplies the same name for one of the ids it
        // resolved to - that is what would let the lookup be rebuilt from memory.
        var rows = new List<RatingRow>(ratings.Wanted.Count);
        foreach (RatedName wanted in ratings.Wanted)
        {
            bool fromGame = wanted.Ids.Any(id =>
                areas.TryGetValue(id, out WorldArea? area)
                && string.Equals(area.Name.Trim(), wanted.Name.Trim(), StringComparison.OrdinalIgnoreCase));

            rows.Add(new RatingRow(wanted.Name, wanted.Rating, wanted.Ids, fromGame));
        }

        string source = catalogue?.Table is { } table
            ? $"\"{table.Path}\", {table.Rows} rows of 0x{table.RowSize:X}"
            : catalogue?.LastError is { Length: > 0 } why ? why : "the WorldAreas table has not been read";

        if (endgame?.Table is { } endgameTable)
        {
            source += $"   +   \"{endgameTable.Path}\", {endgameTable.Rows} rows";
        }

        return new MapDataReport(maps, rows, source, names.Revision > 0, endgame?.Maps.Count ?? 0);
    }

    /// <summary>
    /// The whole report as tab-separated text, for checking it somewhere other than an overlay.
    /// </summary>
    /// <remarks>
    /// TSV rather than JSON because the first thing anybody does with four hundred rows is sort
    /// them in a spreadsheet, and the second is diff two of them. Tabs survive both; a tag list
    /// is joined with commas, which is the one separator a tag id cannot contain.
    /// </remarks>
    public string ToText()
    {
        var text = new StringBuilder(Maps.Count * 96);
        text.Append("# map data, game against file\n# source\t").Append(Source).Append('\n');
        text.Append("# maps\t").Append(Maps.Count)
            .Append("\tin game\t").Append(InGame)
            .Append("\tin file\t").Append(InFile)
            .Append("\tnames differ\t").Append(NamesDiffer)
            .Append("\tunique\t").Append(Uniques)
            .Append("\tunique read from the game\t").Append(UniqueFromGame).Append('\n');
        text.Append("# ratings\t").Append(Ratings.Count)
            .Append("\tunresolved\t").Append(RatingsUnresolved)
            .Append("\tneeding the file\t").Append(RatingsNeedingTheFile).Append('\n');
        text.Append("# atlas maps\t").Append(AtlasMaps)
            .Append("\tfile entries that are not atlas maps\t").Append(NotAtlasMaps).Append('\n');

        text.Append("\nid\tgame name\tfile name\tname\tunique")
            .Append("\tgame tags\tfile tags\trating\ton atlas\tatlas map\n");
        foreach (MapDataRow row in Maps)
        {
            text.Append(row.Id).Append('\t')
                .Append(row.GameName).Append('\t')
                .Append(row.FileName).Append('\t')
                .Append(row.Name).Append('\t')
                .Append(row.InGame ? row.GameUnique.ToString() : "-").Append('\t')
                .Append(string.Join(',', row.GameTags)).Append('\t')
                .Append(string.Join(',', row.FileTags)).Append('\t')
                .Append(row.Rating?.ToString() ?? string.Empty).Append('\t')
                .Append(row.OnAtlas).Append('\t')
                .Append(row.AtlasMap).Append('\n');
        }

        text.Append("\nrating\tvalue\tresolves to\tgame supplies the name\n");
        foreach (RatingRow row in Ratings)
        {
            text.Append(row.Name).Append('\t')
                .Append(row.Rating).Append('\t')
                .Append(row.Resolves ? string.Join(',', row.Ids) : "NOTHING").Append('\t')
                .Append(row.FromGameToo).Append('\n');
        }

        return text.ToString();
    }

    private int Count(Func<MapDataRow, bool> which) => Maps.Count(which);
}
