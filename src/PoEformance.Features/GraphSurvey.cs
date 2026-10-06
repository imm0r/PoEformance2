using System.Globalization;
using System.Text;
using PoEformance.Game.Files;

namespace PoEformance.Features;

/// <summary>
/// Which graph nodes keep the install's materials from being coloured by their graphs, ranked by how many they hold back.
/// </summary>
/// <remarks>
/// MEASURED, NOT GUESSED. The shade compiler evaluates a graph only when every node on its colour
/// path is one it knows; adding nodes one effect at a time - the desert dust's because one tile
/// needed it - would build what the last screenshot asked for rather than what most materials
/// need. So every material in the install is read once, its graphs' colour paths walked, and what
/// is missing counted:
///
///     ALONE    materials that one missing item is all that stands between them and a colour
///     AMONG    materials it is one of several missing items for
///     GREEDY   the order that unlocks the most materials step by step, each item exactly once
///
/// THE COLOUR PATH is everything that feeds an <c>AlbedoColor</c> or a <c>UV</c> - the colour and
/// the coordinates it is read at - followed back along the graph's links; a node nothing on it
/// reaches is not counted, so a graph's normals and gloss do not inflate the list. A colour or a
/// coordinate written at a stage the compiler does not run is an item of its own, <c>stage X</c>.
///
/// A MATERIAL'S ITEMS are those of every graph in its chain that writes either, because the
/// compiler leaves such a graph out whole when it meets one node it cannot evaluate.
///
/// AND EXAMPLES OF WHAT THE VERTEX SIDE HANDS OVER. An item a graph reads from the vertex - an
/// <c>InputVertex</c> or <c>FromVertex</c> node - cannot be settled from the shader sources: what
/// it holds depends on how a graph wires it. So beside the survey goes one graph per such item,
/// whole, with a material naming it - the graph most held-back terrain materials name, where any
/// does - which is what deciding one needs to read.
/// </remarks>
public static class GraphSurvey
{
    /// <summary>What the survey's file, beside the dumps, is called.</summary>
    public const string File = "graph-survey.txt";

    /// <summary>What the file of example graphs beside it is called.</summary>
    public const string ExamplesFile = "graph-examples.txt";

    /// <summary>Most items given an example graph.</summary>
    private const int MostExamples = 16;

    /// <summary>The folder terrain materials sit in, for which example graph to pick.</summary>
    private const string Terrain = "Art/Models/Terrain";

    /// <summary>Most missing items listed in the ranking.</summary>
    private const int MostItems = 80;

    /// <summary>Most steps of the greedy order.</summary>
    private const int MostSteps = 40;

    /// <summary>Most graphs listed among those left out.</summary>
    private const int MostGraphs = 80;

    /// <summary>How often progress is reported, in materials.</summary>
    private const int ProgressEvery = 2000;

