using System.Collections.Frozen;
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
/// THE COLOUR, AND WHAT THE COLOUR READS. A graph writes many things - normals, gloss, the
/// indirect light - and this renderer lights with one lamp and the mesh's own normals, so only the
/// colour is drawn. But a later graph may read what an earlier one wrote: AGT_DesertDust lays its
/// dust by the ambient occlusion DielectricSpecGlossBN put in the indirect light's w. So every
/// channel a graph can read is followed, and once the colour is compiled every step it does not
/// depend on is dropped again (see <see cref="Finish"/>) - the indirect light costs nothing where
/// no colour reads it.
///
/// STAGES IN ORDER, GRAPHS IN ORDER WITHIN ONE. A material's graphs read and write named values -
/// <c>InputUV</c> reads the coordinates, <c>UV</c> writes them - at named stages. Every graph
/// writing at UVSetup is run before any reading at Texturing_Init, and within a stage the
/// material's own order decides: TallDune1c writes its colour twice at Texturing_Init and the
/// second, PBRGround's, is the one that stands. Within one graph at one stage every read sees the
/// values as they were before that graph wrote any. See <see cref="Stages"/> for what the order of
/// the stages rests on, and where it rests on nothing.
///
/// NOTHING IS GUESSED. Every node evaluated here is the fragment the game compiles it from, as
/// <c>shaders/renderer/nodes/utilitynodes.ffx</c> writes it - <c>Power</c> is
/// <c>pow(max(abs(base), 1e-7), exp)</c>, <c>Divide</c> gives nought for a nought divisor,
/// <c>SmoothStep</c> is the node's own curve and not HLSL's. A graph whose colour depends on
/// anything else - MaskedContactFade, the depth behind a pixel, a vertex colour this reader does not
/// have - is left out whole and NAMED, and the colour stays what the graphs before it made it. A material none of
/// whose graphs could be evaluated has no program, and is drawn exactly as it was before graphs
/// were read.
///
/// WHERE THE MODEL STANDS. A model is drawn where its own space puts it - at the origin, unturned
/// and unscaled - so its space is the world's: <c>WorldPos</c>, <c>FromVertexWorldPos</c> and
/// <c>InputVertexPosition</c> at either transform stage are its positions, <c>FromVertexNormal</c>
/// and <c>InputVertexNormal</c> its normals, and <c>ModelOrigin</c> the origin with a scale of one.
/// In the game the same tile stands somewhere else, and a pattern laid in world space lies
/// elsewhere on it; it is the same pattern. This is a choice about how the picture is placed, made
/// on purpose and said here, not a claim about the engine. A graph that MOVES the vertices - writes
/// <c>VertexPosition</c> with anything but what it read - makes every position wrong, and those
/// readers are refused after it.
///
/// <c>FromVertexLocalPosition</c> reads <c>semanticsData.uv9</c>, which no graph writes: the engine's
/// own <c>OutputVertexLocalPosition</c> does, as <c>float4(vertex_local_pos, 1)</c>. That the two are
/// paired is not in any file, and rests on three things: the names pair as <c>FromVertexUV</c> and
/// <c>OutputVertexUV</c> do; the macro the output fragment sets, <c>VERTEX_OUTPUT_TEXCOORD9</c>, is
/// what lets that interpolator exist at all; and Dust_lookup's height gradient, tuned per material
/// through it, works in the game. So it is the position with a w of one.
///
/// A PARAMETER LEFT OUT IS ITS FRAGMENT'S DECLARED DEFAULT. The files omit a value that EQUALS the
/// default and write one that differs, nought included: a FitRange whose <c>inputMax</c> (default
/// 255) is written as <c>0.0</c> beside an <c>outputMax</c> (default 255) that is left out; a
/// CombineTbnNormals whose two scales (default 1) are both left out, which as nought would flatten
/// every normal map in the game. For most parameters the default is nought, and nothing changes.
///
/// AN IN-PORT LEFT UNLINKED READS NOUGHT. The fragment language declares no default for an in-port -
/// If's five are bare <c>in float</c> - so whatever the engine hands an unlinked one, it hands every
/// port of that type alike, and the game's own graphs say what that is. TwoMaterialVertexBlend picks
/// one vertex colour channel by its <c>Set_VertexChannel</c> parameter, declared 1 to 4, through
/// three If nodes that each link only some of their branches: only with nought on the unlinked ones
/// does 1 pick x, 2 y, 3 z and 4 w - with anything else the parameter mixes channels, or picks w
/// whatever it says. MASK_TextureStatic leaves TileGroundUVs' <c>bool scroll</c> unlinked, and a
/// static mask that scrolled would not be one. A texture port is not a value and stays refused
/// unlinked; a node with nothing linked into it at all is another question, and a writer like that
/// is still taken to write nothing (see Fed).
///
/// ONLY THE COLOUR'S XYZ IS DRAWN - the renderer runs a program on opaque triangles alone and takes
/// three components - so a colour's w is compiled apart from its xyz: where the w cannot be (a
/// soft particle's fade by the depth behind it) the colour stands and its w is marked unset, and a
/// later graph reading that w is refused, not handed nought.
///
/// <c>InputVertexColor</c> IS THE MESH'S COLOUR STREAM - four bytes a vertex behind bit 1 of the
/// vertex format word (see SkinnedMesh.Colours for how that was established), interpolated and
/// scaled to nought to one. A mesh WITHOUT the stream reads white. The sources write that down twice
/// and differently - the <c>VertexColor</c> extension point's own default is nought, "Legacy assets
/// rely on zero vertex color default value", and the pixel side's <c>InitSemanticsData</c> starts
/// <c>color0</c> at white - so the game settled it: BasicColour on a rope and on book pages with no
/// stream is drawn with their textures, where nought would have made both black (see
/// SkinnedMesh.Colours). <c>FromVertexColor</c> reads the same: it is <c>semanticsData.color0</c>,
/// white from <c>InitSemanticsData</c> and replaced only through COLOR0 - the mesh's own stream by
/// the engine's OutputVertexLocalColor, or what a graph's OutputVertexColor wrote, which is refused.
///
/// A NODE THAT READS THE CLOCK RUNS WITH IT. <c>Time</c> is the game's clock, and MuddleTex, MuddleTex2
/// and RotateUVOld multiply it by a parameter - a scroll, an angle per second. The clock is handed in
/// per drawing (see Clock), and a picture whose program reads it is redrawn as the clock runs, as an
/// animation is - not frozen at an instant of this reader's choosing, and not refused either. With
/// the parameter at nought, each one's declared default, the term is left out and the program does
/// not read the clock at all. <c>FromVertexVariance</c> is a particle's: the fragment hands on
/// <c>uv2.x</c> under <c>PARTICLE_VARIANCE_ENABLED</c> and nought otherwise, and a mesh is not a
/// particle, so it is nought.
///
/// A FLOAT IS ONE NUMBER IN ALL FOUR COMPONENTS, as HLSL widens one: a scalar node's result is
/// its first component spread across the register, which is also what HLSL does to a vector handed
/// to a float input - it keeps the x.
///
/// LINEAR, as the game's shaders are. A texture flagged sRGB is linearised when it is read, the
/// arithmetic runs on light rather than on display values, and the result is put back into sRGB
/// before the renderer's own shading - which is what a plain texture is drawn as.
/// </remarks>
public sealed class ShadeProgram
{
    /// <summary>Most registers one program may use - it lives on the stack while a triangle is drawn.</summary>
    public const int MostRegisters = 192;

    /// <summary>The registers every program starts with: the coordinates, the position and the normal.</summary>
    internal const int Coordinates = 0;

    internal const int Position = 1;

    internal const int Normal = 2;

    /// <summary>The vertex normal as it is interpolated, not normalised - what <c>FromVertexNormal</c> reads.</summary>
    internal const int VertexNormal = 3;

    /// <summary>The vertex colour as it is interpolated, nought to one - what <c>InputVertexColor</c> and <c>FromVertexColor</c> read. See SkinnedMesh.Colours.</summary>
    internal const int VertexColour = 4;

    /// <summary>
    /// The game's clock, <c>time</c>, in all four components - what <c>Time</c> reads and the scrolls and turns multiply.
    /// </summary>
    /// <remarks>
    /// SET PER DRAWING, from MeshPicture.Canvas.Time, the way an animation's frame is: the picture is
    /// redrawn as the clock runs, as a monster is as it plays. In seconds - the sources do not say,
    /// and the parameters do: RotateUVOld's offset runs to 6.28, a turn in radians, beside a turn
    /// rate of up to eighteen, which is a spin a second and not one an hour.
    /// </remarks>
    internal const int Clock = 5;

    /// <summary>How many registers are the mesh's own, before any a program allocates.</summary>
    private const int Fixed = 6;

    /// <summary>
    /// Stands in for the TBN basis, which is a matrix and so in no register - see <see cref="Basis"/>'s use in Transform.
    /// </summary>
    private const int Basis = -2;

    /// <summary>Most registers a compile may allocate before the unused ones are dropped.</summary>
    private const int MostBuilt = MostRegisters * 4;

    /// <summary>How finely the sRGB curve is tabled. A step is under a third of a display level.</summary>
    private const int Table = 4096;

    /// <summary>The ways <see cref="Op.Compare"/> compares, in its step's Extra.</summary>
    private const int Equal = 0;

    private const int Greater = 1;

    private const int Less = 2;

    private const int Both = 3;

    private const int Either = 4;

    private const int Not = 5;

    /// <summary>
    /// The node types the compiler evaluates.
    /// </summary>
    /// <remarks>
    /// THE ONE LIST, checked before the compiler's switch: a type missing here is refused even if the
    /// switch has a case for it, so a node added to one and not the other fails its own test rather
    /// than leaving the graph survey (see <see cref="Knows"/>) reporting it missing after it works.
    /// Every family is listed only in the variants the game's sources define - there is no Round2.
    /// </remarks>
    private static readonly FrozenSet<string> Evaluated = FrozenSet.ToFrozenSet(
        [
            "ConstantPixel", "ConstantPixel2", "ConstantPixel3", "ConstantPixel4",
            "ConstantFloat", "ConstantFloat2", "ConstantFloat3", "ConstantFloat4",
            "ConstantBool", "ConstantPixelBool", "ConstantInt", "ConstantUInt",
            "Zero", "One", "Half", "Two", "Pi", "Epsilon", "ZeroUInt", "OneUInt", "TwoUInt",
            "Dummy", "Dummy2", "Dummy3", "Dummy4", "DummyBool", "DummyInt", "DummyUInt",
            "Add", "Add2", "Add3", "Add4",
            "Subtract", "Subtract2", "Subtract3", "Subtract4",
            "Multiply", "Multiply2", "Multiply3", "Multiply4",
            "Divide", "Divide2", "Divide3", "Divide4",
            "MultiplyConst", "MultiplyConst2", "MultiplyConst3", "MultiplyConst4",
            "AddConst", "AddConst2", "AddConst3", "AddConst4",
            "SubtractConst", "SubtractConst2", "SubtractConst3", "SubtractConst4",
            "MultiplyAdd", "MultiplyAdd2", "MultiplyAdd3", "MultiplyAdd4",
            "Max", "Max2", "Max3", "Max4", "MaxUInt",
            "Min", "Min2", "Min3", "Min4", "MinUInt",
            "Clamp", "Clamp2", "Clamp3", "Clamp4",
            "Fmod", "Fmod2", "Fmod3", "Fmod4",
            "Power", "Step",
            "Negate", "Negate2", "Negate3", "Negate4",
            "OneMinus", "OneMinus2", "OneMinus3", "OneMinus4",
            "Saturate", "Saturate2", "Saturate3", "Saturate4",
            "Abs", "Abs2", "Abs3", "Abs4",
            "Floor", "Floor2", "Floor3", "Floor4",
            "Ceil", "Ceil2", "Ceil3", "Ceil4",
            "Round",
            "Truncate", "Truncate2", "Truncate3", "Truncate4",
            "Frac", "Frac2", "Frac3", "Frac4",
            "Sqrt", "Sqrt2", "Sqrt3", "Sqrt4",
            "Sign", "Sign2", "Sign3", "Sign4",
            "Normalize2", "Normalize3", "Normalize4",
            "DotProduct2", "DotProduct3", "DotProduct4", "Length3", "Luminance", "GrayScale",
            "Lerp", "Lerp2", "Lerp3", "Lerp4",
            "SmoothStep", "CheapSmoothstep", "If", "FitRange", "FitRangeFromInput", "RemapHue", "RemapHueInput",
            "SelectFloat", "SelectFloat2", "SelectFloat3", "SelectFloat4", "SelectBool", "SelectUInt", "SelectChannel",
            "GreaterThan", "LessThan", "EqualsUInt", "GreaterThanUInt", "LessThanUInt", "And", "Or", "Not",
            "Float2ToCoords", "Float3ToCoords", "Float4ToCoords", "CoordsToFloat2", "CoordsToFloat3", "CoordsToFloat4",
            "SampleTexture", "SampleInputTexture", "SampleInputTextureLod", "SampleInputTriplanar",
            "SampleTriplanar", "SampleTexture2", "SampleTextureAtlas2", "SampleTextureLod", "SampleDispersedTexture",
            "MuddleTex", "MuddleTexFromInput", "MuddleTex2", "FromVertexVariance",
            "RGBToTbn", "ScaleUVMaya", "HardLightBlend", "Sine", "RotateUVOld",
            "FromVertexNormal", "FromVertexWorldPos", "FromVertexLocalPosition", "InputVertexPosition", "InputVertexNormal", "InputVertexColor",
            "FromVertexColor", "Time",
            "ModelOrigin", "GroundScroll", "Transform", "LookUpTexture",
            "Noise31", "PerlinNoise31", "Vibrance", "Rotate", "RotateUV", "RadiusToPolarNorm",
        ],
        StringComparer.Ordinal);

