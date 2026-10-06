using System.Globalization;
using System.Text;
using PoEformance.Game.Diagnostics;
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
/// AND EVERYTHING THE MONSTER HANGS OFF ITSELF. An attached_object names its own .ao with its
/// own mesh, and MonsterModels reads the body alone - so Doryani's skirt, belt, necklace and
/// five other pieces are in the game and not in the pane. Following them is how the files get to
/// say whether each brings a skeleton of its own or is skinned to the parent's rig.
///
/// ON DEMAND ONLY, behind a button. It re-reads and decodes the .ao chain and the manifest, and
/// nothing on the drawing path ever calls it.
/// </remarks>
public static class ModelDump
{
    /// <summary>How far the extends chain is followed. The same cap the model walk uses.</summary>
    private const int MostHops = 8;

    /// <summary>
    /// Most .ao files printed, however many the walk names.
    /// </summary>
    /// <remarks>
    /// RAISED WHEN THE WALK LEARNED TO FOLLOW ATTACHMENTS. Doryani's body is three files and he
    /// hangs nine more off it, each of which may extend a parent of its own - so a cap set for
    /// an extends chain alone would cut the dump off in the middle of the thing it was opened
    /// to answer. It is still a cap and not a hope: effects attach effects, and a walk with no
    /// end is how a diagnostic becomes something nobody runs twice.
    /// </remarks>
    private const int MostFiles = 64;

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
        var walked = new List<string>();
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
            walked.Add(at);
            said.AppendLine(StatDescriptionFiles.Decode(content).TrimEnd());

            if (depth >= MostHops)
            {
                continue;
            }

            AnimatedObject ao = AnimatedObject.Read(content);
            foreach (string parent in ao.Extends)
            {
                queue.Enqueue((parent, depth + 1));
            }

