using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PoEformance.Features;

/// <summary>One file as it should be in the repository.</summary>
/// <param name="Path">Where it goes, relative to the repository root, with forward slashes.</param>
/// <param name="Content">Its bytes.</param>
public sealed record UploadFile(string Path, byte[] Content);

/// <summary>What one press of the upload button came to.</summary>
/// <param name="Ok">Whether the branch now holds every file.</param>
/// <param name="Uploaded">How many files had to be sent; zero when the branch already had them all.</param>
/// <param name="Said">What happened, in a sentence, for the line under the button.</param>
public readonly record struct UploadOutcome(bool Ok, int Uploaded, string Said);

/// <summary>
/// Sends the model pane's exports to the repository, where a workflow bakes them into the sheet.
/// </summary>
/// <remarks>
/// WHY THE BAKE IS NOT DONE HERE, when the overlay already lays exports into its own sheet: the
/// sheet this build carries is the one it was BUILT with, and the repository's may have moved
/// on since - somebody else's bosses, a correction. Baking here and uploading the result would
/// write an old sheet over a newer one. So only the sources travel, and
/// .github/workflows/bake-boss-icons.yml bakes them against the repository as it is, with
/// tools/IconBaker, and opens the pull request.
///
/// THE SOURCES STAY IN THE REPOSITORY, under <see cref="Folder"/>. That is what lets this send
/// only what changed: a git blob's id is a hash of its content, computable here, so a file the
/// branch already has is recognised without downloading it and a second press sends nothing.
/// It also means a sheet can be re-baked from scratch at any time.
///
/// ONE COMMIT PER PRESS, through the git data API (blobs, a tree, a commit, the ref) rather
/// than one contents-API commit per file: a hundred bosses would otherwise be two hundred
/// commits and two hundred workflow runs.
/// </remarks>
public static class BossExportUpload
{
    /// <summary>The branch the exports go to, and the bake commits to.</summary>
    public const string Branch = "boss-icons";

    /// <summary>Where the exported cells and the entries live in the repository.</summary>
    public const string Folder = "assets/boss-exports";

    /// <summary>The entries file's name, in <see cref="Folder"/> as in data/.</summary>
    public const string EntriesName = "boss-icons.json";

    /// <summary>The branch everything is based on.</summary>
    public const string Main = "main";

    /// <summary>
    /// Every exported 64-pixel cell and the entries file, as they should be in the repository.
    /// </summary>
    /// <remarks>
    /// THE CELLS ONLY. The export writes a 1024-pixel version of each picture too, for looking
    /// at; the sheet is made of the 64-pixel one, and the big pair is half a megabyte each.
    /// A file that is not a 64-pixel PNG is left out here rather than discovered by the bake,
    /// where it would stop the run for everybody's bosses.
    ///
    /// The entries file is left out when it does not parse - merging it would stop the bake.
    /// </remarks>
    /// <param name="exports">The exports folder (MonsterPortrait.Folder).</param>
    /// <param name="entries">The boss-icons.json the tool writes, or empty.</param>
    /// <param name="skipped">Gets the name of every file left out, and why.</param>
    public static List<UploadFile> Gather(string exports, string entries, List<string> skipped)
    {
        ArgumentNullException.ThrowIfNull(exports);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(skipped);

        var files = new List<UploadFile>();
        if (Directory.Exists(exports))
        {
            foreach (string family in IconSheetGrowth.Families(Directory.EnumerateFiles(exports, "*.png")))
            {
                Cell(files, skipped, exports, BossIcons.Named(family, cleared: false));
                Cell(files, skipped, exports, BossIcons.Named(family, cleared: true));
            }
        }

        if (entries.Length > 0 && File.Exists(entries))
        {
            if (BossIcons.Load(entries).Unreadable)
            {
                skipped.Add($"{EntriesName}: could not be read");
            }
            else
            {
                files.Add(new UploadFile($"{Folder}/{EntriesName}", File.ReadAllBytes(entries)));
            }
        }

        return files;
    }

