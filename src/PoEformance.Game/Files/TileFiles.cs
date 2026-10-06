using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;

namespace PoEformance.Game.Files;

/// <summary>
/// A terrain tile's definition (<c>.tdt</c>): which template its geometry is in, and how big it is.
/// </summary>
/// <remarks>
/// THE FILE EVERY PLACED TILE NAMES. TileStruct.TgtFilePtr leads to a <c>.tdt</c> - measured, see
/// RoomProbe - so this is the first hop from "the tile the game put here" to the triangles.
///
/// TWO REFERENCES AND WHERE THEY MEET. zao's <c>libpoe/poe/format/tdt.cpp</c> reads versions 3 to 7
/// in full; annalithic's <c>poeformats/Tdt.cs</c> (2026, written against PoE2 files) reads only the
/// front and adds the one rule zao's does not have: a definition whose leading reference is filled
/// INHERITS another and carries nothing else. Everything read here is in both, and nothing past the
/// size is read at all - the side offsets, the sub-tile blocks and the ground grid are walkability
/// and matching data, not geometry.
///
/// THE STRINGS ARE A TABLE AT THE FRONT and every text field is a CHARACTER offset into it, which
/// is the reader's self-check: a field read from the wrong place lands outside the table or in the
/// middle of a word, so <see cref="Ref"/> takes only offsets at which a string begins, and a template
/// that does not end in <c>.tgt</c> is refused rather than followed.
/// </remarks>
public sealed class TileDefinition
{
    /// <summary>Nothing read.</summary>
    public static TileDefinition None { get; } = new() { Why = "nothing to read" };

    /// <summary>The file's version. zao's reader covers 3 to 7.</summary>
    public int Version { get; private init; }

    /// <summary>The definition this one inherits from, or empty. When filled, nothing else is.</summary>
    public string Inherits { get; private init; } = string.Empty;

    /// <summary>The templates the geometry is in - usually one; the field may list several with <c>;</c>.</summary>
    public IReadOnlyList<string> Templates { get; private init; } = [];

    /// <summary>The tile's tag - a feature name such as a doodad group. Informational.</summary>
    public string Tag { get; private init; } = string.Empty;

    /// <summary>Tiles across, as the definition says. Zero where it inherits.</summary>
    public int Width { get; private init; }

    /// <summary>Tiles down.</summary>
    public int Height { get; private init; }

    /// <summary>
    /// The ground type at each corner, as <c>.gt</c> paths: down-left, down-right, up-right, up-left. Empty where unsaid.
    /// </summary>
    /// <remarks>
    /// annalithic's Tdt.cs ORDER AND PLACE - four string references straight after the size - and
    /// the same self-check as every other field here: an offset that does not start a string, or a
    /// string that is not a <c>.gt</c>, reads as empty rather than as a word from the middle of the
    /// table. The terrain's per-corner byte in memory indexes the same kind of list - see
    /// TileCornerData in the offsets schema.
    /// </remarks>
    public IReadOnlyList<string> Grounds { get; private init; } = [];

    /// <summary>Why nothing was read, or empty.</summary>
    public string Why { get; private init; } = string.Empty;

    /// <summary>Whether it read - an inheriting definition reads, with only <see cref="Inherits"/>.</summary>
    public bool Ready => Why.Length == 0;

    /// <summary>Reads a file's bytes. Never throws: a tile that will not read is a tile that does not draw.</summary>
    public static TileDefinition Read(byte[]? content)
    {
        if (content is not { Length: >= 8 })
        {
            return None;
        }

        try
        {
            return Parse(content);
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException
            or IndexOutOfRangeException or OverflowException)
        {
            return new TileDefinition { Why = $"the file does not read as a tile definition: {exception.Message}" };
        }
    }

