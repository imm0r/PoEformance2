using PoEformance.Game.Files;

namespace PoEformance.Features;

/// <summary>
/// What each tile and room placed in the current area needs that the shade compiler has not got, by
/// path - the "not evaluated" line under the picture, for every tile at once, so a search can find
/// the one worth looking at in the game.
/// </summary>
/// <remarks>
/// WHY. Settling what the engine feeds <c>InputVertexColor</c> on a mesh without a colour stream
/// takes a mesh that wears such a material and can be seen in the game - and a map of seventy
/// tiles does not say which of them that is. Clicking through them one by one found a tile whose
/// BasicColour was a black fog blocker, which decides nothing. This reads every placed tile and
/// room the way the pane would, off the frame, and keeps each one's left-out list as text, so
/// <c>needs:InputVertexColor here:yes</c> lists the candidates with their materials named.
///
/// THE SAME LOADS AS THE PANE, through <see cref="TileModels.Of"/> and <see cref="RoomModels.Of"/>
/// with the graphs read, so the text is exactly the line the pane would print - a tile drawn as the
/// chosen tileset is read as that tileset where it places the tile. That is the whole cost of a
/// click on every tile, textures included, which is why it runs once per area and tileset and
/// never on the frame. Built on the first ask after the area's tiles are there, and again only when
/// the area or the tileset changes.
/// </remarks>
public sealed class AreaNeeds(Func<string, byte[]?>? read, TilesetCatalog? tilesets)
{
    private readonly Lock _gate = new();
    private Task<IReadOnlyDictionary<string, string>>? _building;
    private IReadOnlyDictionary<string, int>? _from;
    private string _tileset = string.Empty;
    private int _done;
    private int _of;

    /// <summary>The needs if they are read, starting the read if they are not; null until then.</summary>
    /// <param name="placed">The area's tiles and rooms, by path - a new dictionary whenever the area changes.</param>
    /// <param name="tileset">The tileset tiles are drawn as, or empty for their own materials.</param>
    public IReadOnlyDictionary<string, string>? Ready(IReadOnlyDictionary<string, int>? placed, string tileset)
        => Start(placed, tileset) is { IsCompletedSuccessfully: true } built ? built.Result : null;

    /// <summary>Whether a read is under way.</summary>
    public bool Reading => _building is { IsCompleted: false };

    /// <summary>How far the read has got: tiles done, of how many.</summary>
    public (int Done, int Of) Progress => (Volatile.Read(ref _done), _of);

    /// <summary>
    /// The needs of each path, read now - off the frame only.
    /// </summary>
    /// <param name="read">How to get a file out of the install.</param>
    /// <param name="paths">The tiles and rooms.</param>
    /// <param name="tileset">The tileset tiles are drawn as where it places them, or empty.</param>
    /// <param name="index">Which tilesets place which tile, or null to draw every tile as itself.</param>
    /// <param name="step">Called after each path, for a count.</param>
    public static IReadOnlyDictionary<string, string> Of(
        Func<string, byte[]?> read,
        IReadOnlyList<string> paths,
        string tileset,
        TilesetIndex? index,
        Action? step = null)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(paths);

        IReadOnlyDictionary<string, string>? swaps = tileset.Length > 0 && index is not null ? TilesetIndex.Overrides(read, tileset) : null;
        string shortName = tileset.Length > 0 ? TilesetIndex.Short(tileset) : string.Empty;
        var needs = new Dictionary<string, string>(paths.Count, StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            MonsterModel model;
            if (TileBook.IsRoom(path))
            {
                model = RoomModels.Of(read, path, shaded: true);
            }
            else
            {
                bool placing = swaps is not null && index!.Of(path).Any(one => string.Equals(one, tileset, StringComparison.OrdinalIgnoreCase));
                model = TileModels.Of(read, path, shaded: true, swaps: placing ? swaps : null, tileset: placing ? shortName : string.Empty);
            }

            // THE PANE'S OWN WORDS, so what the search finds is what the picture says. A tile that
            // did not load says so instead, which is also something to search for.
            needs[path] = model.Ready
                ? string.Join(" · ", model.Unshaded)
                : model.Why.Length > 0 ? "did not load: " + model.Why : string.Empty;
            step?.Invoke();
        }

        return needs;
    }

    private Task<IReadOnlyDictionary<string, string>>? Start(IReadOnlyDictionary<string, int>? placed, string tileset)
    {
        if (read is null || placed is not { Count: > 0 })
        {
            return null;
        }

        lock (_gate)
        {
            if (_building is null || !ReferenceEquals(_from, placed) || !string.Equals(_tileset, tileset, StringComparison.OrdinalIgnoreCase))
            {
                _from = placed;
                _tileset = tileset;
                _done = 0;
                _of = placed.Count;
                string[] paths = [.. placed.Keys];
                _building = Task.Run(() => Of(read, paths, tileset, tileset.Length > 0 ? tilesets?.Wait() : null, () => Interlocked.Increment(ref _done)));
            }

            return _building;
        }
    }
}
