using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace PoEformance.Features;

/// <summary>
/// Gets each item's own picture, and keeps it.
/// </summary>
/// <remarks>
/// An item carries the path of its 2D art - <c>Art/2DItems/Weapons/Bow.dds</c> - and not the
/// art itself: that lives inside the game's packed archive, which this tool does not open. The
/// same paths are served as images by poe2db, so the path becomes a URL. That is how the AHK
/// tool does it, and it is why a stash can be drawn as a stash rather than as a list.
///
/// IT GOES OUT TO THE NETWORK, which nothing else in this tool does while playing, so it is
/// OFF until somebody turns it on. What is sent is the art path of an item and nothing else -
/// no account, no character, no stash - but "this tool talks to a website" is a thing somebody
/// should decide rather than discover.
///
/// FETCHED ONCE, EVER. Each picture is written next to the settings and read from there
/// afterwards, so a stash of two thousand items costs its icons once and nothing on every run
/// after. A path that comes back missing is remembered as missing too - otherwise every draw
/// of a shipped-this-league item re-asks a server that has already said no.
/// </remarks>
public sealed class ItemArtStore : IDisposable
{
    /// <summary>Where the pictures are kept.</summary>
    public static string DefaultFolder { get; } = Path.Combine(AppContext.BaseDirectory, "config", "item-art");

    /// <summary>How many are fetched at once, so a big stash cannot open two thousand sockets.</summary>
    public const int AtOnce = 4;

    /// <summary>And how many a single read is allowed to ask for, so the first look is not the slowest.</summary>
    public const int MostPerRun = 400;

    private readonly string _folder;
    private readonly Func<string, CancellationToken, Task<byte[]?>> _fetch;
    private readonly SemaphoreSlim _limit = new(AtOnce, AtOnce);
    private readonly CancellationTokenSource _closing = new();

    // Everything asked for so far: the path, and where it ended up. An empty value is a path
    // that came back with nothing, which is remembered so it is not asked for again.
    private readonly Dictionary<string, string> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _asking = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    private HttpClient? _http;
    private int _fetched;
    private int _missing;

    /// <param name="fetch">
    /// How to get one. Handed in so this can be tested without a network, and so the one place
    /// that talks to the outside world is visible from the constructor.
    /// </param>
    public ItemArtStore(string? folder = null, Func<string, CancellationToken, Task<byte[]?>>? fetch = null)
    {
        _folder = folder ?? DefaultFolder;
        _fetch = fetch ?? Download;
    }

    /// <summary>Whether to fetch anything at all. Off until somebody says otherwise.</summary>
    public bool Enabled { get; set; }

    /// <summary>How many pictures have been fetched this session, and how many came back missing.</summary>
    public (int Fetched, int Missing) Tally
    {
        get
        {
            lock (_gate)
            {
                return (_fetched, _missing);
            }
        }
    }

    /// <summary>How many are still being fetched.</summary>
    public int Pending
    {
        get
        {
            lock (_gate)
            {
                return _asking.Count;
            }
        }
    }

    /// <summary>
    /// The picture for an art path, or an empty string when there is not one yet.
    /// </summary>
    /// <remarks>
    /// NEVER WAITS. It is called while drawing a frame, so it answers with what is on disk and
    /// starts a fetch for what is not - the item shows its name this frame and its picture a
    /// moment later. Blocking here would stall the whole overlay behind somebody's connection.
    /// </remarks>
    public string Local(string? artPath)
    {
        if (string.IsNullOrWhiteSpace(artPath))
        {
            return string.Empty;
        }

        string key = Normalise(artPath);

        lock (_gate)
        {
            if (_known.TryGetValue(key, out string? already))
            {
                return already;
            }
        }

        string file = FileFor(key);
        if (File.Exists(file))
        {
            lock (_gate)
            {
                _known[key] = file;
            }

            return file;
        }

        Ask(key, file);
        return string.Empty;
    }

    /// <summary>Starts a fetch, unless one is already running for this path.</summary>
    private void Ask(string key, string file)
    {
        if (!Enabled)
        {
            return;
        }

        lock (_gate)
        {
            if (_fetched + _missing >= MostPerRun || !_asking.Add(key))
            {
                return;
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _limit.WaitAsync(_closing.Token).ConfigureAwait(false);
                try
                {
                    byte[]? bytes = await _fetch(key, _closing.Token).ConfigureAwait(false);
                    if (bytes is { Length: > 0 })
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                        await File.WriteAllBytesAsync(file, bytes, _closing.Token).ConfigureAwait(false);

                        lock (_gate)
                        {
                            _known[key] = file;
                            _fetched++;
                        }

                        return;
                    }

                    // Remembered as missing. Otherwise every draw re-asks a server that has
                    // already said no, for as long as the window is open.
                    lock (_gate)
                    {
                        _known[key] = string.Empty;
                        _missing++;
                    }
                }
                finally
                {
                    _limit.Release();
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException
                                                  or TaskCanceledException or OperationCanceledException
                                                  or UnauthorizedAccessException)
            {
                // A picture is not worth a crash, and the next look will try again.
            }
            finally
            {
                lock (_gate)
                {
                    _asking.Remove(key);
                }
            }
        });
    }

    /// <summary>The one place that talks to the outside world.</summary>
    /// <remarks>
    /// poe2db serves the game's own art paths as images. Only the path goes out - it says which
    /// KIND of item somebody is looking at and nothing about who they are.
    /// </remarks>
    private async Task<byte[]?> Download(string key, CancellationToken cancelling)
    {
        _http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        using HttpResponseMessage answer = await _http
            .GetAsync($"https://cdn.poe2db.tw/image/{key}.webp", cancelling)
            .ConfigureAwait(false);

        return answer.IsSuccessStatusCode
            ? await answer.Content.ReadAsByteArrayAsync(cancelling).ConfigureAwait(false)
            : null;
    }

    /// <summary>The art path as the picture server spells it.</summary>
    /// <remarks>
    /// Backslashes to forward ones and the file extension off: the game writes the path the way
    /// its own archive spells it, and the server serves the same path as an image.
    /// </remarks>
    public static string Normalise(string artPath)
    {
        string path = artPath.Replace('\\', '/').Trim();
        return path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ? path[..^4] : path;
    }

    /// <summary>
    /// Where one picture is kept.
    /// </summary>
    /// <remarks>
    /// Named by a HASH of the path rather than the path itself. The paths are deep, contain
    /// characters a file name may not, and run past the length Windows accepts - all of which
    /// turn into a picture that silently never caches.
    /// </remarks>
    public string FileFor(string key)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Path.Combine(_folder, $"{Convert.ToHexString(digest)[..24]}.webp");
    }

    public void Dispose()
    {
        _closing.Cancel();
        _http?.Dispose();
        _limit.Dispose();
        _closing.Dispose();
    }
}
