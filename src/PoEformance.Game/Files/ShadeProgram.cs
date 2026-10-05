using System.Numerics;

namespace PoEformance.Game.Files;

/// <summary>A texture a shade program reads: its path, and whether the read undoes sRGB.</summary>
public readonly record struct ShadeTexture(string Path, bool Srgb);

/// <summary>
/// What compiling a material's graphs came to: a program where the colour could be worked out,
/// and every place a graph's colour was left out because a node in it is not understood.
/// </summary>
/// <param name="Program">The program, or null where no graph's colour could be evaluated.</param>
/// <param name="Skipped">Why each skipped graph was skipped, as "node in graph".</param>
public sealed record ShadeCompile(ShadeProgram? Program, IReadOnlyList<string> Skipped);

/// <summary>
/// A material's colour as its shader graphs compute it, compiled into steps over a few registers
/// and run once per pixel.
/// </summary>
/// <remarks>
/// WHY THERE IS ONE. A material does not always hand its colour over as a texture: the
/// stromatolite ledge's RockyLedgec.mat gives StromatoliteLedge_Blend.fxgraph a texture in a slot
/// called Meshmap, and the graph mixes two tiled rock textures by its green channel, darkens them
/// by its red one and lays a dust colour over them by its blue one and a world-space grunge mask.
/// Drawn as colour, that texture is green and magenta. The graph is the only place the real colour
/// is written down, so the graph is what is run.
///
/// ONLY THE COLOUR. A graph writes many things - normals, gloss, the indirect light - and this
/// renderer lights with one lamp and the mesh's own normals, so only what feeds <c>AlbedoColor</c>
/// (and the coordinates and positions that feeds on) is compiled.
///
/// STAGES IN ORDER, GRAPHS IN ORDER WITHIN ONE. A material's graphs read and write named values -
/// <c>InputUV</c> reads the coordinates, <c>UV</c> writes them - at named stages. Every graph
/// writing at UVSetup is run before any reading at Texturing_Init, and within a stage the
/// material's own order decides: TallDune1c writes its colour twice at Texturing_Init and the
/// second, PBRGround's, is the one that stands. Within one graph at one stage every read sees the
/// values as they were before that graph wrote any.
///
/// NOTHING IS GUESSED. No reference reads these graphs, so a node is evaluated only where its type
/// and its ports leave no doubt about what it does - Add, Lerp3, Saturate, SampleTexture. A graph
/// whose colour depends on anything else - MaskedContactFade, Noise31, a vertex colour this reader
/// does not have - is left out whole and NAMED, and the colour stays what the graphs before it
/// made it. A material none of whose graphs could be evaluated has no program, and is drawn
/// exactly as it was before graphs were read.
///
/// LINEAR, as the game's shaders are. A texture flagged sRGB is linearised when it is read, the
/// arithmetic runs on light rather than on display values, and the result is put back into sRGB
/// before the renderer's own shading - which is what a plain texture is drawn as.
/// </remarks>
public sealed class ShadeProgram
{
    /// <summary>The stages a colour is assembled across, in the order they run.</summary>
    /// <remarks>
    /// THE ORDER IS THE NAMES': coordinates are set up before textures are read with them, and the
    /// texturing stage has an initial pass before it. Every stage the graphs read so far is here; a
    /// colour written at any other is left out and named rather than placed by a guess.
    /// </remarks>
    public static readonly IReadOnlyList<string> Stages = ["VertexInit", "UVSetup", "Texturing_Init", "Texturing", "PreLighting"];

    /// <summary>Most registers one program may use - it lives on the stack while a triangle is drawn.</summary>
    public const int MostRegisters = 192;

    /// <summary>The registers every program starts with: the coordinates, the position and the normal.</summary>
    internal const int Coordinates = 0;

    internal const int Position = 1;

    internal const int Normal = 2;

    /// <summary>How finely the sRGB curve is tabled. A step is under a third of a display level.</summary>
    private const int Table = 4096;

    private static readonly float[] ToLinear = Tabled(x => x <= 0.04045f ? x / 12.92f : MathF.Pow((x + 0.055f) / 1.055f, 2.4f));

    private static readonly float[] ToSrgb = Tabled(x => x <= 0.0031308f ? x * 12.92f : (1.055f * MathF.Pow(x, 1f / 2.4f)) - 0.055f);

    private readonly Step[] _steps;
    private readonly int[] _constantAt;
    private readonly Vector4[] _constants;
    private readonly int _result;
    private readonly int[] _sampleTexture;
    private readonly Mipmaps?[] _sheets;

