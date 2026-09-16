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
    }

    /// <summary>Every texture the file lists, in the order it lists them.</summary>
    public IReadOnlyList<MaterialTexture> Textures { get; private init; }

    /// <summary>Slot name to the texture path filling it, as the shader graphs assign them.</summary>
    public IReadOnlyDictionary<string, string> Slots { get; private init; }

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
            foreach ((string slot, string path) in Slots)
            {
                if (slot.Contains("Albedo", StringComparison.OrdinalIgnoreCase)
                    || slot.Contains("Colour", StringComparison.OrdinalIgnoreCase)
                    || slot.Contains("Color", StringComparison.OrdinalIgnoreCase))
                {
                    return path;
                }
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

    /// <summary>Reads one out of an open install. The path may carry a <c>:n</c> selector.</summary>
    /// <remarks>
    /// THE SELECTOR IS NOT PART OF THE NAME. An .ao names a material per shape as
    /// <c>…/Skeleton.mat:0</c>, and asking the install for that path finds nothing - the number
    /// picks within the file. It is cut here so every caller does not have to remember.
    /// </remarks>
    public static MaterialFile Read(GameFiles? files, string? path)
    {
        if (files is null || string.IsNullOrWhiteSpace(path))
        {
            return None;
        }

        string said = path.Replace('\\', '/').Trim();
        int colon = said.LastIndexOf(':');
        if (colon > said.LastIndexOf('.') && colon >= 0 && Digits(said, colon + 1))
        {
            said = said[..colon];
        }

        return Read(files.Read(said));
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

            Walk(ref reader, textures, slots);
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
            : new MaterialFile { Textures = textures, Slots = slots };
    }

    /// <summary>The top-level object, taking the two members that name files.</summary>
    private static void Walk(
        ref Utf8JsonReader reader, List<MaterialTexture> textures, Dictionary<string, string> slots)
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
                Slotted(ref reader, slots);
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
    private static void Slotted(ref Utf8JsonReader reader, Dictionary<string, string> slots)
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
                    Parameters(ref reader, slots);
                    continue;
                }

                reader.Skip();
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