    /// <summary>
    /// Reads every material and writes the survey. Never throws on a file that will not read: it is counted.
    /// </summary>
    /// <param name="read">How to get a file out of the install, by path.</param>
    /// <param name="materials">Every <c>.mat</c> the install has.</param>
    /// <param name="progress">Told how far the survey has got, now and then, for a line under a button.</param>
    /// <param name="examples">Where to write the example graphs for the vertex items - see the remarks - or null for none.</param>
    public static string Of(
        Func<string, byte[]?>? read, IReadOnlyList<string>? materials, Action<string>? progress = null, StringBuilder? examples = null)
    {
        var said = new StringBuilder();
        if (read is null || materials is not { Count: > 0 })
        {
            return said.AppendLine("no survey: no install, or the install walk listed no materials").ToString();
        }

        // IN PATH ORDER, which is close to the order files sit in the bundles - see BundleFile's
        // chunk memo - and makes two surveys of one install comparable line by line.
        List<string> ordered = [.. materials.Order(StringComparer.OrdinalIgnoreCase)];
        var graphs = new Dictionary<string, Facts>(StringComparer.OrdinalIgnoreCase);
        var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var blocked = new List<(string[] Missing, string Folder)>();
        var vertex = new Dictionary<string, Dictionary<string, Example>>(StringComparer.Ordinal);
        int unreadable = 0, ungraphed = 0, colourless = 0, evaluated = 0;

        for (var at = 0; at < ordered.Count; at++)
        {
            if (progress is not null && at % ProgressEvery == 0)
            {
                progress(string.Create(CultureInfo.InvariantCulture, $"{at} of {ordered.Count} materials"));
            }

            string material = ordered[at];
            byte[]? content = read(material);
            if (content is not { Length: > 0 })
            {
                unreadable++;
                continue;
            }

            IReadOnlyList<ShaderInstance> chain = ShaderGraph.Instances(content);
            if (chain.Count == 0)
            {
                ungraphed++;
                continue;
            }

            var missing = new SortedSet<string>(StringComparer.Ordinal);
            var colours = false;
            foreach (ShaderInstance instance in chain)
            {
                if (instance.Parent.Length == 0)
                {
                    continue;
                }

                string parent = instance.Parent.Replace('\\', '/').Trim();
                if (!graphs.TryGetValue(parent, out Facts? facts))
                {
                    facts = Facts.Of(read(parent));
                    graphs[parent] = facts;
                }

                used[parent] = used.GetValueOrDefault(parent) + 1;
                if (facts.Colours)
                {
                    colours = true;
                    missing.UnionWith(facts.Missing);
                }
            }

            if (!colours)
            {
                colourless++;
            }
            else if (missing.Count == 0)
            {
                evaluated++;
            }
            else
            {
                string folder = Folder(material);
                blocked.Add(([.. missing], folder));
                if (examples is not null)
                {
                    Note(vertex, chain, graphs, material, string.Equals(folder, Terrain, StringComparison.OrdinalIgnoreCase));
                }
            }
        }

        progress?.Invoke(string.Create(CultureInfo.InvariantCulture, $"{ordered.Count} of {ordered.Count} materials - writing"));
        int coloured = evaluated + blocked.Count;
        said.Append("graph survey of ").Append(Say(ordered.Count)).AppendLine(" materials in the install").AppendLine();
        said.Append("  ").Append(Say(unreadable)).AppendLine(" would not read");
        said.Append("  ").Append(Say(ungraphed)).AppendLine(" name no graphs - drawn from their texture slots, as before graphs were read");
        said.Append("  ").Append(Say(colourless)).AppendLine(" name graphs, none of which writes a colour or coordinates");
        said.Append("  ").Append(Say(coloured)).AppendLine(" are coloured by their graphs:");
        said.Append("    ").Append(Say(evaluated)).Append(" evaluate whole now (").Append(Share(evaluated, coloured)).AppendLine(")");
        said.Append("    ").Append(Say(blocked.Count)).Append(" have a graph left out (").Append(Share(blocked.Count, coloured)).AppendLine(")");
        said.Append("  ").Append(Say(graphs.Count)).Append(" graphs named, ").Append(Say(graphs.Values.Count(one => !one.Ready)))
            .AppendLine(" of them hold no nodes - a blend mode alone, like ForceAlpha - or are not in the install");

        Ranking(blocked, graphs, said);
        Greedy(blocked, evaluated, coloured, said);
        LeftOut(graphs, used, said);
        Stages(graphs, used, said);
        if (examples is not null)
        {
            Examples(vertex, read, examples);
        }

        return said.ToString();
    }

    /// <summary>Whether an item is something a graph reads from the vertex side.</summary>
    private static bool Vertexed(string item)
        => item.StartsWith("InputVertex", StringComparison.Ordinal) || item.StartsWith("FromVertex", StringComparison.Ordinal);