    private static TileDefinition Parse(byte[] raw)
    {
        ReadOnlySpan<byte> file = raw;
        int version = BinaryPrimitives.ReadInt32LittleEndian(file);
        if (version < 3)
        {
            return new TileDefinition { Version = version, Why = $"version {version} is older than either reference reads" };
        }

        long chars = BinaryPrimitives.ReadUInt32LittleEndian(file[4..]);
        if (8 + (chars * 2) > file.Length)
        {
            return new TileDefinition { Version = version, Why = "the string table says it is bigger than the file" };
        }

        var table = new Table(file.Slice(8, (int)chars * 2));
        int at = 8 + ((int)chars * 2);

        string inherits = string.Empty;
        if (version >= 7)
        {
            inherits = table.Ref(Next(file, ref at));
            if (inherits.Length > 0)
            {
                // annalithic's rule: an inheriting definition carries nothing after the reference.
                return new TileDefinition { Version = version, Inherits = inherits };
            }
        }

        string templates = version > 4 ? table.Ref(Next(file, ref at)) : string.Empty;
        string tag = table.Ref(Next(file, ref at));

        at += 16;                                        // The four side edge types.

        int width;
        int height;
        if (version == 3)
        {
            width = BinaryPrimitives.ReadInt32LittleEndian(file[at..]);
            height = BinaryPrimitives.ReadInt32LittleEndian(file[(at + 4)..]);
            at += 8;
        }
        else
        {
            width = (sbyte)file[at];
            height = (sbyte)file[at + 1];
            at += 2;
        }

        // THE CORNER GROUND TYPES, only where the file is the layout annalithic reads (a version past
        // 3, whose size is two bytes) and only while there are bytes for them.
        string[] grounds = [];
        if (version > 3 && at + 16 <= file.Length)
        {
            grounds = new string[4];
            for (var corner = 0; corner < 4; corner++)
            {
                string type = table.Ref(Next(file, ref at));
                grounds[corner] = type.EndsWith(".gt", StringComparison.OrdinalIgnoreCase) ? type : string.Empty;
            }
        }

        if (width is < 1 or > 64 || height is < 1 or > 64)
        {
            return new TileDefinition
            {
                Version = version,
                Why = $"the tile says it is {width}x{height}, which no tile is - the fields before it were read from the wrong place",
            };
        }

        string[] named = [.. templates.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        foreach (string one in named)
        {
            if (!one.EndsWith(TileTemplate.Extension, StringComparison.OrdinalIgnoreCase))
            {
                return new TileDefinition
                {
                    Version = version,
                    Why = $"the template field reads \"{one}\", which is not a .tgt - the layout did not hold",
                };
            }
        }

        return new TileDefinition
        {
            Version = version,
            Templates = named,
            Tag = tag,
            Width = width,
            Height = height,
            Grounds = grounds,
        };
    }

    private static uint Next(ReadOnlySpan<byte> file, ref int at)
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(file[at..]);
        at += 4;
        return value;
    }

    /// <summary>The front table: UTF-16 strings, each ending in a NUL, addressed by character offset.</summary>
    private readonly ref struct Table(ReadOnlySpan<byte> bytes)
    {
        private readonly ReadOnlySpan<byte> _bytes = bytes;

        /// <summary>
        /// The string beginning at a character offset, or empty.
        /// </summary>
        /// <remarks>
        /// ONLY WHERE A STRING BEGINS - at zero or just past a NUL - which is zao's rule: his reader
        /// keys the table by each string's start and anything else resolves to nothing. annalithic's
        /// would read from the middle of a word, and that is exactly the wrong answer a misplaced
        /// field would produce, so it is not taken.
        /// </remarks>
        public string Ref(uint offset)
        {
            long at = (long)offset * 2;
            if (at + 2 > _bytes.Length)
            {
                return string.Empty;
            }

            if (at > 0 && BinaryPrimitives.ReadUInt16LittleEndian(_bytes[(int)(at - 2)..]) != 0)
            {
                return string.Empty;
            }

            int end = (int)at;
            while (end + 2 <= _bytes.Length && BinaryPrimitives.ReadUInt16LittleEndian(_bytes[end..]) != 0)
            {
                end += 2;
            }

            return System.Text.Encoding.Unicode.GetString(_bytes[(int)at..end]);
        }
    }
}

/// <summary>One run of shapes in a sub-tile that wear one material.</summary>
/// <param name="Material">Index into <see cref="TileTemplate.Materials"/>.</param>
/// <param name="Shapes">How many consecutive shapes of the sub-tile's mesh wear it.</param>
public readonly record struct TileRun(int Material, int Shapes);

/// <summary>
/// A tile template (<c>.tgt</c>): where each sub-tile's mesh is, and which materials go on it.
/// </summary>
/// <remarks>
/// TEXT, UTF-16, one keyword per line - read against poe_data_tools' <c>tgt</c> parser and
/// annalithic's <c>Tgt.cs</c>, which agree on everything taken here:
///
///     version 3
///     SourceScene "..."            optional
///     Size 2 1
///     TileMeshRoot "Art/Models/Terrain/.../Name"
///     GroundMask "..."             optional
///     NormalMaterials 3
///         "a.mat" ...
///     MaterialSlots N ...          optional
///     SubTileMaterialIndices       only where there are materials
///         2 0 5 1 3                one line per sub-tile: a count, then pairs (or triples)
///
/// THE MESH OF A SUB-TILE IS NAMED, NOT LISTED: <c>{root}_c{x}r{y}.tgm</c>, or <c>{root}.tgm</c> for
/// a one-by-one tile - annalithic's rule, and zao's exporter builds the same names. Rows count from
/// the bottom: the first line of the material indices is the TOP row, so line
/// <c>x + (height - y) * width</c> belongs to <c>c{x+1}r{y}</c>.
///
/// VERSIONS BELOW 3 name a single mesh in another shape of file and are refused by name.
/// </remarks>
public sealed class TileTemplate
{
    /// <summary>What a template's file name ends with.</summary>
    public const string Extension = ".tgt";