    /// <summary>The id git gives a file with this content: SHA-1 over "blob {length}\0" and the bytes.</summary>
    /// <remarks>
    /// SHA-1 because that is git's object id, not as a choice of hash for anything secret: it
    /// only has to match what GitHub reports for a file the branch already holds.
    /// </remarks>
    public static string BlobId(ReadOnlySpan<byte> content)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(Encoding.ASCII.GetBytes($"blob {content.Length}\0"));
        hash.AppendData(content);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>The files whose content the repository does not already hold at that path.</summary>
    /// <param name="files">What should be there.</param>
    /// <param name="held">What is there: path to blob id, from a listing of <see cref="Folder"/>.</param>
    public static List<UploadFile> Changed(IReadOnlyList<UploadFile> files, IReadOnlyDictionary<string, string> held)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(held);

        var changed = new List<UploadFile>();
        foreach (UploadFile file in files)
        {
            if (!held.TryGetValue(file.Path, out string? id)
                || !string.Equals(id, BlobId(file.Content), StringComparison.OrdinalIgnoreCase))
            {
                changed.Add(file);
            }
        }

        return changed;
    }

    /// <summary>Whether these bytes are a PNG of one sheet cell, read from its header.</summary>
    public static bool IsCell(ReadOnlySpan<byte> png)
    {
        ReadOnlySpan<byte> signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        return png.Length >= 24
            && png[..8].SequenceEqual(signature)
            && BinaryPrimitives.ReadInt32BigEndian(png[16..]) == IconSheet.Tile
            && BinaryPrimitives.ReadInt32BigEndian(png[20..]) == IconSheet.Tile;
    }

    private static void Cell(List<UploadFile> files, List<string> skipped, string exports, string name)
    {
        string path = Path.Combine(exports, name + ".png");
        if (!File.Exists(path))
        {
            // Only ever an Inactive half; the Active file is what made it a family.
            return;
        }

        byte[] content = File.ReadAllBytes(path);
        if (!IsCell(content))
        {
            skipped.Add($"{name}.png: not a {IconSheet.Tile}x{IconSheet.Tile} PNG");
            return;
        }

        files.Add(new UploadFile($"{Folder}/{name}.png", content));
    }
}

/// <summary>
/// The few GitHub calls an upload needs, with a token, and nothing else.
/// </summary>
/// <remarks>
/// JSON READ WITH JsonDocument AND WRITTEN WITH Utf8JsonWriter, as UpdateCheck reads releases:
/// a handful of fields from a large, versioned payload, with no reflection for Native AOT to
/// trip over and no DTO to keep in step with GitHub's.
///
/// The transport is handed in, so every branch of this - a missing branch, a branch that moved
/// under the commit, a token GitHub refuses - is tested without a network.
/// </remarks>
public sealed class GitHubWriter : IDisposable
{
    /// <summary>How many blobs are sent at once. Enough to overlap the round trips, polite to the API.</summary>
    private const int AtOnce = 4;

    private readonly string _token;
    private readonly string _repository;
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;
    private HttpClient? _http;

