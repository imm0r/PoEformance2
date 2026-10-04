using ImGuiNET;
using PoEformance.Features;

namespace PoEformance.Overlay;

/// <summary>
/// The button that sends the model pane's exports to the repository to be baked into the sheet.
/// </summary>
/// <remarks>
/// THE LAST HAND STEP, GONE. Exports reach the map on this machine the moment they are written;
/// getting them into assets/icons.png for every build was a command run in a checkout of the
/// repository - which needs the checkout, the SDK and git on the machine that plays. This sends
/// the 64-pixel cells and the entries to the boss-icons branch, and a workflow bakes them
/// against the repository as it stands and opens the pull request. See BossExportUpload for
/// why the bake happens there and not here.
///
/// ON A BUTTON AND NOT AFTER EVERY EXPORT, by request: an evening of posing is a dozen
/// exports and some of them get redone, and one press at the end is one commit and one bake.
///
/// THE TOKEN IS ASKED FOR HERE, ONCE, masked, and never shown again - see GitHubTokenStore,
/// which keeps it encrypted to the Windows user. Everything that touches the disk or the
/// network runs on a task; the frame only reads a string.
/// </remarks>
/// <param name="entries">The boss-icons.json the tool writes, or empty - BossIcons.Source.</param>
internal sealed class BossExportPanel(Func<string> entries)
{
    /// <summary>Longest token the field takes. Fine-grained tokens are about a hundred characters.</summary>
    private const uint TokenLength = 256;

    private const string TokenSaid =
        "a GitHub token that may write this repository, saved encrypted to your Windows user.\n"
        + "make one at github.com -> settings -> developer settings -> fine-grained tokens:\n"
        + $"repository {UpdateCheck.Owner}/{UpdateCheck.Repository}, permission Contents: read and write.";

    private const string SendSaid =
        "sends every exported 64 px cell and data/boss-icons.json to the boss-icons branch,\n"
        + "only what the branch does not have yet, in one commit. a workflow on GitHub then bakes\n"
        + "them into assets/icons.png and opens a pull request for you to merge.";

    private string _token = string.Empty;
    private bool _has = GitHubTokenStore.Has();
    private Task? _sending;
    private volatile string _said = string.Empty;

    /// <summary>The token field or the send button, and what the last press came to.</summary>
    public void Draw()
    {
        ImGui.TextDisabled("bake into the repository");

        if (!_has)
        {
            Ask();
        }
        else
        {
            Offer();
        }

        string said = _said;
        if (said.Length > 0)
        {
            ImGuiText.Wrapped(OverlayInk.Quiet, said);
        }
    }

    /// <summary>The masked token field and its save button.</summary>
    private void Ask()
    {
        ImGui.SetNextItemWidth(280f);
        ImGui.InputTextWithHint(
            "##github-token", "GitHub token (Contents: read and write)", ref _token, TokenLength,
            ImGuiInputTextFlags.Password);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(TokenSaid);
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(_token.Trim().Length == 0);
        bool save = ImGui.Button("save token##github-token-save");
        ImGui.EndDisabled();

        if (save)
        {
            _has = GitHubTokenStore.Save(_token, Dpapi.Protect);
            _said = _has ? "token saved, encrypted to this Windows user" : "the token could not be saved";

            // Out of memory the moment it is written down - nothing keeps the plain text around.
            _token = string.Empty;
        }
    }

    /// <summary>The send button, and the way to drop the token.</summary>
    private void Offer()
    {
        bool busy = _sending is { IsCompleted: false };
        ImGui.BeginDisabled(busy);
        bool send = ImGui.Button(busy ? "sending...##boss-export-send" : "send exports to be baked##boss-export-send");
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(SendSaid);
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(busy);
        bool forget = ImGui.SmallButton("forget token##github-token-forget");
        ImGui.EndDisabled();

        if (send)
        {
            Send();
        }

        if (forget)
        {
            _has = !GitHubTokenStore.Forget();
            _said = _has ? "the token could not be deleted" : "token forgotten";
        }
    }

    /// <summary>Gathers and sends on a task; the frame only ever reads the sentence it leaves.</summary>
    private void Send()
    {
        string from = entries();
        _said = "gathering the exports...";
        _sending = Task.Run(async () =>
        {
            try
            {
                string token = GitHubTokenStore.Load(Dpapi.Unprotect);
                if (token.Length == 0)
                {
                    _said = "the saved token could not be opened here - forget it and enter it again";
                    return;
                }

                var skipped = new List<string>();
                List<UploadFile> files = BossExportUpload.Gather(MonsterPortrait.Folder, from, skipped);
                string left = skipped.Count > 0 ? $" (left out: {string.Join(", ", skipped)})" : string.Empty;
                if (files.Count == 0)
                {
                    _said = $"nothing in {MonsterPortrait.Folder} to send{left}";
                    return;
                }

                using var writer = new GitHubWriter(token);
                UploadOutcome outcome = await writer.UploadAsync(files).ConfigureAwait(false);
                _said = outcome.Said + left + (outcome.Ok && outcome.Uploaded > 0 ? $"\n{writer.Runs}" : string.Empty);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _said = $"could not read the exports: {exception.Message}";
            }
        });
    }
}
