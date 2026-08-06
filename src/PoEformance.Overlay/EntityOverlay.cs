using System.Numerics;
using System.Runtime.Versioning;
using ClickableTransparentOverlay;
using ImGuiNET;
using PoEformance.Core.Diagnostics;
using PoEformance.Features;
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
    private readonly TerrainLayer _terrain;
    private readonly IconCache _icons;
    /// <summary>
    /// The reader's noise filter, so it can be switched from the overlay.
    /// </summary>
    /// <remarks>
    /// Here rather than in a settings file because of WHEN it is wanted: a filtered entity is
    /// absent from the snapshot entirely, so the moment somebody needs it is the moment they
    /// are looking at the entity browser wondering where something went. A switch that needs
    /// a restart would be no switch at all.
    /// </remarks>
    public NoiseFilter? Noise { get; set; }

    /// <summary>
    /// How every drawn thing looks - colours, sizes, line widths, and whether it is drawn.
    /// </summary>
    /// <remarks>
    /// Read on the render thread every frame rather than copied into fields on a change, so a
    /// colour picked in the editor lands on the next frame. Nothing else would do: choosing a
    /// colour is a thing you do by looking at it, and a value that needs a restart to take
    /// effect cannot be chosen that way at all.
    ///
    /// Handed on to the layers that draw their own things, so there is ONE of these rather
    /// than one per layer - the editor writes to it, and everything drawn reads from it.
    /// </remarks>
    public OverlayStyle Style
    {
        get => _style;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _style = value;
            if (_poi is not null)
            {
                _poi.Style = value;
            }

            if (_uiBrowser is not null)
            {
                _uiBrowser.Style = value;
            }

            _banner.Style = value;
            _unwalked.Style = value;
            _healthBars.Style = value;
        }
    }

    private OverlayStyle _style = new();

    /// <summary>
    /// Called when a setting the user can see was changed, so it can be written down.
    /// </summary>
    /// <remarks>
    /// Every one of these is a choice made once and expected to hold. Losing them on each
    /// launch is small and constant, which is the kind of friction that never gets reported
    /// and never stops - so the toggles say when they moved and somebody else decides where
    /// that is kept.
    /// </remarks>
    public Action? SettingsChanged { get; set; }

    /// <summary>Applies the settings that persist, and remembers them for the next save.</summary>
    public void Apply(OverlaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        MinimumLootRarity = settings.MinLootRarity;
        ShowTerrain = settings.ShowTerrain;
        ShowLabels = settings.DotLabels;
        _healthBars.OnlyWhenHurt = settings.HealthBarsOnlyWhenHurt;

        if (Noise is not null)
        {
            Noise.Enabled = settings.HideNoise;
        }

        if (_poi is not null)
        {
            _poi.ShowPicker = settings.ShowPoi;
            _poi.ShowLabels = settings.PoiLabels;
            _poi.ShowRoutes = settings.PoiRoutes;
            _poi.ShowArrows = settings.PoiArrows;
        }

        ApplyTerrainStyle(OverlaySettings.ParseColour(settings.TerrainColour), settings.TerrainThickness);
    }

    /// <summary>The settings as they stand now, for writing down.</summary>
    /// <remarks>
    /// Built from the LIVE state rather than from a copy kept alongside it. Two records of the
    /// same thing drift, and the one that would be saved is the one nobody is looking at.
    /// </remarks>
    public OverlaySettings CurrentSettings(OverlaySettings basis)
    {
        ArgumentNullException.ThrowIfNull(basis);

        return basis with
        {
            MinLootRarity = MinimumLootRarity,
            ShowTerrain = ShowTerrain,
            DotLabels = ShowLabels,
            HealthBarsOnlyWhenHurt = _healthBars.OnlyWhenHurt,
            HideNoise = Noise?.Enabled ?? basis.HideNoise,
            ShowPoi = _poi?.ShowPicker ?? basis.ShowPoi,
            PoiLabels = _poi?.ShowLabels ?? basis.PoiLabels,
            PoiRoutes = _poi?.ShowRoutes ?? basis.PoiRoutes,
            PoiArrows = _poi?.ShowArrows ?? basis.PoiArrows,
        };
    }

    /// <summary>What every read cost, kept so a whole map can be looked at afterwards.</summary>
    public CostHistory? Costs { get; set; }

    /// <summary>How much of the area has been walked, if it is being measured.</summary>
    public MapCoverage? Coverage { get; set; }

    private CostWindow? _costWindow;
    private UiBrowserWindow? _uiBrowser;
    private DissectorWindow? _dissector;
    private EntityBrowserWindow? _entityBrowser;
    private PoiLayer? _poi;
    private StyleWindow? _styleWindow;
    private AlertWatcher? _alerts;
    private readonly AlertBanner _banner = new();
    private readonly UnwalkedLayer _unwalked = new();
    private readonly HealthBarLayer _healthBars = new();

    /// <summary>
    /// Adds the alert watcher, which says when something worth knowing about turned up.
    /// </summary>
    /// <remarks>
    /// Looked at on the RENDER thread rather than the reader's, and that is deliberate: it
    /// reads a finished snapshot and touches no memory, so putting it here costs the read
    /// nothing and keeps the reader free of anything that produces user-facing output.
    /// </remarks>
    public void AttachAlerts(AlertWatcher watcher, Action saved, bool visible = false)
    {
        ArgumentNullException.ThrowIfNull(watcher);
        ArgumentNullException.ThrowIfNull(saved);
        _alerts = watcher;
        _alertWindow = new AlertWindow(watcher, saved) { Visible = visible };
        AlertsChanged = saved;
    }

    private AlertWindow? _alertWindow;
    private PreloadWindow? _preloadWindow;
    private PreloadWatch? _preload;

    /// <summary>
    /// Adds the "what is in this area" list.
    /// </summary>
    /// <remarks>
    /// The walk that fills it belongs to whoever owns the reading - it is far too expensive
    /// for a tick and runs once per area on its own thread. This only shows the answer, and
    /// offers the button that forces another look.
    /// </remarks>
    public void AttachPreload(PreloadWatch watch, Action lookAgain, bool visible = false)
    {
        ArgumentNullException.ThrowIfNull(watch);
        ArgumentNullException.ThrowIfNull(lookAgain);
        _preload = watch;
        _preloadWindow = new PreloadWindow(watch, lookAgain) { Visible = visible };
    }

    /// <summary>Called when an alert setting was changed, so it can be written down.</summary>
    /// <remarks>
    /// Separate from <see cref="SettingsChanged"/> because the two live in different files:
    /// the alerts carry their RULES, which is a list rather than a switch, and mixing a list
    /// somebody curates into the overlay's settings would put half a person's configuration
    /// in each of two places.
    /// </remarks>
    public Action? AlertsChanged { get; set; }

    /// <summary>
    /// Adds the appearance editor, and says where its choices should be written down.
    /// </summary>
    /// <remarks>
    /// The saving is somebody else's, like every other setting here: the overlay draws, and
    /// where a file lives is not its business. Attaching this is also what makes the editor
    /// reachable at all - without it the style still applies, it just cannot be changed from
    /// in the game.
    /// </remarks>
    public void AttachStyleEditor(Action saved, bool visible = false)
    {
        ArgumentNullException.ThrowIfNull(saved);
        _styleWindow = new StyleWindow(Style, saved) { Visible = visible };
    }

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

    /// <summary>
    /// The worst drop still worth a marker. Currency is always shown.
    /// </summary>
    /// <remarks>
    /// Magic by default because Path of Exile 2 carpets the floor in normal-rarity items,
    /// and an overlay that marks every one of them is harder to read than no overlay at
    /// all - it turns the useful drops into three more dots among forty.
    ///
    /// Currency ignores this deliberately. It carries no rarity component, so it is
    /// classified by path, and it is the class of drop nobody wants filtered.
    /// </remarks>
    public ItemRarity MinimumLootRarity { get; set; } = ItemRarity.Magic;

    /// <summary>Draw the area's layout on the map, including the parts not explored yet.</summary>
    public bool ShowTerrain { get; set; } = true;

    /// <summary>Draw name labels next to dots.</summary>
    public bool ShowLabels { get; set; }

    /// <summary>Draw the small status window: what is being read, and what auto-flask is doing.</summary>
    public bool ShowStatus { get; set; } = true;

    /// <summary>
    /// Add the projection measurements and the per-kind filters to the status window.
    /// </summary>
    /// <remarks>
    /// OFF by default, and that split is a correction rather than a preference. Everything
    /// in here was built to PROVE the projection - off-centre fractions, scene spread,
    /// marker deltas, the probe height - and once proven it is arithmetic nobody reads
    /// while playing. What stays visible is the part that answers a question during a
    /// session: is it reading, what are the pools, and why did the flask not fire.
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

        // The layer never touches the renderer directly - it is handed the two operations
        // it needs, which is what keeps its projection maths testable away from a GPU.
        _terrain = new TerrainLayer(Upload, key => RemoveImage(key));
        _icons = new IconCache(Upload, key => RemoveImage(key));
    }

    private IntPtr Upload(string key, SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32> image, bool srgb)
    {
        AddOrGetImagePointer(key, image, srgb, out IntPtr handle);
        return handle;
    }

    /// <summary>
    /// Also draw a dot on each entity out in the 3D world.
    /// </summary>
    /// <remarks>
    /// OFF by default. It is the world-to-screen projection's showcase, and as a playing
    /// aid it is the opposite of one: the markers land between the player and the thing
    /// they are fighting. The map is where a radar belongs.
    /// </remarks>
    public bool ShowWorldDots { get; set; }

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

    /// <summary>
    /// Adds the interface browser, served by an inspector on the reader thread.
    /// </summary>
    /// <remarks>
    /// Optional, and the overlay works without it - which is why it is attached rather than
    /// constructed here. The browser exists to reverse-engineer the interface; the overlay
    /// exists to draw the world, and it has no business owning a tree reader.
    /// </remarks>
    public void AttachUiBrowser(UiTreeInspector inspector, bool visible = false)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        _uiBrowser = new UiBrowserWindow(inspector) { Visible = visible, Style = _style };
    }

    /// <summary>
    /// Adds the raw-memory dissector.
    /// </summary>
    /// <remarks>
    /// Over the game for the same reason the interface browser is: what is being watched is
    /// the game reacting, and a window somewhere else cannot be looked at and acted in at the
    /// same time. Finding a field means doing something and seeing what moved, which only
    /// works if both are in front of you.
    /// </remarks>
    public void AttachDissector(StructureInspector inspector, bool visible = false)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        _dissector = new DissectorWindow(inspector) { Visible = visible };
    }

    /// <summary>
    /// Adds the entity browser, which takes an entity apart into its components.
    /// </summary>
    /// <remarks>
    /// Wired to the dissector so a component can be opened where it can be read. Attach that
    /// first: without it the browser still lists everything, but the click that matters -
    /// "show me the one nothing describes" - has nowhere to go.
    /// </remarks>
    public void AttachEntityBrowser(EntityInspector inspector, bool visible = false)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        _entityBrowser = new EntityBrowserWindow(
            inspector,
            (address, label, layout) => _dissector?.Show(address, label, layout))
        {
            Visible = visible,
        };
    }

    /// <summary>
    /// Adds the map's points of interest and the route to a chosen one.
    /// </summary>
    /// <remarks>
    /// Attached like the browser: the overlay draws the world, and finding a way across it is
    /// somebody else's job - here it only projects what the planner produced.
    /// </remarks>
    public void AttachPointsOfInterest(RoutePlanner planner)
    {
        ArgumentNullException.ThrowIfNull(planner);

        // Takes the style as it stands, and the setter hands it on if it is replaced later -
        // so the order these two are done in does not matter. It otherwise would, and the
        // symptom of getting it wrong is a whole layer that ignores the editor.
        _poi = new PoiLayer(planner)
        {
            Style = _style,
            IconFor = _icons.TextureFor,
        };
    }

    protected override Task PostInitialized()
    {
        VSync = true;
        return Task.CompletedTask;
    }

    // What the settings page asked for, kept so the style can override it per frame without
    // either of them losing the other's value.
    private uint _terrainColour = 0xFF64C8FF;
    private int _terrainThickness = 1;

    /// <summary>Sets the layout's colour and line width. A colour of 0 keeps the current one.</summary>
    public void ApplyTerrainStyle(uint colour, int thickness)
    {
        if (colour != 0)
        {
            _terrainColour = colour;
            _terrain.Colour = colour;
        }

        _terrainThickness = thickness;
        _terrain.Thickness = thickness;
    }

    /// <summary>
    /// What the terrain layer holds, for the config page and the status window.
    /// </summary>
    /// <remarks>
    /// The height state is in here because it is the difference between an outline that
    /// stays put on a staircase and one that slides, and nothing on screen distinguishes
    /// "read the heights" from "drew it flat" other than walking up a hill.
    ///
    /// The two ground figures are the measurement for the part that is NOT done. Tile height
    /// is the whole tile's; the game's own is that plus a sub-tile term this does not read.
    /// On flat ground they should agree, and how far apart they drift on a staircase is
    /// exactly how much the missing term is worth - a number to read off rather than a guess
    /// about whether the rest of the port is worth doing.
    /// </remarks>
    public string DescribeTerrain()
    {
        if (_snapshot.Terrain is not TerrainGrid grid)
        {
            return "loading";
        }

        return $"{grid.Describe()}, {DescribeHeights(grid)}, texture {_terrain.Describe()}";
    }

    /// <summary>
    /// The height state, and the one number that says how much is still missing.
    /// </summary>
    /// <remarks>
    /// "ground" is the player's real ground height; "tile" is the height of the whole tile
    /// they stand on. Their difference IS the sub-tile term - the within-tile slope that is
    /// not read - measured live at the one place both figures are available. On flat ground
    /// they should agree; how far they part on a staircase is what the remaining error is
    /// worth, and it decides whether reading the rest is worth doing at all.
    ///
    /// A difference that persists on FLAT ground would mean something else: the two are not
    /// the same quantity, and the outline would be shifted by that much everywhere.
    /// </remarks>
    private string DescribeHeights(TerrainGrid grid)
    {
        if (!grid.HasHeights)
        {
            return $"FLAT - heights unavailable: {grid.HeightNote}";
        }

        if (_snapshot.Player is not WorldEntity player)
        {
            return grid.HeightNote;
        }

        // The residual, measured live at the one place both figures exist: the game's own
        // ground height under the player, against what this computes for the same spot. Zero
        // means the height model agrees with the game; anything else is drawn into the map.
        float here = grid.HeightAt(
            (int)(player.WorldX / MapView.WorldToGrid),
            (int)(player.WorldY / MapView.WorldToGrid));

        return $"{grid.HeightNote}; here ground {player.TerrainHeight:F0} vs computed {here:F0}"
               + $" (off by {player.TerrainHeight - here:F0})";
    }

    /// <summary>Releases the textures along with the renderer that holds them.</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _terrain.Dispose();
            _icons.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Draws a marker's own picture where one was chosen, and says whether it did.
    /// </summary>
    /// <remarks>
    /// False means "draw it the ordinary way", which covers both no icon and an icon that
    /// could not be loaded. Those are deliberately the same answer: a marker whose file went
    /// missing has to still be a marker, because its absence reads as nothing being there.
    /// </remarks>
    private bool DrawIcon(ImDrawListPtr draw, string key, Vector2 at, float radius)
    {
        LayerStyle style = Style[key];
        IntPtr texture = _icons.TextureFor(style.Icon);
        if (texture == IntPtr.Zero)
        {
            return false;
        }

        // Untinted unless a colour was CHOSEN, rather than tinted with the catalogue's
        // default. Somebody supplying their own picture supplied its colours too, and
        // multiplying a finished icon by the red that ordinary monsters happen to default to
        // would make every custom icon look broken. Choosing a colour still tints - which is
        // what makes one white shape serve every rarity.
        draw.AddImage(
            texture,
            at - new Vector2(radius, radius),
            at + new Vector2(radius, radius),
            Vector2.Zero,
            Vector2.One,
            style.ColourOr(0xFFFFFFFF));
        return true;
    }

    /// <summary>
    /// One frame. Nothing that happens in here may end the process.
    /// </summary>
    /// <remarks>
    /// The render loop runs on a thread-pool thread, so an escaping exception terminates
    /// the tool - with the game still running and the player mid-map. That is exactly how a
    /// texture upload failure in a cosmetic terrain layer turned into a crash on entering an
    /// area. A frame that cannot be drawn is worth reporting and skipping; it is not worth
    /// the session.
    ///
    /// Reported once per distinct message rather than every frame, because sixty identical
    /// lines a second is a way of hiding an error, not surfacing it.
    /// </remarks>
    protected override void Render()
    {
        try
        {
            RenderFrame();
        }
        catch (Exception exception) when (_reported.Add(exception.Message))
        {
            Console.Error.WriteLine($"overlay frame failed: {exception}");
        }
    }

    private readonly HashSet<string> _reported = [];

    private void RenderFrame()
    {
        TrackGameWindow();

        int width = (int)ImGui.GetIO().DisplaySize.X;
        int height = (int)ImGui.GetIO().DisplaySize.Y;

        // The renderer knows the viewport, so it hands it to the reader rather than the
        // reader guessing at a window it cannot see. Kept ahead of the foreground gate so
        // the viewport stays current while hidden - the game can be resized from a
        // borderless-window setting change without ever giving up focus.
        _snapshot = _snapshotSource(new UiScale(width, height, _cull));

        // Nothing is drawn unless the game - or one of OUR windows - is in front. Not
        // tidiness: the overlay is always-on-top and covers the game's whole client area,
        // so every dot painted while the user has alt-tabbed away lands on the browser or
        // editor they switched to.
        //
        // Our own windows have to count, and leaving them out is not a subtle bug. Clicking
        // this overlay's own controls gives IT focus, so the game stops being foreground,
        // so the next frame draws nothing, so there is nothing left to click: the window
        // disappears under the cursor and neither dragging nor a button ever registers.
        // Keystrokes are a different question and stay strict - see FlaskKeySender.
        if (!GameWindowTracker.IsForeground(_gameWindow) && !GameWindowTracker.IsOwnProcessForeground())
        {
            return;
        }

        // Before the marker gate, and outside it: the watcher decides for itself where it is
        // quiet, and its own rule is towns rather than "wherever markers are drawn".
        if (_alerts is not null && _snapshot.InGame)
        {
            long now = Environment.TickCount64;
            if (_alerts.Look(_snapshot, now) is Alert raised)
            {
                _banner.Show(raised);
            }

            if (width > 0 && height > 0)
            {
                _banner.Draw(ImGui.GetForegroundDrawList(), width, height, now);
            }
        }

        // Nothing to mark in a town or a hideout, and a screen full of markers over the
        // stash is worse than no overlay. An area that did not resolve counts as hostile,
        // so a failed read never silently switches the tool off.
        if (_snapshot.InGame && width > 0 && height > 0 && _snapshot.Area.WantsMarkers)
        {
            // Markers go ON THE MAP, not over the 3D scene. Scattering dots across the game
            // world puts them between the player and what they are fighting; the map is
            // where a radar belongs, and it is where the game already draws its own.
            DrawMapDots();

            // Over the monsters themselves rather than on the map, and separate from the
            // world dots for that reason: a dot in the world lands between the player and
            // what they are fighting, while a health bar goes where the eye already is.
            _healthBars.Draw(ImGui.GetBackgroundDrawList(), _snapshot, width, height);

            if (ShowWorldDots)
            {
                DrawEntities(width, height);
            }
        }

        // Outside the "in an area" gate: an element can be inspected on a login screen or in
        // a hideout, and the browser reports for itself when there is no tree to read.
        if (Costs is not null)
        {
            _costWindow ??= new CostWindow(Costs);
            _costWindow.CurrentArea = _snapshot.AreaHash;
            _costWindow.Render();
        }

        _uiBrowser?.Render(_tracked);
        _entityBrowser?.Render(_snapshot, _snapshot.Player);
        _dissector?.Render();
        _poi?.DrawPicker(_snapshot, _snapshot.Player);
        _styleWindow?.Render();
        _alertWindow?.Render();
        _preloadWindow?.Render();

        if (ShowStatus)
        {
            DrawStatusWindow(width, height);
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
            if (!DrawnKinds.Contains(entity.Kind) || !WorthDrawing(entity))
            {
                continue;
            }

            string key = KeyFor(entity);
            if (!Style.Visible(key))
            {
                continue;
            }

            ScreenPoint point = ProjectGround(entity, width, height);

            if (!point.OnScreen)
            {
                continue; // behind the camera or outside the viewport
            }

            uint colour = Style.Colour(key);
            var position = new Vector2(point.X, point.Y);
            float size = Style.Sized(key, DotRadius);

            if (!DrawIcon(draw, key, position, size))
            {
                draw.AddCircleFilled(position, size, colour);
                draw.AddCircle(position, size, OutlineColour, 12, Style.Width(StyleCatalogue.Keys.DotOutline, 1.5f));
            }

            if (ShowLabels && entity.Kind is EntityKind.Monster or EntityKind.Chest or EntityKind.WorldItem)
            {
                // The dot's own colour unless a label colour was chosen. Matching the dot is
                // what makes a label readable among forty of them, so it is the default
                // rather than something to configure your way back to.
                draw.AddText(
                    position + new Vector2(size + 3, -7),
                    Style[StyleCatalogue.Keys.DotLabel].ColourOr(colour),
                    entity.ShortName);
            }
        }

        // The player last, so it is never hidden under another dot.
        if (_snapshot.Player is WorldEntity player)
        {
            ScreenPoint point = ProjectGround(player, width, height);
            if (point.OnScreen && Style.Visible(StyleCatalogue.Keys.Player))
            {
                var position = new Vector2(point.X, point.Y);
                float size = Style.Sized(StyleCatalogue.Keys.Player, DotRadius + 2);
                if (!DrawIcon(draw, StyleCatalogue.Keys.Player, position, size))
                {
                    draw.AddCircleFilled(position, size, Style.Colour(StyleCatalogue.Keys.Player));
                    draw.AddCircle(position, size, OutlineColour, 16, Style.Width(StyleCatalogue.Keys.DotOutline, 2f));
                }
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
        uint centreColour = Style.Colour(StyleCatalogue.Keys.AidCentre);
        float centreWidth = Style.Width(StyleCatalogue.Keys.AidCentre, 1.5f);
        draw.AddLine(centre - new Vector2(24, 0), centre + new Vector2(24, 0), centreColour, centreWidth);
        draw.AddLine(centre - new Vector2(0, 24), centre + new Vector2(0, 24), centreColour, centreWidth);
        draw.AddText(centre + new Vector2(28, -7), centreColour, "screen centre");

        if (!ground.OnScreen || !healthbar.OnScreen)
        {
            return;
        }

        var groundPoint = new Vector2(ground.X, ground.Y);
        var healthbarPoint = new Vector2(healthbar.X, healthbar.Y);

        uint groundColour = Style.Colour(StyleCatalogue.Keys.AidGround);
        uint healthbarColour = Style.Colour(StyleCatalogue.Keys.AidHealthbar);

        DrawHealthbarReferences(draw, width, height);

        draw.AddLine(groundPoint, healthbarPoint, Style.Colour(StyleCatalogue.Keys.AidLink), Style.Width(StyleCatalogue.Keys.AidLink, 1f));
        draw.AddCircle(groundPoint, DotRadius + 6, groundColour, 20, Style.Width(StyleCatalogue.Keys.AidGround, 2f));
        draw.AddText(groundPoint + new Vector2(DotRadius + 9, 2), groundColour, "base (Render z)");
        draw.AddCircle(healthbarPoint, DotRadius + 4, healthbarColour, 20, Style.Width(StyleCatalogue.Keys.AidHealthbar, 2f));
        draw.AddText(healthbarPoint + new Vector2(DotRadius + 7, -14), healthbarColour, "health bar (z - ModelBounds)");
    }

    /// <summary>
    /// The live readout: what is being read, the pools, and what auto-flask is doing.
    /// </summary>
    /// <remarks>
    /// Stays visible during play, because a blank overlay is otherwise ambiguous - "nothing
    /// nearby" and "the read chain broke" look identical - and because "why did the flask
    /// not fire" is a question asked mid-session, not during a debugging pass. The
    /// projection measurements below it are the debugging pass, and hide behind --debug.
    /// </remarks>
    private void DrawStatusWindow(int width, int height)
    {
        ImGui.SetNextWindowPos(new Vector2(20, 20), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowBgAlpha(0.7f);

        if (ImGui.Begin("PoEformance", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoFocusOnAppearing))
        {
            if (!_snapshot.InGame)
            {
                // WHICH state, not just "no". A loading screen, the login screen and a
                // character select all draw nothing, and they are different situations -
                // saying "not in an area" over a loading screen reads as a broken read.
                string where = _snapshot.State switch
                {
                    GameStateKind.AreaLoading or GameStateKind.Loading => "loading",
                    GameStateKind.Login or GameStateKind.PreGame => "at the login screen",
                    GameStateKind.SelectCharacter or GameStateKind.CreateCharacter
                        or GameStateKind.DeleteCharacter => "at character select",
                    GameStateKind.Escape => "in the escape menu",
                    GameStateKind.InGame => "in game, but the area has not resolved",
                    GameStateKind.NotLoaded => "game not loaded",
                    GameStateKind.Unreadable => "state unreadable - falling back to the player pointer",
                    _ => $"in {_snapshot.State}",
                };

                ImGui.TextColored(new Vector4(1f, 0.6f, 0.2f, 1f), $"idle:     {where}");
            }
            else
            {
                // Named even when nothing is hidden. An overlay that goes blank in town
                // otherwise looks broken, and this is the line that says it is not - it
                // also reports the area id, which is what pins down any further rule about
                // WHERE the overlay should be active.
                AreaInfo area = _snapshot.Area;
                ImGui.TextColored(
                    area.WantsMarkers ? new Vector4(0.7f, 0.75f, 0.8f, 1f) : new Vector4(1f, 0.6f, 0.2f, 1f),
                    $"area:     {area.Describe()}{(area.WantsMarkers ? string.Empty : " - markers hidden")}");

                ImGui.Text($"entities: {_snapshot.Entities.Count}");

                // The question a map is actually being looked at for near the end of a run:
                // is there any of it left. Measured against what can be REACHED rather than
                // every walkable cell, which is why it is worth showing at all - the same
                // figure against the whole grid reads as a few per cent on a finished map.
                if (Coverage is MapCoverage walked && walked.Measuring)
                {
                    ImGui.TextColored(
                        new Vector4(0.65f, 0.75f, 0.68f, 1f),
                        $"walked:   {walked.Percent:F0}%   ({walked.SeenCells} of {walked.ReachableCells})"
                        + (walked.RegionKnown ? string.Empty : "  - still working out what is reachable"));
                }
                if (ReadStats is not null)
                {
                    (double ms, long reads, long failures) = ReadStats();
                    ImGui.Text($"read:     {ms:F1} ms on its own thread   frame: {1000f / ImGui.GetIO().Framerate:F1} ms"
                        + $"   ({reads} reads{(failures > 0 ? $", {failures} failed" : string.Empty)})");

                    // WHERE the time went, not just how much. One number says a frame was
                    // expensive and nothing about why, and an expensive phase is usually a
                    // redundancy rather than a fundamental cost - which only a breakdown shows.
                    if (_snapshot.Cost.TotalMs > 0)
                    {
                        ImGui.TextDisabled($"          {_snapshot.Cost}");
                    }
                }
                if (ShowDiagnostics)
                {
                    ImGui.Text(_tracked.IsValid
                        ? $"viewport: {width} x {height}  (game {_tracked.Width} x {_tracked.Height} @ {_tracked.X},{_tracked.Y})"
                        : $"viewport: {width} x {height}  (game window not tracked)");
                }

                if (ShowDiagnostics && _snapshot.Player is WorldEntity player)
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

                // In the overlay rather than only in the config window, because this is read
                // while standing on the hill that shows the problem - a readout that needs a
                // second window open is a readout nobody is looking at in that moment.
                if (ShowTerrain)
                {
                    ImGui.TextColored(new Vector4(0.65f, 0.7f, 0.78f, 1f), $"terrain:  {DescribeTerrain()}");
                }

                // Not behind --debug. The UI browser is a working tool rather than a
                // measurement, and its whole point is being reachable in the moment a panel
                // is open - which is not a moment anyone restarts the tool for.
                if (_poi is not null)
                {
                    bool picking = _poi.ShowPicker;
                    if (ImGui.Checkbox("Points of interest", ref picking))
                    {
                        _poi.ShowPicker = picking;
                        SettingsChanged?.Invoke();
                    }
                }

                if (_uiBrowser is not null)
                {
                    bool browsing = _uiBrowser.Visible;
                    if (ImGui.Checkbox("UI browser  (F8 picks what is under the cursor)", ref browsing))
                    {
                        _uiBrowser.Visible = browsing;
                    }
                }

                if (Noise is not null)
                {
                    bool filtering = Noise.Enabled;
                    if (ImGui.Checkbox("Hide noise  (effects, pets, daemons - off to see everything)", ref filtering))
                    {
                        Noise.Enabled = filtering;
                        SettingsChanged?.Invoke();
                    }
                }

                if (_costWindow is not null)
                {
                    bool costs = _costWindow.Visible;
                    if (ImGui.Checkbox("Read cost over time  (per phase, over a whole map)", ref costs))
                    {
                        _costWindow.Visible = costs;
                    }
                }

                if (_entityBrowser is not null)
                {
                    bool browsing = _entityBrowser.Visible;
                    if (ImGui.Checkbox("Entity browser  (components, including the undescribed ones)", ref browsing))
                    {
                        _entityBrowser.Visible = browsing;
                    }
                }

                if (_dissector is not null)
                {
                    bool dissecting = _dissector.Visible;
                    if (ImGui.Checkbox("Memory dissector  (raw structures, and what moves in them)", ref dissecting))
                    {
                        _dissector.Visible = dissecting;
                    }
                }

                if (_styleWindow is not null)
                {
                    bool styling = _styleWindow.Visible;
                    if (ImGui.Checkbox("Appearance  (colour, size and icon of everything drawn)", ref styling))
                    {
                        _styleWindow.Visible = styling;
                    }
                }

                bool hurtOnly = _healthBars.OnlyWhenHurt;
                if (ImGui.Checkbox("Health bars only once hurt  (off shows every monster's)", ref hurtOnly))
                {
                    _healthBars.OnlyWhenHurt = hurtOnly;
                    SettingsChanged?.Invoke();
                }

                if (_preload is not null && _preloadWindow is not null)
                {
                    // The summary next to the switch, so the common case needs no window at
                    // all: the answer is one line and it is already on screen.
                    string here = _preload.Summary();
                    bool showing = _preloadWindow.Visible;
                    if (ImGui.Checkbox(
                            here.Length > 0
                                ? $"In this area: {here}###preload"
                                : $"In this area  ({_preload.All.Count} files)###preload",
                            ref showing))
                    {
                        _preloadWindow.Visible = showing;
                    }
                }

                if (_alerts is not null && _alertWindow is not null)
                {
                    // The count is here rather than only in the window because "it has not
                    // said anything" and "it is not running" look identical, and this is a
                    // feature whose correct behaviour is mostly silence.
                    bool alerting = _alertWindow.Visible;
                    if (ImGui.Checkbox(
                            $"Alerts  ({_alerts.Rules.Count(rule => rule.Enabled)} watched for, {_alerts.Raised} raised)",
                            ref alerting))
                    {
                        _alertWindow.Visible = alerting;
                    }
                }

                // Only when there is one. A path that does not work otherwise shows up as a
                // marker drawn its ordinary way, which is exactly what not setting an icon
                // looks like - so the setting appears to do nothing at all.
                foreach (string problem in _icons.Files.Problems)
                {
                    ImGui.TextColored(new Vector4(1f, 0.55f, 0.4f, 1f), $"icon:     {problem}");
                }

                // The kind breakdown doubles as the filter, since "what is out there" and
                // "what do I want drawn" are the same question asked twice. Note the ##id
                // suffix: ImGui derives a control's identity from its label, so a label
                // carrying a live count would be a NEW control every frame and the click
                // would never register.
                if (ShowDiagnostics)
                {
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
            }

            if (!ShowDiagnostics)
            {
                ImGui.End();
                return;
            }

            ImGui.Separator();
            bool labels = ShowLabels;
            if (ImGui.Checkbox("labels", ref labels))
            {
                ShowLabels = labels;
                SettingsChanged?.Invoke();
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
    /// The large map wins when it is OPEN - the two swap, so both elements exist at all
    /// times and only their visibility says which one the player is looking at. Everything
    /// is clipped to the chosen map's rectangle: the projection places a marker by world
    /// distance, which lands far outside a minimap for anything further away than its edge,
    /// and a marker outside its map is just a dot in the middle of the game.
    /// </remarks>
    private void DrawMapDots()
    {
        if (_snapshot.Player is not WorldEntity player)
        {
            return;
        }

        MapView? chosen = _snapshot.LargeMap is MapView large && large.IsUsable && large.Visible ? large
            : _snapshot.MiniMap is MapView mini && mini.IsUsable && mini.Visible ? mini
            : null;

        if (chosen is not MapView map)
        {
            return; // neither map on screen - the player hid them, so hide with them
        }

        ImDrawListPtr draw = ImGui.GetBackgroundDrawList();

        // Under the markers: the layout is context, not the thing being looked for.
        if (ShowTerrain && Style.Visible(StyleCatalogue.Keys.Terrain) && _snapshot.Terrain is TerrainGrid terrain)
        {
            // The style wins where it says anything, and the settings page's colour and
            // thickness stand where it does not. Two places can set this and only one of them
            // was chosen deliberately, so the deliberate one goes on top rather than the two
            // fighting over a field.
            LayerStyle outline = Style[StyleCatalogue.Keys.Terrain];
            _terrain.Colour = outline.ColourOr(_terrainColour);
            _terrain.Thickness = (int)outline.WidthOr(_terrainThickness);

            _terrain.Draw(draw, map, terrain, new Vector3(player.WorldX, player.WorldY, player.TerrainHeight));
        }

        // Over the layout and under the markers: it is context for where to go next, and a
        // monster dot must never be lost behind it.
        if (Coverage is MapCoverage walked)
        {
            _unwalked.Draw(draw, map, walked, player);
        }

        float radius = map.IsLargeMap ? 3.5f : 2.5f;

        foreach (WorldEntity entity in _snapshot.Entities)
        {
            if (!DrawnKinds.Contains(entity.Kind) || !WorthDrawing(entity))
            {
                continue;
            }

            string key = KeyFor(entity);
            if (!Style.Visible(key))
            {
                continue;
            }

            Vector2 at = map.Project(
                entity.WorldX, entity.WorldY, entity.TerrainHeight,
                player.WorldX, player.WorldY, player.TerrainHeight);

            if (!map.Contains(at))
            {
                continue;
            }

            float size = Style.Sized(key, radius * SizeFor(entity));
            if (!DrawIcon(draw, key, at, size))
            {
                draw.AddCircleFilled(at, size, Style.Colour(key));
                draw.AddCircle(at, size, OutlineColour, 10, Style.Width(StyleCatalogue.Keys.DotOutline, 1f));
            }
        }

        // Over the entity dots: a landmark is what the map is being consulted for, so it wins
        // when the two land on the same pixel.
        _poi?.DrawOnMap(draw, map, _snapshot, player);

        if (ShowCalibration)
        {
            // The self-check for this projection: the player is the map's origin BY
            // CONSTRUCTION, so this ring must sit exactly on the marker the game draws for
            // the player on its own map. Nothing to eyeball and nothing to compare across
            // screenshots - the game supplies the reference every frame.
            uint colour = Style.Colour(StyleCatalogue.Keys.AidCentre);
            draw.AddCircle(map.Centre, 9f, colour, 20, Style.Width(StyleCatalogue.Keys.AidCentre, 2f));
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
        uint colour = Style.Colour(StyleCatalogue.Keys.AidHealthbar);
        float line = Style.Width(StyleCatalogue.Keys.AidHealthbar, 2f);
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
            draw.AddLine(at - new Vector2(18, 0), at - new Vector2(6, 0), colour, line);
            draw.AddLine(at + new Vector2(6, 0), at + new Vector2(18, 0), colour, line);
            draw.AddLine(at - new Vector2(18, 4), at - new Vector2(18, -4), colour, line);
            draw.AddLine(at + new Vector2(18, 4), at + new Vector2(18, -4), colour, line);
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

    /// <summary>
    /// Whether an entity passes the loot filter. Everything that is not a drop passes.
    /// </summary>
    /// <remarks>
    /// A drop whose rarity has not resolved yet is DRAWN. It is one frame old at most, and
    /// showing a drop that turns out to be junk costs a moment of attention, while hiding
    /// one that turns out to be a unique costs the drop.
    /// </remarks>
    private bool WorthDrawing(WorldEntity entity)
    {
        if (entity.Kind != EntityKind.WorldItem)
        {
            return true;
        }

        return entity.Rarity switch
        {
            ItemRarity.Currency => true,
            ItemRarity.Unknown => true,
            _ => entity.Rarity >= MinimumLootRarity,
        };
    }

    /// <summary>Dark outline so dots stay readable over any background.</summary>
    private uint OutlineColour => Style.Colour(StyleCatalogue.Keys.DotOutline);

    /// <summary>
    /// Which style entry an entity is drawn from: drops and monsters by rarity, the rest by
    /// kind.
    /// </summary>
    /// <remarks>
    /// The game's own item-label colours are the DEFAULTS behind these keys, because that is
    /// the association already learned - a yellow dot means the same thing on the floor and on
    /// the overlay, with nothing to translate. A monster's rarity is drawn from the same
    /// palette for the same reason: which of forty dots is the rare pack leader is the
    /// question a monster radar is being consulted for.
    /// </remarks>
    private static string KeyFor(WorldEntity entity) => entity.Kind switch
    {
        EntityKind.WorldItem => StyleCatalogue.ForRarity("entity.item", entity.Rarity),
        EntityKind.Monster => StyleCatalogue.ForRarity("entity.monster", entity.Rarity),
        _ => StyleCatalogue.ForKind(entity.Kind),
    };

    /// <summary>
    /// How much bigger a dot is than the ordinary one, before the chosen scale.
    /// </summary>
    /// <remarks>
    /// Colour alone is not enough for the thing that matters most here. A rare pack leader
    /// among forty red dots has to be findable in the corner of an eye, and on a busy map at
    /// three pixels a hue is the first thing lost - size survives where colour does not.
    /// </remarks>
    public static float SizeFor(WorldEntity entity) => entity.Kind == EntityKind.Monster
        ? entity.Rarity switch
        {
            ItemRarity.Unique => 1.9f,
            ItemRarity.Rare => 1.5f,
            ItemRarity.Magic => 1.2f,
            _ => 1f,
        }
        : 1f;

    private static uint Pack(float r, float g, float b)
        => ImGui.ColorConvertFloat4ToU32(new Vector4(r, g, b, 0.9f));
}
