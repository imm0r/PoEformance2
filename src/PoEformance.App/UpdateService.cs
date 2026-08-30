using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using PoEformance.Features;

namespace PoEformance.App;

/// <summary>
/// The auto-update feature, wired together: the check, the download, and the restart.
/// </summary>
/// <remarks>
/// The pieces this owns live in Features and are testable without Windows and without a
/// network. What is HERE is the part that cannot be either: starting a process that outlives
/// this one, and ending this one so its files can be replaced.
///
/// FOUR STEPS, EACH ASKED FOR. The check runs on its own - that is the whole point of a check
/// - but the download does not start until somebody presses a button, and the restart does not
/// happen until somebody presses another one. A tool that reads another process's memory,
/// three windows deep in a fight, does not get to decide on its own that now is a good moment
/// to close itself.
///
/// The same object serves the config window and the overlay tab. One check, one installer, one
/// state: two of either would have the overlay reporting "up to date" while the config window
/// downloaded, which is the shape of "the tool is lying to me" that costs the most trust.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class UpdateService : IDisposable
{
    private readonly UpdateCheck _check;
    private readonly UpdateInstaller _installer;
    private readonly string[] _restartArguments;

    private UpdateSettings _settings;

    /// <summary>The build the console has already announced, so it is announced once.</summary>
    private string _announced = string.Empty;

    /// <param name="outcome">
    /// What the launch before this one did: "updated", "failed", or empty. The restarted build
    /// is the only thing that can report an update, because the build that performed it has
    /// been replaced and the script that did the copying has exited.
    /// </param>
    public UpdateService(string outcome, string outcomeVersion)
    {
        _settings = UpdateSettingsStore.Load();
        _check = new UpdateCheck(BuildStamp.Load())
        {
            Enabled = _settings.Enabled,
            Skipped = _settings.Skipped,
        };

        _check.Answered = Report;

        _installer = new UpdateInstaller();
        _restartArguments = [.. UpdateScript.RestartArguments(Environment.GetCommandLineArgs()[1..])];

        Outcome = outcome;
        OutcomeVersion = outcomeVersion;
    }

    /// <summary>What the previous launch's update did - "updated", "failed", or empty.</summary>
    public string Outcome { get; }

    /// <summary>The build that update installed, when there was one.</summary>
    public string OutcomeVersion { get; }

    /// <summary>The check itself, for the overlay tab to read.</summary>
    public UpdateCheck Check => _check;

    /// <summary>The installer, for the overlay tab to read.</summary>
    public UpdateInstaller Installer => _installer;

    /// <summary>Says what this build is and starts the first check.</summary>
    public void Start()
    {
        Console.WriteLine();
        Console.WriteLine($"build   {_check.Local.Describe()}");

        if (Outcome.Length > 0)
        {
            Console.WriteLine(Outcome == "updated"
                ? $"update  installed {OutcomeVersion} - this is the new build"
                : "update  DID NOT APPLY - this is still the old build. "
                    + $"What went wrong is in {_installer.LogPath}");
        }

        if (!_settings.Enabled)
        {
            Console.WriteLine("update  checking is off (config window, Update tab)");
            return;
        }

        _check.Refresh();
    }

    /// <summary>Asks again if the last answer has aged out. Cheap enough for a tick.</summary>
    public void Tick() => _check.RefreshIfStale();

    /// <summary>
    /// The console's copy of the notice, printed once per build found.
    /// </summary>
    /// <remarks>
    /// The third surface, and the only one somebody running WITHOUT the overlay and without
    /// the config window ever sees - which is every automation-only session. Once per build,
    /// because the check repeats every six hours and a line repeated on a timer is a line
    /// nobody reads.
    ///
    /// Runs on the checking thread. Console writes are safe there; nothing else here is
    /// touched.
    /// </remarks>
    private void Report(UpdateVerdict verdict)
    {
        if (verdict != UpdateVerdict.Available || !_check.Offering)
        {
            return;
        }

        string commit = _check.Remote.Commit;
        if (string.Equals(commit, _announced, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _announced = commit;
        Console.WriteLine();
        Console.WriteLine($"update  a newer build is available - {_check.Status}");
        Console.WriteLine("        install it from the config window's Update tab, or the "
            + "overlay's Status page.");
    }

    /// <summary>The panel the config page draws.</summary>
    public PoEformance.Config.UpdateView View()
    {
        ReleaseInfo? release = _check.Newest;
        BuildStamp remote = _check.Remote;

        return new PoEformance.Config.UpdateView(
            Enabled: _settings.Enabled,
            Verdict: _check.Verdict.ToString(),
            Status: _check.Status,
            Busy: _check.Busy,
            Offering: _check.Offering,
            Current: _check.Local.Describe(),
            Available: remote.Known ? remote.Describe() : string.Empty,
            ReleaseName: release?.Name ?? string.Empty,
            ReleaseTag: release?.Tag ?? string.Empty,
            Notes: release?.Notes ?? string.Empty,
            ReleaseSize: release?.DownloadSize ?? 0,
            Checked: Since(_check.LastAsked),
            Step: _installer.Step.ToString(),
            InstallStatus: _installer.Status,
            Received: _installer.Received,
            Total: _installer.Total,
            Outcome: Outcome,
            OutcomeVersion: OutcomeVersion,
            Log: _installer.LogPath);
    }

    /// <summary>
    /// Handles the page's update commands. Null when the request is not one of ours.
    /// </summary>
    /// <remarks>
    /// An empty string means "handled - send the state back", which is this bridge's
    /// convention; see <c>ConfigWindowHost.Handle</c>.
    /// </remarks>
    public string? Handle(PoEformance.Config.ConfigRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        switch (request.Type)
        {
            case "checkUpdate":
                // Works whether or not automatic checking is on, and does NOT turn it on. A
                // button press is a one-off request; changing a saved setting behind it would
                // leave the checkbox beside it disagreeing with what the tool now does.
                _check.Refresh();
                return string.Empty;

            case "downloadUpdate":
                Download();
                return string.Empty;

            case "installUpdate":
                Apply();
                return string.Empty;

            case "skipUpdate":
                Skip();
                return string.Empty;

            case "setUpdateSettings":
                if (request.Payload.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                UpdateSettings? sent = request.Payload.Deserialize(
                    PoEformance.Config.ConfigJsonContext.Default.UpdateSettings);
                if (sent is null)
                {
                    return null;
                }

                Save(sent);

                // Switching it back on asks straight away rather than waiting six hours for
                // the staleness timer, which is what somebody who just switched it on wants.
                if (_settings.Enabled && _check.Verdict == UpdateVerdict.NotChecked)
                {
                    _check.Refresh();
                }

                return string.Empty;

            default:
                return null;
        }
    }

    /// <summary>Waves the offered build away, so it is not brought up again.</summary>
    /// <remarks>
    /// The BUILD is waved away, not the feature: the commit is remembered, so the next build
    /// published still gets to ask. A boolean here would have turned one "not now" into
    /// silence for good.
    /// </remarks>
    public void Skip() => Save(_settings with { Skipped = _check.Remote.Commit });

    /// <summary>Fetches and unpacks the published build. Does not replace anything.</summary>
    public void Download()
    {
        if (_check.Newest is not ReleaseInfo release)
        {
            return;
        }

        Console.WriteLine($"update  downloading {release.Tag} ({release.DownloadSize / (1024.0 * 1024):F0} MB)");
        _installer.Begin(release, _check.Remote);
    }

    /// <summary>Whether a download has finished and is waiting to be applied.</summary>
    public bool ReadyToApply => _installer.Step == UpdateStep.Ready && _installer.Plan is not null;

    /// <summary>
    /// Applies the unpacked build: writes the swap script, starts it, and ends this process.
    /// </summary>
    /// <remarks>
    /// THIS DOES NOT RETURN. The script's first act is to wait for this process id to
    /// disappear, so anything done after starting it would be done inside the window the copy
    /// is waiting on.
    ///
    /// <see cref="Environment.Exit(int)"/> rather than an orderly shutdown, because there is no
    /// orderly shutdown to reach from here: this is called from the config window's message
    /// thread or the overlay's render thread, and neither owns the other's loop. Settings are
    /// written when they change rather than at exit, so nothing configured is lost - the one
    /// thing that is, is the tail of a session recording, which is why <c>--record</c> is not
    /// carried into the restart (see <see cref="UpdateScript.RestartArguments"/>).
    /// </remarks>
    public void Apply()
    {
        if (_installer.Plan is not UpdatePlan plan)
        {
            Console.Error.WriteLine("update  nothing is unpacked yet - download it first.");
            return;
        }

        try
        {
            string script = UpdateScript.Text(
                plan,
                Environment.ProcessId,
                _installer.ArchivePath,
                _installer.LogPath,
                _restartArguments);

            File.WriteAllText(_installer.ScriptPath, script);

            // UseShellExecute detaches it properly: started this way the script does not share
            // this process's console, so it survives the exit below rather than dying with the
            // window it was printing into.
            var start = new ProcessStartInfo
            {
                FileName = "cmd.exe",

                // The doubled quotes are cmd's own rule for /c with a quoted path - without
                // them a folder with a space in it makes the script unfindable.
                Arguments = $"/c \"\"{_installer.ScriptPath}\"\"",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = plan.Install,
            };

            using Process? applying = Process.Start(start);
            if (applying is null)
            {
                Console.Error.WriteLine("update  could not start the installer script.");
                return;
            }

            Console.WriteLine();
            Console.WriteLine($"update  installing {plan.Version} - the tool closes and comes back on its own.");
            Console.Out.Flush();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"update  could not be started: {exception.Message}");
            return;
        }

        Environment.Exit(0);
    }

    private void Save(UpdateSettings sent)
    {
        _settings = sent.Normalised();
        _check.Enabled = _settings.Enabled;
        _check.Skipped = _settings.Skipped;

        if (!UpdateSettingsStore.Save(_settings))
        {
            Console.Error.WriteLine(
                $"could not write {UpdateSettingsStore.DefaultPath} - the change applies to this session only.");
        }
    }

    /// <summary>"never", or how long ago in words.</summary>
    private static string Since(DateTimeOffset when)
    {
        if (when == default)
        {
            return "never";
        }

        TimeSpan ago = DateTimeOffset.UtcNow - when;
        return ago switch
        {
            { TotalSeconds: < 90 } => "just now",
            { TotalMinutes: < 90 } => $"{ago.TotalMinutes:F0} minutes ago",
            _ => $"{ago.TotalHours:F0} hours ago",
        };
    }

    public void Dispose()
    {
        _check.Dispose();
        _installer.Dispose();
    }
}
