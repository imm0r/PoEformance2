using System.Buffers.Binary;
using System.Numerics;

namespace PoEformance.Game.Files;

/// <summary>
/// A prop's geometry, out of the game's own <c>.fmt</c> files.
/// </summary>
/// <remarks>
/// THE OTHER HALF OF WHAT A MONSTER IS MADE OF, and the half this tool could not see. An .ao
/// hangs two kinds of thing off a bone: a <c>SkinMesh</c>, which names a .sm and then a .smd, and
/// a <c>FixedMesh</c>, which names a .fmt and nothing else. Nothing here read the second kind, so
/// every one of them came back from the model walk as "no SkinMesh - an effect or a sound" and
/// was left out. Reported from the live client: Malgor, the Nautilord carries a cannon on his
/// shoulder that the pane never drew, and his .ao says why in one line -
/// <c>FixedMesh { fixed_mesh = "…/PirateBossCannon.fmt" }</c>.
///
/// A FIXED MESH IS NOT SKINNED, which is what makes it a different file and an easier one: no
/// bones, no weights, no rig, no manifest beside it. It is a rigid prop that sits where its
/// socket puts it, and it CARRIES ITS OWN MATERIALS - one .mat path per shape, in a string pool
/// at the end of the file - so there is no .sm to look them up in and none is read.
///
/// VERSION 9 WRAPS THE SAME DOLm BLOCK A .smd DOES, which is why this file is short: the
/// geometry is <see cref="SkinnedMesh.Block"/>'s, shared rather than copied, so the four-byte
/// error that cost this project a day stays fixed in one place. Versions below nine put the
/// vertices in a layout of their own; they are refused by name rather than read by guess.
///
/// THE LAYOUT comes from poe_data_tools' fmt parser. What the reader checks for itself is the
/// string pool: every shape's name and material are OFFSETS into it, so a walk that arrived at
/// the pool one step out lands those offsets in the middle of words or past the end - which is
/// visible, cheap to test, and exactly the failure mode that hid in the .smd reader for months.
/// </remarks>
public sealed class FixedMesh
{
    /// <summary>Nothing read - a missing file, or one that is not a fixed mesh.</summary>
    public static FixedMesh None { get; } = new() { Why = "nothing to read" };

    /// <summary>The geometry, with one named shape per entry of <see cref="Named"/>.</summary>
    public SkinnedMesh Mesh { get; private init; } = SkinnedMesh.None;

    /// <summary>
    /// Each shape's name and the <c>.mat</c> the file puts on it, in the mesh's shape order.
    /// </summary>
    /// <remarks>
    /// THE SAME SHAPE THE .ao's OWN MATERIAL LINES TAKE, deliberately: the model walk already
    /// matches a shape to a material by name through that list, so a fixed mesh's materials go
    /// down the path that is there rather than a second one beside it.
    /// </remarks>
    public IReadOnlyList<(string Shape, string Material)> Named { get; private init; } = [];

    /// <summary>Why nothing was read, or empty where something was.</summary>
    public string Why { get; private init; } = string.Empty;

    /// <summary>Whether there is anything to draw.</summary>
    public bool Ready => Mesh.Ready;

    /// <summary>Reads one out of an open install, by the path a <c>FixedMesh</c> block named.</summary>
    public static FixedMesh Read(GameFiles? files, string? path)
        => files is null || string.IsNullOrWhiteSpace(path)
            ? Failed("no install, or no path")
            : Read(files.Read(path.Replace('\\', '/').Trim()));

    /// <summary>
    /// Reads a file's bytes. A file this cannot make sense of comes back with a reason, not a throw.
    /// </summary>
    /// <remarks>
    /// NEVER THROWS, for the same reason <see cref="SkinnedMesh.Read(byte[])"/> does not: the
    /// caller is a window drawing a monster somebody clicked on, and a prop that will not read is
    /// a prop that does not appear rather than a tool that closes.
    /// </remarks>
    public static FixedMesh Read(byte[]? content)
    {
        if (content is not { Length: > HeaderBytes })
        {
            return Failed("nothing to read");
        }

        try
        {
            return Parse(content);
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException
            or IndexOutOfRangeException or OverflowException)
        {
            return Failed($"the file does not read as a fixed mesh: {exception.Message}");
        }
    }

    /// <summary>Version, counts and a box - the smallest a file can be and still have a header.</summary>
    private const int HeaderBytes = 1 + 6 + 24;

    /// <summary>
    /// How wide one of the trailing records is, by version.
    /// </summary>
    /// <remarks>
    /// NOT READ, ONLY STEPPED OVER - their content is unknown to every reader of this format
    /// there is. They still have to be counted exactly, because the string pool sits past them
    /// and the shape names are offsets into it. The widths are the reference parser's.
    /// </remarks>
    private static int Trailing(int version) => version switch
    {
        < 3 => 45,
        3 => 70,
        4 or 5 => 78,
        6 => 83,
        _ => 87,
    };