    /// <summary>Nothing read.</summary>
    public static TileTemplate None { get; } = new() { Why = "nothing to read" };

    /// <summary>The file's version.</summary>
    public int Version { get; private init; }

    /// <summary>Sub-tiles across.</summary>
    public int Width { get; private init; }

    /// <summary>Sub-tiles down.</summary>
    public int Height { get; private init; }

    /// <summary>The path every sub-tile's mesh name is built from.</summary>
    public string MeshRoot { get; private init; } = string.Empty;

    /// <summary>The <c>GroundMask</c> texture the template names, or empty. Not yet drawn with - see ModelDump.</summary>
    public string GroundMask { get; private init; } = string.Empty;

    /// <summary>The materials, in the order the runs index them.</summary>
    public IReadOnlyList<string> Materials { get; private init; } = [];

    /// <summary>Each sub-tile's material runs, in the file's line order. See <see cref="RunsOf"/>.</summary>
    public IReadOnlyList<IReadOnlyList<TileRun>> Runs { get; private init; } = [];

    /// <summary>Why nothing was read, or empty.</summary>
    public string Why { get; private init; } = string.Empty;

    /// <summary>Whether it read.</summary>
    public bool Ready => Why.Length == 0;

    /// <summary>The mesh of the sub-tile in column <paramref name="x"/> and row <paramref name="y"/>, both from one.</summary>
    public string MeshOf(int x, int y)
        => Width == 1 && Height == 1
            ? MeshRoot + TileMesh.Extension
            : string.Create(CultureInfo.InvariantCulture, $"{MeshRoot}_c{x}r{y}{TileMesh.Extension}");

    /// <summary>The material runs of the sub-tile in column x and row y, both from one, or none.</summary>
    public IReadOnlyList<TileRun> RunsOf(int x, int y)
    {
        int line = (x - 1) + ((Height - y) * Width);
        return line >= 0 && line < Runs.Count ? Runs[line] : [];
    }

    /// <summary>Reads a file's bytes, through the game's shared text decode.</summary>
    public static TileTemplate Read(byte[]? content)
        => content is not { Length: > 0 } ? None : Parse(StatDescriptionFiles.Decode(content));