    /// <summary>Counts, for every vertex item a held-back material's graphs miss, which graph it is missed in.</summary>
    private static void Note(
        Dictionary<string, Dictionary<string, Example>> vertex, IReadOnlyList<ShaderInstance> chain,
        Dictionary<string, Facts> graphs, string material, bool terrain)
    {
        foreach (ShaderInstance instance in chain)
        {
            string parent = instance.Parent.Replace('\\', '/').Trim();
            if (parent.Length == 0 || !graphs.TryGetValue(parent, out Facts? facts) || !facts.Colours)
            {
                continue;
            }

            foreach (string item in facts.Missing)
            {
                if (!Vertexed(item))
                {
                    continue;
                }

                if (!vertex.TryGetValue(item, out Dictionary<string, Example>? byGraph))
                {
                    byGraph = new Dictionary<string, Example>(StringComparer.OrdinalIgnoreCase);
                    vertex[item] = byGraph;
                }

                if (!byGraph.TryGetValue(parent, out Example? example))
                {
                    example = new Example(material);
                    byGraph[parent] = example;
                }

                example.All++;
                if (terrain)
                {
                    example.Terrain++;
                    example.TerrainMaterial ??= material;
                }
            }
        }
    }

    /// <summary>One graph per vertex item, whole, with a material naming it - see the remarks.</summary>
    private static void Examples(Dictionary<string, Dictionary<string, Example>> vertex, Func<string, byte[]?> read, StringBuilder said)
    {
        said.AppendLine("graph examples: for each thing a graph reads from the vertex side and the shade compiler does not, the graph")
            .AppendLine("that most held-back terrain materials name (or most materials, where no terrain one does), whole, and a material")
            .AppendLine("naming it - beside " + File);
        foreach ((string item, Dictionary<string, Example> byGraph) in vertex
            .OrderByDescending(one => one.Value.Values.Sum(example => example.All))
            .ThenBy(one => one.Key, StringComparer.Ordinal)
            .Take(MostExamples))
        {
            (string graph, Example example) = byGraph
                .OrderByDescending(one => one.Value.Terrain)
                .ThenByDescending(one => one.Value.All)
                .ThenBy(one => one.Key, StringComparer.OrdinalIgnoreCase)
                .Select(one => (one.Key, one.Value))
                .First();
            string material = example.TerrainMaterial ?? example.Material;
            said.AppendLine().Append("=== ").Append(item).Append(" · graph ").Append(graph)
                .Append(" · named by ").Append(Say(example.Terrain)).Append(" held-back terrain materials, ")
                .Append(Say(example.All)).AppendLine(" in all");
            said.Append("--- material ").AppendLine(material);
            said.AppendLine(Text(read, material));
            said.Append("--- graph ").AppendLine(graph);
            said.AppendLine(Text(read, graph));
        }
    }

    private static string Text(Func<string, byte[]?> read, string path)
        => read(path) is { Length: > 0 } bytes ? StatDescriptionFiles.Decode(bytes).TrimEnd() : "(not in the install)";

    /// <summary>Every missing item: how many materials it alone holds back, how many it is among, in how many graphs, and where.</summary>
    private static void Ranking(List<(string[] Missing, string Folder)> blocked, Dictionary<string, Facts> graphs, StringBuilder said)
    {
        var alone = new Dictionary<string, int>(StringComparer.Ordinal);
        var among = new Dictionary<string, int>(StringComparer.Ordinal);
        var folders = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        foreach ((string[] missing, string folder) in blocked)
        {
            if (missing.Length == 1)
            {
                alone[missing[0]] = alone.GetValueOrDefault(missing[0]) + 1;
            }

            foreach (string item in missing)
            {
                among[item] = among.GetValueOrDefault(item) + 1;
                if (!folders.TryGetValue(item, out Dictionary<string, int>? where))
                {
                    where = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    folders[item] = where;
                }

                where[folder] = where.GetValueOrDefault(folder) + 1;
            }
        }

        var inGraphs = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Facts facts in graphs.Values)
        {
            foreach (string item in facts.Missing)
            {
                inGraphs[item] = inGraphs.GetValueOrDefault(item) + 1;
            }
        }

