using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoEformance.Game.World;

/// <summary>What one atlas content id means.</summary>
/// <param name="Name">The short name, where the game has one. Empty for a bare effect.</param>
/// <param name="Description">
/// The line the game shows on the node. A <c>{0}</c> in it is where the node's own number goes -
/// "Contains {0} additional Shrines" is one entry covering every count of them.
/// </param>
/// <param name="Icon">The game's own art name for it, for anybody drawing icons later.</param>
public readonly record struct AtlasContent(string Name, string Description, string Icon)
{
    /// <summary>Where a magnitude goes, in the wordings that have room for one.</summary>
    public const string Placeholder = "{0}";

    /// <summary>The best single label: the name when there is one, the description otherwise.</summary>
    public string Label => Name.Length > 0 ? Name : Description;

    /// <summary>
    /// The label with this node's own number written into it.
    /// </summary>
    /// <remarks>
    /// THE WORDING DECIDES whether a number is shown at all, which is the whole rule and the
    /// one this project got wrong: a magnitude was appended to everything as "x3", so every
    /// content on the atlas ended in the magnitude of a plain effect - "Area contains Abysses
    /// x64" - because a plain effect carries 1, and 1 is written as 64.
    /// </remarks>
    public string Say(uint raw)
    {
        string label = Label;
        return label.Contains(Placeholder, StringComparison.Ordinal)
            ? label.Replace(
                Placeholder,
                AtlasContentNames.MagnitudeOf(raw).ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal)
            : label;
    }
}

/// <summary>
/// The meaning of the numbers an atlas node carries.
/// </summary>
/// <remarks>
/// DATA, not code, for the same reason the landmark names and the offsets are: which id is
/// "Breach" is knowledge about a game that changes every league, and it belongs in a file that
/// can be corrected without a rebuild. Ported from GameHelper2's AtlasMapNodeContent.
///
/// TWO TABLES, kept apart because the game keeps them apart. Badges hang off the node as
/// objects and effects arrive as tokens in a vector. An id can appear in both with different
/// wording - 0x6157 is the badge "Grand Mirror" and the effect "Contains a reflection of the
/// Map Boss" - so merging them would relabel one of the two.
///
/// A THIRD TABLE USED TO SIT BESIDE THEM and was removed once it was measured rather than
/// assumed: 41 "legacy tokens" carried over from an older client, read as a fallback behind
/// the effects. Every one of their ids was already an effect and not one of the 41 lines
/// differed from the effect's own wording by a character, so the fallback could not fire for
/// any id the game can produce. Dead by construction, not by coincidence - which is the only
/// reason it was safe to delete without a capture to check it against.
///
/// A LOOKUP MASKS THE ID. Only the low sixteen bits identify the content; the high word carries
/// a magnitude ("3 additional Shrines") and, on some tokens, flags. So the number on the node
/// is not the number in the table, and looking one up unmasked finds nothing.
///
/// AND THE FILE IS NOW THE FALLBACK RATHER THAN THE ANSWER. <see cref="Learn"/> replaces what it
/// can with the game's own EndgameMapContent rows the moment an atlas is read, because measuring
/// the shipped file against that table (2026-09-15) found four faults in it that no amount of
/// reading the file could have shown - a badge carrying another badge's text, four sentences with
/// a clause missing, three contents absent, and every high effect id stale by four Stats rows, so
/// a Water biome was reading as Swamp. A file of ids into a table the game renumbers every patch
/// cannot stay right; the table can.
/// </remarks>
public sealed class AtlasContentNames
{
    private readonly IReadOnlyDictionary<uint, AtlasContent> _fileBadges;
    private readonly IReadOnlyDictionary<uint, AtlasContent> _fileEffects;

    // What is in force: the file until an atlas is read, the game's own words afterwards. Swapped
    // whole under Volatile so a reader sees one table or the other and never a half-built one.
    private IReadOnlyDictionary<uint, AtlasContent> _badges;
    private IReadOnlyDictionary<uint, AtlasContent> _effects;
    private IReadOnlyCollection<string> _icons;
    private int _revision;

