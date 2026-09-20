using System.Text;
using System.Text.Json;

namespace PoEformance.Game.Files;

/// <summary>One texture a material names, and what the game says it is.</summary>
/// <param name="Path">The <c>.dds</c> file. <see cref="GameArt"/> decodes it.</param>
/// <param name="Format">As written: <c>DXT1</c>, <c>DXT5</c>, <c>BC7</c>. Empty where unsaid.</param>
public readonly record struct MaterialTexture(string Path, string Format);

/// <summary>
/// A <c>.mat</c> file: which textures go on a mesh, and which slot each one fills.
/// </summary>
/// <remarks>
/// THE HOP THAT ANSWERS "IS THERE A PICTURE". An .ao names a .sm, a .sm names a .smd and a .mat,
/// and only the .mat names a .dds - which is why a survey of the .ao files found thirteen file
/// types and no texture among them, and why that read like "there is none" and was not.
///
/// PLAIN JSON, which is the pleasant surprise in a game whose other formats are diagrams. Two
/// things in the spec are worth honouring anyway and neither is visible in a sample:
///
///   - TRAILING COMMAS ARE ALLOWED. System.Text.Json rejects those by default, so some fraction
///     of materials would have failed to load and looked like a monster with no skin.
///   - THE FILE MAY BE UTF-16, with or without a mark. Both PoE2 files checked came back as
///     UTF-8, so the spec is probably PoE1-era - and the decode handles all three shapes either
///     way, which costs nothing and settles it.
///
/// READ WITH A SCANNER RATHER THAN DESERIALISED, and not only for Native AOT. A parameter's
/// "value" is a single float in some materials and a list of them in others, and a shape that
/// models that faithfully is a lot of type for two strings - while a reader that skips what it
/// does not want cannot trip over it at all.
///
/// THE SLOT NAMES ARE THE GAME'S OWN WORDS - AlbedoTransparency_TEX, NormalGlossAO_TEX - so what
/// a texture is FOR does not have to be guessed from its file name. See <see cref="Albedo"/>.
/// </remarks>
public sealed class MaterialFile
{
    /// <summary>Nothing read - a missing file, or one that is not a material.</summary>
    public static MaterialFile None { get; } = new();

    private MaterialFile()
    {
        Textures = [];
        Slots = new Dictionary<string, string>(StringComparer.Ordinal);
        Graphs = [];
    }

    /// <summary>Every texture the file lists, in the order it lists them.</summary>
    public IReadOnlyList<MaterialTexture> Textures { get; private init; }

    /// <summary>Slot name to the texture path filling it, as the shader graphs assign them.</summary>
    public IReadOnlyDictionary<string, string> Slots { get; private init; }

    /// <summary>
    /// The same slots kept per graph instance, in file order - which is what a <c>:n</c> selects.
    /// </summary>
    /// <remarks>
    /// THE NUMBER AFTER THE MATERIAL PICKS ONE OF THESE, and that was written down in this file
    /// and then not acted on: an .ao names a material per shape as <c>…/Boss.mat:0</c>,
    /// <c>…/Boss.mat:1</c>, and the remark on <see cref="Bare"/> says outright that the number
    /// picks WITHIN the file. Merged into one dictionary, every shape of such a monster reads
    /// the FIRST graph's colour map - which paints the head's sheet onto the cloak and reads as
    /// patches of the wrong colour. Reported from the live client on three bosses in a row:
    /// Bahlak, Connal and Count Geonor.
    ///
    /// <see cref="Slots"/> is kept beside it and is still the merged view, because the caller
    /// with no selector to go on wants exactly that: the file's colour map, whichever graph
    /// happens to carry it.
    /// </remarks>
    public IReadOnlyList<IReadOnlyDictionary<string, string>> Graphs { get; private init; }

    /// <summary>Whether anything was read.</summary>
    public bool Ready => Textures.Count > 0 || Slots.Count > 0;

    /// <summary>
    /// The colour texture - what somebody means by "the monster's skin".
    /// </summary>
    /// <remarks>
    /// THE SLOT NAME DECIDES IT WHERE THERE IS ONE, which is the whole reason the slots are read:
    /// AlbedoTransparency_TEX says what the texture is for in the game's own words, where a file
    /// name only hints. Falling back to the first texture listed is a guess and is marked as one -
    /// it is right on the materials seen so far, where the colour map is written first, and it is
    /// the sort of rule that is wrong quietly.
    ///
    /// A NORMAL MAP IS NEVER THE ANSWER. Its name gives it away in both places, and drawn as
    /// colour it produces a lilac monster - which looks like a shading bug rather than a wrong
    /// texture, and would be hunted in the renderer.
    /// </remarks>
    public string Albedo
    {
        get
        {
            if (Coloured(Slots) is { Length: > 0 } named)
            {
                return named;
            }

            foreach (MaterialTexture one in Textures)
            {
                if (!Normal(one.Path))
                {
                    return one.Path;
                }
            }

            return string.Empty;
        }
    }

