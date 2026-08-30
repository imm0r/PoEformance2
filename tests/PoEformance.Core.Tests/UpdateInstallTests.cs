using System.IO.Compression;
using System.Text;
using PoEformance.Features;

namespace PoEformance.Core.Tests;

/// <summary>
/// The half of the updater that touches the disk: fetch, unpack, check, and the swap script.
/// </summary>
/// <remarks>
/// What is pinned here is the ORDER OF THE GUARDS, because the whole design rests on it.
/// Nothing may reach the installation folder until the archive has been unpacked into a
/// scratch folder AND found to contain the executable - a half-copied installation is not a
/// state this tool can recover from on its own, and the person it happens to is left with a
/// folder that neither runs nor can be updated again.
/// </remarks>
public sealed class UpdateInstallTests
{
    private static readonly BuildStamp Remote = new()
    {
        Tag = "latest-dev",
        Commit = "bbbbbbb2222",
        BuiltUtc = DateTimeOffset.Parse("2026-08-30T00:00:00Z"),
        RunNumber = 42,
    };

    /// <summary>A zip in the shape the publish workflow produces: contents at the root.</summary>
    private static byte[] Archive(params (string Path, string Body)[] entries)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string path, string body) in entries)
            {
                using Stream stream = zip.CreateEntry(path).Open();
                stream.Write(Encoding.UTF8.GetBytes(body));
            }
        }

        return memory.ToArray();
    }

    private static byte[] GoodBuild() => Archive(
        ("PoEformance.App.exe", "MZ this is a build"),
        ("schema/poe2.offsets.json", "{}"),
        ("version.json", """{"commit":"bbbbbbb2222"}"""));

    private static ReleaseInfo ReleaseOf(byte[] archive) => new(
        "latest-dev",
        "Latest build (auto)",
        "- did a thing",
        "https://example.invalid/latest-dev/PoEformance-win-x64.zip",
        archive.Length,
        "https://example.invalid/latest-dev/version.json",
        DateTimeOffset.Parse("2026-08-30T00:00:00Z"));

    private static UpdateInstaller Installing(string root, byte[] served)
        => new(
            folder: Path.Combine(root, "update"),
            install: Path.Combine(root, "installed"),
            executable: Path.Combine(root, "installed", "PoEformance.App.exe"),
            open: (_, _) => Task.FromResult<Stream?>(new MemoryStream(served)));

    private static string Scratch()
    {
        string root = Path.Combine(Path.GetTempPath(), $"poeformance-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "installed"));
        return root;
    }

    // ── Fetch and unpack ───────────────────────────────────────────────────

    [Fact]
    public async Task AGoodBuildUnpacksIntoStagingAndNowhereElse()
    {
        string root = Scratch();
        try
        {
            byte[] archive = GoodBuild();
            using UpdateInstaller installer = Installing(root, archive);

            Assert.True(await installer.RunAsync(ReleaseOf(archive), Remote));

            Assert.Equal(UpdateStep.Ready, installer.Step);
            Assert.True(File.Exists(Path.Combine(installer.StagingPath, "PoEformance.App.exe")));
            Assert.True(File.Exists(Path.Combine(installer.StagingPath, "schema", "poe2.offsets.json")));

            // THE INSTALLATION IS UNTOUCHED. It cannot be otherwise: the executable is
            // running and Windows holds its image locked, so a copy attempted from in here
            // fails halfway and leaves half of each build behind.
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "installed")));

            UpdatePlan plan = Assert.IsType<UpdatePlan>(installer.Plan);
            Assert.Equal(installer.StagingPath, plan.Staging);
            Assert.Equal("bbbbbbb2222", plan.Version);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AShortDownloadIsAFailedDownloadAndNotASmallBuild()
    {
        string root = Scratch();
        try
        {
            byte[] archive = GoodBuild();
            using UpdateInstaller installer = Installing(root, archive[..(archive.Length / 2)]);

            // The release said how many bytes there are. Reporting a truncated file as a zip
            // problem sends the next person looking at the release rather than at the
            // connection that dropped.
            Assert.False(await installer.RunAsync(ReleaseOf(archive), Remote));
            Assert.Equal(UpdateStep.Failed, installer.Step);
            Assert.Contains("stopped at", installer.Status, StringComparison.Ordinal);
            Assert.Null(installer.Plan);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AnArchiveWithoutTheExecutableIsRefused()
    {
        string root = Scratch();
        try
        {
            byte[] archive = Archive(("readme.txt", "not a build"));
            using UpdateInstaller installer = Installing(root, archive);

            // The whole reason to unpack into a scratch folder first: a release whose layout
            // is not what this expects is caught while nothing is at stake.
            Assert.False(await installer.RunAsync(ReleaseOf(archive), Remote));
            Assert.Equal(UpdateStep.Failed, installer.Step);
            Assert.Null(installer.Plan);
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "installed")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ACorruptArchiveFailsWhileNothingIsAtStake()
    {
        string root = Scratch();
        try
        {
            byte[] rubbish = Encoding.UTF8.GetBytes("PK\u0003\u0004 and then nothing that parses");
            using UpdateInstaller installer = Installing(root, rubbish);

            // Every entry in a zip carries a CRC32 and the extractor verifies it, which is
            // why there is no checksum of our own: corruption is caught here, in the scratch
            // folder, where a failure costs a delete.
            Assert.False(await installer.RunAsync(
                ReleaseOf(rubbish) with { DownloadSize = rubbish.Length }, Remote));
            Assert.Equal(UpdateStep.Failed, installer.Step);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ASecondAttemptDoesNotInheritTheFirstOnesLeftovers()
    {
        string root = Scratch();
        try
        {
            byte[] archive = GoodBuild();
            using UpdateInstaller installer = Installing(root, archive);
            Assert.True(await installer.RunAsync(ReleaseOf(archive), Remote));

            // A file the NEW build no longer ships. Extracting over the previous staging copy
            // would carry it forward and then copy it into the installation.
            string stale = Path.Combine(installer.StagingPath, "gone-in-the-new-build.dll");
            File.WriteAllText(stale, "old");

            Assert.True(await installer.RunAsync(ReleaseOf(archive), Remote));
            Assert.False(File.Exists(stale));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── The swap script ────────────────────────────────────────────────────

    private static string Script(params string[] arguments)
        => UpdateScript.Text(
            new UpdatePlan(
                @"C:\Tools\PoEformance\update\staging",
                @"C:\Tools\PoEformance",
                @"C:\Tools\PoEformance\PoEformance.App.exe",
                "bbbbbbb2222"),
            processId: 4242,
            archive: @"C:\Tools\PoEformance\update\PoEformance-win-x64.zip",
            log: @"C:\Tools\PoEformance\update\apply.log",
            arguments);

    [Fact]
    public void TheScriptWaitsForTheToolToExitBeforeItCopies()
    {
        string script = Script("--overlay");

        int waiting = script.IndexOf("PID eq 4242", StringComparison.Ordinal);
        int copying = script.IndexOf("robocopy", StringComparison.Ordinal);

        Assert.True(waiting > 0, "the script has to wait for the process it is replacing");
        Assert.True(copying > waiting, "the copy must come after the wait, not before it");
    }

    [Fact]
    public void TheScriptCopiesAndNeverMirrors()
    {
        string script = Script();

        // /MIR would delete everything in the installation that is not in the new build,
        // which is config\: every setting, every rule, the wealth history, the browser
        // profile. A stale file left behind is inert; deleted settings are gone.
        Assert.Contains("/E", script, StringComparison.Ordinal);
        Assert.DoesNotContain("/MIR", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheScriptComesBackWithTheSwitchesItWasStartedWith()
    {
        string script = Script("--overlay", "--config");

        Assert.Contains("--overlay --config --updated bbbbbbb2222", script, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedCopyStillStartsTheOldBuildAndSaysSo()
    {
        string script = Script("--overlay");

        // The build that started the update is still installed and still runs. Leaving it
        // unstarted would present a failed update as "the tool did not come back", which is a
        // far worse thing to be looking at than a message and a log.
        Assert.Contains(":failed", script, StringComparison.Ordinal);
        Assert.Contains(UpdateScript.FailedFlag, script, StringComparison.Ordinal);
    }

    [Fact]
    public void NoLogRedirectFollowsADigit()
    {
        // cmd reads a digit immediately in front of a redirection operator as a HANDLE
        // NUMBER. "echo updated to bbbbbbb2222>>log" therefore echoes "...bbbbbbb222" and
        // redirects STDERR - the line disappears from the log, and which build was installed
        // is the one thing that log has to record. Commit shas end in a digit half the time,
        // so this is not a hypothetical.
        foreach (string line in Script("--overlay").Split('\n'))
        {
            int at = line.IndexOf(">>", StringComparison.Ordinal);
            if (at > 0)
            {
                Assert.False(
                    char.IsAsciiDigit(line[at - 1]),
                    $"a digit immediately before >> is a handle number to cmd: {line.Trim()}");
            }
        }
    }

    [Fact]
    public void TheRestartFlagAlwaysCarriesAValue()
    {
        // --updated takes a value, and the tool refuses to start when an option that takes one
        // is handed nothing. That refusal is right everywhere except immediately after a
        // successful update, where it would present as the update having broken the tool.
        string script = UpdateScript.Text(
            new UpdatePlan(@"C:\x\staging", @"C:\x", @"C:\x\PoEformance.App.exe", string.Empty),
            1,
            @"C:\x\u.zip",
            @"C:\x\apply.log",
            ["--overlay"]);

        Assert.Contains("--updated unknown", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWaitDoesNotDependOnAConsoleInputHandle()
    {
        // timeout.exe quits immediately when it cannot read the console input handle, and the
        // script runs hidden and detached. That failure is not a slow wait - it is a loop
        // that burns all its tries in milliseconds and gives up while the tool it is waiting
        // for is still shutting down, leaving two builds started.
        string script = Script();

        Assert.DoesNotContain("timeout ", script, StringComparison.Ordinal);
        Assert.Contains("ping -n 2", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInstallFolderIsWrittenWithoutItsTrailingSeparator()
    {
        // AppContext.BaseDirectory - which IS the installation folder - always ends with a
        // separator. Left on, the script reads robocopy "C:\Tools\PoEformance\", and the C
        // runtime's argument parser treats the backslash as escaping the quote: robocopy
        // swallows it, takes the rest of the line as part of the path, and copies nowhere.
        string script = UpdateScript.Text(
            new UpdatePlan(
                @"C:\Tools\PoEformance\update\staging\",
                @"C:\Tools\PoEformance\",
                @"C:\Tools\PoEformance\PoEformance.App.exe",
                "abc"),
            1,
            @"C:\Tools\PoEformance\update\x.zip",
            @"C:\Tools\PoEformance\update\apply.log",
            []);

        Assert.Contains(@"robocopy ""C:\Tools\PoEformance\update\staging"" ""C:\Tools\PoEformance""",
            script, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\""", script, StringComparison.Ordinal);
    }

    [Fact]
    public void APercentSignInAPathIsWrittenAsOne()
    {
        string script = UpdateScript.Text(
            new UpdatePlan(@"C:\100% sure\staging", @"C:\100% sure", @"C:\100% sure\PoEformance.App.exe", "abc"),
            1,
            @"C:\100% sure\update.zip",
            @"C:\100% sure\apply.log",
            []);

        // A percent sign is the one character still special inside double quotes in a batch
        // file. Left alone, "C:\100% sure" would expand as a variable reference and the copy
        // would go somewhere else, or nowhere.
        Assert.Contains(@"C:\100%% sure", script, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\100% sure", script, StringComparison.Ordinal);
    }

    [Fact]
    public void RestartArguments_DropRecordingAndTheLastUpdatesOwnFlags()
    {
        IReadOnlyList<string> kept = UpdateScript.RestartArguments(
            ["--overlay", "--record", "session.rec", "--config", "--updated", "aaa111", "--update-failed"]);

        // --record names a file and restarting would truncate it. A session recording is
        // evidence somebody deliberately captured, and losing one to an update is not a trade
        // this gets to make on their behalf.
        Assert.Equal(["--overlay", "--config"], kept);
    }

    [Fact]
    public void RestartArguments_KeepEverythingElseUntouched()
    {
        // A restart that silently drops --overlay looks exactly like an update that broke the
        // overlay, and that is where the next hour goes.
        IReadOnlyList<string> kept = UpdateScript.RestartArguments(
            ["--overlay", "--config", "--debug", "--schema", @"C:\x\poe2.offsets.json"]);

        Assert.Equal(["--overlay", "--config", "--debug", "--schema", @"C:\x\poe2.offsets.json"], kept);
    }

    // ── The stamp and the settings ─────────────────────────────────────────

    [Fact]
    public void AStampRoundTripsThroughTheFileTheWorkflowWrites()
    {
        string path = Path.Combine(Path.GetTempPath(), $"poeformance-version-{Guid.NewGuid():N}.json");
        try
        {
            Assert.True(BuildStamp.Save(Remote, path));
            BuildStamp read = BuildStamp.Load(path);

            Assert.Equal(Remote.Commit, read.Commit);
            Assert.Equal(Remote.BuiltUtc, read.BuiltUtc);
            Assert.Equal(42, read.RunNumber);
            Assert.True(read.Known);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AMissingStampIsALocalBuildRatherThanAFailure()
    {
        BuildStamp none = BuildStamp.Load(
            Path.Combine(Path.GetTempPath(), $"poeformance-absent-{Guid.NewGuid():N}.json"));

        Assert.False(none.Known);
        Assert.Contains("local build", none.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheWorkflowsStampParsesAsWritten()
    {
        // Exactly the shape .github/workflows/publish.yml writes. If that heredoc and this
        // record ever disagree, every downloaded build reports itself as a local one - which
        // is silent, so it is worth a test rather than a reading.
        BuildStamp stamp = BuildStamp.Parse(
            """
            {
              "tag": "latest-dev",
              "commit": "0123456789abcdef0123456789abcdef01234567",
              "builtUtc": "2026-08-30T17:46:00Z",
              "runNumber": 123
            }
            """);

        Assert.True(stamp.Known);
        Assert.Equal("latest-dev", stamp.Tag);
        Assert.Equal("0123456", stamp.ShortCommit);
        Assert.Equal(123, stamp.RunNumber);
    }

    [Fact]
    public void SettingsRoundTripAndDefaultToChecking()
    {
        string path = Path.Combine(Path.GetTempPath(), $"poeformance-update-settings-{Guid.NewGuid():N}.json");
        try
        {
            Assert.True(UpdateSettings.Default.Enabled);

            Assert.True(UpdateSettingsStore.Save(new UpdateSettings { Enabled = false, Skipped = " abc " }, path));
            UpdateSettings read = UpdateSettingsStore.Load(path);

            Assert.False(read.Enabled);
            Assert.Equal("abc", read.Skipped);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
