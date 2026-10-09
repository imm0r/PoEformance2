using System.Collections.Concurrent;
using PoEformance.Game.Files;
using PoEformance.Game.World;

namespace PoEformance.Features;

/// <summary>
/// Every room the current area loaded: searched for off the frame by its ground and tiles, and placed by its doodads once the area's entities are read - for the large map to outline all at once.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS. The tile book finds one room at a time and outlines the row a person picks; with
/// twenty rooms in an area that is twenty picks to see the layout. Asked for: every room drawn at once,
/// where it stands.
///
/// TWO ANSWERS, AND WHICH ONE THE MAP TAKES. The ground-and-tile search (RoomFinder) ranks places by
/// how well a room's slots agree with what the area laid, and RoomArrangement gives each room one place
/// no surer room holds. That was the first answer and it is kept for the tile book, where a person
/// reads a room's list; but it placed a floor module in the black beside the boss arena, because its
/// best places lay inside the arena and the rule moved it, and it could never draw a wall module that
/// the area lays four times. The second answer asks the area's entities: each doodad line of a room is
/// an entity standing where the line put it, and RoomDoodadFinder finds every place the room's
/// doodads stand. The map draws that wherever the entities can be read - see <see cref="Placed"/> -
/// and the arrangement only where they cannot.
///
/// ONE SEARCH AT A TIME, ON ONE TASK. Each tries its room eight ways round at every tile corner of
/// the area - cheap per try, not per area - and twenty of them at once would take every core from the
/// game for the seconds they run. In turn they take one. The doodad survey follows on its own task
/// once the rooms are read, since it needs their files for the stubs to look for; it is one walk of
/// two entity maps, measured at 47 ms for an area of 1441 entities.
///
/// AGAIN ONLY WHEN THE AREA OR ITS ROOMS CHANGE: the grid by reference, as everywhere, and the rooms
/// by their count - the loaded-file list only ever grows within an area, and a room arriving late is
/// a new arrangement and a new survey. The searches are kept, so a change of RoomOverlap rule
/// arranges them again on the spot.
/// </remarks>
public sealed class AreaRooms
{
    private readonly Func<string, byte[]?> _read;
    private TerrainGrid? _grid;
    private int _rooms = -1;
    private Task<(List<(string Room, RoomLayout Layout, RoomSearch Search)> Rooms, bool[] Walkable)>? _running;
    private List<(string Room, RoomLayout Layout, RoomSearch Search)>? _searched;

    /// <summary>The walkable mask the searches ran with, kept so the arrangement counts each room's standing by the same tiles the search kept it for.</summary>
    private bool[]? _walkable;
    private RoomArrangement? _arranged;

    /// <summary>The stubs the area's rooms name, made once per search - see <see cref="Stubs"/>.</summary>
    private IReadOnlySet<string>? _stubs;
    private List<(string Room, RoomLayout Layout, RoomSearch Search)>? _stubsOf;

    /// <summary>The survey of the area's entity maps for those stubs and the places it gives each room, running or run, and the answer once taken - see <see cref="Survey"/>.</summary>
    private Task<(DoodadSurvey Survey, List<(string Room, RoomLayout Layout, RoomDoodadPlaces Places)> Placed)>? _survey;
    private DoodadSurvey? _surveyed;
    private List<(string Room, RoomLayout Layout, RoomDoodadPlaces Places)>? _placed;

    /// <summary>The running search's count of rooms done - one array per search, so one left to run out cannot count into the next.</summary>
    private int[] _done = [0];
    private int _of;

    /// <param name="read">How to read a file out of the install - each room's own, and the tiles' definitions.</param>
    public AreaRooms(Func<string, byte[]?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        _read = read;
    }

    /// <summary>
    /// Reads the area's entity maps once for the stubs given - see SleepingDoodads - set by whoever owns the game's memory. Null where nothing can, and the map falls back to the arrangement.
    /// </summary>
    public Func<IReadOnlySet<string>, DoodadSurvey>? ReadDoodads { get; set; }

    /// <summary>How many of the area's rooms have been searched, and of how many - for a line saying it is under way.</summary>
    public (int Done, int Of) Progress => (Volatile.Read(ref _done[0]), Volatile.Read(ref _of));

    /// <summary>Whether a search is under way.</summary>
    public bool Running => _running is { IsCompleted: false };

    /// <summary>The last arrangement taken, or null while none is - for a line saying what was drawn.</summary>
    public RoomArrangement? Last => _arranged;

    /// <summary>The rooms searched, each with its file read and its search's answer - null while the search runs.</summary>
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
        if (_searched is not { } searched)
        {
            return null;
        }

