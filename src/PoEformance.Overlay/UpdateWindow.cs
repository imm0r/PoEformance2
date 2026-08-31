using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;

namespace PoEformance.Overlay;

/// <summary>
/// Whether the build you are running is the newest one, and the button that fixes it.
/// </summary>
/// <remarks>
/// IN THE OVERLAY AS WELL AS THE CONFIG WINDOW, because of who is looking at what. The config
/// window is where a setting gets changed and it is opened deliberately; the overlay is what is
/// on screen for the whole session. An update that only announces itself in a window nobody has
/// open announces itself to nobody - and the single most valuable moment for this notice is
/// right after a game patch, when the offsets in the newest build are the reason half the tool
/// has gone quiet.
///
/// It draws NOTHING when there is nothing to say, which is the normal state: on the newest
/// build the page is three lines and no buttons. The notice that matters is on the tab label,
/// where the status page already is.
///
/// The buttons are the same two the config page has and they do the same two things, because
/// they are the same objects - see UpdateService in the App layer. Two copies of this state
/// would have one surface reporting "up to date" while the other downloaded.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class UpdateWindow
{
    private static readonly Vector4 DimText = OverlayInk.Quiet;
    private static readonly Vector4 GoodText = OverlayInk.Good;
    private static readonly Vector4 WarnText = OverlayInk.Warn;
    private static readonly Vector4 BadText = OverlayInk.Bad;
    private static readonly Vector4 NewsText = OverlayInk.Accent;

    private readonly UpdateCheck _check;
    private readonly UpdateInstaller _installer;
    private readonly Action _download;
    private readonly Action _install;
    private readonly Action _skip;

    /// <param name="download">Fetches and unpacks the published build. Replaces nothing.</param>
    /// <param name="install">Swaps the folders and restarts. Does not return.</param>
    /// <param name="skip">Waves this build away so it is not offered again.</param>
    public UpdateWindow(
        UpdateCheck check, UpdateInstaller installer, Action download, Action install, Action skip)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(installer);
        ArgumentNullException.ThrowIfNull(download);
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(skip);

        _check = check;
        _installer = installer;
        _download = download;
        _install = install;
        _skip = skip;
    }

    /// <summary>What the last restart's update did - "updated", "failed", or empty.</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>The build that update installed, when there was one.</summary>
    public string OutcomeVersion { get; set; } = string.Empty;

    /// <summary>The tab's label, which carries the notice.</summary>
    /// <remarks>
    /// The label is the notification. A tab that says "Update" whether or not there is one is a
    /// tab nobody reads twice; one that says "Update available" is read the first time it says
    /// it, which is the only time it needs to be.
    /// </remarks>
    public string Label => _check.Offering ? "Update available" : "Update";

    /// <summary>
    /// Runs on every frame this section is NOT drawn - folded away, or on another page.
    /// </summary>
    /// <remarks>
    /// This is where the check actually gets its tick, and it has to be here rather than in
    /// the draw: the section is folded shut for the whole of an ordinary session, so a check
    /// that only ran while somebody was looking at it would run once at startup and never
    /// again. A four-hour map session would never learn that the build fixing this patch's
    /// offsets went out two hours ago.
    ///
    /// It costs a timestamp comparison per frame, and starts a request twice a day.
    /// </remarks>
    public void Idle() => _check.RefreshIfStale();

    /// <summary>Draws the tab's content.</summary>
    public void DrawTab()
    {
        _check.RefreshIfStale();

        DrawOutcome();

        ImGuiText.Mono(DimText, $"running   {_check.Local.Describe()}");

        if (!_check.Enabled)
        {
            ImGuiText.Wrapped(DimText,
                "Update checking is switched off. The config window's Update tab is where it "
                + "goes back on.");
            return;
        }

        ImGuiText.Wrapped(
            _check.Verdict switch
            {
                UpdateVerdict.Available => NewsText,
                UpdateVerdict.UpToDate => GoodText,
                UpdateVerdict.Failed => WarnText,
                _ => DimText,
            },
            ImGuiText.Escape(_check.Status));

        if (_check.Busy)
        {
            ImGuiText.Wrapped(DimText, "asking GitHub...");
        }

        ImGui.Spacing();
        if (ImGui.Button("Check Now"))
        {
            _check.Refresh();
        }

        if (_check.Verdict != UpdateVerdict.Available || _check.Newest is not ReleaseInfo release)
        {
            return;
        }

        ImGui.SameLine();
        DrawButtons(release);
        DrawProgress();

        ImGui.Separator();
        ImGuiText.Mono(DimText, $"{release.Name}   {release.DownloadSize / (1024.0 * 1024):F0} MB");

        if (release.Notes.Length == 0)
        {
            return;
        }

        // The changelog, in a box of its own so a long one cannot push the buttons off the
        // page. Mono because it is markdown written for a release page - it has lists and
        // shas in it, and neither survives a proportional face lined up by spaces.
        try
        {
            if (ImGui.BeginChild("update-notes", new Vector2(0f, 0f), ImGuiChildFlags.Borders))
            {
                ImGuiText.MonoWrapped(OverlayInk.Ink, release.Notes);
            }
        }
        finally
        {
            // Paired with BeginChild whatever it returned, and in a finally: the notes are a
            // string that arrived over the network, so this is exactly the draw where an
            // exception would leave ImGui's stack unbalanced.
            ImGui.EndChild();
        }
    }

    /// <summary>The notice about the update that has already happened.</summary>
    private void DrawOutcome()
    {
        if (Outcome.Length == 0)
        {
            return;
        }

        if (Outcome == "updated")
        {
            ImGuiText.Wrapped(GoodText, $"Updated to {ImGuiText.Escape(OutcomeVersion)}. This is the new build.");
        }
        else
        {
            ImGuiText.Wrapped(BadText,
                "The last update did NOT apply - this is still the old build. "
                + ImGuiText.Escape(_installer.LogPath) + " says why.");
        }

        ImGui.Separator();
    }

    private void DrawButtons(ReleaseInfo release)
    {
        switch (_installer.Step)
        {
            case UpdateStep.Downloading:
            case UpdateStep.Extracting:
                ImGuiText.Mono(DimText, _installer.Status);
                break;

            case UpdateStep.Ready:
                if (ImGui.Button("Install and Restart"))
                {
                    _install();
                }

                break;

            default:
                if (ImGui.Button($"Download ({release.DownloadSize / (1024.0 * 1024):F0} MB)"))
                {
                    _download();
                }

                ImGui.SameLine();
                if (ImGui.Button("Not This One"))
                {
                    _skip();
                }

                break;
        }
    }

    private void DrawProgress()
    {
        if (_installer.Step == UpdateStep.Failed)
        {
            ImGuiText.Wrapped(BadText, ImGuiText.Escape(_installer.Status));
            return;
        }

        if (_installer.Step is not (UpdateStep.Downloading or UpdateStep.Extracting or UpdateStep.Ready))
        {
            return;
        }

        // A fraction of zero draws an empty bar rather than a wrong one: the size is only
        // unknown when neither the release nor the response header carried it, which is a
        // state worth showing honestly rather than animating over.
        ImGui.ProgressBar(
            (float)_installer.Fraction,
            new Vector2(-1f, 0f),
            _installer.Total > 0
                ? $"{_installer.Received / (1024.0 * 1024):F0} / {_installer.Total / (1024.0 * 1024):F0} MB"
                : $"{_installer.Received / (1024.0 * 1024):F0} MB");

        if (_installer.Step == UpdateStep.Ready)
        {
            ImGuiText.Wrapped(GoodText, ImGuiText.Escape(_installer.Status));
        }
    }
}
