namespace PoEformance.Game.World;

/// <summary>
/// How the game laid a terrain tile down: which axis of the tile's own template each axis of the
/// world reads from, and whether it runs backwards.
/// </summary>
/// <remarks>
/// ONE DECODE, used by the heights and by the tile book alike. A tile's
/// <c>RotationSelector</c> byte indexes the engine's selector table, which picks a triple out of the
/// helper table; the triple chooses, for a position inside the tile, which of four candidates is the
/// template's x and which its y:
///
///     candidates = [ N-1-x,  x,  N-1-y,  y ]
///     template x = candidates[FromX],  template y = candidates[FromY]
///
/// GameHelper2's GetSubTerrainHeight, which is right against the game: the heights it reads land on
/// the ground the game drew. That makes the decode a fact about the WORLD's grid - world cell (x, y)
/// inside a tile reads template cell (template x, template y) - and the mesh turn below is the same
/// fact inverted.
///
/// DEFAULT IS UNKNOWN, so a tile whose selector was never decoded can not pass for one laid as
/// authored. <see cref="Code"/> is 1 + FromX * 4 + FromY, with 0 for "no decode".
/// </remarks>
public readonly record struct TileOrientation
{
    private readonly byte _code;

    private TileOrientation(int fromX, int fromY) => _code = (byte)(1 + (fromX * 4) + fromY);

    /// <summary>The four candidates' order: 0 is the tile's x backwards, 1 its x, 2 its y backwards, 3 its y.</summary>
    public int FromX => (_code - 1) >> 2;

    /// <summary>See <see cref="FromX"/>.</summary>
    public int FromY => (_code - 1) & 3;

    /// <summary>True when the selector decoded to something the heights can be read through.</summary>
    public bool IsKnown => _code != 0;

    /// <summary>
    /// True when the two world axes read the template's two DIFFERENT axes - a turn, a mirror or both.
    /// </summary>
    /// <remarks>
    /// The tables only ever produce such a pair (the helper's first byte decides which axis x reads,
    /// and y is given the other), but a table read from the wrong place can hand anything over, and
    /// a pair reading one axis twice is not a placement a model can be turned into.
    /// </remarks>
    public bool IsPlacement => IsKnown && (FromX >> 1) != (FromY >> 1);

    /// <summary>
    /// Which of the eight placements this is, 0 to 7, or -1 when it is not one. The tile book's key carries this number.
    /// </summary>
    public int Placement => !IsPlacement ? -1 : FromX < 2 ? (FromX * 2) + (FromY - 2) : 4 + ((FromX - 2) * 2) + FromY;

    /// <summary>The placement numbered by <see cref="Placement"/>, or unknown outside 0 to 7.</summary>
    public static TileOrientation OfPlacement(int placement) => placement switch
    {
        >= 0 and < 4 => new TileOrientation(placement >> 1, 2 + (placement & 1)),
        >= 4 and < 8 => new TileOrientation(2 + ((placement - 4) >> 1), placement & 1),
        _ => default,
    };

    /// <summary>
    /// Decodes one selector through the two engine tables - a port of GameHelper2's GetSubTerrainHeight.
    /// </summary>
    /// <remarks>
    /// Every bound the reference checks is checked here and in the same place, so the heights read
    /// through this exactly as they did before it existed: a selector past the table reads entry 24,
    /// and a triple running off the helper table is no decode at all.
    /// </remarks>
    public static TileOrientation Decode(byte selector, ReadOnlySpan<byte> selectorTable, ReadOnlySpan<byte> helperTable)
    {
        int rotation = selector < selectorTable.Length ? selectorTable[selector] * 3 : 24;
        if (rotation > 24)
        {
            rotation = 24;
        }

        if (rotation + 2 >= helperTable.Length)
        {
            return default;
        }

        int rx0 = helperTable[rotation];
        int rx1 = helperTable[rotation + 1];
        int ry0 = helperTable[rotation + 2];
        int ry1 = rx0 == 0 ? 2 : 0;

        int ix = (rx0 * 2) + rx1;
        int iy = ry0 + ry1;
        return (uint)ix > 3 || (uint)iy > 3 ? default : new TileOrientation(ix, iy);
    }

    /// <summary>
    /// Every selector a tile can carry, decoded once - 256 entries, so a byte indexes it without a bound check.
    /// </summary>
    public static TileOrientation[] Table(ReadOnlySpan<byte> selectorTable, ReadOnlySpan<byte> helperTable)
    {
        var table = new TileOrientation[256];
        for (int selector = 0; selector < table.Length; selector++)
        {
            table[selector] = Decode((byte)selector, selectorTable, helperTable);
        }

        return table;
    }

    /// <summary>
    /// The template cell a world cell inside the tile reads, as an index into an array <paramref name="cells"/> wide, or -1.
    /// </summary>
    public int TemplateIndex(int inTileX, int inTileY, int cells)
        => !IsKnown ? -1 : (Candidate(FromY, inTileX, inTileY, cells) * cells) + Candidate(FromX, inTileX, inTileY, cells);

    private static int Candidate(int which, int x, int y, int cells) => which switch
    {
        0 => cells - x - 1,
        1 => x,
        2 => cells - y - 1,
        _ => y,
    };

    /// <summary>
    /// Where a point of the template lands in the world, about the tile's centre: X' = Xx*X + Xy*Y, Y' = Yx*X + Yy*Y.
    /// </summary>
    /// <remarks>
    /// THE DECODE INVERTED. It maps the world onto the template - template = A * world, with A's rows
    /// read straight off <see cref="FromX"/> and <see cref="FromY"/> - and a signed permutation's
    /// inverse is its transpose, so a template point goes to the world through Aᵀ.
    ///
    /// IN THE TEMPLATE'S OWN AXES, which are the world's: the decode is defined on world cells, and a
    /// tile laid as authored reads its template unchanged, so an unturned tile's mesh is the mesh as
    /// the file holds it. All zeros when this is not a placement.
    /// </remarks>
    public (int Xx, int Xy, int Yx, int Yy) Turn
    {
        get
        {
            if (!IsPlacement)
            {
                return default;
            }

            (int ax, int ay) = Row(FromX);
            (int bx, int by) = Row(FromY);
            return (ax, bx, ay, by);
        }
    }

    /// <summary>A's row for one candidate: how the template coordinate it picks depends on world x and y.</summary>
    private static (int X, int Y) Row(int which) => which switch
    {
        0 => (-1, 0),
        1 => (1, 0),
        2 => (0, -1),
        _ => (0, 1),
    };

    /// <summary>True when the placement turns the tile over - a mirror, with or without a turn.</summary>
    public bool Mirrored
    {
        get
        {
            (int xx, int xy, int yx, int yy) = Turn;
            return (xx * yy) - (xy * yx) < 0;
        }
    }

    /// <summary>
    /// How far the placement turns the tile, counter-clockwise in the file's own X and Y, after
    /// mirroring X where <see cref="Mirrored"/> says so: 0, 90, 180 or 270.
    /// </summary>
    /// <remarks>
    /// A mirror and a turn rather than eight separate names, because that is how the eight are made:
    /// every placement is a turn, or a turn of the tile with its X flipped first.
    /// </remarks>
    public int Degrees
    {
        get
        {
            (int xx, _, int yx, _) = Turn;
            if (Mirrored)
            {
                // Turn = R * F with F flipping X, so R's first column is minus the turn's.
                xx = -xx;
                yx = -yx;
            }

            return (xx, yx) switch
            {
                (0, 1) => 90,
                (-1, 0) => 180,
                (0, -1) => 270,
                _ => 0,
            };
        }
    }

    /// <summary>The placement in words: "as authored", "turned 90°", "mirrored, turned 270°" - or why there is none.</summary>
    public override string ToString()
    {
        if (!IsKnown)
        {
            return "orientation unknown";
        }

        if (!IsPlacement)
        {
            return $"not a placement (x from {FromX}, y from {FromY})";
        }

        int degrees = Degrees;
        return Mirrored
            ? degrees == 0 ? "mirrored" : $"mirrored, turned {degrees}°"
            : degrees == 0 ? "as authored" : $"turned {degrees}°";
    }
}
