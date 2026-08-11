using PoEformance.Game.Ui;

namespace PoEformance.Features;

/// <summary>Which measurement a heat map is showing.</summary>
public enum HeatOf
{
    /// <summary>Damage dealt. Where the fighting was.</summary>
    Dealt,

    /// <summary>Damage taken. Where it went badly.</summary>
    Taken,

    /// <summary>Seconds stood there. Where the time actually went.</summary>
    Time,
}

/// <summary>What happened on one patch of ground.</summary>
public readonly record struct HeatCell(long Dealt, long Taken, float Seconds)
{
    /// <summary>One of the three, by name.</summary>
    public float Of(HeatOf what) => what switch
    {
        HeatOf.Taken => Taken,
        HeatOf.Time => Seconds,
        _ => Dealt,
    };
}

/// <summary>
/// Where on the map things happened, so a finished run can be looked at as a picture.
/// </summary>
/// <remarks>
/// THE MAP IS ALREADY DRAWN, so this is the cheapest interesting thing that can go on it: the
/// damage meter already knows how much happened and the snapshot already knows where the
/// player was, and neither of them was writing the two down together. A total says a map was
/// hard; a picture says WHERE it was hard, and only the second one is worth acting on.
///
/// ANCHORED AT THE PLAYER, all three of them, which is a choice and not the only one available.
/// Damage dealt could just as well be filed where the MONSTER stood - that is literally where
/// it landed - and for a question about pack density it would be the better answer. It is
/// filed at the player because the question this is for is where the effort and the danger
/// were, the player's track is the through-line of both, and one rule for all three keeps them
/// comparable on the same picture. A dealt-heat drawn at monsters and a taken-heat drawn at the
/// player would be two maps of different things sharing a colour ramp.
///
/// SPARSE, not a grid. A map is a few thousand cells anybody actually stood on out of tens of
/// thousands the region holds, so a dictionary is smaller than the array and needs none of the
/// region-sizing machinery <see cref="MapCoverage"/> carries.
///
/// Written from the reader thread and read from the renderer, so it locks. Once per read
/// against once per frame.
/// </remarks>
public sealed class HeatMap
{
    /// <summary>
    /// How many grid cells across one patch is.
    /// </summary>
    /// <remarks>
    /// Twice the coverage grid's step, because heat wants BLOBS. A patch the size of a coverage
    /// cell is under a pixel on a zoomed-out map, so the picture would be a smear of noise; at
    /// this size a fight is a mark somebody can point at.
    /// </remarks>
    public const int Step = 8;

    /// <summary>Patches kept before the oldest area is forgotten.</summary>
    public const int MostPatches = 40_000;

    private readonly Lock _gate = new();
    private readonly Dictionary<(uint Area, int X, int Y), HeatCell> _cells = [];

