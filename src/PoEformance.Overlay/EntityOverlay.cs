using System.Numerics;
using System.Runtime.Versioning;
using ClickableTransparentOverlay;
using ImGuiNET;
using PoEformance.Game.Components;
using PoEformance.Game.Ui;
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

    private readonly Func<UiScale, WorldSnapshot> _snapshotSource;
    private readonly IntPtr _gameWindow;
    private readonly int _cull;
    private WorldSnapshot _snapshot = WorldSnapshot.Empty;
    private ClientRect _tracked;

    /// <summary>Radius in pixels of an entity dot.</summary>
    private const float DotRadius = 5f;

    /// <summary>
    /// The entity kinds worth a dot.
    /// </summary>
    /// <remarks>
    /// A filter rather than "draw everything", because most of the entity map is not worth
    /// looking at while playing: terrain pieces, visual effects and the unclassified
    /// remainder are things the RE work needs to SEE, and things a player needs gone. They
    /// stay reachable through the diagnostic window, which is where inspecting the entity
    /// map belongs.
    /// </remarks>
    public HashSet<EntityKind> DrawnKinds { get; } =
        [EntityKind.Monster, EntityKind.Chest, EntityKind.WorldItem, EntityKind.Npc];

    /// <summary>Draw name labels next to dots.</summary>
    public bool ShowLabels { get; set; }

    /// <summary>
    /// Draw the diagnostic window with counts, the player position and the projection checks.
    /// </summary>
    /// <remarks>
    /// OFF by default, and that is a correction rather than a preference. Every instrument
    /// in this class was built to prove the projection, and once proven they are clutter
    /// over the game - which is exactly what the first person to actually play with it
    /// said. They earn their place behind --debug, not on screen by default.
    /// </remarks>
    public bool ShowDiagnostics { get; set; }

    /// <summary>Draw the alignment aids: screen centre and both candidate player heights.</summary>
    public bool ShowCalibration { get; set; }

    /// <summary>
    /// World height added to every marker before projecting, adjustable live.
    /// </summary>
    /// <remarks>
    /// A MEASURING instrument, not a fudge factor. The game's own map draws an X at the
    /// player's position, which is an exact reference; dragging this until the marker sits
    /// on that X reads off the residual as a NUMBER instead of an estimate from a
    /// screenshot. Deliberately in WORLD units rather than pixels, because that is what
    /// distinguishes the possible causes: if the value that aligns it is a round height
    /// (a character's 88, say, or its half) the height fed in is wrong, whereas a value
    /// that shifts when the camera moves means the error is in screen space instead.
    /// </remarks>
    public float ProbeHeight { get; set; }

    /// <summary>
    /// Creates the overlay. <paramref name="snapshotSource"/> is called once per frame and
    /// must be cheap and non-blocking - it is the render thread.
    /// <paramref name="gameWindow"/> is the game's window handle, which the overlay resizes
    /// itself to match.
    /// </summary>
    public EntityOverlay(Func<UiScale, WorldSnapshot> snapshotSource, IntPtr gameWindow, int cull = 0)
        : base("PoEformance", true)
    {
        ArgumentNullException.ThrowIfNull(snapshotSource);
        _snapshotSource = snapshotSource;
        _gameWindow = gameWindow;
        _cull = cull;
    }

    /// <summary>Draw entity dots on the game's own map, using the map's projection.</summary>
    public bool ShowMapDots { get; set; } = true;

    /// <summary>
    /// Optional: read cost, completed reads and failures from the reader thread.
    /// </summary>
    /// <remarks>
    /// Shown next to the frame rate so the two are directly comparable. That comparison IS
    /// the point of the reader thread: read time no longer bounds frame time, and seeing
    /// both numbers is what makes a regression obvious instead of felt.
    /// </remarks>
    public Func<(double Milliseconds, long Reads, long Failures)>? ReadStats { get; set; }

    /// <summary>Optional: what auto-flask did, or why it did nothing.</summary>
    /// <remarks>
    /// Shown because "why did nothing happen" is the question actually asked of an
    /// automation feature, and a silent no-op cannot answer it.
    /// </remarks>
    public Func<string>? FlaskStatus { get; set; }

    protected override Task PostInitialized()
    {
        VSync = true;
        return Task.CompletedTask;
    }

    protected override void Render()
    {
        TrackGameWindow();

        int width = (int)ImGui.GetIO().DisplaySize.X;
        int height = (int)ImGui.GetIO().DisplaySize.Y;

        // The renderer knows the viewport, so it hands it to the reader rather than the
        // reader guessing at a window it cannot see. Kept ahead of the foreground gate so
        // the viewport stays current while hidden - the game can be resized from a
        // borderless-window setting change without ever giving up focus.
        _snapshot = _snapshotSource(new UiScale(width, height, _cull));

        // Nothing is drawn unless the game is the window in front. This is not tidiness:
        // the overlay is always-on-top and covers the game's whole client area, so every
        // dot it paints while the user has alt-tabbed away lands on top of the browser or
        // editor they switched to.
        if (!GameWindowTracker.IsForeground(_gameWindow))
        {
            return;
        }

        if (_snapshot.InGame && width > 0 && height > 0)
        {
            DrawEntities(width, height);
            if (ShowMapDots)
            {
                DrawMapDots();
            }
        }

        if (ShowDiagnostics)
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
            if (!DrawnKinds.Contains(entity.Kind))
            {
                continue; // terrain, effects and the unclassified rest - noise while playing
            }

            ScreenPoint point = ProjectGround(entity, width, height);

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
            ScreenPoint point = ProjectGround(player, width, height);
            if (point.OnScreen)
            {
                var position = new Vector2(point.X, point.Y);
                draw.AddCircleFilled(position, DotRadius + 2, ColourFor(EntityKind.Player));
                draw.AddCircle(position, DotRadius + 2, OutlineColour, 16, 2f);
            }

            if (ShowCalibration)
            {
                DrawCalibration(draw, player, width, height);
            }
        }
    }

    /// <summary>
    /// Projects an entity at ground level, plus the live probe height.
    /// </summary>
    /// <remarks>
    /// The entity's OWN base height, which is what GameHelper2 projects - not TerrainHeight.
    /// TerrainHeight belongs to the map radar's separate projection, and feeding it here was
    /// mixing the two systems up.
    /// </remarks>
    private ScreenPoint ProjectGround(WorldEntity entity, int width, int height)
        => WorldToScreen.Project(
            _snapshot.Matrix, entity.WorldX, entity.WorldY, entity.WorldZ + ProbeHeight, width, height);

    /// <summary>
    /// Draws the alignment aids: screen centre, and the player at both candidate heights.
    /// </summary>
    /// <remarks>
    /// A residual offset between a marker and the character can come from several places -
    /// the height fed into the projection, the overlay not covering the client area, or the
    /// camera simply not centring the player. Guessing between them from a screenshot is
    /// hopeless, so this draws the competing answers AT THE SAME TIME and lets the picture
    /// decide: whichever marker sits on the feet is the correct height, and the crosshair
    /// shows how far off centre the character really is. The line between the two markers
    /// is the character's own height in screen pixels, which is the scale for judging any
    /// leftover error.
    /// </remarks>
    private void DrawCalibration(ImDrawListPtr draw, WorldEntity player, int width, int height)
    {
        ScreenPoint ground = ProjectGround(player, width, height);
        ScreenPoint healthbar = WorldToScreen.Project(
            _snapshot.Matrix, player.WorldX, player.WorldY, player.HealthBarZ, width, height);

        // Screen centre: where the camera claims the player is.
        var centre = new Vector2(width / 2f, height / 2f);
        uint centreColour = Pack(0.3f, 0.9f, 1f);
        draw.AddLine(centre - new Vector2(24, 0), centre + new Vector2(24, 0), centreColour, 1.5f);
        draw.AddLine(centre - new Vector2(0, 24), centre + new Vector2(0, 24), centreColour, 1.5f);
        draw.AddText(centre + new Vector2(28, -7), centreColour, "screen centre");

        if (!ground.OnScreen || !healthbar.OnScreen)
        {
            return;
        }

        var groundPoint = new Vector2(ground.X, ground.Y);
        var healthbarPoint = new Vector2(healthbar.X, healthbar.Y);

        uint groundColour = Pack(0.4f, 1f, 0.4f);
        uint healthbarColour = Pack(1f, 0.4f, 1f);

        DrawHealthbarReferences(draw, width, height);

        draw.AddLine(groundPoint, healthbarPoint, Pack(1f, 1f, 1f), 1f);
        draw.AddCircle(groundPoint, DotRadius + 6, groundColour, 20, 2f);
        draw.AddText(groundPoint + new Vector2(DotRadius + 9, 2), groundColour, "base (Render z)");
        draw.AddCircle(healthbarPoint, DotRadius + 4, healthbarColour, 20, 2f);
        draw.AddText(healthbarPoint + new Vector2(DotRadius + 7, -14), healthbarColour, "health bar (z - ModelBounds)");
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
                if (ReadStats is not null)
                {
                    (double ms, long reads, long failures) = ReadStats();
                    ImGui.Text($"read:     {ms:F1} ms on its own thread   frame: {1000f / ImGui.GetIO().Framerate:F1} ms"
                        + $"   ({reads} reads{(failures > 0 ? $", {failures} failed" : string.Empty)})");
                }
                ImGui.Text(_tracked.IsValid
                    ? $"viewport: {width} x {height}  (game {_tracked.Width} x {_tracked.Height} @ {_tracked.X},{_tracked.Y})"
                    : $"viewport: {width} x {height}  (game window not tracked)");

                if (_snapshot.Player is WorldEntity player)
                {
                    ImGui.Text($"player:   ({player.WorldX:F0}, {player.WorldY:F0}, {player.WorldZ:F0})");

                    // The live projection sanity check: the camera follows the player, so this
                    // must sit at the screen centre. If it drifts, the matrix is wrong.
                    ScreenPoint p = WorldToScreen.Project(
                        _snapshot.Matrix, player.WorldX, player.WorldY, player.TerrainHeight, width, height);
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

                    // The same thing in pixels, which is what a screenshot can be measured
                    // against: how far the marker sits from the screen centre, and how tall
                    // the character is on screen, so the two are directly comparable.
                    ScreenPoint hb = WorldToScreen.Project(
                        _snapshot.Matrix, player.WorldX, player.WorldY, player.WorldZ, width, height);
                    ImGui.Text($"marker:   ({p.X:F0}, {p.Y:F0})   centre ({width / 2}, {height / 2})"
                        + $"   delta ({p.X - (width / 2f):F0}, {p.Y - (height / 2f):F0}) px");
                    ImGui.Text($"character on screen: {Math.Abs(hb.Y - p.Y):F0} px tall"
                        + $"   (world z {player.WorldZ:F0} vs ground {player.TerrainHeight:F0})");
                    if (!healthy && spread <= width * 0.05)
                    {
                        ImGui.TextColored(colour, "  scene collapsed - the matrix offset is wrong.");
                    }
                }

                if (_snapshot.PlayerVitals is Vitals vitals)
                {
                    ImGui.Text($"vitals:   life {Show(vitals.Life)}   mana {Show(vitals.Mana)}   es {Show(vitals.EnergyShield)}");
                }

                // Always shown, including when it failed: an omitted row looks like a
                // feature that is not running, when it actually means a read gave up.
                if (_snapshot.FlaskBelt is FlaskBelt belt && !belt.IsUnknown)
                {
                    ImGui.Text("belt:     " + string.Join("   ", belt.Flasks.Select(f =>
                        $"{f.Slot}:{f.Charges}/{f.ChargesPerUse}"
                        + (f.IsCharm ? " (charm)" : f.CanUse ? string.Empty : " (empty)"))));
                }
                else
                {
                    ImGui.TextColored(new Vector4(1f, 0.5f, 0.4f, 1f),
                        "belt:     not read - run with --flasks for the chain");
                }

                if (FlaskStatus is not null)
                {
                    ImGui.TextColored(new Vector4(0.8f, 0.7f, 0.4f, 1f), $"flask:    {FlaskStatus()}");
                }

                // The kind breakdown doubles as the filter, since "what is out there" and
                // "what do I want drawn" are the same question asked twice. Note the ##id
                // suffix: ImGui derives a control's identity from its label, so a label
                // carrying a live count would be a NEW control every frame and the click
                // would never register.
                foreach (IGrouping<EntityKind, WorldEntity> group in _snapshot.Entities.GroupBy(e => e.Kind).OrderBy(g => g.Key.ToString()))
                {
                    bool drawn = DrawnKinds.Contains(group.Key);
                    if (ImGui.Checkbox($"{group.Key,-10} {group.Count()}##kind{group.Key}", ref drawn))
                    {
                        if (drawn)
                        {
                            DrawnKinds.Add(group.Key);
                        }
                        else
                        {
                            DrawnKinds.Remove(group.Key);
                        }
                    }
                }
            }

            ImGui.Separator();
            bool labels = ShowLabels;
            if (ImGui.Checkbox("labels", ref labels))
            {
                ShowLabels = labels;
            }

            ImGui.SameLine();
            bool calibration = ShowCalibration;
            if (ImGui.Checkbox("calibration", ref calibration))
            {
                ShowCalibration = calibration;
            }

            if (ShowCalibration && _snapshot.Player is WorldEntity subject)
            {
                // Drag until the marker sits on the X the game's own map draws at the
                // player's position, then read the number off. Reported in BOTH units
                // because that is what separates the possible causes: a round world height
                // means the wrong height is being fed in, while a value that only makes
                // sense in pixels means the error is in screen space.
                ImGui.SetNextItemWidth(180);
                float probe = ProbeHeight;
                if (ImGui.DragFloat("probe height", ref probe, 0.5f, -200f, 200f, "%.0f world units"))
                {
                    ProbeHeight = probe;
                }

                ImGui.SameLine();
                if (ImGui.SmallButton("reset"))
                {
                    ProbeHeight = 0;
                }

                // The character's own height calibrates world units against pixels: the two
                // rings are exactly its Render z above the ground.
                float characterUnits = Math.Abs(subject.ModelBoundsZ);
                ScreenPoint top = WorldToScreen.Project(
                    _snapshot.Matrix, subject.WorldX, subject.WorldY, subject.HealthBarZ, width, height);
                ScreenPoint bottom = ProjectGround(subject, width, height);
                float pixelsPerUnit = characterUnits > 0.01f
                    ? Math.Abs(top.Y - bottom.Y) / characterUnits
                    : 0f;

                ImGui.Text(pixelsPerUnit > 0.0001f
                    ? $"probe:    {ProbeHeight:F0} world units = {ProbeHeight * pixelsPerUnit:F0} px"
                      + $"   (scale {pixelsPerUnit:F2} px per world unit)"
                    : "probe:    scale unavailable - no character height to calibrate against");
            }
        }

        ImGui.End();
    }

    /// <summary>
    /// Draws entity dots on the game's own map, through the map's projection.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT the camera matrix. The map is zoomable and this transform accounts
    /// for it; markers projected with the matrix drift away from the game's own map markers
    /// the moment the map is zoomed, which is what made a correct world projection look
    /// broken.
    ///
    /// The large map is preferred when it is open and the minimap otherwise. Which one is
    /// "open" cannot be told apart by a flag alone here, so the choice falls back to whether
    /// the projection lands anywhere sensible on screen.
    /// </remarks>
    private void DrawMapDots()
    {
        if (_snapshot.Player is not WorldEntity player)
        {
            return;
        }

        MapView? chosen = _snapshot.LargeMap is MapView large && large.IsUsable ? large
            : _snapshot.MiniMap is MapView mini && mini.IsUsable ? mini
            : null;

        if (chosen is not MapView map)
        {
            return;
        }

        ImDrawListPtr draw = ImGui.GetBackgroundDrawList();

        foreach (WorldEntity entity in _snapshot.Entities)
        {
            if (!DrawnKinds.Contains(entity.Kind))
            {
                continue;
            }

            Vector2 at = map.Project(
                entity.WorldX, entity.WorldY, entity.TerrainHeight,
                player.WorldX, player.WorldY, player.TerrainHeight);

            draw.AddCircleFilled(at, 3.5f, ColourFor(entity.Kind));
            draw.AddCircle(at, 3.5f, OutlineColour, 10, 1f);
        }

        if (ShowCalibration)
        {
            // The self-check for this projection: the player is the map's origin BY
            // CONSTRUCTION, so this ring must sit exactly on the marker the game draws for
            // the player on its own map. Nothing to eyeball and nothing to compare across
            // screenshots - the game supplies the reference every frame.
            uint colour = Pack(0.3f, 0.9f, 1f);
            draw.AddCircle(map.Centre, 9f, colour, 20, 2f);
            draw.AddText(map.Centre + new Vector2(12, -7), colour,
                $"map centre - the game's player marker belongs here (zoom {map.Zoom:F2})");
        }
    }

    /// <summary>
    /// Marks where each monster's floating health bar should be, if the projection is right.
    /// </summary>
    /// <remarks>
    /// This is the one check that needs no judgement about where a character's feet are. The
    /// game draws a monster's health bar at that monster's Render z - the exact value we feed
    /// the projection - so the bracket must land ON the bar the game itself drew. It is a
    /// pixel-accurate reference point supplied by the game, for free, on every monster on
    /// screen. If the brackets sit on the bars, the matrix and the height interpretation are
    /// both confirmed, and any remaining gap to the feet is simply the character's height.
    /// </remarks>
    private void DrawHealthbarReferences(ImDrawListPtr draw, int width, int height)
    {
        uint colour = Pack(1f, 0.4f, 1f);
        foreach (WorldEntity monster in _snapshot.Entities.Where(e => e.Kind == EntityKind.Monster))
        {
            ScreenPoint bar = WorldToScreen.Project(
                _snapshot.Matrix, monster.WorldX, monster.WorldY, monster.HealthBarZ, width, height);
            if (!bar.OnScreen)
            {
                continue;
            }

            // A bracket rather than a dot: it frames the game's bar instead of hiding it.
            var at = new Vector2(bar.X, bar.Y);
            draw.AddLine(at - new Vector2(18, 0), at - new Vector2(6, 0), colour, 2f);
            draw.AddLine(at + new Vector2(6, 0), at + new Vector2(18, 0), colour, 2f);
            draw.AddLine(at - new Vector2(18, 4), at - new Vector2(18, -4), colour, 2f);
            draw.AddLine(at + new Vector2(18, 4), at + new Vector2(18, -4), colour, 2f);
        }
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
                _snapshot.Matrix, entity.WorldX, entity.WorldY, entity.TerrainHeight, width, height);
            minX = Math.Min(minX, point.X); maxX = Math.Max(maxX, point.X);
            minY = Math.Min(minY, point.Y); maxY = Math.Max(maxY, point.Y);
        }

        return maxX < minX ? 0 : Math.Max(maxX - minX, maxY - minY);
    }

    /// <summary>
    /// A vital as "current/usable (percent)" - the usable pool, not the raw maximum.
    /// </summary>
    private static string Show(Vital vital)
        => vital.Percent < 0 ? "-" : $"{vital.Current}/{vital.Unreserved} ({vital.Percent}%)";

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
