using System.Collections.Concurrent;
using PoEformance.Game.Files;
using PoEformance.Game.World;

namespace PoEformance.Features;

/// <summary>
/// Every room the current area may have laid: read once per area, placed by its doodads once the area's entities are read - and, where nothing can read them, searched for by its ground and tiles - for the large map to outline all at once.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS. The tile book finds one room at a time and outlines the row a person picks; with
/// twenty rooms in an area that is twenty picks to see the layout. Asked for: every room drawn at once,
/// where it stands.
///
/// THE ROOMS ARE THE AREA'S WHOLE SET - see AreaRoomSet - not the loaded list's few: The Assembly's
/// list named ten of fifty-three, and neither the boss room nor the entrance.
///
/// TWO ANSWERS, AND WHICH ONE THE MAP TAKES. The doodads first, wherever the area's entities can be
/// read: each doodad line of a room is an entity standing where the line put it, and RoomDoodadFinder
/// finds every place the room's doodads stand - in milliseconds a room, which is what lets fifty rooms
/// be placed. The tiles are then asked about each place found (RoomFinder.Scorer), since two variants
/// that share every doodad differ only in their tiles. The ground-and-tile search (RoomFinder) over
/// the whole area, seconds a room, runs only where the entities cannot be read, and RoomArrangement
/// then gives each room one place; it placed a floor module in the black beside the boss arena and
/// could never draw a wall module the area lays four times, which is why the doodads lead.
///
/// ONE TASK AT A TIME. Reading the files is quick; the survey is one walk of two entity maps (47 ms
/// for an area of 1441 entities) and the finder after it; the fallback search tries its room eight
/// ways round at every tile corner and takes every core it is given, so the rooms take it in turn.
///
/// AGAIN ONLY WHEN THE AREA OR ITS ROOMS CHANGE: the grid by reference, as everywhere, and the rooms
/// by their count - the loaded-file list only ever grows within an area, and a room arriving late is
/// a new placing. The fallback's searches are kept, so a change of RoomOverlap rule arranges them again
/// on the spot.
/// </remarks>
public sealed class AreaRooms
{
    private readonly Func<string, byte[]?> _read;
    private TerrainGrid? _grid;
    private int _rooms = -1;

    /// <summary>The area's room set, cached on the loaded list's identity - see <see cref="RoomSet"/>.</summary>
    private IReadOnlyList<string>? _setOf;
    private IReadOnlyList<string> _set = [];

    /// <summary>The rooms' files being read, and read.</summary>
    private Task<List<(string Room, RoomLayout Layout)>>? _reading;
    private List<(string Room, RoomLayout Layout)>? _layouts;

    /// <summary>The fallback: the ground-and-tile search of every room, and the arrangement made of it.</summary>
    private Task<(List<(string Room, RoomLayout Layout, RoomSearch Search)> Rooms, bool[] Walkable)>? _running;
    private List<(string Room, RoomLayout Layout, RoomSearch Search)>? _searched;
    private bool[]? _walkable;
    private RoomArrangement? _arranged;
    private IReadOnlyList<(string Room, RoomLayout Layout, RoomCandidate Where)>? _arrangedStanding;

    /// <summary>The stubs the area's rooms name, made once per read - see <see cref="Stubs"/>.</summary>
    private IReadOnlySet<string>? _stubs;
    private List<(string Room, RoomLayout Layout)>? _stubsOf;

    /// <summary>The survey of the area's entity maps for those stubs and the places it gives each room, running or run, and the answer once taken - see <see cref="Survey"/>.</summary>
    private Task<(DoodadSurvey Survey, List<(string Room, RoomLayout Layout, RoomDoodadPlaces Places)> Placed, List<(string Room, RoomLayout Layout, RoomCandidate Where)> Standing)>? _survey;
    private DoodadSurvey? _surveyed;
    private List<(string Room, RoomLayout Layout, RoomDoodadPlaces Places)>? _placed;
    private List<(string Room, RoomLayout Layout, RoomCandidate Where)>? _standing;

    /// <summary>The running task's count of rooms done - one array per task, so one left to run out cannot count into the next.</summary>
    private int[] _done = [0];
    private int _of;

    /// <param name="read">How to read a file out of the install - each room's own, the room sets, and the tiles' definitions.</param>
    public AreaRooms(Func<string, byte[]?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        _read = read;
    }

    /// <summary>
    /// Reads the area's entity maps once for the stubs given - see SleepingDoodads - set by whoever owns the game's memory. Null where nothing can, and the map falls back to the search and the arrangement.
    /// </summary>
    public Func<IReadOnlySet<string>, DoodadSurvey>? ReadDoodads { get; set; }

