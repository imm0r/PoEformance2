using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;

namespace PoEformance.Overlay;

/// <summary>One window somebody can pin down, and what to call it when offering to.</summary>
/// <param name="Id">
/// The key the decision is filed under. NOT the title: a title is wording and gets reworded,
/// and a settings file that forgets where a window was because somebody improved a label is
/// a settings file nobody trusts.
/// </param>
/// <param name="Title">What it is called on screen.</param>
/// <param name="MayClickThrough">
/// False for the one window that is the way back. See <see cref="WindowChrome"/>.
/// </param>
public readonly record struct OverlayWindow(string Id, string Title, bool MayClickThrough = true);

/// <summary>
/// Lets each overlay window be pinned in place, or made invisible to the mouse.
/// </summary>
/// <remarks>
/// TWO THINGS THE OVERLAY CANNOT DO FOR ITSELF, both of which somebody wants once a window is
/// where they want it. A window parked over the middle of the screen gets dragged off by a
/// stray click; a readout that only wants to be READ still eats every click that lands on it.
///
/// CLICK-THROUGH IS ONE IMGUI FLAG and no Win32 work of ours, which is worth writing down
/// because it looks like it should be the other way round. ClickableTransparentOverlay flips
/// the whole overlay window between clickable and <c>WS_EX_TRANSPARENT</c> every frame, on
/// <c>io.WantCaptureMouse</c> alone. A window carrying <see cref="ImGuiWindowFlags.NoMouseInputs"/>
/// never sets that, so the overlay turns transparent to the mouse while the cursor is over it
/// and the click lands on the game. Trying to do this per window with hit-test regions would
/// be fighting the library for the same setting.
///
/// THERE HAS TO BE A WAY BACK. A click-through window cannot be right-clicked, so its own menu
/// is gone the moment it is switched on - the way to undo it is the list in the Appearance tab,
/// which is in the Tools window, which is opened from the status window. That chain is only a
/// way back if its last link cannot be broken, so the status window is the one window that
/// will not go click-through. It takes the lock like any other; the checkbox beside it is
/// disabled and says why. The alternative was a global hotkey, which is a new key to clash
/// with the game over, to solve a problem one exemption already solves.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowChrome
{
    /// <summary>The status window's id - see the note above about why it is special.</summary>
    public const string StatusId = "status";

    /// <summary>Every window that can be pinned down, in the order the list offers them.</summary>
    public static IReadOnlyList<OverlayWindow> Windows { get; } =
    [
        new(StatusId, "PoEformance", MayClickThrough: false),
        new("tools", "Tools"),
        new("poi", "Points of interest"),
        new("preload", "What loaded"),
    ];

    private readonly Dictionary<string, WindowRule> _rules = [];

    /// <summary>Called when a rule changed, so somebody else can write it down.</summary>
    public Action? Changed { get; set; }

    /// <summary>What a window was told. Free unless somebody said otherwise.</summary>
    public WindowRule Of(string id) => _rules.GetValueOrDefault(id) ?? WindowRule.Free;

    /// <summary>
    /// Replaces one window's rule, refusing click-through where it is not allowed.
    /// </summary>
    /// <remarks>
    /// Refused HERE rather than only in the control that offers it, because a settings file is
    /// hand-editable and the state this prevents is one nothing in the tool can undo.
    /// </remarks>
    public void Set(string id, WindowRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (rule.ClickThrough && !Allows(id))
        {
            rule = rule with { ClickThrough = false };
        }

        if (rule.Anything)
        {
            _rules[id] = rule;
        }
        else
        {
            _rules.Remove(id);
        }

        Changed?.Invoke();
    }

    /// <summary>Takes the saved rules, dropping any for windows that no longer exist.</summary>
    public void Apply(IReadOnlyDictionary<string, WindowRule> saved)
    {
        ArgumentNullException.ThrowIfNull(saved);

        _rules.Clear();
        foreach (OverlayWindow window in Windows)
        {
            if (saved.TryGetValue(window.Id, out WindowRule? rule) && rule is not null)
            {
                WindowRule kept = rule.ClickThrough && !window.MayClickThrough
                    ? rule with { ClickThrough = false }
                    : rule;

                if (kept.Anything)
                {
                    _rules[window.Id] = kept;
                }
            }
        }
    }

    /// <summary>The rules as they stand, for writing down. Null when nothing was pinned.</summary>
    /// <remarks>
    /// Null rather than an empty object so an untouched settings file gains no key at all -
    /// the same rule the groups and the preload rules follow.
    /// </remarks>
    public IReadOnlyDictionary<string, WindowRule>? Saved()
        => _rules.Count == 0 ? null : new Dictionary<string, WindowRule>(_rules);

    /// <summary>Whether this window is allowed to go click-through.</summary>
    public static bool Allows(string id)
    {
        foreach (OverlayWindow window in Windows)
        {
            if (window.Id == id)
            {
                return window.MayClickThrough;
            }
        }

        // Unknown ids are allowed: a window nobody registered is not the way back to anything.
        return true;
    }

    /// <summary>This window's own flags, plus whatever it has been told.</summary>
    public ImGuiWindowFlags Flags(string id, ImGuiWindowFlags own = ImGuiWindowFlags.None)
    {
        WindowRule rule = Of(id);

        if (rule.Locked)
        {
            own |= ImGuiWindowFlags.NoMove;
        }

        if (rule.ClickThrough)
        {
            // NoMove as well, and not for tidiness: a window the mouse cannot see cannot be
            // dragged anyway, and saying so keeps the flags describing the same window the
            // user is looking at rather than one that would move if it could.
            own |= ImGuiWindowFlags.NoMouseInputs | ImGuiWindowFlags.NoMove;
        }

        return own;
    }

    /// <summary>
    /// The right-click menu that offers both, drawn inside the window it belongs to.
    /// </summary>
    /// <remarks>
    /// On the window rather than in a settings page because that is where the question is
    /// asked: somebody has just dragged this window into place and wants it to stay there.
    /// The settings page has the same two switches for every window, which is what the one
    /// that has gone click-through needs.
    ///
    /// NoOpenOverItems, so right-clicking a control inside a window still belongs to that
    /// control. A menu that steals every right-click is a menu in the way.
    /// </remarks>
    public void Menu(string id)
    {
        if (!ImGui.BeginPopupContextWindow(
                $"##chrome-{id}",
                ImGuiPopupFlags.MouseButtonRight | ImGuiPopupFlags.NoOpenOverItems))
        {
            return;
        }

        try
        {
            WindowRule rule = Of(id);

            bool locked = rule.Locked;
            if (ImGui.MenuItem("Locked  (cannot be dragged)", string.Empty, ref locked))
            {
                Set(id, rule with { Locked = locked });
            }

            bool through = rule.ClickThrough;
            if (Allows(id))
            {
                if (ImGui.MenuItem("Click-through  (the game gets the click)", string.Empty, ref through))
                {
                    Set(id, rule with { ClickThrough = through });
                }

                if (through)
                {
                    ImGui.Separator();
                    ImGui.TextDisabled("undo it in Tools -> Appearance:");
                    ImGui.TextDisabled("right-clicking this window will not reach it");
                }
            }
            else
            {
                ImGui.MenuItem("Click-through", string.Empty, ref through, enabled: false);
                ImGui.TextDisabled("this window is how the others are unlocked");
            }
        }
        finally
        {
            // In a finally for the same reason every Begin here is: an exception between the
            // two leaves ImGui's stack unbalanced, and the assert that follows takes the
            // process down.
            ImGui.EndPopup();
        }
    }

    /// <summary>Every window and its two switches - the list that can undo a click-through.</summary>
    public void DrawList()
    {
        ImGui.TextDisabled("Locked stays clickable and will not be dragged.");
        ImGui.TextDisabled("Click-through hands the mouse to the game, buttons and all.");
        ImGui.TextDisabled("Both are also on each window's own right-click menu.");
        ImGui.Spacing();

        if (!ImGui.BeginTable("##window-rules", 3, ImGuiTableFlags.SizingFixedFit))
        {
            return;
        }

        try
        {
            ImGui.TableSetupColumn("window");
            ImGui.TableSetupColumn("locked");
            ImGui.TableSetupColumn("click-through");
            ImGui.TableHeadersRow();

            foreach (OverlayWindow window in Windows)
            {
                WindowRule rule = Of(window.Id);

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(window.Title);

                ImGui.TableNextColumn();
                bool locked = rule.Locked;
                if (ImGui.Checkbox($"##locked-{window.Id}", ref locked))
                {
                    Set(window.Id, rule with { Locked = locked });
                }

                ImGui.TableNextColumn();
                bool through = rule.ClickThrough;
                if (!window.MayClickThrough)
                {
                    ImGui.BeginDisabled();
                    ImGui.Checkbox($"##through-{window.Id}", ref through);
                    ImGui.EndDisabled();
                    ImGui.SameLine();
                    ImGui.TextDisabled("(the way back to this list)");
                }
                else if (ImGui.Checkbox($"##through-{window.Id}", ref through))
                {
                    Set(window.Id, rule with { ClickThrough = through });
                }
            }
        }
        finally
        {
            ImGui.EndTable();
        }
    }
}
