using System.Globalization;
using System.Text;
using System.Text.Json;
using PoEformance.Game.Files;

namespace PoEformance.Features;

/// <summary>
/// Every key the install's .env files use, how many files use it and what values it takes - with a few whole files that light their area with a sun.
/// </summary>
/// <remarks>
/// WHY A SURVEY. An area's light is its .env (WorldAreas → Environments → Base_ENVFile), and the
/// one read so far - Seepage - turns its sun off: <c>directional_light</c> holds nothing but a
/// zero multiplier. Which keys give a sun its direction and colour cannot be read off a file that
/// has none, and neither reference parses the format at all. So every .env is read once and its
/// keys counted; the keys a sun uses are then a lookup, not a guess.
///
/// THE FORMAT IS JSON - Seepage.env is an object of sections, each an object of keys whose values
/// are numbers, strings, number arrays (colours) or arrays of objects. Keys are reported as
/// <c>section.key</c>, an array of objects as <c>key[].field</c>, so the shape can be read from the
/// list. A file that is not JSON is counted and its first error named.
///
/// THE EXAMPLES ARE THE PART THAT SETTLES IT: whole files whose <c>directional_light</c> carries
/// more than a multiplier, because what each key MEANS shows only beside the others.
/// </remarks>
public static class EnvSurvey
{
    /// <summary>What the survey's file, beside the dumps, is called.</summary>
    public const string File = "env-survey.txt";

    /// <summary>Distinct example values kept per key.</summary>
    private const int MostExamples = 4;

    /// <summary>Longest an example is printed.</summary>
    private const int MostExampleChars = 96;

    /// <summary>Whole files printed at the end.</summary>
    private const int MostWholeFiles = 4;

    /// <summary>Files that did not read as JSON, named before the rest are counted.</summary>
    private const int MostFailuresNamed = 12;

    private static readonly JsonDocumentOptions Lenient = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The survey's text.</summary>
    /// <param name="read">How to read a file out of the install, by path.</param>
    /// <param name="files">Every .env in the install.</param>
    /// <param name="step">Told how far it has got, now and then.</param>
    public static string Of(Func<string, byte[]?>? read, IReadOnlyList<string> files, Action<string>? step = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        var said = new StringBuilder();
        if (read is null)
        {
            return "no install to read the environments from";
        }

        var keys = new SortedDictionary<string, Key>(StringComparer.Ordinal);
        var cubes = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();
        var lit = new List<(string Path, string Text, int Keys)>();
        int parsed = 0, missing = 0;
        for (var at = 0; at < files.Count; at++)
        {
            if (at % 200 == 0)
            {
                step?.Invoke($"{at} of {files.Count} environments");
            }

            string path = files[at];
            byte[]? content = read(path);
            if (content is not { Length: > 0 })
            {
                missing++;
                continue;
            }

            string text = StatDescriptionFiles.Decode(content);
            try
            {
                using JsonDocument document = JsonDocument.Parse(text, Lenient);
                parsed++;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                Walk(document.RootElement, string.Empty, path, keys, seen);
                foreach ((string kind, string cube) in Cubes(document.RootElement))
                {
                    cubes.TryAdd(cube, kind);
                }
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("directional_light", out JsonElement sun)
                    && sun.ValueKind == JsonValueKind.Object
                    && sun.EnumerateObject().Any(one => one.Name != "multiplier" || (one.Value.ValueKind == JsonValueKind.Number && one.Value.GetDouble() > 0)))
                {
                    lit.Add((path, text, seen.Count));
                }
            }
            catch (JsonException fault)
            {
                failures.Add($"{path}: {fault.Message}");
            }
        }

        said.Append(Say(files.Count)).Append(" .env files in the install, ").Append(Say(parsed)).Append(" read as JSON, ")
            .Append(Say(failures.Count)).Append(" not, ").Append(Say(missing)).AppendLine(" could not be read");
        said.Append(Say(lit.Count)).AppendLine(" of them light their area with a sun: a directional_light with more than a zero multiplier");
        foreach (string failure in failures.Take(MostFailuresNamed))
        {
            said.Append("  not JSON: ").AppendLine(failure);
        }

        said.AppendLine().AppendLine("=== every key, in how many files, its kind, its range and some values");
        foreach ((string name, Key key) in keys)
        {
            said.Append(name).Append("  in ").Append(Say(key.Files)).Append("  ").Append(string.Join('/', key.Kinds.Order(StringComparer.Ordinal)));
            if (key.Numbers > 0)
            {
                said.Append("  ").Append(Num(key.Least)).Append(" .. ").Append(Num(key.Most));
            }

            if (key.Examples.Count > 0)
            {
                said.Append("  e.g. ").Append(string.Join(" | ", key.Examples));
            }

            said.AppendLine();
        }

        Formats(read, cubes, step, said);

        said.AppendLine().AppendLine("=== whole files with a sun, the most keys first");
        foreach ((string path, string text, _) in lit.OrderByDescending(one => one.Keys).ThenBy(one => one.Path, StringComparer.Ordinal).Take(MostWholeFiles))
        {
            said.Append("--- ").AppendLine(path).AppendLine(text.TrimEnd()).AppendLine();
        }

