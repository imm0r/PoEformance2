namespace PoEformance.Features;

/// <summary>
/// What a capture's memory pass reads once more inside the recording, as the ticked parts ask for it - see <see cref="CaptureParts"/>.
/// </summary>
/// <remarks>
/// THE PASS IS CUT TO THE QUESTION. One recording used to read everything the tool knows how to read,
/// and most of it was for nobody: a question about a room's doodads needs the entity maps and
/// nothing of the raw sweep, and a question about a light needs the sweep and nothing of the maps.
/// Each part says what the pass must read for it, and the pass reads the union - the recording is
/// then the overlay's own reads for a few seconds, which it always is, plus exactly what was asked.
/// </remarks>
[Flags]
public enum CaptureReads
{
    /// <summary>Nothing beyond the overlay's own reads.</summary>
    None = 0,

    /// <summary>A fresh world read with every switch on: the terrain, every entity the game lists, the player, the camera.</summary>
    World = 1,

    /// <summary>The area's loaded-file list.</summary>
    Loaded = 2,

    /// <summary>Both entity maps walked whole, with every named entity's path, position and model - see SleepingDoodads.</summary>
    Doodads = 4,

    /// <summary>The raw sweep: the chain's roots whole, one hop beyond them, and every component of the entities around the player - see CaptureSweep.</summary>
    Sweep = 8,
}

/// <summary>One thing a capture can write, as the catalogue lists it - see <see cref="CaptureParts"/>.</summary>
/// <param name="Key">Its id, which the settings keep when it is unticked - stable across rewordings.</param>
/// <param name="Group">The heading it sits under on the Keys page.</param>
/// <param name="Label">What its box says.</param>
/// <param name="File">The file it writes in the capture's folder, or empty where it only shapes the recording.</param>
/// <param name="Hint">What it writes and what it is for, as the box's hover.</param>
/// <param name="Reads">What the memory pass reads for it, where the recording is on.</param>
public sealed record CapturePart(string Key, string Group, string Label, string File, string Hint, CaptureReads Reads = CaptureReads.None)
{
    /// <summary>The box's hover: the file it writes, then the hint - made once, since a page draws it every frame.</summary>
    public string Hover { get; } = File.Length > 0 ? File + ": " + Hint : Hint;

    /// <summary>The box's ImGui label, its id after the label - made once for the same reason.</summary>
    public string Box { get; } = Label + "##capture-" + Key;
}

/// <summary>
/// Everything the capture key can write, as one catalogue: what each part is, where it goes, and what it asks of the memory recording.
/// </summary>
/// <remarks>
/// ONE PLACE FOR EVERY DIAGNOSIS, instead of a copy button beside every line that somebody once had
/// to send. The Tile Book had five, the routes page one, the light fold one, every model pane one,
/// the dissector two, the atlas log one - each put one text on the clipboard, each had to be found,
/// and a question about a room took three of them and a screenshot. The capture key already wrote a
/// folder with the pictures and the rooms around the player; now every one of those texts is a part
/// of that folder, and the key writes the parts that are ticked. A new diagnosis is a new part here,
/// a writer for its file, and nothing on any window.
///
/// ALL ON BY DEFAULT, and the settings keep the keys that are OFF (OverlaySettings.CaptureOff): a
/// part added in a later build is then written without anybody ticking it, and a settings file that
/// never touched the boxes gains no key. What a part costs when nothing is there to write is one
/// line in capture.txt saying so.
///
/// THE RECORDING FOLLOWS THE TICKS - see <see cref="CaptureReads"/>. A part that reads memory says
/// what it needs, <see cref="Reads"/> takes the union over the ticked ones, and the pass inside
/// the recording reads that and no more.
/// </remarks>
public static class CaptureParts
{
    /// <summary>The game alone, the overlay hidden for the shot.</summary>
    public const string GamePicture = "game-picture";

    /// <summary>The game with the overlay over it, as the key went down.</summary>
    public const string OverlayPicture = "overlay-picture";

    /// <summary>Every entity the overlay read this frame.</summary>
    public const string Entities = "entities";

    /// <summary>The files the area loaded.</summary>
    public const string LoadedFiles = "loaded-files";

    /// <summary>The area's environment and the rooms' around the player.</summary>
    public const string Environments = "environments";