    private static bool Normal(string path)
        => path.Contains("_normal", StringComparison.OrdinalIgnoreCase)
            || path.Contains("NormalGloss", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The colour texture of ONE graph instance - what a <c>…/Boss.mat:1</c> asks for.
    /// </summary>
    /// <remarks>
    /// FALLS BACK TO THE WHOLE FILE, which is the same trade the rest of this makes: a
    /// selector pointing past the graphs, or at one that carries no colour map, leaves the
    /// shape with the material's own answer rather than with nothing. That is what it had
    /// before the selector was read at all, so nothing is made worse by a file this does not
    /// understand.
    /// </remarks>
    /// <param name="at">The number after the colon, or negative for "no selector given".</param>
    public string AlbedoAt(int at) => AlbedoOf(at).Path;

    /// <summary>
    /// The colour texture for a selector, and whether a SLOT NAME said so or it was a guess.
    /// </summary>
    /// <remarks>
    /// THE SECOND HALF IS THE ONE WORTH HAVING. Where no slot carries Albedo, Colour or Color,
    /// the answer is "the first texture that is not a normal map" - which is right on the
    /// materials that have been looked at and is still a guess, and a guess that lands on a
    /// mask or an ambient-occlusion map paints the monster in greyscale. That is a picture
    /// somebody has to squint at and call "missing texture" (reported on the Vessel of
    /// Kulemak), where being told "nothing said which map is the colour one" is an answer.
    /// </remarks>
    public (string Path, bool Named) AlbedoOf(int at)
    {
        if (at >= 0 && at < Graphs.Count && Coloured(Graphs[at]) is { Length: > 0 } found)
        {
            return (found, true);
        }

        return Coloured(Slots) is { Length: > 0 } named ? (named, true) : (Albedo, false);
    }

    /// <summary>The slot in one graph whose NAME says it is the colour map, or empty.</summary>
    private static string Coloured(IReadOnlyDictionary<string, string> slots)
    {
        foreach ((string slot, string path) in slots)
        {
            if (slot.Contains("Albedo", StringComparison.OrdinalIgnoreCase)
                || slot.Contains("Colour", StringComparison.OrdinalIgnoreCase)
                || slot.Contains("Color", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// The number after a material path's colon, or -1 where it carries none.
    /// </summary>
    /// <remarks>
    /// The other half of <see cref="Bare"/>: that one answers "which file", this one answers
    /// "which of the graphs in it". Both exist so no caller has to know how the colon works.
    /// </remarks>
    public static int SelectorOf(string? path)
    {
        if (path is not { Length: > 0 })
        {
            return -1;
        }

        string said = path.Replace('\\', '/').Trim();
        int colon = said.LastIndexOf(':');

        return colon > said.LastIndexOf('.') && colon >= 0 && Digits(said, colon + 1)
            && int.TryParse(said[(colon + 1)..], out int at)
            ? at
            : -1;
    }

    /// <summary>Reads one out of an open install. The path may carry a <c>:n</c> selector.</summary>
    /// <remarks>
    /// THE SELECTOR IS NOT PART OF THE NAME. An .ao names a material per shape as
    /// <c>…/Skeleton.mat:0</c>, and asking the install for that path finds nothing - the number
    /// picks within the file. It is cut here so every caller does not have to remember.
    /// </remarks>
    public static MaterialFile Read(GameFiles? files, string? path)
        => files is null || string.IsNullOrWhiteSpace(path) ? None : Read(files.Read(Bare(path)));

    /// <summary>
    /// A material path with any <c>:n</c> selector taken off - the file it actually names.
    /// </summary>
    /// <remarks>
    /// PUBLIC BECAUSE THE INSTALL IS NOT THE ONLY CALLER. Anything reading these files through a
    /// function rather than through <see cref="GameFiles"/> needs the same rule, and a second copy
    /// of it is a second place to forget the colon - which fails by finding nothing, silently.
    /// </remarks>
    public static string Bare(string? path)
    {
        if (path is not { Length: > 0 })
        {
            return string.Empty;
        }

        string said = path.Replace('\\', '/').Trim();
        int colon = said.LastIndexOf(':');
        return colon > said.LastIndexOf('.') && colon >= 0 && Digits(said, colon + 1)
            ? said[..colon]
            : said;
    }

    /// <summary>Reads a file's bytes, sharing the decode with the game's other text files.</summary>
    public static MaterialFile Read(byte[]? content)
        => content is not { Length: > 0 } ? None : Parse(StatDescriptionFiles.Decode(content));

    /// <summary>Reads the text. Public so the format can be tested without an install.</summary>
    public static MaterialFile Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return None;
        }

        var textures = new List<MaterialTexture>();
        var slots = new Dictionary<string, string>(StringComparer.Ordinal);
        var graphs = new List<IReadOnlyDictionary<string, string>>();

        try
        {
            var reader = new Utf8JsonReader(
                Encoding.UTF8.GetBytes(text),
                new JsonReaderOptions
                {
                    // The spec says trailing commas occur. Without this a material carrying one
                    // throws, and the monster simply has no skin for a reason nothing reports.
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                });

            Walk(ref reader, textures, slots, graphs);
        }
        catch (JsonException)
        {
            // A material that is not JSON at all costs its own textures and nothing else: the
            // mesh is already in hand by the time this is asked for, and a grey monster beats no
            // monster.
            return None;
        }

        return textures.Count == 0 && slots.Count == 0
            ? None
            : new MaterialFile { Textures = textures, Slots = slots, Graphs = graphs };
    }

    /// <summary>The top-level object, taking the two members that name files.</summary>
    private static void Walk(
        ref Utf8JsonReader reader,
        List<MaterialTexture> textures,
        Dictionary<string, string> slots,
        List<IReadOnlyDictionary<string, string>> graphs)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return;
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            if (reader.ValueTextEquals("textures"u8))
            {
                reader.Read();
                Textured(ref reader, textures);
                continue;
            }

            if (reader.ValueTextEquals("graphinstances"u8))
            {
                reader.Read();
                Slotted(ref reader, slots, graphs);
                continue;
            }

            reader.Read();
            reader.Skip();
        }
    }

    /// <summary>The textures array: each entry's filename and format.</summary>
    private static void Textured(ref Utf8JsonReader reader, List<MaterialTexture> textures)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return;
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }

