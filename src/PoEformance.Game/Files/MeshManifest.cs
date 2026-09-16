using System.Globalization;
using System.Numerics;

namespace PoEformance.Game.Files;

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
/// </remarks>
/// <param name="Version">Off the first line. Bone groups appear from 6, the box from 5.</param>
/// <param name="Geometry">The <c>.smd</c> that holds the triangles. Empty where the file said none.</param>
/// <param name="Materials">The <c>.mat</c> files, in the order given. An entry may be empty.</param>
/// <param name="Least">The low corner of the bounding box, or zero before version 5.</param>
/// <param name="Most">The high corner.</param>
/// <param name="Bones">Bone group names, for a later pose. Empty before version 6.</param>
public sealed record MeshManifest(
    int Version,
    string Geometry,
    IReadOnlyList<string> Materials,
    Vector3 Least,
    Vector3 Most,
    IReadOnlyList<string> Bones)
{
    /// <summary>Nothing read - a missing file, or one that is not a manifest.</summary>
    public static MeshManifest None { get; } = new(0, string.Empty, [], default, default, []);

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
        var materials = new List<string>();
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
            else if (section != Section.None)
            {
                if (First(line) is { Length: > 0 } said)
                {
                    (section == Section.Materials ? materials : bones).Add(said);
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
    private static string First(string line)
    {
        int open = line.IndexOf('"', StringComparison.Ordinal);
        if (open < 0)
        {
            return string.Empty;
        }

        int shut = line.IndexOf('"', open + 1);
        return shut > open ? line[(open + 1)..shut] : string.Empty;
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
