using System.Globalization;
using System.Text;
using PoEformance.Game.Files;

namespace PoEformance.Features;

/// <summary>
/// What a probed pixel of a model's picture was made of, and how its translucent layers came out, as lines a person can read.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS. The mud over seepage's offices came out as a slab that hid what the game shows,
/// and after the contact fade was written from the game's own shader sources it looked exactly the
/// same - with nothing to say whether the fade ran and found the ground too far below, ran and found
/// no ground at all, or never ran. A picture cannot answer that about its own pixels. These lines
/// can: every surface under the pixel with its material, where it came from and how far apart they
/// lie, and for every mixed layer how much of it had something solid behind it and what alpha it got.
///
/// THE WORDS ARE WORKED OUT HERE AND NOT IN THE PANE, so a test can read them without a window.
/// </remarks>
public static class PictureProbe
{
    /// <summary>Most materials named in the layers' lines; the rest are counted.</summary>
    public const int MostLayers = 8;

    /// <summary>The lines for one probed pixel, nearest surface first.</summary>
    /// <param name="probe">A probe a drawing has been through.</param>
    /// <param name="model">The model the picture is of.</param>
    /// <param name="shades">The programs the picture was drawn with, one per shape, or null.</param>
    public static IReadOnlyList<string> Lines(PixelProbe probe, MonsterModel model, IReadOnlyList<ShadeProgram?>? shades)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(model);
        if (!probe.Drawn)
        {
            return [Say($"probe at {probe.X}, {probe.Y}: not drawn yet")];
        }

        if (probe.Fragments.Count == 0)
        {
            return [Say($"probe at {probe.X}, {probe.Y}: nothing of the model reaches this pixel")];
        }

        // NEAREST FIRST, which is how a person reads a stack of surfaces from above; the drawing's own
        // order breaks a tie, so a surface drawn twice at one depth keeps its order.
        ProbeFragment[] sorted = [.. probe.Fragments.Select((one, at) => (one, at)).OrderBy(pair => pair.one.Depth).ThenBy(pair => pair.at).Select(pair => pair.one)];
        var lines = new List<string>(sorted.Length + 1)
        {
            Say($"probe at {probe.X}, {probe.Y}: {sorted.Length} fragment{(sorted.Length == 1 ? string.Empty : "s")}, nearest first"),
        };

        foreach (ProbeFragment fragment in sorted)
        {
            int shape = ShapeOf(model.Mesh, fragment.Triangle);
            var line = new StringBuilder(160);
            line.Append("  depth ").Append(Number(fragment.Depth))
                .Append(" · ").Append(Outcome(fragment, probe.Final));

            if (fragment.Seen is Seen.Mixed or Seen.Added or Seen.Clear or Seen.Discarded)
            {
                line.Append(" · ").Append(fragment.Behind < float.MaxValue
                    ? Number(fragment.Behind - fragment.Depth) + " in front of the solid behind it"
                    : "nothing solid behind it");
            }

            line.Append(" · ").Append(Described(model, shades, shape));
            lines.Add(line.ToString());
        }

