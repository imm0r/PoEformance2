using System.Globalization;
using PoEformance.Game.Entities;
using PoEformance.Game.Files;

namespace PoEformance.Game.Diagnostics;

/// <summary>What one <c>.ao</c> file, read and followed, turned out to hold.</summary>
/// <param name="Path">The file, as the table or the referring file spelled it.</param>
/// <param name="Object">What was read, or <see cref="AnimatedObject.None"/> when nothing was.</param>
/// <param name="Depth">How many hops from the monster's own file this one was.</param>
public sealed record AoRead(string Path, AnimatedObject Object, int Depth);

/// <summary>
/// What the monsters' Animated Object files hold, counted over a real install.
/// </summary>
/// <param name="Monsters">How many monsters were looked at.</param>
/// <param name="WithFiles">How many of them name at least one .ao file.</param>
/// <param name="Asked">How many distinct files were asked for, following extends and references.</param>
/// <param name="Read">How many of those the install actually had.</param>
/// <param name="Clean">How many parsed with no fault at all.</param>
/// <param name="Structs">Struct type to how many times it appeared.</param>
/// <param name="Keys">Struct type and entry key to how many times that pair appeared.</param>
/// <param name="Extensions">Every file extension referenced from a value, to how often.</param>
/// <param name="Animations">What the animation entries' values were, to how often.</param>
/// <param name="Faults">The parse faults, with the file that produced each.</param>
public sealed record AoSurveyResult(
    int Monsters,
    int WithFiles,
    int Asked,
    int Read,
    int Clean,
    IReadOnlyDictionary<string, int> Structs,
    IReadOnlyDictionary<string, int> Keys,
    IReadOnlyDictionary<string, int> Extensions,
    IReadOnlyDictionary<string, int> Animations,
    IReadOnlyList<string> Faults)
{
    /// <summary>Nothing walked - no install, or no monster names a file.</summary>
    public static AoSurveyResult Nothing { get; }
        = new(0, 0, 0, 0, 0, new Dictionary<string, int>(), new Dictionary<string, int>(),
            new Dictionary<string, int>(), new Dictionary<string, int>(), []);
}

/// <summary>
/// Walks the .ao files the monster table names, and reports what is in them.
/// </summary>
/// <remarks>
/// WRITTEN TO MEASURE, NOT TO DECIDE. Two questions are open that nothing offline can settle, and
/// this exists so that one run on a machine with the game settles both:
///
///   1. IS THERE A PICTURE. The dat tables say no - AOFiles and ACTFiles are 3D assets and
///      MonsterVarietiesArtVariations carries no art file - but that is an answer about the
///      TABLES, and an .ao is a file the tables only point at. So every value that looks like a
///      path is counted by extension, with nothing assumed about which extensions matter. If a
///      .dds turns up here, the rest of the chain to draw it already exists - GameArt decodes
///      BC1-BC7 and the overlay uploads textures today for item icons.
///   2. WHAT IS stance2. The Stance column is free text out of these same files, and the entry
///      shape the reference describes has animations carrying a NAME. So the animation values are
///      counted too: if "stance2" is among them, the column resolves to something a reader can
///      use, and a comment in the Monster Book that says it cannot is wrong and comes out.
///
/// AND THE FAULTS ARE PART OF THE ANSWER. The reader was written against a format diagram and a
/// prototype parser, with no real .ao file anywhere on the machine that wrote it. So the count of
/// files that parsed cleanly is the first thing to look at: a survey with thousands of faults is
/// measuring the reader, not the game.
///
/// THE GRAPH IS FOLLOWED, NOT JUST THE FIRST FILE. A monster's own .ao is often thin - it extends
/// a base and attaches objects - so stopping at the named file would report that monsters have
/// almost nothing, which is false and would look like an answer. Every file is read once however
/// many monsters reach it, which is also what keeps this from being quadratic.
/// </remarks>
public static class AoSurvey
{
    /// <summary>How far the extends and reference chain is followed before giving up.</summary>
    /// <remarks>A file that references itself is a loop; the visited set catches that, this caps depth.</remarks>
    public const int MostHops = 6;

    /// <summary>How many faults are kept. Past this the count is what matters, not the text.</summary>
    public const int MostFaults = 40;

    /// <summary>The entry keys whose values are another .ao to follow. From the format diagram.</summary>
    private static readonly string[] Follows =
    [
        "ao", "fixed_ao", "attached_object", "attached_slaved_animation_object",
    ];

