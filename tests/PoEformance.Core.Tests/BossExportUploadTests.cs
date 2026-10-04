using System.Net;
using System.Text;
using PoEformance.Features;

namespace PoEformance.Core.Tests;

/// <summary>
/// The overlay's "send exports to be baked": what is gathered, what counts as already there,
/// the calls made to GitHub, and the token kept for them.
/// </summary>
/// <remarks>
/// The mistakes pinned here are the quiet ones: a blob id computed differently from git's,
/// which would send every file on every press; a 1024-pixel picture or a broken entries file
/// sent to a bake it would stop; a branch that moved under the commit taken as a failure, or a
/// failure taken as success.
/// </remarks>
public sealed class BossExportUploadTests
{
    /// <summary>A PNG header claiming a size, which is all <see cref="BossExportUpload.IsCell"/> reads.</summary>
    private static byte[] Png(int width, int height)
    {
        byte[] png = new byte[33];
        byte[] signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(png, 0);
        png[16] = (byte)(width >> 24);
        png[17] = (byte)(width >> 16);
        png[18] = (byte)(width >> 8);
        png[19] = (byte)width;
        png[20] = (byte)(height >> 24);
        png[21] = (byte)(height >> 16);
        png[22] = (byte)(height >> 8);
        png[23] = (byte)height;
        return png;
    }

    /// <summary>The ids git itself gives these contents (git hash-object).</summary>
    [Theory]
    [InlineData("", "e69de29bb2d1d6434b8b29ae775ad8c2e48c5391")]
    [InlineData("hello\n", "ce013625030ba8dba906f756967f9e9ca394464a")]
    public void ABlobIdIsTheOneGitGives(string content, string id)
    {
        Assert.Equal(id, BossExportUpload.BlobId(Encoding.UTF8.GetBytes(content)));
    }

    [Fact]
    public void OnlyWhatTheBranchDoesNotHoldIsSent()
    {
        var same = new UploadFile("assets/boss-exports/AActive.png", [1, 2, 3]);
        var changed = new UploadFile("assets/boss-exports/BActive.png", [4, 5, 6]);
        var fresh = new UploadFile("assets/boss-exports/CActive.png", [7]);
        var held = new Dictionary<string, string>
        {
            [same.Path] = BossExportUpload.BlobId(same.Content),
            [changed.Path] = BossExportUpload.BlobId([9, 9]),
        };

        Assert.Equal([changed, fresh], BossExportUpload.Changed([same, changed, fresh], held));
    }

    [Fact]
    public void OnlyCellSizedPngsAreCells()
    {
        Assert.True(BossExportUpload.IsCell(Png(64, 64)));
        Assert.False(BossExportUpload.IsCell(Png(1024, 1024)));
        Assert.False(BossExportUpload.IsCell(Png(64, 32)));
        Assert.False(BossExportUpload.IsCell(new byte[40]));
    }

