using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.Ui;

namespace PoEformance.Overlay;

/// <summary>
/// Browses the game's interface tree, live, over the game.
/// </summary>
/// <remarks>
/// The reverse-engineering tool the rest of the UI work is built on: every feature that
/// touches the interface - the map radar, item slots, a panel that has to be open - starts by
/// finding an element and learning what identifies it. Doing that from a static dump means
/// re-dumping after every click; doing it live means opening a panel and watching the node
/// appear.
///
/// IN THE OVERLAY on purpose, not in the config window. What is being inspected is on the
/// game's screen, so the answer belongs on the game's screen: an element can be highlighted
/// exactly where it sits, and the cursor can point at the thing being asked about. A browser
/// in a second window would describe the interface from somewhere it cannot show.
///
/// It owns no reads. It states what it wants to see through <see cref="UiTreeInspector"/>,
/// which serves it on the reader thread, and draws whatever came back - so a search across
/// the whole interface costs the frame rate nothing.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class UiBrowserWindow
{
    /// <summary>F8 - press to capture whatever sits under the cursor.</summary>
    private const int PickKey = 0x77;

    /// <summary>Longest label a tree row shows before it is cut short.</summary>
    private const int MaxRowLabel = 56;

    private static readonly Vector4 DimText = new(0.62f, 0.65f, 0.72f, 1f);
    private static readonly Vector4 IdText = new(0.85f, 0.78f, 0.45f, 1f);
    private static readonly Vector4 HiddenText = new(0.55f, 0.45f, 0.45f, 1f);

    private readonly UiTreeInspector _inspector;
    private readonly KeyEdge _pickKey = new(PickKey);

    // The tree's open rows. This is the REQUEST - the reader reads children for exactly these
    // - so it is mirrored from what ImGui actually drew rather than tracked separately.
    private readonly HashSet<ulong> _expanded = [];

    // Rows a pick wants opened, for ONE frame. Separate from the set above because ImGui owns
    // the open state and only yields it to an explicit override: a row the user collapsed
    // earlier already has state, so anything short of "always, this frame" leaves the picked
    // element buried in a closed branch.
    private readonly HashSet<ulong> _forceOpen = [];

    private ulong _root;
    private ulong _selected;
    private ulong _hovered;
    private string _search = string.Empty;
    private int _searchSequence;
    private int _pickSequence;
    private int _lastAppliedPick;
    private bool _followCursor;
    private bool _highlightSelected = true;
    private Vector2 _cursor;

    public UiBrowserWindow(UiTreeInspector inspector)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        _inspector = inspector;
    }

    /// <summary>
    /// How the highlight rectangles look. Shared with the overlay, so one editor covers them.
    /// </summary>
    /// <remarks>
    /// Worth changing rather than a formality: these two exist to be TOLD APART from the
    /// panel they are drawn on, and a red box on a red panel identifies nothing.
    /// </remarks>
    public OverlayStyle Style { get; set; } = new();

    /// <summary>Whether the window is on screen. Nothing is read while it is not.</summary>
    public bool Visible { get; set; }

    /// <summary>Draws the window and publishes what it wants read next.</summary>
    /// <param name="client">The game's client area, for turning the cursor into a hit point.</param>
    public void Render(ClientRect client)
    {
        // The pick key is polled even while the window is closed - opening the browser to
        // find out what was under the cursor a moment ago is the wrong way round.
        Vector2? cursor = ScreenInput.CursorIn(client);
        if (_pickKey.Pressed() && cursor is Vector2 point)
        {
            _cursor = point;
            _pickSequence++;
            Visible = true;
        }

        if (!Visible)
        {
            _inspector.Request(UiTreeRequest.Idle);
            return;
        }

        if (_followCursor && cursor is Vector2 live)
        {
            _cursor = live;
            _pickSequence++;
        }

        UiTreeView view = _inspector.View;
        ApplyPick(view);

        _hovered = 0;
        DrawWindow(view);
        DrawHighlights(view);

        _inspector.Request(new UiTreeRequest(
            Enabled: true,
            Root: _root,
            Expanded: _expanded,
            Selected: _selected,
            Search: _search,
            SearchSequence: _searchSequence,
            PickSequence: _pickSequence,
            Cursor: _cursor));
    }

    /// <summary>
    /// Selects what the last pick found and opens the tree down to it.
    /// </summary>
    /// <remarks>
    /// Applied ONCE per pick rather than every frame: re-selecting continuously would make
    /// the tree unusable while following the cursor is on, and would undo any click the user
    /// makes afterwards.
    /// </remarks>
    private void ApplyPick(UiTreeView view)
    {
        if (view.PickSequence == _lastAppliedPick || view.PickChain.Count == 0)
        {
            return;
        }

        _lastAppliedPick = view.PickSequence;
        for (int i = 0; i < view.PickChain.Count - 1; i++)
        {
            _expanded.Add(view.PickChain[i]);
            _forceOpen.Add(view.PickChain[i]);
        }

        _selected = view.PickChain[^1];
    }

    private void DrawWindow(UiTreeView view)
    {
        ImGui.SetNextWindowSize(new Vector2(880, 520), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(new Vector2(80, 80), ImGuiCond.FirstUseEver);

        bool open = Visible;
        if (ImGui.Begin("UI Browser", ref open, ImGuiWindowFlags.NoFocusOnAppearing))
        {
            DrawToolbar(view);
            ImGui.Separator();

            float paneWidth = Math.Max(240f, ImGui.GetContentRegionAvail().X * 0.45f);
            if (ImGui.BeginChild("uib-left", new Vector2(paneWidth, 0), ImGuiChildFlags.Borders))
            {
                DrawLeftPane(view);
            }

            ImGui.EndChild();
            ImGui.SameLine();

            if (ImGui.BeginChild("uib-detail", Vector2.Zero, ImGuiChildFlags.Borders))
            {
                DrawDetails(view);
            }

            ImGui.EndChild();
        }

        ImGui.End();
        Visible = open;
    }

    private void DrawToolbar(UiTreeView view)
    {
        ImGui.SetNextItemWidth(220);
        if (ImGui.InputText("##uib-search", ref _search, 128, ImGuiInputTextFlags.EnterReturnsTrue))
        {
            _searchSequence++;
        }

        ImGui.SameLine();
        if (ImGui.Button("Search"))
        {
            _searchSequence++;
        }

        ImGui.SameLine();
        ImGui.TextColored(DimText, "id or displayed text");

        ImGui.SameLine();
        ImGui.Checkbox("Follow cursor", ref _followCursor);

        ImGui.SameLine();
        ImGui.Checkbox("Highlight", ref _highlightSelected);

        // Root controls. Re-rooting is what makes a deep subtree workable - the interface is
        // over a hundred children wide at the top, and scrolling past it to reach the same
        // panel repeatedly is most of the work otherwise.
        if (ImGui.Button("Game root"))
        {
            _root = 0;
            _expanded.Clear();
        }

        ImGui.SameLine();
        if (ImGui.Button("Root here") && _selected != 0)
        {
            _root = _selected;
            _expanded.Clear();
        }

        ImGui.SameLine();
        if (ImGui.Button("Up") && view.Selected is UiNode node && node.Parent != 0)
        {
            _root = node.Parent;
            _expanded.Clear();
        }

        ImGui.SameLine();
        ImGui.TextColored(DimText, Escape($"root 0x{view.Root:X} | {view.Status} | F8 picks under the cursor"));
    }

    private void DrawLeftPane(UiTreeView view)
    {
        if (!ImGui.BeginTabBar("uib-tabs"))
        {
            return;
        }

        if (ImGui.BeginTabItem("Tree"))
        {
            if (view.Nodes.Count == 0)
            {
                ImGui.TextColored(DimText, Escape(view.Status));
            }
            else
            {
                DrawNode(view, view.Root, 0);
            }

            // Spent: the override lasts exactly the frame that opens the picked path, so a
            // row the user closes afterwards stays closed.
            _forceOpen.Clear();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem($"Search ({view.SearchResults.Count})###uib-results"))
        {
            DrawSearchResults(view);
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    /// <summary>
    /// Draws one row and, when it is open, its children.
    /// </summary>
    /// <remarks>
    /// The open set is taken FROM ImGui rather than pushed into it: the return value of
    /// TreeNodeEx already says whether the row is open, and that is the same condition under
    /// which children get drawn. Keeping a separate flag and telling ImGui what to do with it
    /// gives two sources of truth that drift the moment the user clicks something.
    ///
    /// A row whose children have not arrived yet says so instead of looking like a leaf - the
    /// reader is a tick behind by design, and a silently empty node reads as "nothing in here".
    /// </remarks>
    private void DrawNode(UiTreeView view, ulong address, int depth)
    {
        if (address == 0 || depth > 32)
        {
            return;
        }

        if (!view.Nodes.TryGetValue(address, out UiNode? node))
        {
            ImGui.TextColored(DimText, $"0x{address:X}  (reading...)");
            return;
        }

        ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.OpenOnArrow
                                   | ImGuiTreeNodeFlags.OpenOnDoubleClick
                                   | ImGuiTreeNodeFlags.SpanAvailWidth;

        if (node.ChildCount == 0)
        {
            flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;
        }

        if (address == _selected)
        {
            flags |= ImGuiTreeNodeFlags.Selected;
        }

        // The pick opens the path to what it found, which the user never asked ImGui for.
        if (_forceOpen.Contains(address))
        {
            ImGui.SetNextItemOpen(true, ImGuiCond.Always);
        }

        ImGui.PushStyleColor(ImGuiCol.Text, node.Visible ? TextColour(node) : HiddenText);
        bool open = ImGui.TreeNodeEx((IntPtr)address, flags, Escape(Row(node)));
        ImGui.PopStyleColor();

        if (ImGui.IsItemHovered())
        {
            _hovered = address;
        }

        if (ImGui.IsItemClicked())
        {
            _selected = address;
        }

        if (node.ChildCount == 0)
        {
            return;
        }

        if (!open)
        {
            _expanded.Remove(address);
            return;
        }

        _expanded.Add(address);

        if (node.Children.Count == 0)
        {
            ImGui.TextColored(DimText, "reading...");
        }
        else
        {
            foreach (ulong child in node.Children)
            {
                DrawNode(view, child, depth + 1);
            }
        }

        ImGui.TreePop();
    }

    private void DrawSearchResults(UiTreeView view)
    {
        if (view.SearchResults.Count == 0)
        {
            ImGui.TextColored(DimText, Escape(
                view.SearchQuery.Length == 0
                    ? "Type a name or a piece of on-screen text, then press Enter."
                    : $"Nothing matched \"{view.SearchQuery}\"."));
            return;
        }

        foreach (UiNode node in view.SearchResults)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, node.Visible ? TextColour(node) : HiddenText);
            // ### rather than ##: the label is game text, and ImGui reads everything after
            // the first ## as the identity - so a caption that happens to contain one would
            // give two different elements the same id and make them one row.
            bool clicked = ImGui.Selectable($"{Row(node)}###{node.Address:X}", node.Address == _selected);
            ImGui.PopStyleColor();

            if (ImGui.IsItemHovered())
            {
                _hovered = node.Address;
            }

            if (clicked)
            {
                _selected = node.Address;
            }
        }
    }

    private void DrawDetails(UiTreeView view)
    {
        if (view.Selected is not UiNode node)
        {
            ImGui.TextColored(DimText, "Select an element, or press F8 over one in the game.");
            return;
        }

        Field("StringId", node.StringId.Length > 0 ? node.StringId : "(none)");

        // Wrapped, and shown in full: this is where the long captions the tree row had to cut
        // are actually readable, and a caption is often the only handle an element has.
        ImGui.PushTextWrapPos(0f);
        Field("Text", node.Text.Length > 0 ? node.Text : "(none)");
        ImGui.PopTextWrapPos();

        Field("Address", $"0x{node.Address:X}");
        ImGui.SameLine();
        Copy("copy##addr", $"0x{node.Address:X}");

        string path = view.SelectedPath.Count > 0
            ? string.Concat(view.SelectedPath.Select(i => $"[{i}]"))
            : "(not under the browsed root)";
        Field("Path", path);
        if (view.SelectedPath.Count > 0)
        {
            ImGui.SameLine();
            Copy("copy##path", path);
        }

        ImGui.Separator();

        // Both halves of visibility, because which one is false is the answer to "why is this
        // not on screen": its own flag, or an ancestor's.
        Field("Visible", node.Visible ? "yes" : node.SelfVisible ? "no - an ancestor is hidden" : "no - own flag");
        Field("Children", node.ChildCount.ToString());
        Field("Screen", $"{node.Position.X:F0}, {node.Position.Y:F0}   {node.Size.X:F0} x {node.Size.Y:F0}");
        Field("UI space", $"{node.UnscaledPosition.X:F1}, {node.UnscaledPosition.Y:F1}"
                          + $"   (relative {node.RelativePosition.X:F1}, {node.RelativePosition.Y:F1})");
        Field("Flags", $"0x{node.Flags:X8}   {DescribeFlags(node.Flags)}");
        Field("Scale", $"index {node.ScaleIndex}, multiplier {node.LocalMultiplier:F2}");
        Field("Parent", node.Parent != 0 ? $"0x{node.Parent:X}" : "(none)");

        if (node.ItemPtr != 0)
        {
            // Only meaningful on inventory and stash slots, so it is shown only when it is
            // there rather than as a permanent row of zeroes.
            Field("Item", $"0x{node.ItemPtr:X}");
            ImGui.SameLine();
            Copy("copy##item", $"0x{node.ItemPtr:X}");
        }
    }

    /// <summary>
    /// Draws a rectangle around the selected and hovered elements, on the game itself.
    /// </summary>
    /// <remarks>
    /// The point of putting the browser here. A tree row is a name; a rectangle on the panel
    /// it belongs to is the identification, and it is instant - hovering rows until the right
    /// box lights up is faster than reading any list of coordinates.
    /// </remarks>
    private void DrawHighlights(UiTreeView view)
    {
        if (!_highlightSelected)
        {
            return;
        }

        ImDrawListPtr draw = ImGui.GetForegroundDrawList();

        if (_hovered != 0 && _hovered != _selected && Style.Visible(StyleCatalogue.Keys.AidHovered))
        {
            Outline(draw, Find(view, _hovered), Style.Colour(StyleCatalogue.Keys.AidHovered), Style.Width(StyleCatalogue.Keys.AidHovered, 1.5f));
        }

        if (Style.Visible(StyleCatalogue.Keys.AidSelected))
        {
            Outline(draw, view.Selected, Style.Colour(StyleCatalogue.Keys.AidSelected), Style.Width(StyleCatalogue.Keys.AidSelected, 2f));
        }
    }

    private static UiNode? Find(UiTreeView view, ulong address)
    {
        if (view.Nodes.TryGetValue(address, out UiNode? node))
        {
            return node;
        }

        foreach (UiNode result in view.SearchResults)
        {
            if (result.Address == address)
            {
                return result;
            }
        }

        return null;
    }

    private static void Outline(ImDrawListPtr draw, UiNode? node, uint colour, float thickness)
    {
        if (node is null || node.Size.X <= 0 || node.Size.Y <= 0)
        {
            return;
        }

        draw.AddRect(node.Position, node.Position + node.Size, colour, 0f, ImDrawFlags.None, thickness);
    }

    /// <summary>One tree row: what identifies the element, and how many are under it.</summary>
    /// <remarks>
    /// The label is truncated because it can be a whole tooltip: elements carry their
    /// displayed text, and a paragraph of it in a tree row pushes every count off the pane.
    /// The detail pane shows the whole thing.
    /// </remarks>
    private static string Row(UiNode node)
    {
        string label = node.Label;
        if (label.Length > MaxRowLabel)
        {
            label = string.Concat(label.AsSpan(0, MaxRowLabel), "...");
        }

        return node.ChildCount > 0 ? $"{label}  ({node.ChildCount})" : label;
    }

    /// <summary>
    /// Doubles every percent sign, for the ImGui calls that are printf underneath.
    /// </summary>
    /// <remarks>
    /// TreeNodeEx and TextColored take a FORMAT string, and the text here comes from the
    /// game - "25% increased Damage" is an ordinary label in Path of Exile. Passed through
    /// unescaped, a %s in a caption makes the formatter read an argument that was never
    /// supplied, which is a crash on the render thread rather than a wrong string.
    ///
    /// TextUnformatted is used wherever a value can be shown as-is; this is for the calls
    /// where there is no such variant.
    /// </remarks>
    private static string Escape(string text) => text.Replace("%", "%%", StringComparison.Ordinal);

    private static Vector4 TextColour(UiNode node)
        => node.StringId.Length > 0 ? IdText : new Vector4(0.86f, 0.88f, 0.92f, 1f);

    private static string DescribeFlags(uint flags)
    {
        var parts = new List<string>(2);
        if ((flags & 0x800) != 0)
        {
            parts.Add("visible");
        }

        if ((flags & 0x400) != 0)
        {
            parts.Add("modifies position");
        }

        return parts.Count > 0 ? string.Join(", ", parts) : "-";
    }

    private static void Field(string label, string value)
    {
        ImGui.TextColored(DimText, label);
        ImGui.SameLine(110);
        ImGui.TextUnformatted(value);
    }

    private static void Copy(string id, string text)
    {
        if (ImGui.SmallButton(id))
        {
            ImGui.SetClipboardText(text);
        }
    }

}