        said.AppendLine().AppendLine("=== what is missing, by the materials it holds back")
            .AppendLine("item · alone: the only thing missing for that many materials · among: missing for that many · graphs · where those materials are");
        foreach (string item in among.Keys
            .OrderByDescending(one => alone.GetValueOrDefault(one))
            .ThenByDescending(one => among[one])
            .ThenBy(one => one, StringComparer.Ordinal)
            .Take(MostItems))
        {
            said.Append("  ").Append(item)
                .Append(" · alone ").Append(Say(alone.GetValueOrDefault(item)))
                .Append(" · among ").Append(Say(among[item]))
                .Append(" · graphs ").Append(Say(inGraphs.GetValueOrDefault(item)))
                .Append(" · ").AppendLine(string.Join(", ", folders[item]
                    .OrderByDescending(one => one.Value)
                    .Take(4)
                    .Select(one => one.Key + " " + Say(one.Value))));
        }

        if (among.Count > MostItems)
        {
            said.Append("  ").Append(Say(among.Count - MostItems)).AppendLine(" more items, each holding back fewer");
        }
    }

    /// <summary>
    /// The order that unlocks the most materials: each step adds the item that completes the most, given the steps before it.
    /// </summary>
    private static void Greedy(List<(string[] Missing, string Folder)> blocked, int evaluated, int coloured, StringBuilder said)
    {
        said.AppendLine().AppendLine("=== an order to add them in: each step the item that completes the most materials, given the steps before");
        var remaining = blocked.Select(one => new HashSet<string>(one.Missing, StringComparer.Ordinal)).ToList();
        int done = evaluated;
        for (var step = 1; step <= MostSteps && remaining.Count > 0; step++)
        {
            // COMPLETES FIRST, THEN AMONG: when no single item finishes a material, the one standing
            // in the way of the most is the one that brings the most closer.
            var completes = new Dictionary<string, int>(StringComparer.Ordinal);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (HashSet<string> left in remaining)
            {
                foreach (string item in left)
                {
                    counts[item] = counts.GetValueOrDefault(item) + 1;
                    if (left.Count == 1)
                    {
                        completes[item] = completes.GetValueOrDefault(item) + 1;
                    }
                }
            }

            string best = counts.Keys
                .OrderByDescending(one => completes.GetValueOrDefault(one))
                .ThenByDescending(one => counts[one])
                .ThenBy(one => one, StringComparer.Ordinal)
                .First();

            int finished = 0;
            for (int one = remaining.Count - 1; one >= 0; one--)
            {
                if (remaining[one].Remove(best) && remaining[one].Count == 0)
                {
                    remaining.RemoveAt(one);
                    finished++;
                }
            }

            done += finished;
            said.Append("  ").Append(Say(step)).Append(". ").Append(best)
                .Append(" · completes ").Append(Say(finished))
                .Append(" · ").Append(Share(done, coloured)).AppendLine(" of the coloured materials evaluate whole after it");
        }
    }

    /// <summary>The graphs left out, by how many materials name them, with what each is missing.</summary>
    private static void LeftOut(Dictionary<string, Facts> graphs, Dictionary<string, int> used, StringBuilder said)
    {
        said.AppendLine().AppendLine("=== graphs left out, by the materials naming them");
        foreach ((string graph, Facts facts) in graphs
            .Where(one => one.Value.Colours && one.Value.Missing.Count > 0)
            .OrderByDescending(one => used.GetValueOrDefault(one.Key))
            .ThenBy(one => one.Key, StringComparer.OrdinalIgnoreCase)
            .Take(MostGraphs))
        {
            said.Append("  ").Append(graph).Append(" · ").Append(Say(used.GetValueOrDefault(graph))).Append(" materials · missing ")
                .AppendLine(string.Join(", ", facts.Missing));
        }
    }

    /// <summary>Every stage a colour or coordinates are written at, whether the compiler runs it, and how many materials it reaches.</summary>
    private static void Stages(Dictionary<string, Facts> graphs, Dictionary<string, int> used, StringBuilder said)
    {
        var graphCount = new Dictionary<string, int>(StringComparer.Ordinal);
        var materialCount = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach ((string graph, Facts facts) in graphs)
        {
            foreach (string stage in facts.Stages)
            {
                graphCount[stage] = graphCount.GetValueOrDefault(stage) + 1;
                materialCount[stage] = materialCount.GetValueOrDefault(stage) + used.GetValueOrDefault(graph);
            }
        }

        said.AppendLine().AppendLine("=== stages colours and coordinates are written at");
        foreach (string stage in graphCount.Keys.OrderByDescending(one => materialCount[one]).ThenBy(one => one, StringComparer.Ordinal))
        {
            said.Append("  ").Append(stage.Length > 0 ? stage : "(none)")
                .Append(ShadeProgram.Stages.Contains(stage) ? " · run" : " · NOT run")
                .Append(" · graphs ").Append(Say(graphCount[stage]))
                .Append(" · material uses ").AppendLine(Say(materialCount[stage]));
        }
    }

    /// <summary>A material's first three folders - <c>Art/Textures/Environment</c> - for where the held-back materials are.</summary>
    private static string Folder(string path)
    {
        string slashed = path.Replace('\\', '/');
        var cut = -1;
        for (var depth = 0; depth < 3; depth++)
        {
            int next = slashed.IndexOf('/', cut + 1);
            if (next < 0)
            {
                break;
            }

            cut = next;
        }

        return cut > 0 ? slashed[..cut] : "(top level)";
    }

    private static string Say(int number) => number.ToString(CultureInfo.InvariantCulture);

    private static string Share(int part, int whole)
        => whole == 0 ? "-" : (100.0 * part / whole).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    /// <summary>How many held-back materials miss a vertex item in one graph, and one of them to show.</summary>
    private sealed class Example(string material)
    {
        public string Material { get; } = material;

        public string? TerrainMaterial { get; set; }

        public int All { get; set; }

        public int Terrain { get; set; }
    }

    /// <summary>What one graph's colour path needs, worked out once however many materials name it.</summary>
    private sealed class Facts
    {
        private static readonly Facts Unread = new() { Ready = false };

        /// <summary>Whether the file read as a graph.</summary>
        public bool Ready { get; private init; } = true;

        /// <summary>Whether it writes a colour or coordinates at all.</summary>
        public bool Colours { get; private init; }

        /// <summary>The node types on its colour path the compiler cannot evaluate, and the stages it does not run.</summary>
        public SortedSet<string> Missing { get; } = new(StringComparer.Ordinal);

        /// <summary>The stages its colour and coordinates are written at.</summary>
        public SortedSet<string> Stages { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Walks a graph back from every colour and coordinate it writes and notes what the compiler lacks.
        /// </summary>
        public static Facts Of(byte[]? content)
        {
            ShaderGraph graph = ShaderGraph.Read(content);
            if (!graph.Ready)
            {
                return Unread;
            }

            var into = new Dictionary<(string, int, string), List<(string, int, string)>>();
            foreach (ShaderLink link in graph.Links)
            {
                (string, int, string) target = (link.Target.Type, link.Target.Index, link.Target.Stage);
                if (!into.TryGetValue(target, out List<(string, int, string)>? sources))
                {
                    sources = [];
                    into[target] = sources;
                }

                sources.Add((link.Source.Type, link.Source.Index, link.Source.Stage));
            }

            var roots = graph.Nodes
                .Where(node => node.Type is "AlbedoColor" or "UV" && into.ContainsKey((node.Type, node.Index, node.Stage)))
                .ToList();
            if (roots.Count == 0)
            {
                return new Facts();
            }

            var facts = new Facts { Colours = true };
            var seen = new HashSet<(string, int, string)>();
            var pending = new Stack<(string, int, string)>();
            foreach (ShaderNode root in roots)
            {
                facts.Stages.Add(root.Stage);
                if (!ShadeProgram.Stages.Contains(root.Stage))
                {
                    facts.Missing.Add("stage " + (root.Stage.Length > 0 ? root.Stage : "(none)"));
                }

                pending.Push((root.Type, root.Index, root.Stage));
            }

            while (pending.Count > 0)
            {
                foreach ((string, int, string) source in into.GetValueOrDefault(pending.Pop()) ?? [])
                {
                    if (!seen.Add(source))
                    {
                        continue;
                    }

                    if (!ShadeProgram.Knows(source.Item1))
                    {
                        facts.Missing.Add(source.Item1);
                    }

                    pending.Push(source);
                }
            }

            return facts;
        }
    }
}