    /// <summary>How many of the area's rooms the running task has done, and of how many - for a line saying it is under way.</summary>
    public (int Done, int Of) Progress => (Volatile.Read(ref _done[0]), Volatile.Read(ref _of));

    /// <summary>Whether the rooms are being read, or searched for by their ground and tiles.</summary>
    public bool Running => _reading is { IsCompleted: false } || _running is { IsCompleted: false };

    /// <summary>The rooms with their files read - null while they are being read. See AreaRoomSet for which rooms.</summary>
    public IReadOnlyList<(string Room, RoomLayout Layout)>? Rooms => _layouts;

    /// <summary>The last arrangement by ground and tiles, or null while none is - the fallback where the entities cannot be read.</summary>
    public RoomArrangement? Last => _arranged;

    /// <summary>The rooms searched by their ground and tiles, each with its search's answer - null in the doodad path, or while the search runs.</summary>
    public IReadOnlyList<(string Room, RoomLayout Layout, RoomSearch Search)>? Searched => _searched;

    /// <summary>Whether a survey of the area's entity maps is under way - see <see cref="Survey"/>.</summary>
    public bool Surveying => _survey is { IsCompleted: false };

    /// <summary>The last survey of the area's entity maps for the rooms' doodads, or null while none has been taken - see SleepingDoodads.</summary>
    public DoodadSurvey? Doodads
    {
        get
        {
            Taken();
            return _surveyed;
        }
    }

    /// <summary>
    /// Every room with the places its doodads stand in the area - every place, a room laid more than once standing more than once - or null while the entities are not read. See RoomDoodadFinder.
    /// </summary>
    /// <remarks>One list per survey, by reference, so a reader can tell a new answer from the last at a glance.</remarks>
    public IReadOnlyList<(string Room, RoomLayout Layout, RoomDoodadPlaces Places)>? Placed
    {
        get
        {
            Taken();
            return _placed;
        }
    }

    /// <summary>
    /// Where every room stands, one entry a place - by the doodads where they are read, else by the arrangement - or null while neither has an answer. What the capture's "rooms around you" is read from.
    /// </summary>
    public IReadOnlyList<(string Room, RoomLayout Layout, RoomCandidate Where)>? Standing
    {
        get
        {
            Taken();
            if (_standing is not null)
            {
                return _standing;
            }

            if (_arranged is { } arranged && _arrangedStanding is null)
            {
                _arrangedStanding = [.. arranged.Laid.Select(laid => (laid.Room, laid.Layout, laid.Where))];
            }

            return _arrangedStanding;
        }
    }

    /// <summary>The room's file as read, or null where it is not one of the area's or not read yet.</summary>
    public RoomLayout? LayoutOf(string room)
    {
        foreach ((string path, RoomLayout layout) in _layouts ?? [])
        {
            if (string.Equals(path, room, StringComparison.OrdinalIgnoreCase))
            {
                return layout;
            }
        }

        return null;
    }

    /// <summary>
    /// Every room the area may have laid - the loaded list's <c>.arm</c> files and every room of its room sets - cached on the list's identity, which changes only with the area. See AreaRoomSet.
    /// </summary>
    public IReadOnlyList<string> RoomSet(IReadOnlyList<string> loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        if (!ReferenceEquals(loaded, _setOf))
        {
            _setOf = loaded;
            _set = AreaRoomSet.Files(loaded, _read);
        }

        return _set;
    }

    /// <summary>
    /// Every path the area's rooms name as a doodad's stub, compared without case - null while the rooms are still being read.
    /// </summary>
    /// <remarks>
    /// THE STUB, NOT THE .ao: a doodad line names both, and the stub is the entity's own path where
    /// the game makes an entity of it - see RoomDoodad.Stub. Most lines carry the plain
    /// Metadata/MiscellaneousObjects/Doodad, and the game keeps those as entities too - 684 of them in
    /// The Assembly - told apart by the model each loaded, which the survey reads beside the path.
    /// </remarks>
    public IReadOnlySet<string>? Stubs()
    {
        if (_layouts is not { } layouts)
        {
            return null;
        }

        if (!ReferenceEquals(layouts, _stubsOf))
        {
            var stubs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach ((_, RoomLayout layout) in layouts)
            {
                foreach (RoomDoodad doodad in layout.Doodads)
                {
                    if (doodad.Stub.Length > 0)
                    {
                        stubs.Add(doodad.Stub);
                    }
                }
            }

            _stubsOf = layouts;
            _stubs = stubs;
        }

        return _stubs;
    }

