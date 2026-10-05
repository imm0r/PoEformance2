using System.Globalization;
using System.Text.Json;

namespace PoEformance.Game.Files;

/// <summary>
/// One parameter of a graph node, or one a material hands it: whichever of a path, a value and
/// an sRGB flag it carries.
/// </summary>
/// <param name="Path">A texture's path, or empty.</param>
/// <param name="Numbers">A value as numbers - one for a scalar or a bool, more for a vector - or empty.</param>
/// <param name="Srgb">Whether a texture is read as sRGB, where it says.</param>
public sealed record ShaderValue(string Path, float[] Numbers, bool? Srgb)
{
    /// <summary>A parameter that says nothing - written <c>{}</c>.</summary>
    public static ShaderValue Empty { get; } = new(string.Empty, [], null);

    /// <summary>Whether it carries anything at all.</summary>
    public bool Said => Path.Length > 0 || Numbers.Length > 0 || Srgb is not null;

    /// <summary>
    /// This parameter with whatever <paramref name="over"/> says laid over it, member by member.
    /// </summary>
    /// <remarks>
    /// MEMBER BY MEMBER, because a material's override is written the way the node's default is -
    /// <c>{"path": ..., "srgb": true}</c> over <c>{"format": "BC1", "srgb": false}</c> - and what it
    /// does not mention stays the node's.
    /// </remarks>
    public ShaderValue Under(ShaderValue over)
        => over.Said
            ? new ShaderValue(
                over.Path.Length > 0 ? over.Path : Path,
                over.Numbers.Length > 0 ? over.Numbers : Numbers,
                over.Srgb ?? Srgb)
            : this;
}

/// <summary>One node of a shader graph: what it is, which of its kind, at which stage, and its parameters.</summary>
/// <param name="Type">The node type as the graph names it - <c>SampleTexture</c>, <c>Lerp3</c>.</param>
/// <param name="Index">Which node of that type - links name a node by type and index.</param>
/// <param name="Stage">The stage it reads or writes at, for the nodes that have one; empty for the rest.</param>
/// <param name="Parameters">Its parameters, in order.</param>
/// <param name="Custom">The name a material can set its parameters by, or empty.</param>
public sealed record ShaderNode(string Type, int Index, string Stage, ShaderValue[] Parameters, string Custom);

/// <summary>One end of a link: a node's port, and which components of it.</summary>
/// <param name="Type">The node's type.</param>
/// <param name="Index">The node's index among its type.</param>
/// <param name="Stage">The node's stage, where it has one.</param>
/// <param name="Variable">The port - <c>output</c>, <c>uv</c>, <c>alpha</c>.</param>
/// <param name="Swizzle">The components, <c>xyz</c> or <c>w</c>, or empty for the whole value.</param>
public readonly record struct ShaderEnd(string Type, int Index, string Stage, string Variable, string Swizzle);

/// <summary>A link from one node's output to another's input.</summary>
public readonly record struct ShaderLink(ShaderEnd Source, ShaderEnd Target);

/// <summary>A graph instance in a material: which graph, and the parameters the material sets on it.</summary>
/// <param name="Parent">The graph's path.</param>
/// <param name="Custom">Parameter lists by the custom name the graph's nodes expose.</param>
public sealed record ShaderInstance(string Parent, IReadOnlyDictionary<string, ShaderValue[]> Custom);

/// <summary>
/// A shader graph - an <c>.fxgraph</c> - as nodes and links.
/// </summary>
/// <remarks>
/// JSON, written by the game's own graph editor: a list of nodes, each a type, an index among its
/// type and sometimes a stage, and a list of links naming both ends by type, index and port. A
/// material is a list of instances of such graphs, each with the parameters it sets; what the
/// graph computes from them is written nowhere but here. See <see cref="ShadeProgram"/> for what
/// is done with it.
///
/// NO REFERENCE READS THIS FORMAT. annalithic's Mat.cs reads a material's <c>parent</c> and stops;
/// zao reads the blend mode off it; neither walks the nodes. So the reader keeps everything the
/// file says and decides nothing - meaning is the compiler's business, and refusing what it does
/// not know is too.
/// </remarks>
public sealed class ShaderGraph
{
    private static readonly JsonDocumentOptions Lenient = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private ShaderGraph()
    {
        Nodes = [];
        Links = [];
    }

    /// <summary>Nothing read.</summary>
    public static ShaderGraph None { get; } = new();

    /// <summary>The nodes, in file order.</summary>
    public IReadOnlyList<ShaderNode> Nodes { get; private init; }

    /// <summary>The links, in file order.</summary>
    public IReadOnlyList<ShaderLink> Links { get; private init; }

    /// <summary>Whether the file read as a graph.</summary>
    public bool Ready => Nodes.Count > 0 || Links.Count > 0;