    /// <summary>
    /// The 64-pixel pairs and the entries are gathered; the big pictures, wrong sizes and an
    /// entries file that does not parse are not.
    /// </summary>
    [Fact]
    public void TheCellsAndTheEntriesAreGatheredAndNothingElse()
    {
        string root = Path.Combine(Path.GetTempPath(), $"boss-export-{Guid.NewGuid():N}");
        string exports = Path.Combine(root, "exports");
        Directory.CreateDirectory(exports);
        try
        {
            File.WriteAllBytes(Path.Combine(exports, "SaphiraActive.png"), Png(64, 64));
            File.WriteAllBytes(Path.Combine(exports, "SaphiraInactive.png"), Png(64, 64));
            File.WriteAllBytes(Path.Combine(exports, "SaphiraActive-1024.png"), Png(1024, 1024));
            File.WriteAllBytes(Path.Combine(exports, "LoneActive.png"), Png(64, 64));
            File.WriteAllBytes(Path.Combine(exports, "WrongActive.png"), Png(128, 128));
            File.WriteAllText(Path.Combine(exports, "Saphira.files.txt"), "dump");

            string entries = Path.Combine(root, "boss-icons.json");
            File.WriteAllText(entries, """{ "areas": { "MapGrimhaven": "Saphira" }, "tiles": {} }""");

            var skipped = new List<string>();
            List<UploadFile> files = BossExportUpload.Gather(exports, entries, skipped);

            Assert.Equal(
            [
                "assets/boss-exports/LoneActive.png",
                "assets/boss-exports/SaphiraActive.png",
                "assets/boss-exports/SaphiraInactive.png",
                "assets/boss-exports/boss-icons.json",
            ],
            files.Select(f => f.Path));
            Assert.Equal(["WrongActive.png: not a 64x64 PNG"], skipped);

            File.WriteAllText(entries, """{ "areas": { "MapGrimhaven": """);
            skipped.Clear();
            files = BossExportUpload.Gather(exports, entries, skipped);
            Assert.DoesNotContain(files, f => f.Path.EndsWith("boss-icons.json", StringComparison.Ordinal));
            Assert.Contains("boss-icons.json: could not be read", skipped);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A stand-in GitHub: answers by method and path, and remembers what it was asked.</summary>
    private sealed class FakeGitHub
    {
        public List<(string Method, string Path, string Body)> Calls { get; } = [];

        public Dictionary<string, string> Held { get; } = [];

        public bool BranchExists { get; set; }

        /// <summary>How many times the ref update is refused as not a fast-forward before it is taken.</summary>
        public int Moves { get; set; }

        public HttpStatusCode Everything { get; set; } = HttpStatusCode.OK;

        public async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken cancelling)
        {
            string path = request.RequestUri!.PathAndQuery.Replace("/repos/imm0r/PoEformance2/", string.Empty, StringComparison.Ordinal);
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancelling);
            Calls.Add((request.Method.Method, path, body));
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);

            if (Everything != HttpStatusCode.OK)
            {
                return Answer(Everything, """{ "message": "no" }""");
            }

            return (request.Method.Method, path) switch
            {
                ("GET", "git/ref/heads/boss-icons") => BranchExists
                    ? Answer(HttpStatusCode.OK, """{ "object": { "sha": "branchtip" } }""")
                    : Answer(HttpStatusCode.NotFound, """{ "message": "Not Found" }"""),
                ("GET", "git/ref/heads/main") => Answer(HttpStatusCode.OK, """{ "object": { "sha": "maintip" } }"""),
                ("GET", var listing) when listing.StartsWith("contents/assets/boss-exports", StringComparison.Ordinal) => Held.Count == 0
                    ? Answer(HttpStatusCode.NotFound, """{ "message": "Not Found" }""")
                    : Answer(HttpStatusCode.OK, "[" + string.Join(',', Held.Select(pair =>
                        $$"""{ "type": "file", "path": "{{pair.Key}}", "sha": "{{pair.Value}}" }""")) + "]"),
                ("GET", var commit) when commit.StartsWith("git/commits/", StringComparison.Ordinal) =>
                    Answer(HttpStatusCode.OK, """{ "tree": { "sha": "basetree" } }"""),
                ("POST", "git/blobs") => Answer(HttpStatusCode.Created, $$"""{ "sha": "blob{{Calls.Count}}" }"""),
                ("POST", "git/trees") => Answer(HttpStatusCode.Created, """{ "sha": "newtree" }"""),
                ("POST", "git/commits") => Answer(HttpStatusCode.Created, """{ "sha": "newcommit" }"""),
                ("POST", "git/refs") => Answer(HttpStatusCode.Created, "{}"),
                ("PATCH", "git/refs/heads/boss-icons") => Moves-- > 0
                    ? Answer(HttpStatusCode.UnprocessableEntity, """{ "message": "Update is not a fast forward" }""")
                    : Answer(HttpStatusCode.OK, "{}"),
                _ => Answer(HttpStatusCode.NotImplemented, "{}"),
            };
        }

        private static HttpResponseMessage Answer(HttpStatusCode status, string json)
            => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private static readonly UploadFile Cell = new("assets/boss-exports/SaphiraActive.png", [1, 2, 3]);
    private static readonly UploadFile Entries = new("assets/boss-exports/boss-icons.json", "{}"u8.ToArray());

    /// <summary>The first press: the branch is made from main, with every file in one commit.</summary>
    [Fact]
    public async Task TheFirstPressMakesTheBranchFromMain()
    {
        var github = new FakeGitHub();
        using var writer = new GitHubWriter("token", github.Send);

        UploadOutcome outcome = await writer.UploadAsync([Cell, Entries]);

        Assert.True(outcome.Ok);
        Assert.Equal(2, outcome.Uploaded);
        Assert.Equal(2, github.Calls.Count(c => c.Path == "git/blobs"));

        (string _, string _, string commit) = github.Calls.Single(c => c.Path == "git/commits");
        Assert.Contains("\"parents\":[\"maintip\"]", commit, StringComparison.Ordinal);

        (string _, string _, string tree) = github.Calls.Single(c => c.Path == "git/trees");
        Assert.Contains("\"base_tree\":\"basetree\"", tree, StringComparison.Ordinal);
        Assert.Contains("assets/boss-exports/SaphiraActive.png", tree, StringComparison.Ordinal);

        (string _, string _, string made) = github.Calls.Single(c => c.Path == "git/refs");
        Assert.Contains("refs/heads/boss-icons", made, StringComparison.Ordinal);
        Assert.Contains("newcommit", made, StringComparison.Ordinal);
    }

