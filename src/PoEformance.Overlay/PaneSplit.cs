using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;

namespace PoEformance.Overlay;

/// <summary>
/// A draggable boundary between two panes sharing a window.
/// </summary>
/// <remarks>
/// ImGui has no splitter of its own, so every two-pane window here had a LEFT PANE OF FIXED
/// WIDTH - 360 pixels of entity list however wide the window was, names clipped on one side
/// of the line and room to spare on the other. The classic construction stands in: an
/// invisible button between the panes takes the drag, and the boundary moves with it.
///
/// The position is kept as a SHARE of the window rather than as pixels, so resizing the
/// window keeps the proportions instead of keeping the left pane. It lives for the session
/// and starts each launch at the pane's old default: where a window SITS is a decision and
/// is written down, but how the space inside it is dealt this hour is a working adjustment,
/// like a scroll position.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class PaneSplit(float share)
{
    /// <summary>How far the boundary can be pushed toward either edge.</summary>
    /// <remarks>
    /// Neither pane may vanish: a pane dragged to nothing takes its own grip's context with
    /// it, and the way back is knowing the invisible sliver is there - which is the
    /// click-through trap again, without the rescue icon.
    /// </remarks>
    private const float Least = 0.12f;
    private const float Most = 0.88f;

    private float _share = share;

    /// <summary>The whole width at the moment it was dealt, for turning a drag into a share.</summary>
    private float _width;

    /// <summary>The left pane's width right now. Ask just before beginning that pane.</summary>
    public float Left()
    {
        _width = ImGui.GetContentRegionAvail().X;
        return MathF.Max(1f, MathF.Round(Math.Clamp(_share, Least, Most) * _width));
    }

    /// <summary>The divider itself. Call BETWEEN the two panes, in place of the bare SameLine.</summary>
    public void Bar()
    {
        ImGui.SameLine(0f, 0f);

        float grip = MathF.Max(6f, ImGui.GetFontSize() * 0.45f);
        float height = MathF.Max(1f, ImGui.GetContentRegionAvail().Y);
        ImGui.InvisibleButton("##pane-split", new Vector2(grip, height));

        bool held = ImGui.IsItemActive();
        bool hovered = ImGui.IsItemHovered();

        if (held || hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
        }

        if (held && _width > 0f)
        {
            _share = Math.Clamp(_share + (ImGui.GetIO().MouseDelta.X / _width), Least, Most);
        }

        // Faint always and brighter under the mouse: a handle nobody can see is a handle
        // nobody finds, and one that never reacts does not read as a handle at all.
        Vector2 min = ImGui.GetItemRectMin();
        Vector2 max = ImGui.GetItemRectMax();
        float x = MathF.Round((min.X + max.X) / 2f);
        uint colour = held ? 0xB0FF_FFFFu : hovered ? 0x70FF_FFFFu : 0x28FF_FFFFu;
        ImGui.GetWindowDrawList().AddLine(
            new Vector2(x, min.Y), new Vector2(x, max.Y), colour, held || hovered ? 3f : 1f);

        ImGui.SameLine(0f, 0f);
    }
}
