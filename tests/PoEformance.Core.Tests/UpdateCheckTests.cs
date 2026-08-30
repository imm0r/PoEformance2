using PoEformance.Features;

namespace PoEformance.Core.Tests;

/// <summary>
/// The half of the updater that decides whether there is anything to do.
/// </summary>
/// <remarks>
/// Every test here runs with the network handed in, which is the point: the decision is a
/// comparison between two small files, and the expensive part of getting it wrong is that both
/// wrong answers are silent. Offering an update to the build already installed is a restart
/// loop; failing to offer one leaves somebody running last month's offsets against this week's
/// patch, and the symptom of that is not "no update" - it is half the tool reading garbage.
/// </remarks>
public sealed class UpdateCheckTests
{
    private const string Zip = UpdateCheck.AssetName;
    private const string Stamp = UpdateCheck.StampName;

    /// <summary>One release in the shape the GitHub API answers with.</summary>
    /// <remarks>
    /// Written out rather than trimmed to the six fields that are read, because what is being
    /// tested is a reader pointed at somebody else's schema: a fixture holding only the fields
    /// it wants proves nothing about the ones it has to walk past.
    /// </remarks>
    private static string Release(
        string tag, string notes, string uploaded, bool withStamp = true, bool draft = false, long size = 1234)
    {
        string zip = Asset(Zip, tag, uploaded, size);
        string stamp = withStamp ? "," + Asset(Stamp, tag, uploaded, 120) : string.Empty;

        return "{"
            + $"\"tag_name\":\"{tag}\","
            + "\"name\":\"Latest build (auto)\","
            + $"\"draft\":{(draft ? "true" : "false")},"
            + "\"prerelease\":true,"
            + "\"published_at\":\"2026-01-01T00:00:00Z\","
            + $"\"body\":\"{Escaped(notes)}\","
            + $"\"assets\":[{zip}{stamp}]"
            + "}";
    }

    /// <summary>Enough JSON escaping for the fixtures here - release notes have newlines in them.</summary>
    private static string Escaped(string text)
        => text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private static string Asset(string name, string tag, string uploaded, long size)
        => "{"
            + $"\"name\":\"{name}\","
            + $"\"size\":{size},"
            + $"\"updated_at\":\"{uploaded}\","
            + $"\"browser_download_url\":\"https://example.invalid/{tag}/{name}\""
            + "}";

    private static string StampJson(string commit, string built)
        => $"{{\"tag\":\"latest-dev\",\"commit\":\"{commit}\",\"builtUtc\":\"{built}\",\"runNumber\":7}}";

    private static BuildStamp Built(string commit, string when)
        => new() { Tag = "latest-dev", Commit = commit, BuiltUtc = DateTimeOffset.Parse(when), RunNumber = 1 };