        return lines;
    }

    /// <summary>
    /// The lines for how every translucent material came out over the whole picture, the most-drawn first.
    /// </summary>
    /// <param name="tally">What a drawing counted.</param>
    /// <param name="model">The model the picture is of.</param>
    /// <param name="shades">The programs the picture was drawn with, one per shape, or null.</param>
    public static IReadOnlyList<string> Layers(LayerTally tally, MonsterModel model, IReadOnlyList<ShadeProgram?>? shades)
    {
        ArgumentNullException.ThrowIfNull(tally);
        ArgumentNullException.ThrowIfNull(model);

        // BY MATERIAL, NOT BY SHAPE: a room's mud is sixty shapes of one material, and the question
        // is about the material.
        var byMaterial = new Dictionary<string, Summed>(StringComparer.OrdinalIgnoreCase);
        for (var shape = 0; shape < tally.Shapes; shape++)
        {
            LayerCount count = tally.Of(shape);
            if (count.Fragments == 0)
            {
                continue;
            }

            string material = shape < model.ShapeMaterials.Count && model.ShapeMaterials[shape].Length > 0
                ? MaterialFile.Bare(model.ShapeMaterials[shape])
                : "(no material)";
            if (!byMaterial.TryGetValue(material, out Summed? summed))
            {
                summed = new Summed(material);
                byMaterial[material] = summed;
            }

            summed.Add(count, shades is not null && shape < shades.Count && shades[shape] is not null);
        }

        if (byMaterial.Count == 0)
        {
            return ["layers: nothing translucent reached the picture"];
        }

        Summed[] ranked = [.. byMaterial.Values.OrderByDescending(one => one.Fragments).ThenBy(one => one.Material, StringComparer.Ordinal)];
        var lines = new List<string>(Math.Min(ranked.Length, MostLayers) + 2)
        {
            "layers: how each translucent material came out over this picture - pixels it reached in front of the solid there",
        };

        foreach (Summed one in ranked.Take(MostLayers))
        {
            lines.Add(one.Said());
        }

        if (ranked.Length > MostLayers)
        {
            lines.Add(Say($"  and {ranked.Length - MostLayers} more"));
        }

        return lines;
    }

    /// <summary>The shape a triangle belongs to, or -1.</summary>
    /// <remarks>A scan rather than a search: asked a few times per probe, over a few thousand shapes.</remarks>
    public static int ShapeOf(SkinnedMesh mesh, int triangle)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        int index = triangle * 3;
        for (var shape = 0; shape < mesh.Shapes.Count; shape++)
        {
            MeshShape one = mesh.Shapes[shape];
            if (index >= one.From && index < one.From + one.Count)
            {
                return shape;
            }
        }

        return -1;
    }

    private static string Outcome(ProbeFragment fragment, float final) => fragment.Seen switch
    {
        Seen.Solid => fragment.Depth <= final ? "shown" : "drawn, then covered by a nearer surface",
        Seen.Under => "hidden under the surface drawn before it",
        Seen.Again => "its shape had already blended here",
        Seen.Cut => "cut out",
        Seen.Discarded => "dropped by its program",
        Seen.Clear => "mixed with alpha 0",
        Seen.Mixed => "mixed, alpha " + Fraction(fragment.Cover),
        Seen.Added => "added, alpha " + Fraction(fragment.Cover),
        _ => fragment.Seen.ToString(),
    };

    /// <summary>A shape's material, how it is blended, what draws it, and where it came from.</summary>
    private static string Described(MonsterModel model, IReadOnlyList<ShadeProgram?>? shades, int shape)
    {
        if (shape < 0)
        {
            return "no shape";
        }

        string material = shape < model.ShapeMaterials.Count && model.ShapeMaterials[shape].Length > 0
            ? Tail(MaterialFile.Bare(model.ShapeMaterials[shape]))
            : "no material";
        string mode = shape < model.Modes.Count && model.Modes[shape].Length > 0 ? model.Modes[shape] : "-";
        string blend = shape < model.Blends.Count ? model.Blends[shape].ToString().ToLowerInvariant() : "opaque";
        string drawn = shades is not null && shape < shades.Count && shades[shape] is not null
            ? "program"
            : shape < model.Skins.Count && model.Skins[shape] is not null ? "texture" : "plain";
        string from = shape < model.ShapeSources.Count && model.ShapeSources[shape].Length > 0
            ? " · from " + model.ShapeSources[shape]
            : string.Empty;
        return Say($"{material} ({mode} -> {blend}, {drawn}) · shape {shape}{from}");
    }

    private static string Tail(string path) => path[(path.LastIndexOfAny(['/', '\\']) + 1)..];

    private static string Number(float value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    private static string Fraction(float value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Say(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    /// <summary>One material's counts added up over its shapes.</summary>
    private sealed class Summed(string material)
    {
        private readonly int[] _cover = new int[LayerTally.CoverBins];
        private readonly int[] _gaps = new int[LayerTally.GapBins];
        private int _behind;
        private int _discarded;
        private bool _program;
        private bool _texture;

        public string Material { get; } = material;

        public long Fragments { get; private set; }

        public void Add(LayerCount count, bool program)
        {
            Fragments += count.Fragments;
            _behind += count.Behind;
            _discarded += count.Discarded;
            for (var bin = 0; bin < _cover.Length; bin++)
            {
                _cover[bin] += count.Cover[bin];
            }

            for (var bin = 0; bin < _gaps.Length; bin++)
            {
                _gaps[bin] += count.Gaps[bin];
            }

            _program |= program;
            _texture |= !program;
        }

        public string Said()
        {
            var line = new StringBuilder(220);
            line.Append("  ").Append(Tail(Material))
                .Append(" · ").Append(_program && _texture ? "program and texture" : _program ? "program" : "texture alpha")
                .Append(" · ").Append(Fragments.ToString("N0", CultureInfo.InvariantCulture)).Append(" px")
                .Append(" · solid behind ").Append(Share(_behind, Fragments));

            if (_behind > 0)
            {
                line.Append(" · gap");
                IReadOnlyList<float> edges = LayerTally.GapEdges;
                for (var bin = 0; bin < _gaps.Length; bin++)
                {
                    string label = bin == 0
                        ? "<" + Edge(edges[0])
                        : bin < edges.Count ? Edge(edges[bin - 1]) + "-" + Edge(edges[bin]) : Edge(edges[^1]) + "+";
                    line.Append(bin == 0 ? " " : ", ").Append(label).Append(' ').Append(Share(_gaps[bin], _behind));
                }
            }

            line.Append(" · alpha 0 ").Append(Share(_cover[0], Fragments))
                .Append(", <¼ ").Append(Share(_cover[1], Fragments))
                .Append(", <½ ").Append(Share(_cover[2], Fragments))
                .Append(", <¾ ").Append(Share(_cover[3], Fragments))
                .Append(", <1 ").Append(Share(_cover[4], Fragments))
                .Append(", 1 ").Append(Share(_cover[5], Fragments))
                .Append(" · dropped ").Append(Share(_discarded, Fragments));
            return line.ToString();
        }

        private static string Edge(float value) => value.ToString("0", CultureInfo.InvariantCulture);

        // BY HAND RATHER THAN "0%": the invariant culture writes a percentage with a space before the sign.
        private static string Share(long part, long whole)
            => whole <= 0 ? "-" : Math.Round(100.0 * part / whole).ToString("0", CultureInfo.InvariantCulture) + "%";
    }
}