    /// <summary>How many patches are held, across every area.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _cells.Count;
            }
        }
    }

    /// <summary>Turns a world position into the patch it falls on.</summary>
    public static (int X, int Y) PatchOf(float worldX, float worldY)
        => ((int)MathF.Floor(worldX / MapView.WorldToGrid / Step),
            (int)MathF.Floor(worldY / MapView.WorldToGrid / Step));

    /// <summary>The world position of a patch's near corner, for drawing it.</summary>
    public static (float X, float Y) WorldOf(int patchX, int patchY)
        => (patchX * Step * MapView.WorldToGrid, patchY * Step * MapView.WorldToGrid);

    /// <summary>
    /// How many patches a step may be spread across before it is treated as a jump.
    /// </summary>
    /// <remarks>
    /// A portal, a loading screen or a dash puts the player somewhere they did not walk, and
    /// painting the line between would be a stripe across the map along ground nobody crossed.
    /// Sixteen patches is far more than a thirtieth of a second of running and far less than
    /// any teleport.
    /// </remarks>
    public const int MostStepsAcross = 16;

    /// <summary>
    /// Records what happened along the step the player just took.
    /// </summary>
    /// <remarks>
    /// ALONG THE STEP, not at the destination. Reads land thirty times a second and a running
    /// player crosses more than one patch between two of them, so filing everything at the
    /// point of the read leaves a DOTTED line - patches with gaps between them that the player
    /// walked straight through. That is what made the first picture look sparse: not a lack of
    /// data, a lack of the ground between the samples.
    ///
    /// The amounts are split evenly across the patches crossed. Whether the damage really
    /// happened at the start or the end of a thirtieth of a second is not a thing this can
    /// know, and pretending otherwise by putting all of it on one end would be a sharper
    /// picture of something invented.
    ///
    /// Takes the amounts rather than reading them, because the only place that knows how much
    /// happened in ONE read is the meter that just worked it out - a running total handed over
    /// here would have to be differenced twice.
    /// </remarks>
    public void Add(
        uint area, float fromX, float fromY, float toX, float toY, long dealt, long taken, float seconds)
    {
        if (area == 0 || (dealt <= 0 && taken <= 0 && seconds <= 0f))
        {
            return;
        }

        (int firstX, int firstY) = PatchOf(fromX, fromY);
        (int lastX, int lastY) = PatchOf(toX, toY);

        int across = Math.Max(Math.Abs(lastX - firstX), Math.Abs(lastY - firstY));
        if (across is <= 0 or > MostStepsAcross)
        {
            // One patch, or a jump. Either way the destination is the only place known to be
            // true, so it takes the whole of it.
            One(area, lastX, lastY, dealt, taken, seconds);
            return;
        }

        // Every patch the step passed through, the ends included. Whole numbers rather than a
        // line-drawing algorithm: at this size the difference is a patch's corner, and a corner
        // is smaller than the blur a heat map is drawn with anyway.
        int steps = across + 1;
        for (int i = 0; i < steps; i++)
        {
            float along = (float)i / across;
            One(
                area,
                (int)MathF.Round(firstX + ((lastX - firstX) * along)),
                (int)MathF.Round(firstY + ((lastY - firstY) * along)),
                dealt / steps,
                taken / steps,
                seconds / steps);
        }
    }

    /// <summary>Records against one place, for a caller that took no step.</summary>
    public void Add(uint area, float worldX, float worldY, long dealt, long taken, float seconds)
        => Add(area, worldX, worldY, worldX, worldY, dealt, taken, seconds);

    /// <summary>Records against one patch.</summary>
    private void One(uint area, int x, int y, long dealt, long taken, float seconds)
    {
        lock (_gate)
        {
            // A cap rather than an eviction policy: heat is only ever read for the area being
            // looked at, so the useful thing to protect is that a long session cannot grow
            // without end. Starting over is cheap and obvious; a half-forgotten map is not.
            if (_cells.Count >= MostPatches && !_cells.ContainsKey((area, x, y)))
            {
                return;
            }

            _cells.TryGetValue((area, x, y), out HeatCell was);
            _cells[(area, x, y)] = new HeatCell(
                was.Dealt + Math.Max(0, dealt),
                was.Taken + Math.Max(0, taken),
                was.Seconds + MathF.Max(0f, seconds));
        }
    }

    /// <summary>Forgets everything.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _cells.Clear();
        }
    }

    /// <summary>Every patch of one area, with its position.</summary>
    public IReadOnlyList<(int X, int Y, HeatCell Cell)> In(uint area)
    {
        var mine = new List<(int, int, HeatCell)>();

        lock (_gate)
        {
            foreach (((uint Area, int X, int Y) at, HeatCell cell) in _cells)
            {
                if (at.Area == area)
                {
                    mine.Add((at.X, at.Y, cell));
                }
            }
        }

        return mine;
    }

    /// <summary>
    /// What counts as fully hot in an area - the scale the picture is drawn against.
    /// </summary>
    /// <remarks>
    /// THE NINETY-FIFTH rather than the highest, for the same reason the read-cost summary
    /// uses it: one boss standing on one patch is ten times anything else on the map, and
    /// scaled against that every other fight is the same shade of nothing. Cutting the top off
    /// means the busiest few patches are all equally hot, which is true enough - they are all
    /// "the most" - and everything below them is told apart.
    /// </remarks>
    public float Hottest(uint area, HeatOf what)
    {
        var values = new List<float>();
        foreach ((int _, int _, HeatCell cell) in In(area))
        {
            float value = cell.Of(what);
            if (value > 0f)
            {
                values.Add(value);
            }
        }

        if (values.Count == 0)
        {
            return 0f;
        }

        values.Sort();
        return values[Math.Min(values.Count - 1, (int)(values.Count * 0.95))];
    }
}