        return said.ToString();
    }

    /// <summary>The cube maps an environment names - its <c>environment_mapping</c>'s <c>*_cube</c> paths, by kind.</summary>
    public static IEnumerable<(string Kind, string Path)> Cubes(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("environment_mapping", out JsonElement mapping)
            || mapping.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (JsonProperty one in mapping.EnumerateObject())
        {
            if (one.Name.EndsWith("_cube", StringComparison.Ordinal) && one.Value.ValueKind == JsonValueKind.String
                && one.Value.GetString() is { Length: > 0 } path)
            {
                yield return (one.Name, path);
            }
        }
    }

    /// <summary>The cube maps an environment's text names, or none where it is not JSON.</summary>
    public static List<(string Kind, string Path)> Cubes(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            using JsonDocument document = JsonDocument.Parse(text, Lenient);
            return [.. Cubes(document.RootElement)];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// What the cube maps are stored as: each distinct header, how many cubes have it, and some of them.
    /// </summary>
    /// <remarks>
    /// THE AMBIENT LIGHT WAITS ON THIS. An area's diffuse cube is its ambient, and the picture cannot
    /// sample one it cannot decode; the decoder in use reads the first face of a few block formats and
    /// no HDR ones. The header says which formats - and sizes - are there to be read.
    /// </remarks>
    private static void Formats(Func<string, byte[]?> read, SortedDictionary<string, string> cubes, Action<string>? step, StringBuilder said)
    {
        said.AppendLine().Append("=== the cube maps the environments name, by how they are stored: ").Append(Say(cubes.Count)).AppendLine(" distinct");
        var formats = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        int at = 0;
        foreach ((string path, string kind) in cubes)
        {
            if (at++ % 50 == 0)
            {
                step?.Invoke($"cube {at} of {cubes.Count}");
            }

            string header = kind + ": " + ModelDump.Header(GameArt.ReadRaw(read, path));
            if (!formats.TryGetValue(header, out List<string>? paths))
            {
                paths = [];
                formats[header] = paths;
            }

            paths.Add(path);
        }

        foreach ((string header, List<string> paths) in formats.OrderByDescending(one => one.Value.Count))
        {
            said.Append(Say(paths.Count)).Append("  ").Append(header).Append("  e.g. ").AppendLine(string.Join(" | ", paths.Take(3)));
        }
    }

    /// <summary>Every key under an element, by its dotted name - each file counted once per key.</summary>
    private static void Walk(JsonElement element, string name, string path, SortedDictionary<string, Key> keys, HashSet<string> seen)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    Walk(property.Value, name.Length == 0 ? property.Name : name + "." + property.Name, path, keys, seen);
                }

                return;

            case JsonValueKind.Array when element.EnumerateArray().Any(one => one.ValueKind == JsonValueKind.Object):
                foreach (JsonElement one in element.EnumerateArray())
                {
                    Walk(one, name + "[]", path, keys, seen);
                }

                return;
        }

        if (name.Length == 0)
        {
            return;
        }

        if (!keys.TryGetValue(name, out Key? key))
        {
            key = new Key();
            keys[name] = key;
        }

        if (seen.Add(name))
        {
            key.Files++;
        }

        key.Kinds.Add(element.ValueKind switch
        {
            JsonValueKind.Number => "number",
            JsonValueKind.String => "string",
            JsonValueKind.True or JsonValueKind.False => "bool",
            JsonValueKind.Array => $"array of {element.GetArrayLength()}",
            _ => element.ValueKind.ToString().ToLowerInvariant(),
        });

        if (element.ValueKind == JsonValueKind.Number)
        {
            key.Count(element.GetDouble());
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement one in element.EnumerateArray().Where(one => one.ValueKind == JsonValueKind.Number))
            {
                key.Count(one.GetDouble());
            }
        }

        if (key.Examples.Count < MostExamples)
        {
            string raw = element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : element.GetRawText();
            raw = string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (raw.Length > MostExampleChars)
            {
                raw = raw[..MostExampleChars] + "...";
            }

            if (!key.Examples.Contains(raw))
            {
                key.Examples.Add(raw);
            }
        }
    }

    private static string Say(int number) => number.ToString(CultureInfo.InvariantCulture);

    private static string Num(double number) => number.ToString("0.#####", CultureInfo.InvariantCulture);

    /// <summary>What one key was seen as, over the whole install.</summary>
    private sealed class Key
    {
        public int Files { get; set; }

        public HashSet<string> Kinds { get; } = new(StringComparer.Ordinal);

        public List<string> Examples { get; } = [];

        public int Numbers { get; private set; }

        public double Least { get; private set; } = double.MaxValue;

        public double Most { get; private set; } = double.MinValue;

        public void Count(double value)
        {
            Numbers++;
            Least = Math.Min(Least, value);
            Most = Math.Max(Most, value);
        }
    }
}