    /// <summary>
    /// Reads every .ao the given monsters name, following what those files point at.
    /// </summary>
    /// <param name="files">The open install. Null gives <see cref="AoSurveyResult.Nothing"/>.</param>
    /// <param name="table">The monster table - the install's own, since the export has no AOFiles.</param>
    /// <param name="match">
    /// Only monsters whose path or name contains this, or null for all of them.
    /// </param>
    /// <param name="keep">Files whose full read is kept for printing, or null to keep none.</param>
    public static AoSurveyResult Read(
        GameFiles? files,
        MonsterVarieties? table,
        string? match = null,
        List<AoRead>? keep = null)
    {
        if (files is null || table is null || table.Count == 0)
        {
            return AoSurveyResult.Nothing;
        }

        var structs = new Dictionary<string, int>(StringComparer.Ordinal);
        var keys = new Dictionary<string, int>(StringComparer.Ordinal);
        var extensions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var animations = new Dictionary<string, int>(StringComparer.Ordinal);
        var faults = new List<string>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Path, int Depth)>();

        var monsters = 0;
        var withFiles = 0;
        var asked = 0;
        var read = 0;
        var clean = 0;

        foreach ((string path, MonsterVariety one) in table.All)
        {
            if (match is { Length: > 0 } && !Hits(path, one.Name, match))
            {
                continue;
            }

            monsters++;
            if (one.AoFiles is not { Count: > 0 } named)
            {
                continue;
            }

            withFiles++;
            foreach (string file in named)
            {
                queue.Enqueue((file, 0));
            }
        }

        while (queue.Count > 0)
        {
            (string path, int depth) = queue.Dequeue();
            if (path.Length == 0 || !seen.Add(path))
            {
                continue;
            }

            asked++;
            AnimatedObject ao = AnimatedObject.Read(files, path);
            if (!ao.Ready)
            {
                continue;
            }

            read++;
            if (ao.Faults.Count == 0)
            {
                clean++;
            }
            else if (faults.Count < MostFaults)
            {
                faults.Add($"{path}: {ao.Faults[0]}");
            }

            keep?.Add(new AoRead(path, ao, depth));

            foreach (string extends in ao.Extends)
            {
                Note(extensions, Extension(extends));
                if (depth < MostHops)
                {
                    queue.Enqueue((extends, depth + 1));
                }
            }

            foreach ((AoStruct block, AoEntry entry) in ao.Entries())
            {
                Note(structs, block.Name);
                Note(keys, $"{block.Name}.{entry.Key}");

                // THE ANIMATION'S VALUE IS ITS NAME, which is the whole reason the Stance question
                // is being asked here. Its CHILDREN are keyframes keyed by time - those are not
                // names and would swamp the count.
                if (string.Equals(entry.Key, "animation", StringComparison.Ordinal)
                    && entry.Value is { Length: > 0 })
                {
                    Note(animations, entry.Value);
                }

                foreach (string reference in Referenced(entry))
                {
                    Note(extensions, Extension(reference));

                    if (depth < MostHops
                        && reference.EndsWith(".ao", StringComparison.OrdinalIgnoreCase)
                        && Follows.Contains(entry.Key, StringComparer.Ordinal))
                    {
                        queue.Enqueue((reference, depth + 1));
                    }
                }
            }
        }

