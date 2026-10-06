using PoEformance.Game.Files;

namespace PoEformance.Features;

/// <summary>
/// Which tiles of the whole install wear a material whose shader graphs read the game's clock.
/// </summary>
/// <remarks>
/// WHAT <c>clock:yes</c> SEARCHES BEYOND THE CURRENT AREA. The area's own reading (see
/// <see cref="AreaNeeds"/>) loads each placed tile whole - meshes, materials, textures - which is a
/// click's cost per tile and too much for the install's twenty-odd thousand. Whether a tile runs with
/// the clock needs none of the geometry: only its definition, its templates' material lists and each
/// material's graphs compiled - the compile the graph survey runs over every material, without a
/// texture decoded. Each template and each material is looked at once however many tiles share it.
///
/// TILES ONLY. A room's materials sit behind each doodad's .ao and mesh file, the whole model walk,
/// so rooms are answered by the area's reading where they are placed, and not here.
///
/// THE TILE'S OWN MATERIALS, as its files name them - not a tileset's swaps, which depend on where
/// the tile is placed. The area's reading, which draws a tile as the chosen tileset, covers those.
/// </remarks>
public static class TileClocks
{
    /// <summary>
    /// The tiles, of those given, whose materials' graphs read the clock - off the frame only.
    /// </summary>
    /// <param name="read">How to get a file out of the install.</param>
    /// <param name="tiles">The install's tiles and rooms; rooms are passed over.</param>
    /// <param name="step">Called after each path, for a count.</param>
    public static IReadOnlySet<string> Of(Func<string, byte[]?> read, IReadOnlyList<string> tiles, Action? step = null)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(tiles);

        var paints = new MonsterModels.Paints();
        var byTemplate = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var byMaterial = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var clocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string tile in tiles)
        {
            if (tile.Length > 0 && !TileBook.IsRoom(tile))
            {
                (TileDefinition definition, _) = TileModels.Defined(read, tile);
                if (definition.Ready && definition.Templates.Any(Template))
                {
                    clocked.Add(tile);
                }
            }

            step?.Invoke();
        }

        return clocked;

        bool Template(string path)
        {
            string at = path.Replace('\\', '/').Trim();
            if (!byTemplate.TryGetValue(at, out bool runs))
            {
                TileTemplate layout = TileTemplate.Read(read(at));
                runs = layout.Ready && layout.Materials.Any(Material);
                byTemplate[at] = runs;
            }

            return runs;
        }

        // A PROGRAM THAT IS A PLAIN TEXTURE reads no clock; one that compiled and reads Time is what
        // the picture runs - see ShadeProgram.UsesTime.
        bool Material(string material)
        {
            string file = MaterialFile.Bare(material);
            if (file.Length == 0)
            {
                return false;
            }

            if (!byMaterial.TryGetValue(file, out bool runs))
            {
                runs = paints.Compiled(read, file).Compiled.Program is { UsesTime: true, Plain: < 0 };
                byMaterial[file] = runs;
            }

            return runs;
        }
    }
}

/// <summary>
/// <see cref="TileClocks"/> built once in the background, on the first ask after the install's list is there.
/// </summary>
public sealed class TileClockCatalog(Func<string, byte[]?>? read, Func<IReadOnlyList<string>> tiles)
{
    private readonly Lock _gate = new();
    private Task<IReadOnlySet<string>>? _building;
    private IReadOnlyList<string>? _from;
    private int _done;
    private int _of;

    /// <summary>The tiles that run with the clock if they are read, starting the read if not; null until then.</summary>
    public IReadOnlySet<string>? Ready => Start() is { IsCompletedSuccessfully: true } built ? built.Result : null;

    /// <summary>Whether the read is under way.</summary>
    public bool Reading => _building is { IsCompleted: false };

    /// <summary>How far the read has got: paths done, of how many.</summary>
    public (int Done, int Of) Progress => (Volatile.Read(ref _done), _of);

    private Task<IReadOnlySet<string>>? Start()
    {
        IReadOnlyList<string> list = tiles();
        if (read is null || list.Count == 0)
        {
            return null;
        }

        lock (_gate)
        {
            if (_building is null || !ReferenceEquals(_from, list))
            {
                _from = list;
                _done = 0;
                _of = list.Count;
                _building = Task.Run(() => TileClocks.Of(read, list, () => Interlocked.Increment(ref _done)));
            }

            return _building;
        }
    }
}
