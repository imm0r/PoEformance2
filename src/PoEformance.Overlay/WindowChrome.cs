using System.Numerics;
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
/// OFFERED IN THREE PLACES, which is one decision rather than three: two icons in each
/// window's title bar beside its close button (<see cref="TitleButtons"/>), the same two on
/// its right-click menu (<see cref="Menu"/>), and every window at once in the Appearance tab
/// (<see cref="DrawList"/>). The icons are the state as well as the switch and cost a glance;
/// the menu is where a right-click looks; the list is the only one of the three a
/// click-through window has not taken away from itself.
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
    /// Both switches as icons in the title bar, immediately left of the close button.
    /// </summary>
    /// <param name="id">Which window's rule these belong to.</param>
    /// <param name="closable">
    /// Whether the caller passed a <c>ref open</c> to <c>ImGui.Begin</c>, so the icons stop
    /// short of the X. ImGui keeps that to itself - there is nothing to ask - so the caller
    /// has to say, and a caller that says the wrong thing draws over its own close button.
    /// </param>
    /// <remarks>
    /// THE MENU IS NOT DISCOVERABLE and the list is three clicks away. Nothing about a window
    /// says it can be pinned, and nothing says whether it already is; two icons beside the
    /// close button are the state and the switch at once, in the one strip of a window that
    /// is always present and never has content in it.
    ///
    /// DRAWING ITEMS OVER THE TITLE BAR takes two liberties that ImGui allows but does not
    /// advertise, and both are load-bearing:
    ///
    /// - THE CLIP RECT IS PUSHED OUT over the title bar first. Without it the buttons are not
    ///   merely invisible - <c>ItemAdd</c> discards anything that misses the content clip
    ///   rect, so they would take no clicks either, and the bug would look like dead icons.
    /// - AN ITEM OVER THE TITLE BAR IS WHAT SUPPRESSES THE TITLE BAR'S OWN HANDLING, by
    ///   design rather than by luck: ImGui begins a window drag only while nothing is hovered
    ///   and skips the double-click collapse when something was hovered last frame. Icons
    ///   painted straight into the draw list with a hand-rolled hit test would get neither,
    ///   and dragging the window would be what clicking "locked" did.
    ///
    /// Both of those come at a price paid by a window that sizes itself to its contents, and
    /// the two comments in the body are how it is kept to nothing worth seeing.
    ///
    /// Call it inside the <c>if (ImGui.Begin(...))</c> body: a collapsed window submits no
    /// items at all, which is also why a collapsed window shows the X and not these.
    /// </remarks>
    public void TitleButtons(string id, bool closable = false)
    {
        ImGuiStylePtr style = ImGui.GetStyle();

        // The font size, because that is what ImGui sizes its own close button by - matching
        // it is what makes the three read as one set rather than as ours bolted beside theirs.
        float size = ImGui.GetFontSize();
        float gap = style.ItemInnerSpacing.X;

        Vector2 corner = ImGui.GetWindowPos();
        float width = ImGui.GetWindowWidth();
        float bar = ImGui.GetFrameHeight();

        // WHERE THE STRIP ENDS, and the inset is arithmetic rather than taste. An item handed
        // to ImGui widens the window's MEASURED CONTENT wherever it was put, title bar
        // included, and an AlwaysAutoResize window refits itself to that measurement every
        // frame - so a strip anchored to the window's own right edge is a loop that feeds
        // itself. WindowPadding is exactly where the content stops, so a strip ending THERE
        // measures no wider than the content already did and the loop has a fixed point;
        // ending at the close button's FramePadding inset instead measures four pixels wider
        // every frame, and the status window walks off the side of the screen at sixty
        // pixels a second. With a close button the strip is further in than either and the
        // question does not arise.
        float right = corner.X + width - style.WindowPadding.X;
        if (closable)
        {
            right = corner.X + width - style.FramePadding.X - size - gap;
        }

        float left = right - (size * 2f) - gap;

        // Not onto the collapse arrow or the title. ImGui sizes its title text against its own
        // buttons and knows nothing of ours, so on a window too narrow for both the icons would
        // sit on top of the name - and the menu and the Appearance list are both still there.
        if (left < corner.X + style.FramePadding.X + size + gap)
        {
            return;
        }

        // WHETHER THE ICONS ARE ITEMS THIS FRAME, which is the rest of that same problem. The
        // inset above stops an auto-fitting window GROWING; what it cannot stop is the window
        // being held at its widest, because a measurement that reaches the content's edge also
        // stops the content shrinking away from it. So the hit areas exist only while the
        // pointer is in the title bar - the one moment nothing about the window's content is
        // changing width - and the window is back to fitting itself the instant the pointer
        // leaves. The ICONS are painted either way: they are the state as much as the switch,
        // and a lock that is only visible while pointed at says nothing.
        Vector2 mouse = ImGui.GetMousePos();
        bool live = mouse.X >= corner.X
            && mouse.X < corner.X + width
            && mouse.Y >= corner.Y
            && mouse.Y < corner.Y + bar;

        ImGui.PushClipRect(corner, new Vector2(corner.X + width, corner.Y + bar), false);
        Vector2 resume = ImGui.GetCursorPos();

        try
        {
            ImDrawListPtr draw = ImGui.GetWindowDrawList();
            var lockAt = new Vector2(left, corner.Y + style.FramePadding.Y);
            Vector2 throughAt = lockAt with { X = lockAt.X + size + gap };

            WindowRule rule = Of(id);

            if (Toggle($"##locked-{id}", lockAt, size, live, enabled: true, tip: LockedTip(rule.Locked)))
            {
                Set(id, rule with { Locked = !rule.Locked });

                // Re-read rather than paint the old state: the icon showing the click that has
                // just landed is the whole feedback, and a frame of the previous picture reads
                // as a button that did not work.
                rule = Of(id);
            }

            Padlock(draw, lockAt, size, Ink(rule.Locked, allowed: true), rule.Locked);

            bool allowed = Allows(id);
            if (Toggle($"##through-{id}", throughAt, size, live, allowed, ThroughTip(rule.ClickThrough, allowed)))
            {
                Set(id, rule with { ClickThrough = !rule.ClickThrough });
                rule = Of(id);
            }

            Pointer(draw, throughAt, size, Ink(rule.ClickThrough, allowed), rule.ClickThrough);
        }
        finally
        {
            // The cursor put back where the window's own content expects it, and the clip rect
            // popped in a finally for the same reason every Begin here is ended in one: leaving
            // either unbalanced takes the process down on ImGui's next assert.
            ImGui.SetCursorPos(resume);
            ImGui.PopClipRect();
        }
    }

    /// <summary>One title bar icon's hit area and its hover backing. True when it was clicked.</summary>
    /// <remarks>
    /// An invisible button rather than a drawn one, because what is wanted from ImGui here is
    /// the HIT TEST and the hover state, not its frame - the icon underneath is the button as
    /// far as anybody looking at it is concerned.
    /// </remarks>
    /// <param name="live">
    /// False while the pointer is nowhere near, which submits nothing at all. See the note in
    /// <see cref="TitleButtons"/> about what an item in a title bar costs a window that fits
    /// itself to its contents.
    /// </param>
    private static bool Toggle(string tag, Vector2 at, float size, bool live, bool enabled, string tip)
    {
        if (!live)
        {
            return false;
        }

        ImGui.SetCursorScreenPos(at);
        bool clicked = ImGui.InvisibleButton(tag, new Vector2(size, size)) && enabled;

        if (!ImGui.IsItemHovered())
        {
            return clicked;
        }

        // The tooltip even where the switch is refused: "why can this one not" is exactly the
        // question a greyed-out icon raises, and the answer has nowhere else to go.
        ImGui.SetTooltip(tip);

        if (enabled)
        {
            ImGui.GetWindowDrawList().AddCircleFilled(
                at + new Vector2(size / 2f, size / 2f),
                size * 0.55f,
                ImGui.GetColorU32(ImGui.IsItemActive() ? ImGuiCol.ButtonActive : ImGuiCol.ButtonHovered),
                12);
        }

        return clicked;
    }

    /// <summary>What colour an icon is drawn in: lit when on, dim when off, fainter when refused.</summary>
    private static uint Ink(bool on, bool allowed)
        => !allowed
            ? ImGui.GetColorU32(ImGuiCol.TextDisabled, 0.5f)
            : ImGui.GetColorU32(on ? ImGuiCol.Text : ImGuiCol.TextDisabled);

    private static string LockedTip(bool locked)
        => locked
            ? "Locked - a stray click will not drag this window.\nClick to let it be moved again."
            : "Lock this window where it is.\nIt stays clickable; it just cannot be dragged.";

    private static string ThroughTip(bool through, bool allowed)
    {
        if (!allowed)
        {
            return "Click-through is not offered here.\nThis window is how the others are unlocked again.";
        }

        return through
            ? "Click-through is on - the game is getting the clicks.\nUndo it in Tools -> Appearance."
            : "Hand the mouse to the game: clicks land on what is behind this window.\n"
              + "It cannot be undone from here afterwards - the undo is Tools -> Appearance.";
    }

    /// <summary>A padlock, shut or open - the icon for "will not be dragged".</summary>
    /// <remarks>
    /// Drawn rather than typed, for the same reason the map markers are: the font is loaded
    /// with the English glyph range, so there is no padlock character to print.
    /// </remarks>
    private static void Padlock(ImDrawListPtr draw, Vector2 at, float size, uint colour, bool shut)
    {
        float line = MathF.Max(1f, size * 0.10f);
        float middle = at.X + (size * 0.5f);
        float body = at.Y + (size * 0.52f);

        draw.AddRectFilled(
            new Vector2(middle - (size * 0.28f), body),
            new Vector2(middle + (size * 0.28f), at.Y + (size * 0.86f)),
            colour,
            size * 0.08f);

        // The shackle. Shut is a half circle standing on the body; open is the same arc lifted
        // clear with its right leg gone. The LEFT leg stays where it is in both, so the two
        // states read as one thing swinging rather than as two unrelated pictures.
        float radius = size * 0.19f;
        float hinge = body - (size * 0.10f) - (shut ? 0f : size * 0.14f);

        draw.PathArcTo(
            new Vector2(middle, hinge), radius, MathF.PI, shut ? MathF.Tau : MathF.PI * 1.8f, 10);
        draw.PathStroke(colour, ImDrawFlags.None, line);

        draw.AddLine(new Vector2(middle - radius, hinge), new Vector2(middle - radius, body), colour, line);

        if (shut)
        {
            draw.AddLine(new Vector2(middle + radius, hinge), new Vector2(middle + radius, body), colour, line);
        }
    }

    /// <summary>A mouse pointer, solid or hollow - the icon for "the click reaches the game".</summary>
    private static void Pointer(ImDrawListPtr draw, Vector2 at, float size, uint colour, bool through)
    {
        float line = MathF.Max(1f, size * 0.09f);
        Vector2 tip = at + new Vector2(size * 0.30f, size * 0.13f);
        Vector2 heel = at + new Vector2(size * 0.30f, size * 0.77f);
        Vector2 wing = at + new Vector2(size * 0.70f, size * 0.50f);
        Vector2 waist = at + new Vector2(size * 0.50f, size * 0.63f);
        Vector2 tail = at + new Vector2(size * 0.61f, size * 0.88f);

        if (through)
        {
            // HOLLOW while the clicks pass through it, which is the state itself drawn: the
            // window is still on screen and no longer in the way of anything.
            draw.AddTriangle(tip, heel, wing, colour, line);
            draw.AddLine(waist, tail, colour, line);
        }
        else
        {
            draw.AddTriangleFilled(tip, heel, wing, colour);
            draw.AddLine(waist, tail, colour, line * 2f);
        }
    }

    /// <summary>
    /// The right-click menu that offers both, drawn inside the window it belongs to.
    /// </summary>
    /// <remarks>
    /// On the window rather than in a settings page because that is where the question is
    /// asked: somebody has just dragged this window into place and wants it to stay there.
    /// Kept alongside the title bar icons rather than replaced by them - it is where a
    /// right-click looks, it names both switches in words, and it reaches the one window with
    /// no title bar to put icons on.
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
        ImGui.TextDisabled("Both are also on each window's title bar, and its right-click menu.");
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