    /// <param name="token">A token that may write the repository's contents.</param>
    /// <param name="send">Sends one request. Defaults to an HttpClient made like UpdateCheck's.</param>
    /// <param name="owner">The repository's owner.</param>
    /// <param name="repository">The repository's name.</param>
    public GitHubWriter(
        string token,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? send = null,
        string owner = UpdateCheck.Owner,
        string repository = UpdateCheck.Repository)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        _token = token;
        _repository = $"{owner}/{repository}";
        _send = send ?? Send;
    }

    /// <summary>Where the bake's runs can be watched.</summary>
    public string Runs => $"https://github.com/{_repository}/actions/workflows/bake-boss-icons.yml";

    /// <summary>
    /// Puts the files on <see cref="BossExportUpload.Branch"/> in one commit, sending only what changed.
    /// </summary>
    /// <remarks>
    /// TWICE AT MOST. The bake rebuilds the branch from main and force-pushes it, so the tip
    /// this commit was made on can move between reading it and moving the ref; GitHub refuses
    /// that update as not a fast-forward, and the answer is to start again from the new tip.
    /// The second refusal is reported rather than tried a third time.
    /// </remarks>
    public async Task<UploadOutcome> UploadAsync(IReadOnlyList<UploadFile> files, CancellationToken cancelling = default)
    {
        ArgumentNullException.ThrowIfNull(files);

        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                (int found, string tip) = await RefOf(BossExportUpload.Branch, cancelling).ConfigureAwait(false);
                if (found != 200 && found != 404)
                {
                    return Refused(found);
                }

                bool creating = found == 404;
                string parent = tip;
                if (creating)
                {
                    (int main, parent) = await RefOf(BossExportUpload.Main, cancelling).ConfigureAwait(false);
                    if (main != 200)
                    {
                        return Refused(main);
                    }
                }

                (int listed, Dictionary<string, string> held) = await Held(parent, cancelling).ConfigureAwait(false);
                if (listed != 200 && listed != 404)
                {
                    return Refused(listed);
                }

                List<UploadFile> changed = BossExportUpload.Changed(files, held);
                if (changed.Count == 0)
                {
                    return new UploadOutcome(true, 0, $"nothing new - {BossExportUpload.Branch} already has all {files.Count} file(s)");
                }

                string commit = await Commit(parent, changed, cancelling).ConfigureAwait(false);
                if (commit.Length == 0)
                {
                    return new UploadOutcome(false, 0, "GitHub did not take the files - nothing was changed");
                }

                int moved = creating
                    ? (await Ask(HttpMethod.Post, "git/refs", Body(writer =>
                    {
                        writer.WriteString("ref", $"refs/heads/{BossExportUpload.Branch}");
                        writer.WriteString("sha", commit);
                    }), cancelling).ConfigureAwait(false)).Status
                    : (await Ask(HttpMethod.Patch, $"git/refs/heads/{BossExportUpload.Branch}", Body(writer =>
                    {
                        writer.WriteString("sha", commit);
                        writer.WriteBoolean("force", false);
                    }), cancelling).ConfigureAwait(false)).Status;

                if (moved is 200 or 201)
                {
                    return new UploadOutcome(
                        true,
                        changed.Count,
                        $"sent {changed.Count} file(s) to {BossExportUpload.Branch} - the bake runs on GitHub and opens the pull request");
                }

                if (moved != 422)
                {
                    return Refused(moved);
                }
            }

            return new UploadOutcome(false, 0, $"{BossExportUpload.Branch} kept moving while this was sent - press again");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new UploadOutcome(false, 0, $"could not reach GitHub: {exception.Message}");
        }
    }

    /// <summary>The blobs, the tree and the commit. The commit's id, or empty when GitHub refused.</summary>
    private async Task<string> Commit(string parent, List<UploadFile> changed, CancellationToken cancelling)
    {
        (int read, JsonElement commit) = await Ask(HttpMethod.Get, $"git/commits/{parent}", null, cancelling).ConfigureAwait(false);
        if (read != 200)
        {
            return string.Empty;
        }

        string baseTree = commit.GetProperty("tree").GetProperty("sha").GetString() ?? string.Empty;

        using var gate = new SemaphoreSlim(AtOnce);
        string[] blobs = await Task.WhenAll(changed.Select(async file =>
        {
            await gate.WaitAsync(cancelling).ConfigureAwait(false);
            try
            {
                (int made, JsonElement blob) = await Ask(HttpMethod.Post, "git/blobs", Body(writer =>
                {
                    writer.WriteString("content", Convert.ToBase64String(file.Content));
                    writer.WriteString("encoding", "base64");
                }), cancelling).ConfigureAwait(false);
                return made == 201 ? blob.GetProperty("sha").GetString() ?? string.Empty : string.Empty;
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        if (Array.Exists(blobs, blob => blob.Length == 0))
        {
            return string.Empty;
        }

        (int planted, JsonElement tree) = await Ask(HttpMethod.Post, "git/trees", Body(writer =>
        {
            writer.WriteString("base_tree", baseTree);
            writer.WriteStartArray("tree");
            for (int i = 0; i < changed.Count; i++)
            {
                writer.WriteStartObject();
                writer.WriteString("path", changed[i].Path);
                writer.WriteString("mode", "100644");
                writer.WriteString("type", "blob");
                writer.WriteString("sha", blobs[i]);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }), cancelling).ConfigureAwait(false);
        if (planted != 201)
        {
            return string.Empty;
        }

        string treeId = tree.GetProperty("sha").GetString() ?? string.Empty;
        (int made, JsonElement created) = await Ask(HttpMethod.Post, "git/commits", Body(writer =>
        {
            writer.WriteString("message", $"Boss exports: {changed.Count} file(s) from the overlay");
            writer.WriteString("tree", treeId);
            writer.WriteStartArray("parents");
            writer.WriteStringValue(parent);
            writer.WriteEndArray();
        }), cancelling).ConfigureAwait(false);

        return made == 201 ? created.GetProperty("sha").GetString() ?? string.Empty : string.Empty;
    }

    /// <summary>A branch's tip: 200 and its commit, or the status GitHub answered.</summary>
    private async Task<(int Status, string Sha)> RefOf(string branch, CancellationToken cancelling)
    {
        (int status, JsonElement found) = await Ask(HttpMethod.Get, $"git/ref/heads/{branch}", null, cancelling).ConfigureAwait(false);
        return status == 200
            ? (200, found.GetProperty("object").GetProperty("sha").GetString() ?? string.Empty)
            : (status, string.Empty);
    }

    /// <summary>What the exports folder holds at a commit, path to blob id. 404 when it does not exist yet.</summary>
    private async Task<(int Status, Dictionary<string, string> Held)> Held(string commit, CancellationToken cancelling)
    {
        var held = new Dictionary<string, string>(StringComparer.Ordinal);
        (int status, JsonElement listing) = await Ask(
            HttpMethod.Get, $"contents/{BossExportUpload.Folder}?ref={commit}", null, cancelling).ConfigureAwait(false);

        if (status == 200 && listing.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in listing.EnumerateArray())
            {
                if (item.GetProperty("type").GetString() == "file"
                    && item.GetProperty("path").GetString() is { } path
                    && item.GetProperty("sha").GetString() is { } sha)
                {
                    held[path] = sha;
                }
            }
        }

        return (status, held);
    }

    /// <summary>A refusal, said in terms of what to do about it.</summary>
    private UploadOutcome Refused(int status) => new(false, 0, status switch
    {
        401 => "GitHub refused the token - forget it and enter a new one",
        403 => $"the token may not write to {_repository} - it needs Contents: read and write",
        404 => $"{_repository} is not visible to this token",
        0 => "GitHub could not be reached",
        _ => $"GitHub answered {status} - nothing was changed",
    });

    /// <summary>One call: the status, and the JSON answer (default when there is none).</summary>
    private async Task<(int Status, JsonElement Answer)> Ask(
        HttpMethod method, string path, byte[]? body, CancellationToken cancelling)
    {
        using var request = new HttpRequestMessage(method, $"https://api.github.com/repos/{_repository}/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        using HttpResponseMessage answer = await _send(request, cancelling).ConfigureAwait(false);
        byte[] bytes = await answer.Content.ReadAsByteArrayAsync(cancelling).ConfigureAwait(false);
        if (bytes.Length == 0 || answer.StatusCode == HttpStatusCode.NoContent)
        {
            return ((int)answer.StatusCode, default);
        }

        using JsonDocument document = JsonDocument.Parse(bytes);
        return ((int)answer.StatusCode, document.RootElement.Clone());
    }

    /// <summary>A JSON object written by the callback, as bytes.</summary>
    private static byte[] Body(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken cancelling)
    {
        _http ??= UpdateCheck.Client();
        return _http.SendAsync(request, cancelling);
    }

    public void Dispose() => _http?.Dispose();
}
