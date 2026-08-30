using System.IO.Compression;
using System.Net.Http;

namespace PoEformance.Features;

/// <summary>How far an install has got.</summary>
public enum UpdateStep
{
    /// <summary>Nothing has been started.</summary>
    Idle,

    /// <summary>The zip is coming down.</summary>
    Downloading,

    /// <summary>The zip is being unpacked into the staging folder.</summary>
    Extracting,

    /// <summary>Unpacked and checked - all that is left is to swap the folders and restart.</summary>
    Ready,

    /// <summary>Something went wrong. Nothing outside the update folder was touched.</summary>
    Failed,
}

/// <summary>
/// Everything the restart needs, once a build is unpacked and checked.
/// </summary>
/// <param name="Staging">The unpacked new build.</param>
/// <param name="Install">The folder it replaces - where this executable is running from.</param>
/// <param name="Executable">The full path of the executable to start again afterwards.</param>
/// <param name="Version">The commit being installed, which the restarted build reports.</param>
public sealed record UpdatePlan(string Staging, string Install, string Executable, string Version);

/// <summary>
/// Fetches a release, unpacks it beside the tool, and checks it before anything is replaced.
/// </summary>
/// <remarks>
/// NOTHING IS OVERWRITTEN BY THIS CLASS. It downloads into <c>update/</c> and unpacks into
/// <c>update/staging/</c>, and the swap is a separate step that happens after the process has
/// exited - because it cannot happen before. The executable is running, and on Windows a
/// running image is locked; so are the native libraries beside it (cimgui, WebView2Loader,
/// oo2core), which the process has loaded. A copy attempted from inside the running tool fails
/// halfway and leaves an installation that is half one build and half another.
///
/// That staging folder is also the integrity check, and the reason this needs no checksum of
/// its own. A truncated or corrupted download fails at <see cref="ZipFile.ExtractToDirectory"/>
/// - every entry in a zip carries a CRC32 and the extractor verifies it - and it fails while
/// unpacking into a scratch folder, where a failure costs a delete. The transport is HTTPS to
/// GitHub, so what a hash would add on top is protection against GitHub serving a wrong file,
/// which is not the threat a tool like this can meaningfully check for anyway.
/// </remarks>
public sealed class UpdateInstaller : IDisposable
{
    /// <summary>Everything this class writes lives under here.</summary>
    public static string DefaultFolder => Path.Combine(AppContext.BaseDirectory, "update");

    /// <summary>How long the download is given before it counts as lost.</summary>
    /// <remarks>
    /// Minutes rather than seconds: the asset is a self-contained runtime and runs to well
    /// over a hundred megabytes, and the check's twenty-second patience would fail every
    /// download on an ordinary connection.
    /// </remarks>
    public static readonly TimeSpan Patience = TimeSpan.FromMinutes(20);

    private readonly Func<string, CancellationToken, Task<Stream?>> _open;
    private readonly string _folder;
    private readonly string _install;
    private readonly string _executable;
    private readonly CancellationTokenSource _closing = new();

    private HttpClient? _http;