    /// <summary>
    /// Starts a survey of the area's entity maps for the rooms' doodads and the placing of every room by them, on its own task - see SleepingDoodads and RoomDoodadFinder. False where nothing can read them, the rooms are not read yet, or one is under way.
    /// </summary>
    /// <remarks>Started on its own once the rooms are read - see <see cref="Arranged"/>. The capture key takes the answer as it stands (CaptureParts.Doodads) rather than asking again: the survey is the area's, and the area does not change under it.</remarks>
    public bool Survey()
    {
        if (ReadDoodads is not { } read || Surveying || _layouts is not { } layouts || Stubs() is not { } stubs || _grid is not { Ground: { } ground } grid)
        {
            return false;
        }

        int tilesX = grid.TilesX;
        int tilesY = grid.TilesY;
        TerrainTiles? tiles = grid.Tiles;
        Func<string, byte[]?> readFile = _read;
        _surveyed = null;
        _placed = null;
        _standing = null;
        _survey = Task.Run(() =>
        {
            DoodadSurvey survey = read(stubs);
            bool[] walkable = grid.WalkableTileMask();

            // EACH TILE FILE ONCE across every room's places - the same cache the tile book keeps per area.
            var known = new ConcurrentDictionary<string, TileIdentity?>(StringComparer.OrdinalIgnoreCase);
            TileIdentity? Identity(string path) => known.GetOrAdd(path, one => TileIdentity.Of(TileModels.Defined(readFile, one).Definition));

            var found = new List<(string Room, RoomDoodadPlaces Places)>(layouts.Count);
            foreach ((string room, RoomLayout layout) in layouts)
            {
                RoomDoodadPlaces places = RoomDoodadFinder.Find(layout.Doodads, layout.Width, layout.Height, survey.Found, tilesX, tilesY);
                found.Add((room, places.Places.Count == 0 ? places : places with { Places = Tiled(places.Places, layout, ground, tilesX, tilesY, tiles, Identity, walkable) }));
            }

            // ONE ROOM PER DOODAD: the variants of a room all vote for the tile where the laid one stands.
            List<(string Room, RoomDoodadPlaces Places)> settled = RoomDoodadFinder.Settle(found, tilesX, tilesY);
            var placed = new List<(string Room, RoomLayout Layout, RoomDoodadPlaces Places)>(layouts.Count);
            var standing = new List<(string Room, RoomLayout Layout, RoomCandidate Where)>();
            for (var one = 0; one < layouts.Count; one++)
            {
                (string room, RoomLayout layout) = layouts[one];
                placed.Add((room, layout, settled[one].Places));
                foreach (RoomDoodadPlace place in settled[one].Places.Places)
                {
                    standing.Add((room, layout, place.Where));
                }
            }

            return (survey, placed, standing);
        });
        return true;
    }