        return new AoSurveyResult(
            monsters, withFiles, asked, read, clean, structs, keys, extensions, animations, faults);
    }

    /// <summary>
    /// The paths in one entry's value - the value itself, or the quoted strings inside a script.
    /// </summary>
    /// <remarks>
    /// A SCRIPT CARRIES ITS PATHS IN QUOTES: <c>PlayEffect("…/foo.ao", 1)</c>. Reading only the
    /// quoted values would miss every file an <c>on_*</c> handler names, and those are exactly the
    /// effect packs and attached objects that make a monster look like itself.
    /// </remarks>
    public static IEnumerable<string> Referenced(AoEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.Kind == AoValueKind.Quoted)
        {
            if (Looks(entry.Value))
            {
                yield return entry.Value;
            }

            yield break;
        }

        if (entry.Kind != AoValueKind.Script && entry.Kind != AoValueKind.Payload)
        {
            yield break;
        }

        string text = entry.Value;
        for (var at = 0; at < text.Length; at++)
        {
            if (text[at] != '"')
            {
                continue;
            }

            int end = text.IndexOf('"', at + 1);
            if (end < 0)
            {
                yield break;
            }

            string said = text[(at + 1)..end];
            if (Looks(said))
            {
                yield return said;
            }

            at = end;
        }
    }

    /// <summary>Whether a string is plausibly a path: it has an extension and no spaces in it.</summary>
    private static bool Looks(string said)
        => said.Length is > 3 and < 512
            && said.Contains('/', StringComparison.Ordinal)
            && Extension(said).Length > 1;

    /// <summary>The extension, lowercased, or empty. Only the part after the LAST dot.</summary>
    private static string Extension(string path)
    {
        int dot = path.LastIndexOf('.');
        if (dot < 0 || dot == path.Length - 1 || path.Length - dot > 8)
        {
            return string.Empty;
        }

        for (int at = dot + 1; at < path.Length; at++)
        {
            if (!char.IsAsciiLetterOrDigit(path[at]))
            {
                return string.Empty;
            }
        }

        return path[dot..].ToLowerInvariant();
    }

    private static bool Hits(string path, string? name, string match)
        => path.Contains(match, StringComparison.OrdinalIgnoreCase)
            || (name is { Length: > 0 } && name.Contains(match, StringComparison.OrdinalIgnoreCase));

    private static void Note(Dictionary<string, int> into, string what)
    {
        if (what.Length == 0)
        {
            return;
        }

        into[what] = into.TryGetValue(what, out int had) ? had + 1 : 1;
    }

    /// <summary>Prints what the survey found, most common first.</summary>
    public static void Report(AoSurveyResult? result, TextWriter output, int most = 25)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (result is null || result.Asked == 0)
        {
            output.WriteLine("ao  nothing to walk - no install, or no monster names an .ao file.");
            return;
        }

        output.WriteLine(
            $"ao  {Say(result.Monsters)} monsters, {Say(result.WithFiles)} naming a file;"
            + $" {Say(result.Asked)} files asked for, {Say(result.Read)} read,"
            + $" {Say(result.Clean)} of those with no parse fault");

        // FIRST, BECAUSE IT DECIDES WHETHER THE REST MEANS ANYTHING. A survey where most files
        // faulted is measuring this reader against the real format, not the game against a
        // question - and the faults are then the finding.
        if (result.Faults.Count > 0)
        {
            output.WriteLine($"    {Say(result.Read - result.Clean)} files had a fault. First few:");
            foreach (string fault in result.Faults.Take(8))
            {
                output.WriteLine($"      ! {fault}");
            }
        }

        Table(output, "referenced file types", result.Extensions, most);
        Table(output, "struct types", result.Structs, most);
        Table(output, "animation names", result.Animations, most);
        Table(output, "entry keys", result.Keys, most);
    }

    /// <summary>Prints one monster's files in full - every struct and every entry.</summary>
    public static void Detail(IReadOnlyList<AoRead>? reads, TextWriter output, int mostEntries = 400)
    {
        ArgumentNullException.ThrowIfNull(output);

        foreach (AoRead one in reads ?? [])
        {
            output.WriteLine();
            output.WriteLine(
                $"  {new string(' ', one.Depth * 2)}{one.Path}"
                + $"   (version {Say(one.Object.Version)}"
                + (one.Object.Abstract ? ", abstract" : string.Empty)
                + $", {Say(one.Object.Structs.Count)} structs)");

            foreach (string extends in one.Object.Extends)
            {
                output.WriteLine($"      extends {extends}");
            }

            var printed = 0;
            foreach (AoStruct block in one.Object.Structs)
            {
                output.WriteLine($"      {block.Name}{(block.Client ? "  [client]" : string.Empty)}");
                foreach (AoEntry entry in block.Entries)
                {
                    if (printed++ >= mostEntries)
                    {
                        output.WriteLine("        …");
                        break;
                    }

                    Entry(output, entry, 4);
                }
            }

            foreach (string fault in one.Object.Faults)
            {
                output.WriteLine($"      ! {fault}");
            }
        }
    }

    private static void Entry(TextWriter output, AoEntry entry, int indent)
    {
        string value = entry.Value.ReplaceLineEndings(" ");
        if (value.Length > 96)
        {
            value = string.Concat(value.AsSpan(0, 95), "…");
        }

        output.WriteLine($"{new string(' ', indent * 2)}{entry.Key} = [{entry.Kind}] {value}");
        foreach (AoEntry child in entry.Children)
        {
            Entry(output, child, indent + 1);
        }
    }

    private static void Table(
        TextWriter output, string what, IReadOnlyDictionary<string, int> counts, int most)
    {
        if (counts.Count == 0)
        {
            return;
        }

        output.WriteLine($"    {what} ({Say(counts.Count)} distinct):");
        foreach ((string name, int on) in counts
            .OrderByDescending(one => one.Value)
            .ThenBy(one => one.Key, StringComparer.Ordinal)
            .Take(most))
        {
            output.WriteLine($"      {on,6} {name}");
        }

        if (counts.Count > most)
        {
            output.WriteLine($"      … and {Say(counts.Count - most)} more");
        }
    }

    private static string Say(int value) => value.ToString(CultureInfo.InvariantCulture);
}