            // AND WHAT IT HANGS OFF ITSELF. Reported from the live client: Doryani stands in the
            // game in a skirt, a belt, a necklace and two shoulder danglers, and the model pane
            // drew a bare-legged Doryani - because every one of those is an attached_object
            // naming its OWN .ao with its own mesh, and MonsterModels reads the body alone.
            // Following them here is what says whether each one brings a skeleton of its own
            // (posed rigidly at the socket) or is skinned to the parent's rig, which is the
            // question that decides how they get drawn - and it cannot be answered from the
            // body's file.
            foreach (string hung in Attached(ao))
            {
                queue.Enqueue((hung, depth + 1));
            }
        }

        Manifest(read, said, model, walked);
        Shapes(said, model);
        Fitting(read, said, model, walked);
        return said.ToString();
    }

    /// <summary>
    /// The materials and textures behind a rigid model - a terrain tile - shape by shape.
    /// </summary>
    /// <remarks>
    /// A TILE HAS NO .ao CHAIN TO PRINT, and what is asked of it is a different question: why a
    /// shape comes out in a colour no desert has. The answer is in three places a picture cannot
    /// show - which material the shape wears, which texture the material's slots hand over as the
    /// colour map, and what block format that texture is - so all three are printed: every
    /// material verbatim, every texture's DDS header, and one line per shape joining them.
    /// </remarks>
    /// <param name="read">How to get a file out of the install, by path.</param>
    /// <param name="path">The tile's .tdt.</param>
    /// <param name="model">The model already gathered, for the shapes and what each one wears.</param>
    /// <param name="tilesets">Which tilesets place which tile - see <see cref="Ground"/>.</param>
    /// <param name="shaders">The install's shader sources, counted here and written whole by <see cref="ShaderSources"/>.</param>
    public static string OfTile(
        Func<string, byte[]?>? read,
        string path,
        MonsterModel? model,
        TilesetIndex? tilesets = null,
        IReadOnlyList<string>? shaders = null)
    {
        var said = new StringBuilder();
        said.Append("tile: ").AppendLine(path);
        if (read is null || model is null)
        {
            return said.AppendLine("nothing to read: no install, or no model").ToString();
        }

        // DRAWN AS A TILESET, the materials below are the ones it swapped in - say so first, or the
        // .mat section reads as the tile's own.
        if (model.Tileset.Length > 0)
        {
            said.Append("drawn as tileset ").Append(model.Tileset).Append(": ").Append(Say(model.Swapped))
                .AppendLine(" of the tile's materials swapped - the materials below are the swapped-in ones");
        }

        IReadOnlyList<MeshShape> shapes = model.Mesh.Shapes;
        IReadOnlyList<string> materials = model.ShapeMaterials;
        IReadOnlyList<string> textures = model.ShapeTextures;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var graphs = new List<string>();
        foreach (string material in materials)
        {
            string file = MaterialFile.Bare(material);
            if (file.Length == 0 || !seen.Add(file))
            {
                continue;
            }

            said.AppendLine().Append("=== .mat ").AppendLine(file);
            byte[]? content = read(file.Replace('\\', '/').Trim());
            if (content is not { Length: > 0 })
            {
                said.AppendLine("(not in the install)");
                continue;
            }

            said.AppendLine(StatDescriptionFiles.Decode(content).TrimEnd());
            graphs.AddRange(MaterialFile.Read(content).Parents);
        }

        // AND THE GRAPHS THE MATERIALS NAME, verbatim, once each. A material only hands a graph its
        // inputs; what the graph does with them is in the graph. The stromatolite ledge was the case:
        // its .mat gives StromatoliteLedge_Blend a texture in a slot called Meshmap, the pane drew
        // that texture as colour and got green and magenta, and which textures the graph blends by
        // it - and how - is written nowhere but the graph. One level deep: a graph's own parents
        // are printed only where some material names them too.
        var printed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string graph in graphs)
        {
            if (graph.Length == 0 || !printed.Add(graph))
            {
                continue;
            }

            said.AppendLine().Append("=== graph ").AppendLine(graph);
            byte[]? content = read(graph.Replace('\\', '/').Trim());
            said.AppendLine(content is { Length: > 0 } ? StatDescriptionFiles.Decode(content).TrimEnd() : "(not in the install)");
        }

        seen.Clear();
        foreach (string texture in textures)
        {
            if (texture.Length == 0 || !seen.Add(texture))
            {
                continue;
            }

            said.AppendLine().Append("=== texture ").AppendLine(texture);
            said.AppendLine(Header(GameArt.ReadRaw(read, texture)));
        }

        // AND THE TEXTURES THE SHADE PROGRAMS READ, which no shape names: a blend graph's own rock
        // textures and masks. Each with its header and what the decoder made of every channel -
        // the questions a program's colour turns on are "is this alpha real" and "where does this
        // mask's blue sit", and both are numbers, not a look at the picture.
        seen.Clear();
        foreach (ShadeProgram? program in model.Shades)
        {
            if (program is null)
            {
                continue;
            }

            for (var one = 0; one < program.Textures.Count; one++)
            {
                ShadeTexture texture = program.Textures[one];
                if (!seen.Add(texture.Path))
                {
                    continue;
                }

                said.AppendLine().Append("=== program texture ").Append(texture.Path)
                    .AppendLine(texture.Srgb ? " (read as sRGB)" : " (read as linear)");
                said.AppendLine(Header(GameArt.ReadRaw(read, texture.Path)));
                said.Append(Channels(one < program.Sheets.Count ? program.Sheets[one] : null));
            }
        }

        said.AppendLine().Append("=== shapes (").Append(Say(shapes.Count)).AppendLine(")");
        IReadOnlyList<string> modes = model.Modes;
        for (var one = 0; one < shapes.Count; one++)
        {
            MeshShape shape = shapes[one];
            said.Append(Say(one))
                .Append("	from ").Append(Say(shape.From))
                .Append("	count ").Append(Say(shape.Count))
                .Append("	mat ").Append(one < materials.Count && materials[one].Length > 0 ? materials[one] : "-")
                .Append("	tex ").Append(one < textures.Count && textures[one].Length > 0 ? textures[one] : "-")
                .Append("	blend ").Append(one < modes.Count && modes[one].Length > 0 ? modes[one] : "-")
                .Append("	graphs ").AppendLine(one < model.Shades.Count && model.Shades[one] is not null ? "program" : "-");
        }

        Ground(read, path, tilesets, printed, said);
        Shaders(shaders, said);
        return said.ToString();
    }

    /// <summary>Most of one text file printed, so a large list cannot bury the rest of the dump.</summary>
    private const int MostText = 48 * 1024;

    /// <summary>Most tilesets printed; the rest that name the tile are only listed.</summary>
    private const int MostTilesets = 12;

    /// <summary>Most ground materials printed with their graphs and textures.</summary>
    private const int MostGroundMaterials = 32;

    /// <summary>
    /// Everything that could say what a tile's GROUND is drawn with, raw.
    /// </summary>
    /// <remarks>
    /// THE GROUND IS THE HALF OF A TILE THIS CANNOT YET PAINT. The props carry their materials; the
    /// ground block carries none, and on a desert map it is the sand that covers most of a tile -
    /// which is why the picture is all rock where the game shows dunes. No reference paints it:
    /// annalithic's terrain importer colours each corner's ground type at random. So every file on
    /// the way is printed as it is, for a reader to find the link in:
    ///
    ///     the .tdt's four corner ground types (.gt), and each .gt's text
    ///     the .tgt's GroundMask, with its format and channels
    ///     the ground mesh: whether it has coordinates, and their range
    ///     the tilesets (.tsi) whose .tst lists this tile, with what their MaterialsList (.mtd)
    ///     maps the corner types to, their TileMaterialOverrides (.tmo) and BlendMaskOverride -
    ///     the files annalithic's Tsi.cs names and does not open
    ///     the ground materials those lists name, with their graphs and textures
    ///
    /// FOUND BY READING EVERY TILESET, because nothing points from a tile to the areas that use it.
    /// Hundreds of small files, once, behind the button - which is why the dump runs off the frame.
    /// </remarks>
    private static void Ground(
        Func<string, byte[]?> read,
        string path,
        TilesetIndex? tilesets,
        HashSet<string> printed,
        StringBuilder said)
    {
        said.AppendLine().AppendLine("=== ground");

        string at = Slashed(path);
        TileDefinition definition = TileDefinition.None;
        var hops = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (hops.Add(at) && hops.Count <= MostHops)
        {
            definition = TileDefinition.Read(read(at));
            if (!definition.Ready)
            {
                said.Append("the definition did not read: ").Append(at).Append(" - ").AppendLine(definition.Why);
                return;
            }

            if (definition.Inherits.Length == 0)
            {
                break;
            }

            said.Append(at).Append(" inherits ").AppendLine(definition.Inherits);
            at = Slashed(definition.Inherits);
        }

        string[] corners = ["down-left", "down-right", "up-right", "up-left"];
        said.Append("corners of ").Append(at).Append(" (version ").Append(Say(definition.Version)).AppendLine("):");
        for (var corner = 0; corner < corners.Length; corner++)
        {
            string type = corner < definition.Grounds.Count ? definition.Grounds[corner] : string.Empty;
            said.Append("  ").Append(corners[corner]).Append(": ").AppendLine(type.Length > 0 ? type : "-");
        }

        // THE NAME IS THE KEY a tileset's MaterialsList looks the ground up by, so each corner's
        // type is carried on by name, with how many corners it covers.
        var names = new List<(string Name, int Corners)>();
        foreach (string type in definition.Grounds.Where(one => one.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            said.AppendLine().Append("=== .gt ").AppendLine(type);
            string? text = Raw(read, type);
            said.AppendLine(text is null ? "(not in the install)" : Trimmed(text));

            string name = GroundType.NameOf(text);
            if (name.Length > 0)
            {
                names.Add((name, definition.Grounds.Count(one => string.Equals(one, type, StringComparison.OrdinalIgnoreCase))));
            }
        }

        // THE TILE'S OWN MATERIALS, from its templates, for the override lists below: the model's are
        // the ones a chosen tileset swapped in, and an override names what it swaps out.
        var own = new List<string>();
        foreach (string template in definition.Templates)
        {
            byte[]? raw = read(Slashed(template));
            said.AppendLine().Append("=== .tgt ").AppendLine(template);
            said.AppendLine(raw is { Length: > 0 } ? Trimmed(StatDescriptionFiles.Decode(raw)) : "(not in the install)");

            TileTemplate layout = TileTemplate.Read(raw);
            if (!layout.Ready)
            {
                continue;
            }

            own.AddRange(layout.Materials);
            if (layout.GroundMask.Length > 0)
            {
                said.AppendLine().Append("=== ground mask ").AppendLine(layout.GroundMask);
                Picture(read, layout.GroundMask, said);
            }

            said.AppendLine().Append("=== ground mesh of ").AppendLine(template);
            for (var y = 1; y <= layout.Height; y++)
            {
                for (var x = 1; x <= layout.Width; x++)
                {
                    string mesh = layout.MeshOf(x, y);
                    TileMesh part = TileMesh.Read(read(Slashed(mesh)));
                    said.Append("  c").Append(Say(x)).Append('r').Append(Say(y)).Append(": ")
                        .AppendLine(part.Ready ? Spread(part.Ground) : "(did not read: " + part.Why + ")");
                }
            }
        }

        // THE TILE AS ASKED FOR, not the definition it inherits from: a tile list names the tile it
        // places, and a parent definition is another tile as far as the list is concerned.
        Tilesets(read, Slashed(path), tilesets, names, own, printed, said);
    }

    /// <summary>A ground mesh in one line: its size, and where its coordinates and positions lie.</summary>
    private static string Spread(SkinnedMesh mesh)
    {
        if (!mesh.Ready)
        {
            return "no ground";
        }

        var line = new StringBuilder();
        line.Append(Say(mesh.Positions.Length)).Append(" vertices · ").Append(Say(mesh.Triangles)).Append(" triangles · x ")
            .Append(Number(mesh.Least.X)).Append("..").Append(Number(mesh.Most.X)).Append(" y ")
            .Append(Number(mesh.Least.Y)).Append("..").Append(Number(mesh.Most.Y)).Append(" z ")
            .Append(Number(mesh.Least.Z)).Append("..").Append(Number(mesh.Most.Z));

        if (!mesh.Coordinated || mesh.Coordinates.Length == 0)
        {
            return line.Append(" · no texture coordinates").ToString();
        }

        float uLeast = float.MaxValue, uMost = float.MinValue, vLeast = float.MaxValue, vMost = float.MinValue;
        foreach (System.Numerics.Vector2 spot in mesh.Coordinates)
        {
            uLeast = MathF.Min(uLeast, spot.X);
            uMost = MathF.Max(uMost, spot.X);
            vLeast = MathF.Min(vLeast, spot.Y);
            vMost = MathF.Max(vMost, spot.Y);
        }

        return line.Append(" · u ").Append(Number(uLeast)).Append("..").Append(Number(uMost))
            .Append(" v ").Append(Number(vLeast)).Append("..").Append(Number(vMost)).ToString();
    }

    /// <summary>
    /// The tilesets whose tile list names this tile, what each draws this tile's ground with, and those materials.
    /// </summary>
    /// <remarks>
    /// BY THE WHOLE PATH, from <see cref="TilesetIndex"/>. A FILE NAME alone is not this tile: two
    /// tilesets naming <c>Desert/AntNest/Stromatolite/CliffCvM_Stroma1.tdt</c> were printed for
    /// <c>Desert/Stromatolite/</c>'s tile, and their clay was nearly taken for its ground. Those are
    /// only listed; every tileset placing the tile itself - through an included list too - is opened.
    ///
    /// ONLY WHAT TOUCHES THIS TILE: of each MaterialsList the groups this tile's corner types select,
    /// and the group with no name; of each override list the lines that replace one of this tile's
    /// materials or one of those ground materials. The whole files buried the answer last time.
    ///
    /// AND THEN THE GROUND MATERIALS THEMSELVES, once each, the way the props' are printed above:
    /// the material, the graphs not already printed, the Input nodes those graphs name, what the
    /// program compiles to, and every texture it reads with its channels. A ground mesh has no
    /// texture coordinates, so which Input a ground graph takes its coordinates from is the question.
    /// </remarks>
    private static void Tilesets(
        Func<string, byte[]?> read,
        string tile,
        TilesetIndex? tilesets,
        IReadOnlyList<(string Name, int Corners)> names,
        IReadOnlyList<string> worn,
        HashSet<string> printed,
        StringBuilder said)
    {
        said.AppendLine().AppendLine("=== tilesets that use this tile");
        if (tilesets is not { Searched: > 0 })
        {
            said.AppendLine("(no tilesets read - the install walk collected no .tsi under Metadata/Terrain, or it has not run yet)");
            return;
        }

        IReadOnlyList<string> placing = tilesets.Of(tile);
        IReadOnlyList<(string Tile, IReadOnlyList<string> Tilesets)> alike = tilesets.Alike(tile);
        said.Append("searched ").Append(Say(tilesets.Searched)).Append(" tilesets; ")
            .Append(Say(placing.Count)).Append(" place this tile, ")
            .Append(Say(alike.Sum(one => one.Tilesets.Count))).AppendLine(" a tile of the same file name in another folder");
        foreach (string tsi in placing)
        {
            said.Append("  ").AppendLine(tsi);
        }

        foreach ((string other, IReadOnlyList<string> sets) in alike)
        {
            foreach (string tsi in sets)
            {
                said.Append("  ").Append(tsi).Append(" (places ").Append(other).AppendLine(" - another tile, not opened)");
            }
        }

        var tileWears = new HashSet<string>(
            worn.Select(MaterialFile.Bare).Where(one => one.Length > 0).Select(Slashed), StringComparer.OrdinalIgnoreCase);
        var grounds = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (string tsi in placing.Take(MostTilesets))
        {
            string where = TilesetIndex.Short(tsi);
            string text = Raw(read, tsi) ?? string.Empty;
            said.AppendLine().Append("=== .tsi ").AppendLine(tsi);
            said.AppendLine(Trimmed(text));

            var mine = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (TilesetFile.Value(text, "MaterialsList") is { Length: > 0 } named)
            {
                string mtd = TilesetFile.Beside(tsi, named);
                GroundMaterials list = GroundMaterials.Read(read(Slashed(mtd)));
                said.AppendLine().Append("=== MaterialsList ").AppendLine(mtd);
                if (list.Groups.Count == 0)
                {
                    said.Append("(did not read: ").Append(list.Why).AppendLine(")");
                    said.AppendLine(Text(read, mtd));
                }
                else
                {
                    said.Append("version ").Append(Say(list.Version)).Append(" · ").Append(Say(list.Groups.Count)).Append(" groups: ")
                        .AppendLine(string.Join(", ", list.Groups.Select(one => one.Name.Length > 0 ? "\"" + one.Name + "\"" : "(unnamed)")));
                    if (!list.Ready)
                    {
                        said.Append("(stopped reading at ").Append(list.Why).AppendLine(")");
                    }

                    void Offered(GroundGroup group, string label)
                    {
                        said.Append("  ").Append(label).Append(": ");
                        Group(group, said);
                        foreach (GroundChoice choice in group.Choices.Concat(group.Extras))
                        {
                            mine.Add(choice.Material);
                            if (!grounds.TryGetValue(choice.Material, out List<string>? users))
                            {
                                users = [];
                                grounds[choice.Material] = users;
                                order.Add(choice.Material);
                            }

                            users.Add(where + " " + (group.Name.Length > 0 ? "\"" + group.Name + "\"" : "(unnamed)"));
                        }
                    }

                    foreach ((string name, int corners) in names)
                    {
                        string label = "\"" + name + "\" at " + Say(corners) + (corners == 1 ? " corner" : " corners");
                        if (list.Named(name) is { } group)
                        {
                            Offered(group, label);
                        }
                        else
                        {
                            said.Append("  ").Append(label).AppendLine(": not listed");
                        }
                    }

                    if (list.Named(string.Empty) is { } unnamed)
                    {
                        Offered(unnamed, "the unnamed group");
                    }
                }
            }

            if (TilesetFile.Value(text, "TileMaterialOverrides") is { Length: > 0 } overriding)
            {
                string tmo = TilesetFile.Beside(tsi, overriding);
                IReadOnlyList<MaterialOverride> swaps = MaterialOverrides.Read(read(Slashed(tmo)));
                var touching = swaps.Where(one => tileWears.Contains(Slashed(one.From)) || mine.Contains(Slashed(one.From))).ToList();
                said.AppendLine().Append("=== TileMaterialOverrides ").AppendLine(tmo);
                said.Append(Say(swaps.Count)).Append(" overrides, ").Append(Say(touching.Count))
                    .AppendLine(" of this tile's materials or its ground's:");
                foreach (MaterialOverride swap in touching)
                {
                    said.Append("  ").Append(swap.From).Append(" -> ").AppendLine(swap.To);
                }
            }

            if (TilesetFile.Value(text, "BlendMaskOverride") is { Length: > 0 } blend)
            {
                string mask = TilesetFile.Beside(tsi, blend);
                said.AppendLine().Append("=== BlendMaskOverride ").AppendLine(mask);
                Picture(read, mask, said);
            }
        }

        GroundMaterialsOf(read, order, grounds, printed, said);
    }

    /// <summary>A MaterialsList group in a few lines: its choices, weights, and the numbers nobody has named.</summary>
    private static void Group(GroundGroup group, StringBuilder said)
    {
        said.Append(Say(group.Choices.Count)).Append(group.Choices.Count == 1 ? " choice" : " choices");
        if (group.Weights.Count > 0)
        {
            said.Append(" · weights ").Append(string.Join(' ', group.Weights.Select(Say))).Append(" · then ").Append(Say(group.Trailing));
        }

        said.AppendLine();
        Chosen(group.Choices, said);
        if (group.Extras.Count > 0)
        {
            said.Append("    ").Append(Say(group.Extras.Count)).Append(" extra after ")
                .Append(Say(group.ExtraNumber)).Append(' ').AppendLine(group.ExtraFlag ? "1" : "0");
            Chosen(group.Extras, said);
        }
    }

    private static void Chosen(IReadOnlyList<GroundChoice> choices, StringBuilder said)
    {
        foreach (GroundChoice choice in choices)
        {
            said.Append("      ").Append(choice.Material);
            foreach (string layer in choice.Layers)
            {
                said.Append(" + ").Append(layer);
            }

            said.AppendLine();
        }
    }

    /// <summary>
    /// Every ground material the tilesets offer this tile, once each: the material, its graphs, what they compile to, and the textures read.
    /// </summary>
    private static void GroundMaterialsOf(
        Func<string, byte[]?> read,
        IReadOnlyList<string> order,
        Dictionary<string, List<string>> grounds,
        HashSet<string> printed,
        StringBuilder said)
    {
        if (order.Count == 0)
        {
            return;
        }

        said.AppendLine().Append("=== ground materials (").Append(Say(order.Count)).AppendLine(")");
        var paints = new MonsterModels.Paints();
        var pictured = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string material in order.Take(MostGroundMaterials))
        {
            said.AppendLine().Append("=== ground .mat ").AppendLine(material);
            said.Append("offered by: ").AppendLine(string.Join(", ", grounds[material]));
            byte[]? content = read(Slashed(material));
            if (content is not { Length: > 0 })
            {
                said.AppendLine("(not in the install)");
                continue;
            }

            said.AppendLine(Trimmed(StatDescriptionFiles.Decode(content)));
            (ShadeCompile compiled, IReadOnlyList<string> inputs) = paints.Compiled(read, material);
            said.Append("graphs name: ").AppendLine(inputs.Count > 0 ? string.Join(", ", inputs) : "no Input node");
            said.Append("program: ").AppendLine(compiled.Program switch
            {
                null => "none - " + (compiled.Skipped.Count > 0 ? string.Join("; ", compiled.Skipped) : "no graph compiled"),
                { Plain: >= 0 } plain => "a plain read of " + plain.Textures[plain.Plain].Path,
                { } program => Say(program.Textures.Count) + " textures read"
                    + (compiled.Skipped.Count > 0 ? " · left out: " + string.Join("; ", compiled.Skipped) : string.Empty),
            });

            foreach (string graph in MaterialFile.Read(content).Parents)
            {
                if (graph.Length == 0 || !printed.Add(graph))
                {
                    continue;
                }

                said.AppendLine().Append("=== graph ").AppendLine(graph);
                byte[]? text = read(Slashed(graph));
                said.AppendLine(text is { Length: > 0 } ? StatDescriptionFiles.Decode(text).TrimEnd() : "(not in the install)");
            }

            if (compiled.Program is not { } shading)
            {
                continue;
            }

            foreach (ShadeTexture texture in shading.Textures)
            {
                if (!pictured.Add(texture.Path))
                {
                    continue;
                }

                said.AppendLine().Append("=== ground texture ").Append(texture.Path)
                    .AppendLine(texture.Srgb ? " (read as sRGB)" : " (read as linear)");
                Picture(read, texture.Path, said);
            }
        }

        if (order.Count > MostGroundMaterials)
        {
            said.AppendLine().Append(Say(order.Count - MostGroundMaterials)).AppendLine(" more ground materials not printed");
        }
    }

    /// <summary>What the file every shader source is written into, beside a tile's dump, is called.</summary>
    public const string ShaderFile = "shader-sources.txt";

    /// <summary>
    /// Where the shader sources are, by folder - the sources themselves go whole into <see cref="ShaderFile"/>.
    /// </summary>
    /// <remarks>
    /// A SEARCH BY KEYWORD WAS NOT ENOUGH. The first version printed every line naming the ground or
    /// the terrain, hit its limit of 240 lines with eight files unread, and gave lines without the
    /// fragments around them - <c>uv = tile_ground_tiling * pos * ground_scalemove_uv.xy</c> with no
    /// way to tell what <c>pos</c> is. The questions waiting on these files are bigger than the
    /// ground, too: the order of the stages a graph runs in (<c>Texturing_Calc</c>, which the desert
    /// dust writes its colour at), and what each graph node computes - <c>SmoothStep</c> with a centre
    /// and a steepness is not HLSL's smoothstep. So every source is written whole, once, beside the
    /// dump, and read where it can be searched properly.
    /// </remarks>
    private static void Shaders(IReadOnlyList<string>? shaders, StringBuilder said)
    {
        said.AppendLine().AppendLine("=== shader sources");
        if (shaders is not { Count: > 0 })
        {
            said.AppendLine("(the install walk found none: nothing under Shaders/ and no .ffx, .hlsl, .hlsli or .fxh anywhere - or it has not run yet)");
            return;
        }

        said.Append(Say(shaders.Count)).AppendLine(" files, by folder:");
        foreach (IGrouping<string, string> folder in shaders
            .GroupBy(Folder, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(one => one.Count())
            .ThenBy(one => one.Key, StringComparer.OrdinalIgnoreCase)
            .Take(48))
        {
            said.Append("  ").Append(folder.Key).Append("  ").AppendLine(Say(folder.Count()));
        }

        said.Append("every one is written whole beside this dump, to ").AppendLine(ShaderFile);
    }

    /// <summary>
    /// Every shader source whole, one after another under its path - see <see cref="Shaders"/>.
    /// </summary>
    /// <remarks>
    /// IN PATH ORDER, so two exports diff. Nothing cut short: a fragment is only useful whole. A file
    /// the install will not hand over is named as missing rather than dropped, so the count matches.
    /// </remarks>
    /// <param name="read">How to get a file out of the install, by path.</param>
    /// <param name="shaders">The install's shader sources.</param>
    public static string ShaderSources(Func<string, byte[]?>? read, IReadOnlyList<string>? shaders)
    {
        var said = new StringBuilder();
        if (read is null || shaders is not { Count: > 0 })
        {
            return said.AppendLine("no shader sources: no install, or the install walk found none").ToString();
        }

        List<string> ordered = [.. shaders.Order(StringComparer.OrdinalIgnoreCase)];
        said.Append(Say(ordered.Count)).AppendLine(" shader sources from the install, whole, in path order").AppendLine();
        foreach (string one in ordered)
        {
            said.Append("=== shader source ").AppendLine(one);
            said.AppendLine(Raw(read, one) is { } text ? text.TrimEnd() : "(not in the install)").AppendLine();
        }

        return said.ToString();
    }

    /// <summary>A path's first two folders, the grouping the shader list is counted by.</summary>
    private static string Folder(string path)
    {
        string slashed = Slashed(path);
        int first = slashed.IndexOf('/');
        int second = first >= 0 ? slashed.IndexOf('/', first + 1) : -1;
        return second > 0 ? slashed[..second] : first > 0 ? slashed[..first] : "(top level)";
    }

    /// <summary>A texture's header and what the decoder made of its channels.</summary>
    private static void Picture(Func<string, byte[]?> read, string path, StringBuilder said)
    {
        byte[]? raw = GameArt.ReadRaw(read, path);
        said.AppendLine(Header(raw));
        said.Append(Channels(Mipmaps.Of(GameArt.Decode(raw))));
    }

    private static string? Raw(Func<string, byte[]?> read, string path)
        => read(Slashed(path)) is { Length: > 0 } bytes ? StatDescriptionFiles.Decode(bytes) : null;

    private static string Text(Func<string, byte[]?> read, string path)
        => Raw(read, path) is { } text ? Trimmed(text) : "(not in the install)";

    private static string Trimmed(string text)
        => text.Length > MostText ? text[..MostText].TrimEnd() + Environment.NewLine + "(cut off)" : text.TrimEnd();

    private static string Slashed(string path) => path.Replace('\\', '/').Trim();

    private static string Number(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// What a DDS file's header says: its size, its levels and its block format.
    /// </summary>
    /// <remarks>
    /// THE FORMAT IS THE PART WORTH HAVING. A texture decoded in the wrong format - or a two-channel
    /// map taken for a colour one - is a shape in colours nobody painted, and only the header says
    /// which of the two it is. Read straight off the bytes, because the decoder keeps none of it.
    /// </remarks>
    private static string Header(byte[]? dds)
    {
        const int Least = 128;
        if (dds is null || dds.Length < Least)
        {
            return "(not in the install, or too short to be a DDS)";
        }

        if (dds[0] != (byte)'D' || dds[1] != (byte)'D' || dds[2] != (byte)'S' || dds[3] != (byte)' ')
        {
            return "(not a DDS)";
        }

        int height = BitConverter.ToInt32(dds, 12);
        int width = BitConverter.ToInt32(dds, 16);
        int levels = BitConverter.ToInt32(dds, 28);
        uint flags = BitConverter.ToUInt32(dds, 80);
        string fourCc = Encoding.ASCII.GetString(dds, 84, 4).TrimEnd('\0');
        var line = new StringBuilder();
        line.Append(Say(width)).Append('x').Append(Say(height))
            .Append(" · ").Append(Say(levels)).Append(" levels")
            .Append(" · pixel format flags 0x").Append(flags.ToString("X", CultureInfo.InvariantCulture));

        if ((flags & 0x4) != 0)
        {
            line.Append(" · fourCC ").Append(fourCc);
            if (fourCc == "DX10" && dds.Length >= Least + 4)
            {
                int format = BitConverter.ToInt32(dds, Least);
                line.Append(" · DXGI format ").Append(Say(format));
                if (Dxgi(format) is { Length: > 0 } name)
                {
                    line.Append(" (").Append(name).Append(')');
                }
            }
        }
        else
        {
            line.Append(" · ").Append(Say(BitConverter.ToInt32(dds, 88))).Append(" bits")
                .Append(" · masks r 0x").Append(BitConverter.ToUInt32(dds, 92).ToString("X8", CultureInfo.InvariantCulture))
                .Append(" g 0x").Append(BitConverter.ToUInt32(dds, 96).ToString("X8", CultureInfo.InvariantCulture))
                .Append(" b 0x").Append(BitConverter.ToUInt32(dds, 100).ToString("X8", CultureInfo.InvariantCulture))
                .Append(" a 0x").Append(BitConverter.ToUInt32(dds, 104).ToString("X8", CultureInfo.InvariantCulture));
        }

        return line.ToString();
    }

    /// <summary>
    /// The DXGI formats textures come in, by number - the ones that settle whether a channel is there.
    /// </summary>
    /// <remarks>
    /// THE NUMBERS ARE DIRECTX'S OWN, from its DXGI_FORMAT enumeration. Only the ones a texture of
    /// this game has turned up in, or whose alpha is the question - with or without an X, one channel
    /// or four. Anything else prints as its number alone.
    /// </remarks>
    private static string Dxgi(int format) => format switch
    {
        2 => "R32G32B32A32_FLOAT",
        10 => "R16G16B16A16_FLOAT",
        28 => "R8G8B8A8_UNORM",
        29 => "R8G8B8A8_UNORM_SRGB",
        49 => "R8G8_UNORM",
        61 => "R8_UNORM",
        65 => "A8_UNORM",
        71 => "BC1_UNORM",
        72 => "BC1_UNORM_SRGB",
        74 => "BC2_UNORM",
        77 => "BC3_UNORM",
        78 => "BC3_UNORM_SRGB",
        80 => "BC4_UNORM",
        83 => "BC5_UNORM",
        87 => "B8G8R8A8_UNORM",
        88 => "B8G8R8X8_UNORM",
        91 => "B8G8R8A8_UNORM_SRGB",
        95 => "BC6H_UF16",
        98 => "BC7_UNORM",
        99 => "BC7_UNORM_SRGB",
        _ => string.Empty,
    };

    /// <summary>
    /// What the decoder made of each channel of a texture's full-size level: lowest, a tenth of the
    /// way up, the middle, nine tenths, highest, and the mean - as bytes, 0 to 255.
    /// </summary>
    /// <remarks>
    /// AS THE DECODER HANDS THEM OVER, before any sRGB is undone, because that is the thing in
    /// question: an alpha of 255 everywhere is either a texture without alpha or a decoder that
    /// dropped it, and the header beside it says which. Spread as well as range, because a mask
    /// that is 255 on one texel and 0 on the rest has the same lowest and highest as one that is
    /// half and half.
    /// </remarks>
    private static string Channels(Mipmaps? sheet)
    {
        if (sheet is null)
        {
            return "(not decoded)" + Environment.NewLine;
        }

        GamePicture top = sheet.Top;
        byte[] rgba = top.Rgba;
        int texels = top.Width * top.Height;
        var counts = new int[4 * 256];
        var sums = new long[4];
        for (var at = 0; at < texels; at++)
        {
            int from = at * 4;
            for (var part = 0; part < 4; part++)
            {
                byte value = rgba[from + part];
                counts[(part * 256) + value]++;
                sums[part] += value;
            }
        }

        var said = new StringBuilder();
        said.Append(Say(top.Width)).Append('x').Append(Say(top.Height)).AppendLine(" decoded · channel: lowest p10 median p90 highest · mean");
        ReadOnlySpan<char> names = "rgba";
        for (var part = 0; part < 4; part++)
        {
            ReadOnlySpan<int> channel = counts.AsSpan(part * 256, 256);
            said.Append("  ").Append(names[part]).Append(": ")
                .Append(Say(Rank(channel, 0))).Append(' ')
                .Append(Say(Rank(channel, texels / 10))).Append(' ')
                .Append(Say(Rank(channel, texels / 2))).Append(' ')
                .Append(Say(Rank(channel, (texels * 9) / 10))).Append(' ')
                .Append(Say(Rank(channel, texels - 1)))
                .Append(" · ").AppendLine((sums[part] / (double)Math.Max(1, texels)).ToString("F1", CultureInfo.InvariantCulture));
        }

        return said.ToString();

        // THE VALUE AT ONE RANK OF THE SORTED CHANNEL, off its histogram.
        static int Rank(ReadOnlySpan<int> channel, int rank)
        {
            var below = 0;
            for (var value = 0; value < channel.Length; value++)
            {
                below += channel[value];
                if (below > rank)
                {
                    return value;
                }
            }

            return channel.Length - 1;
        }
    }

    /// <summary>
    /// Where each piece SITS, and whether its socket is a bone the parent's rig really has.
    /// </summary>
    /// <remarks>
    /// THE QUESTION THE PICTURE ASKED. With the pieces joined on, Doryani's shoulder danglers came
    /// out symmetrical about him and at knee height - right in x, wrong in y - and his skirt hung
    /// far below his feet. Symmetric-but-sunken is the signature of a piece whose bones did not
    /// match and fell back to the root, and it cannot be told apart from a piece modelled in its
    /// own space by looking at it.
    ///
    /// SO BOTH HALVES ARE PRINTED. The socket, and whether the PARENT rig carries a bone of that
    /// name at all - a socket the parent does not have means every vertex of that piece falls
    /// back. And the piece's own bounding box beside the body's: a box around the origin says the
    /// mesh is modelled in its own space and needs the socket's transform on it, while a box up at
    /// shoulder height says it is already in the parent's space and only the bones are wrong.
    ///
    /// Those are different fixes, and this is the difference.
    /// </remarks>
    private static void Fitting(
        Func<string, byte[]?> read, StringBuilder said, MonsterModel? model, IReadOnlyList<string> walked)
    {
        said.AppendLine().AppendLine("=== fitting");

        if (model?.Rig is not { Ready: true } rig)
        {
            said.AppendLine("(the monster has no rig, so nothing here can be matched)");
            return;
        }

        var bones = new HashSet<string>(rig.Bones.Select(one => one.Name), StringComparer.OrdinalIgnoreCase);
        said.Append("the monster's rig: ").Append(Say(rig.Bones.Count)).AppendLine(" bones");

        // THE MOUNTS THE MONSTER OFFERS. The game's own name for an attachment point is aux_ -
        // Malgor's anchor hangs off aux_anchor_jntBnd and his cannon off aux_cannon_jntBnd - so
        // this says in one line which of them a rig has. It is what answers "is this piece meant
        // for a socket nobody wrote down", which a piece socketed "<root>" makes worth asking.
        string[] mounts =
        [
            .. rig.Bones.Select(one => one.Name)
                .Where(one => one.StartsWith("aux", StringComparison.OrdinalIgnoreCase)),
        ];

        said.Append("mounts: ").AppendLine(mounts.Length > 0 ? string.Join(", ", mounts) : "(none named aux_)");

        // THE PARENT'S OWN REST POSE, so a piece's bones can be held up against it by name. Where
        // the two rigs put a shared bone in the same place, the piece is modelled in the
        // monster's space; where they do not, it is not - and that was decided three times by
        // guessing before it was printed once.
        var named_ = new Dictionary<string, int>(rig.Bones.Count, StringComparer.OrdinalIgnoreCase);
        for (var one = 0; one < rig.Bones.Count; one++)
        {
            named_.TryAdd(rig.Bones[one].Name, one);
        }

        IReadOnlyList<System.Numerics.Matrix4x4> over_ = SkeletonPose.Of(rig)?.BindModel ?? [];

        // WHAT HANGS OFF A PIECE IS PRINTED UNDER THAT PIECE AND AGAINST ITS RIG - see Hanging.
        // The flat list held Tycho's skirt layers up against the BODY, which is not the rig they
        // are authored beside, and a dump that compares the wrong two rigs sends the next reader
        // the way this one went. The list is still what starts the walk, and the pieces reached
        // through their carrier are struck off it as they are printed.
        Hanging(read, said, walked, bones, named_, over_, 1, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>How deep the attachment tree is followed here. Doryani's belt hangs a dagger.</summary>
    private const int MostDeep = 4;

    /// <summary>
    /// Every attachment line in one carrier's files, printed against THAT carrier's rig.
    /// </summary>
    /// <remarks>
    /// A PIECE HANGS OFF WHATEVER CARRIES IT. Tycho's SkirtLayers.ao is an attached_object of his
    /// Skirt.ao and rests its bones exactly where the SKIRT rests them; held up against the body
    /// it looked like a piece sharing two names by accident, which is what this printed for as
    /// long as it walked every file it had seen in one flat list.
    /// </remarks>
    private static void Hanging(
        Func<string, byte[]?> read,
        StringBuilder said,
        IReadOnlyList<string> files,
        HashSet<string> bones,
        IReadOnlyDictionary<string, int> parent,
        IReadOnlyList<System.Numerics.Matrix4x4> over_,
        int depth,
        HashSet<string> seen)
    {
        foreach (string one in files)
        {
            // KEYED ON THE PATH THE INSTALL ANSWERED WITH, not on the one that was asked for, so
            // a piece reached through its carrier and again through the flat list is one piece.
            (string at, byte[]? content) = Find(read, one);
            if (content is not { Length: > 0 } || !seen.Add(at))
            {
                continue;
            }

            foreach (AoStruct block in AnimatedObject.Read(content).Structs)
            {
                foreach (AoEntry entry in block.Entries)
                {
                    if (Array.IndexOf(Hangs, entry.Key) < 0 || entry.Kind != AoValueKind.Quoted)
                    {
                        continue;
                    }

                    Hung(read, said, bones, parent, over_, entry, depth, seen);
                }
            }
        }
    }

    /// <summary>One attachment line: its socket, whether the parent has that bone, and its box.</summary>
    private static void Hung(
        Func<string, byte[]?> read,
        StringBuilder said,
        HashSet<string> bones,
        IReadOnlyDictionary<string, int> parent,
        IReadOnlyList<System.Numerics.Matrix4x4> over_,
        AoEntry entry,
        int depth,
        HashSet<string> seen)
    {
        string value = entry.Value.Trim();
        int space = value.IndexOf(' ', StringComparison.Ordinal);
        string socket = space < 0 ? string.Empty : value[..space];
        string path = space < 0 ? value : value[(space + 1)..].Trim();
        if (!path.EndsWith(AnimatedObject.Suffix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string step = new(' ', depth * 2);
        said.Append(step).Append(path[(path.LastIndexOf('/') + 1)..])
            .Append("  socket ").Append(socket.Length > 0 ? socket : "(none)")
            .Append(socket.Length > 0 && bones.Contains(socket) ? " [in its carrier's rig]" : " [NOT in its carrier's rig]");

        // THE CHILDREN OF THE ATTACHMENT LINE, which move the piece off its socket and which the
        // walk threw away until the Frostborn Fiend's block of ice turned up upside down on the
        // floor. Printed as written, because the axis order of a rotation with two non-zero
        // components is still unsettled and the next monster that has one settles it.
        foreach (AoEntry child in entry.Children)
        {
            said.Append("  ").Append(child.Key).Append(" = \"").Append(child.Value).Append('"');
        }

        (string _, byte[]? content) = Find(read, path);
        if (content is not { Length: > 0 })
        {
            said.AppendLine("  (not in the install)");
            return;
        }

        // THE PIECE AND EVERYTHING IT EXTENDS. Gulzal's hammer is four lines and a parent, and
        // a dump that read the four lines said it had no rig, no pairing and no materials - all
        // three of which are in the axe it extends. A diagnostic that reads less than the walk
        // does sends the next reader the wrong way.
        List<AnimatedObject> chain = Whole(read, AnimatedObject.Read(content));
        AnimatedObject piece = chain[0];
        string skin = Skin(chain);
        if (skin.Length == 0)
        {
            // A RIGID PROP IS NOT AN EFFECT, and calling it one is what hid Malgor's cannon: it
            // names a .fmt through FixedMesh, carries its own materials, and has no .sm at all.
            string prop = Entryed(chain, "FixedMesh", "fixed_mesh");
            if (prop.Length == 0)
            {
                said.AppendLine("  (no mesh - an effect or a sound)");
                Under(read, said, path, bones, parent, over_, depth, seen);
                return;
            }

            FixedMesh fixture = FixedMesh.Read(read(prop.Replace('\\', '/').Trim()));
            said.Append("  fixed mesh ").Append(prop[(prop.LastIndexOf('/') + 1)..]);
            said.AppendLine(fixture.Ready
                ? $"  {Say(fixture.Mesh.Shapes.Count)} shapes"
                : $"  (did not read: {fixture.Why})");

            foreach ((string shape, string material) in fixture.Named)
            {
                said.Append(step).Append("    ").Append(shape).Append("  ->  ")
                    .AppendLine(material.Length > 0 ? material : "(no material)");
            }

            Under(read, said, path, bones, parent, over_, depth, seen);
            return;
        }

        MeshManifest manifest = MeshManifest.Read(read(skin.Replace('\\', '/').Trim()));
        said.Append("  box ").Append(Box(manifest.Least)).Append("..").AppendLine(Box(manifest.Most));

        SkinnedMesh geometry = SkinnedMesh.Read(read(manifest.Geometry.Replace('\\', '/').Trim()));
        Facts(said, step + "  ", geometry.Facts);
        AnimationSkeleton? own = Rigged(
            read, said, bones, parent, over_, SkeletonPose.Highest(geometry), chain, step);

        // AND WHAT HANGS OFF THIS PIECE, against THIS piece's rig where it brought one.
        if (own is { Ready: true } rig && SkeletonPose.Of(rig) is { } mine)
        {
            var theirs = new HashSet<string>(
                rig.Bones.Select(one => one.Name), StringComparer.OrdinalIgnoreCase);
            var named_ = new Dictionary<string, int>(rig.Bones.Count, StringComparer.OrdinalIgnoreCase);
            for (var one = 0; one < rig.Bones.Count; one++)
            {
                named_.TryAdd(rig.Bones[one].Name, one);
            }

            Under(read, said, path, theirs, named_, mine.BindModel, depth, seen);
            return;
        }

        Under(read, said, path, bones, parent, over_, depth, seen);
    }

    /// <summary>Whatever a piece hangs off ITSELF, one step deeper and against its own rig.</summary>
    private static void Under(
        Func<string, byte[]?> read,
        StringBuilder said,
        string path,
        HashSet<string> bones,
        IReadOnlyDictionary<string, int> parent,
        IReadOnlyList<System.Numerics.Matrix4x4> over_,
        int depth,
        HashSet<string> seen)
    {
        if (depth < MostDeep)
        {
            Hanging(read, said, [path], bones, parent, over_, depth + 1, seen);
        }
    }

    /// <summary>
    /// How many of a piece's own bones are printed.
    /// </summary>
    /// <remarks>
    /// ENOUGH FOR THE WHOLE RIG, since Brughor's corpse armour turned up with eighty bones and
    /// the answer to where his corpses go is in the ones this stopped before: whether a plain
    /// name like <c>hip_jntBnd</c> is the monster's or a corpse's is settled by whether the same
    /// rig ALSO carries a <c>…|hip_jntBnd</c>, and a list cut at twenty-four cannot say.
    /// </remarks>
    private const int MostBones = 128;

    /// <summary>
    /// A piece's OWN rig, beside the parent's: whose names its bones carry and where they rest.
    /// </summary>
    /// <remarks>
    /// THE QUESTION TWO MONSTERS HAVE NOW ASKED. Bahlak's feather bundle and Malgor's ship's wheel
    /// and seaweed are all socketed <c>&lt;root&gt;</c> - no bone of the parent to hang them from -
    /// and all three come out at the monster's origin instead of on him. Every one of them brings
    /// a rig of its own, and the seaweed's .ao goes further and lists PARENT bone names in
    /// <c>attachment_bones</c>: <c>hip_jntBnd spine_1_jntBnd … R_arm_tentacle_jntBnd_1</c>.
    ///
    /// SO THE ANSWER IS IN THE PIECE'S OWN SKELETON, and this prints it rather than assuming it.
    /// If its bones carry the parent's names, the piece is skinned to the parent's rig and belongs
    /// in the monster's space through each bone's rest transform - which is a different fix from
    /// the rigid socket the anchor and the beard get. If they carry names of their own, it is not,
    /// and the answer is elsewhere. The two cases look identical in a picture and are told apart
    /// here, in one file, without another build.
    /// </remarks>
    private static AnimationSkeleton? Rigged(
        Func<string, byte[]?> read,
        StringBuilder said,
        HashSet<string> bones,
        IReadOnlyDictionary<string, int> parent,
        IReadOnlyList<System.Numerics.Matrix4x4> over_,
        int highest,
        IReadOnlyList<AnimatedObject> chain,
        string step)
    {
        string path = string.Empty;
        foreach (AoStruct block in chain.SelectMany(one => one.Named("ClientAnimationController")))
        {
            foreach (AoEntry entry in block.Entries)
            {
                if (string.Equals(entry.Key, "skeleton", StringComparison.Ordinal) && entry.Value.Length > 0)
                {
                    path = entry.Value;
                }
            }
        }

        if (path.Length == 0)
        {
            said.Append(step).AppendLine("  (no rig of its own)");
            return null;
        }

        AnimationSkeleton own = AnimationSkeleton.Read(read(path.Replace('\\', '/').Trim()));
        if (!own.Ready)
        {
            said.Append(step).Append("  rig ").Append(path).AppendLine(" (did not read)");
            return null;
        }

        var shared = 0;
        foreach (SkeletonBone one in own.Bones)
        {
            if (Meant(bones, one.Name))
            {
                shared++;
            }
        }

        said.Append(step).Append("  rig ").Append(Say(own.Bones.Count)).Append(" bones, ")
            .Append(Say(shared)).AppendLine(" of them names its carrier's rig also has");

        // AND WHETHER THE PIECE'S MESH IS INDEXED BY THAT RIG AT ALL. A mesh whose highest
        // weighted bone is past the rig's count was rigged to something else - and the only
        // other rig in play is the parent's - so its numbers must not be put through the
        // piece's table. Printed because a mesh half-mapped and half-dropped looks exactly like
        // a piece in the wrong place.
        if (highest >= 0)
        {
            said.Append(step).Append("  mesh weights reach bone ").Append(Say(highest))
                .Append(" of ").Append(Say(own.Bones.Count))
                .AppendLine(highest >= own.Bones.Count
                    ? "  [PAST this rig - it is the carrier's numbering]"
                    : "  [inside this rig]");
        }

        SkeletonPose? pose = SkeletonPose.Of(own);
        IReadOnlyList<System.Numerics.Matrix4x4> rest = pose?.BindModel ?? [];
        IReadOnlyList<int> over = pose?.Parents ?? [];

        for (var one = 0; one < own.Bones.Count && one < MostBones; one++)
        {
            said.Append(step).Append("    ").Append(Say(one)).Append(' ').Append(own.Bones[one].Name)
                .Append(Meant(bones, own.Bones[one].Name) ? "  [shared]" : "  [its own]");

            // THE PARENT'S NUMBER, because it is not always lower than the child's - a bone's
            // ancestors are walked to find one the parent rig has, and a walk that assumed the
            // order dropped Bahlak's head feathers on the rig root. Printed so the next reader
            // can see the ordering rather than assume it.
            if (one < over.Count)
            {
                said.Append("  under ").Append(over[one] >= 0 ? Say(over[one]) : "nothing");
            }

            if (one < rest.Count)
            {
                said.Append("  rests at ").Append(Box(rest[one].Translation));
            }

            // AND WHERE THE CARRIER RESTS THE SAME BONE, side by side. Whether the two rigs carry
            // the same rest pose is the question every theory about these pieces turned on, and
            // it was answered three times by guessing before it was ever printed.
            if (Meant(parent, own.Bones[one].Name, out int also) && also < over_.Count)
            {
                said.Append("  carrier has ").Append(Box(over_[also].Translation));
            }

            said.AppendLine();
        }

        return own;
    }

    /// <summary>
    /// Whether a carrier's rig carries a bone of this name, a MERGED rig's path included.
    /// </summary>
    /// <remarks>
    /// THE SAME READING MonsterModels USES, and it belongs here for the same reason the chain
    /// does: a dump that matches fewer names than the walk says a piece shares nothing when it
    /// shares its whole spine. Brughor's corpse armour writes the monster's bones as their whole
    /// path - <c>root_jntBnd|spine_2_jntBnd|chest_jntBnd</c> - because the corpses merged into it
    /// carry a chest of their own.
    /// </remarks>
    private static bool Meant(HashSet<string> bones, string name)
    {
        if (bones.Contains(name))
        {
            return true;
        }

        int bar = name.LastIndexOf('|');
        return bar >= 0 && bones.Contains(name[(bar + 1)..]);
    }

    /// <inheritdoc cref="Meant(HashSet{string}, string)"/>
    private static bool Meant(IReadOnlyDictionary<string, int> where, string name, out int found)
    {
        if (where.TryGetValue(name, out found))
        {
            return true;
        }

        int bar = name.LastIndexOf('|');
        return bar >= 0 && where.TryGetValue(name[(bar + 1)..], out found);
    }

    private static string Box(System.Numerics.Vector3 at)
        => $"({at.X.ToString("0.#", CultureInfo.InvariantCulture)},"
            + $"{at.Y.ToString("0.#", CultureInfo.InvariantCulture)},"
            + $"{at.Z.ToString("0.#", CultureInfo.InvariantCulture)})";

    /// <summary>
    /// The .ao files this one hangs off itself - armour, clothing, weapons, effects.
    /// </summary>
    /// <remarks>
    /// THE FOUR KEYS <see cref="AoSurvey"/> ALREADY FOLLOWS, and its parsing with them: an
    /// attachment's value is a SOCKET AND THEN A PATH inside one pair of quotes -
    /// <c>"hip_jntBnd Metadata/Monsters/Doryani/TrueDoryani/attachments/Skirt.ao"</c> - so taken
    /// whole it is a path no install has. That trap cost the first survey 2289 of its 3262 files
    /// and is not worth falling into twice.
    ///
    /// THE SOCKET IS NOT KEPT HERE, only the file. The socket names a bone of the parent's rig
    /// (<c>_jntBnd</c>, the same suffix the manifest's BoneGroups use) and it is what a renderer
    /// would need; a dump only has to get the file on screen, and the line it came from is
    /// printed above it in full anyway.
    /// </remarks>
    private static IEnumerable<string> Attached(AnimatedObject ao)
    {
        foreach (AoStruct block in ao.Structs)
        {
            foreach (AoEntry entry in block.Entries)
            {
                if (Array.IndexOf(Hangs, entry.Key) < 0)
                {
                    continue;
                }

                foreach (string one in AoSurvey.Referenced(entry))
                {
                    yield return one;
                }
            }
        }
    }

    /// <summary>The entry keys whose value is another .ao. From the format diagram; see AoSurvey.</summary>
    private static readonly string[] Hangs =
        ["ao", "fixed_ao", "attached_object", "attached_slaved_animation_object"];

    /// <summary>
    /// The mesh manifest, verbatim, and then the geometry's own headers.
    /// </summary>
    /// <remarks>
    /// THE .sm IS FOUND FROM THE .ao CHAIN, not from the model - which carries the .smd the
    /// manifest NAMED and not the manifest itself, so this section printed a binary file as text
    /// for as long as it existed. A dump whose own labels are wrong is worse than no dump.
    ///
    /// AND THE HEADERS BESIDE IT, because the manifest cannot say whether the reader walked the
    /// geometry correctly and <see cref="MeshFacts"/> can: what the file says the shape names
    /// weigh against what the reader found where it went looking is an invariant a wrong step
    /// cannot satisfy by accident.
    /// </remarks>
    private static void Manifest(
        Func<string, byte[]?> read, StringBuilder said, MonsterModel? model, IReadOnlyList<string> walked)
    {
        string manifest = string.Empty;
        foreach (string one in walked)
        {
            (string _, byte[]? content) = Find(read, one);
            if (content is { Length: > 0 } && Skin(AnimatedObject.Read(content)) is { Length: > 0 } named)
            {
                manifest = named;
                break;
            }
        }

        if (manifest.Length == 0)
        {
            said.AppendLine().AppendLine("=== .sm (none was named)");
        }
        else
        {
            said.AppendLine().Append("=== .sm ").AppendLine(manifest);
            byte[]? content = read(manifest.Replace('\\', '/').Trim());
            said.AppendLine(content is { Length: > 0 }
                ? StatDescriptionFiles.Decode(content).TrimEnd()
                : "(not in the install)");
        }

        string geometry = model?.Mesh_ ?? string.Empty;
        said.AppendLine().Append("=== .smd ").AppendLine(geometry.Length > 0 ? geometry : "(none was named)");
        if (geometry.Length > 0)
        {
            Facts(said, "  ", SkinnedMesh.Read(read(geometry.Replace('\\', '/').Trim())).Facts);
        }
    }

    /// <summary>A piece and everything it extends, nearest first - the same walk MonsterModel does.</summary>
    private static List<AnimatedObject> Whole(Func<string, byte[]?> read, AnimatedObject ao)
    {
        var chain = new List<AnimatedObject> { ao };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(ao.Extends);

        while (queue.Count > 0 && chain.Count <= MostHops)
        {
            string path = queue.Dequeue();
            if (path.Length == 0 || !seen.Add(path))
            {
                continue;
            }

            (string _, byte[]? over) = Find(read, path);
            if (over is not { Length: > 0 })
            {
                continue;
            }

            AnimatedObject said = AnimatedObject.Read(over);
            chain.Add(said);
            foreach (string up in said.Extends)
            {
                queue.Enqueue(up);
            }
        }

        return chain;
    }

    /// <summary>The skin a piece wears once what it inherits and what it drops are both counted.</summary>
    private static string Skin(IReadOnlyList<AnimatedObject> chain)
    {
        var gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<string>();

        foreach (AoStruct block in chain.SelectMany(one => one.Named("SkinMesh")))
        {
            foreach (AoEntry entry in block.Entries)
            {
                if (entry.Value.Length == 0)
                {
                    continue;
                }

                if (string.Equals(entry.Key, "remove_skin", StringComparison.Ordinal))
                {
                    gone.Add(entry.Value);
                }
                else if (string.Equals(entry.Key, "skin", StringComparison.Ordinal))
                {
                    kept.Add(entry.Value);
                }
            }
        }

        foreach (string one in kept)
        {
            if (!gone.Contains(one))
            {
                return one;
            }
        }

        return string.Empty;
    }

    /// <summary>One entry's value, taken from the nearest file in the chain that has it.</summary>
    private static string Entryed(IReadOnlyList<AnimatedObject> chain, string block, string key)
    {
        foreach (AnimatedObject one in chain)
        {
            if (Entryed(one, block, key) is { Length: > 0 } said)
            {
                return said;
            }
        }

        return string.Empty;
    }

    /// <summary>The <c>skin</c> a SkinMesh block names, or empty where the file has none.</summary>
    private static string Skin(AnimatedObject ao) => Entryed(ao, "SkinMesh", "skin");

    /// <summary>One entry's value out of one kind of block, or empty where the file has none.</summary>
    private static string Entryed(AnimatedObject ao, string block, string key)
    {
        foreach (AoStruct one in ao.Named(block))
        {
            foreach (AoEntry entry in one.Entries)
            {
                if (string.Equals(entry.Key, key, StringComparison.Ordinal) && entry.Value.Length > 0)
                {
                    return entry.Value;
                }
            }
        }

        return string.Empty;
    }

    /// <summary>What a mesh file's own headers said, and whether they agree with each other.</summary>
    private static void Facts(StringBuilder said, string indent, MeshFacts facts)
    {
        if (facts.Vertices == 0)
        {
            said.Append(indent).AppendLine("(the geometry did not read)");
            return;
        }

        said.Append(indent).Append("version ").Append(Say(facts.Version))
            .Append(" · c0h ").Append(Say(facts.Corner))
            .Append(" · ").Append(Say(facts.Details)).Append(" level(s) of detail")
            .Append(" · format 0x").Append(facts.Format.ToString("X", CultureInfo.InvariantCulture))
            .Append(" · stride ").Append(Say(facts.Stride)).AppendLine();

        said.Append(indent).Append("shapes ").Append(Say(facts.Shapes))
            .Append(" in the header, ").Append(Say(facts.BlockShapes)).Append(" in the block")
            .Append(" · ").Append(Say(facts.Triangles)).Append(" triangles over ")
            .Append(Say(facts.Vertices)).AppendLine(" vertices");

        said.Append(indent).Append("names: ").Append(Say(facts.NamesSaid))
            .Append(" bytes said, ").Append(Say(facts.NamesRead)).Append(" read")
            .AppendLine(facts.NamesSaid == facts.NamesRead ? "  [agree]" : "  [DISAGREE - a step is wrong]");
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