    /// <summary>
    /// The area's rooms arranged by their ground and tiles, or null while they are not - starting whatever this area still needs: the files read where the area or its rooms are new, then the doodad survey, or the search where no entities can be read. Called every frame; cheap when nothing changed.
    /// </summary>
    /// <remarks>Null is the ordinary answer in the doodad path, where <see cref="Placed"/> and <see cref="Standing"/> carry the rooms: the arrangement is the fallback's.</remarks>
    /// <param name="grid">The current area, or null where none is read.</param>
    /// <param name="rooms">The rooms the area may have laid, by file - see <see cref="RoomSet"/>.</param>
    /// <param name="rule">Which tiles two rooms may both hold, for the arrangement.</param>
    public RoomArrangement? Arranged(TerrainGrid? grid, IReadOnlyCollection<string> rooms, RoomOverlap rule)
    {
        ArgumentNullException.ThrowIfNull(rooms);
        if (grid?.Ground is not { } ground)
        {
            return null;
        }

        if (!ReferenceEquals(grid, _grid) || rooms.Count != _rooms)
        {
            // LET THE OLD ONE RUN OUT: it reads nothing this one changes, and its answer is dropped.
            _grid = grid;
            _rooms = rooms.Count;
            _layouts = null;
            _searched = null;
            _walkable = null;
            _arranged = null;
            _arrangedStanding = null;
            _running = null;

            // A SURVEY IS THE AREA'S: one running for the old area runs out and is dropped like the search.
            _survey = null;
            _surveyed = null;
            _placed = null;
            _standing = null;
            int[] done = [0];
            _done = done;
            Volatile.Write(ref _of, rooms.Count);
            string[] files = [.. rooms];
            Func<string, byte[]?> readFile = _read;
            _reading = Task.Run(() =>
            {
                var layouts = new List<(string Room, RoomLayout Layout)>(files.Length);
                foreach (string file in files)
                {
                    layouts.Add((file, RoomLayout.Read(readFile(file))));
                    Interlocked.Increment(ref done[0]);
                }

                return layouts;
            });
        }

        if (_layouts is null && _reading is { IsCompleted: true } reading)
        {
            _layouts = reading.IsCompletedSuccessfully ? reading.Result : [];

            // THE DOODADS NEXT, without being asked, wherever they can be read: the map is drawn from
            // them. Else the search, one room at a time.
            if (ReadDoodads is not null)
            {
                Survey();
            }
            else
            {
                List<(string Room, RoomLayout Layout)> layouts = _layouts;
                int[] done = [0];
                _done = done;
                Volatile.Write(ref _of, layouts.Count);
                _running = Task.Run(() => Search(layouts, grid, ground, done));
            }
        }

        if (_searched is null && _running is { IsCompleted: true } finished)
        {
            (_searched, _walkable) = finished.IsCompletedSuccessfully ? finished.Result : ([], null);
        }

        if (_searched is not null && (_arranged is null || _arranged.Rule != rule))
        {
            _arranged = RoomArrangement.Arrange(_searched, grid.TilesX, grid.TilesY, rule, _walkable);
            _arrangedStanding = null;
        }

        return _arranged;
    }

    /// <summary>
    /// Each place with the tiles the area laid agreeing at that very place - the tie-breaker between variants that share every doodad, see RoomDoodadFinder.Settle - where the room can be scored against this area at all.
    /// </summary>
    private static List<RoomDoodadPlace> Tiled(
        IReadOnlyList<RoomDoodadPlace> places,
        RoomLayout layout,
        TerrainGroundTypes ground,
        int tilesX,
        int tilesY,
        TerrainTiles? tiles,
        Func<string, TileIdentity?> identity,
        bool[] walkable)
    {
        (RoomScorer? scorer, _) = RoomFinder.Scorer(layout, ground, tilesX, tilesY, tiles, identity, walkable);
        if (scorer is null || !scorer.Checks)
        {
            return [.. places];
        }

        var tiled = new List<RoomDoodadPlace>(places.Count);
        foreach (RoomDoodadPlace place in places)
        {
            RoomPlace? scored = scorer.Score(place.Where.X, place.Where.Y, place.Where.Turn);
            tiled.Add(scored is null ? place : place with { TilesAgree = scored.Where.TilesAgree });
        }

        return tiled;
    }

    /// <summary>Takes a finished survey's answer, once.</summary>
    private void Taken()
    {
        if (_surveyed is null && _survey is { IsCompleted: true } done)
        {
            if (done.IsCompletedSuccessfully)
            {
                (_surveyed, _placed, _standing) = done.Result;
            }
            else
            {
                _surveyed = DoodadSurvey.Not("the read failed: " + (done.Exception?.GetBaseException().Message ?? "cancelled"));
                _placed = [];
                _standing = [];
            }
        }
    }

    /// <summary>Every room searched in turn by its ground and tiles, and the walkable mask they were searched with - the fallback where the entities cannot be read.</summary>
    private (List<(string Room, RoomLayout Layout, RoomSearch Search)> Rooms, bool[] Walkable) Search(
        List<(string Room, RoomLayout Layout)> layouts, TerrainGrid grid, TerrainGroundTypes ground, int[] done)
    {
        // EACH TILE FILE ONCE across every room - the same cache the tile book keeps per area.
        var known = new ConcurrentDictionary<string, TileIdentity?>(StringComparer.OrdinalIgnoreCase);
        TileIdentity? Identity(string path) => known.GetOrAdd(path, one => TileIdentity.Of(TileModels.Defined(_read, one).Definition));
        bool[] walkable = grid.WalkableTileMask();
        var searched = new List<(string Room, RoomLayout Layout, RoomSearch Search)>(layouts.Count);
        foreach ((string file, RoomLayout layout) in layouts)
        {
            RoomSearch search = RoomFinder.Find(layout, ground, grid.TilesX, grid.TilesY, grid.Tiles, Identity, walkable: walkable);
            searched.Add((file, layout, search));
            Interlocked.Increment(ref done[0]);
        }

        return (searched, walkable);
    }
}