        if (!ReferenceEquals(searched, _stubsOf))
        {
            var stubs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach ((_, RoomLayout layout, _) in searched)
            {
                foreach (RoomDoodad doodad in layout.Doodads)
                {
                    if (doodad.Stub.Length > 0)
                    {
                        stubs.Add(doodad.Stub);
                    }
                }
            }

            _stubsOf = searched;
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
        if (ReadDoodads is not { } read || Surveying || _searched is not { } searched || Stubs() is not { } stubs || _grid is not { } grid)
        {
            return false;
        }

        int tilesX = grid.TilesX;
        int tilesY = grid.TilesY;
        _surveyed = null;
        _placed = null;
        _survey = Task.Run(() =>
        {
            DoodadSurvey survey = read(stubs);
            var found = new List<(string Room, RoomDoodadPlaces Places)>(searched.Count);
            foreach ((string room, RoomLayout layout, RoomSearch search) in searched)
            {
                RoomDoodadPlaces places = RoomDoodadFinder.Find(layout.Doodads, layout.Width, layout.Height, survey.Found, tilesX, tilesY);
                found.Add((room, places with { Places = Tiled(places.Places, search) }));
            }

            // ONE ROOM PER PLACE: the variants of a room all vote for the tile where the laid one stands.
            List<(string Room, RoomDoodadPlaces Places)> settled = RoomDoodadFinder.Settle(found, tilesX, tilesY);
            var placed = new List<(string Room, RoomLayout Layout, RoomDoodadPlaces Places)>(searched.Count);
            for (var one = 0; one < searched.Count; one++)
            {
                placed.Add((searched[one].Room, searched[one].Layout, settled[one].Places));
            }

            return (survey, placed);
        });
        return true;
    }

    /// <summary>
    /// The area's rooms arranged, or null while they are being searched for - starting the search where this area or its rooms are new, and the doodad survey once the search is done. Called every frame; cheap when nothing changed.
    /// </summary>
    /// <param name="grid">The current area, or null where none is read.</param>
    /// <param name="rooms">The rooms the area loaded, by file.</param>
    /// <param name="rule">Which tiles two rooms may both hold.</param>
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
            _searched = null;
            _walkable = null;
            _arranged = null;

            // A SURVEY IS THE AREA'S: one running for the old area runs out and is dropped like the search.
            _survey = null;
            _surveyed = null;
            _placed = null;
            int[] done = [0];
            _done = done;
            Volatile.Write(ref _of, rooms.Count);
            string[] files = [.. rooms];
            _running = Task.Run(() => Search(files, grid, ground, done));
        }

        if (_searched is null && _running is { IsCompleted: true } finished)
        {
            (_searched, _walkable) = finished.IsCompletedSuccessfully ? finished.Result : ([], null);

            // THE DOODADS NEXT, without being asked: the map is drawn from them wherever they can be read.
            Survey();
        }

        if (_searched is not null && (_arranged is null || _arranged.Rule != rule))
        {
            _arranged = RoomArrangement.Arrange(_searched, grid.TilesX, grid.TilesY, rule, _walkable);
        }

        return _arranged;
    }

    /// <summary>
    /// Each place with the tiles the ground-and-tile search found agreeing at that very place, where it listed it - the tie-breaker between variants that share every doodad, see RoomDoodadFinder.Settle.
    /// </summary>
    private static List<RoomDoodadPlace> Tiled(IReadOnlyList<RoomDoodadPlace> places, RoomSearch search)
    {
        var tiled = new List<RoomDoodadPlace>(places.Count);
        foreach (RoomDoodadPlace place in places)
        {
            int agree = -1;
            foreach (RoomCandidate candidate in search.Candidates)
            {
                if (candidate.X == place.Where.X && candidate.Y == place.Where.Y && candidate.Turn == place.Where.Turn)
                {
                    agree = candidate.TilesAgree;
                    break;
                }
            }

            tiled.Add(place with { TilesAgree = agree });
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
                (_surveyed, _placed) = done.Result;
            }
            else
            {
                _surveyed = DoodadSurvey.Not("the read failed: " + (done.Exception?.GetBaseException().Message ?? "cancelled"));
                _placed = [];
            }
        }
    }

    /// <summary>Every room searched in turn, and the walkable mask they were searched with.</summary>
    private (List<(string Room, RoomLayout Layout, RoomSearch Search)> Rooms, bool[] Walkable) Search(string[] files, TerrainGrid grid, TerrainGroundTypes ground, int[] done)
    {
        // EACH TILE FILE ONCE across every room - the same cache the tile book keeps per area.
        var known = new ConcurrentDictionary<string, TileIdentity?>(StringComparer.OrdinalIgnoreCase);
        TileIdentity? Identity(string path) => known.GetOrAdd(path, one => TileIdentity.Of(TileModels.Defined(_read, one).Definition));
        bool[] walkable = grid.WalkableTileMask();
        var searched = new List<(string Room, RoomLayout Layout, RoomSearch Search)>(files.Length);
        foreach (string file in files)
        {
            RoomLayout layout = RoomLayout.Read(_read(file));
            RoomSearch search = RoomFinder.Find(layout, ground, grid.TilesX, grid.TilesY, grid.Tiles, Identity, walkable: walkable);
            searched.Add((file, layout, search));
            Interlocked.Increment(ref done[0]);
        }

        return (searched, walkable);
    }
}
