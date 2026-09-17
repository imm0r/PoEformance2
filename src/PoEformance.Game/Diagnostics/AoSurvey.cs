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
/// <param name="Inside">
/// For a few files of each referenced type, what THAT file turned out to reference - see
/// <see cref="AoSurvey.Peek"/>. It is the hop the .ao files cannot answer about themselves.
/// </param>
/// <param name="CutShort">Whether the walk hit <see cref="AoSurvey.MostFiles"/> and stopped.</param>
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
    IReadOnlyList<string> Faults,
    IReadOnlyList<string> Inside,
    bool CutShort = false)
{
    /// <summary>Nothing walked - no install, or no monster names a file.</summary>
    public static AoSurveyResult Nothing { get; }
        = new(0, 0, 0, 0, 0, new Dictionary<string, int>(), new Dictionary<string, int>(),
            new Dictionary<string, int>(), new Dictionary<string, int>(), [], []);
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

    /// <summary>
    /// How many distinct files are read before the walk stops.
    /// </summary>
    /// <remarks>
    /// A CAP AND NOT A HOPE, because the shape of what is being walked is the thing nobody knows
    /// yet: effects attach effects, and following attached_object six deep across 2733 monsters
    /// could be five thousand files or fifty thousand. Somebody is going to run this once on
    /// their own machine to answer a question, and a diagnostic that might take an hour is one
    /// they stop rather than finish. When the cap is reached the report says so, so a number
    /// that was cut short cannot be read as the whole table.
    /// </remarks>
    public const int MostFiles = 20_000;

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
    /// <param name="each">
    /// Called with every file as it is read, or null. It is the same news as <paramref name="keep"/>
    /// and the reason for both is MEMORY: keeping the walk is 20,000 parsed files at once, which is
    /// fine for the one monster the detail dump prints and is most of a gigabyte over the install.
    /// A survey that only wants to TALLY something out of each file takes it here and holds nothing.
    /// </param>
    public static AoSurveyResult Read(
        GameFiles? files,
        MonsterVarieties? table,
        string? match = null,
        List<AoRead>? keep = null,
        Action<AoRead>? each = null)
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
        var samples = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

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

        while (queue.Count > 0 && asked < MostFiles)
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

            if (keep is not null || each is not null)
            {
                var one = new AoRead(path, ao, depth);
                keep?.Add(one);
                each?.Invoke(one);
            }

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

                // THE NAMES ARE IN THE PAYLOAD, which the first survey got wrong. It counted the
                // value of an "animation" entry and found eight names over the whole install -
                // that key is rare. What a monster's animations are actually called is inside the
                // JSON of an "animations" entry, thousands of them:
                //
                //     animations = '[ { "name": "attack_long_sword_shield_01a", "events": [ …
                //
                // Eight names was not a finding about the game. It was a finding about which key
                // was being read, and it is the count the Stance question turns on.
                if (entry.Value is { Length: > 0 })
                {
                    if (string.Equals(entry.Key, "animation", StringComparison.Ordinal))
                    {
                        Note(animations, entry.Value);
                    }
                    else if (entry.Kind == AoValueKind.Payload)
                    {
                        foreach (string name in Names(entry.Value))
                        {
                            Note(animations, name);
                        }
                    }
                }

                foreach (string reference in Referenced(entry))
                {
                    string extension = Extension(reference);
                    Note(extensions, extension);

                    // A FEW OF EACH TYPE, kept so the next hop can be looked at in the same run.
                    // Not the .ao files - those are walked properly above, and peeking at one
                    // would report what this reader already read.
                    if (extension.Length > 0
                        && !string.Equals(extension, AnimatedObject.Suffix, StringComparison.Ordinal))
                    {
                        if (!samples.TryGetValue(extension, out List<string>? into))
                        {
                            into = [];
                            samples[extension] = into;
                        }

                        string bare = Bare(reference);
                        if (into.Count < PeekEach && !into.Contains(bare, StringComparer.OrdinalIgnoreCase))
                        {
                            into.Add(bare);
                        }
                    }

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
            monsters, withFiles, asked, read, clean, structs, keys, extensions, animations, faults,
            Peek(files, samples),
            CutShort: asked >= MostFiles && queue.Count > 0);
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

        // A QUOTED VALUE IS NOT ONE THING. Every attached_object in the game names a SOCKET and
        // then a file, both inside the one pair of quotes:
        //
        //     attached_object = "eye_L Metadata/Effects/Spells/undead_summon_skeletons/eye_glow.ao"
        //
        // Taken whole, that is a path no install has, and the second survey asked for 3262 files
        // and found 1852 - the 2289 attachments were failing as a body. The remark on Looks said
        // "no spaces in it" while the code never checked, which is the shape of mistake that
        // survives review: the comment was right and the code was not.
        if (entry.Kind == AoValueKind.Quoted)
        {
            if (Path(entry.Value) is { Length: > 0 } one && Looks(one))
            {
                yield return one;
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

    /// <summary>How many files of each referenced type are opened to see what is in them.</summary>
    public const int PeekEach = 3;

    /// <summary>
    /// Opens a few of the files the .ao files POINT AT, and says what those point at in turn.
    /// </summary>
    /// <remarks>
    /// THE HOP THE FIRST SURVEY COULD NOT MAKE, and the one the picture question turns on. That
    /// run reported thirteen referenced file types and not one .dds among them, which reads like
    /// "there is no texture" and is not what it says: a skin names a MESH and a MATERIAL -
    ///
    ///     skin      = "Art/Models/MONSTERS/BasicSkeleton/BasicSkeletonVar.sm"
    ///     HipsShape = "Art/Models/…/Textures/ExpeditionSkeleton.mat:0"
    ///
    /// - and where the texture is named is inside one of those, which the .ao files cannot say.
    ///
    /// NOTHING IS ASSUMED ABOUT THEIR FORMAT. These are opened as bytes, decoded the way the
    /// game's text files are, and scanned for anything shaped like a path. A binary file decodes
    /// to rubbish and simply yields nothing, which is itself the finding: it means the next hop
    /// needs a real reader rather than a scan. What comes back is reported by extension, so the
    /// answer is a count rather than an opinion.
    /// </remarks>
    /// <param name="files">The open install.</param>
    /// <param name="samples">A few paths of each type, as the walk collected them.</param>
    public static IReadOnlyList<string> Peek(
        GameFiles? files, IReadOnlyDictionary<string, List<string>>? samples)
    {
        if (files is null || samples is null || samples.Count == 0)
        {
            return [];
        }

        var said = new List<string>();
        foreach ((string extension, List<string> paths) in samples.OrderBy(one => one.Key, StringComparer.Ordinal))
        {
            var found = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var opened = 0;
            var text = 0;

            foreach (string path in paths)
            {
                if (files.Read(path) is not { Length: > 0 } content)
                {
                    continue;
                }

                opened++;
                string read = Files.StatDescriptionFiles.Decode(content);
                var any = false;
                foreach (string reference in Strings(read))
                {
                    any = true;
                    Note(found, Extension(reference));
                }

                if (any)
                {
                    text++;
                }
            }

            if (opened == 0)
            {
                continue;
            }

            said.Add(
                found.Count == 0
                    ? $"      {extension,-8} {opened} opened, nothing path-shaped inside"
                        + " - binary, or it names nothing"
                    : $"      {extension,-8} {opened} opened, {text} with paths inside -> "
                        + string.Join(
                            "  ",
                            found.OrderByDescending(one => one.Value)
                                .Take(6)
                                .Select(one => $"{one.Key}:{Say(one.Value)}")));
        }

        return said;
    }

    /// <summary>
    /// Anything inside a file that looks like a path, whatever the file's format.
    /// </summary>
    /// <remarks>
    /// RUNS OF PRINTABLE CHARACTERS rather than quoted strings, because a quote is a text-format
    /// habit and the next hop may not be one. A run is broken by anything unprintable, which is
    /// what makes this work on a binary file that happens to store its paths as plain bytes -
    /// and yield nothing at all on one that does not, which is the honest answer in that case.
    /// </remarks>
    private static IEnumerable<string> Strings(string content)
    {
        var from = -1;
        for (var at = 0; at <= content.Length; at++)
        {
            char c = at < content.Length ? content[at] : '\0';
            bool part = c is (>= (char)0x20 and < (char)0x7F) && c != '"' && c != ' ';

            if (part)
            {
                if (from < 0)
                {
                    from = at;
                }

                continue;
            }

            if (from >= 0)
            {
                string said = content[from..at];
                if (Looks(said))
                {
                    yield return said;
                }

                from = -1;
            }
        }
    }

    /// <summary>
    /// Every <c>"name": "…"</c> in a payload, which is what an animation is called.
    /// </summary>
    /// <remarks>
    /// SCANNED RATHER THAN DESERIALISED, and not only because this ships AOT. The payloads are
    /// described as JSON-LIKE, which is not the same as JSON - one of them held a backslash-escaped
    /// quote in the middle of a value in the sample this was written against - and a parser that
    /// threw on the first payload it disliked would lose every name in that file. A scan takes what
    /// it recognises and steps over what it does not, which is the right failure for a measurement.
    /// </remarks>
    public static IEnumerable<string> Names(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        const string Key = "\"name\"";
        int at = payload.IndexOf(Key, StringComparison.Ordinal);
        while (at >= 0)
        {
            int colon = at + Key.Length;
            while (colon < payload.Length && (payload[colon] == ' ' || payload[colon] == '\t'))
            {
                colon++;
            }

            if (colon < payload.Length && payload[colon] == ':')
            {
                int open = payload.IndexOf('"', colon + 1);
                int shut = open < 0 ? -1 : payload.IndexOf('"', open + 1);
                if (open >= 0 && shut > open)
                {
                    yield return payload[(open + 1)..shut];
                    at = payload.IndexOf(Key, shut, StringComparison.Ordinal);
                    continue;
                }
            }

            at = payload.IndexOf(Key, at + Key.Length, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The path inside a quoted value, which may have a socket name in front of it.
    /// </summary>
    /// <remarks>
    /// FROM THE FIRST WORD THAT HAS A SLASH IN IT, TO THE END - a rule with exactly two shapes to
    /// satisfy, and it is the second that rules out the obvious one:
    ///
    ///     "eye_L Metadata/Effects/…/eye_glow.ao"                  socket, then a path
    ///     "Audio/Sound Effects/…/BodyBarrierIdle.loop.ogg"        one path, WITH A SPACE IN IT
    ///
    /// Splitting on whitespace and keeping the path-shaped pieces handles the first and destroys
    /// the second: "Audio/Sound" and "Effects/…/…ogg" are two halves of one name. A socket is
    /// never a path - every one in the data is a bone or a slot, eye_L, hip_jntBnd, &lt;root&gt; -
    /// so the slash is what separates them, and everything past it belongs to the file.
    ///
    /// THE COMMENT HERE USED TO SAY "no spaces in it" while the code never checked, and making
    /// the code agree with it is what turned up the second shape: every .ogg in the sample
    /// disappeared at once. The comment was wrong about the game, and the code had been right by
    /// omission.
    /// </remarks>
    public static string Path(string? value)
    {
        if (value is not { Length: > 0 })
        {
            return string.Empty;
        }

        var from = 0;
        while (from < value.Length)
        {
            int space = value.IndexOf(' ', from);
            int end = space < 0 ? value.Length : space;

            if (value.AsSpan(from, end - from).Contains('/'))
            {
                return value[from..].Trim();
            }

            if (space < 0)
            {
                break;
            }

            from = space + 1;
        }

        return string.Empty;
    }

    /// <summary>Whether a string is plausibly a path: it has a folder and an extension.</summary>
    /// <remarks>
    /// NO RULE ABOUT SPACES, deliberately - see <see cref="Path"/>. "Audio/Sound Effects/…" is a
    /// real folder in the game and a check for whitespace here silently drops every sound file.
    /// </remarks>
    private static bool Looks(string said)
        => said.Length is > 3 and < 512
            && said.Contains('/', StringComparison.Ordinal)
            && Extension(said).Length > 1;

    /// <summary>
    /// The extension, lowercased, or empty. Only the part after the LAST dot.
    /// </summary>
    /// <remarks>
    /// A TRAILING ":n" IS CUT OFF FIRST, which the first survey over a real install is what found:
    /// a skin names one material per shape, and it names it with an index -
    ///
    ///     HipsShape = "Art/Models/…/Textures/ExpeditionSkeleton.mat:0"
    ///
    /// - so the text after the last dot was "mat:0", the colon is not alphanumeric, and every one
    /// of them was thrown away as not-a-path. The survey reported 16 .mat references beside 1683
    /// .sm, while the detail dump of a single monster showed fourteen materials on one mesh. The
    /// count was not small, it was a filter measuring itself.
    ///
    /// THE INDEX IS NOT PART OF THE FILE NAME - it selects within the file - so cutting it is also
    /// what makes the path readable, which is what the next hop will need.
    /// </remarks>
    public static string Extension(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        int colon = path.LastIndexOf(':');
        int dot = path.LastIndexOf('.');
        if (colon > dot && dot >= 0 && Digits(path, colon + 1))
        {
            path = path[..colon];
        }

        dot = path.LastIndexOf('.');
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

    /// <summary>The path with any ":n" selector taken off, which is the file it names.</summary>
    public static string Bare(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        int colon = path.LastIndexOf(':');
        return colon > path.LastIndexOf('.') && colon >= 0 && Digits(path, colon + 1)
            ? path[..colon]
            : path;
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

        // A CUT-SHORT WALK IS NOT A SMALLER ANSWER TO THE SAME QUESTION, so it is said before the
        // numbers rather than under them: the counts are then a sample of the graph, and the
        // breadth-first order means a sample weighted towards what the monsters name directly.
        if (result.CutShort)
        {
            output.WriteLine(
                $"    STOPPED AT {Say(MostFiles)} FILES - the counts below are a sample of the"
                + " graph, not all of it.");
        }

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

        // DIRECTLY UNDER THE TYPES THEY ARE ABOUT, because the two are one question: what the .ao
        // files name, and what those files name in turn. A .dds that appears only here is still a
        // .dds that a monster leads to.
        if (result.Inside.Count > 0)
        {
            output.WriteLine("    and what a few of each of those hold:");
            foreach (string line in result.Inside)
            {
                output.WriteLine(line);
            }
        }
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