    /// <summary>Every room file the area loaded, whole.</summary>
    public const string RoomFiles = "room-files";

    /// <summary>The rooms arranged around the player, with their lights and files.</summary>
    public const string RoomsNear = "rooms-near";

    /// <summary>Every room arranged by its ground and tiles.</summary>
    public const string RoomsArranged = "rooms-arranged";

    /// <summary>The Tile Book's picked row against the rooms on the map, and where it parts with the area.</summary>
    public const string RoomPick = "room-pick";

    /// <summary>Every tile file the area laid, as a room's slot asks about it.</summary>
    public const string TileIdentities = "tile-identities";

    /// <summary>The survey of the area's entity maps for the rooms' doodads, every sighting listed.</summary>
    public const string Doodads = "doodads";

    /// <summary>Every room placed by its doodads.</summary>
    public const string RoomsPlaced = "rooms-placed";

    /// <summary>The active routes.</summary>
    public const string Routes = "routes";

    /// <summary>The light hunt's report.</summary>
    public const string SceneLight = "scene-light";

    /// <summary>The model panes' probe lines.</summary>
    public const string ModelProbe = "model-probe";

    /// <summary>The Memory Dissector's places, route and last search.</summary>
    public const string Dissector = "dissector";

    /// <summary>The atlas check's report.</summary>
    public const string AtlasLog = "atlas-log";

    /// <summary>The memory recording itself.</summary>
    public const string Memory = "memory";

    /// <summary>The raw sweep inside the recording.</summary>
    public const string RawSweep = "raw-sweep";

    /// <summary>The headings, in the order the Keys page shows them.</summary>
    public static IReadOnlyList<string> Groups { get; } =
        ["Pictures", "Entities", "Area files", "Rooms by ground and tiles", "Rooms by doodads", "Windows", "Memory"];