    private static FixedMesh Parse(byte[] raw)
    {
        ReadOnlySpan<byte> file = raw;
        var at = 0;

        int version = file[at++];

        // VERSIONS BELOW NINE PUT THE VERTICES IN A LAYOUT OF THEIR OWN rather than in a DOLm
        // block. Refused by name instead of read by guess: the older layout read as this one
        // would produce a prop-shaped answer out of the wrong bytes.
        if (version < 9)
        {
            return Failed($"version {version.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                + " puts its geometry outside a DOLm block, which this does not read");
        }

        int shapes = Read16(file, ref at);
        int pieces = file[at++];
        at += 2;                                    // How many of the per-piece records there are.
        int records = file[at++];

        if (at + 24 > file.Length)
        {
            return Failed("the file ends inside its own header");
        }

        // The box, PER AXIS, the same way a .smd writes it: min x, max x, min y, max y, min z, max z.
        var least = new Vector3(Float(file, at), Float(file, at + 8), Float(file, at + 16));
        var most = new Vector3(Float(file, at + 4), Float(file, at + 12), Float(file, at + 20));
        at += 24;

        MeshBlock block = SkinnedMesh.Block(file, at);
        if (!block.Ready)
        {
            return Failed(block.Why);
        }

        at = block.At;

        // ONE PAIR OF OFFSETS PER SHAPE - a name and a material, both into the pool at the end.
        if (at + (shapes * 8L) > file.Length)
        {
            return Failed("the file says it has more shapes than it holds");
        }

        var points = new (int Name, int Material)[shapes];
        for (var one = 0; one < shapes; one++)
        {
            points[one] = ((int)Read32(file, ref at), (int)Read32(file, ref at));
        }

        // The pieces, and then the records each of them owns. Stepped over, not read.
        if (at + (pieces * 6L) > file.Length)
        {
            return Failed("the file says it has more pieces than it holds");
        }

        long past = at;
        for (var one = 0; one < pieces; one++)
        {
            past += 2 + 4 + (file[at + (one * 6) + 1] * 12L);
        }

        past += records * (long)Trailing(version);
        if (past + 4 > file.Length)
        {
            return Failed("the file ends before its string pool");
        }

        at = (int)past;
        int chars = (int)Read32(file, ref at);
        if (chars < 0 || at + (chars * 2L) > file.Length)
        {
            return Failed("the string pool says it is bigger than the file that holds it");
        }

        ReadOnlySpan<byte> pool = file.Slice(at, chars * 2);

        // THE SHAPES OF THE BLOCK AND THE NAMES OF THE HEADER ARE THE SAME SHAPES, and where the
        // two counts disagree the smaller one is all that can be paired. Clamped rather than
        // refused: a prop with one unnamed shape still draws.
        int both = Math.Min(shapes, block.Extents.Length);
        var named = new MeshShape[block.Extents.Length];
        var materials = new (string Shape, string Material)[both];
        for (var one = 0; one < block.Extents.Length; one++)
        {
            (int from, int count) = block.Extents[one];
            string name = one < both
                ? Word(pool, points[one].Name)
                : string.Empty;

            if (name.Length == 0)
            {
                name = $"shape {one.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            }

            from = Math.Clamp(from, 0, block.Indices.Length);
            named[one] = new MeshShape(name, from, Math.Clamp(count, 0, block.Indices.Length - from));

            if (one < both)
            {
                materials[one] = (name, Word(pool, points[one].Material));
            }
        }

        return new FixedMesh
        {
            Mesh = SkinnedMesh.Of(
                block.Positions, block.Normals, block.Indices, least, most,
                block.Coordinates, named),
            Named = materials,
        };
    }

    /// <summary>
    /// One string out of the pool: from an offset in characters, up to the zero that ends it.
    /// </summary>
    /// <remarks>
    /// AN OFFSET THAT LANDS NOWHERE COMES BACK EMPTY, which is the check this reader has. The
    /// offsets are written by the same file the walk stepped through, so one that points past the
    /// pool or into the middle of nothing means the walk arrived in the wrong place - and an empty
    /// name is a shape that falls back rather than a prop made of the wrong bytes.
    /// </remarks>
    private static string Word(ReadOnlySpan<byte> pool, int at)
    {
        if (at < 0 || (at * 2) >= pool.Length)
        {
            return string.Empty;
        }

        int from = at * 2;
        int to = from;
        while (to + 1 < pool.Length && BinaryPrimitives.ReadUInt16LittleEndian(pool[to..]) != 0)
        {
            to += 2;
        }

        return System.Text.Encoding.Unicode.GetString(pool[from..to]);
    }

    private static float Float(ReadOnlySpan<byte> file, int at)
        => BinaryPrimitives.ReadSingleLittleEndian(file[at..]);

    private static int Read16(ReadOnlySpan<byte> file, ref int at)
    {
        int said = BinaryPrimitives.ReadUInt16LittleEndian(file[at..]);
        at += 2;
        return said;
    }

    private static uint Read32(ReadOnlySpan<byte> file, ref int at)
    {
        uint said = BinaryPrimitives.ReadUInt32LittleEndian(file[at..]);
        at += 4;
        return said;
    }

    private static FixedMesh Failed(string why) => new() { Why = why };
}