    /// <param name="folder">Where to download and unpack. Defaults to <c>update/</c> beside the exe.</param>
    /// <param name="install">The folder a finished update replaces. Defaults to this build's own.</param>
    /// <param name="executable">
    /// What to start again after the swap. <see cref="Environment.ProcessPath"/> rather than the
    /// assembly's location, which is empty under the single-file publish the release ships.
    /// </param>
    /// <param name="open">Opens one address for reading, or null when it could not.</param>
    public UpdateInstaller(
        string? folder = null,
        string? install = null,
        string? executable = null,
        Func<string, CancellationToken, Task<Stream?>>? open = null)
    {
        _folder = folder ?? DefaultFolder;
        _install = install ?? AppContext.BaseDirectory;
        _executable = executable
            ?? Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "PoEformance.App.exe");
        _open = open ?? Download;
    }

    /// <summary>The executable a finished update starts again.</summary>
    public string Executable => _executable;

    /// <summary>Where the zip is downloaded to.</summary>
    public string ArchivePath => Path.Combine(_folder, UpdateCheck.AssetName);

    /// <summary>Where the zip is unpacked to.</summary>
    public string StagingPath => Path.Combine(_folder, "staging");

    /// <summary>Where the swap script writes what it did.</summary>
    public string LogPath => Path.Combine(_folder, "apply.log");

    /// <summary>Where the swap script itself is written.</summary>
    public string ScriptPath => Path.Combine(_folder, "apply.cmd");

    /// <summary>How far this has got.</summary>
    public UpdateStep Step { get; private set; } = UpdateStep.Idle;

    /// <summary>What is happening, or what went wrong, in words.</summary>
    public string Status { get; private set; } = string.Empty;

    /// <summary>Bytes downloaded so far.</summary>
    public long Received { get; private set; }

    /// <summary>Bytes expected, from the release. Zero when the release did not say.</summary>
    public long Total { get; private set; }

    /// <summary>How far along the download is, 0 to 1. Zero when the size is unknown.</summary>
    public double Fraction => Total > 0 ? Math.Clamp(Received / (double)Total, 0, 1) : 0;

    /// <summary>What the restart needs, once <see cref="Step"/> is <see cref="UpdateStep.Ready"/>.</summary>
    public UpdatePlan? Plan { get; private set; }

    /// <summary>Starts fetching a release in the background.</summary>
    public void Begin(ReleaseInfo release, BuildStamp remote)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(remote);

        if (Step is UpdateStep.Downloading or UpdateStep.Extracting)
        {
            return;
        }

        _ = Task.Run(() => RunAsync(release, remote, _closing.Token));
    }

    /// <summary>
    /// Fetches, unpacks and checks one release. The whole of the install that can be done
    /// while the tool is running.
    /// </summary>
    /// <returns>True when there is a plan to apply.</returns>
    public async Task<bool> RunAsync(ReleaseInfo release, BuildStamp remote, CancellationToken cancelling = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(remote);

        Plan = null;
        Received = 0;
        Total = release.DownloadSize;

        try
        {
            Clean();
            Directory.CreateDirectory(_folder);

            Step = UpdateStep.Downloading;
            Status = "downloading";

            Stream? source = await _open(release.DownloadUrl, cancelling).ConfigureAwait(false);
            if (source is null)
            {
                return Stop($"the download did not start - {release.DownloadUrl} did not answer");
            }

            await using (source)
            await using (FileStream file = File.Create(ArchivePath))
            {
                byte[] buffer = new byte[256 * 1024];
                int read;
                while ((read = await source.ReadAsync(buffer, cancelling).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancelling).ConfigureAwait(false);
                    Received += read;
                }
            }

            // A SHORT FILE IS A FAILED DOWNLOAD, not a small build. Without this the next step
            // is an extractor being handed a truncated archive, which reports a zip problem -
            // and "the release is corrupt" is a very different thing to go looking for than
            // "the connection dropped".
            if (Total > 0 && Received != Total)
            {
                return Stop($"the download stopped at {Received:N0} of {Total:N0} bytes");
            }

            Step = UpdateStep.Extracting;
            Status = "unpacking";

            // Into an EMPTY folder. Overwriting in place would leave anything the new build
            // dropped - a renamed dll, a data file that moved - sitting in the staging copy and
            // then copied back over the installation.
            Directory.CreateDirectory(StagingPath);
            ZipFile.ExtractToDirectory(ArchivePath, StagingPath, overwriteFiles: true);

            string exe = Path.Combine(StagingPath, Path.GetFileName(_executable));
            if (!File.Exists(exe))
            {
                return Stop(
                    $"the release unpacked without {Path.GetFileName(_executable)} in it - "
                    + "nothing was replaced");
            }

            Plan = new UpdatePlan(
                StagingPath,
                _install,
                _executable,
                remote.Known ? remote.Commit : release.Tag);

            Step = UpdateStep.Ready;
            Status = $"ready to install {(remote.Known ? remote.ShortCommit : release.Tag)}"
                + " - the tool restarts into it";
            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return Stop($"the download did not finish: {exception.Message}");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return Stop($"the update could not be unpacked: {exception.Message}");
        }
    }

    /// <summary>Throws away whatever a previous attempt left behind.</summary>
    /// <remarks>
    /// Best effort on purpose. A staging folder that cannot be deleted must not stop the next
    /// attempt from starting - the extract overwrites into it either way, and a leftover file
    /// is worth far less than a working update path.
    /// </remarks>
    public void Clean()
    {
        try
        {
            if (Directory.Exists(StagingPath))
            {
                Directory.Delete(StagingPath, recursive: true);
            }

            if (File.Exists(ArchivePath))
            {
                File.Delete(ArchivePath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Nothing here is worth failing an update over; see the remarks.
        }
    }

    private bool Stop(string why)
    {
        Step = UpdateStep.Failed;
        Status = why;
        Plan = null;
        return false;
    }

    /// <summary>The one place that fetches the archive.</summary>
    /// <remarks>
    /// <c>ResponseHeadersRead</c> is what makes the progress real: the default buffers the
    /// whole response before handing anything back, so a hundred-and-fifty-megabyte download
    /// would sit at zero percent and then jump to done.
    /// </remarks>
    private async Task<Stream?> Download(string address, CancellationToken cancelling)
    {
        _http ??= UpdateCheck.Client();
        _http.Timeout = Patience;

        HttpResponseMessage answer = await _http
            .GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancelling)
            .ConfigureAwait(false);

        if (!answer.IsSuccessStatusCode)
        {
            answer.Dispose();
            return null;
        }

        // The release did not always say how big the asset is; the response does.
        if (Total == 0 && answer.Content.Headers.ContentLength is long length)
        {
            Total = length;
        }

        // The response itself is left to the collector rather than disposed here: the caller
        // disposes the stream, and disposing the content stream is what returns the connection
        // to the pool. Disposing the response first would close the stream being returned.
        return await answer.Content.ReadAsStreamAsync(cancelling).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _closing.Cancel();
        _closing.Dispose();
        _http?.Dispose();
    }
}
