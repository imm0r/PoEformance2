using System.Collections.Concurrent;
using PoEformance.Game.Files;
using PoEformance.Game.World;

namespace PoEformance.Features;

/// <summary>
/// Every room the current area loaded, searched for off the frame and arranged so no two share a tile - for the large map to outline all at once.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS. The tile book finds one room at a time and outlines the row a person picks; with
/// twenty rooms in an area that is twenty picks to see the layout. Asked for: every room drawn at once,
/// taking each search's first row as right for now, and no two rooms on top of one another - which is
/// RoomArrangement's to settle.
///
/// ONE SEARCH AT A TIME, ON ONE TASK. Each tries its room eight ways round at every tile corner of
/// the area - cheap per try, not per area - and twenty of them at once would take every core from the
/// game for the seconds they run. In turn they take one, and the map fills in when the last is done.
/// The searches are the tile book's own (RoomFinder.Find with the same walkable mask), so a room
/// drawn here sits where the first row of its list puts it, until another room is surer of that spot.
///
/// AGAIN ONLY WHEN THE AREA OR ITS ROOMS CHANGE: the grid by reference, as everywhere, and the rooms
/// by their count - the loaded-file list only ever grows within an area, and a room arriving late is
/// a new arrangement. The searches are kept, so a change of RoomOverlap rule arranges them again on
/// the spot: placing the rooms down their lists only looks up the tiles each place covers, where the
/// searches took seconds.
/// </remarks>
public sealed class AreaRooms
{
    private readonly Func<string, byte[]?> _read;
    private TerrainGrid? _grid;
    private int _rooms = -1;
    private Task<List<(string Room, RoomLayout Layout, RoomSearch Search)>>? _running;
    private List<(string Room, RoomLayout Layout, RoomSearch Search)>? _searched;
    private RoomArrangement? _arranged;

    /// <summary>The running search's count of rooms done - one array per search, so one left to run out cannot count into the next.</summary>
    private int[] _done = [0];
    private int _of;

    /// <param name="read">How to read a file out of the install - each room's own, and the tiles' definitions.</param>
    public AreaRooms(Func<string, byte[]?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        _read = read;
    }

    /// <summary>How many of the area's rooms have been searched, and of how many - for a line saying it is under way.</summary>
    public (int Done, int Of) Progress => (Volatile.Read(ref _done[0]), Volatile.Read(ref _of));

    /// <summary>Whether a search is under way.</summary>
    public bool Running => _running is { IsCompleted: false };

    /// <summary>The last arrangement taken, or null while none is - for a line saying what was drawn.</summary>
    public RoomArrangement? Last => _arranged;

    /// <summary>
    /// The area's rooms arranged, or null while they are being searched for - starting the search where this area or its rooms are new. Called every frame; cheap when nothing changed.
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
            _arranged = null;
            int[] done = [0];
            _done = done;
            Volatile.Write(ref _of, rooms.Count);
            string[] files = [.. rooms];
            _running = Task.Run(() => Search(files, grid, ground, done));
        }

        if (_searched is null && _running is { IsCompleted: true } finished)
        {
            _searched = finished.IsCompletedSuccessfully ? finished.Result : [];
        }

        if (_searched is not null && (_arranged is null || _arranged.Rule != rule))
        {
            _arranged = RoomArrangement.Arrange(_searched, grid.TilesX, grid.TilesY, rule);
        }

        return _arranged;
    }

    /// <summary>Every room searched in turn.</summary>
    private List<(string Room, RoomLayout Layout, RoomSearch Search)> Search(string[] files, TerrainGrid grid, TerrainGroundTypes ground, int[] done)
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

        return searched;
    }
}