    /// <summary>Every part, grouped as <see cref="Groups"/> orders them.</summary>
    public static IReadOnlyList<CapturePart> All { get; } =
    [
        new(GamePicture, "Pictures", "the game alone", CaptureReport.GameShot,
            "The game as it was, the overlay hidden for the shot - what the game drew, with nothing of the tool over it."),
        new(OverlayPicture, "Pictures", "the game with the overlay", CaptureReport.OverlayShot,
            "The game with the overlay over it, as the key went down - what you were looking at."),

        new(Entities, "Entities", "every entity", CaptureReport.EntitiesFile,
            "Every entity the overlay read this frame, nearest first: where it stands, what it is, and where in memory it was. "
            + "With the recording on, the world is read once more inside it with every switch on - visuals, effects, buffs, aim, "
            + "actions, as many entities as the game lists, and the terrain.",
            CaptureReads.World),

        new(LoadedFiles, "Area files", "the area's loaded files", CaptureReport.LoadedFile,
            "Every file the area loaded, as the game lists them - which rooms, tiles, models and environments the area is built from. "
            + "With the recording on, the list is read once more inside it.",
            CaptureReads.Loaded),
        new(Environments, "Area files", "the environments", CaptureReport.EnvironmentsFile,
            "The area's own environment and those the rooms around you name, whole - the light, the fog, the sky."),
        new(RoomFiles, "Area files", "the room files", CaptureReport.RoomFilesFile,
            "Every room file (.arm) the area loaded, whole: the doodad lines and the slots each room is placed by. With "
            + "doodads.txt it is the whole of what the placing works from, so a placing can be run again without the game."),

        new(RoomsNear, "Rooms by ground and tiles", "the rooms around you", CaptureReport.RoomsFile,
            "Every room arranged within three tiles of yours, its doodads' lights and the pieces attached to them, and the file "
            + "of each room you stand in - so a lamp in the picture can be found in a file."),
        new(RoomsArranged, "Rooms by ground and tiles", "every room arranged", CaptureReport.ArrangedFile,
            "Where the ground-and-tile search arranged every room of the area, what disagrees there, and how many of its tiles "
            + "can be walked - the Tile Book's \"rooms:\" line and its hover, as the map draws them without an entity read."),
        new(RoomPick, "Rooms by ground and tiles", "the Tile Book's picked row", CaptureReport.PickFile,
            "The row picked in the Tile Book's room list held against the rooms on the map, and every place it parts with the "
            + "area - the two folds under the list. Nothing where no row is picked."),
        new(TileIdentities, "Rooms by ground and tiles", "the laid tiles' definitions", CaptureReport.TileIdentitiesFile,
            "Every distinct tile file the area laid, with what a room's slot is checked against - size, tag, edge and corner ground "
            + "types. With the recording's terrain and room-files.txt it is what the ground-and-tile search runs again from without the game."),

        new(Doodads, "Rooms by doodads", "the doodads found", CaptureReport.DoodadsFile,
            "The survey of the area's entity maps for the rooms' doodads: each room's share, each path found and how many stand, "
            + "then every sighting with its id, path, model and position. With the recording on, both maps are walked once more "
            + "inside it with every named entity's path, position and model, so the placing can be run again from the recording.",
            CaptureReads.Doodads),
        new(RoomsPlaced, "Rooms by doodads", "every room placed", CaptureReport.PlacedFile,
            "Every place each room's doodads stand, with hits, lines, offset and tiles agreeing, what it yielded to whom, or why "
            + "it has none - the Tile Book's \"rooms:\" line and its hover, as the map draws them."),

        new(Routes, "Windows", "the active routes", CaptureReport.RoutesFile,
            "Where you stand and every route - its end and its stops - in world units, as the Routes page lists them. Nothing where none is set."),
        new(SceneLight, "Windows", "the light hunt's report", CaptureReport.SceneLightFile,
            "The whole report of the last \"find the readings\" in the Tile Book's Light fold: every reading, where each was found, "
            + "the raw angles beside them. Nothing where none has run."),
        new(ModelProbe, "Windows", "the model probe", CaptureReport.ProbeFile,
            "What the last probe click in a model pane listed under that pixel, and how each translucent layer came out over the "
            + "picture - for every book whose probe is on. Nothing where none is."),
        new(Dissector, "Windows", "the dissector", CaptureReport.DissectorFile,
            "The places the Memory Dissector has open, the route walked to them, and what its last search found with the bytes "
            + "round each place. Nothing where it is untouched."),
        new(AtlasLog, "Windows", "the atlas check", CaptureReport.AtlasLogFile,
            "The Atlas page's \"Check the Read\" report whole, as its Debug Log tab has it. Nothing where it has not run."),

        new(Memory, "Memory", "a memory recording", CaptureReport.MemoryFile,
            "The overlay's own reads for three seconds, and inside them one pass reading once more from nothing whatever the "
            + "ticked parts above ask for - memory.txt beside it says what the pass read where. Replays with --replay, game closed."),
        new(RawSweep, "Memory", "the raw bytes", string.Empty,
            "In the recording: the game's root objects whole, one hop beyond every pointer in them, and every component of the "
            + "entities around you - the bytes nothing reads yet, for the questions nobody has asked. A few megabytes.",
            CaptureReads.Sweep),
    ];

    /// <summary>The parts under one heading, in the catalogue's order.</summary>
    public static IEnumerable<CapturePart> In(string group) => All.Where(part => string.Equals(part.Group, group, StringComparison.Ordinal));

    /// <summary>Whether a part is written, given the keys that are off.</summary>
    public static bool IsOn(string key, IReadOnlySet<string> off)
    {
        ArgumentNullException.ThrowIfNull(off);
        return !off.Contains(key);
    }

    /// <summary>
    /// What the pass inside the recording reads for the parts that are on - nothing where the recording itself is off.
    /// </summary>
    public static CaptureReads Reads(IReadOnlySet<string> off)
    {
        ArgumentNullException.ThrowIfNull(off);
        if (off.Contains(Memory))
        {
            return CaptureReads.None;
        }

        CaptureReads reads = CaptureReads.None;
        foreach (CapturePart part in All)
        {
            if (!off.Contains(part.Key))
            {
                reads |= part.Reads;
            }
        }

        return reads;
    }

    /// <summary>The keys that are off, with any the catalogue no longer knows dropped - what the settings keep.</summary>
    public static IReadOnlyList<string> Kept(IReadOnlySet<string> off)
    {
        ArgumentNullException.ThrowIfNull(off);
        return [.. All.Where(part => off.Contains(part.Key)).Select(part => part.Key)];
    }
}