    /// <summary>The channels a graph reads and writes, in the order <see cref="Writer"/> numbers them.</summary>
    /// <remarks>
    /// FROM THE FIRST FOLLOWED ON, a channel whose write is left out is LOST rather than left as it
    /// was: no later graph reads a stale indirect light without being named for it. The four before
    /// keep their old value, as they always have - the colour is what is drawn and a left-out write of
    /// it is said; a coordinate, position or normal a graph moves and this could not follow changes
    /// neither.
    /// </remarks>
    private static readonly string[] Channels =
        ["UV", "WorldPos", "WorldNormal", "Albedo", "IndirectColor", "SpecularColor", "EmissiveColor", "SubsurfaceColor", "Glossiness", "TbnNormal", "TbnBasis"];

    private const int FirstFollowed = 4;

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
        ShadeTexture[] textures, int[] sampleTexture, int plain, IReadOnlyList<string> graphs, Mipmaps?[] sheets, bool hasAlpha)
    {
        HasAlpha = hasAlpha;
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
        UsesNormal = result == Normal || steps.Any(one => one.Reads(Normal));
        UsesVertexNormal = result == VertexNormal || steps.Any(one => one.Reads(VertexNormal));
        UsesVertexColour = result == VertexColour || steps.Any(one => one.Reads(VertexColour));
        UsesTime = result == Clock || steps.Any(one => one.Reads(Clock));
    }

    internal enum Op : byte
    {
        Clear,
        Swizzle,
        Add,
        Subtract,
        Multiply,
        Divide,
        Min,
        Max,
        Fmod,
        MultiplyAdd,
        Clamp,
        Power,
        Step,
        Negate,
        OneMinus,
        Saturate,
        Abs,
        Floor,
        Ceil,
        Round,
        Truncate,
        Frac,
        Sqrt,
        Sign,
        Normalize,
        Dot,
        Length,
        Lerp,
        Fit,
        SmoothStep,
        Hermite,
        If,
        Select,
        Compare,
        Pick,
        HueTurn,
        Noise,
        Vibrance,
        Rotate,
        Polar,
        RemapHue,
        Triplanar,
        HardLight,
        Sine,
        Sample,
        SampleLod,
    }

    /// <summary>The stages a colour is assembled across, in the order they are run.</summary>
    /// <remarks>
    /// THE ORDER IS NOT IN THE GAME'S SHADER SOURCES - no file lists it - so it is only as good as
    /// what each step of it rests on:
    /// <list type="bullet">
    /// <item>coordinates are set up before textures are read with them, and the texturing is done
    /// before the lighting's own pass over the colour - the stage FAMILIES in order;</item>
    /// <item>a family's <c>_Init</c> pass comes before the rest of it: AGT_DesertDust at
    /// Texturing_Calc lays dust over the colour DielectricSpecGlossBN wrote at Texturing_Init, and
    /// ParallaxUvSpaceContactFade at Texturing_Final fades the alpha that same graph wrote;</item>
    /// <item>nothing says how the plain stage, <c>_Calc</c> and <c>_Final</c> of one family fall
    /// against each other. They are run in that order, and where it would matter - a write reads
    /// what one at another of them writes, or both write one channel - that write is left out and
    /// named rather than placed by a guess (see <see cref="Unordered"/>).</item>
    /// </list>
    /// A colour written at any stage not here is left out and named.
    /// </remarks>
    public static readonly IReadOnlyList<string> Stages =
    [
        "VertexInit",
        "UVSetup", "UVSetup_Calc", "UVSetup_Final",
        "Texturing_Init", "Texturing", "Texturing_Calc", "Texturing_Final",
        "PreLighting", "PreLighting_Calc", "PreLighting_Final",
    ];

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

    /// <summary>Whether a run reads the vertex normal as it is interpolated.</summary>
    internal bool UsesVertexNormal { get; }

    /// <summary>Whether a run reads the vertex colour - white where the mesh has no colour stream.</summary>
    public bool UsesVertexColour { get; }

    /// <summary>Whether a run reads the game's clock - then the picture changes as it runs, and is redrawn as an animation is.</summary>
    public bool UsesTime { get; }

    /// <summary>
    /// Whether the graphs set the colour's alpha, which a cut-out shape is then cut on instead of its texture's.
    /// </summary>
    /// <remarks>
    /// THE ENGINE CUTS ON THE FINAL ALBEDO, not on a texture: AlphaTestClipping passes
    /// <c>albedo_color.a</c> to PerformAlphaTestClip. The case that showed it is
    /// transparentobjectsc.mat - ForceAlphaTest over a graph that writes Zero to the colour, alpha and
    /// all, and no texture at all - an invisible helper the game never shows, which a cut taken from
    /// the texture alone could not drop, and drew as a black slab under the ship in Port's boss room.
    /// </remarks>
    public bool HasAlpha { get; }

    /// <summary>
    /// Whether a graph node of this type is something the compiler can evaluate - for the graph survey.
    /// </summary>
    /// <remarks>
    /// The readers of the values it tracks count, and so does <c>InputTexture</c>, which is never
    /// compiled on its own but read through the <c>SampleInputTexture</c> it feeds.
    /// </remarks>
    public static bool Knows(string type)
        => type is not null && (Evaluated.Contains(type) || ReadBy(type) is not null || type == "InputTexture");

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

        // WHAT EACH NAMED VALUE HOLDS NOW, by register, and which of its components nobody has set.
        // The colour starts unwritten: a graph that reads it before any has written it has nothing to
        // read. The rest start as the engine's InitSurface and InitMaterial leave them, where every
        // lighting model leaves them alike - the indirect light's w is the material's own ambient
        // occlusion, a uniform this has not got, and the specular, emissive and gloss differ between
        // the models (texturing.ffx), so those are unset. The TBN basis is the model's own, which
        // ParallaxUvSpaceContactFade hands on as "model_tbn_basis".
        var state = new Dictionary<string, Held>(StringComparer.Ordinal)
        {
            ["UV"] = new(Coordinates, 0),
            ["WorldPos"] = new(Position, 0),
            ["WorldNormal"] = new(Normal, 0),
            ["IndirectColor"] = new(build.Constant(Vector4.Zero), 0b1000),
            ["SubsurfaceColor"] = new(build.Constant(Vector4.Zero), 0),
            ["TbnNormal"] = new(build.Constant(new Vector4(0f, 0f, 1f, 0f)), 0),
            ["TbnBasis"] = new(Basis, 0),
        };

        // A FOLLOWED CHANNEL WHOSE WRITE WAS LEFT OUT, by the graph that wrote it - see Channels.
        var lost = new Dictionary<string, string>(StringComparer.Ordinal);

        var lookups = new Lookup[chain.Count];
        var writers = new List<Writer>[chain.Count, Stages.Count];
        var every = new List<Writer>();
        var moved = new Displaced(null, null);
        for (var link = 0; link < chain.Count; link++)
        {
            lookups[link] = new Lookup(chain[link].Graph);
            foreach (ShaderNode node in chain[link].Graph.Nodes)
            {
                // A VERTEX STAGE IS NOT RUN, but a graph that writes the position or the normal there
                // with anything but what it read has moved the mesh, and what the pixel side reads of
                // either is not the mesh's any more - see the class's remarks.
                if (node.Type is "VertexPosition" or "VertexNormal" && lookups[link].Fed(node) && !PassedThrough(lookups[link], node))
                {
                    string by = Named(chain[link].Instance.Parent);
                    moved = node.Type == "VertexPosition" ? moved with { Position = by } : moved with { Normal = by };
                }

                // AND ONE THAT WRITES COLOR0 HAS REPLACED WHAT FromVertexColor READS: the fragment
                // OutputVertexColor sets semanticsData.color0 at the vertex stage, which is not run.
                if (node.Type == "OutputVertexColor" && lookups[link].Fed(node))
                {
                    moved = moved with { Colour = Named(chain[link].Instance.Parent) };
                }

                int at = IndexOf(node.Stage);
                if (at < 0 || Written(node.Type) is not { } channel || !lookups[link].Fed(node))
                {
                    continue;
                }

                var writer = new Writer(node, at, Array.IndexOf(Channels, channel), Reads(lookups[link], node), Same(lookups[link], node, channel));
                (writers[link, at] ??= []).Add(writer);
                every.Add(writer);
            }
        }

        for (var at = 0; at < Stages.Count; at++)
        {
            string stage = Stages[at];
            for (var link = 0; link < chain.Count; link++)
            {
                if (writers[link, at] is not { } mine)
                {
                    continue;
                }

                // EVERY READ IN ONE GRAPH AT ONE STAGE SEES THE VALUES FROM BEFORE IT - see the remarks
                // - so the snapshot is taken once, and one unit serves every write, sharing what they share.
                (ShaderInstance instance, _) = chain[link];
                string named = Named(instance.Parent);
                var seen = new Dictionary<string, Held>(state, StringComparer.Ordinal);
                var unit = new Unit(build, lookups[link], instance, seen, lost, moved);
                var wrote = new List<(string Channel, int Register, int Unset, bool Alpha)>();
                foreach (Writer writer in mine)
                {
                    string channel = Channels[writer.Channel];
                    string what = channel == "UV" ? $"{named}'s coordinates" : named;
                    string why;
                    if (Unordered(every, writer) is { } against)
                    {
                        why = $"{what} at {stage}, whose order against {against} is not known";
                    }
                    else
                    {
                        Builder.Mark mark = build.Marked();
                        unit.Start();
                        if (channel == "Albedo")
                        {
                            // THE COLOUR'S W APART FROM ITS XYZ - see the class's remarks.
                            if (unit.Colour(writer.Node) is { } colour && build.Fits)
                            {
                                wrote.Add((channel, colour.Register, colour.Unset, colour.Alpha));
                                continue;
                            }
                        }
                        else if (unit.Port(writer.Node, "input") is { } register && build.Fits && (channel == "TbnBasis") == (register == Basis))
                        {
                            wrote.Add((channel, register, 0, true));
                            continue;
                        }

                        string stopped = !build.Fits ? "too many steps" : unit.Why.Length > 0 ? unit.Why : $"{writer.Node.Type} not from the basis it read";
                        why = $"{stopped} in {what}";
                        build.Back(mark);
                        unit.Forget(mark.Next);
                    }

                    // THE COLOUR AND THE COORDINATES ARE SAID WHEN LEFT OUT: one is what is drawn, the
                    // other is where every texture after it is read. A followed channel is lost.
                    if (channel is "Albedo" or "UV")
                    {
                        skipped.Add(why);
                    }
                    else if (writer.Channel >= FirstFollowed)
                    {
                        wrote.Add((channel, int.MinValue, 0, true));
                    }
                }

                // IN THE GRAPH'S OWN ORDER, so a second write of one channel stands over the first.
                foreach ((string channel, int register, int unset, bool alpha) in wrote)
                {
                    if (register == int.MinValue)
                    {
                        state.Remove(channel);
                        lost[channel] = named;
                        continue;
                    }

                    state[channel] = new Held(register, unset, alpha);
                    lost.Remove(channel);
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
                if (node.Type == "AlbedoColor" && IndexOf(node.Stage) < 0 && lookups[link].Fed(node))
                {
                    skipped.Add($"AlbedoColor at {node.Stage} in {Named(chain[link].Instance.Parent)}");
                }
            }
        }

        if (!state.TryGetValue("Albedo", out Held albedo))
        {
            return new ShadeCompile(null, skipped);
        }

        // THE COLOUR'S W IS ITS ALPHA where the graphs set it - a texture's own, or a constant's - and
        // the alpha test cuts on it: see HasAlpha.
        ShadeProgram? program = Finish(build, albedo.Register, graphs, albedo.Alpha && (albedo.Unset & 0b1000) == 0);
        if (program is null)
        {
            skipped.Add($"more than {MostRegisters} registers in the colour");
        }

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
            [.. Textures], _sampleTexture, Plain, Graphs, bound, HasAlpha);
    }

    /// <summary>
    /// Puts the constants in their registers - once per triangle, since nothing writes them.
    /// </summary>
    internal void Preset(Span<Vector4> registers, float time = 0f)
    {
        registers[Clock] = new Vector4(time);
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
    /// <param name="normal">The pixel's normal in the model's space, interpolated and not yet normalised.</param>
    /// <param name="levels">The level each texture read takes, from <see cref="Levels"/>.</param>
    /// <param name="vertexColour">The pixel's vertex colour, interpolated, nought to one; read only where <see cref="UsesVertexColour"/>.</param>
    internal Vector3 Colour(
        Span<Vector4> registers, Vector2 coordinates, Vector3 position, Vector3 normal, ReadOnlySpan<float> levels, Vector4 vertexColour = default)
        => Colour(registers, coordinates, position, normal, levels, out _, vertexColour);

    /// <summary>
    /// The colour at one pixel, and the alpha the graphs left in its w - what the engine's alpha test cuts on.
    /// </summary>
    /// <remarks>
    /// THE ALPHA AS THE GRAPHS LEFT IT, not encoded: the engine's AlphaTestClipping fragment hands
    /// <c>albedo_color.a</c> to PerformAlphaTestClip, which clips on <c>alpha - cutoff</c>. Meaningful
    /// only where <see cref="HasAlpha"/>.
    /// </remarks>
    internal Vector3 Colour(
        Span<Vector4> registers, Vector2 coordinates, Vector3 position, Vector3 normal, ReadOnlySpan<float> levels,
        out float alpha, Vector4 vertexColour = default)
    {
        registers[Coordinates] = new Vector4(coordinates, 0f, 0f);
        registers[Position] = new Vector4(position, 0f);
        if (UsesVertexColour)
        {
            registers[VertexColour] = vertexColour;
        }

        if (UsesNormal)
        {
            registers[Normal] = new Vector4(normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : normal, 0f);
        }

        if (UsesVertexNormal)
        {
            registers[VertexNormal] = new Vector4(normal, 0f);
        }

        Run(registers, levels, default, -1);

        Vector4 colour = registers[_result];
        alpha = colour.W;
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
        Span<float> levels,
        ReadOnlySpan<Vector4> colours = default)
    {
        for (var corner = 0; corner < 3; corner++)
        {
            registers[Coordinates] = new Vector4(coordinates[corner], 0f, 0f);
            registers[Position] = new Vector4(positions[corner], 0f);
            registers[Normal] = new Vector4(normals[corner], 0f);
            registers[VertexNormal] = new Vector4(normals[corner], 0f);
            registers[VertexColour] = colours.Length == 3 ? colours[corner] : default;
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

                case Op.Divide:
                {
                    Vector4 a = r[step.A];
                    Vector4 b = r[step.B];
                    r[step.To] = new Vector4(Divided(a.X, b.X), Divided(a.Y, b.Y), Divided(a.Z, b.Z), Divided(a.W, b.W));
                    break;
                }

                case Op.Min:
                    r[step.To] = Vector4.Min(r[step.A], r[step.B]);
                    break;

                case Op.Max:
                    r[step.To] = Vector4.Max(r[step.A], r[step.B]);
                    break;

                case Op.Fmod:
                {
                    Vector4 a = r[step.A];
                    Vector4 b = r[step.B];
                    r[step.To] = new Vector4(a.X % b.X, a.Y % b.Y, a.Z % b.Z, a.W % b.W);
                    break;
                }

                case Op.MultiplyAdd:
                    r[step.To] = (r[step.A] * r[step.B]) + r[step.C];
                    break;

                case Op.Clamp:
                    r[step.To] = Vector4.Min(Vector4.Max(r[step.A], r[step.B]), r[step.C]);
                    break;

                case Op.Power:
                    r[step.To] = new Vector4(MathF.Pow(MathF.Max(MathF.Abs(r[step.A].X), 1e-7f), r[step.B].X));
                    break;

                case Op.Step:
                    r[step.To] = new Vector4(r[step.B].X >= r[step.A].X ? 1f : 0f);
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

                case Op.Abs:
                    r[step.To] = Vector4.Abs(r[step.A]);
                    break;

                case Op.Floor:
                {
                    Vector4 v = r[step.A];
                    r[step.To] = new Vector4(MathF.Floor(v.X), MathF.Floor(v.Y), MathF.Floor(v.Z), MathF.Floor(v.W));
                    break;
                }

                case Op.Ceil:
                {
                    Vector4 v = r[step.A];
                    r[step.To] = new Vector4(MathF.Ceiling(v.X), MathF.Ceiling(v.Y), MathF.Ceiling(v.Z), MathF.Ceiling(v.W));
                    break;
                }

                case Op.Round:
                {
                    // TO EVEN AT A HALF, as the compiled round_ne does.
                    Vector4 v = r[step.A];
                    r[step.To] = new Vector4(MathF.Round(v.X), MathF.Round(v.Y), MathF.Round(v.Z), MathF.Round(v.W));
                    break;
                }

                case Op.Truncate:
                {
                    Vector4 v = r[step.A];
                    r[step.To] = new Vector4(MathF.Truncate(v.X), MathF.Truncate(v.Y), MathF.Truncate(v.Z), MathF.Truncate(v.W));
                    break;
                }

                case Op.Frac:
                {
                    Vector4 v = r[step.A];
                    r[step.To] = v - new Vector4(MathF.Floor(v.X), MathF.Floor(v.Y), MathF.Floor(v.Z), MathF.Floor(v.W));
                    break;
                }

                case Op.Sqrt:
                    r[step.To] = Vector4.SquareRoot(r[step.A]);
                    break;

                case Op.Sign:
                {
                    Vector4 v = r[step.A];
                    r[step.To] = new Vector4(Signed(v.X), Signed(v.Y), Signed(v.Z), Signed(v.W));
                    break;
                }

                case Op.Normalize:
                    r[step.To] = Normalized(r[step.A], step.Extra);
                    break;

                case Op.Dot:
                    r[step.To] = new Vector4(Dotted(r[step.A], r[step.B], step.Extra));
                    break;

                case Op.Length:
                {
                    Vector4 v = r[step.A];
                    r[step.To] = new Vector4(MathF.Sqrt(Dotted(v, v, step.Extra)));
                    break;
                }

                case Op.Lerp:
                    // THE ALPHA IS A FLOAT in every Lerp, the vector ones too, so its x is all of it.
                    r[step.To] = r[step.A] + ((r[step.B] - r[step.A]) * r[step.C].X);
                    break;

                case Op.Fit:
                    r[step.To] = new Vector4(Fitted(r[step.A].X, r[step.B].X, r[step.C].X, r[step.D].X, r[step.E].X));
                    break;

                case Op.SmoothStep:
                    r[step.To] = new Vector4(Smoothed(r[step.A].X, r[step.B].X, r[step.C].X));
                    break;

                case Op.Hermite:
                    r[step.To] = new Vector4(Hermite(r[step.A].X, r[step.B].X, r[step.C].X));
                    break;

                case Op.If:
                {
                    // NOT A NUMBER IS NEITHER GREATER NOR LESSER, so it takes the equal branch, as the node's
                    // if / else if / else does.
                    float a = r[step.A].X;
                    float b = r[step.B].X;
                    r[step.To] = new Vector4(a > b ? r[step.C].X : a < b ? r[step.E].X : r[step.D].X);
                    break;
                }

                case Op.Select:
                    r[step.To] = r[step.C].X != 0f ? r[step.B] : r[step.A];
                    break;

                case Op.Compare:
                    r[step.To] = new Vector4(Compared(r[step.A].X, step.B >= 0 ? r[step.B].X : 0f, step.Extra) ? 1f : 0f);
                    break;

                case Op.Pick:
                {
                    Vector4 v = r[step.A];
                    float index = r[step.B].X;
                    r[step.To] = new Vector4(index == 0f ? v.X : index == 1f ? v.Y : index == 2f ? v.Z : index == 3f ? v.W : 0f);
                    break;
                }

                case Op.HueTurn:
                    r[step.To] = Turn(r[step.A].X, r[step.B].X, r[step.C].X);
                    break;

                case Op.RemapHue:
                    r[step.To] = Remapped(r[step.A], r[step.B]);
                    break;

                case Op.Noise:
                {
                    var at = new Vector3(r[step.A].X, r[step.A].Y, r[step.A].Z);
                    r[step.To] = new Vector4(step.Extra == 0 ? ValueNoise(at) : PerlinNoise(at));
                    break;
                }

                case Op.Vibrance:
                    r[step.To] = Vibrant(r[step.A].X, r[step.B]);
                    break;

                case Op.Rotate:
                    r[step.To] = Rotated(r[step.A].X, r[step.B], step.C >= 0 ? r[step.C] : Vector4.Zero);
                    break;

                case Op.Polar:
                    r[step.To] = Polar(r[step.A]);
                    break;

                case Op.Triplanar:
                    r[step.To] = Triplanar(r[step.A], r[step.B], r[step.C], r[step.D], step.Extra != 0);
                    break;

                case Op.HardLight:
                    r[step.To] = HardLit(r[step.A], r[step.B]);
                    break;

                case Op.Sine:
                {
                    Vector4 v = r[step.A];
                    r[step.To] = new Vector4(MathF.Sin(v.X), MathF.Sin(v.Y), MathF.Sin(v.Z), MathF.Sin(v.W));
                    break;
                }

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

            if (step.Splat)
            {
                r[step.To] = new Vector4(r[step.To].X);
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

        // A LEVEL THAT IS NOT A NUMBER READS THE TOP ONE rather than an index nowhere.
        Vector4 texel = MeshPicture.Sample4(sheet, spot, level > 0f ? MathF.Min(level, sheet.Count - 1) : 0f);
        return Textures[texture].Srgb
            ? new Vector4(Linear(texel.X), Linear(texel.Y), Linear(texel.Z), texel.W)
            : texel;
    }

    /// <summary>The program with every step the colour does not depend on dropped, and its registers, reads and textures renumbered.</summary>
    /// <remarks>
    /// WHY IT IS NEEDED. Every channel is compiled as it is written, before anything knows whether a
    /// colour will read it: DielectricSpecGlossBN's indirect light reads its NormalGlossAO texture,
    /// and a material with nothing after it would decode that texture and read it on every pixel for
    /// nothing. Walking back from the colour keeps exactly what it reads, and the textures only the
    /// dropped steps read are never named, so never loaded.
    /// </remarks>
    private static ShadeProgram? Finish(Builder build, int result, List<string> graphs, bool hasAlpha)
    {
        List<Step> steps = build.Steps;
        var live = new bool[build.Next];
        live[result] = true;
        var keep = new bool[steps.Count];
        for (int at = steps.Count - 1; at >= 0; at--)
        {
            Step step = steps[at];
            if (!live[step.To])
            {
                continue;
            }

            // A SWIZZLE WRITES PART OF ITS REGISTER, so the register stays live for the steps before
            // that wrote the rest; nothing else writes a register twice.
            keep[at] = true;
            Live(live, step.A);
            if (step.Op != Op.Sample)
            {
                Live(live, step.B);
            }

            Live(live, step.C);
            Live(live, step.D);
            Live(live, step.E);
        }

        var map = new int[build.Next];
        Array.Fill(map, -1);
        for (var one = 0; one < Fixed; one++)
        {
            map[one] = one;
        }

        int next = Fixed;
        var constantAt = new List<int>();
        var constants = new List<Vector4>();
        for (var one = 0; one < build.ConstantAt.Count; one++)
        {
            int at = build.ConstantAt[one];
            if (live[at])
            {
                map[at] = next++;
                constantAt.Add(map[at]);
                constants.Add(build.Constants[one]);
            }
        }

        var textureMap = new int[build.Textures.Count];
        Array.Fill(textureMap, -1);
        var textures = new List<ShadeTexture>();
        var sampleTextures = new List<int>();
        var kept = new List<Step>();
        for (var at = 0; at < steps.Count; at++)
        {
            if (!keep[at])
            {
                continue;
            }

            Step step = steps[at];
            if (map[step.To] < 0)
            {
                map[step.To] = next++;
            }

            int b = step.B;
            int extra = step.Extra;
            if (step.Op is Op.Sample or Op.SampleLod)
            {
                if (textureMap[extra] < 0)
                {
                    textureMap[extra] = textures.Count;
                    textures.Add(build.Textures[extra]);
                }

                extra = textureMap[extra];
                if (step.Op == Op.Sample)
                {
                    b = sampleTextures.Count;
                    sampleTextures.Add(extra);
                }
                else
                {
                    b = Mapped(map, b);
                }
            }
            else
            {
                b = Mapped(map, b);
            }

            kept.Add(new Step(
                step.Op, map[step.To], Mapped(map, step.A), b, Mapped(map, step.C), Mapped(map, step.D), Mapped(map, step.E), extra, step.Splat));
        }

        if (next > MostRegisters)
        {
            return null;
        }

        int plain = build.PlainOf(result);
        ShadeTexture[] named = [.. textures];
        return new ShadeProgram(
            [.. kept], [.. constantAt], [.. constants], next, map[result],
            named, [.. sampleTextures], plain >= 0 ? textureMap[plain] : -1, graphs, new Mipmaps?[named.Length], hasAlpha);
    }

    private static void Live(bool[] live, int register)
    {
        if (register >= 0)
        {
            live[register] = true;
        }
    }

    private static int Mapped(int[] map, int register) => register >= 0 ? map[register] : register;

    /// <summary>
    /// The stage of a write this one's outcome hangs on though nothing says which runs first; else null.
    /// </summary>
    /// <remarks>
    /// TWO STAGES ARE UNORDERED when they are of one family and neither is its <c>_Init</c> - see
    /// <see cref="Stages"/>. Between two writes at such stages the order matters where one READS
    /// what the other writes - a graph at Texturing_Calc laying something over a colour a graph at
    /// Texturing writes comes out differently the other way round - and where both write one channel,
    /// since whichever is last stands. Then the reading write, or the later of the two, is left out
    /// and named rather than placed by a guess. Nothing else is held back: AddDetailMap at Texturing
    /// adds to the normal, AGT_DesertDust at Texturing_Calc reads and writes the normal too, and the
    /// dust's colour, which reads no normal, is not the normal's business.
    ///
    /// A WRITE OF WHAT WAS READ, unchanged - <c>UV</c> fed straight from <c>InputUV</c>, as half
    /// the graphs do to keep a channel they do not touch - comes to the same in either order, and
    /// counts for neither side.
    /// </remarks>
    private static string? Unordered(List<Writer> every, Writer writer)
    {
        string stage = Stages[writer.Stage];
        if (writer.Same || Initial(stage))
        {
            return null;
        }

        string family = Family(stage);
        foreach (Writer other in every)
        {
            string theirs = Stages[other.Stage];
            if (other.Stage == writer.Stage || other.Same || Initial(theirs)
                || !string.Equals(Family(theirs), family, StringComparison.Ordinal))
            {
                continue;
            }

            if ((writer.Reads & (1 << other.Channel)) != 0 || (other.Channel == writer.Channel && other.Stage < writer.Stage))
            {
                return theirs;
            }
        }

        return null;
    }

    /// <summary>The channels a write's value is worked out from, as bits of <see cref="Channels"/>: every reader behind it.</summary>
    private static int Reads(Lookup lookup, ShaderNode writer)
    {
        var reads = 0;
        var seen = new HashSet<ShaderNode>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<ShaderNode>();
        pending.Push(writer);
        while (pending.Count > 0)
        {
            foreach (ShaderLink link in lookup.Into(pending.Pop()))
            {
                if (lookup.Node(link.Source) is not { } source || !seen.Add(source))
                {
                    continue;
                }

                if (ReadBy(source.Type) is { } channel)
                {
                    reads |= 1 << Array.IndexOf(Channels, channel);
                }

                pending.Push(source);
            }
        }

        return reads;
    }

    /// <summary>Whether a write is of its own channel's reader, whole and unchanged.</summary>
    private static bool Same(Lookup lookup, ShaderNode writer, string channel)
    {
        IReadOnlyList<ShaderLink> into = lookup.Into(writer);
        return into.Count == 1
            && into[0].Source.Swizzle.Length == 0
            && into[0].Target.Swizzle.Length == 0
            && lookup.Node(into[0].Source) is { } source
            && ReadBy(source.Type) == channel;
    }

    /// <summary>Whether a vertex-stage write hands on exactly what its own reader gave it, component for component.</summary>
    private static bool PassedThrough(Lookup lookup, ShaderNode writer)
    {
        foreach (ShaderLink link in lookup.Into(writer))
        {
            if (!string.Equals(link.Source.Type, "Input" + writer.Type, StringComparison.Ordinal)
                || !string.Equals(link.Source.Swizzle, link.Target.Swizzle, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether a link's target swizzle names the w alone - the alpha of a colour, which is not drawn.</summary>
    public static bool AlphaOnly(string swizzle)
    {
        if (swizzle.Length == 0)
        {
            return false;
        }

        foreach (char letter in swizzle)
        {
            if (letter is not ('w' or 'a'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Initial(string stage) => stage.EndsWith("_Init", StringComparison.Ordinal);

    private static string Family(string stage)
    {
        int cut = stage.IndexOf('_', StringComparison.Ordinal);
        return cut > 0 ? stage[..cut] : stage;
    }

    private static int IndexOf(string stage)
    {
        for (var at = 0; at < Stages.Count; at++)
        {
            if (string.Equals(Stages[at], stage, StringComparison.Ordinal))
            {
                return at;
            }
        }

        return -1;
    }

    private static float Divided(float a, float b) => MathF.Abs(b) > 0f ? a / b : 0f;

    private static float Signed(float value) => value > 0f ? 1f : value < 0f ? -1f : 0f;

    private static float Dotted(Vector4 a, Vector4 b, int width) => width switch
    {
        2 => (a.X * b.X) + (a.Y * b.Y),
        3 => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z),
        _ => Vector4.Dot(a, b),
    };

    /// <summary>The Normalize nodes': <c>input / (length(input) + 1e-7)</c>, over as many components as the node has.</summary>
    private static Vector4 Normalized(Vector4 value, int width)
    {
        Vector4 part = width switch
        {
            2 => new Vector4(value.X, value.Y, 0f, 0f),
            3 => new Vector4(value.X, value.Y, value.Z, 0f),
            _ => value,
        };
        return part / (MathF.Sqrt(Vector4.Dot(part, part)) + 1e-7f);
    }

    /// <summary>
    /// FitRange's and FitRangeFromInput's: a value moved from one range into another, not clamped.
    /// </summary>
    /// <remarks>An empty input range divides by one, as the node does, rather than by nothing.</remarks>
    private static float Fitted(float value, float inMin, float inMax, float outMin, float outMax)
    {
        float divisor = inMax - inMin;
        return ((value - inMin) * (outMax - outMin) / (MathF.Abs(divisor) > 0f ? divisor : 1f)) + outMin;
    }

    /// <summary>The SmoothStep node's own curve, not HLSL's smoothstep - see utilitynodes.ffx.</summary>
    private static float Smoothed(float center, float steepness, float value)
    {
        float power = Math.Clamp((2f / MathF.Max(1e-3f, 1f - steepness)) - 1f, -1e5f, 1e5f);
        float said = 1f - center
            + ((-Pow(Saturated((1f - MathF.Max(center, value)) / (1f - center)), power) * (1f - center))
               + (Pow(Saturated(MathF.Min(center, value) / center), power) * center));

        // THE NODE'S OWN GUARD: "handling edge case of pow(0, 0) returning NaN".
        return float.IsNaN(said) ? center : said;
    }

    /// <summary>CheapSmoothstep's: HLSL's own smoothstep, the cubic between two edges.</summary>
    /// <remarks>
    /// <c>t = saturate((x - min) / (max - min)); t * t * (3 - 2 * t)</c>, as HLSL defines it. Equal
    /// edges divide by nothing, and saturate makes of that what a graphics card does: one above the
    /// edge, nought below it and nought at it, where nought by nought is not a number.
    /// </remarks>
    private static float Hermite(float low, float high, float value)
    {
        float t = Saturated((value - low) / (high - low));
        return t * t * (3f - (2f * t));
    }

    /// <summary>
    /// pow as a graphics card works it out - exp2(y * log2(x)) - which is where SmoothStep's guard comes from.
    /// </summary>
    /// <remarks>
    /// NOUGHT TO THE NOUGHT IS NOT A NUMBER THERE, where .NET says one; the node relies on that to
    /// fall back to its centre, so it is kept. A negative base has no logarithm and is not a number either.
    /// </remarks>
    private static float Pow(float x, float y)
    {
        if (x > 0f)
        {
            return MathF.Pow(x, y);
        }

        if (x < 0f)
        {
            return float.NaN;
        }

        return y > 0f ? 0f : y < 0f ? float.PositiveInfinity : float.NaN;
    }

    /// <summary>saturate as HLSL has it: nought for what is not a number.</summary>
    private static float Saturated(float value) => value > 0f ? (value < 1f ? value : 1f) : 0f;

    private static bool Compared(float a, float b, int how) => how switch
    {
        Equal => a == b,
        Greater => a > b,
        Less => a < b,
        Both => a != 0f && b != 0f,
        Either => a != 0f || b != 0f,
        Not => a == 0f,
        _ => false,
    };

    /// <summary>
    /// RemapHueHelper's per-pixel constants: the angle's cosine and sine, the saturation doubled and the brightness made -1..1.
    /// </summary>
    /// <remarks>THE GAME'S OWN PI, 3.1425, is kept: the hue turns by what the shader turns it by.</remarks>
    private static Vector4 Turn(float hue, float saturation, float brightness)
    {
        float angle = hue / 180f * 3.1425f;
        return new Vector4(MathF.Cos(angle), MathF.Sin(angle), saturation * 2f, (brightness * 2f) - 1f);
    }

    /// <summary>RemapHueHelper, from utilitynodes.ffx, over the constants <see cref="Turn"/> works out.</summary>
    private static Vector4 Remapped(Vector4 colour, Vector4 turn)
    {
        const float k = 0.57735f;
        var c = new Vector3(colour.X, colour.Y, colour.Z);
        float cos = turn.X;

        // cross(k, c) with every component of k alike.
        var cross = new Vector3(k * (c.Z - c.Y), k * (c.X - c.Z), k * (c.Y - c.X));
        Vector3 hue = (c * cos) + (cross * turn.Y) + (new Vector3(k) * (k * (c.X + c.Y + c.Z)) * (1f - cos));
        Vector3 bright = hue + new Vector3(turn.W);
        float intensity = Vector3.Dot(bright, new Vector3(0.299f, 0.587f, 0.114f));
        Vector3 said = new Vector3(intensity) + ((bright - new Vector3(intensity)) * turn.Z);
        return new Vector4(said, 0f);
    }

    /// <summary>Noise31: <c>vnoise31</c> from the <c>noises</c> declarations - value noise over <c>hash33</c>, smoothed.</summary>
    /// <remarks>
    /// THE X ALONE is kept of every corner's hash: the node ends in <c>.x</c>, and every lerp before it
    /// works component by component, so the other two never reach it.
    /// </remarks>
    private static float ValueNoise(Vector3 x)
    {
        var p = new Vector3(MathF.Floor(x.X), MathF.Floor(x.Y), MathF.Floor(x.Z));
        Vector3 f = x - p;
        f = f * f * (new Vector3(3f) - (2f * f));
        float Lerp(float a, float b, float t) => a + (t * (b - a));
        float Corner(float dx, float dy, float dz) => Hash33X(p + new Vector3(dx, dy, dz));
        return Lerp(
            Lerp(Lerp(Corner(0f, 0f, 0f), Corner(1f, 0f, 0f), f.X), Lerp(Corner(0f, 1f, 0f), Corner(1f, 1f, 0f), f.X), f.Y),
            Lerp(Lerp(Corner(0f, 0f, 1f), Corner(1f, 0f, 1f), f.X), Lerp(Corner(0f, 1f, 1f), Corner(1f, 1f, 1f), f.X), f.Y),
            f.Z);
    }

    /// <summary>The x of <c>hash33</c>: <c>frac((p3.x + p3.y) * p3.z)</c> once p3 is scrambled as the declaration does.</summary>
    private static float Hash33X(Vector3 p3)
    {
        p3 *= new Vector3(0.1031f, 0.1030f, 0.0973f);
        p3 -= new Vector3(MathF.Floor(p3.X), MathF.Floor(p3.Y), MathF.Floor(p3.Z));
        float d = (p3.X * (p3.Y + 19.19f)) + (p3.Y * (p3.X + 19.19f)) + (p3.Z * (p3.Z + 19.19f));
        p3 += new Vector3(d);
        float h = (p3.X + p3.Y) * p3.Z;
        return h - MathF.Floor(h);
    }

    /// <summary>PerlinNoise31: the x of <c>GetPerlinNoise3</c> - gradients from <c>hash33UintPcg</c> on the cell, in the declaration's order.</summary>
    private static float PerlinNoise(Vector3 pos)
    {
        int bx = (int)MathF.Floor(pos.X), by = (int)MathF.Floor(pos.Y), bz = (int)MathF.Floor(pos.Z);
        Vector3 ratio = pos - new Vector3(MathF.Floor(pos.X), MathF.Floor(pos.Y), MathF.Floor(pos.Z));
        Vector3 r2 = ratio * ratio;
        Vector3 r3 = r2 * ratio;
        ratio = (3f * r2) - (2f * r3);
        float ix = 1f - ratio.X, iy = 1f - ratio.Y, iz = 1f - ratio.Z;

        float res = 0f;
        res += Gradient(bx, by, bz, pos) * ix * iy * iz;
        res += Gradient(bx + 1, by, bz, pos) * ratio.X * iy * iz;
        res += Gradient(bx + 1, by + 1, bz, pos) * ratio.X * ratio.Y * iz;
        res += Gradient(bx, by + 1, bz, pos) * ix * ratio.Y * iz;
        res += Gradient(bx, by, bz + 1, pos) * ix * iy * ratio.Z;
        res += Gradient(bx + 1, by, bz + 1, pos) * ratio.X * iy * ratio.Z;
        res += Gradient(bx + 1, by + 1, bz + 1, pos) * ratio.X * ratio.Y * ratio.Z;
        res += Gradient(bx, by + 1, bz + 1, pos) * ix * ratio.Y * ratio.Z;

        // GetPerlinNoiseRange(3) is sqrt(3) / 2.
        return (res / (MathF.Sqrt(3f) / 2f) * 0.5f) + 0.5f;
    }

    /// <summary>The x of <c>GetGradientSample</c>: the offset from the cell along its hashed, normalised gradient.</summary>
    private static float Gradient(int cx, int cy, int cz, Vector3 pos)
    {
        (uint hx, uint hy, uint hz) = Pcg(unchecked((uint)cx), unchecked((uint)cy), unchecked((uint)cz));
        var grad = new Vector3(HashUnit(hx) - 0.5f, HashUnit(hy) - 0.5f, HashUnit(hz) - 0.5f);
        grad /= MathF.Sqrt(Vector3.Dot(grad, grad));
        return Vector3.Dot(pos - new Vector3(cx, cy, cz), grad);
    }

    /// <summary><c>hash33UintPcg</c>, from the <c>hashes</c> declarations: integer arithmetic, so the same to the bit.</summary>
    private static (uint X, uint Y, uint Z) Pcg(uint x, uint y, uint z)
    {
        unchecked
        {
            x = (x * 1664525u) + 1013904223u;
            y = (y * 1664525u) + 1013904223u;
            z = (z * 1664525u) + 1013904223u;
            x += y * z;
            y += z * x;
            z += x * y;
            x ^= x >> 16;
            y ^= y >> 16;
            z ^= z >> 16;
            x += y * z;
            y += z * x;
            z += x * y;
            return (x, y, z);
        }
    }

    /// <summary><c>HashUToF</c>: 23 bits of a hash as a float in [0, 1).</summary>
    private static float HashUnit(uint hash) => Saturated(BitConverter.Int32BitsToSingle(unchecked((int)((hash >> 9) | 0x3f800000u))) - 1f);

    /// <summary>Vibrance: <c>pow(saturate(3v² - 2v³), 1 / (color + 1e-6))</c>, component by component of the colour.</summary>
    private static Vector4 Vibrant(float value, Vector4 colour)
    {
        float lift = Saturated((3f * value * value) - (2f * value * value * value));
        return new Vector4(
            Pow(lift, 1f / (colour.X + 1e-6f)), Pow(lift, 1f / (colour.Y + 1e-6f)), Pow(lift, 1f / (colour.Z + 1e-6f)), 0f);
    }

    /// <summary>Rotate and RotateUV: the coordinates turned by the angle about a centre - nought for Rotate.</summary>
    private static Vector4 Rotated(float angle, Vector4 uv, Vector4 centre)
    {
        (float sin, float cos) = MathF.SinCos(angle);
        float u = uv.X - centre.X;
        float v = uv.Y - centre.Y;
        return new Vector4((cos * u) + (sin * v) + centre.X, (-sin * u) + (cos * v) + centre.Y, 0f, 0f);
    }

    /// <summary>RadiusToPolarNorm: the length, and the angle as nought to one - by the node's own 3.1415.</summary>
    private static Vector4 Polar(Vector4 radius)
    {
        float length = MathF.Sqrt((radius.X * radius.X) + (radius.Y * radius.Y));
        return new Vector4(length, (MathF.Atan2(radius.Y / length, radius.X / length) / 3.1415f * 0.5f) + 0.5f, 0f, 0f);
    }

    /// <summary>
    /// The blend of three reads along the axes, weighted by the normal: SampleInputTriplanar's by its
    /// squared components, SampleTriplanar's by their absolute values.
    /// </summary>
    /// <remarks>
    /// TWO NODES, TWO WEIGHTINGS, and each as its fragment writes it: the first normalises by
    /// <c>max(1e-5, length)</c> and squares, the second divides by <c>length + 1e-7</c> and takes the
    /// absolute value. On a face square to an axis they agree; on a slope the squares favour the
    /// nearest axis more.
    /// </remarks>
    private static Vector4 Triplanar(Vector4 alongX, Vector4 alongY, Vector4 alongZ, Vector4 normal, bool absolute)
    {
        var n = new Vector3(normal.X, normal.Y, normal.Z);
        Vector3 weights;
        if (absolute)
        {
            weights = Vector3.Abs(n / (n.Length() + 1e-7f));
            weights /= MathF.Max(1e-7f, weights.X + weights.Y + weights.Z);
        }
        else
        {
            n /= MathF.Max(1e-5f, n.Length());
            weights = n * n;
            weights /= MathF.Max(1e-5f, weights.X + weights.Y + weights.Z);
        }

        return (alongX * weights.X) + (alongY * weights.Y) + (alongZ * weights.Z);
    }

    /// <summary>HardLightBlend, component by component: the second colour below a half multiplies, above it screens.</summary>
    private static Vector4 HardLit(Vector4 one, Vector4 other)
    {
        float Lit(float a, float b) => b < 0.5f ? 2f * a * b : 1f - (2f * (1f - a) * (1f - b));
        return new Vector4(Lit(one.X, other.X), Lit(one.Y, other.Y), Lit(one.Z, other.Z), Lit(one.W, other.W));
    }

    // NOT A NUMBER COMES OUT AS NOUGHT rather than as an index nowhere: a graph's square root of a
    // negative, or fmod by nought, is not a number on a graphics card too.
    private static float Linear(float value) => ToLinear[(int)((Saturated(value) * (Table - 1)) + 0.5f)];

    private static float Srgb(float value) => ToSrgb[(int)((Saturated(value) * (Table - 1)) + 0.5f)];

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
        "IndirectColor" => "IndirectColor",
        "SpecularColor" => "SpecularColor",
        "EmissiveColor" => "EmissiveColor",
        "SubsurfaceColor" => "SubsurfaceColor",
        "Glossiness" => "Glossiness",
        "TbnNormal" => "TbnNormal",
        "TbnBasis" => "TbnBasis",
        _ => null,
    };

    /// <summary>The value a reader node reads, or null for a node that is not a reader this knows.</summary>
    private static string? ReadBy(string type) => type switch
    {
        "InputUV" => "UV",
        "InputWorldPos" => "WorldPos",
        "InputWorldNormal" => "WorldNormal",
        "InputAlbedoColor" => "Albedo",
        "InputIndirectColor" => "IndirectColor",
        "InputSpecularColor" => "SpecularColor",
        "InputEmissiveColor" => "EmissiveColor",
        "InputSubsurfaceColor" => "SubsurfaceColor",
        "InputGlossiness" => "Glossiness",
        "InputTbnNormal" => "TbnNormal",
        "InputTbnBasis" => "TbnBasis",
        _ => null,
    };

    /// <summary>
    /// The components of its register a node's named output is, for the nodes with several outputs; else empty.
    /// </summary>
    /// <remarks>
    /// A LINK NAMES ITS PORT, and for these the port is part of one register: <c>Float3ToCoords</c>'s
    /// <c>y</c> is its input's y, <c>SampleTexture</c>'s <c>g</c> its read's green, <c>ModelOrigin</c>'s
    /// <c>model_origin</c> the xyz and its <c>scale</c> the w. Read as the whole register, a link from
    /// <c>.y</c> would hand on x, y and z.
    /// </remarks>
    private static string Implied(string type, string variable) => type switch
    {
        "Float2ToCoords" or "Float3ToCoords" or "Float4ToCoords" => variable is "x" or "y" or "z" or "w" ? variable : string.Empty,
        "SampleTexture" => variable switch
        {
            "r" => "x",
            "g" => "y",
            "b" => "z",
            "a" => "w",
            _ => string.Empty,
        },
        "ModelOrigin" => variable switch
        {
            "model_origin" => "xyz",
            "scale" => "w",
            _ => string.Empty,
        },
        "LookUpTexture" => variable switch
        {
            "r" => "x",
            "g" => "y",
            "b" => "z",
            _ => string.Empty,
        },
        _ => string.Empty,
    };

    /// <summary>A graph's file name without its folder or extension, for the line under the picture.</summary>
    private static string Named(string path)
    {
        string said = path.Replace('\\', '/');
        said = said[(said.LastIndexOf('/') + 1)..];
        int dot = said.LastIndexOf('.');
        return dot > 0 ? said[..dot] : said;
    }

    /// <summary>What a channel holds: its register, and which of its components nothing has set.</summary>
    /// <summary>A channel's register, the components nothing set, and whether a link wrote its w - see HasAlpha.</summary>
    private readonly record struct Held(int Register, int Unset, bool Alpha = true);

    /// <summary>The graphs, if any, that moved the vertices' positions and normals, or wrote their colour, at a vertex stage.</summary>
    private readonly record struct Displaced(string? Position, string? Normal, string? Colour = null);

    /// <summary>One fed writer of a channel: its node, the stage it writes at, the channel, what it reads, and whether it only hands on what it read.</summary>
    private readonly record struct Writer(ShaderNode Node, int Stage, int Channel, int Reads, bool Same);

    /// <summary>One step: what to do, where to put it, and from which registers.</summary>
    /// <param name="Extra">A texture's index for a read, the packed swizzle for a swizzle, the width for a dot, length or normalisation, how for a comparison.</param>
    /// <param name="Splat">Whether the result is a float, its x spread across all four components.</param>
    internal readonly record struct Step(Op Op, int To, int A, int B, int C, int D, int E, int Extra, bool Splat = false)
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
        /// <summary>Which registers hold, in x, y and z, exactly one plain read - see <see cref="ShadeProgram.Plain"/>.</summary>
        private readonly Dictionary<int, int> _plain = [];

        /// <summary>The constants by value, so one value takes one register however many nodes say it.</summary>
        private readonly Dictionary<Vector4, int> _byValue = [];

        /// <summary>The constants by register - what Transform needs to know of its input.</summary>
        private readonly Dictionary<int, Vector4> _value = [];

        /// <summary>
        /// The registers that hold only a direction: the TBN basis's normal, whose length the engine does not say.
        /// </summary>
        private readonly HashSet<int> _direction = [];

        public List<Step> Steps { get; } = [];

        public List<int> ConstantAt { get; } = [];

        public List<Vector4> Constants { get; } = [];

        public List<ShadeTexture> Textures { get; } = [];

        public List<int> SampleTextures { get; } = [];

        public int Next { get; private set; } = Fixed;

        public bool Fits => Next <= MostBuilt;

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

            foreach (KeyValuePair<Vector4, int> gone in _byValue.Where(one => one.Value >= mark.Next).ToList())
            {
                _byValue.Remove(gone.Key);
                _value.Remove(gone.Value);
            }

            _direction.RemoveWhere(one => one >= mark.Next);
            Next = mark.Next;
        }

        public int Register() => Next++;

        public int Constant(Vector4 value)
        {
            if (_byValue.TryGetValue(value, out int known))
            {
                return known;
            }

            int at = Register();
            ConstantAt.Add(at);
            Constants.Add(value);
            _byValue[value] = at;
            _value[at] = value;
            return at;
        }

        public bool ConstantOf(int register, out Vector4 value) => _value.TryGetValue(register, out value);

        public int Emit(Op op, int a, int b = -1, int c = -1, int d = -1, int e = -1, int extra = 0, bool splat = false)
        {
            int to = Register();
            Steps.Add(new Step(op, to, a, b, c, d, e, extra, splat));
            return to;
        }

        /// <summary>A step writing part of a register already allocated.</summary>
        public void Into(int into, int from, int packed) => Steps.Add(new Step(Op.Swizzle, into, from, -1, -1, -1, -1, packed));

        public void Directional(int register) => _direction.Add(register);

        public bool Direction(int register) => _direction.Contains(register);

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

    /// <summary>One graph's writes at one stage being compiled: its graph, its instance and the values it reads.</summary>
    private sealed class Unit(
        Builder build, Lookup lookup, ShaderInstance instance,
        IReadOnlyDictionary<string, Held> seen, IReadOnlyDictionary<string, string> lost, Displaced moved)
    {
        /// <summary>Every component taken from x - a float spread across a register.</summary>
        private const int Spread = 0b1111 << 8;

        private readonly Dictionary<ShaderNode, int> _done = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<ShaderNode> _open = new(ReferenceEqualityComparer.Instance);

        /// <summary>The registers of the nodes whose outputs are two reads, by output - see <see cref="Paired"/>.</summary>
        private readonly Dictionary<(ShaderNode Node, string Output), int> _pairs = [];

        /// <summary>Why the expression could not be compiled - the node type it stopped at.</summary>
        public string Why { get; private set; } = string.Empty;

        /// <summary>Begins one write.</summary>
        public void Start() => Why = string.Empty;

        /// <summary>Forgets what a write that was backed out compiled, from the register it was backed out to.</summary>
        public void Forget(int from)
        {
            foreach (ShaderNode gone in _done.Where(one => one.Value >= from).Select(one => one.Key).ToList())
            {
                _done.Remove(gone);
            }

            foreach ((ShaderNode Node, string Output) gone in _pairs.Where(one => one.Value >= from).Select(one => one.Key).ToList())
            {
                _pairs.Remove(gone);
            }
        }

        /// <summary>The register holding what arrives on one port - nought where nothing is linked to it - or null.</summary>
        /// <remarks>See the class remarks for why an unlinked port reads nought.</remarks>
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

            return links.Count == 0 ? build.Constant(Vector4.Zero) : Assembled(node, port, links, -1);
        }

        /// <summary>
        /// The register holding a colour writer's input: its xyz, and its w where that compiles.
        /// </summary>
        /// <remarks>
        /// THE W IS TRIED AFTER THE XYZ AND INTO THE SAME REGISTER, and backed out on its own where it
        /// fails: the colour is what is drawn, the w is not, so a w this cannot work out costs nothing
        /// but the w - marked unset, so that no later graph reads it. A writer with nothing but a w
        /// on it is refused: what its xyz are then is not written down.
        /// </remarks>
        public (int Register, int Unset, bool Alpha)? Colour(ShaderNode node)
        {
            var colour = new List<ShaderLink>();
            var alpha = new List<ShaderLink>();
            foreach (ShaderLink link in lookup.Into(node))
            {
                if (string.Equals(link.Target.Variable, "input", StringComparison.Ordinal))
                {
                    (AlphaOnly(link.Target.Swizzle) ? alpha : colour).Add(link);
                }
            }

            if (colour.Count == 0)
            {
                Fail(alpha.Count == 0 ? $"{node.Type} with nothing on input" : $"{node.Type} with its w alone, whose xyz are then not written down");
                return null;
            }

            // ITS OWN REGISTER WHERE A W IS TO FOLLOW: a whole value brought straight across is the
            // source's register, which the w must not be written into.
            if (Assembled(node, "input", colour, -1, alpha.Count > 0) is not { } into)
            {
                return null;
            }

            // WHETHER A LINK WROTE THE W: a whole value, a swizzle naming it, or a w of its own. Every
            // real graph that writes the colour's xyz brings InputAlbedoColor's w across beside it, which
            // says a write replaces the whole value - so a w written is the engine's alpha, and one left
            // to the cleared register is not known to be.
            bool written = alpha.Count > 0;
            foreach (ShaderLink link in colour)
            {
                written |= link.Target.Swizzle.Length == 0 || (Mask(link.Target.Swizzle) & 0b1000) != 0;
            }

            var unset = 0;
            if (alpha.Count > 0)
            {
                Builder.Mark mark = build.Marked();
                if (Assembled(node, "input", alpha, into) is null)
                {
                    build.Back(mark);
                    Forget(mark.Next);
                    Why = string.Empty;
                    unset = 0b1000;
                }
            }

            return (into, unset, written);
        }

        /// <summary>
        /// The links into one port assembled into a register: the given one, or a new one - or the source's own
        /// where one whole value comes straight across and nothing says it needs its own.
        /// </summary>
        private int? Assembled(ShaderNode node, string port, List<ShaderLink> links, int into, bool own = false)
        {
            // ONE WHOLE VALUE STRAIGHT ACROSS needs no step of its own.
            if (!own && into < 0 && links.Count == 1 && links[0].Target.Swizzle.Length == 0 && Swizzled(links[0].Source) is { Length: 0 })
            {
                return Brought(node, port, links[0].Source, string.Empty);
            }

            var parts = new (int From, int Packed)[links.Count];
            int covered = 0;
            for (var one = 0; one < links.Count; one++)
            {
                if (Swizzled(links[one].Source) is not { } swizzle)
                {
                    return Fail($"{node.Type} with a swizzle this does not read");
                }

                if (Brought(node, port, links[one].Source, swizzle) is not { } from)
                {
                    return null;
                }

                // A MATRIX OR A DIRECTION CUT INTO PARTS is neither any more.
                if (from == Basis || build.Direction(from))
                {
                    return Fail($"{node.Type} with part of the TBN basis");
                }

                if (Packed(swizzle, links[one].Target.Swizzle) is not { } packed)
                {
                    return Fail($"{node.Type} with a swizzle this does not read");
                }

                parts[one] = (from, packed);
                covered |= packed >> 8;
            }

            if (into < 0)
            {
                into = build.Register();
                if (covered != 0b1111)
                {
                    build.Steps.Add(new Step(Op.Clear, into, -1, -1, -1, -1, -1, 0));
                }
            }

            foreach ((int from, int packed) in parts)
            {
                build.Into(into, from, packed);
                build.Carried(into, from, packed);
            }

            return into;
        }

        /// <summary>
        /// The register a link brings, with what it may be used for checked.
        /// </summary>
        /// <remarks>
        /// THREE THINGS ARE REFUSED HERE. A component nothing has set - the indirect light's w before
        /// any graph wrote it, the vertex world position's w, which no file says. The TBN basis
        /// anywhere but where a matrix goes. And the basis's normal anywhere its length would matter:
        /// the engine builds the basis from the interpolated normal, and whether it normalises it on
        /// the way is not written down.
        /// </remarks>
        private int? Brought(ShaderNode node, string port, ShaderEnd source, string swizzle)
        {
            if (lookup.Node(source) is { } reader
                && ReadBy(reader.Type) is { } channel
                && seen.TryGetValue(channel, out Held held)
                && (Mask(swizzle) & held.Unset) != 0)
            {
                return Fail($"{reader.Type}'s {Letters(Mask(swizzle) & held.Unset)}, which nothing has set");
            }

            if (lookup.Node(source) is { Type: "FromVertexWorldPos" } && (Mask(swizzle) & 0b1000) != 0)
            {
                return Fail("FromVertexWorldPos's w, which no file says");
            }

            // The fragment declares an a and never assigns it.
            if (lookup.Node(source) is { Type: "LookUpTexture" } && source.Variable == "a")
            {
                return Fail("LookUpTexture's a, which the fragment never sets");
            }

            if (Output(source) is not { } register)
            {
                return null;
            }

            if (register == Basis && (node.Type, port) is not (("Transform", "inmatrix") or ("TbnBasis", "input")))
            {
                return Fail($"{node.Type} reading the TBN basis");
            }

            if (build.Direction(register) && !Directional(node.Type, port))
            {
                return Fail($"{node.Type} with the TBN basis's normal, whose length is not known");
            }

            return register;
        }

        /// <summary>A link's source swizzle, with the components a named output is put in; null where it cannot be.</summary>
        /// <remarks>
        /// THE LINK'S OWN SWIZZLE IS OF THE OUTPUT, not of the register behind it: <c>model_origin.y</c>
        /// is the register's y, <c>scale.x</c> its w. A letter past the output's width is no swizzle of it.
        /// </remarks>
        private string? Swizzled(ShaderEnd end)
        {
            string implied = lookup.Node(end) is { } source ? Implied(source.Type, end.Variable) : string.Empty;
            if (implied.Length == 0 || end.Swizzle.Length == 0)
            {
                return implied.Length == 0 ? end.Swizzle : implied;
            }

            var said = new char[end.Swizzle.Length];
            for (var at = 0; at < said.Length; at++)
            {
                int part = Component(end.Swizzle[at]);
                if (part < 0 || part >= implied.Length)
                {
                    return null;
                }

                said[at] = implied[part];
            }

            return new string(said);
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

            if (node.Type is "SampleTexture2" or "SampleTextureAtlas2")
            {
                return Paired(node, end.Variable);
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

        /// <summary>
        /// One output of a node that reads its texture twice - <c>rgba0</c> and <c>rgba1</c>, each its own register.
        /// </summary>
        /// <remarks>
        /// BOTH READS ARE EMITTED THE FIRST TIME EITHER IS ASKED FOR, since they share what comes before
        /// them, and the one no link reads is dropped with the rest of the dead steps in Finish.
        /// </remarks>
        private int? Paired(ShaderNode node, string output)
        {
            if (_pairs.TryGetValue((node, output), out int known))
            {
                return known;
            }

            if (!_open.Add(node))
            {
                return Fail($"{node.Type} feeding itself");
            }

            (int First, int Second)? pair = node.Type == "SampleTexture2" ? Twice(node) : Atlas(node);
            _open.Remove(node);
            if (pair is not { } registers)
            {
                return null;
            }

            _pairs[(node, "rgba0")] = registers.First;
            _pairs[(node, "rgba1")] = registers.Second;
            return output switch
            {
                "rgba0" => registers.First,
                "rgba1" => registers.Second,
                _ => Fail($"{node.Type} has no output called {output}"),
            };
        }

        /// <summary>SampleTexture2: one texture read at two coordinates.</summary>
        private (int, int)? Twice(ShaderNode node)
            => Texture(node, Parameter(node, 0)) is { } sheet && Port(node, "uv0") is { } first && Port(node, "uv1") is { } second
                ? (build.Sample(sheet, first), build.Sample(sheet, second))
                : null;

        /// <summary>
        /// SampleTextureAtlas2: the left and right halves of one texture, read at one coordinate wrapped in u.
        /// </summary>
        /// <remarks>
        /// <c>uv.x = fmod(uv.x + 1, 1)</c> - the u alone, the v as it came - then each half at
        /// <c>uv * (0.5, 1)</c>, the right one shifted by a half. HLSL's fmod keeps the sign of what it
        /// divides, as the step's does.
        /// </remarks>
        private (int, int)? Atlas(ShaderNode node)
        {
            if (Texture(node, Parameter(node, 0)) is not { } sheet || Port(node, "uv") is not { } uv)
            {
                return null;
            }

            int wrapped = build.Emit(Op.Fmod, build.Emit(Op.Add, uv, build.Constant(Vector4.One)), build.Constant(Vector4.One));
            int spot = build.Register();
            build.Steps.Add(new Step(Op.Clear, spot, -1, -1, -1, -1, -1, 0));
            build.Into(spot, wrapped, 0b0001 << 8);
            build.Into(spot, uv, (1 << 2) | (0b0010 << 8));
            int half = build.Constant(new Vector4(0.5f, 1f, 0f, 0f));
            int left = build.Emit(Op.Multiply, spot, half);
            int right = build.Emit(Op.MultiplyAdd, spot, half, build.Constant(new Vector4(0.5f, 0f, 0f, 0f)));
            return (build.Sample(sheet, left), build.Sample(sheet, right));
        }

        private int? Compiled(ShaderNode node)
        {
            if (ReadBy(node.Type) is { } channel)
            {
                if (seen.TryGetValue(channel, out Held held))
                {
                    return held.Register;
                }

                return lost.TryGetValue(channel, out string? by)
                    ? Fail($"{node.Type} after {by}'s was left out")
                    : Fail($"{node.Type} before any graph wrote it");
            }

            if (!Evaluated.Contains(node.Type))
            {
                return Fail(node.Type);
            }

            // A FLOAT NODE'S RESULT IS ITS X, SPREAD - see the class's remarks. The families' float
            // members are the ones without a width on the end: Max, not Max3.
            bool splat = !char.IsAsciiDigit(node.Type[^1]);
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
                case "ConstantInt":
                case "ConstantUInt":
                    return build.Constant(Vectored(Parameter(node, 0).Numbers));

                case "Zero":
                case "ZeroUInt":
                    return build.Constant(Vector4.Zero);

                // PBRGroundBN's alpha - every ground material in the desert tilesets - and a scalar
                // broadcast like every other constant here.
                case "One":
                case "OneUInt":
                    return build.Constant(Vector4.One);

                case "Half":
                    return build.Constant(new Vector4(0.5f));

                case "Two":
                case "TwoUInt":
                    return build.Constant(new Vector4(2f));

                case "Pi":
                    return build.Constant(new Vector4(3.14159265359f));

                case "Epsilon":
                    return build.Constant(new Vector4(1e-4f));

                // A FLOAT PASSED THROUGH IS STILL CUT TO ITS X; a vector one is the register itself.
                case "Dummy":
                case "DummyBool":
                case "DummyInt":
                case "DummyUInt":
                    return Single(node) is { } passed ? build.Emit(Op.Swizzle, passed, extra: Spread) : null;

                case "Dummy2":
                case "Dummy3":
                case "Dummy4":
                    return Single(node);

                case "Add":
                case "Add2":
                case "Add3":
                case "Add4":
                    return Binary(node, Op.Add, "a", "b", splat);

                case "Subtract":
                case "Subtract2":
                case "Subtract3":
                case "Subtract4":
                    return Binary(node, Op.Subtract, "a", "b", splat);

                case "Multiply":
                case "Multiply2":
                case "Multiply3":
                case "Multiply4":
                    return Binary(node, Op.Multiply, "a", "b", splat);

                case "Divide":
                case "Divide2":
                case "Divide3":
                case "Divide4":
                    return Binary(node, Op.Divide, "a", "b", splat);

                case "Max":
                case "Max2":
                case "Max3":
                case "Max4":
                case "MaxUInt":
                    return Binary(node, Op.Max, "a", "b", splat);

                case "Min":
                case "Min2":
                case "Min3":
                case "Min4":
                case "MinUInt":
                    return Binary(node, Op.Min, "a", "b", splat);

                case "Fmod":
                case "Fmod2":
                case "Fmod3":
                case "Fmod4":
                    return Binary(node, Op.Fmod, "a", "b", splat);

                // ONE NUMBER WHATEVER THE NODE'S WIDTH: "uniform float mult_const3", spread.
                case "MultiplyConst":
                case "MultiplyConst2":
                case "MultiplyConst3":
                case "MultiplyConst4":
                    return Constanted(node, Op.Multiply, splat);

                case "AddConst":
                case "AddConst2":
                case "AddConst3":
                case "AddConst4":
                    return Constanted(node, Op.Add, splat);

                case "SubtractConst":
                case "SubtractConst2":
                case "SubtractConst3":
                case "SubtractConst4":
                    return Constanted(node, Op.Subtract, splat);

                case "MultiplyAdd":
                case "MultiplyAdd2":
                case "MultiplyAdd3":
                case "MultiplyAdd4":
                    return Port(node, "a") is { } ma && Port(node, "b") is { } mb && Port(node, "c") is { } mc
                        ? build.Emit(Op.MultiplyAdd, ma, mb, mc, splat: splat)
                        : null;

                case "Clamp":
                case "Clamp2":
                case "Clamp3":
                case "Clamp4":
                    return Port(node, "input") is { } clamped && Port(node, "iMin") is { } low && Port(node, "iMax") is { } high
                        ? build.Emit(Op.Clamp, clamped, low, high, splat: splat)
                        : null;

                case "Power":
                    return Binary(node, Op.Power, "base", "exp", false);

                case "Step":
                    return Binary(node, Op.Step, "val", "threshold", false);

                case "Negate":
                case "Negate2":
                case "Negate3":
                case "Negate4":
                    return Unary(node, Op.Negate, splat);

                case "OneMinus":
                case "OneMinus2":
                case "OneMinus3":
                case "OneMinus4":
                    return Unary(node, Op.OneMinus, splat);

                case "Saturate":
                case "Saturate2":
                case "Saturate3":
                case "Saturate4":
                    return Unary(node, Op.Saturate, splat);

                case "Abs":
                case "Abs2":
                case "Abs3":
                case "Abs4":
                    return Unary(node, Op.Abs, splat);

                case "Floor":
                case "Floor2":
                case "Floor3":
                case "Floor4":
                    return Unary(node, Op.Floor, splat);

                case "Ceil":
                case "Ceil2":
                case "Ceil3":
                case "Ceil4":
                    return Unary(node, Op.Ceil, splat);

                case "Round":
                    return Unary(node, Op.Round, splat);

                case "Truncate":
                case "Truncate2":
                case "Truncate3":
                case "Truncate4":
                    return Unary(node, Op.Truncate, splat);

                case "Frac":
                case "Frac2":
                case "Frac3":
                case "Frac4":
                    return Unary(node, Op.Frac, splat);

                case "Sqrt":
                case "Sqrt2":
                case "Sqrt3":
                case "Sqrt4":
                    return Unary(node, Op.Sqrt, splat);

                case "Sign":
                case "Sign2":
                case "Sign3":
                case "Sign4":
                    return Unary(node, Op.Sign, splat);

                case "Normalize2":
                case "Normalize3":
                case "Normalize4":
                    return Single(node) is { } normalised ? build.Emit(Op.Normalize, normalised, extra: Width(node.Type)) : null;

                case "DotProduct2":
                case "DotProduct3":
                case "DotProduct4":
                    return Port(node, "a") is { } da && Port(node, "b") is { } db
                        ? build.Emit(Op.Dot, da, db, extra: Width(node.Type))
                        : null;

                case "Length3":
                    return Single(node) is { } measured ? build.Emit(Op.Length, measured, extra: 3) : null;

                case "Luminance":
                    return Single(node) is { } lit
                        ? build.Emit(Op.Dot, lit, build.Constant(new Vector4(0.299f, 0.587f, 0.114f, 0f)), extra: 3)
                        : null;

                // legacy.ffx's, and a float3 for all it is one number: dot(float3(0.222, 0.707, 0.071), in_color).
                case "GrayScale":
                    return Single(node) is { } grey
                        ? build.Emit(Op.Dot, grey, build.Constant(new Vector4(0.222f, 0.707f, 0.071f, 0f)), extra: 3)
                        : null;

                case "Lerp":
                case "Lerp2":
                case "Lerp3":
                case "Lerp4":
                    return Port(node, "a") is { } a && Port(node, "b") is { } b && Port(node, "alpha") is { } alpha
                        ? build.Emit(Op.Lerp, a, b, alpha, splat: splat)
                        : null;

                case "SmoothStep":
                    return Port(node, "center") is { } center && Port(node, "steepness") is { } steepness
                        && Port(node, "in_value") is { } smoothed
                        ? build.Emit(Op.SmoothStep, center, steepness, smoothed)
                        : null;

                case "CheapSmoothstep":
                    return Port(node, "min_value") is { } edge0 && Port(node, "max_value") is { } edge1
                        && Port(node, "value") is { } stepped
                        ? build.Emit(Op.Hermite, edge0, edge1, stepped)
                        : null;

                // a > b gives greater, a < b lesser, anything else equals - the ports in Step's C, D and E.
                case "If":
                    return Port(node, "a") is { } left && Port(node, "b") is { } right
                        && Port(node, "greater") is { } greater && Port(node, "equals") is { } equals
                        && Port(node, "lesser") is { } lesser
                        ? build.Emit(Op.If, left, right, greater, equals, lesser)
                        : null;

                case "FitRangeFromInput":
                    return Port(node, "value") is { } value
                        && Port(node, "in_min") is { } inMin && Port(node, "in_max") is { } inMax
                        && Port(node, "out_min") is { } outMin && Port(node, "out_max") is { } outMax
                        ? build.Emit(Op.Fit, value, inMin, inMax, outMin, outMax)
                        : null;

                // THE RANGE IS THE NODE'S OWN: inputMin, inputMax, outputMin, outputMax, declared 0, 255, 0, 255.
                case "FitRange":
                    return Port(node, "value") is { } fitted
                        ? build.Emit(
                            Op.Fit, fitted,
                            build.Constant(new Vector4(Said(node, 0, 0f))), build.Constant(new Vector4(Said(node, 1, 255f))),
                            build.Constant(new Vector4(Said(node, 2, 0f))), build.Constant(new Vector4(Said(node, 3, 255f))))
                        : null;

                // Hue, saturation and brightness, declared 0, a half and a half.
                case "RemapHue":
                    return Port(node, "color_map") is { } mapped
                        ? build.Emit(Op.RemapHue, mapped, build.Constant(Turn(Said(node, 0, 0f), Said(node, 1, 0.5f), Said(node, 2, 0.5f))))
                        : null;

                case "RemapHueInput":
                    return Port(node, "color_map") is { } remapped
                        && Port(node, "hue") is { } turned && Port(node, "saturation") is { } sat && Port(node, "brightness") is { } bright
                        ? build.Emit(Op.RemapHue, remapped, build.Emit(Op.HueTurn, turned, sat, bright))
                        : null;

                case "SelectFloat":
                case "SelectFloat2":
                case "SelectFloat3":
                case "SelectFloat4":
                case "SelectBool":
                case "SelectUInt":
                    return Port(node, "a") is { } no && Port(node, "b") is { } yes && Port(node, "condition") is { } condition
                        ? build.Emit(Op.Select, no, yes, condition, splat: splat)
                        : null;

                case "SelectChannel":
                    return Port(node, "input") is { } channels && Port(node, "index") is { } index
                        ? build.Emit(Op.Pick, channels, index)
                        : null;

                case "GreaterThan":
                case "GreaterThanUInt":
                    return Compare(node, Greater);

                case "LessThan":
                case "LessThanUInt":
                    return Compare(node, Less);

                case "EqualsUInt":
                    return Compare(node, Equal);

                case "And":
                    return Compare(node, Both);

                case "Or":
                    return Compare(node, Either);

                case "Not":
                    return Port(node, "a") is { } negated ? build.Emit(Op.Compare, negated, extra: Not) : null;

                // THE INPUT ITSELF: each output is one of its components - see Implied.
                case "Float2ToCoords":
                case "Float3ToCoords":
                case "Float4ToCoords":
                    return Port(node, "input");

                case "CoordsToFloat2":
                case "CoordsToFloat3":
                case "CoordsToFloat4":
                    return Assembled(node, Width(node.Type));

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

                case "SampleInputTriplanar":
                    return Triplanar(node);

                case "SampleTriplanar":
                    return Triplanar(node, own: true);

                case "SampleTextureLod":
                    return Texture(node, Parameter(node, 0)) is { } levelled && Port(node, "uv") is { } there && Port(node, "lod") is { } level
                        ? build.Emit(Op.SampleLod, there, level, extra: levelled)
                        : null;

                case "SampleDispersedTexture":
                    return Dispersed(node);

                case "MuddleTex":
                    return Muddled(node);

                case "MuddleTexFromInput":
                    return MuddledFromInput(node);

                case "MuddleTex2":
                    return MuddledTwice(node);

                // semanticsData.uv2.x under PARTICLE_VARIANCE_ENABLED and nought otherwise, says the
                // fragment; a mesh is not a particle, so it is nought.
                case "FromVertexVariance":
                    return build.Constant(Vector4.Zero);

                case "RGBToTbn":
                    return Single(node) is { } rgb
                        ? build.Emit(Op.MultiplyAdd, rgb, build.Constant(new Vector4(2f)), build.Constant(new Vector4(-1f)))
                        : null;

                case "ScaleUVMaya":
                    return Scaled(node);

                case "HardLightBlend":
                    return Binary(node, Op.HardLight, "color1", "color2", false);

                case "Sine":
                    return Unary(node, Op.Sine, splat);

                case "RotateUVOld":
                    return RotatedOld(node);

                // semanticsData.normal: the vertex normal as interpolated, in the model's space - the
                // world's, for a model drawn where it stands (see the class's remarks).
                case "FromVertexNormal":
                    return moved.Normal is { } bent ? Fail($"{node.Type} after {bent} turned the vertices") : VertexNormal;

                // semanticsData.world_pos, the vertex position as interpolated - the model's, drawn where
                // it stands. Its w is refused in Brought.
                case "FromVertexWorldPos":
                    return moved.Position is { } shifted ? Fail($"{node.Type} after {shifted} moved the vertices") : Position;

                // VertexLocalPosition's float4(position, 1), at either transform stage the same for a model
                // drawn where it stands; and semanticsData.uv9, which the engine's OutputVertexLocalPosition
                // sets to the same - see the class's remarks.
                case "InputVertexPosition":
                case "FromVertexLocalPosition":
                    return moved.Position is { } displaced ? Fail($"{node.Type} after {displaced} moved the vertices") : Placed();

                case "InputVertexNormal":
                    return moved.Normal is { } tilted ? Fail($"{node.Type} after {tilted} turned the vertices") : VertexNormal;

                // The mesh's own colour stream, where it has one - see the class's remarks.
                case "InputVertexColor":
                    return VertexColour;

                // semanticsData.color0, which InitSemanticsData sets to white and the vertex side replaces
                // only through COLOR0: the engine's OutputVertexLocalColor hands the mesh's own colour stream
                // across, a graph's OutputVertexColor whatever it wrote. So the same register as
                // InputVertexColor - the stream, white without one - unless a graph wrote it.
                case "FromVertexColor":
                    return moved.Colour is { } painted ? Fail($"{node.Type} after {painted} wrote the vertex colour") : VertexColour;

                // output = time - the game's clock, spread as a float is. See Clock.
                case "Time":
                    return Clock;

                case "LookUpTexture":
                    return LookedUp(node);

                // The model's origin, and how much its transform stretches x: nought and one for a model
                // drawn where it stands. One register, the outputs parts of it - see Implied.
                case "ModelOrigin":
                    return build.Constant(new Vector4(0f, 0f, 0f, 1f));

                case "Noise31":
                    return Single(node) is { } noised ? build.Emit(Op.Noise, noised, extra: 0) : null;

                case "PerlinNoise31":
                    return Single(node) is { } perlin ? build.Emit(Op.Noise, perlin, extra: 1) : null;

                case "Vibrance":
                    return Port(node, "val") is { } vibrance && Port(node, "color") is { } vivid
                        ? build.Emit(Op.Vibrance, vibrance, vivid)
                        : null;

                case "Rotate":
                    return Port(node, "angle") is { } angle && Port(node, "in_uv") is { } swung
                        ? build.Emit(Op.Rotate, angle, swung)
                        : null;

                // Its centre is declared "0.5 0.5".
                case "RotateUV":
                    return Port(node, "angle") is { } spin && Port(node, "in_uv") is { } spun
                        ? build.Emit(
                            Op.Rotate, spin, spun,
                            build.Constant(Parameter(node, 0).Numbers is { Length: > 0 } centre ? Vectored(centre) : new Vector4(0.5f, 0.5f, 0f, 0f)))
                        : null;

                case "RadiusToPolarNorm":
                    return Single(node) is { } radius ? build.Emit(Op.Polar, radius) : null;

                // in_position.xy + ground_scalemove_uv.zw, where that shift is the engine's, set for the
                // whole scene and in no file. It moves where a pattern lies, not what it looks like, so
                // it is taken as nought: AGT_DesertDust's dust is the same dust, slid.
                case "GroundScroll":
                    return Port(node, "in_position");

                case "Transform":
                    return Transformed(node);

                default:
                    return Fail(node.Type);
            }
        }

        private int? Binary(ShaderNode node, Op op, string left, string right, bool splat)
            => Port(node, left) is { } a && Port(node, right) is { } b ? build.Emit(op, a, b, splat: splat) : null;

        private int? Unary(ShaderNode node, Op op, bool splat)
            => Single(node) is { } input ? build.Emit(op, input, splat: splat) : null;

        /// <summary>A node's input and its one-number parameter: MultiplyConst, AddConst, SubtractConst.</summary>
        private int? Constanted(ShaderNode node, Op op, bool splat)
            => Port(node, "a") is { } a ? build.Emit(op, a, build.Constant(Vectored(Parameter(node, 0).Numbers)), splat: splat) : null;

        private int? Compare(ShaderNode node, int how)
            => Port(node, "a") is { } a && Port(node, "b") is { } b ? build.Emit(Op.Compare, a, b, extra: how) : null;

        /// <summary>CoordsToFloat2, 3 and 4: each float input's x put in its own component.</summary>
        private int? Assembled(ShaderNode node, int width)
        {
            var parts = new int[width];
            for (var part = 0; part < width; part++)
            {
                if (Port(node, "xyzw"[part].ToString()) is not { } one)
                {
                    return null;
                }

                parts[part] = one;
            }

            // THE COMPONENTS PAST THE WIDTH ARE NEVER READ - a float3 has no w to hand on - so they
            // are left as they lie.
            int into = build.Register();
            for (var part = 0; part < width; part++)
            {
                build.Into(into, parts[part], (1 << part) << 8);
            }

            return into;
        }

        /// <summary>
        /// SampleInputTriplanar and SampleTriplanar: the texture read three times, along each axis, and blended by the normal.
        /// </summary>
        /// <remarks>
        /// THREE ORDINARY READS - at the position's yz, xz and xy - so each takes its own level from
        /// the triangle, as a graphics card would give each its own derivatives. The first node is
        /// handed its texture and weights by the normal's squares; the second owns its texture and
        /// weights by the normal's absolute values - see <see cref="ShadeProgram.Triplanar"/>.
        /// </remarks>
        private int? Triplanar(ShaderNode node, bool own = false)
        {
            int? texture = own ? Texture(node, Parameter(node, 0)) : Handed(node);
            if (texture is not { } sheet || Port(node, "uv") is not { } uvw || Port(node, "world_normal") is not { } normal)
            {
                return null;
            }

            int yz = build.Emit(Op.Swizzle, uvw, extra: 1 | (2 << 2) | (0b0011 << 8));
            int xz = build.Emit(Op.Swizzle, uvw, extra: 0 | (2 << 2) | (0b0011 << 8));
            int alongX = build.Sample(sheet, yz);
            int alongY = build.Sample(sheet, xz);
            int alongZ = build.Sample(sheet, uvw);
            return build.Emit(Op.Triplanar, alongX, alongY, alongZ, normal, extra: own ? 1 : 0);
        }

        /// <summary>
        /// SampleDispersedTexture: the red, green and blue read a little apart, as a lens would spread them.
        /// </summary>
        /// <remarks>
        /// Three reads at the level given - at <c>uv</c>, <c>uv + dispersion / 2</c> and
        /// <c>uv + dispersion</c> - the first's red, the second's green, the third's blue, and a third of
        /// each alpha.
        /// </remarks>
        private int? Dispersed(ShaderNode node)
        {
            if (Handed(node, "tex") is not { } sheet || Port(node, "uv") is not { } uv
                || Port(node, "uv_dispersion") is not { } spread || Port(node, "mip_level") is not { } level)
            {
                return null;
            }

            int near = build.Emit(Op.SampleLod, uv, level, extra: sheet);
            int middle = build.Emit(Op.SampleLod, build.Emit(Op.MultiplyAdd, spread, build.Constant(new Vector4(0.5f)), uv), level, extra: sheet);
            int far = build.Emit(Op.SampleLod, build.Emit(Op.Add, uv, spread), level, extra: sheet);
            const float third = 1f / 3f;
            return build.Emit(
                Op.Add,
                build.Emit(Op.Multiply, near, build.Constant(new Vector4(1f, 0f, 0f, third))),
                build.Emit(Op.Multiply, middle, build.Constant(new Vector4(0f, 1f, 0f, third))),
                build.Emit(Op.Multiply, far, build.Constant(new Vector4(0f, 0f, 1f, third))));
        }

        /// <summary>
        /// MuddleTex: the coordinates pushed about by a texture read at them - scaled, shifted by the variance - times an intensity.
        /// </summary>
        /// <remarks>
        /// <c>MuddleTexHelper</c>: <c>muddle_uv = uv * freq + time * scroll + variance; uv + (tex(muddle_uv).rg - 0.5) * intensity</c>.
        /// THE SCROLL RIDES ON THE CLOCK - see Clock - and without one the term is left out, the same
        /// picture at every instant. The texture is at parameter 0, the frequency (declared 1) at 2, the
        /// scroll at 3 and the intensity (declared 0) at 4.
        /// </remarks>
        private int? Muddled(ShaderNode node)
        {
            if (Texture(node, Parameter(node, 0)) is not { } sheet || Port(node, "in_uv") is not { } uv || Port(node, "variance") is not { } variance)
            {
                return null;
            }

            Vector4 scroll = Numbers(node, 3, Vector4.Zero);
            int shifted = scroll.X != 0f || scroll.Y != 0f
                ? build.Emit(Op.MultiplyAdd, Clock, build.Constant(new Vector4(scroll.X, scroll.Y, 0f, 0f)), variance)
                : variance;

            return Muddle(sheet, uv, build.Constant(new Vector4(Said(node, 2, 1f))), shifted, build.Constant(new Vector4(Said(node, 4, 0f))));
        }

        /// <summary>MuddleTexFromInput: MuddleTex with its frequency, scroll and intensity on ports.</summary>
        /// <remarks>A scroll on a port rides on the clock as in <see cref="Muddled"/>; a constant nought leaves the term out.</remarks>
        private int? MuddledFromInput(ShaderNode node)
        {
            if (Texture(node, Parameter(node, 0)) is not { } sheet || Port(node, "in_uv") is not { } uv
                || Port(node, "flow_variance") is not { } variance || Port(node, "flow_frequency") is not { } frequency
                || Port(node, "flow_scroll") is not { } scroll || Port(node, "flow_intensity") is not { } intensity)
            {
                return null;
            }

            int shifted = build.ConstantOf(scroll, out Vector4 fixed_) && fixed_.X == 0f && fixed_.Y == 0f
                ? variance
                : build.Emit(Op.MultiplyAdd, Clock, Scroll(scroll), variance);

            return Muddle(sheet, uv, frequency, shifted, intensity);
        }

        /// <summary>
        /// MuddleTex2: two muddles of one texture added, each with its own frequency, scroll and intensity in one float4.
        /// </summary>
        /// <remarks>
        /// <c>Freq ScrollX ScrollY Intensity</c>, declared "1 0 0 0", at parameters 2 and 3:
        /// <c>muddle_uv = in_uv * params.x + time * params.yz</c> for each, the scroll riding on the clock
        /// as in <see cref="Muddled"/>, and no variance.
        /// </remarks>
        private int? MuddledTwice(ShaderNode node)
        {
            if (Texture(node, Parameter(node, 0)) is not { } sheet || Port(node, "in_uv") is not { } uv)
            {
                return null;
            }

            Vector4 first = Numbers(node, 2, new Vector4(1f, 0f, 0f, 0f));
            Vector4 second = Numbers(node, 3, new Vector4(1f, 0f, 0f, 0f));
            int once = Offset(sheet, Spot(uv, first), build.Constant(new Vector4(first.W)));
            int twice = Offset(sheet, Spot(uv, second), build.Constant(new Vector4(second.W)));
            return build.Emit(Op.Add, uv, once, twice);
        }

        /// <summary>One of MuddleTex2's spots: <c>uv * params.x + time * params.yz</c>, the clock's term left out where the scroll is nought.</summary>
        private int Spot(int uv, Vector4 parameters)
        {
            int scaled = build.Emit(Op.Multiply, uv, build.Constant(new Vector4(parameters.X)));
            return parameters.Y == 0f && parameters.Z == 0f
                ? scaled
                : build.Emit(Op.MultiplyAdd, Clock, build.Constant(new Vector4(parameters.Y, parameters.Z, 0f, 0f)), scaled);
        }

        /// <summary>A float2 scroll from a port, its z and w cleared so the clock times it moves only the coordinates.</summary>
        private int Scroll(int scroll)
            => build.Emit(Op.Multiply, scroll, build.Constant(new Vector4(1f, 1f, 0f, 0f)));

        /// <summary>The helper's body, the clock's term already in the variance: <c>uv + (tex(uv * frequency + variance).rg - 0.5) * intensity</c>.</summary>
        private int Muddle(int sheet, int uv, int frequency, int variance, int intensity)
            => build.Emit(Op.Add, uv, Offset(sheet, build.Emit(Op.MultiplyAdd, uv, frequency, variance), intensity));

        /// <summary>What a muddle read at a spot pushes the coordinates by: <c>(tex(spot).rg - 0.5) * intensity</c>.</summary>
        private int Offset(int sheet, int spot, int intensity)
            => build.Emit(Op.Multiply, build.Emit(Op.Subtract, build.Sample(sheet, spot), build.Constant(new Vector4(0.5f))), intensity);

        /// <summary>
        /// ScaleUVMaya: the coordinates scaled about a pivot given in Maya's texture space, whose v runs the other way.
        /// </summary>
        /// <remarks><c>real_pivot = (pivot.x, 1 - pivot.y); (uv - real_pivot) * scale + real_pivot</c>, the pivot declared "0 0".</remarks>
        private int? Scaled(ShaderNode node)
        {
            if (Port(node, "in_uv") is not { } uv || Port(node, "scale") is not { } scale)
            {
                return null;
            }

            Vector4 pivot = Numbers(node, 0, Vector4.Zero);
            int real = build.Constant(new Vector4(pivot.X, 1f - pivot.Y, 0f, 0f));
            return build.Emit(Op.MultiplyAdd, build.Emit(Op.Subtract, uv, real), scale, real);
        }

        /// <summary>
        /// RotateUVOld: the coordinates turned about the half-point of their own quadrant.
        /// </summary>
        /// <remarks>
        /// <c>angle = time * AngleTime + AngleOffset</c>, the turn riding on the clock - see Clock - and
        /// the declared default "0 0". The offset is <c>sign(uv) * 0.5</c>,
        /// and the turn is <c>(x cos - y sin, x sin + y cos)</c> - the other way round from Rotate's, so
        /// Rotate's step is given the angle negated.
        /// </remarks>
        private int? RotatedOld(ShaderNode node)
        {
            if (Port(node, "in_uv") is not { } uv)
            {
                return null;
            }

            Vector4 turn = Numbers(node, 0, Vector4.Zero);
            int angle = turn.X == 0f
                ? build.Constant(new Vector4(-turn.Y))
                : build.Emit(Op.MultiplyAdd, Clock, build.Constant(new Vector4(-turn.X)), build.Constant(new Vector4(-turn.Y)));

            int centre = build.Emit(Op.Multiply, build.Emit(Op.Sign, uv), build.Constant(new Vector4(0.5f)));
            return build.Emit(Op.Rotate, angle, uv, centre);
        }

        /// <summary>The vertex position as the vertex stage hands it on: the position, with a w of one.</summary>
        private int Placed()
        {
            int said = build.Emit(Op.Swizzle, Position, extra: (1 << 2) | (2 << 4) | (0b0111 << 8));
            build.Into(said, build.Constant(Vector4.One), 0b1000 << 8);
            return said;
        }

        /// <summary>
        /// LookUpTexture, from effects.ffx: a row of a texture read by the colour's alpha, scaled.
        /// </summary>
        /// <remarks>
        /// <c>tex2Dlod(lookup, float4(saturate(a * scale) * 0.95 + 0.025, 0, 0.5, -0.5))</c>, then
        /// <c>rgb *= mult</c> - the texture at parameter 0, the multiplier at 2 and the scale at 3, both
        /// declared 1. The fragment takes a <c>uv</c> and never reads it; a level below nought is the
        /// top one. Its <c>a</c> output is never assigned, and is refused in Brought.
        /// </remarks>
        private int? LookedUp(ShaderNode node)
        {
            if (Texture(node, Parameter(node, 0)) is not { } table || Port(node, "source_albedo") is not { } source)
            {
                return null;
            }

            float mult = Said(node, 2, 1f);
            float scale = Said(node, 3, 1f);
            int alpha = build.Emit(Op.Swizzle, source, extra: 3 | (3 << 2) | (3 << 4) | (3 << 6) | (0b1111 << 8));
            int along = build.Emit(
                Op.MultiplyAdd,
                build.Emit(Op.Saturate, build.Emit(Op.Multiply, alpha, build.Constant(new Vector4(scale)))),
                build.Constant(new Vector4(0.95f)),
                build.Constant(new Vector4(0.025f)));
            int spot = build.Register();
            build.Steps.Add(new Step(Op.Clear, spot, -1, -1, -1, -1, -1, 0));
            build.Into(spot, along, 0b0001 << 8);
            build.Into(spot, build.Constant(new Vector4(0.5f)), 0b0010 << 8);
            int read = build.Emit(Op.SampleLod, spot, build.Constant(new Vector4(-0.5f)), extra: table);
            return build.Emit(Op.Multiply, read, build.Constant(new Vector4(mult, mult, mult, 1f)));
        }

        /// <summary>
        /// Transform by the TBN basis, where the vector lies along the basis's normal alone.
        /// </summary>
        /// <remarks>
        /// <c>mul(input, inmatrix)</c> with the basis's rows tangent, binormal and normal, so
        /// <c>Transform(float3(0, 0, 1), InputTbnBasis)</c> - AGT_DesertDust's and the parallax's
        /// surface normal - is the normal row. The mesh has no tangents, so a vector with any part
        /// along the other two rows is refused; and the result is a direction only - see Brought.
        /// </remarks>
        private int? Transformed(ShaderNode node)
        {
            if (Port(node, "inmatrix") is not { } matrix || Port(node, "input") is not { } vector)
            {
                return null;
            }

            if (matrix != Basis)
            {
                return Fail("Transform by a matrix other than the TBN basis");
            }

            if (!build.ConstantOf(vector, out Vector4 along) || along.X != 0f || along.Y != 0f)
            {
                return Fail("Transform of a vector along the tangent or binormal, which the mesh does not have");
            }

            int said = build.Emit(Op.Multiply, VertexNormal, build.Constant(new Vector4(along.Z)));
            build.Directional(said);
            return said;
        }

        /// <summary>A one-input node's input, whatever its port is called - there is only the one, and nought where it is unlinked.</summary>
        private int? Single(ShaderNode node)
        {
            string[] ports = [.. lookup.Into(node).Select(one => one.Target.Variable).Distinct(StringComparer.Ordinal)];
            return ports.Length switch
            {
                0 => build.Constant(Vector4.Zero),
                1 => Port(node, ports[0]),
                _ => Fail($"{node.Type} with {ports.Length} inputs"),
            };
        }

        /// <summary>The texture an InputTexture hands a node through its texture port - <c>in_texture</c>, or <c>tex</c> on the dispersed read.</summary>
        private int? Handed(ShaderNode node, string port = "in_texture")
        {
            ShaderLink[] links = [.. lookup.Into(node).Where(one => one.Target.Variable == port)];
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
        /// <c>{}</c> on the material's side leaves the node's own. An entry neither side fills is the
        /// fragment's declared default - see the class's remarks - which for the constants and the
        /// Const nodes is nought, as <c>"UseRoughness": [{}]</c> beside <c>"Sharpness": [{"value":
        /// 1.0}]</c> shows.
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

        /// <summary>A one-number parameter: as written, or the fragment's declared default where left out - see the class's remarks.</summary>
        private float Said(ShaderNode node, int at, float declared)
        {
            float[] numbers = Parameter(node, at).Numbers;
            return numbers.Length > 0 ? numbers[0] : declared;
        }

        /// <summary>A vector parameter: as written, or the fragment's declared default where left out.</summary>
        private Vector4 Numbers(ShaderNode node, int at, Vector4 declared)
        {
            float[] numbers = Parameter(node, at).Numbers;
            return numbers.Length > 0 ? Vectored(numbers) : declared;
        }

        private int? Fail(string why)
        {
            if (Why.Length == 0)
            {
                Why = why;
            }

            return null;
        }

        /// <summary>The ports a direction of unknown length may go to: where it is normalised before use, or passed through.</summary>
        private static bool Directional(string type, string port)
            => (type, port) is ("SampleInputTriplanar", "world_normal") or ("SampleTriplanar", "world_normal")
                || type is "Normalize2" or "Normalize3" or "Normalize4" or "Dummy3" or "Dummy4";

        /// <summary>The width on the end of a node's type: 3 for Normalize3.</summary>
        private static int Width(string type) => type[^1] - '0';

        /// <summary>Which components a swizzle reads, all four for none.</summary>
        private static int Mask(string swizzle)
        {
            if (swizzle.Length == 0)
            {
                return 0b1111;
            }

            var mask = 0;
            foreach (char letter in swizzle)
            {
                int part = Component(letter);
                if (part >= 0)
                {
                    mask |= 1 << part;
                }
            }

            return mask;
        }

        private static string Letters(int mask)
            => string.Concat(Enumerable.Range(0, 4).Where(part => (mask & (1 << part)) != 0).Select(part => "xyzw"[part]));

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
