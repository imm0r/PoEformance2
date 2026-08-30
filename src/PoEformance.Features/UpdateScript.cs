using System.Text;

namespace PoEformance.Features;

/// <summary>
/// The last step of an update: the part that has to happen after the tool has exited.
/// </summary>
/// <remarks>
/// WHY A SCRIPT AND NOT CODE. The files being replaced include the running executable and the
/// native libraries it has loaded, and Windows holds an open image locked - so the copy cannot
/// be performed by the process being replaced, from inside itself, at any point. Something has
/// to outlive the process, wait for it to go, copy, and start the new one. A batch file run by
/// <c>cmd.exe</c> is the smallest thing that can: it is already on every Windows, it needs no
/// execution policy (which is what rules out PowerShell - a machine set to
/// <c>Restricted</c> would fail the update at the very last step, after the download), and it
/// leaves a readable artefact on disk when somebody wants to know what happened.
///
/// IT COPIES, IT DOES NOT MIRROR. <c>robocopy /E</c> adds and overwrites; <c>/MIR</c> would
/// delete everything in the installation that is not in the new build - which is
/// <c>config/</c>: every setting, every rule, the wealth history and the browser profile. The
/// cost of copying is that a file the new build no longer ships stays behind. That is the
/// right way round: a stale file is inert, and deleted settings are gone.
///
/// IT ALWAYS RESTARTS THE TOOL, including when the copy failed. An update that goes wrong
/// halfway must not present as "the tool did not come back" - the build that started the
/// update is still installed and still runs, so it is started again with
/// <c>--update-failed</c> and says so, next to a log that names the reason.
/// </remarks>
public static class UpdateScript
{
    /// <summary>How long the script waits for the tool to exit before giving up.</summary>
    /// <remarks>
    /// Two minutes, checked once a second. Long enough for a slow teardown - the overlay
    /// releases GPU resources and the config window tears down a browser - and short enough
    /// that a process which is never going to exit does not leave a script waiting forever
    /// holding a staging folder.
    /// </remarks>
    public const int WaitSeconds = 120;

    /// <summary>The flag the restarted build is given after a successful update.</summary>
    public const string UpdatedFlag = "--updated";

    /// <summary>The flag it is given when the copy failed.</summary>
    public const string FailedFlag = "--update-failed";

    /// <summary>
    /// Writes the batch file that swaps the folders and starts the new build.
    /// </summary>
    /// <param name="plan">What was unpacked, and where it goes.</param>
    /// <param name="processId">The tool's own process id - what the script waits for.</param>
    /// <param name="archive">The downloaded zip, deleted once the copy succeeded.</param>
    /// <param name="log">Where the script writes what it did.</param>
    /// <param name="arguments">
    /// The command line to start again with, already stripped by
    /// <see cref="RestartArguments"/>.
    /// </param>
    public static string Text(
        UpdatePlan plan,
        int processId,
        string archive,
        string log,
        IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(arguments);

        // --updated TAKES A VALUE, and the tool refuses to start when an option that takes one
        // is handed nothing - deliberately, because a swallowed value silently drops whatever
        // followed it. That refusal is right everywhere except here, where it would land
        // immediately after a successful update and present as "the update broke the tool".
        string version = plan.Version.Length > 0 ? plan.Version : "unknown";

        var text = new StringBuilder();
        text.AppendLine("@echo off");
        text.AppendLine("setlocal");
        text.AppendLine("rem  Written by PoEformance to finish an update. It waits for the tool to");
        text.AppendLine("rem  exit, copies the unpacked build over the installation, and starts it");
        text.AppendLine("rem  again. Safe to delete; safe to read.");
        text.AppendLine($"set \"LOG={Batch(log)}\"");

        // A SPACE BEFORE EVERY >>, and it is not cosmetic. cmd reads a digit immediately in
        // front of a redirection operator as a HANDLE NUMBER, so `echo ...bbbbbbb2222>>log`
        // parses as "echo ...bbbbbbb222" with stderr redirected - the line vanishes from the
        // log, and the one thing the log has to record is which build was installed. Commit
        // shas end in a digit about as often as not.
        text.AppendLine($"echo === %DATE% %TIME% update starting, waiting for pid {processId} === >>\"%LOG%\"");
        text.AppendLine();

        // Waiting by PID rather than by file lock: a lock test would have to guess which of the
        // files being replaced the process is holding, and gets a different answer depending on
        // which features ran this session.
        text.AppendLine("set /a TRIES=0");
        text.AppendLine(":wait");
        text.AppendLine($"tasklist /FI \"PID eq {processId}\" /NH 2>nul | find \"{processId}\" >nul");
        text.AppendLine("if errorlevel 1 goto gone");
        text.AppendLine("set /a TRIES+=1");
        text.AppendLine($"if %TRIES% GEQ {WaitSeconds} (");
        text.AppendLine($"  echo [%DATE% %TIME%] pid {processId} is still running - nothing was copied >>\"%LOG%\"");
        text.AppendLine("  goto failed");
        text.AppendLine(")");
        // PING RATHER THAN TIMEOUT, for one reason: timeout.exe reads the console input
        // handle and quits immediately when it cannot ("input redirection is not
        // supported"). The script is started hidden and detached, so how it ends up with a
        // usable input handle is not something worth depending on - and the failure is not a
        // slow wait, it is a loop that burns all its tries in milliseconds and declares the
        // update failed while the tool it is waiting for is still shutting down.
        //
        // Ping only ever errs the other way. Two pings to the loopback address wait a second
        // between them; if something were blocking even that, they wait LONGER, and a wait
        // that is too long costs a few seconds while a wait that is too short costs the
        // update.
        text.AppendLine("ping -n 2 127.0.0.1 >nul 2>&1");
        text.AppendLine("goto wait");
        text.AppendLine();

        text.AppendLine(":gone");
        text.AppendLine($"echo [%DATE% %TIME%] copying the new build into place >>\"%LOG%\"");

        // /E copies subdirectories including empty ones; /R and /W keep a locked file from
        // costing thirty seconds each. Robocopy's exit codes below 8 all mean success of some
        // shade (nothing copied, files copied, extras present), so 8 is the threshold.
        text.AppendLine(
            $"robocopy \"{Folder(plan.Staging)}\" \"{Folder(plan.Install)}\" /E /R:2 /W:1 /NFL /NDL /NJH /NJS >>\"%LOG%\"");
        text.AppendLine("if errorlevel 8 goto failed");
        text.AppendLine($"rd /s /q \"{Folder(plan.Staging)}\" >nul 2>&1");
        text.AppendLine($"del /q \"{Batch(archive)}\" >nul 2>&1");
        text.AppendLine($"echo [%DATE% %TIME%] updated to {Batch(version)} >>\"%LOG%\"");

        // The installation folder is the one directory that is certainly still there after the
        // copy, and it is where the tool looks for its schema, ui and data anyway.
        text.AppendLine($"cd /d \"{Folder(plan.Install)}\"");
        text.AppendLine(
            $"start \"\" \"{Batch(plan.Executable)}\" {Line(arguments)} {UpdatedFlag} {Quote(version)}");
        text.AppendLine("goto done");
        text.AppendLine();

        text.AppendLine(":failed");
        text.AppendLine($"echo [%DATE% %TIME%] the update was NOT applied - starting the old build again >>\"%LOG%\"");
        text.AppendLine($"cd /d \"{Folder(plan.Install)}\"");
        text.AppendLine(
            $"start \"\" \"{Batch(plan.Executable)}\" {Line(arguments)} {FailedFlag}");
        text.AppendLine();

        text.AppendLine(":done");
        text.AppendLine("endlocal");
        return text.ToString();
    }

