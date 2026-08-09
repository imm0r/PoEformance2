using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace PoEformance.Features;

/// <summary>
/// Gets each item's own picture, and keeps it.
/// </summary>
/// <remarks>
/// An item carries the path of its 2D art - <c>Art/2DItems/Weapons/Bow.dds</c> - and not the
/// art itself. It is why a stash can be drawn as a stash rather than as a list.
///
/// TWO PLACES IT CAN COME FROM, and they are not equal.
///
/// THE INSTALL, which is where it actually is. The path names a file in the game's own bundles,
/// so a copy of the game is a complete set of the art: nothing leaves the machine, nothing can
/// be out of date, and it works offline. This is preferred whenever the install can be read,
/// and it needs no permission - reading a file the player already has is not somebody else's
/// business.
///
/// THE WEB, as the fallback, from poe2db - which is how the AHK tool does it, and what is left
/// when the install cannot be found or a texture will not decode. THAT one is OFF until
/// somebody turns it on: what is sent is an art path and nothing else, no account, no
/// character, no stash, but "this tool talks to a website" should be a decision rather than
/// something to discover.
///
/// FETCHED ONCE, EVER, either way. Each picture is written next to the settings and read from
/// there afterwards, so a stash of two thousand items costs its icons once and nothing on every
/// run after. A path that comes back missing is remembered as missing too - otherwise every
/// draw of a shipped-this-league item re-asks a server that has already said no.
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

    private Func<string, byte[]?>? _install;

    // Everything asked for so far: the path, and where it ended up. An empty value is a path
    // that came back with nothing, which is remembered so it is not asked for again.
    private readonly Dictionary<string, string> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _asking = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    private HttpClient? _http;
    private int _fetched;
    private int _missing;
    private int _unpacked;

    /// <param name="fetch">
    /// How to get one. Handed in so this can be tested without a network, and so the one place
    /// that talks to the outside world is visible from the constructor.
    /// </param>
    public ItemArtStore(string? folder = null, Func<string, CancellationToken, Task<byte[]?>>? fetch = null)
    {
        _folder = folder ?? DefaultFolder;
        _fetch = fetch ?? Download;
    }

    /// <summary>Whether to ask the WEB for pictures. Off until somebody says otherwise.</summary>
    /// <remarks>Reading them out of the install is not gated on this - see the class remarks.</remarks>
    public bool Enabled { get; set; }

    /// <summary>
    /// Where to get a picture out of the installed game, if it can be read.
    /// </summary>
    /// <remarks>
    /// Takes an art path and gives back an encoded image, or null when the install has no such
    /// texture. Handed in rather than opened here so the decoding - which is somebody else's
    /// format all the way down - stays where the other image handling is, and so all of this
    /// can be tested without a copy of the game.
    /// </remarks>
    public Func<string, byte[]?>? Install
    {
        get { lock (_gate) { return _install; } }
        set { lock (_gate) { _install = value; } }
    }

    /// <summary>Whether pictures can be had at all, from either place.</summary>
    public bool Working => Enabled || Install is not null;

    /// <summary>Where this session's pictures came from, and how many were nowhere.</summary>
    public (int Unpacked, int Fetched, int Missing) Tally
    {
        get
        {
            lock (_gate)
            {
                return (_unpacked, _fetched, _missing);
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

        // BOTH SPELLINGS go down. The key is the path with its extension off, which is how the
        // picture server spells it and how the cache is keyed; the install wants the path as the
        // item wrote it, extension and all, because that is what is in its index.
        Ask(key, file, artPath);
        return string.Empty;
    }

    /// <summary>Starts a look for one, unless one is already running for this path.</summary>
    /// <remarks>
    /// ON A BACKGROUND THREAD even for the install, which is a local file and might look like
    /// something to just do. Unpacking one is a bundle chunk decompressed and a block-compressed
    /// texture decoded, which is small but not free - and a stash opening asks for hundreds at
    /// once, on the thread that is drawing them.
    /// </remarks>
    private void Ask(string key, string file, string artPath)
    {
        if (!Working)
        {
            return;
        }

        lock (_gate)
        {
            if (_unpacked + _fetched + _missing >= MostPerRun || !_asking.Add(key))
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
                    // The install first: it is the real source, it is not somebody else's
                    // server, and it cannot be out of date.
                    Func<string, byte[]?>? install = Install;
                    byte[]? bytes = install?.Invoke(artPath);
                    bool local = bytes is { Length: > 0 };

                    if (!local && Enabled)
                    {
                        bytes = await _fetch(key, _closing.Token).ConfigureAwait(false);
                    }

                    if (bytes is { Length: > 0 })
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                        await Published(file, bytes).ConfigureAwait(false);

                        lock (_gate)
                        {
                            _known[key] = file;
                            if (local)
                            {
                                _unpacked++;
                            }
                            else
                            {
                                _fetched++;
                            }
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

    /// <summary>What a half-written picture is called while it is being written.</summary>
    public const string WhileWriting = ".part";

    /// <summary>
    /// Puts a finished picture where the renderer will look, in one step.
    /// </summary>
    /// <remarks>
    /// WRITTEN BESIDE ITS NAME AND RENAMED INTO IT, never straight to the name itself, and the
    /// reason is who the reader is: the thread that DRAWS, asking for the file the instant it
    /// exists. <see cref="File.Exists"/> is true from the moment a write begins, so writing in
    /// place hands the renderer either a file still open for writing - a sharing violation,
    /// which Windows words as "used by another process" - or half a picture, which is not a
    /// picture at all.
    ///
    /// Neither is a passing annoyance, because of what the renderer does with the answer: an
    /// icon that cannot be read is remembered as one that never will be, so the picture is
    /// gone for the rest of the session. It hits exactly the icons that were being fetched
    /// while the stash was first drawn, which is all of them on a first run.
    ///
    /// A rename is atomic: the name either does not exist or names a finished, closed file.
    /// </remarks>
    private static async Task Published(string file, byte[] bytes)
    {
        string writing = file + WhileWriting;

        try
        {
            await File.WriteAllBytesAsync(writing, bytes).ConfigureAwait(false);
            File.Move(writing, file, overwrite: true);
        }
        catch
        {
            // A part-file left behind would be picked up by nothing - it is not the name
            // anything looks for - but it would sit there until somebody wondered what it was.
            try
            {
                File.Delete(writing);
            }
            catch (Exception sweeping) when (sweeping is IOException or UnauthorizedAccessException)
            {
                // Tidying is not worth an exception of its own.
            }

            throw;
        }
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
    ///
    /// And named for no PARTICULAR kind of picture, because there are now two sources and they
    /// do not agree on one - the install's textures are unpacked to one format and poe2db
    /// serves another. What reads these works the kind out from the content, so calling them
    /// all one thing would only be a file extension that lies.
    /// </remarks>
    public string FileFor(string key)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Path.Combine(_folder, $"{Convert.ToHexString(digest)[..24]}.img");
    }

    public void Dispose()
    {
        _closing.Cancel();
        _http?.Dispose();
        _limit.Dispose();
        _closing.Dispose();
    }
}