    /// <summary>Later presses add to the branch and send only what it does not have.</summary>
    [Fact]
    public async Task ALaterPressSendsOnlyWhatChanged()
    {
        var github = new FakeGitHub { BranchExists = true };
        github.Held[Cell.Path] = BossExportUpload.BlobId(Cell.Content);
        using var writer = new GitHubWriter("token", github.Send);

        UploadOutcome outcome = await writer.UploadAsync([Cell, Entries]);

        Assert.True(outcome.Ok);
        Assert.Equal(1, outcome.Uploaded);
        Assert.Single(github.Calls, c => c.Path == "git/blobs");
        Assert.Contains(github.Calls, c => c.Path == "git/commits" && c.Body.Contains("\"branchtip\"", StringComparison.Ordinal));
        Assert.Contains(github.Calls, c => c.Method == "PATCH" && c.Body.Contains("\"force\":false", StringComparison.Ordinal));
    }

    /// <summary>A press with nothing new writes nothing at all.</summary>
    [Fact]
    public async Task APressWithNothingNewWritesNothing()
    {
        var github = new FakeGitHub { BranchExists = true };
        github.Held[Cell.Path] = BossExportUpload.BlobId(Cell.Content);
        using var writer = new GitHubWriter("token", github.Send);

        UploadOutcome outcome = await writer.UploadAsync([Cell]);

        Assert.True(outcome.Ok);
        Assert.Equal(0, outcome.Uploaded);
        Assert.DoesNotContain(github.Calls, c => c.Method != "GET");
    }

    /// <summary>The bake force-pushed in between: the commit is made again on the new tip, once.</summary>
    [Fact]
    public async Task ABranchThatMovedIsTriedOnceMoreAndThenReported()
    {
        var once = new FakeGitHub { BranchExists = true, Moves = 1 };
        using (var writer = new GitHubWriter("token", once.Send))
        {
            Assert.True((await writer.UploadAsync([Cell])).Ok);
            Assert.Equal(2, once.Calls.Count(c => c.Method == "PATCH"));
        }

        var always = new FakeGitHub { BranchExists = true, Moves = 5 };
        using (var writer = new GitHubWriter("token", always.Send))
        {
            UploadOutcome outcome = await writer.UploadAsync([Cell]);
            Assert.False(outcome.Ok);
            Assert.Contains("press again", outcome.Said, StringComparison.Ordinal);
            Assert.Equal(2, always.Calls.Count(c => c.Method == "PATCH"));
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "refused the token")]
    [InlineData(HttpStatusCode.Forbidden, "Contents: read and write")]
    public async Task ARefusedTokenSaysWhatToDo(HttpStatusCode status, string said)
    {
        var github = new FakeGitHub { Everything = status };
        using var writer = new GitHubWriter("token", github.Send);

        UploadOutcome outcome = await writer.UploadAsync([Cell]);

        Assert.False(outcome.Ok);
        Assert.Contains(said, outcome.Said, StringComparison.Ordinal);
        Assert.DoesNotContain(github.Calls, c => c.Method != "GET");
    }

    /// <summary>
    /// The token goes through the protect step on its way in and the unprotect step on its way
    /// out, and is never in the file as written.
    /// </summary>
    [Fact]
    public void TheTokenIsStoredOnlyAsWhatTheProtectStepMade()
    {
        string path = Path.Combine(Path.GetTempPath(), $"github-{Guid.NewGuid():N}.json");
        static byte[] Flip(byte[] bytes) => [.. bytes.Select(b => (byte)(b ^ 0x5A))];

        try
        {
            Assert.False(GitHubTokenStore.Has(path));
            Assert.Equal(string.Empty, GitHubTokenStore.Load(Flip, path));
            Assert.False(GitHubTokenStore.Save("   ", Flip, path));

            Assert.True(GitHubTokenStore.Save(" github_pat_secret ", Flip, path));
            Assert.True(GitHubTokenStore.Has(path));
            Assert.DoesNotContain("github_pat_secret", File.ReadAllText(path), StringComparison.Ordinal);
            Assert.Equal("github_pat_secret", GitHubTokenStore.Load(Flip, path));

            // A blob from somebody else's machine does not open here: empty, so the overlay asks again.
            Assert.Equal(
                string.Empty,
                GitHubTokenStore.Load(_ => throw new System.Security.Cryptography.CryptographicException("not yours"), path));

            Assert.True(GitHubTokenStore.Forget(path));
            Assert.False(GitHubTokenStore.Has(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
