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
/// window keeps the proportions instead of keeping the left pane.
///
/// WHETHER IT OUTLIVES THE SESSION IS THE CALLER'S, and it used to be settled here: a boundary
/// started every launch at its default, on the argument that how the space inside a window is
/// dealt this hour is a working adjustment like a scroll position. That holds for a window with
/// two panes and one obvious split. It does not hold for the monster book, which was reported
/// from the live client: three panes, a table whose columns somebody has widened to fit, and a
/// model pane sized to the monitor - that is a reading layout somebody sets up once, and having
/// it thrown away on every launch is the whole complaint. So a caller that wants it kept reads
/// <see cref="Share"/>, writes it down when <see cref="Settled"/> says a drag ended, and hands
/// it back through <see cref="Restore"/>. A caller that does not simply leaves all three alone
/// and gets exactly the old behaviour.
/// </remarks>
/// <param name="share">How much of the width the left pane starts with.</param>
/// <param name="name">
/// What tells this boundary from the others in the same window.
/// </param>
/// <remarks>
/// THE NAME IS NOT DECORATION, and leaving it out was a real bug. Every splitter used to submit
/// its grip as the literal "##pane-split", so two of them in one window got the SAME ImGui id -
/// and IsItemActive() only ever asks whether g.ActiveId equals the last item's id. Dragging the
/// facet rail's boundary therefore made the LIST's boundary report itself active too, and both
/// moved by the same delta. It is the kind of fault that reads as a mysterious layout jump rather
/// than as an id collision, and a third pane would have made it a three-way one.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class PaneSplit(float share, string name = "pane")
{
    /// <summary>This boundary's own id, built once rather than per frame.</summary>
    private readonly string _grip = $"##split-{name}";

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

    /// <summary>Whether the boundary was being dragged last frame, and whether that drag moved it.</summary>
    private bool _dragging;
    private bool _moved;

    /// <summary>How much of the width the left pane has, for a caller that writes it down.</summary>
    public float Share => _share;

    /// <summary>
    /// Told once when a drag of this boundary ends, having moved it.
    /// </summary>
    /// <remarks>
    /// WHEN THE DRAG ENDS AND NOT WHILE IT RUNS. The settings file is rewritten whole by whoever
    /// listens to this, and a boundary being dragged moves on every frame of the drag - sixty
    /// rewrites a second for one adjustment. A drag that ended where it started says nothing and
    /// is not reported.
    /// </remarks>
    public Action? Settled { get; set; }

    /// <summary>Puts back a share that was written down. Nonsense is ignored, so the default stands.</summary>
    public void Restore(double share)
    {
        if (double.IsFinite(share) && share > 0d)
        {
            _share = Math.Clamp((float)share, Least, Most);
        }
    }

    /// <summary>How wide the grip between two panes is.</summary>
    /// <remarks>
    /// PUBLIC BECAUSE A CALLER LAYING OUT ABOVE THE PANES NEEDS IT. The monster book puts each of
    /// its controls over the pane that control belongs to, and that row is submitted before any
    /// pane exists - so it has to work out where the boundaries will land, and a grip is part of
    /// the width they take. One formula rather than the number written twice.
    /// </remarks>
    public static float Grip => MathF.Max(6f, ImGui.GetFontSize() * 0.45f);

    /// <summary>The left pane's width for a given room, WITHOUT laying anything out.</summary>
    /// <remarks>
    /// APART FROM <see cref="Left"/> BECAUSE THAT ONE REMEMBERS THE ROOM, which is what turns a
    /// drag into a share. Called a second time in a frame from somewhere the room is different -
    /// which is exactly what a caller working out the layout above the panes would do - it would
    /// hand the drag the wrong number and the boundary would move at the wrong rate.
    /// </remarks>
    public float Would(float room)
        => MathF.Max(1f, MathF.Round(Math.Clamp(_share, Least, Most) * room));

    /// <summary>The left pane's width right now. Ask just before beginning that pane.</summary>
    public float Left()
    {
        _width = ImGui.GetContentRegionAvail().X;
        return Would(_width);
    }

    /// <summary>The divider itself. Call BETWEEN the two panes, in place of the bare SameLine.</summary>
    /// <param name="height">
    /// How tall the grip is. Zero asks for the rest of the room, which is right only where the
    /// panes beside it take the rest of the room too.
    /// </param>
    /// <remarks>
    /// THE GRIP HAS TO BE THE PANES' HEIGHT AND NOT THE ROOM'S, and getting that wrong is quiet.
    /// A grip is invisible and ImGui gives a line the height of the TALLEST item on it, so a grip
    /// reaching past the panes beside it simply makes the row taller than any of them - with
    /// nothing on screen to say so. That swallowed the monster book's footer whole: the panes held
    /// a line back for it, the grip took the line anyway, and the footer landed under the bottom
    /// of a window which then grew a scrollbar nobody had asked for.
    ///
    /// The parameter has a default because the other windows using this DO fill the room, and for
    /// them asking is the right answer - it is only a caller keeping something back that has to
    /// say so.
    /// </remarks>
    public void Bar(float height = 0f)
    {
        ImGui.SameLine(0f, 0f);

        float tall = height > 0f ? height : MathF.Max(1f, ImGui.GetContentRegionAvail().Y);
        ImGui.InvisibleButton(_grip, new Vector2(Grip, tall));

        bool held = ImGui.IsItemActive();
        bool hovered = ImGui.IsItemHovered();

        if (held || hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
        }

        if (held && _width > 0f && ImGui.GetIO().MouseDelta.X != 0f)
        {
            _share = Math.Clamp(_share + (ImGui.GetIO().MouseDelta.X / _width), Least, Most);
            _moved = true;
        }

        if (_dragging && !held)
        {
            if (_moved)
            {
                Settled?.Invoke();
            }

            _moved = false;
        }

        _dragging = held;

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