    // ── Parsing ────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_ReadsTheAssetsTheUpdaterNeeds()
    {
        IReadOnlyList<ReleaseInfo> found = GitHubReleases.Parse(
            $"[{Release("latest-dev", "### Changes\n- did a thing", "2026-08-30T10:00:00Z")}]",
            Zip,
            Stamp);

        ReleaseInfo release = Assert.Single(found);
        Assert.Equal("latest-dev", release.Tag);
        Assert.Equal($"https://example.invalid/latest-dev/{Zip}", release.DownloadUrl);
        Assert.Equal($"https://example.invalid/latest-dev/{Stamp}", release.StampUrl);
        Assert.Equal(1234, release.DownloadSize);
        Assert.Contains("did a thing", release.Notes, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_OrdersByWhenTheBuildWasUploaded()
    {
        // NOT by published_at, which is the same for every one of these: the rolling tag is
        // created once and edited forever. Sorting by it would leave "the newest release"
        // meaning "whichever GitHub happened to list first".
        IReadOnlyList<ReleaseInfo> found = GitHubReleases.Parse(
            $"[{Release("older", "old", "2026-08-01T00:00:00Z")},"
            + $"{Release("latest-dev", "new", "2026-08-30T00:00:00Z")}]",
            Zip,
            Stamp);

        Assert.Equal("latest-dev", found[0].Tag);
        Assert.Equal("older", found[1].Tag);
    }

    [Fact]
    public void Parse_SkipsDraftsAndReleasesWithoutABuild()
    {
        string noAsset = """
            {"tag_name":"notes-only","name":"n","draft":false,"body":"","assets":[]}
            """;

        IReadOnlyList<ReleaseInfo> found = GitHubReleases.Parse(
            $"[{Release("draft", "x", "2026-08-30T00:00:00Z", draft: true)},{noAsset}]",
            Zip,
            Stamp);

        // A draft's assets are not downloadable without a token, and a release with no zip is
        // not something anybody can install. Neither is an update.
        Assert.Empty(found);
    }

    [Fact]
    public void Parse_SurvivesAnAnswerThatIsNotAReleaseList()
    {
        // What a rate-limited API actually returns: an object with a message in it. The check
        // reports "no release carries the build" and asks again later, rather than throwing
        // out of a background task.
        Assert.Empty(GitHubReleases.Parse("""{"message":"API rate limit exceeded"}""", Zip, Stamp));
        Assert.Empty(GitHubReleases.Parse("not json at all", Zip, Stamp));
    }

    // ── The comparison ─────────────────────────────────────────────────────

    [Fact]
    public void Compare_SameCommitIsUpToDate()
        => Assert.Equal(
            UpdateVerdict.UpToDate,
            UpdateCheck.Compare(Built("abc123", "2026-08-01T00:00:00Z"), Built("ABC123", "2026-08-01T00:00:00Z")));

    [Fact]
    public void Compare_ANewerBuildIsAvailable()
        => Assert.Equal(
            UpdateVerdict.Available,
            UpdateCheck.Compare(Built("aaa", "2026-08-01T00:00:00Z"), Built("bbb", "2026-08-30T00:00:00Z")));

    [Fact]
    public void Compare_AnOlderPublishedBuildIsNotOffered()
    {
        // Being AHEAD of the release is an ordinary state here - a build from a branch, or one
        // whose commit was reverted after publishing. Offering an "update" would walk that
        // build backwards, and the person taking it would have no way of telling.
        Assert.Equal(
            UpdateVerdict.UpToDate,
            UpdateCheck.Compare(Built("aaa", "2026-08-30T00:00:00Z"), Built("bbb", "2026-08-01T00:00:00Z")));
    }

    [Fact]
    public void Compare_ABuildThatCannotSayWhatItIsIsNeverOffered()
    {
        // The developer-machine case. There is no version.json in a bin folder, so there is
        // nothing to compare - and the wrong answer here is "available", which would offer to
        // unzip a release over somebody's working tree.
        Assert.Equal(
            UpdateVerdict.CannotCompare,
            UpdateCheck.Compare(BuildStamp.Unknown, Built("bbb", "2026-08-30T00:00:00Z")));

        Assert.Equal(
            UpdateVerdict.CannotCompare,
            UpdateCheck.Compare(Built("aaa", "2026-08-01T00:00:00Z"), BuildStamp.Unknown));
    }

    [Fact]
    public void AHalfStampIsNotKnown()
    {
        // Both halves or neither: the commit says whether two builds differ, the timestamp
        // says which way round they are. One without the other decides nothing.
        Assert.False(new BuildStamp { Commit = "abc" }.Known);
        Assert.False(new BuildStamp { BuiltUtc = DateTimeOffset.UtcNow }.Known);
        Assert.True(Built("abc", "2026-08-01T00:00:00Z").Known);
    }

    // ── The check end to end ───────────────────────────────────────────────

    private static UpdateCheck Checking(BuildStamp local, params (string Match, string Answer)[] answers)
        => new(
            local,
            ask: (address, _) =>
            {
                foreach ((string match, string answer) in answers)
                {
                    if (address.Contains(match, StringComparison.Ordinal))
                    {
                        return Task.FromResult<string?>(answer);
                    }
                }

                return Task.FromResult<string?>(null);
            });

    [Fact]
    public async Task ANewerPublishedBuildIsOffered()
    {
        using UpdateCheck check = Checking(
            Built("aaaaaaa1111", "2026-08-01T00:00:00Z"),
            ("api.github.com", $"[{Release("latest-dev", "- fixed a thing", "2026-08-30T00:00:00Z")}]"),
            (Stamp, StampJson("bbbbbbb2222", "2026-08-30T00:00:00Z")));

        await check.CheckAsync();

        Assert.Equal(UpdateVerdict.Available, check.Verdict);
        Assert.True(check.Offering);
        Assert.Equal("bbbbbbb2222", check.Remote.Commit);
        Assert.Contains("fixed a thing", check.Newest!.Notes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWavedAwayBuildStopsBeingOffered()
    {
        using UpdateCheck check = Checking(
            Built("aaaaaaa1111", "2026-08-01T00:00:00Z"),
            ("api.github.com", $"[{Release("latest-dev", "notes", "2026-08-30T00:00:00Z")}]"),
            (Stamp, StampJson("bbbbbbb2222", "2026-08-30T00:00:00Z")));

        await check.CheckAsync();
        check.Skipped = "bbbbbbb2222";

        // The VERDICT does not change - the build really is newer, and the tab still says so.
        // What stops is the nagging.
        Assert.Equal(UpdateVerdict.Available, check.Verdict);
        Assert.False(check.Offering);
    }

    [Fact]
    public async Task ANextBuildStillGetsToAsk()
    {
        using UpdateCheck check = Checking(
            Built("aaaaaaa1111", "2026-08-01T00:00:00Z"),
            ("api.github.com", $"[{Release("latest-dev", "notes", "2026-08-31T00:00:00Z")}]"),
            (Stamp, StampJson("ccccccc3333", "2026-08-31T00:00:00Z")));

        check.Skipped = "bbbbbbb2222";
        await check.CheckAsync();

        // "Not this one" is about a build, not about the feature. A boolean here would have
        // turned one dismissal into permanent silence.
        Assert.True(check.Offering);
    }

    [Fact]
    public async Task AReleaseWithoutAStampCannotBeCompared()
    {
        using UpdateCheck check = Checking(
            Built("aaaaaaa1111", "2026-08-01T00:00:00Z"),
            ("api.github.com", $"[{Release("latest-dev", "notes", "2026-08-30T00:00:00Z", withStamp: false)}]"));

        await check.CheckAsync();

        // The tempting guess is to use the asset's upload time as the build time. It is wrong
        // in the direction that matters: the upload finishes AFTER the build, so the running
        // build compares as older than itself and every launch offers an update to the copy
        // already installed.
        Assert.Equal(UpdateVerdict.CannotCompare, check.Verdict);
        Assert.False(check.Offering);
    }

    [Fact]
    public async Task NobodyAnsweringIsAFailureAndNotAnUpdate()
    {
        using UpdateCheck check = Checking(Built("aaaaaaa1111", "2026-08-01T00:00:00Z"));

        await check.CheckAsync();

        Assert.Equal(UpdateVerdict.Failed, check.Verdict);
        Assert.False(check.Offering);
        Assert.Null(check.Newest);
    }

    [Fact]
    public async Task StalenessDecidesWhetherToAskAgain()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-30T12:00:00Z");
        var check = new UpdateCheck(
            Built("aaaaaaa1111", "2026-08-01T00:00:00Z"),
            ask: (_, _) => Task.FromResult<string?>(null),
            clock: () => now);

        using (check)
        {
            Assert.True(check.Old); // never asked
            await check.CheckAsync();
            Assert.False(check.Old);

            now += UpdateCheck.GoesStale + TimeSpan.FromMinutes(1);
            Assert.True(check.Old);
        }
    }
}
