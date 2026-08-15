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
public readonly record struct OverlayWindow(string Id, string Title);

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
/// the menu is where a right-click looks; the list is the one that reaches a window whose own
/// two routes have been switched off.
///
/// THERE HAS TO BE A WAY BACK, and it is the pointer icon itself. A click-through window
/// cannot be right-clicked, so its menu goes the moment it is switched on; the icon does not,
/// because <see cref="TitleButtons"/> asks the overlay to take the mouse back for exactly as
/// long as the cursor is inside that one square. The cursor's position is known throughout -
/// ClickableTransparentOverlay reads it with <c>GetCursorPos</c> rather than from window
/// messages, so the overlay knows where the mouse is even while it is transparent to it, which
/// is the fact this whole arrangement rests on.
///
/// THAT REPLACED AN EXEMPTION. The status window used to refuse click-through outright,
/// because the only undo was the Appearance list, which is in the Tools window, which is
/// opened from a checkbox in the status window - so a click-through status window took the
/// list with it. A switch that undoes itself where it sits needs no such carve-out, so there
/// is no longer a window that cannot go click-through. What the arrangement DOES need is that
/// the title bar be there to hold the icon, which is why a click-through window is also never
/// collapsed - see <see cref="Flags"/>.
///
/// The one window this does not cover is the preload panel, which has no title bar to put an
/// icon on. Its undo is still the Appearance list, and that stays reachable because any other
/// window can now hand itself back.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowChrome
{
    /// <summary>The main window's id.</summary>
    /// <remarks>
    /// Still called "status" in the settings file although the window has since swallowed the
    /// tools. The key is what a user's saved rule is filed under, and renaming one silently
    /// discards whatever they set - which is a worse price than a name that has aged.
    /// </remarks>
    public const string StatusId = "status";

    /// <summary>Every window that can be pinned down, in the order the list offers them.</summary>
    /// <remarks>
    /// "tools" is gone from here because the window is: every tool is a page of the main one
    /// now, so there is nothing left with its own title bar to pin. A rule somebody had
    /// already saved under that id stays in their file and does nothing, which is the same
    /// harmless end any retired window comes to.
    /// </remarks>
    public static IReadOnlyList<OverlayWindow> Windows { get; } =
    [
        new(StatusId, "PoEformance"),
        new("poi", "Points of interest"),
        new("preload", "What loaded"),
    ];

    private readonly Dictionary<string, WindowRule> _rules = [];

    /// <summary>Called when a rule changed, so somebody else can write it down.</summary>
    public Action? Changed { get; set; }

    /// <summary>What a window was told. Free unless somebody said otherwise.</summary>
    public WindowRule Of(string id) => _rules.GetValueOrDefault(id) ?? WindowRule.Free;

    /// <summary>Replaces one window's rule.</summary>
    public void Set(string id, WindowRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

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
            if (saved.TryGetValue(window.Id, out WindowRule? rule) && rule is not null && rule.Anything)
            {
                _rules[window.Id] = rule;
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

    /// <summary>This window's own flags, plus whatever it has been told.</summary>
    /// <remarks>
    /// Also settles the next window's COLLAPSED state, which is a side effect and is meant to
    /// be: this is called immediately before <c>ImGui.Begin</c> at every call site, which is
    /// the only moment a <c>SetNextWindow*</c> can be said at all.
    /// </remarks>
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

            // AND NEVER COLLAPSED, which is what keeps the way back from being lost. A
            // collapsed window submits no items and gets no title bar icons drawn, and a
            // click-through one cannot be un-collapsed by double-clicking it either - so the
            // pair is a window that shows nothing, takes no clicks and cannot be undone from
            // itself. Refused rather than merely discouraged, because the pair is reachable
            // without anybody choosing it: ImGui remembers collapsed state in its own ini and
            // our settings file is hand-editable, so both halves can arrive already set.
            own |= ImGuiWindowFlags.NoCollapse;
            ImGui.SetNextWindowCollapsed(false);
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
    /// A CLICK-THROUGH WINDOW GETS NEITHER, and takes no harm from it. Its items can never be
    /// hovered - ImGui's hover search skips a window carrying NoMouseInputs outright - so the
    /// icons are hit-tested by hand there instead; see <see cref="Reach"/>, which is the way
    /// back out of the state. Nothing is lost by the swap, because the drag and the collapse
    /// that an item exists to suppress went with the window's own inputs.
    ///
    /// Call it inside the <c>if (ImGui.Begin(...))</c> body. A collapsed window submits no
    /// items and so shows the X and not these - which is exactly why a click-through window is
    /// never collapsed (<see cref="Flags"/>): there, these ARE the X.
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
        // buttons and knows nothing of ours, so on a window too narrow for both, the icons would
        // sit on top of the name.
        //
        // Giving up here also gives up the way back out of click-through, which is why the
        // threshold is worth reading rather than trusting: it is about eighty pixels, and the
        // narrowest window registered here is the route picker at three hundred and sixty. A
        // window that ever could be that narrow needs the way back thought about again, not a
        // smaller icon.
        if (left < corner.X + style.FramePadding.X + size + gap)
        {
            return;
        }

        // WHETHER THE ICONS ARE ITEMS THIS FRAME, which is the rest of that same problem. The
        // inset above stops an auto-fitting window GROWING; what it cannot stop is the window
        // being held at its widest, because a measurement that reaches the content's edge also
        // stops the content shrinking away from it. So the items exist only while the pointer
        // is in the title bar - the one moment nothing about the window's content is changing
        // width - and the window is back to fitting itself the instant the pointer leaves. The
        // ICONS are painted either way: they are the state as much as the switch, and a lock
        // that is only visible while pointed at says nothing.
        //
        // The cursor is asked for here rather than taken from the hover state on purpose. It
        // is the one question that still has an answer on a click-through window, where ImGui
        // reports nothing hovered at all.
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

            // WHICH HIT TEST, and the split is forced rather than chosen. A click-through
            // window carries NoMouseInputs, and ImGui's hover search skips such a window
            // entirely - an item inside one can never be hovered, so an InvisibleButton there
            // is a control that is drawn and dead. Reach() does the test by hand instead and
            // buys the mouse back for the square it covers. It is also the path with nothing
            // to lose by it: the drag and the double-click collapse that an item exists to
            // suppress are already gone with the window's own inputs.
            bool through = rule.ClickThrough;

            if (through
                ? Reach(lockAt, size, mouse, LockedTip(rule.Locked))
                : Toggle($"##locked-{id}", lockAt, size, live, LockedTip(rule.Locked)))
            {
                Set(id, rule with { Locked = !rule.Locked });

                // Re-read rather than paint the old state: the icon showing the click that has
                // just landed is the whole feedback, and a frame of the previous picture reads
                // as a button that did not work.
                rule = Of(id);
            }

            Padlock(draw, lockAt, size, Ink(rule.Locked), rule.Locked);

            if (through
                ? Reach(throughAt, size, mouse, ThroughTip(through))
                : Toggle($"##through-{id}", throughAt, size, live, ThroughTip(through)))
            {
                Set(id, rule with { ClickThrough = !rule.ClickThrough });
                rule = Of(id);
            }

            Pointer(draw, throughAt, size, Ink(rule.ClickThrough), rule.ClickThrough);
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
    private static bool Toggle(string tag, Vector2 at, float size, bool live, string tip)
    {
        if (!live)
        {
            return false;
        }

        ImGui.SetCursorScreenPos(at);
        bool clicked = ImGui.InvisibleButton(tag, new Vector2(size, size));

        if (!ImGui.IsItemHovered())
        {
            return clicked;
        }

        ImGui.SetTooltip(tip);
        Backing(at, size, ImGui.IsItemActive());
        return clicked;
    }

    /// <summary>
    /// The same icon on a window the mouse has been told to ignore. True when it was clicked.
    /// </summary>
    /// <remarks>
    /// THIS IS THE WAY BACK OUT OF CLICK-THROUGH, so it is worth saying exactly what carries
    /// it. Two facts, both checked rather than assumed:
    ///
    /// - The overlay is clickable or transparent AS A WHOLE, and ClickableTransparentOverlay
    ///   decides which every frame from <c>io.WantCaptureMouse</c> alone. So asking for the
    ///   mouse back is not a per-window request at all - it is one flag, asserted for exactly
    ///   as long as the cursor is inside this one square, and dropped the moment it leaves.
    ///   Everywhere else on the window the click still lands on the game.
    /// - The cursor's position keeps arriving while the overlay is transparent, because that
    ///   library reads it with <c>GetCursorPos</c> and not from window messages. Were it
    ///   message-driven the position would freeze on the way out and the cursor could never
    ///   be seen arriving here - which is the failure that would make this a one-way switch
    ///   with no symptom other than an icon that does nothing.
    ///
    /// The BUTTON presses do come from messages, so they only start arriving once the overlay
    /// has been made clickable again - a frame or two after the cursor lands here, since the
    /// flag is read at the top of the next frame. Hence the press rather than the release:
    /// asking for a full click inside a square the size of a letter, when the first half of
    /// one may have gone to the game, is asking for a switch that works on the second try.
    /// </remarks>
    private static bool Reach(Vector2 at, float size, Vector2 mouse, string tip)
    {
        if (mouse.X < at.X || mouse.X >= at.X + size || mouse.Y < at.Y || mouse.Y >= at.Y + size)
        {
            return false;
        }

        ImGui.SetNextFrameWantCaptureMouse(true);

        ImGui.SetTooltip(tip);
        Backing(at, size, ImGui.IsMouseDown(ImGuiMouseButton.Left));
        return ImGui.IsMouseClicked(ImGuiMouseButton.Left);
    }

    /// <summary>The disc ImGui puts under its own title bar buttons when they are pointed at.</summary>
    private static void Backing(Vector2 at, float size, bool held)
        => ImGui.GetWindowDrawList().AddCircleFilled(
            at + new Vector2(size / 2f, size / 2f),
            size * 0.55f,
            ImGui.GetColorU32(held ? ImGuiCol.ButtonActive : ImGuiCol.ButtonHovered),
            12);

    /// <summary>What colour an icon is drawn in: lit when the switch is on, dim when off.</summary>
    private static uint Ink(bool on)
        => ImGui.GetColorU32(on ? ImGuiCol.Text : ImGuiCol.TextDisabled);

    private static string LockedTip(bool locked)
        => locked
            ? "Locked - a stray click will not drag this window.\nClick to let it be moved again."
            : "Lock this window where it is.\nIt stays clickable; it just cannot be dragged.";

    private static string ThroughTip(bool through)
        => through
            ? "Click-through is on - the game is getting the clicks.\nThis icon still takes yours. Click to take the window back."
            : "Hand the mouse to the game: clicks land on what is behind this window.\nThis icon stays live, and is how it comes back.";

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
            if (ImGui.MenuItem("Click-through  (the game gets the click)", string.Empty, ref through))
            {
                Set(id, rule with { ClickThrough = through });
            }

            if (through)
            {
                // Said on the way IN rather than only afterwards, because afterwards this menu
                // is gone - a click-through window cannot be right-clicked. Both routes named:
                // the pointer is the near one, and the list is what the window with no title
                // bar has instead.
                ImGui.Separator();
                ImGui.TextDisabled("right-clicking this window will not reach this menu again:");
                ImGui.TextDisabled("the pointer in its title bar hands the window back,");
                ImGui.TextDisabled("and Tools -> Appearance lists every window either way");
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
        ImGui.TextDisabled("Click-through hands the mouse to the game, buttons and all -");
        ImGui.TextDisabled("except the pointer icon in the window's own title bar, which");
        ImGui.TextDisabled("stays live and hands the window back. Both switches are there too.");
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
                if (ImGui.Checkbox($"##through-{window.Id}", ref through))
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
