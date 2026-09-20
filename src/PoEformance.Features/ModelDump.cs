using System.Globalization;
using System.Text;
using PoEformance.Game.Entities;
using PoEformance.Game.Files;

namespace PoEformance.Features;

/// <summary>
/// Writes out the files a monster's model is built from, verbatim.
/// </summary>
/// <remarks>
/// BECAUSE A COUNT IS NOT A FORMAT. The model pane can say "15 shapes from 1 texture · named: 0
/// in the .ao, 7 in the .sm" and that sentence is the end of what a derived number can tell
/// anybody: it establishes that the .ao names no material per shape and that the manifest names
/// FEWER materials than the mesh has shapes, and it cannot say what the files put there instead.
/// Three bosses - Veynar, Connal and Count Geonor's human form - sat at exactly that wall across
/// three fixes, and every further step from there would have been a theory about a file format
/// nobody in this project had read.
///
/// SO THIS PRINTS THE BYTES. Both file types are UTF-16 text, both are small, and the parsers
/// above them keep only the keywords they already know - <see cref="MeshManifest.Parse"/> drops
/// every line whose first word it does not recognise, so whatever maps fifteen shapes onto seven
/// materials would be discarded in silence if it were there. A dump cannot be wrong about that
/// the way a summary can.
///
/// AND THE WHOLE extends CHAIN, not the one file the walk stopped at.
/// <see cref="MonsterModel"/>'s search ends at the first .ao that names a skin, so a parent that
/// carries the materials is never read - which is itself one of the candidate explanations. It
/// can only be ruled in or out by a walk that does not stop, and that is what runs here.
///
/// ON DEMAND ONLY, behind a button. It re-reads and decodes the .ao chain and the manifest, and
/// nothing on the drawing path ever calls it.
/// </remarks>
public static class ModelDump
{
    /// <summary>How far the extends chain is followed. The same cap the model walk uses.</summary>
    private const int MostHops = 8;

    /// <summary>Most .ao files printed, however many the chain names. A guard, not a limit met in practice.</summary>
    private const int MostFiles = 32;

    /// <summary>
    /// The text of every file behind one monster's model: the .ao chain, then the manifest.
    /// </summary>
    /// <param name="read">How to get a file out of the install, by path.</param>
    /// <param name="one">The monster, for the .ao files it names.</param>
    /// <param name="path">The monster's metadata path, as the book has it.</param>
    /// <param name="model">The model already gathered, for the manifest and the shapes.</param>
    public static string Of(
        Func<string, byte[]?>? read, MonsterVariety? one, string path, MonsterModel? model)
    {
        var said = new StringBuilder();
        said.Append("monster: ").Append(one?.Name ?? "?").Append(" [").Append(path).AppendLine("]");

        if (read is null || one?.AoFiles is not { Count: > 0 } named)
        {
            return said.AppendLine("nothing to read: no install, or the monster names no .ao file").ToString();
        }

        // BREADTH FIRST AND PAST THE FIRST ANSWER, unlike the model walk - see the remarks. The
        // visited set is what stops a file that extends itself, the depth cap what stops a long
        // chain, and they catch different things: neither on its own is enough.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Path, int Depth)>();
        foreach (string one_ in named)
        {
            queue.Enqueue((one_, 0));
        }

        var files = 0;
        while (queue.Count > 0 && files < MostFiles)
        {
            (string next, int depth) = queue.Dequeue();
            if (next.Length == 0 || !seen.Add(next))
            {
                continue;
            }

            (string at, byte[]? content) = Find(read, next);
            said.AppendLine().Append("=== .ao ").Append(at).Append(" (depth ").Append(Say(depth)).AppendLine(")");
            if (content is not { Length: > 0 })
            {
                said.AppendLine("(not in the install)");
                continue;
            }

            files++;
            said.AppendLine(StatDescriptionFiles.Decode(content).TrimEnd());

            if (depth < MostHops)
            {
                foreach (string parent in AnimatedObject.Read(content).Extends)
                {
                    queue.Enqueue((parent, depth + 1));
                }
            }
        }

        Manifest(read, said, model);
        Shapes(said, model);
        return said.ToString();
    }

    /// <summary>The mesh manifest, verbatim - the file that names fewer materials than there are shapes.</summary>
    private static void Manifest(Func<string, byte[]?> read, StringBuilder said, MonsterModel? model)
    {
        string path = model?.Mesh_ ?? string.Empty;
        if (path.Length == 0)
        {
            said.AppendLine().AppendLine("=== .sm (none was named)");
            return;
        }

        said.AppendLine().Append("=== .sm ").AppendLine(path);
        byte[]? content = read(path.Replace('\\', '/').Trim());
        said.AppendLine(content is { Length: > 0 }
            ? StatDescriptionFiles.Decode(content).TrimEnd()
            : "(not in the install)");
    }

    /// <summary>
    /// The shapes the geometry actually holds, with their slices of the index buffer.
    /// </summary>
    /// <remarks>
    /// THE OTHER HALF OF THE JOIN. Whatever names the manifest lists, the question is which of
    /// the mesh's shapes each one belongs on - so the shape names are printed beside them. It is
    /// also the only way to tell a mesh whose shapes are genuinely unnamed ("shape 3", which
    /// <see cref="SkinnedMesh"/> falls back to) from one whose names simply do not match.
    /// </remarks>
    private static void Shapes(StringBuilder said, MonsterModel? model)
    {
        if (model?.Mesh.Shapes is not { Count: > 0 } shapes)
        {
            said.AppendLine().AppendLine("=== shapes (none)");
            return;
        }

        said.AppendLine().Append("=== shapes (").Append(Say(shapes.Count)).AppendLine(")");
        for (var one = 0; one < shapes.Count; one++)
        {
            MeshShape shape = shapes[one];
            said.Append(Say(one)).Append('\t').Append(shape.Name)
                .Append("\tfrom ").Append(Say(shape.From))
                .Append("\tcount ").AppendLine(Say(shape.Count));
        }
    }

    /// <summary>
    /// Resolves a path the way the model walk does, and hands back what it read.
    /// </summary>
    /// <remarks>
    /// AN EXTENDS LINE CARRIES NO EXTENSION - <c>extends "Metadata/Parent"</c> - while a monster's
    /// own .ao is named in full. The same rule MonsterModel.Object applies, kept here so the dump
    /// reads the same files the model did rather than a subset of them.
    /// </remarks>
    private static (string Path, byte[]? Content) Find(Func<string, byte[]?> read, string path)
    {
        string said = path.Replace('\\', '/').Trim();
        if (read(said) is { Length: > 0 } content)
        {
            return (said, content);
        }

        int slash = said.LastIndexOf('/');
        if (said.IndexOf('.', slash + 1) >= 0)
        {
            return (said, null);
        }

        string with = said + AnimatedObject.Suffix;
        return (with, read(with));
    }

    private static string Say(int number) => number.ToString(CultureInfo.InvariantCulture);
}