    /// <summary>
    /// The command line to come back with: this session's, minus what must not run twice.
    /// </summary>
    /// <remarks>
    /// The switches are kept, because a restart that silently drops <c>--overlay</c> looks
    /// exactly like an update that broke the overlay. Two things are dropped:
    ///
    /// <c>--record</c>, because it names a file and restarting would truncate it. A session
    /// recording is evidence somebody deliberately captured, and losing one to an update is
    /// not a trade this gets to make on their behalf.
    ///
    /// Any <c>--updated</c> or <c>--update-failed</c> the last restart added, so the notice
    /// belongs to this update rather than being carried forward from the previous one for the
    /// rest of the build's life.
    /// </remarks>
    public static IReadOnlyList<string> RestartArguments(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var kept = new List<string>(arguments.Count);
        for (int i = 0; i < arguments.Count; i++)
        {
            switch (arguments[i])
            {
                case "--record":
                case UpdatedFlag:
                    // Both take a value, which goes with them.
                    if (i + 1 < arguments.Count && !arguments[i + 1].StartsWith('-'))
                    {
                        i++;
                    }

                    break;

                case FailedFlag:
                    break;

                default:
                    kept.Add(arguments[i]);
                    break;
            }
        }

        return kept;
    }

    /// <summary>One argument, quoted for a batch line.</summary>
    private static string Quote(string argument)
        => argument.Length > 0 && argument.IndexOfAny([' ', '\t']) < 0
            ? Batch(argument)
            : $"\"{Batch(argument)}\"";

    private static string Line(IReadOnlyList<string> arguments)
        => string.Join(' ', arguments.Select(Quote));

    /// <summary>
    /// Escapes a value for a batch file.
    /// </summary>
    /// <remarks>
    /// A percent sign is the one character that is still special INSIDE double quotes in a
    /// batch file - <c>%TEMP%</c> in a path would expand to something else, and a lone
    /// <c>%</c> would eat the character after it. Doubling is how a batch file writes one.
    /// Everything else a Windows path may contain is literal within quotes.
    /// </remarks>
    private static string Batch(string value) => value.Replace("%", "%%", StringComparison.Ordinal);

    /// <summary>
    /// Escapes a DIRECTORY for a batch line, without the trailing separator.
    /// </summary>
    /// <remarks>
    /// THE TRAILING BACKSLASH IS A BUG WAITING TO HAPPEN, and this path has one by default:
    /// <see cref="AppContext.BaseDirectory"/> always ends with a separator, and the installation
    /// folder is exactly that. Written into the script it becomes <c>"C:\Tools\PoEformance\"</c>
    /// - and while cmd itself does not treat a backslash as an escape, the C runtime's argument
    /// parser does, so <c>robocopy</c> sees the quote as escaped, swallows it, and takes the
    /// rest of the line as part of the source path. The copy then goes nowhere, or somewhere
    /// else, and the log says the source does not exist.
    ///
    /// A bare drive ("C:\") is left alone: trimming it produces "C:", which means "wherever the
    /// current directory on C: is" rather than the root, and that is worse than the quoting.
    /// Nothing is installed at a drive root, but silently redirecting a copy is not a way to
    /// find that out.
    /// </remarks>
    private static string Folder(string path)
    {
        string trimmed = path.TrimEnd('\\', '/');
        return Batch(trimmed.Length == 0 || trimmed.EndsWith(':') ? path : trimmed);
    }
}