    private ShadeProgram(
        Step[] steps, int[] constantAt, Vector4[] constants, int registers, int result,
        ShadeTexture[] textures, int[] sampleTexture, int plain, IReadOnlyList<string> graphs, Mipmaps?[] sheets)
    {
        _steps = steps;
        _constantAt = constantAt;
        _constants = constants;
        Registers = registers;
        _result = result;
        Textures = textures;
        _sampleTexture = sampleTexture;
        Plain = plain;
        Graphs = graphs;
        _sheets = sheets;
        UsesNormal = steps.Any(one => one.Reads(Normal));
    }

    internal enum Op : byte
    {
        Clear,
        Swizzle,
        Add,
        Subtract,
        Multiply,
        Negate,
        OneMinus,
        Saturate,
        Power,
        Normalize,
        Lerp,
        Fit,
        Sample,
        SampleLod,
    }

    /// <summary>How many registers a run needs.</summary>
    public int Registers { get; }

    /// <summary>The textures the program reads, in the order its steps name them.</summary>
    public IReadOnlyList<ShadeTexture> Textures { get; }

    /// <summary>How many texture reads take their level from the triangle - see <see cref="Levels"/>.</summary>
    public int Samples => _sampleTexture.Length;

    /// <summary>
    /// The texture the whole colour IS, where it is one sRGB read at the mesh's own coordinates; else -1.
    /// </summary>
    /// <remarks>
    /// THE ORDINARY MATERIAL, and worth recognising: DielectricSpecGlossBN's colour is its
    /// AlbedoTransparency texture and nothing else, and drawing that through a program would be
    /// the renderer's plain texture read done slower. So it is said, and the caller draws the
    /// texture.
    /// </remarks>
    public int Plain { get; }

    /// <summary>The graphs whose colour the program carries, by path.</summary>
    public IReadOnlyList<string> Graphs { get; }

    /// <summary>The decoded textures, one per entry of <see cref="Textures"/> - null where not yet bound.</summary>
    public IReadOnlyList<Mipmaps?> Sheets => _sheets;

    /// <summary>Whether every texture is in hand, which a program must be before it is drawn with.</summary>
    public bool Bound => Array.TrueForAll(_sheets, one => one is not null);

    /// <summary>Whether a run reads the normal, which costs a normalisation per pixel.</summary>
    internal bool UsesNormal { get; }

    /// <summary>
    /// Compiles the colour a material's graphs compute, or says why it cannot.
    /// </summary>
    /// <param name="chain">The material's graph instances in its own order, each with its graph.</param>
    public static ShadeCompile Compile(IReadOnlyList<(ShaderInstance Instance, ShaderGraph Graph)> chain)
    {
        ArgumentNullException.ThrowIfNull(chain);

        var build = new Builder();
        var skipped = new List<string>();
        var graphs = new List<string>();

        // WHAT EACH NAMED VALUE HOLDS NOW, by register. The colour starts unwritten: a graph that
        // reads it before any has written it has nothing to read.
        var state = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["UV"] = Coordinates,
            ["WorldPos"] = Position,
            ["WorldNormal"] = Normal,
        };

        var lookups = new Lookup[chain.Count];
        for (var one = 0; one < chain.Count; one++)
        {
            lookups[one] = new Lookup(chain[one].Graph);
        }

        foreach (string stage in Stages)
        {
            for (var link = 0; link < chain.Count; link++)
            {
                (ShaderInstance instance, ShaderGraph graph) = chain[link];
                Lookup lookup = lookups[link];
                Dictionary<string, int>? seen = null;
                var wrote = new List<(string Channel, int Register)>();
                foreach (ShaderNode node in graph.Nodes)
                {
                    if (!string.Equals(node.Stage, stage, StringComparison.Ordinal)
                        || Written(node.Type) is not { } channel
                        || !lookup.Fed(node))
                    {
                        continue;
                    }

                    // EVERY READ IN ONE GRAPH AT ONE STAGE SEES THE VALUES FROM BEFORE IT - see
                    // the remarks - so the snapshot is taken once, at its first write.
                    seen ??= new Dictionary<string, int>(state, StringComparer.Ordinal);
                    Builder.Mark mark = build.Marked();
                    var unit = new Unit(build, lookup, instance, seen);
                    if (unit.Port(node, "input") is { } register && build.Fits)
                    {
                        wrote.Add((channel, register));
                        continue;
                    }

                    // THE COLOUR AND THE COORDINATES ARE SAID WHEN LEFT OUT: one is what is drawn, the
                    // other is where every texture after it is read. A position or a normal a
                    // graph moves and this could not follow changes neither.
                    string why = build.Fits ? unit.Why : "too many steps";
                    build.Back(mark);
                    if (channel == "Albedo")
                    {
                        skipped.Add($"{why} in {Named(instance.Parent)}");
                    }
                    else if (channel == "UV")
                    {
                        skipped.Add($"{why} in {Named(instance.Parent)}'s coordinates");
                    }
                }

                foreach ((string channel, int register) in wrote)
                {
                    state[channel] = register;
                    if (channel == "Albedo" && !graphs.Contains(instance.Parent, StringComparer.OrdinalIgnoreCase))
                    {
                        graphs.Add(instance.Parent);
                    }
                }
            }
        }