    private AtlasContentNames(
        IReadOnlyDictionary<uint, AtlasContent> badges,
        IReadOnlyDictionary<uint, AtlasContent> effects)
    {
        _fileBadges = badges;
        _fileEffects = effects;
        _badges = badges;
        _effects = effects;
        _icons = ArtNames(badges, effects);
    }

    /// <summary>
    /// Every art name a pair of tables mentions, each one once.
    /// </summary>
    /// <remarks>
    /// A content with no picture named is left out rather than carried as an empty string: what
    /// asks for this is a walk of four million paths, and an empty name matches the ones with no
    /// name at all.
    /// </remarks>
    private static HashSet<string> ArtNames(
        IReadOnlyDictionary<uint, AtlasContent> badges,
        IReadOnlyDictionary<uint, AtlasContent> effects)
    {
        var icons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IReadOnlyDictionary<uint, AtlasContent> table in new[] { badges, effects })
        {
            foreach (AtlasContent content in table.Values)
            {
                if (content.Icon.Length > 0)
                {
                    icons.Add(content.Icon);
                }
            }
        }

        return icons;
    }

    /// <summary>Nothing known, which is what a missing file leaves.</summary>
    /// <remarks>
    /// A FRESH ONE EVERY TIME, not a shared singleton, and that matters now that <see cref="Learn"/>
    /// exists: a session with no data file still learns from the game, and a shared Empty would
    /// carry one session's contents into everything else holding the same instance.
    /// </remarks>
    public static AtlasContentNames Empty => new(
        new Dictionary<uint, AtlasContent>(),
        new Dictionary<uint, AtlasContent>());

    /// <summary>
    /// Every art name the table mentions, each one once.
    /// </summary>
    /// <remarks>
    /// AN <see cref="AtlasContent.Icon"/> IS A NAME AND NOT A PATH, which is a gap rather than a
    /// choice: the icon column of the game's <c>EndgameMapContent</c> table holds the whole path,
    /// and every project that has published this data - GameHelper2's Atlas plugin, the tool
    /// before this one, and so this file - kept only the last part of it.
    ///
    /// The missing half is not written down here, and it is not guessed at either: the install
    /// says what it calls its own files, so the names go to it in one list and come back as
    /// paths. This is that list, and <see cref="Learn"/> only ever ADDS to it - a walk of the
    /// install index that has already answered stays answered.
    /// </remarks>
    public IReadOnlyCollection<string> Icons => Volatile.Read(ref _icons);

    /// <summary>How many meanings are in force, across both tables.</summary>
    public int Count => Volatile.Read(ref _badges).Count + Volatile.Read(ref _effects).Count;

    /// <summary>Changes whenever <see cref="Learn"/> replaced something, for anything caching.</summary>
    public int Revision => Volatile.Read(ref _revision);

    /// <summary>How many badges the game itself supplied, or zero while the file is on its own.</summary>
    public int LearntBadges { get; private set; }

    /// <summary>How many effects it supplied. Fewer than it could - see <see cref="Learn"/>.</summary>
    public int LearntEffects { get; private set; }

    /// <summary>Every badge the FILE names, which is what a comparison against the game wants.</summary>
    public IReadOnlyDictionary<uint, AtlasContent> Badges => _fileBadges;

    /// <summary>Every effect it names, likewise. Keyed by the masked id, not by what a node carries.</summary>
    public IReadOnlyDictionary<uint, AtlasContent> Effects => _fileEffects;

    /// <summary>What identifies a content: the low half of whatever the node carried.</summary>
    public static uint IdOf(uint raw) => raw & 0xFFFF;

    /// <summary>
    /// The high half is a magnitude in SIXTY-FOURTHS, not a count.
    /// </summary>
    /// <remarks>
    /// Which is why a plain effect - one of anything - arrives as 64 rather than as 1, and why
    /// this project spent a while showing "Area contains Abysses x64" on every node of the
    /// atlas. A binary effect ("always", "doubles") carries 100.
    /// </remarks>
    public const uint MagnitudeUnit = 64;

    /// <summary>
    /// Delirious, the one token whose high half is not all magnitude.
    /// </summary>
    /// <remarks>
    /// Its top two bits are flags, so they are masked off before the division. Left unmasked a
    /// delirious map reads as tens of thousands of per cent.
    /// </remarks>
    public const uint DeliriousId = 0x685A;

    /// <summary>
    /// How much of it there is - 3 in "3 additional Shrines".
    /// </summary>
    /// <remarks>
    /// Zero for the many contents that are simply present or absent, and the caller should
    /// treat zero as "no number to show" rather than as the number nought.
    /// </remarks>
    public static uint MagnitudeOf(uint raw)
        => IdOf(raw) == DeliriousId
            ? ((raw >> 16) & 0x3FFF) / MagnitudeUnit
            : (raw >> 16) / MagnitudeUnit;

    /// <summary>What a badge id means, or null when this table has never heard of it.</summary>
    public AtlasContent? Badge(uint raw) => Look(Volatile.Read(ref _badges), raw);

    /// <summary>What a content token means, or null when the effect table has never heard of it.</summary>
    public AtlasContent? Effect(uint raw) => Look(Volatile.Read(ref _effects), raw);

    private static AtlasContent? Look(IReadOnlyDictionary<uint, AtlasContent> table, uint raw)
        => table.TryGetValue(IdOf(raw), out AtlasContent found) ? found : null;

    /// <summary>
    /// Replaces what the file says with what the game says, from EndgameMapContent's own rows.
    /// </summary>
    /// <remarks>
    /// WHY THIS IS NOT JUST A CORRECTED FILE. The faults the measurement found are not typos, they
    /// are DRIFT: a stat id is a position in Stats.dat, so the four rows the game inserted between
    /// 19545 and 24104 moved every later one, and a file of those numbers is wrong from the patch
    /// that inserts them. Reading the table costs one walk per session and is right by
    /// construction on whatever client is running.
    ///
    /// A BADGE IS TAKEN WHOLE. Its id is the row's own position plus 100 (AtlasNode.BadgeVectorBegin,
    /// confirmed 2026-09-15 on 67 of the 70 rows by name as well as by number), and a content has
    /// no magnitude of its own, so name and sentence transfer without interpretation.
    ///
    /// AN EFFECT IS TAKEN ONLY WHERE IT CANNOT BE AMBIGUOUS, which is the conservative half of this
    /// and deliberately leaves value on the table. Three conditions, all measured rather than
    /// assumed: the stat is granted by exactly ONE row, that row grants only that stat, and the
    /// row's sentence contains NO NUMBER. The first two are because a shared stat belongs to no one
    /// content - "Contains {0} additional Essence" is granted by three rows with three different
    /// sentences, and picking one would relabel the other two. The THIRD is the one worth spelling
    /// out: where a sentence has a number, whose number it is has not been settled. The file writes
    /// "{0}" and substitutes the magnitude off the node's token; the row writes the content's own
    /// amount. For "Area has 2 additional random Waystone Modifiers" holing the digit reproduces
    /// the file exactly - but "Area has +1 to Monster Level" holed would print "+100" the moment a
    /// token arrives as a binary effect, and nothing here says which of those Irradiated is. So 22
    /// of the 28 otherwise-eligible stats are taken and the six with numbers are left to the file,
    /// which already holes the ones it knows.
    ///
    /// The six biomes are in those 22, which is the fault this fixes outright.
    /// </remarks>
    /// <param name="rows">EndgameMapContent, in table order.</param>
    /// <param name="badgeIdBase">What a row's index is added to. 100, from the schema.</param>
    /// <returns>How many meanings the game supplied, badges and effects together.</returns>
    public int Learn(IReadOnlyList<MapContentRow> rows, uint badgeIdBase)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return 0;
        }

        var badges = new Dictionary<uint, AtlasContent>(_fileBadges);
        var effects = new Dictionary<uint, AtlasContent>(_fileEffects);

        // Which rows grant a stat, so a shared one can be told from a content's own.
        var owners = new Dictionary<long, int>();
        foreach (MapContentRow row in rows)
        {
            foreach (long stat in row.Stats)
            {
                owners[stat] = owners.GetValueOrDefault(stat) + 1;
            }
        }

        int badgeCount = 0;
        int effectCount = 0;
        foreach (MapContentRow row in rows)
        {
            uint id = (uint)row.Index + badgeIdBase;
            string words = EndgameMapContentCatalogue.AsTheFileWouldWriteIt(row.Description);

            // The art name where the game supplies a path, and the file's otherwise: a row with no
            // picture of its own may still have a name somebody has dropped a PNG under.
            string icon = row.IconPath.Length > 0
                ? EndgameMapContentCatalogue.ArtName(row.IconPath)
                : badges.TryGetValue(id, out AtlasContent had) ? had.Icon : string.Empty;

            badges[id] = new AtlasContent(row.Name, words, icon);
            badgeCount++;

            if (row.Stats.Count != 1 || owners[row.Stats[0]] != 1 || words.Any(char.IsDigit))
            {
                continue;   // shared, compound, or carrying a number whose owner is unsettled
            }

            effects[(uint)row.Stats[0]] = new AtlasContent(string.Empty, words, icon);
            effectCount++;
        }

        LearntBadges = badgeCount;
        LearntEffects = effectCount;

        // ADDED TO rather than rebuilt, so this cannot take a name away: the install walk behind
        // Icons may already have turned one into a path, and a name that vanishes would strand it.
        var icons = new HashSet<string>(Volatile.Read(ref _icons), StringComparer.OrdinalIgnoreCase);
        icons.UnionWith(ArtNames(badges, effects));

        Volatile.Write(ref _badges, badges);
        Volatile.Write(ref _effects, effects);
        Volatile.Write(ref _icons, icons);
        Interlocked.Increment(ref _revision);
        return badgeCount + effectCount;
    }

    /// <summary>
    /// Loads the file, or returns <see cref="Empty"/> when it is missing or unreadable.
    /// </summary>
    /// <remarks>
    /// Never throws. An atlas with unnamed contents is worth having; a tool that will not start
    /// because somebody edited a comma into the wrong place is not.
    /// </remarks>
    public static AtlasContentNames Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return Empty;
        }

        try
        {
            using FileStream stream = File.OpenRead(path);
            AtlasContentFile? file = JsonSerializer.Deserialize(stream, AtlasContentJsonContext.Default.AtlasContentFile);
            if (file is null)
            {
                return Empty;
            }

            return new AtlasContentNames(Table(file.Badges), Table(file.Effects));
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return Empty;
        }
    }

    /// <summary>Turns one table of the file into something keyed by number.</summary>
    /// <remarks>
    /// The keys are written as hex strings because that is how every id in this game is
    /// discussed, read and reported - a file of decimals would be correct and unreadable, and
    /// unreadable data does not get corrected.
    /// </remarks>
    private static Dictionary<uint, AtlasContent> Table(Dictionary<string, AtlasContentEntry>? entries)
    {
        var built = new Dictionary<uint, AtlasContent>();
        foreach ((string key, AtlasContentEntry entry) in entries ?? [])
        {
            if (Parse(key) is uint id)
            {
                built[id] = new AtlasContent(entry.Name ?? string.Empty, entry.Description ?? string.Empty, entry.Icon ?? string.Empty);
            }
        }

        return built;
    }

    private static uint? Parse(string key)
    {
        string text = key.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? key[2..] : key;
        return uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out uint id) ? id : null;
    }
}

/// <summary>The shape of the file on disk.</summary>
public sealed class AtlasContentFile
{
    [JsonPropertyName("badges")]
    public Dictionary<string, AtlasContentEntry>? Badges { get; set; }

    [JsonPropertyName("effects")]
    public Dictionary<string, AtlasContentEntry>? Effects { get; set; }
}

/// <summary>One entry of it.</summary>
public sealed class AtlasContentEntry
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }
}

/// <summary>Source-generated JSON, so the atlas data survives Native AOT.</summary>
[JsonSerializable(typeof(AtlasContentFile))]
public sealed partial class AtlasContentJsonContext : JsonSerializerContext;