    /// <summary>Reads a graph file's bytes; <see cref="None"/> for anything that is not one.</summary>
    public static ShaderGraph Read(byte[]? content)
    {
        if (content is not { Length: > 0 })
        {
            return None;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(StatDescriptionFiles.Decode(content), Lenient);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return None;
            }

            var nodes = new List<ShaderNode>();
            if (root.TryGetProperty("nodes", out JsonElement listed) && listed.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement node in listed.EnumerateArray())
                {
                    if (node.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    nodes.Add(new ShaderNode(
                        Text(node, "type"),
                        Whole(node, "index"),
                        Text(node, "stage"),
                        node.TryGetProperty("parameters", out JsonElement parameters) ? Values(parameters) : [],
                        Text(node, "custom_parameter")));
                }
            }

            var links = new List<ShaderLink>();
            if (root.TryGetProperty("links", out JsonElement joined) && joined.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement link in joined.EnumerateArray())
                {
                    if (link.ValueKind == JsonValueKind.Object
                        && link.TryGetProperty("src", out JsonElement from) && from.ValueKind == JsonValueKind.Object
                        && link.TryGetProperty("dst", out JsonElement to) && to.ValueKind == JsonValueKind.Object)
                    {
                        links.Add(new ShaderLink(End(from), End(to)));
                    }
                }
            }

            return new ShaderGraph { Nodes = nodes, Links = links };
        }
        catch (JsonException)
        {
            return None;
        }
    }

    /// <summary>
    /// The graph instances a material lists, in order, with the parameters it sets on each.
    /// </summary>
    /// <remarks>
    /// IN ORDER, because order is meaning: two graphs writing the colour at the same stage leave the
    /// later one's - TallDune1c lists BasicColour and then PBRGround, both at Texturing_Init.
    /// </remarks>
    public static IReadOnlyList<ShaderInstance> Instances(byte[]? material)
    {
        if (material is not { Length: > 0 })
        {
            return [];
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(StatDescriptionFiles.Decode(material), Lenient);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("graphinstances", out JsonElement listed)
                || listed.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var instances = new List<ShaderInstance>();
            foreach (JsonElement instance in listed.EnumerateArray())
            {
                if (instance.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var custom = new Dictionary<string, ShaderValue[]>(StringComparer.Ordinal);
                if (instance.TryGetProperty("custom_parameters", out JsonElement set) && set.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement one in set.EnumerateArray())
                    {
                        string name = one.ValueKind == JsonValueKind.Object ? Text(one, "name") : string.Empty;
                        if (name.Length > 0 && one.TryGetProperty("parameters", out JsonElement parameters))
                        {
                            custom[name] = Values(parameters);
                        }
                    }
                }

                instances.Add(new ShaderInstance(Text(instance, "parent"), custom));
            }

            return instances;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static ShaderEnd End(JsonElement end)
        => new(Text(end, "type"), Whole(end, "index"), Text(end, "stage"), Text(end, "variable"), Text(end, "swizzle"));

    private static ShaderValue[] Values(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new ShaderValue[parameters.GetArrayLength()];
        var at = 0;
        foreach (JsonElement one in parameters.EnumerateArray())
        {
            values[at++] = one.ValueKind == JsonValueKind.Object ? Value(one) : ShaderValue.Empty;
        }

        return values;
    }

    /// <summary>
    /// One parameter object: <c>path</c>, <c>srgb</c> and <c>value</c>, each where present.
    /// </summary>
    /// <remarks>
    /// A VALUE IS A NUMBER, AN ARRAY OF THEM OR A BOOL, and all three become numbers - a bool as one
    /// or nought, which is what a shader reads it as. A string value is a sampler's name
    /// (<c>SamplerDynamicWrap</c>) and is not a number of anything, so it is left out.
    /// </remarks>
    private static ShaderValue Value(JsonElement one)
    {
        string path = Text(one, "path");
        bool? srgb = one.TryGetProperty("srgb", out JsonElement flag) && flag.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? flag.ValueKind == JsonValueKind.True
            : null;

        float[] numbers = [];
        if (one.TryGetProperty("value", out JsonElement value))
        {
            numbers = value.ValueKind switch
            {
                JsonValueKind.Number => [value.GetSingle()],
                JsonValueKind.True => [1f],
                JsonValueKind.False => [0f],
                JsonValueKind.Array => [.. value.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.Number).Select(n => n.GetSingle())],
                _ => [],
            };
        }

        return path.Length == 0 && numbers.Length == 0 && srgb is null ? ShaderValue.Empty : new ShaderValue(path, numbers, srgb);
    }

    private static string Text(JsonElement owner, string name)
        => owner.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int Whole(JsonElement owner, string name)
        => owner.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int number)
            ? number
            : 0;

    /// <summary>A node's key as links spell it.</summary>
    internal static string Key(string type, int index, string stage)
        => string.Create(CultureInfo.InvariantCulture, $"{type}#{index}@{stage}");
}