        // A COLOUR WRITTEN AT A STAGE NOT IN THE LIST is a graph this has not seen the like of: said,
        // not placed.
        for (var link = 0; link < chain.Count; link++)
        {
            foreach (ShaderNode node in chain[link].Graph.Nodes)
            {
                if (node.Type == "AlbedoColor" && !Stages.Contains(node.Stage) && lookups[link].Fed(node))
                {
                    skipped.Add($"AlbedoColor at {node.Stage} in {Named(chain[link].Instance.Parent)}");
                }
            }
        }

        if (!state.TryGetValue("Albedo", out int result))
        {
            return new ShadeCompile(null, skipped);
        }

        int plain = build.PlainOf(result);
        ShadeTexture[] textures = [.. build.Textures];
        var program = new ShadeProgram(
            [.. build.Steps], [.. build.ConstantAt], [.. build.Constants], build.Next, result,
            textures, [.. build.SampleTextures], plain, graphs, new Mipmaps?[textures.Length]);
        return new ShadeCompile(program, skipped);
    }

    /// <summary>This program with its textures in hand, one per entry of <see cref="Textures"/>.</summary>
    public ShadeProgram With(IReadOnlyList<Mipmaps?> sheets)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        var bound = new Mipmaps?[Textures.Count];
        for (var one = 0; one < bound.Length && one < sheets.Count; one++)
        {
            bound[one] = sheets[one];
        }

        return new ShadeProgram(
            _steps, _constantAt, _constants, Registers, _result,
            [.. Textures], _sampleTexture, Plain, Graphs, bound);
    }

    /// <summary>
    /// Puts the constants in their registers - once per triangle, since nothing writes them.
    /// </summary>
    internal void Preset(Span<Vector4> registers)
    {
        for (var one = 0; one < _constantAt.Length; one++)
        {
            registers[_constantAt[one]] = _constants[one];
        }
    }

    /// <summary>
    /// The colour at one pixel, in sRGB 0..1, ready for the renderer's shading.
    /// </summary>
    /// <param name="registers">Scratch, <see cref="Registers"/> long, with <see cref="Preset"/> already run on it.</param>
    /// <param name="coordinates">The pixel's texture coordinates.</param>
    /// <param name="position">The pixel's position in the model's space.</param>
    /// <param name="normal">The pixel's normal in the model's space, not yet normalised.</param>
    /// <param name="levels">The level each texture read takes, from <see cref="Levels"/>.</param>
    internal Vector3 Colour(
        Span<Vector4> registers, Vector2 coordinates, Vector3 position, Vector3 normal, ReadOnlySpan<float> levels)
    {
        registers[Coordinates] = new Vector4(coordinates, 0f, 0f);
        registers[Position] = new Vector4(position, 0f);
        if (UsesNormal)
        {
            registers[Normal] = new Vector4(normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : normal, 0f);
        }

        Run(registers, levels, default, -1);

        Vector4 colour = registers[_result];
        return new Vector3(Srgb(colour.X), Srgb(colour.Y), Srgb(colour.Z));
    }

    /// <summary>
    /// The level each texture read takes on one triangle, worked out from what it reads at the corners.
    /// </summary>
    /// <remarks>
    /// THE RENDERER'S OWN RULE, PER READ: how many texels one pixel steps across, from the
    /// triangle's corners on the screen and on the texture - see MeshPicture.Level. A read whose
    /// coordinates are the mesh's times twelve steps across twelve times as many texels, and the
    /// corners say so without the program having to: they are run through it with the reads
    /// themselves left out, and what each read would have been handed is kept.
    /// </remarks>
    internal void Levels(
        Span<Vector4> registers,
        ReadOnlySpan<Vector2> coordinates,
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<Vector3> normals,
        Vector3 c0, Vector3 c1, Vector3 c2, float area,
        Span<Vector2> spots,
        Span<float> levels)
    {
        for (var corner = 0; corner < 3; corner++)
        {
            registers[Coordinates] = new Vector4(coordinates[corner], 0f, 0f);
            registers[Position] = new Vector4(positions[corner], 0f);
            registers[Normal] = new Vector4(normals[corner], 0f);
            Run(registers, default, spots, corner);
        }

        for (var read = 0; read < _sampleTexture.Length; read++)
        {
            Mipmaps? sheet = _sheets[_sampleTexture[read]];
            levels[read] = sheet is null
                ? 0f
                : MeshPicture.Level(c0, c1, c2, spots[read * 3], spots[(read * 3) + 1], spots[(read * 3) + 2], area, sheet);
        }
    }

    /// <summary>
    /// The steps, in order. With <paramref name="corner"/> at -1 it is a pixel; otherwise the reads
    /// are skipped and their coordinates kept in <paramref name="spots"/> for that corner.
    /// </summary>
    private void Run(Span<Vector4> r, ReadOnlySpan<float> levels, Span<Vector2> spots, int corner)
    {
        foreach (ref readonly Step step in _steps.AsSpan())
        {
            switch (step.Op)
            {
                case Op.Clear:
                    r[step.To] = Vector4.Zero;
                    break;

                case Op.Swizzle:
                {
                    Vector4 from = r[step.A];
                    Vector4 into = r[step.To];
                    int packed = step.Extra;
                    int mask = packed >> 8;
                    for (var part = 0; part < 4; part++)
                    {
                        if ((mask & (1 << part)) != 0)
                        {
                            into[part] = from[(packed >> (part * 2)) & 3];
                        }
                    }

                    r[step.To] = into;
                    break;
                }

                case Op.Add:
                    r[step.To] = step.C >= 0 ? r[step.A] + r[step.B] + r[step.C] : r[step.A] + r[step.B];
                    break;

                case Op.Subtract:
                    r[step.To] = r[step.A] - r[step.B];
                    break;

                case Op.Multiply:
                    r[step.To] = r[step.A] * r[step.B];
                    break;

                case Op.Negate:
                    r[step.To] = -r[step.A];
                    break;

                case Op.OneMinus:
                    r[step.To] = Vector4.One - r[step.A];
                    break;

                case Op.Saturate:
                    r[step.To] = Vector4.Clamp(r[step.A], Vector4.Zero, Vector4.One);
                    break;

                case Op.Power:
                {
                    Vector4 b = r[step.A];
                    Vector4 e = r[step.B];
                    r[step.To] = new Vector4(
                        MathF.Pow(b.X, e.X), MathF.Pow(b.Y, e.Y), MathF.Pow(b.Z, e.Z), MathF.Pow(b.W, e.W));
                    break;
                }

                case Op.Normalize:
                {
                    var v = new Vector3(r[step.A].X, r[step.A].Y, r[step.A].Z);
                    r[step.To] = new Vector4(v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : v, 0f);
                    break;
                }

                case Op.Lerp:
                    r[step.To] = r[step.A] + ((r[step.B] - r[step.A]) * r[step.C]);
                    break;

                case Op.Fit:
                    r[step.To] = Fitted(r[step.A], r[step.B], r[step.C], r[step.D], r[step.E]);
                    break;

                case Op.Sample:
                    if (corner >= 0)
                    {
                        Vector4 at = r[step.A];
                        spots[(step.B * 3) + corner] = new Vector2(at.X, at.Y);
                        r[step.To] = Vector4.Zero;
                    }
                    else
                    {
                        r[step.To] = Read(step.Extra, new Vector2(r[step.A].X, r[step.A].Y), levels[step.B]);
                    }

                    break;

                case Op.SampleLod:
                    r[step.To] = corner >= 0
                        ? Vector4.Zero
                        : Read(step.Extra, new Vector2(r[step.A].X, r[step.A].Y), r[step.B].X);
                    break;
            }
        }
    }

    /// <summary>One texture read at a level, linearised where the texture is sRGB.</summary>
    private Vector4 Read(int texture, Vector2 spot, float level)
    {
        Mipmaps? sheet = _sheets[texture];
        if (sheet is null)
        {
            return Vector4.Zero;
        }

        Vector4 texel = MeshPicture.Sample4(sheet, spot, Math.Clamp(level, 0f, sheet.Count - 1));
        return Textures[texture].Srgb
            ? new Vector4(Linear(texel.X), Linear(texel.Y), Linear(texel.Z), texel.W)
            : texel;
    }

    /// <summary>
    /// A value moved from one range into another, each component on its own.
    /// </summary>
    /// <remarks>
    /// NOT CLAMPED. Whether the game's node clamps is not written down anywhere this has; where its
    /// input lies inside the input range - a texture channel in nought to one, mapped from nought to
    /// one or one to nought, which is every use seen - the two agree. An empty input range gives the
    /// bottom of the output one rather than a division by nothing.
    /// </remarks>
    private static Vector4 Fitted(Vector4 value, Vector4 inMin, Vector4 inMax, Vector4 outMin, Vector4 outMax)
    {
        var said = default(Vector4);
        for (var part = 0; part < 4; part++)
        {
            float span = inMax[part] - inMin[part];
            said[part] = span == 0f
                ? outMin[part]
                : outMin[part] + ((value[part] - inMin[part]) / span * (outMax[part] - outMin[part]));
        }

        return said;
    }

    private static float Linear(float value) => ToLinear[(int)((Math.Clamp(value, 0f, 1f) * (Table - 1)) + 0.5f)];

    private static float Srgb(float value) => ToSrgb[(int)((Math.Clamp(value, 0f, 1f) * (Table - 1)) + 0.5f)];

    private static float[] Tabled(Func<float, float> curve)
    {
        var table = new float[Table];
        for (var one = 0; one < Table; one++)
        {
            table[one] = curve(one / (float)(Table - 1));
        }

        return table;
    }

    /// <summary>The value a writer node writes, or null for a node that writes none this reads.</summary>
    private static string? Written(string type) => type switch
    {
        "UV" => "UV",
        "WorldPos" => "WorldPos",
        "WorldNormal" => "WorldNormal",
        "AlbedoColor" => "Albedo",
        _ => null,
    };

    /// <summary>The value a reader node reads, or null for a node that is not a reader this knows.</summary>
    private static string? ReadBy(string type) => type switch
    {
        "InputUV" => "UV",
        "InputWorldPos" => "WorldPos",
        "InputWorldNormal" => "WorldNormal",
        "InputAlbedoColor" => "Albedo",
        _ => null,
    };

    /// <summary>A graph's file name without its folder or extension, for the line under the picture.</summary>
    private static string Named(string path)
    {
        string said = path.Replace('\\', '/');
        said = said[(said.LastIndexOf('/') + 1)..];
        int dot = said.LastIndexOf('.');
        return dot > 0 ? said[..dot] : said;
    }

    /// <summary>One step: what to do, where to put it, and from which registers.</summary>
    /// <param name="Extra">A texture's index for a read, the packed swizzle for a swizzle.</param>
    internal readonly record struct Step(Op Op, int To, int A, int B, int C, int D, int E, int Extra)
    {
        public bool Reads(int register) => Op switch
        {
            Op.Clear => false,

            // A READ'S B IS WHICH LEVEL IT TAKES, not a register.
            Op.Sample => A == register,
            _ => A == register || B == register || C == register || D == register || E == register,
        };
    }

    /// <summary>A graph's nodes and links, found by the keys links name them by.</summary>
    private sealed class Lookup
    {
        private readonly Dictionary<string, ShaderNode> _nodes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ShaderNode> _loose = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<ShaderLink>> _into = new(StringComparer.Ordinal);

        public Lookup(ShaderGraph graph)
        {
            foreach (ShaderNode node in graph.Nodes)
            {
                _nodes.TryAdd(ShaderGraph.Key(node.Type, node.Index, node.Stage), node);
                _loose.TryAdd(ShaderGraph.Key(node.Type, node.Index, string.Empty), node);
            }

            foreach (ShaderLink link in graph.Links)
            {
                string key = KeyOf(link.Target);
                if (!_into.TryGetValue(key, out List<ShaderLink>? list))
                {
                    _into[key] = list = [];
                }

                list.Add(link);
            }
        }

        public ShaderNode? Node(ShaderEnd end)
            => _nodes.TryGetValue(ShaderGraph.Key(end.Type, end.Index, end.Stage), out ShaderNode? node)
                ? node
                : _loose.GetValueOrDefault(ShaderGraph.Key(end.Type, end.Index, string.Empty));

        /// <summary>The links into one node, every port.</summary>
        public IReadOnlyList<ShaderLink> Into(ShaderNode node)
            => _into.TryGetValue(ShaderGraph.Key(node.Type, node.Index, node.Stage), out List<ShaderLink>? list)
                ? list
                : (IReadOnlyList<ShaderLink>)[];

        /// <summary>Whether anything feeds a node - a writer with nothing on its input writes nothing.</summary>
        public bool Fed(ShaderNode node) => Into(node).Count > 0;

        private string KeyOf(ShaderEnd end)
            => Node(end) is { } node
                ? ShaderGraph.Key(node.Type, node.Index, node.Stage)
                : ShaderGraph.Key(end.Type, end.Index, end.Stage);
    }

    /// <summary>The steps, constants and textures being collected, with a way back to an earlier point.</summary>
    private sealed class Builder
    {
        public List<Step> Steps { get; } = [];

        public List<int> ConstantAt { get; } = [];

        public List<Vector4> Constants { get; } = [];

        public List<ShadeTexture> Textures { get; } = [];

        public List<int> SampleTextures { get; } = [];

        /// <summary>Which registers hold, in x, y and z, exactly one plain read - see <see cref="ShadeProgram.Plain"/>.</summary>
        private readonly Dictionary<int, int> _plain = [];

        public int Next { get; private set; } = 3;

        public bool Fits => Next <= MostRegisters;

        public readonly record struct Mark(int Steps, int Constants, int Textures, int Samples, int Next);

        public Mark Marked() => new(Steps.Count, Constants.Count, Textures.Count, SampleTextures.Count, Next);

        public void Back(Mark mark)
        {
            Steps.RemoveRange(mark.Steps, Steps.Count - mark.Steps);
            ConstantAt.RemoveRange(mark.Constants, ConstantAt.Count - mark.Constants);
            Constants.RemoveRange(mark.Constants, Constants.Count - mark.Constants);
            Textures.RemoveRange(mark.Textures, Textures.Count - mark.Textures);
            SampleTextures.RemoveRange(mark.Samples, SampleTextures.Count - mark.Samples);
            foreach (int gone in _plain.Keys.Where(one => one >= mark.Next).ToList())
            {
                _plain.Remove(gone);
            }

            Next = mark.Next;
        }

        public int Register() => Next++;

        public int Constant(Vector4 value)
        {
            int at = Register();
            ConstantAt.Add(at);
            Constants.Add(value);
            return at;
        }

        public int Emit(Op op, int a, int b = -1, int c = -1, int d = -1, int e = -1, int extra = 0)
        {
            int to = Register();
            Steps.Add(new Step(op, to, a, b, c, d, e, extra));
            return to;
        }

        public int Texture(ShadeTexture texture)
        {
            int at = Textures.IndexOf(texture);
            if (at < 0)
            {
                at = Textures.Count;
                Textures.Add(texture);
            }

            return at;
        }

        public int Sample(int texture, int coordinates)
        {
            int read = SampleTextures.Count;
            SampleTextures.Add(texture);
            int to = Emit(Op.Sample, coordinates, read, extra: texture);
            if (coordinates == Coordinates && Textures[texture].Srgb)
            {
                _plain[to] = texture;
            }

            return to;
        }

        /// <summary>Keeps the plain read's mark through a swizzle that carries its x, y and z across unchanged.</summary>
        public void Carried(int into, int from, int packed)
        {
            int mask = packed >> 8;
            bool straight = (mask & 7) == 7 && (packed & 0x3F) == 0b10_01_00;
            if (straight && _plain.TryGetValue(from, out int texture))
            {
                _plain[into] = texture;
            }
            else if ((mask & 7) != 0)
            {
                _plain.Remove(into);
            }
        }

        public int PlainOf(int register) => _plain.TryGetValue(register, out int texture) ? texture : -1;
    }

    /// <summary>One writer's expression being compiled: its graph, its instance and the values it reads.</summary>
    private sealed class Unit(Builder build, Lookup lookup, ShaderInstance instance, IReadOnlyDictionary<string, int> seen)
    {
        private readonly Dictionary<ShaderNode, int> _done = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<ShaderNode> _open = new(ReferenceEqualityComparer.Instance);

        /// <summary>Why the expression could not be compiled - the node type it stopped at.</summary>
        public string Why { get; private set; } = string.Empty;

        /// <summary>The register holding what arrives on one port, or null.</summary>
        public int? Port(ShaderNode node, string port)
        {
            var links = new List<ShaderLink>();
            foreach (ShaderLink link in lookup.Into(node))
            {
                if (string.Equals(link.Target.Variable, port, StringComparison.Ordinal))
                {
                    links.Add(link);
                }
            }

            if (links.Count == 0)
            {
                return Fail($"{node.Type} with nothing on {port}");
            }

            // ONE WHOLE VALUE STRAIGHT ACROSS needs no step of its own.
            if (links.Count == 1 && links[0].Source.Swizzle.Length == 0 && links[0].Target.Swizzle.Length == 0)
            {
                return Output(links[0].Source);
            }

            var parts = new (int From, int Packed)[links.Count];
            int covered = 0;
            for (var one = 0; one < links.Count; one++)
            {
                if (Output(links[one].Source) is not { } from)
                {
                    return null;
                }

                if (Packed(links[one].Source.Swizzle, links[one].Target.Swizzle) is not { } packed)
                {
                    return Fail($"{node.Type} with a swizzle this does not read");
                }

                parts[one] = (from, packed);
                covered |= packed >> 8;
            }

            int into = build.Register();
            if (covered != 0b1111)
            {
                build.Steps.Add(new Step(Op.Clear, into, -1, -1, -1, -1, -1, 0));
            }

            foreach ((int from, int packed) in parts)
            {
                build.Steps.Add(new Step(Op.Swizzle, into, from, -1, -1, -1, -1, packed));
                build.Carried(into, from, packed);
            }

            return into;
        }

        /// <summary>The register holding a node's output.</summary>
        private int? Output(ShaderEnd end)
        {
            if (lookup.Node(end) is not { } node)
            {
                return Fail($"a link to {end.Type} #{end.Index}, which the graph does not have");
            }

            if (_done.TryGetValue(node, out int known))
            {
                return known;
            }

            if (!_open.Add(node))
            {
                return Fail($"{node.Type} feeding itself");
            }

            int? said = Compiled(node);
            _open.Remove(node);
            if (said is { } register)
            {
                _done[node] = register;
            }

            return said;
        }

        private int? Compiled(ShaderNode node)
        {
            if (ReadBy(node.Type) is { } channel)
            {
                return seen.TryGetValue(channel, out int register)
                    ? register
                    : Fail($"{node.Type} before any graph wrote it");
            }

            switch (node.Type)
            {
                case "ConstantPixel":
                case "ConstantPixel2":
                case "ConstantPixel3":
                case "ConstantPixel4":
                case "ConstantFloat":
                case "ConstantFloat2":
                case "ConstantFloat3":
                case "ConstantFloat4":
                case "ConstantBool":
                case "ConstantPixelBool":
                    return build.Constant(Vectored(Parameter(node, 0).Numbers));

                case "Zero":
                    return build.Constant(Vector4.Zero);

                case "MultiplyConst":
                case "MultiplyConst2":
                case "MultiplyConst3":
                case "MultiplyConst4":
                    return Port(node, "a") is { } scaled
                        ? build.Emit(Op.Multiply, scaled, build.Constant(Vectored(Parameter(node, 0).Numbers)))
                        : null;

                case "Add":
                case "Add2":
                case "Add3":
                case "Add4":
                    return Binary(node, Op.Add, "a", "b");

                case "Subtract":
                case "Subtract2":
                case "Subtract3":
                case "Subtract4":
                    return Binary(node, Op.Subtract, "a", "b");

                case "Multiply":
                case "Multiply2":
                case "Multiply3":
                case "Multiply4":
                    return Binary(node, Op.Multiply, "a", "b");

                case "Power":
                    return Binary(node, Op.Power, "base", "exp");

                case "Negate":
                    return Unary(node, Op.Negate);

                case "OneMinus":
                    return Unary(node, Op.OneMinus);

                case "Saturate":
                    return Unary(node, Op.Saturate);

                case "Normalize3":
                    return Unary(node, Op.Normalize);

                case "Dummy4":
                    return Single(node);

                case "Lerp":
                case "Lerp2":
                case "Lerp3":
                case "Lerp4":
                    return Port(node, "a") is { } a && Port(node, "b") is { } b && Port(node, "alpha") is { } alpha
                        ? build.Emit(Op.Lerp, a, b, alpha)
                        : null;

                case "FitRangeFromInput":
                    return Port(node, "value") is { } value
                        && Port(node, "in_min") is { } inMin && Port(node, "in_max") is { } inMax
                        && Port(node, "out_min") is { } outMin && Port(node, "out_max") is { } outMax
                        ? build.Emit(Op.Fit, value, inMin, inMax, outMin, outMax)
                        : null;

                case "SampleTexture":
                    return Texture(node, Parameter(node, 0)) is { } sheet && Port(node, "uv") is { } spot
                        ? build.Sample(sheet, spot)
                        : null;

                case "SampleInputTexture":
                    return Handed(node) is { } handed && Port(node, "uv") is { } at
                        ? build.Sample(handed, at)
                        : null;

                case "SampleInputTextureLod":
                    return Handed(node) is { } given && Port(node, "uv") is { } where && Port(node, "lod") is { } lod
                        ? build.Emit(Op.SampleLod, where, lod, extra: given)
                        : null;

                default:
                    return Fail(node.Type);
            }
        }

        private int? Binary(ShaderNode node, Op op, string left, string right)
            => Port(node, left) is { } a && Port(node, right) is { } b ? build.Emit(op, a, b) : null;

        private int? Unary(ShaderNode node, Op op) => Single(node) is { } input ? build.Emit(op, input) : null;

        /// <summary>A one-input node's input, whatever its port is called - there is only the one.</summary>
        private int? Single(ShaderNode node)
        {
            string[] ports = [.. lookup.Into(node).Select(one => one.Target.Variable).Distinct(StringComparer.Ordinal)];
            return ports.Length == 1 ? Port(node, ports[0]) : Fail($"{node.Type} with {ports.Length} inputs");
        }

        /// <summary>The texture an InputTexture hands a SampleInputTexture through its <c>in_texture</c> port.</summary>
        private int? Handed(ShaderNode node)
        {
            ShaderLink[] links = [.. lookup.Into(node).Where(one => one.Target.Variable == "in_texture")];
            if (links.Length != 1 || lookup.Node(links[0].Source) is not { Type: "InputTexture" } source)
            {
                return Fail($"{node.Type} without an InputTexture");
            }

            return Texture(source, Parameter(source, 0));
        }

        private int? Texture(ShaderNode node, ShaderValue said)
            => said.Path.Length > 0
                ? build.Texture(new ShadeTexture(said.Path.Replace('\\', '/').Trim(), said.Srgb ?? false))
                : Fail($"{node.Type} naming no texture");

        /// <summary>
        /// A node's parameter with the material's override laid over it.
        /// </summary>
        /// <remarks>
        /// BY POSITION, because that is how both are written: a node's <c>parameters</c> list and the
        /// material's list under the node's custom name line up entry for entry, and an empty
        /// <c>{}</c> on the material's side leaves the node's own. An entry neither side fills reads
        /// as nought - the files leave out a value that is false or zero, as <c>"UseRoughness":
        /// [{}]</c> beside <c>"Sharpness": [{"value": 1.0}]</c> shows.
        /// </remarks>
        private ShaderValue Parameter(ShaderNode node, int at)
        {
            ShaderValue own = at < node.Parameters.Length ? node.Parameters[at] : ShaderValue.Empty;
            return node.Custom.Length > 0
                && instance.Custom.TryGetValue(node.Custom, out ShaderValue[]? set)
                && at < set.Length
                ? own.Under(set[at])
                : own;
        }

        private int? Fail(string why)
        {
            if (Why.Length == 0)
            {
                Why = why;
            }

            return null;
        }

        /// <summary>A value's numbers as four components: a scalar in all four, a vector in its own.</summary>
        private static Vector4 Vectored(float[] numbers)
        {
            if (numbers.Length == 0)
            {
                return Vector4.Zero;
            }

            if (numbers.Length == 1)
            {
                return new Vector4(numbers[0]);
            }

            var said = Vector4.Zero;
            for (var part = 0; part < 4 && part < numbers.Length; part++)
            {
                said[part] = numbers[part];
            }

            return said;
        }

        /// <summary>
        /// A link's swizzles as one step's: which source component lands in each target component, and which are written.
        /// </summary>
        /// <remarks>
        /// ONE SOURCE COMPONENT IS A SCALAR and lands in every component written; several land in
        /// order. With no target swizzle the whole value is written - a scalar in all four, a
        /// vector in as many as it has, the rest left at nought.
        /// </remarks>
        private static int? Packed(string from, string to)
        {
            int[] source = from.Length == 0 ? [0, 1, 2, 3] : [.. from.Select(Component)];
            if (source.Any(one => one < 0) || source.Length is 0 or > 4)
            {
                return null;
            }

            var select = new int[4];
            var mask = 0;
            if (to.Length > 0)
            {
                for (var at = 0; at < to.Length; at++)
                {
                    int part = Component(to[at]);
                    if (part < 0 || (source.Length > 1 && at >= source.Length))
                    {
                        return null;
                    }

                    select[part] = source.Length == 1 ? source[0] : source[at];
                    mask |= 1 << part;
                }
            }
            else if (source.Length == 1)
            {
                Array.Fill(select, source[0]);
                mask = 0b1111;
            }
            else
            {
                for (var at = 0; at < source.Length; at++)
                {
                    select[at] = source[at];
                    mask |= 1 << at;
                }
            }

            return select[0] | (select[1] << 2) | (select[2] << 4) | (select[3] << 6) | (mask << 8);
        }

        private static int Component(char letter) => letter switch
        {
            'x' or 'r' => 0,
            'y' or 'g' => 1,
            'z' or 'b' => 2,
            'w' or 'a' => 3,
            _ => -1,
        };
    }
}