            var path = string.Empty;
            var format = string.Empty;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                bool wantsPath = reader.ValueTextEquals("filename"u8);
                bool wantsFormat = reader.ValueTextEquals("format"u8);

                reader.Read();
                if (wantsPath && reader.TokenType == JsonTokenType.String)
                {
                    path = reader.GetString() ?? string.Empty;
                }
                else if (wantsFormat && reader.TokenType == JsonTokenType.String)
                {
                    format = reader.GetString() ?? string.Empty;
                }
                else
                {
                    reader.Skip();
                }
            }

            if (path.Length > 0)
            {
                textures.Add(new MaterialTexture(path, format));
            }
        }
    }

    /// <summary>
    /// The graph instances, for the slot each texture fills.
    /// </summary>
    /// <remarks>
    /// THREE LEVELS DOWN AND WORTH IT: graphinstances, then custom_parameters, then parameters,
    /// where a "name" like AlbedoTransparency_TEX sits beside a "path". Everything else in there -
    /// curves, variances, blend modes - is skipped by the reader rather than modelled.
    /// </remarks>
    private static void Slotted(
        ref Utf8JsonReader reader,
        Dictionary<string, string> slots,
        List<IReadOnlyDictionary<string, string>> graphs)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return;
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }

            // ONE DICTIONARY PER GRAPH, kept in file order - the ":n" after a material picks
            // by that order - and merged into the flat one as well, for the caller that has
            // no selector to go on. Every instance is kept, including an empty one, because
            // the number counts graphs and not graphs-that-had-something-in-them.
            var graph = new Dictionary<string, string>(StringComparer.Ordinal);

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                bool wanted = reader.ValueTextEquals("custom_parameters"u8);
                reader.Read();

                if (wanted)
                {
                    Parameters(ref reader, graph);
                    continue;
                }

                reader.Skip();
            }

            graphs.Add(graph);
            foreach ((string slot, string path) in graph)
            {
                slots.TryAdd(slot, path);
            }
        }
    }

    /// <summary>One graph's custom parameters: a name, and the first path under it.</summary>
    private static void Parameters(ref Utf8JsonReader reader, Dictionary<string, string> slots)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return;
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }

            var name = string.Empty;
            var path = string.Empty;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                bool wantsName = reader.ValueTextEquals("name"u8);
                bool wantsParameters = reader.ValueTextEquals("parameters"u8);

                reader.Read();

                if (wantsName && reader.TokenType == JsonTokenType.String)
                {
                    name = reader.GetString() ?? string.Empty;
                    continue;
                }

                if (wantsParameters)
                {
                    path = Pathed(ref reader);
                    continue;
                }

                reader.Skip();
            }

            // THE SLOT NAMES CARRY DECORATION IN SOME MATERIALS - "- Glow_TEX", "00. Intensity" -
            // so the leading punctuation is cut and the rest kept as written. A name with no path
            // under it is a number rather than a texture, and is not a slot.
            name = name.TrimStart(' ', '-', '.', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            if (name.Length > 0 && path.Length > 0)
            {
                slots.TryAdd(name, path);
            }
        }
    }

    /// <summary>The first "path" inside a parameters array, or empty.</summary>
    private static string Pathed(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return string.Empty;
        }

        var found = string.Empty;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                bool wanted = reader.ValueTextEquals("path"u8);
                reader.Read();

                if (wanted && reader.TokenType == JsonTokenType.String && found.Length == 0)
                {
                    found = reader.GetString() ?? string.Empty;
                    continue;
                }

                reader.Skip();
            }
        }

        return found;
    }

    private static bool Digits(string said, int from)
    {
        if (from >= said.Length)
        {
            return false;
        }

        for (int at = from; at < said.Length; at++)
        {
            if (!char.IsAsciiDigit(said[at]))
            {
                return false;
            }
        }

        return true;
    }
}
