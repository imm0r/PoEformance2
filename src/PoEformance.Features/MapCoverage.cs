using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Features;

/// <summary>
/// How much of a map has been walked - and, more usefully, how much is left.
/// </summary>
/// <remarks>
/// MEASURED AGAINST WHAT CAN BE REACHED, not against every walkable cell, and that is the
/// whole difficulty. Path of Exile 2's walkable grid holds a great deal of ground nobody can
/// get to: scenery floors, the far side of walls, and on a multi-level map every storey
/// flattened into the same plane. Divide by all of it and a finished map reads as a few per
/// cent - which is what the AutoHotkey tool shipped first, and what sent it looking for a bug
/// in the marking rather than in the denominator.
///
/// So the denominator is a flood fill from where the player started. It runs ONCE per area, on
/// a background thread rather than sliced across frames - the reference had to slice it
/// because AutoHotkey has one thread, and porting that machinery would be porting the
/// workaround. Until it finishes the percentage is computed against every walkable cell, which
/// under-reports rather than over-reports: a number that starts low and jumps up is better
/// than one that starts at ninety and falls.
///
/// A COARSE grid, four terrain cells to a side. The question is "have I been down there",
/// which a four-cell square answers exactly as well as a single cell while making the buffers
/// sixteen times smaller and the flood sixteen times faster.
/// </remarks>
public sealed class MapCoverage
{
    /// <summary>Terrain cells to a side of one coarse cell.</summary>
    public const int CoarseStep = 4;

    /// <summary>
    /// How far around the player counts as seen, in coarse cells.
    /// </summary>
    /// <remarks>
    /// Roughly what fits on screen. Marking only the cell underfoot would need every corridor
    /// walked down its middle to read as covered, and nobody plays that way - a map cleared
    /// normally would sit at forty per cent forever.
    /// </remarks>
    public const int SeeRadius = 22;

    /// <summary>Most areas remembered, so a long session cannot grow without bound.</summary>
    public const int MaxRemembered = 24;

    /// <summary>Runs the flood on a background thread. The default.</summary>
    public static Action<Action> Background { get; } = work => _ = Task.Run(work);

    /// <summary>Runs the flood right now. For tests, which cannot wait on a thread.</summary>
    public static Action<Action> Immediate { get; } = work => work();

    private readonly Action<Action> _schedule;
    private readonly Dictionary<uint, Area> _remembered = [];
    private readonly List<uint> _order = [];
    private Area? _here;

    public MapCoverage(Action<Action>? schedule = null) => _schedule = schedule ?? Background;

    /// <summary>How much of what can be reached has been seen, 0 to 100.</summary>
    public float Percent => _here?.Percent ?? 0f;

    /// <summary>How many coarse cells of the reachable region have been seen.</summary>
    /// <remarks>
    /// Exposed alongside the percentage because a fraction reads better than a number on its
    /// own - and because the percentage is CLAMPED, which would otherwise hide a numerator
    /// that has grown past its denominator. That is not hypothetical: the sight disc reaches
    /// through walls into ground the player cannot get to, so a numerator counted without the
    /// region filter really does overrun.
    /// </remarks>
    public int SeenCells => _here?.Seen ?? 0;

    /// <summary>How many coarse cells can be reached at all. The denominator.</summary>
    public int ReachableCells => _here?.Reachable ?? 0;

    /// <summary>Coarse cells across the area being measured.</summary>
    public int CoarseWidth => _here?.Width ?? 0;

    /// <summary>Coarse cells down the area being measured.</summary>
    public int CoarseHeight => _here?.Height ?? 0;

    /// <summary>
    /// Whether a coarse cell can be reached and has not been walked past yet.
    /// </summary>
    /// <remarks>
    /// For drawing where there is still ground to cover, which is the useful half of this -
    /// "forty per cent left" and "left is over THERE" are different amounts of help.
    ///
    /// Read from whichever thread is drawing while the reader marks cells. Nothing needs
    /// locking: a bool write cannot tear, so the worst case is a cell that shows as unwalked
    /// for one more frame than it should. False until the region is known, so nothing is drawn
    /// while the answer would be against the whole grid - marks covering ground nobody can get
    /// to would be exactly the wrong advice.
    /// </remarks>
    public bool StillToWalk(int coarseX, int coarseY) => _here?.StillToWalk(coarseX, coarseY) ?? false;

    /// <summary>Whether the reachable region has been worked out yet.</summary>
    /// <remarks>
    /// Worth saying out loud, because the percentage means something different before and
    /// after: an under-report against the whole grid, then the real figure.
    /// </remarks>
    public bool RegionKnown => _here?.Region is not null;

    /// <summary>Whether there is an area being measured at all.</summary>
    public bool Measuring => _here is not null;

