using System.Numerics;
using System.Runtime.Versioning;
using System.Text;
using ImGuiNET;

namespace PoEformance.Overlay;

/// <summary>
/// The one window the tools live in, each on its own tab.
/// </summary>
/// <remarks>
/// One window rather than one per tool, because the tool had grown ten floating windows and
/// a checkbox for each in the status readout - a desktop's worth of window management drawn
/// over a game that wants the screen. A tab bar keeps every tool one click away and puts
/// "where did that window go" out of the vocabulary: it is always in the same place.
///
/// A tool registers a tab and TWO callbacks: what to draw while its tab is in front, and
/// what to do while it is not. The second exists because these tools do things off-screen -
/// several drive an inspector on the reader thread, which must be told to stop reading when
/// nobody is looking, and the editors save settled changes, which must happen even when the
/// change was made just before switching away. A tab that merely stopped being drawn would
/// leave both hanging.
///
/// Only the tab in front draws and only it is served, which is a change from the free
/// windows: two inspectors can no longer run at once. That is the trade tabs make, and it is
/// the right one here - the pairing that mattered, the entity browser handing an address to
/// the dissector, survives as a jump to the dissector's tab with the address already loaded.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ToolTabs
{
    private sealed record Tab(int Order, string Id, string Label, Action Draw, Action? Idle);

    private readonly List<Tab> _tabs = [];

    // The tab to force in front on the next frame the bar draws. Kept until then rather
    // than for exactly one frame, because the window can be hidden or collapsed when the
    // request is made - F8 picks an element with the window closed - and a request dropped
    // on an undrawn frame would make surfacing a tool work only sometimes.
    private string? _bringToFront;

    /// <summary>The id this window's lock and click-through are filed under.</summary>
    public const string ChromeId = "tools";

    /// <summary>Whether the window is on screen. The idle callbacks run either way.</summary>
    public bool Visible { get; set; }

    /// <summary>Whether this window is pinned in place or handed to the mouse.</summary>
    public WindowChrome Chrome { get; set; } = new();

    /// <summary>Whether anything registered a tab, so an empty window is never offered.</summary>
    public bool Any => _tabs.Count > 0;

    /// <summary>
    /// Registers a tool's tab. <paramref name="order"/> fixes its place in the bar.
    /// </summary>
    /// <remarks>
    /// An explicit order rather than registration order, because registration order is
    /// whatever sequence the app wires features up in - a fact about the code, not a
    /// decision about the interface. The play-time tools sit left of the reverse-engineering
    /// ones, and that should survive any refactor of the wiring.
    ///
    /// Spaced in tens, so a tab can be put between two others without renumbering them.
    /// </remarks>
    /// <param name="draw">The tab's content, drawn while it is the one in front.</param>
    /// <param name="idle">
    /// Runs on every frame the content is NOT drawn - the tab is behind another, or the
    /// window is closed or collapsed. This is where an inspector is told to stop reading
    /// and where a settings editor writes down a change made just before switching away.
    /// </param>
    public void Add(int order, string id, string label, Action draw, Action? idle = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(label);
        ArgumentNullException.ThrowIfNull(draw);

        if (_tabs.Any(tab => tab.Id == id))
        {
            throw new ArgumentException($"a tab called '{id}' is already registered", nameof(id));
        }

        int at = _tabs.FindIndex(tab => tab.Order > order);
        var made = new Tab(order, id, label, draw, idle);
        if (at < 0)
        {
            _tabs.Add(made);
        }
        else
        {
            _tabs.Insert(at, made);
        }
    }

    /// <summary>Opens the window with the given tab in front.</summary>
    /// <remarks>
    /// What makes a tab reachable from elsewhere in the tool: the entity browser sends an
    /// address to the dissector, F8 picks an element into the interface browser. Both were
    /// "set Visible on that window" before; now they are this.
    /// </remarks>
    public void Show(string id)
    {
        Visible = true;
        _bringToFront = id;
    }

    /// <summary>Draws the window, or runs every idle callback while it is closed.</summary>
    public void Render()
    {
        if (!Visible)
        {
            RunIdles(inFront: null);
            return;
        }

        // Roomy enough for the widest content (the dissector), because every tool now
        // shares one frame - the per-window sizes went with the windows.
        ImGui.SetNextWindowSize(new Vector2(940f, 620f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(new Vector2(100f, 100f), ImGuiCond.FirstUseEver);

        // Surfacing a tool must actually surface it: a jump into a collapsed window would
        // select the right tab inside a title bar.
        if (_bringToFront is not null)
        {
            ImGui.SetNextWindowCollapsed(false);
        }

        bool open = Visible;
        bool expanded = ImGui.Begin(
            "Tools", ref open, Chrome.Flags(ChromeId, ImGuiWindowFlags.NoFocusOnAppearing));

        string? inFront = null;

        // End in a finally: an exception between Begin and End leaves ImGui's stack
        // unbalanced and the assert that follows takes the process down.
        try
        {
            if (expanded)
            {
                // Before the tabs, and the close button declared so they stop short of it.
                Chrome.TitleButtons(ChromeId, closable: true);

                inFront = DrawTabs();

                // LAST, after the tabs and their contents. The menu declines to open over a
                // control, and what is under the cursor is only known once the controls have
                // been submitted - asked first it would steal the right-click the colour
                // pickers in the Appearance tab have their own use for.
                Chrome.Menu(ChromeId);
            }
        }
        finally
        {
            ImGui.End();
        }

        Visible = open;
        RunIdles(inFront);
    }

    /// <summary>Draws the tab bar, and says which tab's content was drawn.</summary>
    private string? DrawTabs()
    {
        // Scrolling rather than squeezing when the bar runs out of room, with the popup
        // list as the way to see every tab at once - squeezed-to-illegible tab titles are
        // the many-windows problem wearing a different hat.
        if (!ImGui.BeginTabBar(
                "tools",
                ImGuiTabBarFlags.Reorderable
                | ImGuiTabBarFlags.TabListPopupButton
                | ImGuiTabBarFlags.FittingPolicyScroll))
        {
            return null;
        }

        // The request as it stood when the bar started drawing. A tab's own content can file
        // the NEXT one - the entity browser's jump to the dissector runs inside its Draw -
        // and that request belongs to the next frame, not to the sweep that is already past
        // the tab it names.
        string? bringing = _bringToFront;

        string? inFront = null;
        try
        {
            foreach (Tab tab in _tabs)
            {
                ImGuiTabItemFlags flags = tab.Id == bringing
                    ? ImGuiTabItemFlags.SetSelected
                    : ImGuiTabItemFlags.None;

                // ###id so the label could carry live text without the tab becoming a new
                // control - ImGui identity comes from the label, as the status window's
                // checkboxes learned the hard way.
                if (!BeginTabItem($"{tab.Label}###{tab.Id}", flags))
                {
                    continue;
                }

                try
                {
                    tab.Draw();
                    inFront = tab.Id;
                }
                finally
                {
                    ImGui.EndTabItem();
                }
            }

            // The bar drew, so the request it started with was either applied just now or
            // named a tab that does not exist - done with it either way. One filed during
            // the sweep is a different request and stays for the next frame.
            if (_bringToFront == bringing)
            {
                _bringToFront = null;
            }
        }
        finally
        {
            ImGui.EndTabBar();
        }

        return inFront;
    }

    /// <summary>Runs the idle callback of every tab whose content was not drawn.</summary>
    private void RunIdles(string? inFront)
    {
        foreach (Tab tab in _tabs)
        {
            if (tab.Id != inFront)
            {
                tab.Idle?.Invoke();
            }
        }
    }

    /// <summary>ImGui's BeginTabItem with flags but WITHOUT a close button.</summary>
    /// <remarks>
    /// ImGui.NET only exposes the flags parameter together with the ref-bool that puts a
    /// close button on every tab, and a close button is exactly what these tabs must not
    /// have: a closed tab needs a list somewhere to reopen it from, which is the
    /// checkbox-per-window arrangement this window exists to replace. The native call
    /// accepts null for that parameter, so it is made directly.
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
