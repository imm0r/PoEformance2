using System.Numerics;
using System.Runtime.Versioning;
using System.Text;
using ImGuiNET;
using PoEformance.Features;

namespace PoEformance.Overlay;

/// <summary>
/// The tool's one window: a tab per PAGE, and a page may hold several tools.
/// </summary>
/// <remarks>
/// One window rather than one per tool, because the tool had grown ten floating windows and a
/// checkbox for each in the status readout - a desktop's worth of window management drawn over
/// a game that wants the screen. A tab bar keeps every tool one click away and puts "where did
/// that window go" out of the vocabulary: it is always in the same place.
///
/// AND A PAGE PER SUBJECT, rather than a tab per tool, which is the second round of the same
/// problem. Fourteen tabs do not fit on a bar, so the bar scrolled - and a tab you have to
/// scroll to find is a window you have to go looking for wearing a different hat. Tools that
/// answer one subject now share a page: the damage figure and the projectiles are both "what
/// is my build doing", the entity browser and the dissector are both "what is that thing".
///
/// A page with several tools is an ACCORDION: every section gets a collapsing header and the
/// first opens by default. The headers therefore all sit together at the top, so the second
/// tool is one click away rather than a scroll away - which is the trap the obvious
/// arrangement falls into, since the tool leading a page is usually the tall one. Folded, a
/// section costs a single line; opened, two of them sit one above the other, which is the
/// arrangement the entity browser and the dissector always wanted.
///
/// A page holding ONE tool has no header at all. A header saying what the tab already says is
/// a line of chrome and a click, for nothing.
///
/// A tool registers a section and TWO callbacks: what to draw while it is on screen, and what
/// to do while it is not. The second exists because these tools do things off-screen - several
/// drive an inspector on the reader thread, which must be told to stop reading when nobody is
/// looking, and the editors save settled changes, which must happen even when the change was
/// made just before switching away. A collapsed header counts as not drawn, so a tool folded
/// away is idle exactly as a tool on another tab is.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ToolTabs
{
    private sealed record Section(int Order, string Id, string Label, Action Draw, Action? Idle)
    {
        /// <summary>A label read per frame, for a header that has news to carry.</summary>
        /// <remarks>
        /// What the <c>###id</c> naming below was already built for. The registered label is
        /// still there and is what a tool is called; this is the same tool saying something
        /// about itself in the one place somebody is guaranteed to see it without opening it.
        /// </remarks>
        public Func<string>? Live { get; init; }
    }

    private sealed class Page(string id, string label)
    {
        public string Id { get; } = id;
        public string Label { get; } = label;
        public List<Section> Sections { get; } = [];
        public int Order => Sections.Count > 0 ? Sections[0].Order : int.MaxValue;
    }

    private readonly List<Page> _pages = [];

    // The pages somebody took off the bar. A registered tool stays registered - its idle
    // callback keeps running, its jumps keep working - the tab is just not offered, which is
    // what "I never use this one" actually asks for. Ids rather than Page references because
    // the settings apply before every page has registered.
    private readonly HashSet<string> _hidden = [];

    // The page to force in front on the next frame the bar draws, and the section to unfold
    // when it gets there. Kept until then rather than for exactly one frame, because the
    // window can be collapsed when the request is made - F8 picks an element with the tools
    // on another tab - and a request dropped on an undrawn frame would make surfacing a tool
    // work only sometimes.
    private string? _bringToFront;
    private string? _unfold;

    /// <summary>The id this window's lock and click-through are filed under.</summary>
    /// <remarks>
    /// The STATUS window's id, because this is now that window: the live readout is its first
    /// page. Keeping the id means the pinning and click-through somebody set on it survive,
    /// and it is the id the appearance list already offers.
    /// </remarks>
    public const string ChromeId = WindowChrome.StatusId;

    /// <summary>Whether this window is pinned in place or handed to the mouse.</summary>
    public WindowChrome Chrome { get; set; } = new();

    /// <summary>How solid this window is drawn, as the user set it.</summary>
    /// <remarks>
    /// Read at Begin rather than copied into a field on a change, so a slider dragged in the
    /// Appearance page is seen while it is being dragged - which is the only way anybody can
    /// judge how see-through is see-through enough.
    /// </remarks>
    public InterfaceStyle Interface { get; set; } = InterfaceStyle.Default;

    /// <summary>Whether anything registered a page, so an empty window is never offered.</summary>
    public bool Any => _pages.Count > 0;

    /// <summary>Fires when the set of hidden pages changes, so it can be written down.</summary>
    public Action? HiddenChanged { get; set; }

    /// <summary>
    /// The strip of live facts drawn above the tabs, on every page.
    /// </summary>
    /// <remarks>
    /// A callback rather than the facts themselves, because this class knows nothing about the
    /// game and should not start: what goes on the strip is whatever the overlay is currently
    /// reading, and the overlay is the thing holding it. See <see cref="StatusBar"/> for why
    /// there is a strip at all - and for why the window no longer resizes itself.
    /// </remarks>
    public Action? Header { get; set; }

    /// <summary>The hidden page ids, sorted so the settings file is stable.</summary>
    public string[] Hidden() => _hidden.Order(StringComparer.Ordinal).ToArray();

    /// <summary>Replaces the hidden set with what a settings file says.</summary>
    /// <remarks>
    /// Not validated against the registered pages: settings are applied while the tools are
    /// still being wired up, so a page hidden here may well register a moment later - and an
    /// id no page ever claims simply never matters.
    /// </remarks>
    public void ApplyHidden(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        _hidden.Clear();
        foreach (string id in ids)
        {
            _hidden.Add(id);
        }
    }

    /// <summary>
    /// How wide and tall the window opens the FIRST time, and never again.
    /// </summary>
    /// <remarks>
    /// Roomy enough for the widest content, which is the dissector - but it is a starting
    /// point, not a size the window is held at. See <see cref="Render"/>: the size belongs to
    /// whoever dragged the corner last.
    /// </remarks>
    private static readonly Vector2 ToolSize = new(940f, 620f);

    /// <summary>
    /// Whether the readout page is in front, which decides only how see-through the window is.
    /// </summary>
    /// <remarks>
    /// IT USED TO DECIDE THE SIZE TOO, and that was the window's worst habit. The readout page
    /// auto-sized to its own dozen lines and every other page was forced to
    /// <see cref="ToolSize"/> on the frame it arrived - so glancing at the readout and coming
    /// back THREW AWAY whatever size the window had been dragged to. Anybody who had sized the
    /// dissector to fit beside their game re-did it every time, and there was no way to keep a
    /// size at all: the auto-resize flag also takes the resize grip off, so on the readout the
    /// window could not be dragged, and off it the drag was overwritten.
    ///
    /// The live facts are a strip above the tabs now (see <see cref="Header"/>), so no page
    /// needs to be small, so no page needs to set a size. The window has ONE size and it is the
    /// user's. What is still worth switching is the OPACITY - the readout is looked past at a
    /// glance during a fight and a tool is read - and that is all this flag does now.
    /// </remarks>
    private bool _onReadout = true;

    /// <summary>
    /// Registers a tool. Without <paramref name="page"/> the tool is a page of its own.
    /// </summary>
    /// <remarks>
    /// An explicit order rather than registration order, because registration order is
    /// whatever sequence the app wires features up in - a fact about the code, not a decision
    /// about the interface. The play-time tools sit left of the reverse-engineering ones, and
    /// that should survive any refactor of the wiring. A page takes the order of its first
    /// section, so where a page sits is decided by the tool that leads it.
    ///
    /// Spaced in tens, so a tool can be put between two others without renumbering them.
    /// </remarks>
    /// <param name="draw">The section's content, drawn while it is on screen.</param>
    /// <param name="idle">Runs on every frame the content is NOT drawn.</param>
    /// <param name="page">The page to join. Defaults to a page of this tool's own.</param>
    /// <param name="pageLabel">
    /// What that page is called. Only the first registration for a page needs it; later ones
    /// join whatever is already there.
    /// </param>
    /// <param name="live">
    /// A label read every frame, replacing <paramref name="label"/> on the section header.
    /// For a tool with something to announce while it is folded away.
    /// </param>
    public void Add(
        int order,
        string id,
        string label,
        Action draw,
        Action? idle = null,
        string? page = null,
        string? pageLabel = null,
        Func<string>? live = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(label);
        ArgumentNullException.ThrowIfNull(draw);

        if (_pages.Any(p => p.Sections.Any(s => s.Id == id)))
        {
            throw new ArgumentException($"a tool called '{id}' is already registered", nameof(id));
        }

        string pageId = page ?? id;
        Page? found = _pages.Find(p => p.Id == pageId);
        if (found is null)
        {
            found = new Page(pageId, pageLabel ?? label);
            _pages.Add(found);
        }

        int at = found.Sections.FindIndex(s => s.Order > order);
        var made = new Section(order, id, label, draw, idle) { Live = live };
        if (at < 0)
        {
            found.Sections.Add(made);
        }
        else
        {
            found.Sections.Insert(at, made);
        }

        // By the page's own order, which its first section decides - so a tool registered late
        // can still lead a page and pull it left.
        _pages.Sort((left, right) => left.Order.CompareTo(right.Order));
    }

    /// <summary>Brings a tool to the front, by its own id or by its page's.</summary>
    /// <remarks>
    /// What makes a tool reachable from elsewhere: the entity browser sends an address to the
    /// dissector, F8 picks an element into the interface browser. Naming a SECTION unfolds it
    /// as well as selecting its page, because the two now differ - a page can be in front
    /// while the tool somebody asked for is folded away on it.
    /// </remarks>
    public void Show(string id)
    {
        Page? page = _pages.Find(p => p.Id == id)
                     ?? _pages.Find(p => p.Sections.Any(s => s.Id == id));

        _bringToFront = page?.Id ?? id;
        _unfold = id;

        // A jump wins over a hiding: F8 must surface the interface tree whether or not its
        // tab is on the bar. Hidden-and-unreachable would make every "open this tool"
        // affordance work only sometimes, which reads as the tool being broken.
        if (page is not null && _hidden.Remove(page.Id))
        {
            HiddenChanged?.Invoke();
        }
    }

    /// <summary>Draws the window.</summary>
    public void Render()
    {
        if (!Any)
        {
            return;
        }

        // Out of the way while one of the game's panels is under this window - the passive tree
        // and the stash are what the player is looking at, and a readout across them is right
        // information in the way. The idles still run: a tool told nobody is looking is exactly
        // what a hidden window means, and the inspectors behind these tabs must stop reading.
        if (Chrome.Covered(ChromeId))
        {
            RunIdles(null);
            return;
        }

        ImGui.SetNextWindowPos(new Vector2(20f, 20f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(ToolSize, ImGuiCond.FirstUseEver);

        // See-through while it is the readout, solid while it is a tool. The readout sits in
        // the corner during a fight and wants to be out of the way; a table of struct bytes
        // with the game showing through it is unreadable, and squinting is not a trade worth
        // making for a window somebody deliberately opened.
        //
        // BOTH ARE THE USER'S TO SET, because the trade is between reading this and seeing the
        // game, and where that line falls depends on the screen, the resolution and how far
        // away they sit. It used to be 0.7 and 1 written here, and 0.7 of near-black over a
        // hideout at noon is a panel with foliage inside the letters.
        ImGui.SetNextWindowBgAlpha(
            _onReadout ? Interface.ReadoutOpacityOr : Interface.PanelOpacityOr);

        // Surfacing a tool must actually surface it: a jump into a collapsed window would
        // select the right tab inside a title bar.
        if (_bringToFront is not null)
        {
            ImGui.SetNextWindowCollapsed(false);
        }

        // NO SIZE FLAG AT ALL, which is the point: neither AlwaysAutoResize nor a size set per
        // page. ImGui then keeps whatever size the window has - which is the one the user
        // dragged it to, remembered across pages, across sessions and across a tool that wants
        // to be wide. See the note on _onReadout for what this replaces.
        bool expanded = ImGui.Begin("PoEformance", Chrome.Flags(ChromeId, ImGuiWindowFlags.NoFocusOnAppearing));

        // Before the contents and outside the expanded test, because a collapsed window is
        // still somewhere: its title bar is the ground it covers, and that is what the next
        // frame compares against the game's panels.
        Chrome.Measure(ChromeId);

        string? inFront = null;

        // End in a finally: an exception between Begin and End leaves ImGui's stack unbalanced
        // and the assert that follows takes the process down.
        try
        {
            if (expanded)
            {
                // Before the tabs, so they stop short of the icons. NOT closable, alone among
                // the windows: this one carries the live readout and every way back into the
                // tool, so a close button would be a button that hides the button.
                Chrome.TitleButtons(ChromeId);

                // ABOVE THE TABS, so it is on screen whichever page is in front - which is the
                // entire reason it exists. Outside DrawPages because it belongs to the window
                // rather than to any page, and because the tab bar's heading face must not
                // reach it.
                if (Header is not null)
                {
                    StatusBar.Begin();
                    Header();
                    StatusBar.End();
                }

                inFront = DrawPages();

                // LAST, after the tabs and their contents. The menu declines to open over a
                // control, and what is under the cursor is only known once the controls have
                // been submitted - asked first it would steal the right-click the colour
                // pickers in the Appearance page have their own use for.
                Chrome.Menu(ChromeId);
            }
        }
        finally
        {
            ImGui.End();
        }

        RunIdles(inFront);
    }

    /// <summary>Whether the readout page is the one in front.</summary>
    /// <remarks>
    /// The FIRST page, whatever it is called, rather than a name matched here. The window's
    /// leading page is its readout by construction - it is the one with the lowest order - and
    /// a rule keyed on "status" would quietly stop applying the day that page is renamed.
    /// </remarks>
    private bool OnReadout(string? inFront) => inFront is not null && inFront == _pages[0].Id;

    /// <summary>
    /// Draws the tab bar and the page in front, and says which page that was.
    /// </summary>
    /// <remarks>
    /// THE BAR CHOOSES; THE CONTENT IS DRAWN AFTER IT. Everywhere else a page's content sits
    /// between BeginTabItem and EndTabItem, which is the shape ImGui's own examples use - but
    /// the bar is set in the heading face, and a tab bar takes its height from the font in
    /// force when it BEGINS. Pushing that face around the bar with the content still inside
    /// would set every page in it; pushing it around the labels alone would leave the bar
    /// sized for the small face with the labels overflowing. Selecting inside the bar and
    /// drawing outside it is the arrangement where both are the size they should be.
    ///
    /// The same arrangement is what lets the bar have its own INK. ImGui has no colour of its
    /// own for a tab's label: TabItemEx pushes none, and the label reaches the draw list through
    /// TabItemLabelAndCloseButton, RenderTextEllipsis and RenderTextClippedEx, which asks for
    /// ImGuiCol_Text at the last step (checked in ImGui 1.91.6, the version bundled here). So
    /// the tint has to be pushed around the bar and taken off again before the page under it is
    /// drawn, which is precisely the span this method already brackets for the font.
    /// </remarks>
    private string? DrawPages()
    {
        // The request as it stood when the bar started drawing. A page's own content can file
        // the NEXT one - the entity browser's jump to the dissector runs inside its Draw - and
        // that request belongs to the next frame, not to the sweep that is already past it.
        string? bringing = _bringToFront;

        Page? front = null;

        OverlayFonts.PushHeading();
        ImGui.PushStyleColor(ImGuiCol.Text, OverlayInk.AccentInk);
        try
        {
            // Scrolling rather than squeezing when the bar runs out of room, with the popup
            // list as the way to see every page at once - squeezed-to-illegible titles are the
            // many-windows problem wearing a different hat.
            if (!ImGui.BeginTabBar(
                    "tools",
                    ImGuiTabBarFlags.Reorderable
                    | ImGuiTabBarFlags.TabListPopupButton
                    | ImGuiTabBarFlags.FittingPolicyScroll))
            {
                return null;
            }

            try
            {
                foreach (Page page in _pages)
                {
                    // A hidden page's tab is simply not offered - except the first, which is
                    // the readout and the way back into everything, this list's editor
                    // included. The hide list never offers it either; this guard is for a
                    // settings file that says otherwise.
                    if (_hidden.Contains(page.Id) && page != _pages[0])
                    {
                        continue;
                    }

                    ImGuiTabItemFlags flags = page.Id == bringing
                        ? ImGuiTabItemFlags.SetSelected
                        : ImGuiTabItemFlags.None;

                    // ###id so a label could carry live text without the tab becoming a new
                    // control - ImGui identity comes from the label, as the status readout's
                    // checkboxes learned the hard way.
                    if (!BeginTabItem($"{page.Label}###{page.Id}", flags))
                    {
                        continue;
                    }

                    front = page;
                    ImGui.EndTabItem();
                }
            }
            finally
            {
                ImGui.EndTabBar();
            }
        }
        finally
        {
            ImGui.PopStyleColor();
            OverlayFonts.PopHeading();
        }

        if (front is not null)
        {
            // The tab item used to be the page's id scope, and drawing outside it takes that
            // away: two pages with a control of the same name - and several have a "filter" -
            // would share one ImGui id, which is one scroll position and one open state
            // between them. The page's own id restores exactly what the tab item gave.
            ImGui.PushID(front.Id);
            try
            {
                DrawPage(front);
            }
            finally
            {
                ImGui.PopID();
            }
        }

        // AFTER the content, unlike before, because the content is what reads _unfold: the
        // jump that names a section has to survive long enough for that section to be drawn.
        // A request the content itself filed is a different one and stays for the next frame.
        if (_bringToFront == bringing)
        {
            _bringToFront = null;
            _unfold = null;
        }

        _onReadout = OnReadout(front?.Id);
        return front?.Id;
    }

    /// <summary>The page's content, inside a scroll region so the tab bar stays put.</summary>
    /// <remarks>
    /// The bar used to leave with the page: a page taller than the window scrolled as a
    /// WHOLE, tabs and all, so switching tabs from anywhere below the fold meant scrolling
    /// back to the top first - every time, on every tall page. With the content in a child
    /// window the scrollbar is the page's own, and the bar, the title and its icons never
    /// move.
    ///
    /// EVERY page, the readout included. The readout used to be the exception, because it was
    /// the page that sized the window to its content and a fill-what-remains child inside a
    /// window asking its content how big to be is a circle ImGui resolves as a box of nothing.
    /// No page sizes the window any more, so the exception is gone with it - and the readout
    /// gets what the other pages had all along: its own scrollbar, and a tab bar that stays put.
    /// </remarks>
    private void DrawPage(Page page)
    {
        try
        {
            // Its own id per page, so every page keeps its own scroll position across
            // switches.
            if (ImGui.BeginChild($"page-{page.Id}"))
            {
                DrawSections(page);
            }
        }
        finally
        {
            // In a finally, and unconditionally: EndChild pairs with BeginChild whatever it
            // returned, and an exception between the two leaves ImGui's stack unbalanced.
            ImGui.EndChild();
        }
    }

    /// <summary>Draws a page: one tool bare, several as an accordion.</summary>
    private void DrawSections(Page page)
    {
        if (page.Sections.Count == 1)
        {
            page.Sections[0].Draw();
            _drawn.Add(page.Sections[0].Id);
            return;
        }

        for (int i = 0; i < page.Sections.Count; i++)
        {
            Section section = page.Sections[i];

            // Named with ###id for the same reason the tabs are: the label could then carry
            // live text - a count, a name - without the header becoming a new control every
            // frame and forgetting whether it was open.
            //
            // The first opens by default and the rest do not, which is only about what greets
            // somebody arriving on the page. Everything after that is ImGui's own memory of
            // what they last had open, which is the right answer and not ours to overrule -
            // hence FirstUseEver rather than a state we keep.
            ImGui.SetNextItemOpen(i == 0, ImGuiCond.FirstUseEver);

            // Asked for by name: a jump from elsewhere - the entity browser handing an address
            // to the dissector - has to unfold the tool it named, or it lands on a page with
            // the answer folded away on it.
            if (section.Id == _unfold)
            {
                ImGui.SetNextItemOpen(true, ImGuiCond.Always);
            }

            // In the heading face, like the tab above it and the titled rules below: a page
            // that folds into several tools needs its fold lines to outrank their contents,
            // which is the whole reason the second size exists.
            if (!OverlayFonts.SectionHeader($"{section.Live?.Invoke() ?? section.Label}###{section.Id}"))
            {
                continue;
            }

            section.Draw();
            _drawn.Add(section.Id);
        }
    }

    /// <summary>Which sections drew this frame, so the rest can be told they did not.</summary>
    /// <remarks>
    /// A field rather than a return value because a page has many sections and only the ones
    /// that actually drew are known at the end - a folded header draws nothing and its tool
    /// has to go idle, exactly as a tool on another tab does.
    /// </remarks>
    private readonly HashSet<string> _drawn = [];

    /// <summary>Runs the idle callback of every section whose content was not drawn.</summary>
    private void RunIdles(string? inFront)
    {
        foreach (Page page in _pages)
        {
            foreach (Section section in page.Sections)
            {
                if (page.Id != inFront || !_drawn.Contains(section.Id))
                {
                    section.Idle?.Invoke();
                }
            }
        }

        _drawn.Clear();
    }

    /// <summary>Checkboxes for which pages sit on the bar, drawn wherever the caller puts it.</summary>
    /// <remarks>
    /// The answer to "several of these tabs I never use": the tool stays registered and its
    /// idle keeps running, the TAB is just not offered any more. Two pages are never offered
    /// for hiding: the FIRST, because the readout is the way back into everything - this
    /// list's own editor included - and the page named by <paramref name="except"/>, which
    /// is the page this list is drawn on: hiding the list with the list leaves no way to
    /// undo either. A hidden page is not gone - <see cref="Show"/> puts it back on the bar,
    /// so F8 and the browser-to-dissector handoff keep working.
    /// </remarks>
    /// <param name="except">The page hosting this list, which must not offer to hide itself.</param>
    public void DrawHideList(string except)
    {
        // One wrapping paragraph rather than two hand-broken lines - see OverlayLayout.Note.
        OverlayLayout.Note(
            "Unticked tabs leave the bar. They come back here - or by themselves, the moment"
            + " something jumps to them (F8, a handoff).");
        ImGui.Spacing();

        foreach (Page page in _pages)
        {
            if (page == _pages[0] || page.Id == except)
            {
                continue;
            }

            // ###id for the reason the tabs carry it: the checkbox must survive a relabel.
            bool shown = !_hidden.Contains(page.Id);
            if (!ImGui.Checkbox($"{page.Label}###tab-{page.Id}", ref shown))
            {
                continue;
            }

            if (shown)
            {
                _hidden.Remove(page.Id);
            }
            else
            {
                _hidden.Add(page.Id);
            }

            HiddenChanged?.Invoke();
        }
    }

    /// <summary>ImGui's BeginTabItem with flags but WITHOUT a close button.</summary>
    /// <remarks>
    /// ImGui.NET only exposes the flags parameter together with the ref-bool that puts a close
    /// button on every tab, and a close button is exactly what these tabs must not have: a
    /// closed tab needs a list somewhere to reopen it from, which is the checkbox-per-window
    /// arrangement this window exists to replace. The native call accepts null for that
    /// parameter, so it is made directly.
    /// </remarks>
    private static unsafe bool BeginTabItem(string label, ImGuiTabItemFlags flags)
    {
        int worst = Encoding.UTF8.GetMaxByteCount(label.Length) + 1;
        Span<byte> bytes = worst <= 128 ? stackalloc byte[128] : new byte[worst];
        int written = Encoding.UTF8.GetBytes(label, bytes);
        bytes[written] = 0;

        fixed (byte* text = bytes)
        {
            return ImGuiNative.igBeginTabItem(text, null, flags) != 0;
        }
    }
}