    /// <summary>Reads the text. Public so the format can be tested without an install.</summary>
    public static TileTemplate Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return None;
        }

        string[] lines = text.Split('\n');
        var at = 0;

        if (!Keyword(lines, ref at, "version", out string rest)
            || !int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out int version))
        {
            return new TileTemplate { Why = "no version line" };
        }

        if (version < 3)
        {
            return new TileTemplate { Version = version, Why = $"version {version} names its mesh another way, which this does not read" };
        }

        Keyword(lines, ref at, "SourceScene", out _);

        if (!Keyword(lines, ref at, "Size", out rest) || Numbers(rest) is not [int width, int height]
            || width is < 1 or > 64 || height is < 1 or > 64)
        {
            return new TileTemplate { Version = version, Why = "no Size line of two numbers" };
        }

        if (!Keyword(lines, ref at, "TileMeshRoot", out rest) || Quoted(rest) is not { Length: > 0 } root)
        {
            return new TileTemplate { Version = version, Why = "no TileMeshRoot line" };
        }

        string mask = Keyword(lines, ref at, "GroundMask", out rest) ? Quoted(rest) ?? string.Empty : string.Empty;

        var materials = new List<string>();
        if (Keyword(lines, ref at, "NormalMaterials", out rest)
            && int.TryParse(rest.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
        {
            for (var one = 0; one < count && at < lines.Length; one++)
            {
                materials.Add(Quoted(lines[at++]) ?? string.Empty);
            }
        }

        if (Keyword(lines, ref at, "MaterialSlots", out rest)
            && int.TryParse(rest.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int slots))
        {
            at = Math.Min(lines.Length, at + slots);
        }

        var runs = new List<IReadOnlyList<TileRun>>(width * height);
        if (materials.Count > 0 && Keyword(lines, ref at, "SubTileMaterialIndices", out rest))
        {
            // THE FIRST SUB-TILE MAY SHARE THE KEYWORD'S LINE, since the grammar only asks for
            // whitespace between them - so what follows the keyword is tried before the next line.
            string first = rest.Trim();
            if (first.Length > 0)
            {
                runs.Add(Run(first, materials.Count));
            }

            while (runs.Count < width * height && at < lines.Length)
            {
                string line = lines[at++].Trim();
                if (line.Length > 0)
                {
                    runs.Add(Run(line, materials.Count));
                }
            }
        }

        return new TileTemplate
        {
            Version = version,
            Width = width,
            Height = height,
            MeshRoot = root.Replace('\\', '/'),
            GroundMask = mask.Replace('\\', '/'),
            Materials = materials,
            Runs = runs,
        };
    }

    /// <summary>
    /// One sub-tile's runs: a count, then that many pairs or triples.
    /// </summary>
    /// <remarks>
    /// PAIRS ARE (MATERIAL, SHAPES) and of a triple the LAST TWO are - annalithic's reading, which is
    /// the only one that says what the numbers mean; poe_data_tools reads both shapes without naming
    /// them. A material index past the list is dropped, and a line that is neither shape is no runs.
    /// </remarks>
    private static IReadOnlyList<TileRun> Run(string line, int materials)
    {
        int[] numbers = Numbers(line);
        if (numbers.Length == 0)
        {
            return [];
        }

        int count = numbers[0];
        int width = numbers.Length - 1 == count * 3 ? 3 : numbers.Length - 1 == count * 2 ? 2 : 0;
        if (width == 0 || count <= 0)
        {
            return [];
        }

        var runs = new List<TileRun>(count);
        for (var one = 0; one < count; one++)
        {
            int material = numbers[1 + (one * width) + (width - 2)];
            int shapes = numbers[1 + (one * width) + (width - 1)];
            runs.Add(new TileRun(material < materials ? material : -1, Math.Max(0, shapes)));
        }

        return runs;
    }

    /// <summary>
    /// The rest of the next non-blank line when it starts with the keyword, stepping past it - or
    /// false and no step. An optional keyword that is not there costs nothing.
    /// </summary>
    private static bool Keyword(string[] lines, ref int at, string keyword, out string rest)
    {
        int look = at;
        while (look < lines.Length && lines[look].Trim().Length == 0)
        {
            look++;
        }

        if (look < lines.Length)
        {
            string line = lines[look].Trim();
            if (line.StartsWith(keyword, StringComparison.Ordinal)
                && (line.Length == keyword.Length || char.IsWhiteSpace(line[keyword.Length])))
            {
                rest = line[keyword.Length..];
                at = look + 1;
                return true;
            }
        }

        rest = string.Empty;
        return false;
    }

    private static string? Quoted(string line)
    {
        int open = line.IndexOf('"', StringComparison.Ordinal);
        int close = open >= 0 ? line.IndexOf('"', open + 1) : -1;
        return close > open ? line[(open + 1)..close] : null;
    }

    private static int[] Numbers(string text)
    {
        string[] words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var numbers = new int[words.Length];
        for (var one = 0; one < words.Length; one++)
        {
            if (!int.TryParse(words[one], NumberStyles.Integer, CultureInfo.InvariantCulture, out numbers[one]))
            {
                return [];
            }
        }

        return numbers;
    }
}

/// <summary>
/// One sub-tile's geometry (<c>.tgm</c>): its props and walls, and its ground.
/// </summary>
/// <remarks>
/// VERSION 9 AND UP WRAP TWO DOLm BLOCKS, which is why this file is short - both are
/// <see cref="SkinnedMesh.Block"/>'s, the same reader the monsters and the props use. The layout
/// between them is poe_data_tools' <c>tgm</c> parser, and annalithic's <c>Tgm.cs</c> agrees with it
/// to the byte:
///
///     u8 version, six floats of box, u16 shapes, u16, u8, u8 tail count
///     DOLm                        the tile's props and walls - may hold nothing
///     per shape: ordinal          u16 at version 9, u32 after it
///                u16              from version 12
///                six floats of box
///     DOLm                        the ground - may hold nothing
///     per shape: u16 ordinal and six floats of box
///     tail count records of 87 bytes
///
/// THE END OF THE FILE IS THE CHECK. poe_data_tools demands it, and a step that went one field
/// wrong anywhere after the first block would not arrive there - so <see cref="Exact"/> says
/// whether it did, and a mismatch is reported rather than hidden. The props are kept either way,
/// because their block was read and verified by the DOLm reader before anything after it was.
///
/// VERSIONS BELOW 9 put the vertices outside a DOLm block and are refused by name.
/// </remarks>
public sealed class TileMesh
{
    /// <summary>What a tile mesh's file name ends with.</summary>
    public const string Extension = ".tgm";