    /// <summary>How many areas are being remembered.</summary>
    public int Remembered => _remembered.Count;

    /// <summary>Takes one snapshot and updates the figure.</summary>
    public void Look(WorldSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.Terrain is not TerrainGrid grid || snapshot.Player is not WorldEntity player)
        {
            return;
        }

        var at = (
            X: (int)(player.WorldX / MapView.WorldToGrid) / CoarseStep,
            Y: (int)(player.WorldY / MapView.WorldToGrid) / CoarseStep);

        Area area = Switch(snapshot.AreaHash, grid, at);
        area.Saw(at);
    }

    /// <summary>Puts everything back, as if the session had just started.</summary>
    public void Forget()
    {
        _remembered.Clear();
        _order.Clear();
        _here = null;
    }

    /// <summary>
    /// Moves to the area being measured, resuming a known one.
    /// </summary>
    /// <remarks>
    /// Resuming matters more than it sounds. Leaving a half-explored map to sell in town and
    /// coming back is ordinary play, and a figure that resets to zero on the way back is one
    /// nobody trusts afterwards. Keyed by the instance hash, so two runs of the SAME map
    /// layout do not share each other's progress.
    /// </remarks>
    private Area Switch(uint hash, TerrainGrid grid, (int X, int Y) from)
    {
        if (_here is Area current && current.Hash == hash)
        {
            return current;
        }

        if (_remembered.TryGetValue(hash, out Area? known) && known.Fits(grid))
        {
            _here = known;
            return known;
        }

        var fresh = new Area(hash, grid);
        _remembered[hash] = fresh;
        _order.Remove(hash);
        _order.Add(hash);

        while (_order.Count > MaxRemembered)
        {
            _remembered.Remove(_order[0]);
            _order.RemoveAt(0);
        }

        _here = fresh;
        _schedule(() => fresh.WorkOutWhatIsReachable(from));
        return fresh;
    }

    /// <summary>One area's visited map and the region it is measured against.</summary>
    private sealed class Area
    {
        private readonly TerrainGrid _grid;
        private readonly bool[] _seen;
        private readonly int _width;
        private readonly int _height;
        private readonly int _walkable;

        private int _seenCount;
        private int _seenInRegion;

        // The flood hands its result over HERE, and the reader thread picks it up on its next
        // look. Everything that mutates then happens on that one thread, so the running count
        // below needs no locking and cannot be raced into disagreeing with the array it counts.
        private volatile bool[]? _handover;
        private int _handoverSize;

        // Adopted from the handover, and read by whoever is drawing. A reference assignment is
        // atomic, so a reader sees no region or a finished one - never a half-built one, which
        // would report against a denominator that is still growing.
        private volatile bool[]? _region;
        private int _regionSize;

        internal Area(uint hash, TerrainGrid grid)
        {
            Hash = hash;
            _grid = grid;
            _width = Math.Max(1, grid.Width / CoarseStep);
            _height = Math.Max(1, grid.Height / CoarseStep);
            _seen = new bool[_width * _height];

            for (int y = 0; y < _height; y++)
            {
                for (int x = 0; x < _width; x++)
                {
                    if (Walkable(x, y))
                    {
                        _walkable++;
                    }
                }
            }
        }

        internal uint Hash { get; }

        internal bool[]? Region => _region;

        internal int Width => _width;

        internal int Height => _height;

        /// <summary>Reachable, and not walked past yet.</summary>
        internal bool StillToWalk(int x, int y)
        {
            bool[]? region = _region;
            if (region is null || (uint)x >= (uint)_width || (uint)y >= (uint)_height)
            {
                return false;
            }

            int index = (y * _width) + x;
            return region[index] && !_seen[index];
        }

        /// <summary>Whether this belongs to the grid on offer - a size change means a new area.</summary>
        internal bool Fits(TerrainGrid grid)
            => grid.Width / CoarseStep == _width && grid.Height / CoarseStep == _height;

        /// <summary>
        /// Cells of the region that have been seen.
        /// </summary>
        /// <remarks>
        /// A running count rather than a walk of the grid. Whoever is drawing asks for this
        /// several times a frame, and a coarse grid on a large map runs to a hundred thousand
        /// cells - counting them on each ask is millions of iterations a second on the thread
        /// that can least afford them.
        /// </remarks>
        internal int Seen => _region is null ? _seenCount : _seenInRegion;

        /// <summary>Cells that can be reached - the whole grid until the flood has run.</summary>
        internal int Reachable => _region is null ? _walkable : _regionSize;

        internal float Percent
        {
            get
            {
                int total = Reachable;
                return total <= 0 ? 0f : Math.Clamp(Seen * 100f / total, 0f, 100f);
            }
        }

        /// <summary>Marks everything within sight of a coarse cell.</summary>
        internal void Saw((int X, int Y) at)
        {
            TakeTheRegionIfItIsReady();

            bool[]? region = _region;

            for (int dy = -SeeRadius; dy <= SeeRadius; dy++)
            {
                for (int dx = -SeeRadius; dx <= SeeRadius; dx++)
                {
                    if ((dx * dx) + (dy * dy) > SeeRadius * SeeRadius)
                    {
                        continue;
                    }

                    int x = at.X + dx;
                    int y = at.Y + dy;
                    if ((uint)x >= (uint)_width || (uint)y >= (uint)_height)
                    {
                        continue;
                    }

                    int index = (y * _width) + x;
                    if (!_seen[index] && Walkable(x, y))
                    {
                        _seen[index] = true;
                        _seenCount++;

                        if (region is not null && region[index])
                        {
                            _seenInRegion++;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Adopts a finished flood, counting what was already seen inside it.
        /// </summary>
        /// <remarks>
        /// On the reader's thread, which is the point: the flood runs elsewhere and only hands
        /// its array over, so every change to the running count happens on ONE thread and the
        /// count cannot drift away from the array it is counting.
        /// </remarks>
        private void TakeTheRegionIfItIsReady()
        {
            bool[]? arrived = _handover;
            if (arrived is null)
            {
                return;
            }

            _handover = null;
            _regionSize = _handoverSize;
            _seenInRegion = CountSeenInRegion(arrived);
            _region = arrived;
        }

        /// <summary>
        /// Floods out from where the player came in, to find what is actually reachable.
        /// </summary>
        /// <remarks>
        /// The denominator, and the reason the figure means anything. Runs once, off the
        /// thread that is drawing, and publishes by assigning the finished array - so a
        /// reader sees the old denominator or the new one, never a growing one.
        /// </remarks>
        internal void WorkOutWhatIsReachable((int X, int Y) from)
        {
            if (!TryStandOn(from, out (int X, int Y) start))
            {
                return;   // nowhere to start from; the whole-grid figure stands
            }

            var reached = new bool[_width * _height];
            var queue = new Queue<(int X, int Y)>();
            reached[(start.Y * _width) + start.X] = true;
            queue.Enqueue(start);
            int count = 1;

            while (queue.Count > 0)
            {
                (int x, int y) = queue.Dequeue();

                foreach ((int dx, int dy) in Steps)
                {
                    int nx = x + dx;
                    int ny = y + dy;
                    if ((uint)nx >= (uint)_width || (uint)ny >= (uint)_height)
                    {
                        continue;
                    }

                    int index = (ny * _width) + nx;
                    if (reached[index] || !Walkable(nx, ny))
                    {
                        continue;
                    }

                    reached[index] = true;
                    count++;
                    queue.Enqueue((nx, ny));
                }
            }

            // Handed over rather than installed. The reader thread adopts it on its next look
            // and counts what was seen while the flood was running - see TakeTheRegionIfItIsReady.
            _handoverSize = count;
            _handover = reached;
        }

        private static readonly (int X, int Y)[] Steps = [(1, 0), (-1, 0), (0, 1), (0, -1)];

        private int CountSeenInRegion(bool[] region)
        {
            int seen = 0;
            for (int i = 0; i < _seen.Length; i++)
            {
                if (_seen[i] && region[i])
                {
                    seen++;
                }
            }

            return seen;
        }

        /// <summary>
        /// Whether a coarse cell can be stood on - true when ANY of its terrain cells can.
        /// </summary>
        /// <remarks>
        /// Any rather than all, so a corridor narrower than four cells is still ground. All
        /// would erase every thin passage in the area, and thin passages are most of a map.
        /// </remarks>
        private bool Walkable(int x, int y)
        {
            int baseX = x * CoarseStep;
            int baseY = y * CoarseStep;

            for (int dy = 0; dy < CoarseStep; dy++)
            {
                for (int dx = 0; dx < CoarseStep; dx++)
                {
                    if (_grid.IsWalkable(baseX + dx, baseY + dy))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>The nearest walkable coarse cell, so a start just inside a wall still works.</summary>
        private bool TryStandOn((int X, int Y) from, out (int X, int Y) found)
        {
            for (int radius = 0; radius <= 12; radius++)
            {
                for (int dy = -radius; dy <= radius; dy++)
                {
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != radius)
                        {
                            continue;   // the ring only, so the first hit is the nearest
                        }

                        int x = from.X + dx;
                        int y = from.Y + dy;
                        if ((uint)x < (uint)_width && (uint)y < (uint)_height && Walkable(x, y))
                        {
                            found = (x, y);
                            return true;
                        }
                    }
                }
            }

            found = default;
            return false;
        }
    }
}
