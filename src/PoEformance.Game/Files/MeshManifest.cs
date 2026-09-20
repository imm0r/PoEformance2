using System.Globalization;
using System.Numerics;

namespace PoEformance.Game.Files;

/// <summary>
/// One line of a manifest's Materials section: a <c>.mat</c> path and the number after it.
/// </summary>
/// <param name="Path">The material file. The format allows this to be empty, and files use it.</param>
/// <param name="Number">
/// The unsigned number the line ends with. Unidentified - see <see cref="MeshManifest.Spread"/>
/// for the one reading this project acts on and the test the file has to pass first.
/// </param>
public readonly record struct MeshMaterial(string Path, int Number);

/// <summary>
/// What a <c>.sm</c> file says: where the geometry is, and what goes on it.
/// </summary>
/// <remarks>
/// A MANIFEST AND NOT A MESH, which is the thing to know about this file. The name says skinned
/// mesh and the contents are four lines of bookkeeping - the geometry is one hop further on, in
/// the <c>.smd</c> this names. Reading it expecting vertices is how an afternoon goes missing.
///
/// ITS OWN FORMAT, NOT THE .ao ONE. Both are UTF-16 text out of the same game and they look alike
/// at a glance, which is the trap: an .ao is <c>key = value</c> with braces, this is a keyword
/// then its values, whitespace separated, one record per line. Pointing the .ao reader at a .sm
/// produces faults rather than a mesh, which is at least loud - a reader that half-succeeded
/// would be worse.
///
/// TWO SOURCES FOR THE SAME BOUNDING BOX, and that is worth keeping. The box is written here in
/// text and again in the .smd's binary header, so the two agree or something is wrong - it is the
/// check that first proved <see cref="SkinnedMesh"/> was reading the right bytes at the right
/// stride. See <see cref="Least"/>.
///
/// THE MATERIAL HERE IS THE DEFAULT, and a monster usually overrides it: the .ao's SkinMesh entry
/// names a material per SHAPE, with an index after it - <c>…/ExpeditionSkeleton.mat:0</c> - while
/// this names one for the mesh as a whole. Whoever draws the thing wants the .ao's answer where
/// there is one and this where there is not.
///
/// AND EVERY MATERIAL LINE CARRIES A NUMBER AFTER THE PATH, which this read past for a long time
/// and which is the only thing in either file that can join seven materials to fifteen shapes.
/// See <see cref="MeshMaterial.Number"/> and <see cref="Spread"/>.
/// </remarks>
/// <param name="Version">Off the first line. Bone groups appear from 6, the box from 5.</param>
/// <param name="Geometry">The <c>.smd</c> that holds the triangles. Empty where the file said none.</param>
/// <param name="Materials">The <c>.mat</c> files with their numbers, in the order given.</param>
/// <param name="Least">The low corner of the bounding box, or zero before version 5.</param>
/// <param name="Most">The high corner.</param>
/// <param name="Bones">Bone group names, for a later pose. Empty before version 6.</param>
public sealed record MeshManifest(
    int Version,
    string Geometry,
    IReadOnlyList<MeshMaterial> Materials,
    Vector3 Least,
    Vector3 Most,
    IReadOnlyList<string> Bones)
{
    /// <summary>Nothing read - a missing file, or one that is not a manifest.</summary>
    public static MeshManifest None { get; } = new(0, string.Empty, [], default, default, []);

    /// <summary>
    /// One material per shape, in the mesh's order - or empty where the file does not say.
    /// </summary>
    /// <remarks>
    /// THE NUMBER IS UNIDENTIFIED AND THIS DOES NOT PRETEND OTHERWISE. The only other reader of
    /// this format in the open calls it <c>unk1</c>, so nothing is known about it from outside;
    /// what IS known is the shape of the problem it would solve. Count Geonor's human form is
    /// fifteen shapes and its manifest names seven materials, Veynar thirty-five and three,
    /// Connal eleven and two - and neither file holds anything else that could join the two
    /// lists, the .smd's shape record being an index range and nothing more.
    ///
    /// SO THE FILE IS MADE TO PROVE IT BEFORE IT IS BELIEVED. Read as a run length - this
    /// material covers the next N shapes - the numbers have to add up to EXACTLY the shape
    /// count, and seven arbitrary numbers summing to fifteen is not something that happens by
    /// accident. Where they do not add up, this answers empty and the caller keeps whatever it
    /// did before; nothing is painted on a reading the file did not support. Which way it went
    /// is printed under the model, so the answer is checkable rather than assumed.
    ///
    /// A ZERO-LENGTH RUN IS ALLOWED THROUGH, because a material that covers no shape is still
    /// an entry in the list and dropping it would shift every run after it.
    /// </remarks>
    /// <param name="shapes">How many shapes the geometry turned out to have.</param>
    public IReadOnlyList<string> Spread(int shapes)
    {
        if (shapes <= 0 || Materials.Count == 0)
        {
            return [];
        }

        var total = 0L;
        foreach (MeshMaterial one in Materials)
        {
            if (one.Number < 0)
            {
                return [];
            }

            total += one.Number;
            if (total > shapes)
            {
                return [];
            }
        }

        if (total != shapes)
        {
            return [];
        }

        var spread = new string[shapes];
        var at = 0;
        foreach (MeshMaterial one in Materials)
        {
            for (var run = 0; run < one.Number; run++)
            {
                spread[at++] = one.Path;
            }
        }

        return spread;
    }

    /// <summary>Whether there is a geometry file to go on with.</summary>
    public bool Ready => Geometry.Length > 0;

    /// <summary>Reads one out of an open install, by the path an <c>.ao</c>'s SkinMesh named.</summary>
    public static MeshManifest Read(GameFiles? files, string? path)
        => files is null || string.IsNullOrWhiteSpace(path)
            ? None
            : Read(files.Read(path.Replace('\\', '/').Trim()));

    /// <summary>Reads a file's bytes, sharing the decode with the game's other text files.</summary>
    public static MeshManifest Read(byte[]? content)
        => content is not { Length: > 0 } ? None : Parse(StatDescriptionFiles.Decode(content));

    /// <summary>
    /// Reads the text. Public so the format can be tested without an install.
    /// </summary>
    /// <remarks>
    /// LINE BY LINE AND KEYWORD FIRST, which the format allows where the .ao's does not: every
    /// record here starts on its own line and its values do not span one. The indented lines under
    /// a count belong to the keyword above them, so they are read by their SHAPE rather than by
    /// counting - a count that disagreed with the lines present would otherwise read the next
    /// keyword as a material.
    /// </remarks>
    public static MeshManifest Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return None;
        }

        var version = 0;
        var geometry = string.Empty;
        var materials = new List<MeshMaterial>();
        var bones = new List<string>();
        Vector3 least = default;
        Vector3 most = default;
        var section = Section.None;

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            // An indented line continues whatever keyword opened the section; a flush one ends it.
            bool indented = raw.Length > 0 && (raw[0] == '\t' || raw[0] == ' ');
            if (!indented)
            {
                section = Section.None;
            }
            else if (section == Section.Materials)
            {
                // KEPT EVEN WHEN THE PATH IS EMPTY, which the old reader dropped. The format
                // allows an empty material and the number beside it still counts, so dropping
                // the line shifts every run after it - harmless while only the paths were read
                // and wrong the moment they are joined to shapes by position.
                if (Quoted(line, out string path, out string after))
                {
                    materials.Add(new MeshMaterial(path, Number(after)));
                }

                continue;
            }
            else if (section != Section.None)
            {
                if (First(line) is { Length: > 0 } said)
                {
                    bones.Add(said);
                }

                continue;
            }

            string word = Word(line, out string rest);
            switch (word)
            {
                case "version":
                    version = Number(rest);
                    break;

                case "SkinnedMeshData":
                    geometry = First(rest);
                    break;

                case "Materials":
                    section = Section.Materials;
                    break;

                case "BoneGroups":
                    section = Section.Bones;
                    break;

                case "BoundingBox":
                    (least, most) = Box(rest);
                    break;

                default:
                    break;
            }
        }

        return geometry.Length == 0 && materials.Count == 0
            ? None
            : new MeshManifest(version, geometry, materials, least, most, bones);
    }

    private enum Section
    {
        None,
        Materials,
        Bones,
    }

    /// <summary>The first word of a line, and what is left after it.</summary>
    private static string Word(string line, out string rest)
    {
        int space = line.AsSpan().IndexOfAny(' ', '\t');
        if (space < 0)
        {
            rest = string.Empty;
            return line;
        }

        rest = line[(space + 1)..].TrimStart();
        return line[..space];
    }

    /// <summary>The first quoted string on a line, or empty. The spec allows an empty one.</summary>
    private static string First(string line) => Quoted(line, out string said, out _) ? said : string.Empty;

    /// <summary>
    /// The first quoted string and whatever follows it, and whether there was one at all.
    /// </summary>
    /// <remarks>
    /// THE BOOL IS THE POINT, not the string: an empty material and a line with no quote on it
    /// both read as "" and they are not the same thing - one is an entry and one is not.
    /// </remarks>
    private static bool Quoted(string line, out string said, out string rest)
    {
        said = string.Empty;
        rest = string.Empty;

        int open = line.IndexOf('"', StringComparison.Ordinal);
        if (open < 0)
        {
            return false;
        }

        int shut = line.IndexOf('"', open + 1);
        if (shut < open)
        {
            return false;
        }

        said = line[(open + 1)..shut];
        rest = line[(shut + 1)..];
        return true;
    }

    private static int Number(string said)
        => int.TryParse(said.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int read)
            ? read
            : 0;

    /// <summary>
    /// Six floats as two corners - the low triple first, then the high one.
    /// </summary>
    /// <remarks>
    /// NOT THE ORDER THE .smd USES. This writes min x, min y, min z, max x, max y, max z, while
    /// the binary header pairs them per axis: min x, max x, min y, max y, min z, max z. The two
    /// hold the same six numbers and reading either as the other swaps the mesh's height for its
    /// width, which looks like a mesh and is the wrong one.
    /// </remarks>
    private static (Vector3 Least, Vector3 Most) Box(string said)
    {
        Span<float> six = stackalloc float[6];
        var found = 0;
        foreach (Range part in said.AsSpan().Split(' '))
        {
            ReadOnlySpan<char> one = said.AsSpan()[part].Trim();
            if (one.Length > 0
                && found < 6
                && float.TryParse(one, NumberStyles.Float, CultureInfo.InvariantCulture, out float read))
            {
                six[found++] = read;
            }
        }

        return found < 6
            ? (default, default)
            : (new Vector3(six[0], six[1], six[2]), new Vector3(six[3], six[4], six[5]));
    }
}