    private const int TailBytes = 87;
    private const int BoxBytes = 24;

    /// <summary>Nothing read.</summary>
    public static TileMesh None { get; } = new() { Why = "nothing to read" };

    /// <summary>The file's version.</summary>
    public int Version { get; private init; }

    /// <summary>The props and walls - the part that carries the template's materials. May be empty.</summary>
    public SkinnedMesh Props { get; private init; } = SkinnedMesh.None;

    /// <summary>The ground under them. May be empty.</summary>
    public SkinnedMesh Ground { get; private init; } = SkinnedMesh.None;

    /// <summary>Whether the walk ended exactly at the end of the file - the layout's self-check.</summary>
    public bool Exact { get; private init; }

    /// <summary>Why nothing was read, or empty.</summary>
    public string Why { get; private init; } = string.Empty;

    /// <summary>Whether it read - a sub-tile with no geometry at all still reads.</summary>
    public bool Ready => Why.Length == 0;

    /// <summary>Reads a file's bytes. Never throws.</summary>
    public static TileMesh Read(byte[]? content)
    {
        if (content is not { Length: > 1 + BoxBytes + 6 })
        {
            return None;
        }

        try
        {
            return Parse(content);
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException
            or IndexOutOfRangeException or OverflowException)
        {
            return new TileMesh { Why = $"the file does not read as a tile mesh: {exception.Message}" };
        }
    }

    private static TileMesh Parse(byte[] raw)
    {
        ReadOnlySpan<byte> file = raw;
        int version = file[0];
        if (version < 9)
        {
            return new TileMesh
            {
                Version = version,
                Why = $"version {version} puts its geometry outside a DOLm block, which this does not read",
            };
        }

        int at = 1 + BoxBytes;
        at += 4;                                        // Shapes and an unnamed u16.
        at += 1;                                        // An unnamed u8.
        int tails = file[at++];

        MeshBlock props = SkinnedMesh.Block(file, at, empty: true);
        if (!props.Ready)
        {
            return new TileMesh { Version = version, Why = props.Why };
        }

        SkinnedMesh main = Mesh(props);
        int bound = (version == 9 ? 2 : 4) + (version >= 12 ? 2 : 0) + BoxBytes;
        at = props.At + (Shapes(props) * bound);

        MeshBlock floor = SkinnedMesh.Block(file, at, empty: true);
        if (!floor.Ready)
        {
            // THE PROPS STAND ON THEIR OWN. Their block read and checked itself; only what follows
            // it did not, and that is said rather than dropping a tile that can be drawn.
            return new TileMesh { Version = version, Props = main, Why = main.Ready ? string.Empty : floor.Why };
        }

        at = floor.At + (Shapes(floor) * (2 + BoxBytes)) + (tails * TailBytes);

        return new TileMesh
        {
            Version = version,
            Props = main,
            Ground = Mesh(floor),
            Exact = at == file.Length,
        };
    }

    /// <summary>How many per-shape records follow a block: one per shape of its first level, if it has one.</summary>
    private static int Shapes(MeshBlock block) => block.Facts.Details > 0 ? block.Facts.BlockShapes : 0;

    /// <summary>
    /// A block as a mesh, its shapes named by position.
    /// </summary>
    /// <remarks>
    /// A TILE'S SHAPES HAVE NO NAMES in the file - its materials are given by RUNS of shapes in the
    /// template, not by name - so they are numbered, which is what the runs count through.
    /// </remarks>
    private static SkinnedMesh Mesh(MeshBlock block)
    {
        if (block.Positions.Length == 0 || block.Indices.Length == 0)
        {
            return SkinnedMesh.None;
        }

        var shapes = new MeshShape[block.Extents.Length];
        for (var one = 0; one < shapes.Length; one++)
        {
            (int from, int count) = block.Extents[one];
            from = Math.Clamp(from, 0, block.Indices.Length);
            shapes[one] = new MeshShape(
                one.ToString(CultureInfo.InvariantCulture), from, Math.Clamp(count, 0, block.Indices.Length - from));
        }

        Vector3 least = block.Positions[0];
        Vector3 most = least;
        foreach (Vector3 point in block.Positions)
        {
            least = Vector3.Min(least, point);
            most = Vector3.Max(most, point);
        }

        return SkinnedMesh.Of(block.Positions, block.Normals, block.Indices, least, most, block.Coordinates, shapes, block.Facts, block.Colours);
    }
}
