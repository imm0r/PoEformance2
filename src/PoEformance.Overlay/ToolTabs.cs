using System.Numerics;
using System.Runtime.Versioning;
using System.Text;
using ImGuiNET;

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
    private sealed record Section(int Order, string Id, string Label, Action Draw, Action? Idle);

    private sealed class Page(string id, string label)
    {
        public string Id { get; } = id;
        public string Label { get; } = label;
        public List<Section> Sections { get; } = [];
        public int Order => Sections.Count > 0 ? Sections[0].Order : int.MaxValue;
    }

    private readonly List<Page> _pages = [];

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

    /// <summary>Whether anything registered a page, so an empty window is never offered.</summary>
    public bool Any => _pages.Count > 0;

    /// <summary>How wide and tall the window opens when a tool is in front.</summary>
    /// <remarks>
    /// Roomy enough for the widest content, which is the dissector. The status page sizes
    /// itself instead - see <see cref="Render"/>.
    /// </remarks>
    private static readonly Vector2 ToolSize = new(940f, 620f);

    /// <summary>
    /// Whether the page in front sizes the window to its content.
    /// </summary>
    /// <remarks>
    /// The live readout is a dozen short lines and is meant to sit in the corner during a
    /// fight, so it auto-resizes as it always did. A tool wants room. Switching between them
    /// therefore switches the flag, and the frame that leaves the readout has to SAY how big
    /// to be - a window coming out of auto-resize otherwise keeps the small size it was just
    /// given, and the dissector opens into a letterbox.
    /// </remarks>
    private bool _sizedToContent = true;

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
    public void Add(
        int order,
        string id,
        string label,
        Action draw,
        Action? idle = null,
        string? page = null,
        string? pageLabel = null)
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
        var made = new Section(order, id, label, draw, idle);
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
    }

    /// <summary>Draws the window.</summary>
    public void Render()
    {
        if (!Any)
        {
            return;
        }

        ImGui.SetNextWindowPos(new Vector2(20f, 20f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(ToolSize, ImGuiCond.FirstUseEver);

        // See-through while it is the readout, solid while it is a tool. The readout sits in
        // the corner during a fight and wants to be out of the way; a table of struct bytes
        // with the game showing through it is unreadable, and squinting is not a trade worth
        // making for a window somebody deliberately opened.
        ImGui.SetNextWindowBgAlpha(_sizedToContent ? 0.7f : 1f);

        // Surfacing a tool must actually surface it: a jump into a collapsed window would
        // select the right tab inside a title bar.
        if (_bringToFront is not null)
        {
            ImGui.SetNextWindowCollapsed(false);
        }

        ImGuiWindowFlags own = ImGuiWindowFlags.NoFocusOnAppearing;
        if (_sizedToContent)
        {
            own |= ImGuiWindowFlags.AlwaysAutoResize;
        }
        else if (_leftTheReadout)
        {
            // The one frame that has to say a size, because it is the frame the auto-resize
            // flag comes off and ImGui would otherwise keep the readout's height.
            _leftTheReadout = false;
            ImGui.SetNextWindowSize(ToolSize, ImGuiCond.Always);
        }

        bool expanded = ImGui.Begin("PoEformance", Chrome.Flags(ChromeId, own));

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

    /// <summary>Whether the last frame was the auto-sized readout. See <see cref="_sizedToContent"/>.</summary>
    private bool _leftTheReadout;

    /// <summary>The page whose content sizes the window to itself, if it is in front.</summary>
    /// <remarks>
    /// The FIRST page, whatever it is called, rather than a name matched here. The window's
    /// leading page is its readout by construction - it is the one with the lowest order - and
    /// a rule keyed on "status" would quietly stop applying the day that page is renamed.
    /// </remarks>
    private bool SizesItself(string? inFront) => inFront is not null && inFront == _pages[0].Id;

    /// <summary>Draws the tab bar and the page in front, and says which page that was.</summary>
    private string? DrawPages()
    {
        // Scrolling rather than squeezing when the bar runs out of room, with the popup list
        // as the way to see every page at once - squeezed-to-illegible titles are the
        // many-windows problem wearing a different hat.
        if (!ImGui.BeginTabBar(
                "tools",
                ImGuiTabBarFlags.Reorderable
                | ImGuiTabBarFlags.TabListPopupButton
                | ImGuiTabBarFlags.FittingPolicyScroll))
        {
            return null;
        }

        // The request as it stood when the bar started drawing. A page's own content can file
        // the NEXT one - the entity browser's jump to the dissector runs inside its Draw - and
        // that request belongs to the next frame, not to the sweep that is already past it.
        string? bringing = _bringToFront;

        string? inFront = null;
        try
        {
            foreach (Page page in _pages)
            {
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

                try
                {
                    DrawSections(page);
                    inFront = page.Id;
                }
                finally
                {
                    ImGui.EndTabItem();
                }
            }

            // The bar drew, so the request it started with was either applied just now or
            // named a page that does not exist - done with it either way. One filed during the
            // sweep is a different request and stays for the next frame.
            if (_bringToFront == bringing)
            {
                _bringToFront = null;
                _unfold = null;
            }
        }
        finally
        {
            ImGui.EndTabBar();
        }

        bool sizes = SizesItself(inFront);
        _leftTheReadout = _sizedToContent && !sizes;
        _sizedToContent = sizes;
        return inFront;
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

            if (!ImGui.CollapsingHeader($"{section.Label}###{section.Id}"))
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
