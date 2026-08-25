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
    /// What the game has stopped listing and this keeps drawing, when the reader is sharing it.
    /// </summary>
    /// <remarks>
    /// Held rather than copied, for the same reason the noise filter is: it is a switch the
    /// reader owns and this window is where somebody reaches for it.
    /// </remarks>
    public EntityMemory? Memory { get; set; }

    /// <summary>
    /// Whether the reader keeps the hostile ground effects, for the same reason as above.
    /// </summary>
    /// <remarks>
    /// A pair of callbacks rather than the reader itself: the overlay draws and has no other
    /// business with it, and this is the one bit of it worth reaching for from up here.
    /// </remarks>
    public Func<bool>? KeepingEffects { get; set; }

    /// <summary>Turns that dropping off and on.</summary>
    public Action<bool>? KeepEffects { get; set; }

    /// <summary>
    /// Tells the reader whether to read the game's visual entities, where the projectiles are.
    /// </summary>
    /// <remarks>
    /// Driven by the projectile layer rather than offered as a switch of its own, and that is
    /// one decision rather than two on purpose. Without it no projectile reaches a snapshot at
    /// all - see <see cref="WorldReader.ReadVisualEntities"/> - so a separate switch would be a
    /// second thing to find before the first one did anything, which is exactly the overlay
    /// that drew nothing over a screen of Spark. Turning the layer off takes the read cost with
    /// it, so nobody pays for a feature they are not looking at.
    /// </remarks>
    public Action<bool>? ReadVisuals { get; set; }

    /// <summary>
    /// Tells the reader whether to read what is ON the monsters, for the status icons.
    /// </summary>
    /// <remarks>
    /// The same arrangement as the visual entities above and for the same reason: without the
    /// read no monster carries buffs, so a separate switch would be a second thing to find
    /// before the first one drew anything - and with it every rare-or-better monster costs a
    /// component walk nothing else in the tool wants. Driven by the settings that ask for it,
    /// so switching the icons off takes the cost with them.
    /// </remarks>
    public Action<bool>? ReadMonsterBuffs { get; set; }

    /// <summary>
    /// Tells the reader whether to read where things POINT and what they are doing.
    /// </summary>
    /// <remarks>
    /// The third of these, on the same terms: two reads per monster that only the aim rays want,
    /// switched on by the settings that ask for them and taken away again with them.
    /// </remarks>
    public Action<bool>? ReadAim { get; set; }

    /// <summary>
    /// What the tracker draws - lines to monsters, rings on dangerous ground, status icons.
    /// </summary>
    /// <remarks>
    /// ONE record shared by three layers, set through here rather than on each: they are edited
    /// together in one tab, and three copies of the settings would be three things to keep in
    /// step. The layers read it on the render thread every frame, so an edit lands on the next.
    /// </remarks>
    public TrackerSettings Tracker
    {
        get => _tracker;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _tracker = value;
            _monsterLines.Settings = value;
            _groundDanger.Settings = value;
            _statusIcons.Settings = value;
            _aim.Settings = value;
        }
    }

    private TrackerSettings _tracker = TrackerSettings.Default;

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
            _preloadPanel.Style = value;
            _preloadEntry.Style = value;
            _unwalked.Style = value;
            _heat.Style = value;
            _healthBars.Style = value;
            _projectiles.Style = value;
            _atlas.Style = value;
        }
    }

    private OverlayStyle _style = new();

    /// <summary>
    /// How the tool's OWN windows are drawn - text size, and how solid the panels are.
    /// </summary>
    /// <remarks>
    /// NOT APPLIED WHERE IT IS SET, and that is the whole reason this is a field with a flag
    /// beside it rather than a property that does the work. ImGui's style and its font atlas
    /// belong to the render thread's context, and the first thing that sets this is the saved
    /// settings being applied while the app wires itself up - on the main thread, before that
    /// context exists at all. Touching ImGui from there is a crash, and one that would only
    /// happen on a machine slow enough to lose the race.
    ///
    /// So a change is only RECORDED here, and the next frame picks it up - see
    /// <see cref="ApplyInterfaceIfAsked"/>. That also gives the font swap somewhere sensible
    /// to live: rebuilding the atlas is expensive and must not happen on a frame that did not
    /// ask for it.
    /// </remarks>
    public InterfaceStyle Interface
    {
        get => _interface;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _interface = value.Normalised();
            _interfaceWanted = true;

            // The tool window is the only one told: the preload panel's solidity is a
            // catalogue entry of its own (StyleCatalogue.Keys.PreloadListBack), and two
            // settings for one window is one of them being ignored.
            _tools.Interface = _interface;
        }
    }

    private InterfaceStyle _interface = InterfaceStyle.Default;

    // Volatile because the two ends are different threads: the wiring and the configuration
    // window ask, the render thread answers. Nothing here needs to be ordered against anything
    // else - a change landing one frame later is invisible - but a flag that is never re-read
    // is a setting that never arrives, and that failure would only show on somebody else's
    // machine.
    private volatile bool _interfaceWanted = true;
    private int _wearing;

    /// <summary>
    /// Puts the chosen text size and palette on, on a frame that asked for them.
    /// </summary>
    /// <remarks>
    /// Called at the top of the frame rather than part-way through it, so that no window is
    /// drawn half in one palette and half in the next.
    ///
    /// The FONT is only touched when the size actually changed, because a change means
    /// rebuilding the atlas and re-uploading the texture. The palette costs a couple of
    /// hundred vector writes and is simply reapplied.
    /// </remarks>
    private void ApplyInterfaceIfAsked()
    {
        if (!_interfaceWanted)
        {
            return;
        }

        _interfaceWanted = false;
        OverlayTheme.Apply(_interface);

        if (_wearing != _interface.TextSizeOr)
        {
            WearASerif(_interface.TextSizeOr);
        }
    }

    /// <summary>
    /// Which windows are pinned in place or handed over to the mouse.
    /// </summary>
    /// <remarks>
    /// ONE of these, like the style, and for the same reason: every window reads it and the
    /// settings write it, so a copy per window is a copy that drifts. Handed to the windows
    /// that draw themselves rather than reached for statically, so a test can hand them
    /// another.
    /// </remarks>
    public WindowChrome Chrome { get; } = new();

    /// <summary>Hands the shared window rules to everything that opens a window.</summary>
    /// <remarks>
    /// Called from the constructor rather than assigned where each window is built, because
    /// two of them are created there and one arrives later - and a window that missed the
    /// assignment silently keeps its own empty copy, which reads as a lock that did nothing.
    /// </remarks>
    private void ShareChrome()
    {
        _tools.Chrome = Chrome;
        _preloadPanel.Chrome = Chrome;
        Chrome.Changed = () => SettingsChanged?.Invoke();
        _tools.HiddenChanged = () => SettingsChanged?.Invoke();
    }

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
        HideBehindPanels = settings.HideBehindPanels;
        HideWindowsBehindPanels = settings.HideWindowsBehindPanels;
        _projectiles.Enabled = settings.ShowProjectiles;
        _projectiles.ShowTrails = settings.ProjectileTrails;
        _projectiles.ShowPaths = settings.ProjectilePaths;
        _projectiles.MineOnly = settings.ProjectilesMineOnly;
        Interface = settings.InterfaceOrDefault;
        Chrome.Apply(settings.WindowsOrEmpty);
        _tools.ApplyHidden(settings.HiddenTabsOrEmpty);

        if (Noise is not null)
        {
            Noise.Enabled = settings.HideNoise;
        }

        if (Memory is not null)
        {
            Memory.Enabled = settings.RememberOutOfRange;
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
            HideBehindPanels = HideBehindPanels,
            HideWindowsBehindPanels = HideWindowsBehindPanels,
            ShowProjectiles = _projectiles.Enabled,
            ProjectileTrails = _projectiles.ShowTrails,
            ProjectilePaths = _projectiles.ShowPaths,
            ProjectilesMineOnly = _projectiles.MineOnly,

            // Only once it differs from the defaults, so an untouched file gains no key and a
            // default corrected in a release still reaches somebody who never opened the
            // sliders - the same bargain the window rules and the marker styles make.
            Interface = _interface == InterfaceStyle.Default ? basis.Interface : _interface,
            Windows = Chrome.Saved(),
            HiddenTabs = _tools.Hidden() is { Length: > 0 } hiddenTabs ? hiddenTabs : null,
            HideNoise = Noise?.Enabled ?? basis.HideNoise,
            RememberOutOfRange = Memory?.Enabled ?? basis.RememberOutOfRange,
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

    /// <summary>
    /// How much damage is being done, if it is being measured.
    /// </summary>
    /// <remarks>
    /// Fed on the READER thread - the meter takes one sample per snapshot, and sampling it
    /// from here instead would count every VSync frame that redraws an unchanged snapshot as
    /// a reading in which no damage happened. This side only reads the figures.
    /// </remarks>
    public DamageMeter? Damage { get; set; }

    /// <summary>The tabbed window every tool lives in.</summary>
    /// <remarks>
    /// The tools below register themselves here as they are attached; what remains of each
    /// window class is its tab's content and the work it does while not in front. The order
    /// passed with each registration pins the tab's place in the bar - play-time tools left
    /// of the reverse-engineering ones - rather than leaving it to attach order, which is
    /// app wiring and not an interface decision.
    /// </remarks>
    private readonly ToolTabs _tools = new();

    // The tools something elsewhere in the overlay jumps to by name.
    private const string UiBrowserTab = "uibrowser";
    private const string DissectorTab = "dissector";

    /// <summary>
    /// The pages that hold more than one tool.
    /// </summary>
    /// <remarks>
    /// Named constants rather than strings at each registration, because a page is a JOIN:
    /// the tools that share one are wired up in different methods, at different times, and a
    /// typo in one of them would not fail - it would quietly open a page of its own with one
    /// tool on it, which is the fourteen-tab bar growing back a tab at a time.
    ///
    /// Grouped by the question they answer rather than by what they read. "How much damage am
    /// I doing" and "where did my projectiles go" are one question asked twice, and so are
    /// "what is that thing" and "what is inside it".
    /// </remarks>
    private const string Status = "status";
    private const string Area = "area";
    private const string Combat = "combat";
    private const string Atlas = "atlas";
    private const string Entities = "entities";

    // Only the tools something else still reaches for keep a field; the rest live on as
    // their tab's callbacks and nothing more. A field that nothing reads is a claim that
    // somebody talks to that window, and here nobody does.
    private CostWindow? _costWindow;
    private EffectWindow? _effectWindow;
    private ProjectileWindow? _projectileWindow;
    private DamageWindow? _damageWindow;
    private UiBrowserWindow? _uiBrowser;
    private DissectorWindow? _dissector;
    private PoiLayer? _poi;
    private RoutePlanner? _planner;
    private AlertWatcher? _alerts;
    private readonly AlertBanner _banner = new();
    private readonly RuleLayer _rules = new();

    /// <summary>What the rule engine decided to show this tick, or null when it is not wired.</summary>
    /// <remarks>
    /// A function rather than the engine itself, and the reason is the layering: the engine is
    /// evaluated on the READER thread, once per read, and the renderer redraws far more often
    /// than that. Handing the overlay the engine would invite it to evaluate per frame, which
    /// is how the reference plugin ends up with a macro whose firing rate depends on the frame
    /// rate.
    /// </remarks>
    private Func<IReadOnlyList<RuleDrawing>>? _ruleDrawings;

    /// <summary>The ranges the rule being edited measures, for the debug view.</summary>
    private Func<IReadOnlyList<PreviewRing>>? _ruleRanges;
    private Func<IReadOnlyList<PreviewFact>>? _ruleFacts;
    private readonly UnwalkedLayer _unwalked = new();
    private readonly HeatLayer _heat = new();
    private readonly EffectLayer _effects = new();
    private readonly HealthBarLayer _healthBars = new();

    // The tracker's three layers, which share one settings record - see AttachTracker.
    private readonly MonsterLineLayer _monsterLines = new();
    private readonly GroundDangerLayer _groundDanger = new();
    private readonly StatusIconLayer _statusIcons = new();
    private readonly AimLayer _aim = new();

    /// <summary>
    /// The animation table, for saying what a monster is doing rather than which number it is.
    /// </summary>
    /// <remarks>
    /// Loaded from a data file by whoever wires this up, like every other table here - the
    /// overlay draws, and where a file lives is not its business. Absent, the numbers still
    /// show; a table is a label and never a fact.
    /// </remarks>
    public AnimationNames Animations
    {
        get => _animations;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _animations = value;
            _aim.Animations = value;
        }
    }

    private AnimationNames _animations = AnimationNames.Empty;

    // The trails belong to the LAYER's question rather than the reader's, so they are kept
    // here: a projectile's path across the screen is something only a thing that sees every
    // snapshot in order can assemble, and the reader hands out one snapshot at a time.
    private readonly ProjectileLayer _projectiles = new();
    private readonly ProjectileWatch _projectileWatch = new();
    private readonly AtlasLayer _atlas = new();
    private AtlasWatch? _atlasWatch;
    private readonly RitualLayer _ritual = new();
    private RitualWatch? _ritualWatch;
    private RitualWindow? _ritualWindow;

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
        var window = new AlertWindow(watcher, saved);
        _alertWindow = window;
        _tools.Add(10, "alerts", "Alerts", window.DrawTab, window.Idle);

        // The banner's colours, beside the rules that fire it rather than on a style page
        // two tabs away - a feature's looks live with the feature.
        var styles = new StyleRows(Style, SaveStyle, StyleCatalogue.Homes.Alerts);
        _tools.Add(11, "alerts-style", "How it looks", styles.Draw, styles.Idle, page: "alerts");

        if (visible)
        {
            _tools.Show("alerts");
        }

        AlertsChanged = saved;

        // The preload list is edited in this window, and it may have been attached first -
        // both orders happen, so neither attach assumes it went second.
        if (_preload is not null && PreloadRulesChanged is not null)
        {
            _alertWindow.AttachPreload(_preload, _preloadSettings, TookPreload, SayItNow);
        }
    }

    /// <summary>Called when the preload alerts changed, so they can be written down.</summary>
    /// <remarks>
    /// Their own callback rather than <see cref="AlertsChanged"/>: they are a different list
    /// in a different file, and one save that wrote both would make deleting an entity rule
    /// rewrite the preload rules too.
    /// </remarks>
    public Action<PreloadSettings>? PreloadRulesChanged { get; set; }

    /// <summary>Takes a change from either editor and passes it on.</summary>
    private void TookPreload(PreloadSettings changed)
    {
        _preloadSettings = changed;
        _preloadPanel.Enabled = changed.List;
        PreloadRulesChanged?.Invoke(changed);
    }

    private AlertWindow? _alertWindow;
    private PreloadWatch? _preload;
    private PreloadSettings _preloadSettings = PreloadSettings.Default;
    private readonly PreloadPanel _preloadPanel = new();
    private readonly PreloadEntryBanner _preloadEntry = new();

    /// <summary>The area whose findings have already been said out loud.</summary>
    /// <remarks>
    /// Kept so the banner fires ONCE per area rather than on every frame that finds a
    /// finding. Zero is not an area, so it also covers the state before anything was read.
    /// </remarks>
    private uint _announced;

    /// <summary>
    /// Adds the "what is in this area" list.
    /// </summary>
    /// <remarks>
    /// The walk that fills it belongs to whoever owns the reading - it is far too expensive
    /// for a tick and runs once per area on its own thread. This only shows the answer, and
    /// offers the button that forces another look.
    /// </remarks>
    /// <param name="settings">What the preload alerts do - the banner, the list, the gate.</param>
    /// <param name="rulesChanged">
    /// Writes the rules down. Called from the raw-list window as well as the editor, because
    /// a rule added by pressing "+ watch" on a path is exactly as permanent as a typed one.
    /// </param>
    public void AttachPreload(
        PreloadWatch watch,
        Action lookAgain,
        Action sweep,
        PreloadSettings settings,
        Action<PreloadSettings> rulesChanged,
        bool visible = false)
    {
        ArgumentNullException.ThrowIfNull(watch);
        ArgumentNullException.ThrowIfNull(lookAgain);
        ArgumentNullException.ThrowIfNull(sweep);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(rulesChanged);
        _preload = watch;
        _preloadSettings = settings;
        _preloadPanel.Enabled = settings.List;
        PreloadRulesChanged = rulesChanged;
        var window = new PreloadWindow(
            watch,
            lookAgain,
            sweep,
            () => TookPreload(_preloadSettings with { Rules = watch.Rules }));
        _tools.Add(20, "preload", "In this area", window.DrawTab, page: Area, pageLabel: "Area");

        // The list's backings and the three weight markers, under the list they draw.
        var styles = new StyleRows(Style, SaveStyle, StyleCatalogue.Homes.Area);
        _tools.Add(
            21, "preload-style", "How the loaded list looks", styles.Draw, styles.Idle,
            page: Area, pageLabel: "Area");

        if (visible)
        {
            _tools.Show("preload");
        }

        // The editor for these lives in the alerts window, which may already be attached -
        // both orders happen depending on how the app is wired, so neither is assumed.
        _alertWindow?.AttachPreload(watch, settings, TookPreload, SayItNow);
    }

    /// <summary>
    /// Says what this area loaded, once, on the way in.
    /// </summary>
    /// <remarks>
    /// Fired from the AREA the findings belong to rather than from a flag the reader sets,
    /// because the walk finishes on its own thread some moments after the zone changed - so
    /// "has it been said for this area" is the only question that has a stable answer here.
    ///
    /// The area is marked as said even when there was nothing to say. Otherwise every frame
    /// in a quiet map re-asks a question already answered, and worse, a rule added mid-map
    /// would announce an area you have been standing in for ten minutes.
    /// </remarks>
    private void AnnounceWhatLoaded(long nowMs)
    {
        if (_preload is not { Looked: true } watch || !_preloadSettings.Banner)
        {
            return;
        }

        uint area = watch.Area;
        if (area == 0 || area == _announced)
        {
            return;
        }

        _announced = area;

        IReadOnlyList<(PreloadWeight Weight, string Names)> saying =
            watch.AnnounceableByWeight(_preloadSettings.MinFiles);

        if (saying.Count > 0)
        {
            _preloadEntry.Announce(saying, nowMs);
        }
    }

    /// <summary>Says the card again for the area already being stood in.</summary>
    /// <remarks>
    /// Bypasses the once-per-area guard on purpose - this is somebody asking to see it, which
    /// is a different question from whether it has already been said. It goes through the same
    /// gates otherwise, so a card with nothing in it stays a card with nothing in it.
    /// </remarks>
    private void SayItNow()
    {
        if (_preload is not { Looked: true } watch)
        {
            return;
        }

        IReadOnlyList<(PreloadWeight Weight, string Names)> saying =
            watch.AnnounceableByWeight(_preloadSettings.MinFiles);

        if (saying.Count > 0)
        {
            _preloadEntry.Announce(saying, Environment.TickCount64);
        }
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
        _styleSaved = saved;
        var window = new StyleWindow
        {
            Chrome = Chrome,

            // The interface's own size and solidity, which are kept in the OVERLAY settings
            // rather than in the style file - so the writing down is SettingsChanged's, not
            // this editor's own save. Applied the moment it changes and written down when the
            // drag stops, which is the same bargain every other control on this page makes.
            Interface = new StyleWindow.InterfaceEditor(
                () => Interface,
                chosen => Interface = chosen,
                () => SettingsChanged?.Invoke()),

            // This page hosts the list, so it is the page the list must never offer to
            // hide - see DrawHideList.
            TabList = () => _tools.DrawHideList("style"),
        };

        // What is drawn on the game WORLD - markers, routes, the layout, the aids - has no
        // feature tab to be styled on, so it gets a page of its own, right before the page
        // about the tool itself. The feature styles sit with their features; see
        // StyleCatalogue.Homes for the split.
        var markers = new StyleRows(Style, SaveStyle, StyleCatalogue.Homes.Markers);
        _tools.Add(
            65,
            "markers",
            "Markers",
            () =>
            {
                markers.DrawResetLine();
                markers.Draw();
            },
            markers.Idle);

        _tools.Add(70, "style", "Appearance", window.DrawTab, window.Idle);
        if (visible)
        {
            _tools.Show("style");
        }
    }

    /// <summary>
    /// Writes the marker styles down, once the editor that knows how has been attached.
    /// </summary>
    /// <remarks>
    /// Late-bound because the feature tabs carry style rows too, and their attach methods can
    /// run before <see cref="AttachStyleEditor"/> brings the save action - both orders happen
    /// depending on how the app is wired. Until it arrives, edits still apply live; they are
    /// written down with the next change after the wiring completes.
    /// </remarks>
    private void SaveStyle() => _styleSaved?.Invoke();

    private Action? _styleSaved;

    /// <summary>
    /// Adds the tracker: lines to monsters, rings on dangerous ground, status icons.
    /// </summary>
    /// <remarks>
    /// Its editor is a TAB rather than a page in the configuration window, and that is forced
    /// by what has to be typed into it. A status rule matches on the game's own internal buff
    /// name - a spelling nobody is ever shown - so the only way to write one is to have the
    /// effect on you and read the list of what you are carrying, which means the editor has to
    /// be open while playing.
    /// </remarks>
    public void AttachTracker(TrackerSettings settings, Action<TrackerSettings> saved, bool visible = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(saved);

        Tracker = settings;
        var window = new TrackerWindow(
            () => Tracker,
            changed =>
            {
                Tracker = changed;
                saved(changed);
            },
            () => Tracker.IconSheet.Length == 0
                ? default
                : _icons.PictureFor(Tracker.IconSheet, IconCache.MaxSheetEdge),
            () => Animations);

        _tools.Add(
            32, "tracker", "Tracker", () => window.DrawTab(_snapshot), page: Combat, pageLabel: "Combat");

        if (visible)
        {
            _tools.Show("tracker");
        }
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

    /// <summary>
    /// Add the projection measurements and the per-kind filters to the readout.
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

        // The entry card takes its plates from the same cache the markers use, so a picture
        // that cannot be loaded is reported in one place and given up on once.
        _preloadEntry.Icons = _icons;

        // And the status icon sheet out of the same one, for the same reason: one texture per
        // file, and one place that says why a file did not load.
        _statusIcons.Icons = _icons;

        // FIRST, and registered here rather than where the tools attach, because this one is
        // not attached by anybody: it is this class's own readout, it is what the window shows
        // when nothing else is asked for, and being the leading page is what makes the window
        // size itself to a dozen short lines during a fight. Order 0 keeps it leftmost however
        // the rest are numbered.
        _tools.Add(
            0, Status, "Live readout", () => DrawStatusTab(_viewport.X, _viewport.Y),
            page: Status, pageLabel: "Status");

        ShareChrome();
    }

    /// <summary>
    /// The viewport as the last frame measured it, for the pages that need it.
    /// </summary>
    /// <remarks>
    /// A field because a tab's content is a callback registered once, while the size arrives
    /// per frame. The readout is the only page that cares - it reports the viewport and
    /// projects the player through it - and one frame's lag is not a thing anybody could see.
    /// </remarks>
    private (int X, int Y) _viewport;

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
    /// Stop drawing in world space while a screen-filling panel is open.
    /// </summary>
    /// <remarks>
    /// ON by default, because it is what somebody expects: markers over a stash or health bars
    /// across the passive tree are right information in the way, which is worse than none.
    ///
    /// It is a switch rather than a rule because the panels are found at unverified offsets and
    /// paths, and the way a wrong one fails is the worst kind - a panel reported open forever,
    /// so the overlay never comes back and nothing says why. The status window prints which
    /// panels are being reported so a stuck one names itself, and this turns the question off
    /// entirely for anybody who would rather have the markers than the tidiness.
    /// </remarks>
    public bool HideBehindPanels { get; set; } = true;

    /// <summary>
    /// Whether the tool's own WINDOWS get out of the way of an open panel as well.
    /// </summary>
    /// <remarks>
    /// The same idea as <see cref="HideBehindPanels"/> and deliberately its own switch, because
    /// what the two cost when they are wrong is not the same. A marker layer that hides itself
    /// leaves the game exactly as it was; a window that hides itself takes controls with it -
    /// the tabs, the readout, and the Appearance page holding these two checkboxes.
    ///
    /// ONLY THE WINDOWS ACTUALLY OVER A PANEL, which is what makes it worth having on: the
    /// corner readout stays where it is while an inventory is open on the other side of the
    /// screen, and goes when the passive tree takes the whole screen. See
    /// <see cref="WindowChrome.Covered"/>, which also records the ways back.
    /// </remarks>
    public bool HideWindowsBehindPanels { get; set; } = true;

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
        var window = new UiBrowserWindow(inspector) { Style = _style };
        _uiBrowser = window;
        _tools.Add(
            100, UiBrowserTab, "Interface tree", window.DrawTab, window.Idle, page: Entities);
        if (visible)
        {
            _tools.Show(UiBrowserTab);
        }
    }

    /// <summary>
    /// Adds the atlas overlay, served by a watch on the reader thread.
    /// </summary>
    /// <remarks>
    /// Attached rather than constructed here for the same reason as the browser: this class
    /// draws, and the atlas is read by something else. What it hands over is a finished view.
    /// </remarks>
    /// <summary>Adds the rule engine's captions and bars.</summary>
    /// <remarks>
    /// Takes what to DRAW rather than the engine, so nothing here can trigger an evaluation.
    /// See <see cref="_ruleDrawings"/>.
    /// </remarks>
    public void AttachRules(
        Func<IReadOnlyList<RuleDrawing>> drawings,
        Func<IReadOnlyList<PreviewRing>>? ranges = null,
        Func<IReadOnlyList<PreviewFact>>? facts = null)
    {
        ArgumentNullException.ThrowIfNull(drawings);
        _ruleDrawings = drawings;
        _ruleRanges = ranges;
        _ruleFacts = facts;
    }

    public void AttachAtlas(AtlasWatch watch, Action<AtlasSettings> saved, bool visible = false)
    {
        ArgumentNullException.ThrowIfNull(watch);
        ArgumentNullException.ThrowIfNull(saved);
        _atlasWatch = watch;
        var window = new AtlasWindow(watch, saved);
        _tools.Add(40, "atlas", "Atlas", window.DrawTab, window.Idle, page: Atlas, pageLabel: "Atlas");

        // The atlas's plate, web and route colours, between the atlas and the ritual line
        // that draws onto it.
        var styles = new StyleRows(Style, SaveStyle, StyleCatalogue.Homes.Atlas);
        _tools.Add(
            41, "atlas-style", "How the atlas looks", styles.Draw, styles.Idle,
            page: Atlas, pageLabel: "Atlas");

        if (visible)
        {
            _tools.Show("atlas");
        }

        _atlas.Style = _style;
    }

    /// <summary>
    /// Adds the stash listing.
    /// </summary>
    /// <remarks>
    /// A tab and nothing else: it draws nothing over the game, because what it answers -
    /// what have I got, and where - is not a question anybody asks mid-fight.
    /// </remarks>
    public void AttachStash(
        StashInspector inspector,
        ItemArtStore art,
        PriceStore prices,
        TradePrices trade,
        Action signIn,
        bool visible = false)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        ArgumentNullException.ThrowIfNull(art);
        ArgumentNullException.ThrowIfNull(prices);
        ArgumentNullException.ThrowIfNull(trade);
        ArgumentNullException.ThrowIfNull(signIn);

        // The icon cache is the overlay's, because uploading a texture needs the renderer -
        // so the window is handed a way to turn a file into something drawable rather than
        // being given the renderer itself. The sign-in is handed in for the same shape of
        // reason: the trade window is a browser, and this layer cannot see that one.
        var window = new StashWindow(
            inspector, art, prices, trade, signIn,
            file => _icons.PictureFor(file, IconCache.MaxWideEdge).Texture);
        _tools.Add(60, "stash", "Stash", window.DrawTab);
        if (visible)
        {
            _tools.Show("stash");
        }
    }

    /// <summary>
    /// Adds the quest list: every quest and the step it is waiting on.
    /// </summary>
    /// <remarks>
    /// Its own tab rather than a line somewhere, because it is a LIST and the thing people go
    /// looking for in it is the quest they forgot - which needs the whole act on screen at
    /// once, not the one the game happens to be tracking.
    /// </remarks>
    public void AttachQuests(QuestWatch watch, bool visible = false)
    {
        ArgumentNullException.ThrowIfNull(watch);
        var window = new QuestWindow(watch);
        _tools.Add(25, "quests", "Quests", window.DrawTab, page: Area);

        // Its own tab rather than a fold inside the quests one. It answers a different
        // question - "what is my map still hiding" against "what am I supposed to do next" -
        // and it rides the same read, so it costs a tab and nothing else.
        var pins = new MapPinWindow(watch);
        _tools.Add(26, "mappins", "Map pins", pins.DrawTab, page: Area);

        if (visible)
        {
            _tools.Show("quests");
        }
    }

    /// <summary>
    /// Adds the ritual-line planner, which rides the atlas read.
    /// </summary>
    /// <remarks>
    /// Separate from the atlas even though it shares its read: it answers a different question,
    /// it is idle unless a line is being drawn, and its tab is a list somebody works through
    /// rather than a set of switches.
    /// </remarks>
    public void AttachRitual(
        RitualWatch watch,
        Func<IReadOnlyDictionary<string, int>> worth,
        Action<IReadOnlyDictionary<string, int>> apply,
        Action<IReadOnlyDictionary<string, int>> save,
        bool visible = false)
    {
        ArgumentNullException.ThrowIfNull(watch);
        _ritualWatch = watch;
        var window = new RitualWindow(watch, worth, apply, save);
        _ritualWindow = window;
        _tools.Add(50, "ritual", "Ritual line", window.DrawTab, page: Atlas);
        if (visible)
        {
            _tools.Show("ritual");
        }
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
        var window = new DissectorWindow(inspector);
        _dissector = window;
        _tools.Add(110, DissectorTab, "Dissector", window.DrawTab, window.Idle, page: Entities);
        if (visible)
        {
            _tools.Show(DissectorTab);
        }
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

        // The jump also brings the dissector's tab in front - "show me this" used to mean
        // opening that window, and with tabs it means switching to it. Only for a real
        // address: Show ignores zero, and a tab switch to an unchanged dissector would
        // just take the browser away from under the click that asked for it.
        // The planner is read through the field rather than captured, because the two Attach
        // calls have no fixed order and a captured null would silently mean "this build has
        // no routes" - which looks exactly like a broken feature.
        var window = new EntityBrowserWindow(
            inspector,
            (address, label, layout) =>
            {
                if (address != 0 && _dissector is not null)
                {
                    _dissector.Show(address, label, layout);
                    _tools.Show(DissectorTab);
                }
            },
            (address, worldX, worldY) => _planner?.Toggle(address, worldX, worldY),
            address => _planner?.IsTarget(address) ?? false,
            (address, label) =>
            {
                // Brings the dissector forward exactly as "dissect" does, and for the same
                // reason: the new place appearing beside the others IS the confirmation that
                // the click landed. Adding a third costs one tab click back, which is cheap
                // next to a button that looks like it did nothing.
                if (address != 0 && _dissector is not null && _dissector.Compare(address, label))
                {
                    _tools.Show(DissectorTab);
                }
            });
        _tools.Add(
            90, "entities", "Entity browser", () => window.DrawTab(_snapshot, _snapshot.Player),
            window.Idle, page: Entities, pageLabel: "Entities");
        if (visible)
        {
            _tools.Show("entities");
        }
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
        // Kept as well as handed on: the entity browser routes to whatever is selected, and
        // it is attached separately from this.
        _planner = planner;

        _poi = new PoiLayer(planner)
        {
            Style = _style,
            Chrome = Chrome,
            IconFor = _icons.TextureFor,
        };
    }

    protected override Task PostInitialized()
    {
        VSync = true;

        // The palette and the font both land on the first frame instead of here, so there is
        // ONE path that puts the interface on rather than one for start-up and another for a
        // change - the second of which is the one that gets forgotten. See
        // <see cref="ApplyInterfaceIfAsked"/>.
        return Task.CompletedTask;
    }

    /// <summary>
    /// Faces to try for the interface, best first.
    /// </summary>
    /// <remarks>
    /// FONTS WINDOWS ALREADY HAS, rather than one shipped with the tool. A face that can be
    /// redistributed is a licence somebody has to read and a file that has to travel; these
    /// are on every Windows since Vista and cost nothing but the attempt.
    ///
    /// Serifs, because the thing being drawn over is a game whose own lettering is carved and
    /// gilded, and the default here is a 13-pixel bitmap face designed for debug output. On a
    /// painted banner that reads as a placeholder - which, in fairness, is what it was.
    /// </remarks>
    private static readonly string[] Serifs =
    [
        "constan.ttf",   // Constantia - warm, and the closest of these to the game's own
        "georgia.ttf",   // Georgia - the most legible at a small size
        "pala.ttf",      // Palatino Linotype - the last resort that is still a serif
    ];

    /// <summary>
    /// Puts the interface in a face that suits what it is drawn over, if one can be found.
    /// </summary>
    /// <remarks>
    /// Every step is allowed to fail into the one after it, ending with the built-in font,
    /// because none of this is worth a tool that does not start: a missing Windows font, a
    /// locked file, a face ImGui refuses. ReplaceFont says whether it worked, so the answer is
    /// checked rather than assumed.
    ///
    /// THE SIZE IS REMEMBERED whether or not a face was found, and it is the ATTEMPT that is
    /// recorded rather than the success. Otherwise a machine with none of these fonts asks for
    /// a new atlas on every single frame, for a face it is never going to get.
    /// </remarks>
    /// <param name="size">Pixels. Comes from <see cref="Interface"/>, already inside its range.</param>
    private void WearASerif(int size)
    {
        _wearing = size;

        string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        if (fonts.Length == 0)
        {
            return;
        }

        foreach (string face in Serifs)
        {
            string file = Path.Combine(fonts, face);

            try
            {
                if (File.Exists(file)
                    && ReplaceFont(file, size, ClickableTransparentOverlay.FontGlyphRangeType.English))
                {
                    return;
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Try the next one. A font is decoration, and the built-in face is waiting.
            }
        }
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
    /// <param name="fade">
    /// How much of the icon's opacity to keep. Below one for a marker drawn from memory - see
    /// <see cref="FadeFor"/>.
    /// </param>
    private bool DrawIcon(ImDrawListPtr draw, string key, Vector2 at, float radius, float fade = 1f)
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
            OverlayStyle.Faded(style.ColourOr(0xFFFFFFFF), fade));
        return true;
    }

    /// <summary>How solid a marker is drawn, which is how it says whether it is a sighting.</summary>
    /// <remarks>
    /// A remembered thing has not moved - only the standing kinds are kept, which is the whole
    /// rule in <see cref="EntityMemory"/> - so its position is exactly as true as it was when
    /// it was read. What has aged is whether it is still THERE: somebody else may have opened
    /// the chest, and the game is no longer in a position to say. Dimmer rather than hidden,
    /// and dimmer rather than identical, because both of those answer a question that was not
    /// asked.
    /// </remarks>
    private static float FadeFor(WorldEntity entity)
        => entity.IsRemembered ? OverlayStyle.RememberedAlpha : 1f;

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
        // FIRST, before anything is submitted: this is the thread that owns ImGui's style and
        // its font atlas, and a palette applied part-way through a frame draws half a window
        // in each of two of them.
        ApplyInterfaceIfAsked();

        TrackGameWindow();

        int width = (int)ImGui.GetIO().DisplaySize.X;
        int height = (int)ImGui.GetIO().DisplaySize.Y;

        // The renderer knows the viewport, so it hands it to the reader rather than the
        // reader guessing at a window it cannot see. Kept ahead of the foreground gate so
        // the viewport stays current while hidden - the game can be resized from a
        // borderless-window setting change without ever giving up focus.
        // Kept in step every frame rather than on the switch being moved, because the switch
        // moves from three places - the tab, the settings being applied, a future page - and
        // one thing that syncs cannot drift out of step the way three callers can. It costs a
        // delegate call and a bool.
        ReadVisuals?.Invoke(_projectiles.Enabled);

        // The same arrangement, for the read the status icons need: kept in step every frame
        // rather than at the moment a switch moves, because the switch moves from the tab and
        // from the settings being applied, and one thing that syncs cannot drift out of step
        // the way two callers can.
        ReadMonsterBuffs?.Invoke(_tracker.NeedsMonsterBuffs);
        ReadAim?.Invoke(_tracker.NeedsAim);

        _viewport = (width, height);
        _snapshot = _snapshotSource(new UiScale(width, height, _cull));

        // Where the game's panels are, handed to the windows so one lying over an open panel can
        // take itself off screen until it is not. Published every frame including the empty
        // case, which is what brings a hidden window back the moment the panel shuts - and the
        // empty list is also how the switch is expressed, so a user who turned this off is
        // indistinguishable from a player with no panel open.
        Chrome.Covering = HideWindowsBehindPanels ? _snapshot.Covering : [];

        // Nothing is drawn unless the game - or one of OUR windows - is in front. Not
        // tidiness: the overlay is always-on-top and covers the game's whole client area,
        // so every dot painted while the user has alt-tabbed away lands on the browser or
        // editor they switched to.
        //
        // Our own windows have to count, and leaving them out is not a subtle bug. Clicking
        // this overlay's own controls gives IT focus, so the game stops being foreground,
        // so the next frame draws nothing, so there is nothing left to click: the window
        // disappears under the cursor and neither dragging nor a button ever registers.
        // Keystrokes are a different question and stay strict - see InputSender.
        if (!GameWindowTracker.IsForeground(_gameWindow) && !GameWindowTracker.IsOwnProcessForeground())
        {
            return;
        }

        // Before the marker gate, and outside it: the watcher decides for itself where it is
        // quiet, and its own rule is towns rather than "wherever markers are drawn".
        if (_snapshot.InGame)
        {
            long now = Environment.TickCount64;

            // What the area loaded goes first, so that when both have something to say in the
            // same frame the live one wins the banner: an entity alert is about something
            // happening now, and this is about the area, which will still be true in a second.
            AnnounceWhatLoaded(now);

            if (_alerts is not null && _alerts.Look(_snapshot, now) is Alert raised)
            {
                _banner.Show(raised);
            }

            if (width > 0 && height > 0)
            {
                _banner.Draw(ImGui.GetForegroundDrawList(), width, height, now);
                _preloadEntry.Draw(ImGui.GetForegroundDrawList(), width, height, now);
            }
        }

        // Outside the InGame block and above the panel gate, both deliberately. A rule may
        // legitimately watch for not being in the game - a reminder while a portal loads - and
        // a caption is not a world marker, so a stash being open is no reason to take away a
        // readout somebody put at the top of their screen. Whether any of this shows at all was
        // decided by the engine, which knows about focus and about panels.
        if (_ruleDrawings is not null && width > 0 && height > 0)
        {
            _rules.Draw(ImGui.GetForegroundDrawList(), _ruleDrawings(), width, height);
        }

        // The ranges a rule measures, while somebody is building it. Under the world-panel
        // gate rather than over it, unlike the captions: these are drawn ON THE GROUND, so a
        // stash open over them is the same "right information in the way" every other world
        // marker is hidden for.
        if (_ruleRanges is not null && width > 0 && height > 0 && !_snapshot.InAPanel)
        {
            _rules.DrawRanges(
                ImGui.GetForegroundDrawList(),
                _ruleRanges(),
                _snapshot,
                GameWindowTracker.CursorInClient(_gameWindow),
                width,
                height,
                _ruleFacts?.Invoke());
        }

        // Nothing to mark in a town or a hideout, and a screen full of markers over the
        // stash is worse than no overlay. An area that did not resolve counts as hostile,
        // so a failed read never silently switches the tool off.
        //
        // Nor while a panel is over the game, which is the same argument one step further in:
        // everything below draws in world space, and a screen-filling panel is what the player
        // is actually looking at. Right information in the way is worse than none. What counts
        // as a panel - and whether to ask at all - is PanelReader and HideBehindPanels.
        if (_snapshot.InGame && width > 0 && height > 0 && _snapshot.Area.WantsMarkers
            && !(HideBehindPanels && _snapshot.InAPanel))
        {
            // Markers go ON THE MAP, not over the 3D scene. Scattering dots across the game
            // world puts them between the player and what they are fighting; the map is
            // where a radar belongs, and it is where the game already draws its own.
            DrawMapDots();

            // UNDER everything else in world space, because it is drawn ON the ground and
            // filled rings would otherwise cover the markers standing in them.
            _groundDanger.Draw(ImGui.GetBackgroundDrawList(), _snapshot, width, height);

            // Over the monsters themselves rather than on the map, and separate from the
            // world dots for that reason: a dot in the world lands between the player and
            // what they are fighting, while a health bar goes where the eye already is.
            _healthBars.Draw(ImGui.GetBackgroundDrawList(), _snapshot, width, height);

            // After the health bars so a line lands over them rather than under, and before
            // the status icons so it never crosses the numbers they carry.
            _monsterLines.Draw(ImGui.GetBackgroundDrawList(), _snapshot, width, height);

            // Between the lines and the icons: a ray is a world-space line like the monster
            // lines, and the icons carry numbers that nothing should be drawn over.
            _aim.Draw(ImGui.GetBackgroundDrawList(), _snapshot, width, height);
            _statusIcons.Draw(ImGui.GetBackgroundDrawList(), _snapshot, width, height);

            if (ShowWorldDots)
            {
                DrawEntities(width, height);
            }

            // After the dots, so an effect ring sits over the marker it belongs beside rather
            // than under it - and it is a ring precisely so the marker still reads through.
            _effects.Draw(
                ImGui.GetBackgroundDrawList(),
                _snapshot,
                entity =>
                {
                    ScreenPoint point = ProjectGround(entity, width, height);
                    return (point.OnScreen, point.X, point.Y);
                });

            // LAST of the world-space layers, so it lands on top of them. A projectile is the
            // only thing drawn here that is gone within half a second, which makes it the one
            // thing that cannot afford to be underneath something standing still.
            //
            // Followed on THIS thread, and only while it is being drawn. Where a projectile
            // has been is a drawing question - it turns a mark that blinks into a direction -
            // and nothing else in the tool asks it. A snapshot redrawn at VSync extends no
            // trail, because the watch appends only where the position CHANGED; see
            // ProjectileWatch.
            if (_projectiles.Enabled)
            {
                _projectileWatch.Update(_snapshot, Environment.TickCount64);
                _projectiles.Draw(
                    ImGui.GetBackgroundDrawList(), _snapshot, _projectileWatch, width, height);
            }
        }

        // OUTSIDE the marker gate above, which only lets things through in a hostile area.
        // The atlas is opened from a hideout at least as often as from a map, and it draws
        // over the game's own panel rather than over the world - so the reasons that gate
        // exists (dots between the player and the fight) do not apply to it. It draws
        // nothing at all while the panel is closed, which is what the view reports.
        if (_atlasWatch is not null)
        {
            // Read once, both from the same settings: the window can change them between
            // frames, and a layer told to hide names by one half of a stale pair is worse
            // than either answer.
            AtlasSettings drawing = _atlasWatch.Settings;
            _atlas.ShowNames = drawing.Names;
            _atlas.TextScale = drawing.Writing;

            // Published from HERE because this is the thread that has it. ImGui's mouse position
            // is the real cursor even while the game has focus and the overlay is click-through:
            // the library re-reads it from the system every frame rather than from the messages
            // an unfocused window never receives.
            _atlasWatch.Cursor = ImGui.GetMousePos();

            _atlas.Draw(ImGui.GetBackgroundDrawList(), _atlasWatch.View);

            // After the atlas, so a route runs OVER the map labels it passes rather than under
            // them - it is the thing being looked at while it is on screen.
            if (_ritualWatch is not null && _ritualWindow is not null)
            {
                _ritual.TextScale = drawing.Writing;
                _ritual.Draw(ImGui.GetBackgroundDrawList(), _ritualWatch.View, _ritualWindow.Picked);
            }
        }

        // Registered on first use rather than at attach because the cost history is a
        // property, set whenever the app happens to wire it - the tab appears the moment
        // there is something for it to show.
        if (Costs is not null)
        {
            if (_costWindow is null)
            {
                var made = new CostWindow(Costs);
                _costWindow = made;
                _tools.Add(80, "cost", "Read cost", made.DrawTab, page: Status);
            }

            _costWindow.CurrentArea = _snapshot.AreaHash;
        }

        // Registered here rather than at attach because nothing attaches it: the layer and its
        // watch are owned by this class, so the tab exists the first time a frame is drawn.
        if (_projectileWindow is null)
        {
            var made = new ProjectileWindow(
                _projectiles, _projectileWatch, () => SettingsChanged?.Invoke());
            _projectileWindow = made;
            _tools.Add(
                35, "projectiles", "Projectiles", () => made.DrawTab(_snapshot), page: Combat);

            // The marks' and trails' colours, folded under the switches they belong to.
            var styles = new StyleRows(Style, SaveStyle, StyleCatalogue.Homes.Projectiles);
            _tools.Add(
                36, "projectiles-style", "How projectiles look", styles.Draw, styles.Idle,
                page: Combat);
        }

        // The same first-use registration, and for the same reason: the two callbacks it needs
        // are properties the app sets when it wires the reader up.
        if (_effectWindow is null && KeepingEffects is not null && KeepEffects is not null)
        {
            var made = new EffectWindow(_effects, KeepingEffects, KeepEffects, Noise);
            _effectWindow = made;
            _tools.Add(95, "effects", "Effects", () => made.DrawTab(_snapshot), page: Entities);
        }

        // Registered on first use for the same reason as the cost tab: the meter is a
        // property, so the tab appears once there is something behind it.
        if (Damage is DamageMeter meter)
        {
            if (_damageWindow is null)
            {
                _damageWindow = new DamageWindow(meter) { Heat = _heat };
                _tools.Add(30, "damage", "Damage", _damageWindow.DrawTab, page: Combat, pageLabel: "Combat");
            }

            _damageWindow.CurrentArea = _snapshot.AreaHash;
        }

        // Polled outside the tools window on purpose: F8 captures what is under the cursor
        // the moment it is pressed, with the tools closed or on another tab, and the pick
        // is what brings the browser in front - not the other way around.
        if (_uiBrowser is not null && _uiBrowser.Tick(_tracked))
        {
            _tools.Show(UiBrowserTab);
        }

        // Its own small window rather than a tab, alone among the tools: routing is done
        // WHILE playing, and a route picked from inside the toolbox would mean parking a
        // full-size window over the fight to click a destination.
        _poi?.DrawPicker(_snapshot, _snapshot.Player);

        _tools.Render();

        // A window rather than something painted, so it can be closed and moved - which means
        // it belongs here with the windows and not in the drawing pass. Only where the
        // findings are about the ground under your feet: the walk never runs in a town, so
        // what is held there is the last MAP's list, and showing that beside the stash would
        // be a straight lie.
        if (_preload is not null && _snapshot.InGame && _snapshot.Area.WantsMarkers)
        {
            _preloadPanel.Draw(_snapshot.AreaHash, _preload.Findings);
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

            float fade = FadeFor(entity);
            uint chosen = Style.Colour(key);
            uint colour = OverlayStyle.Faded(chosen, fade);
            var position = new Vector2(point.X, point.Y);
            float size = Style.Sized(key, DotRadius);

            if (!DrawIcon(draw, key, position, size, fade))
            {
                draw.AddCircleFilled(position, size, colour);
                draw.AddCircle(
                    position, size, OverlayStyle.Faded(OutlineColour, fade), 12,
                    Style.Width(StyleCatalogue.Keys.DotOutline, 1.5f));
            }

            if (ShowLabels && entity.Kind is EntityKind.Monster or EntityKind.Chest or EntityKind.WorldItem)
            {
                // The dot's own colour unless a label colour was chosen. Matching the dot is
                // what makes a label readable among forty of them, so it is the default
                // rather than something to configure your way back to.
                draw.AddText(
                    position + new Vector2(size + 3, -7),
                    OverlayStyle.Faded(Style[StyleCatalogue.Keys.DotLabel].ColourOr(chosen), fade),
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
    /// The window's leading page: what is being read, the pools, and the switches.
    /// </summary>
    /// <remarks>
    /// FIRST among the pages, because a blank overlay is otherwise ambiguous - "nothing
    /// nearby" and "the read chain broke" look identical - and because "why did the flask not
    /// fire" is a question asked mid-session rather than during a debugging pass. Leading also
    /// means the window sizes itself to this page's dozen short lines while it is in front,
    /// which is what lets it sit in a corner during a fight and still open into the dissector.
    ///
    /// The measurements are the debugging pass and hide behind --debug, in their own rows and
    /// their own controls, so what is left is the part somebody reads at a glance.
    /// </remarks>
    private void DrawStatusTab(int width, int height)
    {
        DrawReadout(width, height);

        ImGui.Separator();
        DrawSwitches();

        if (!ShowDiagnostics)
        {
            return;
        }

        ImGui.Separator();
        DrawMeasuringControls(width, height);
    }

    /// <summary>A label and its value, as one row of the readout's two columns.</summary>
    /// <remarks>
    /// A TABLE rather than the padded strings this used to be - "area:     Clearfell" and
    /// friends, lined up by counting spaces. That only ever looked right in a monospaced font,
    /// and ImGui's default is not one: every row was a couple of pixels off from its
    /// neighbours, and any value long enough to matter pushed its own label out of line.
    ///
    /// The colour is PUSHED rather than passed to TextColored, and that is not a style
    /// preference. ImGui's Text calls are printf - a value read out of the game can carry a
    /// percent sign, "100% Increased" is a real monster name, and TextColored would take it
    /// as a conversion specifier and print whatever was in the register. TextUnformatted
    /// cannot, so this row is safe by construction rather than by remembering to escape.
    /// </remarks>
    private static void Row(string label, string value, Vector4? colour = null)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextDisabled(label);
        ImGui.TableNextColumn();

        if (colour is Vector4 tint)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, tint);
        }

        ImGui.TextUnformatted(value);

        if (colour is not null)
        {
            ImGui.PopStyleColor();
        }
    }

    private static readonly Vector4 Warning = new(1f, 0.6f, 0.2f, 1f);
    private static readonly Vector4 Quiet = new(0.7f, 0.75f, 0.8f, 1f);
    private static readonly Vector4 Measured = new(0.65f, 0.75f, 0.68f, 1f);
    private static readonly Vector4 Bad = new(1f, 0.5f, 0.4f, 1f);

    /// <summary>What is being read, the pools, and what auto-flask is doing.</summary>
    private void DrawReadout(int width, int height)
    {
        if (!ImGui.BeginTable("##readout", 2, ImGuiTableFlags.SizingFixedFit))
        {
            return;
        }

        try
        {
            if (!_snapshot.InGame)
            {
                // WHICH state, not just "no". A loading screen, the login screen and a
                // character select all draw nothing, and they are different situations -
                // saying "not in an area" over a loading screen reads as a broken read.
                Row("idle", _snapshot.State switch
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
                }, Warning);
                return;
            }

            // Named even when nothing is hidden. An overlay that goes blank in town otherwise
            // looks broken, and this is the line that says it is not - it also reports the
            // area id, which is what pins down any further rule about WHERE it should be
            // active.
            AreaInfo area = _snapshot.Area;
            Row(
                "area",
                area.Describe() + (area.WantsMarkers ? string.Empty : "   - markers hidden"),
                area.WantsMarkers ? Quiet : Warning);

            // The two counts kept apart: one is what the game listed this read, the other is
            // what it has stopped listing and this still draws. Added together they would
            // read as the entity list having grown, which is the one thing this line is used
            // to watch.
            Row("entities", _snapshot.Remembered > 0
                ? $"{_snapshot.Entities.Count - _snapshot.Remembered}"
                  + $"   (+{_snapshot.Remembered} remembered out of range)"
                : $"{_snapshot.Entities.Count}");

            // The question a map is actually being looked at for near the end of a run: is
            // there any of it left. Measured against what can be REACHED rather than every
            // walkable cell, which is why it is worth showing at all - the same figure
            // against the whole grid reads as a few per cent on a finished map.
            if (Coverage is MapCoverage walked && walked.Measuring)
            {
                Row(
                    "walked",
                    $"{walked.Percent:F0}%   ({walked.SeenCells} of {walked.ReachableCells})"
                    + (walked.RegionKnown ? string.Empty : "   - still working out what is reachable"),
                    Measured);
            }

            if (ReadStats is not null)
            {
                (double ms, long reads, long failures) = ReadStats();
                Row(
                    "read",
                    $"{ms:F1} ms on its own thread   frame {1000f / ImGui.GetIO().Framerate:F1} ms"
                    + $"   ({reads} reads{(failures > 0 ? $", {failures} failed" : string.Empty)})");
            }

            if (_snapshot.PlayerVitals is Vitals vitals)
            {
                Row("vitals", $"life {Show(vitals.Life)}   mana {Show(vitals.Mana)}   es {Show(vitals.EnergyShield)}");
            }

            // Here rather than only on its page, because this is a number you watch while
            // doing the thing it measures - a damage figure you have to open a window to read
            // cannot tell you whether the pack you just hit died to that skill.
            if (Damage is DamageMeter damage && damage.Measuring)
            {
                Row(
                    "damage",
                    $"{DamageWindow.Number(damage.Dps)} dps   peak {DamageWindow.Number(damage.Peak)}"
                    + $"   {DamageWindow.Number(damage.Total)} this area",
                    new Vector4(1f, 1f, 0.6f, 1f));
            }

            // Always shown, including when it failed: an omitted row looks like a feature that
            // is not running, when it actually means a read gave up.
            if (_snapshot.FlaskBelt is FlaskBelt belt && !belt.IsUnknown)
            {
                Row("belt", string.Join("   ", belt.Flasks.Select(f =>
                    $"{f.Slot}:{f.Charges}/{f.ChargesPerUse}"
                    + (f.IsCharm ? " (charm)" : f.CanUse ? string.Empty : " (empty)"))));
            }
            else
            {
                Row("belt", "not read - run with --flasks for the chain", Bad);
            }

            if (FlaskStatus is not null)
            {
                Row("flask", FlaskStatus(), new Vector4(0.8f, 0.7f, 0.4f, 1f));
            }

            // The summary stays here even though the full list is a page of its own: the
            // common case needs no page at all - the answer is one line and it is already on
            // screen.
            if (_preload is not null)
            {
                string here = _preload.Summary();
                Row("loaded", here.Length > 0 ? here : $"{_preload.All.Count} files", Measured);
            }

            // WHICH ones, not just whether. The panels are found at unverified offsets and
            // paths, and the way a wrong one fails is that something reads open forever and
            // the markers never come back - which looks exactly like an overlay that broke.
            // Named here, it is instead one line saying which panel to stop believing.
            //
            // AND WHERE EACH ONE IS, which is a separate answer and fails separately: the bit
            // decides about the markers, the rectangle decides about this window. HOW it was
            // arrived at is printed too, because the ways differ in how much they can be
            // trusted, and a screenshot of this line is what settles which one a machine took -
            // it is how the atlas was caught reporting itself 733 pixels too narrow.
            if (_snapshot.InAPanel)
            {
                string where = !HideWindowsBehindPanels
                    ? "windows staying put"
                    : _snapshot.Covering.Count == 0
                        ? "no screen area read - windows staying put"
                        : "covering " + string.Join(
                            ", ",
                            _snapshot.Covering.Select(
                                area => $"{area.Panel} {area.Right - area.Left:F0}x{area.Bottom - area.Top:F0}"
                                        + $" {Extent(area.From)}"));

                Row(
                    "panels",
                    $"{_snapshot.Panels}   ({where})",
                    new Vector4(1f, 0.8f, 0.35f, 1f));
            }

            // Only when there is one. A path that does not work otherwise shows up as a
            // marker drawn its ordinary way, which is exactly what not setting an icon looks
            // like - so the setting appears to do nothing at all.
            foreach (string problem in _icons.Files.Problems)
            {
                Row("icon", problem, Bad);
            }

            if (ShowDiagnostics)
            {
                DrawMeasurements(width, height);
            }
        }
        finally
        {
            ImGui.EndTable();
        }
    }

    /// <summary>
    /// The rows that are only interesting while something is being worked out.
    /// </summary>
    /// <remarks>
    /// BEHIND --debug, all of it, and that is a correction rather than a preference. Every row
    /// here was written to settle a question - where the read's time goes, whether the terrain
    /// heights line up, whether the projection still centres the player - and once settled
    /// they are arithmetic nobody reads while playing. Left on, they were most of a readout
    /// whose job is to answer "is it working" at a glance: a line reporting
    /// "here ground 23 vs computed 23 (off by 0), texture 1932x1863" is a sentence you have to
    /// stop and parse to learn that nothing is wrong.
    /// </remarks>
    private void DrawMeasurements(int width, int height)
    {
        // WHERE the read's time went, not just how much. One number says a frame was expensive
        // and nothing about why, and an expensive phase is usually a redundancy rather than a
        // fundamental cost - which only a breakdown shows.
        if (_snapshot.Cost.TotalMs > 0)
        {
            Row("cost", _snapshot.Cost.ToString());
        }

        Row("viewport", _tracked.IsValid
            ? $"{width} x {height}   (game {_tracked.Width} x {_tracked.Height} @ {_tracked.X},{_tracked.Y})"
            : $"{width} x {height}   (game window not tracked)");

        // In the overlay rather than only in the config window, because this is read while
        // standing on the hill that shows the problem - a readout that needs a second window
        // open is a readout nobody is looking at in that moment.
        if (ShowTerrain)
        {
            Row("terrain", DescribeTerrain(), new Vector4(0.65f, 0.7f, 0.78f, 1f));
        }

        if (_snapshot.Player is not WorldEntity player)
        {
            return;
        }

        Row("player", $"({player.WorldX:F0}, {player.WorldY:F0}, {player.WorldZ:F0})");

        // The live projection sanity check: the camera follows the player, so this must sit at
        // the screen centre. If it drifts, the matrix is wrong.
        ScreenPoint p = WorldToScreen.Project(
            _snapshot.Matrix, player.WorldX, player.WorldY, player.TerrainHeight, width, height);
        double offCentre = WorldToScreen.OffCentreFraction(p, width, height);

        // Centred ALONE is not proof: a matrix that blows w up collapses the whole scene onto
        // the centre, so the player looks perfect while nothing else is drawable. Showing the
        // scene's pixel spread next to it makes that failure visible instead of reassuring.
        double spread = ScreenSpread(width, height);
        bool healthy = offCentre < 0.15 && spread > width * 0.05;
        Vector4 colour = healthy
            ? new Vector4(0.4f, 1f, 0.4f, 1f)
            : new Vector4(1f, 0.4f, 0.4f, 1f);

        Row("projection", $"off-centre {offCentre:F3}   scene spread {spread:F0} px", colour);

        // The same thing in pixels, which is what a screenshot can be measured against: how
        // far the marker sits from the screen centre, and how tall the character is on screen,
        // so the two are directly comparable.
        ScreenPoint hb = WorldToScreen.Project(
            _snapshot.Matrix, player.WorldX, player.WorldY, player.WorldZ, width, height);
        Row(
            "marker",
            $"({p.X:F0}, {p.Y:F0})   centre ({width / 2}, {height / 2})"
            + $"   delta ({p.X - (width / 2f):F0}, {p.Y - (height / 2f):F0}) px");
        Row(
            "character",
            $"{Math.Abs(hb.Y - p.Y):F0} px tall"
            + $"   (world z {player.WorldZ:F0} vs ground {player.TerrainHeight:F0})");

        if (!healthy && spread <= width * 0.05)
        {
            Row(string.Empty, "scene collapsed - the matrix offset is wrong.", colour);
        }
    }

    /// <summary>The switches with no page of their own.</summary>
    /// <remarks>
    /// One line each and every one of them is reached for mid-fight, which is why they sit
    /// under the readout rather than on the appearance page: a switch you have to go to
    /// another tab for is a switch you flip after the thing you wanted it for has happened.
    /// </remarks>
    private void DrawSwitches()
    {
        // Not a page like the rest: routing is done WHILE playing, so the picker keeps its own
        // small window - see the note where it is drawn.
        if (_poi is not null)
        {
            bool picking = _poi.ShowPicker;
            if (ImGui.Checkbox("Points of interest", ref picking))
            {
                _poi.ShowPicker = picking;
                SettingsChanged?.Invoke();
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

        if (Memory is not null)
        {
            bool remembering = Memory.Enabled;
            if (ImGui.Checkbox(
                    "Keep what is out of range  (places and drops the game has stopped listing)",
                    ref remembering))
            {
                Memory.Enabled = remembering;
                SettingsChanged?.Invoke();
            }
        }

        bool hurtOnly = _healthBars.OnlyWhenHurt;
        if (ImGui.Checkbox("Health bars only once hurt  (off shows every monster's)", ref hurtOnly))
        {
            _healthBars.OnlyWhenHurt = hurtOnly;
            SettingsChanged?.Invoke();
        }

        bool behind = HideBehindPanels;
        if (ImGui.Checkbox("Hide behind big panels  (stash, skill tree, atlas, world map)", ref behind))
        {
            HideBehindPanels = behind;
            SettingsChanged?.Invoke();
        }

        bool windowsBehind = HideWindowsBehindPanels;
        if (ImGui.Checkbox(
                "Hide these windows over a panel  (only the ones actually on top of it)",
                ref windowsBehind))
        {
            HideWindowsBehindPanels = windowsBehind;
            SettingsChanged?.Invoke();
        }

        bool labels = ShowLabels;
        if (ImGui.Checkbox("Name labels beside the dots", ref labels))
        {
            ShowLabels = labels;
            SettingsChanged?.Invoke();
        }
    }

    /// <summary>The kind filter and the projection probe. Both are --debug work.</summary>
    private void DrawMeasuringControls(int width, int height)
    {
        // The kind breakdown doubles as the filter, since "what is out there" and "what do I
        // want drawn" are the same question asked twice. Note the ##id suffix: ImGui derives a
        // control's identity from its label, so a label carrying a live count would be a NEW
        // control every frame and the click would never register.
        foreach (IGrouping<EntityKind, WorldEntity> group in
                 _snapshot.Entities.GroupBy(e => e.Kind).OrderBy(g => g.Key.ToString(), StringComparer.Ordinal))
        {
            bool drawn = DrawnKinds.Contains(group.Key);
            if (ImGui.Checkbox($"{group.Key,-12} {group.Count()}##kind{group.Key}", ref drawn))
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

        bool calibration = ShowCalibration;
        if (ImGui.Checkbox("Calibration aids  (screen centre and both candidate heights)", ref calibration))
        {
            ShowCalibration = calibration;
        }

        if (!ShowCalibration || _snapshot.Player is not WorldEntity subject)
        {
            return;
        }

        // Drag until the marker sits on the X the game's own map draws at the player's
        // position, then read the number off. Reported in BOTH units because that is what
        // separates the possible causes: a round world height means the wrong height is being
        // fed in, while a value that only makes sense in pixels means the error is in screen
        // space.
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 10f);
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

        // The character's own height calibrates world units against pixels: the two rings are
        // exactly its Render z above the ground.
        float characterUnits = Math.Abs(subject.ModelBoundsZ);
        ScreenPoint top = WorldToScreen.Project(
            _snapshot.Matrix, subject.WorldX, subject.WorldY, subject.HealthBarZ, width, height);
        ScreenPoint bottom = ProjectGround(subject, width, height);
        float pixelsPerUnit = characterUnits > 0.01f
            ? Math.Abs(top.Y - bottom.Y) / characterUnits
            : 0f;

        ImGui.TextUnformatted(pixelsPerUnit > 0.0001f
            ? $"probe    {ProbeHeight:F0} world units = {ProbeHeight * pixelsPerUnit:F0} px"
              + $"   (scale {pixelsPerUnit:F2} px per world unit)"
            : "probe    scale unavailable - no character height to calibrate against");
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

        // UNDER EVERYTHING, including the layout: it is the ground the rest is read against,
        // and a monster dot lost behind a heat patch is a marker that did not work.
        if (Damage is DamageMeter measured)
        {
            _heat.Draw(draw, map, measured.Heat, player, _snapshot.AreaHash);
        }

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
            float fade = FadeFor(entity);
            if (!DrawIcon(draw, key, at, size, fade))
            {
                draw.AddCircleFilled(at, size, OverlayStyle.Faded(Style.Colour(key), fade));
                draw.AddCircle(
                    at, size, OverlayStyle.Faded(OutlineColour, fade), 10,
                    Style.Width(StyleCatalogue.Keys.DotOutline, 1f));
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

        // What the game is listing, not what is remembered: a sighting is out of the bubble
        // by definition, so it projects far off screen and would inflate this on a correct
        // matrix in proportion to how much of the map has been walked.
        foreach (WorldEntity entity in _snapshot.Listed)
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

    /// <summary>How a panel's rectangle was arrived at, in words rather than as an enum name.</summary>
    /// <remarks>
    /// Read while something is being diagnosed, so it says what it MEANS: a measurement that can
    /// be wrong reads differently from a rule that cannot. "by element" is the one that has
    /// already been caught lying, on the atlas, on an ultrawide.
    /// </remarks>
    private static string Extent(PanelExtent from) => from switch
    {
        PanelExtent.Element => "by its own size",
        PanelExtent.Children => "by what it draws",
        PanelExtent.Kind => "whole screen by kind",
        _ => "whole screen - nothing measurable",
    };

    /// <summary>
    /// Whether an entity is worth a marker: the loot filter, and the things that are still in
    /// the world while no longer being worth pointing at.
    /// </summary>
    /// <remarks>
    /// Both draw loops pass through here, which is the point of it - the dots on the screen
    /// and the dots on the game's map answer the same question and had drifted apart before.
    ///
    /// A drop whose rarity has not resolved yet is DRAWN. It is one frame old at most, and
    /// showing a drop that turns out to be junk costs a moment of attention, while hiding
    /// one that turns out to be a unique costs the drop.
    /// </remarks>
    private bool WorthDrawing(WorldEntity entity)
    {
        // An emptied chest is scenery. It was already excluded from the points of interest
        // and not from the dots, so a looted map still showed every container on it.
        if (entity.IsSpent)
        {
            return false;
        }

        // Your own flame wall is not a dot. Only FRIENDLY effects get this far - the reader
        // drops the hostile ones - and it keeps them deliberately, because whose side a thing
        // is on is a fact while whether to draw it is a preference. This is where that
        // preference lives, and it is the half that was missing: the health bars stopped and
        // the dots did not, so one cast still covered the map in markers.
        //
        // Effects only, not friendly things in general. A minion or a totem is targetable, so
        // it is not an effect and stays drawn - which is what the targetable clause is for.
        if (entity.IsEffect)
        {
            return false;
        }

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
