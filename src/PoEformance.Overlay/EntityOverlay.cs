using System.Numerics;
using System.Runtime.Versioning;
using ClickableTransparentOverlay;
using ImGuiNET;
using PoEformance.Game.World;

namespace PoEformance.Overlay;

/// <summary>
/// The in-game overlay: draws a dot for every entity at its projected screen position.
/// </summary>
/// <remarks>
/// Click-through and always-on-top via ClickableTransparentOverlay, drawn with ImGui on a
/// GPU-backed transparent window - the game keeps every input, the overlay only paints.
///
/// The renderer NEVER touches game memory. It is handed a finished
/// <see cref="WorldSnapshot"/> by whoever owns the reading, projects each entity through
/// the snapshot's matrix, and draws. That separation is what lets the projection be tested
/// against recordings while this class stays a thin drawing loop.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class EntityOverlay : ClickableTransparentOverlay.Overlay
{
    // Fully qualified: this project's own namespace is PoEformance.Overlay, which would
    // otherwise shadow the library's Overlay type.

    private readonly Func<WorldSnapshot> _snapshotSource;
    private readonly IntPtr _gameWindow;
    private WorldSnapshot _snapshot = WorldSnapshot.Empty;
    private ClientRect _tracked;

    /// <summary>Radius in pixels of an entity dot.</summary>
    private const float DotRadius = 5f;

    /// <summary>Draw name labels next to dots.</summary>
    public bool ShowLabels { get; set; } = true;

    /// <summary>Draw the diagnostic window with counts and the player position.</summary>
    public bool ShowDebugWindow { get; set; } = true;

    /// <summary>
    /// Creates the overlay. <paramref name="snapshotSource"/> is called once per frame and
    /// must be cheap and non-blocking - it is the render thread.
    /// <paramref name="gameWindow"/> is the game's window handle, which the overlay resizes
    /// itself to match.
    /// </summary>
    public EntityOverlay(Func<WorldSnapshot> snapshotSource, IntPtr gameWindow)
        : base("PoEformance", true)
    {
        ArgumentNullException.ThrowIfNull(snapshotSource);
        _snapshotSource = snapshotSource;
        _gameWindow = gameWindow;
    }

    protected override Task PostInitialized()
    {
        VSync = true;
        return Task.CompletedTask;
    }

    protected override void Render()
    {
        _snapshot = _snapshotSource();
        TrackGameWindow();

        int width = (int)ImGui.GetIO().DisplaySize.X;
        int height = (int)ImGui.GetIO().DisplaySize.Y;

        if (_snapshot.InGame && width > 0 && height > 0)
        {
            DrawEntities(width, height);
        }

        if (ShowDebugWindow)
        {
            DrawDebugWindow(width, height);
        }
    }

    /// <summary>
    /// Matches the overlay to the game's client area, so a projected pixel means the same
    /// thing in both windows.
    /// </summary>
    /// <remarks>
    /// Only applied on a CHANGE: assigning position and size re-creates swap-chain resources,
    /// so doing it every frame would cost far more than the comparison saves - and it also
    /// keeps the window movable by the user when no game window is being tracked.
    /// </remarks>
    private void TrackGameWindow()
    {
        ClientRect rect = GameWindowTracker.TryGet(_gameWindow);
        if (!rect.IsValid || rect == _tracked)
        {
            return;
        }

        _tracked = rect;
        Position = new System.Drawing.Point(rect.X, rect.Y);
        Size = new System.Drawing.Size(rect.Width, rect.Height);
    }

    /// <summary>Projects every entity and paints it on the background draw list.</summary>
    private void DrawEntities(int width, int height)
    {
        ImDrawListPtr draw = ImGui.GetBackgroundDrawList();

        foreach (WorldEntity entity in _snapshot.Entities)
        {
            ScreenPoint point = WorldToScreen.Project(
                _snapshot.Matrix, entity.WorldX, entity.WorldY, entity.WorldZ, width, height);

            if (!point.OnScreen)
            {
                continue; // behind the camera or outside the viewport
            }

            uint colour = ColourFor(entity.Kind);
            var position = new Vector2(point.X, point.Y);

            draw.AddCircleFilled(position, DotRadius, colour);
            draw.AddCircle(position, DotRadius, OutlineColour, 12, 1.5f);

            if (ShowLabels && entity.Kind is EntityKind.Monster or EntityKind.Chest or EntityKind.WorldItem)
            {
                draw.AddText(position + new Vector2(DotRadius + 3, -7), colour, entity.ShortName);
            }
        }

        // The player last, so it is never hidden under another dot.
        if (_snapshot.Player is WorldEntity player)
        {
            ScreenPoint point = WorldToScreen.Project(
                _snapshot.Matrix, player.WorldX, player.WorldY, player.WorldZ, width, height);
            if (point.OnScreen)
            {
                var position = new Vector2(point.X, point.Y);
                draw.AddCircleFilled(position, DotRadius + 2, ColourFor(EntityKind.Player));
                draw.AddCircle(position, DotRadius + 2, OutlineColour, 16, 2f);
            }
        }
    }

    /// <summary>A small always-visible readout, so a blank overlay is never ambiguous.</summary>
    private void DrawDebugWindow(int width, int height)
    {
        ImGui.SetNextWindowPos(new Vector2(20, 20), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowBgAlpha(0.7f);

        if (ImGui.Begin("PoEformance", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoFocusOnAppearing))
        {
            if (!_snapshot.InGame)
            {
                ImGui.TextColored(new Vector4(1f, 0.6f, 0.2f, 1f), "not in an area");
            }
            else
            {
                ImGui.Text($"entities: {_snapshot.Entities.Count}");
                ImGui.Text(_tracked.IsValid
                    ? $"viewport: {width} x {height}  (game {_tracked.Width} x {_tracked.Height} @ {_tracked.X},{_tracked.Y})"
                    : $"viewport: {width} x {height}  (game window not tracked)");

                if (_snapshot.Player is WorldEntity player)
                {
                    ImGui.Text($"player:   ({player.WorldX:F0}, {player.WorldY:F0}, {player.WorldZ:F0})");

                    // The live projection sanity check: the camera follows the player, so this
                    // must sit at the screen centre. If it drifts, the matrix is wrong.
                    ScreenPoint p = WorldToScreen.Project(
                        _snapshot.Matrix, player.WorldX, player.WorldY, player.WorldZ, width, height);
                    double offCentre = WorldToScreen.OffCentreFraction(p, width, height);

                    // Centred ALONE is not proof: a matrix that blows w up collapses the whole
                    // scene onto the centre, so the player looks perfect while nothing else is
                    // drawable. Showing the scene's pixel spread next to it makes that failure
                    // visible instead of reassuring.
                    double spread = ScreenSpread(width, height);
                    bool healthy = offCentre < 0.15 && spread > width * 0.05;
                    Vector4 colour = healthy
                        ? new Vector4(0.4f, 1f, 0.4f, 1f)
                        : new Vector4(1f, 0.4f, 0.4f, 1f);
                    ImGui.TextColored(colour, $"player off-centre: {offCentre:F3}   scene spread: {spread:F0} px");
                    if (!healthy && spread <= width * 0.05)
                    {
                        ImGui.TextColored(colour, "  scene collapsed - the matrix offset is wrong.");
                    }
                }

                foreach (IGrouping<EntityKind, WorldEntity> group in _snapshot.Entities.GroupBy(e => e.Kind).OrderBy(g => g.Key.ToString()))
                {
                    ImGui.Text($"  {group.Key,-10} {group.Count()}");
                }
            }

            ImGui.Separator();
            bool labels = ShowLabels;
            if (ImGui.Checkbox("labels", ref labels))
            {
                ShowLabels = labels;
            }
        }

        ImGui.End();
    }

    /// <summary>
    /// Widest pixel gap between any two projected entities - near zero when a bad matrix has
    /// collapsed the scene onto one point.
    /// </summary>
    private double ScreenSpread(int width, int height)
    {
        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        foreach (WorldEntity entity in _snapshot.Entities)
        {
            ScreenPoint point = WorldToScreen.Project(
                _snapshot.Matrix, entity.WorldX, entity.WorldY, entity.WorldZ, width, height);
            minX = Math.Min(minX, point.X); maxX = Math.Max(maxX, point.X);
            minY = Math.Min(minY, point.Y); maxY = Math.Max(maxY, point.Y);
        }

        return maxX < minX ? 0 : Math.Max(maxX - minX, maxY - minY);
    }

    /// <summary>White outline so dots stay readable over any background.</summary>
    private static uint OutlineColour => ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.8f));

    /// <summary>Colour per entity kind. ImGui packs colours as ABGR.</summary>
    private static uint ColourFor(EntityKind kind) => kind switch
    {
        EntityKind.Player => Pack(0.3f, 1f, 0.3f),
        EntityKind.Monster => Pack(1f, 0.25f, 0.25f),
        EntityKind.Chest => Pack(1f, 0.85f, 0.2f),
        EntityKind.WorldItem => Pack(0.4f, 0.8f, 1f),
        EntityKind.Npc => Pack(0.6f, 0.9f, 0.6f),
        EntityKind.Effect => Pack(0.7f, 0.5f, 1f),
        EntityKind.Terrain => Pack(0.6f, 0.6f, 0.6f),
        _ => Pack(0.8f, 0.8f, 0.8f),
    };

    private static uint Pack(float r, float g, float b)
        => ImGui.ColorConvertFloat4ToU32(new Vector4(r, g, b, 0.9f));
}
