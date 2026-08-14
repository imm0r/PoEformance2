using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Scanning;
using PoEformance.Core.Schema;

[assembly: SupportedOSPlatform("windows")]

namespace PoEformance.App;

/// <summary>
/// The composition root. Everything the program does is wired here, by hand, in
/// order - no dependency container, no reflection, no plugins. To understand the
/// program, read this file top to bottom.
/// </summary>
/// <remarks>
/// This is a THIN shell. The one thing it does that only works on Windows is attach to
/// the game process; the actual drift-report engine lives in
/// <see cref="DriftReport"/> in Core, so it runs against a live attach, a replay, or a
/// synthetic test process, on any OS.
///
/// The attach + module scan happen ONCE; the report re-runs cheaply. So <c>--watch</c>
/// keeps the tool attached and re-validates whenever the schema file changes on disk -
/// edit an offset, save, see the new report, no rebuild and no re-attach. That is the
/// whole point of "offsets are data".
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        var options = CliOptions.Parse(args);
        Console.WriteLine("PoEformance (C# port) - drift report");

        string schemaPath = options.SchemaPath ?? FindSchemaFile();
        Console.WriteLine($"schema  {schemaPath}");

        // Before the attach on purpose: this reads a FILE, so it must still answer when the
        // game is not running - which is exactly when someone sits down to check why a
        // flask key did not work.
        if (options.ProbeKeys)
        {
            PoEformance.Features.KeyBindingProbe.Report(Console.Out);
        }

        Console.WriteLine();

        // ── Attach (or replay) - the only Windows-specific step ──────────────
        IMemoryReader reader;
        RecordingMemoryReader? recorder = null;
        IntPtr gameWindow = IntPtr.Zero;

        if (options.ReplayPath is not null)
        {
            reader = ReplayMemoryReader.Load(File.OpenRead(options.ReplayPath));
            Console.WriteLine($"replay  {options.ReplayPath} ({((ReplayMemoryReader)reader).FrameCount} frames)");
        }
        else
        {
            Process? game = FindGameProcess();
            if (game is null)
            {
                Console.Error.WriteLine("PathOfExile process not found. Start the game, or pass --replay <file>.");
                return 1;
            }

            LiveMemoryReader? live = LiveMemoryReader.TryAttach(game);
            if (live is null)
            {
                Console.Error.WriteLine($"Found PoE2 (pid {game.Id}) but could not open it for reading.");
                Console.Error.WriteLine("Run the terminal as Administrator.");
                return 1;
            }

            Console.WriteLine($"attach  pid {live.ProcessId}, module 0x{live.ModuleBase:X} ({live.ModuleSize / (1024 * 1024)} MB)");

            // The overlay sizes itself to this window, so a wrong viewport is impossible.
            gameWindow = game.MainWindowHandle;

            if (options.RecordPath is not null)
            {
                recorder = new RecordingMemoryReader(live, File.Create(options.RecordPath));
                reader = recorder;
                Console.WriteLine($"record  {options.RecordPath}");
            }
            else
            {
                reader = live;
            }
        }

        using IMemoryReader _ = reader;

        // The scanner copies the module image once and caches it, so re-running the
        // report (in --watch) re-resolves statics against the cached image cheaply.
        var scanner = new PatternScanner(reader);

        DriftReportResult result = RunReportOnce(reader, scanner, schemaPath, recorder, options.Verbose);

        // Store the resolved statics IMMEDIATELY, not at exit. Finding them needs the game's
        // whole module image, which is far too large to record, so a replay depends entirely
        // on these six notes - and a session that is closed with Ctrl+C, or that ends in the
        // overlay, never reaches an exit-time write. An uploaded recording with no statics
        // cannot be replayed at all, which is exactly what happened to the first one.
        if (recorder is not null)
        {
            foreach (ResolvedStatic resolved in result.Statics.Where(s => s.Found))
            {
                recorder.NoteStatic(resolved.Name, resolved.Address);
            }
        }

        // Probe the player and project its position - the end-to-end proof of the whole
        // read chain. Running it here also means a --record session captures the component
        // reads, so the projection can be verified offline from the recording.
        if (result.Statics.FirstOrDefault(s => s.Name == "GameStates")?.Found == true)
        {
            OffsetSchema probeSchema = SchemaJson.Load(schemaPath);
            ulong gameStates = result.Statics.First(s => s.Name == "GameStates").Address;
            recorder?.MarkFrame();
            new PoEformance.Game.Diagnostics.PlayerProbe(reader, probeSchema).ProbeAndReport(gameStates, Console.Out);
            recorder?.MarkFrame();
        }

        // Scan the whole entity map once. Besides the readout, this is what puts the map
        // traversal into a --record session so it can be replayed and tested offline.
        ulong gameStatesAddress = result.Statics.FirstOrDefault(s => s.Name == "GameStates")?.Address ?? 0;

        // Kept out of the block below so the auto-flask report can see the BELT. Without it
        // that report cannot tell an unbound flask from a charm, and the two mean opposite
        // things - see ReportAutoFlask.
        PoEformance.Game.Components.FlaskBelt? belt = null;

        if (gameStatesAddress != 0)
        {
            recorder?.MarkFrame();
            OffsetSchema worldSchema = SchemaJson.Load(schemaPath);
            PoEformance.Game.World.WorldSnapshot snapshot = ReportWorldScan(reader, worldSchema, gameStatesAddress);
            belt = snapshot.FlaskBelt;
            recorder?.MarkFrame();

            // Verify the matrix against the whole scene, not just the player. Reading the
            // scan window also puts it into a --record session, so the same hunt can be
            // re-run offline against the recording.
            RunMatrixHunt(reader, worldSchema, gameStatesAddress, snapshot);
            recorder?.MarkFrame();

            if (options.ProbeFlasks)
            {
                new PoEformance.Game.Diagnostics.FlaskProbe(reader, worldSchema)
                    .Report(gameStatesAddress, Console.Out);
                recorder?.MarkFrame();
            }
        }

        // ── Auto-flask ───────────────────────────────────────────────────────
        // Two sources, deliberately kept apart: WHAT to do comes from our settings file,
        // WHICH KEY uses a flask comes from the GAME's own config. Assuming the key would
        // work perfectly until someone rebinds one, and then fail with no symptom beyond
        // "nothing happens" - see FlaskKeyBindings.
        var flaskKeys = PoEformance.Features.FlaskKeyBindings.Load();
        var flaskSettings = PoEformance.Features.AutoFlaskSettingsStore.Load();
        var autoFlask = new PoEformance.Features.AutoFlask(
            flaskSettings.ToRules(flaskKeys),
            enabled: flaskSettings.Enabled || options.AutoFlask);
        if (options.ShowOverlay || options.ShowConfig || options.AutoFlask)
        {
            ReportAutoFlask(autoFlask, flaskKeys, flaskSettings, belt, forcedOn: options.AutoFlask);

            if (!options.ShowConfig)
            {
                Console.WriteLine("           (pass --config for the settings window - that is where slots");
                Console.WriteLine("           are switched on; it can stay open while the overlay runs)");
            }
        }

        // The config window runs on its own thread so it can be open WHILE the overlay is,
        // which is what makes its switches worth having: a setting that needs a restart is
        // a settings file with extra steps.
        // The overlay does not exist yet - the config window starts first so it is already
        // up while the overlay runs - so the two are joined by a handle the config thread
        // fills in later rather than by a reference that cannot be taken yet.
        var overlayHandle = new OverlayHandle();
        var overlaySettings = PoEformance.Features.OverlaySettingsStore.Load();

        // Topmost only when the overlay is up, i.e. when a fullscreen game would otherwise
        // hide it. On its own it is an ordinary window and should behave like one.
        Thread? configWindow = options.ShowConfig
            ? StartConfigWindow(
                reader, schemaPath, result, gameStatesAddress, autoFlask, flaskKeys, flaskSettings,
                overlaySettings, overlayHandle, alwaysOnTop: options.ShowOverlay)
            : null;

        if (options.ShowOverlay && options.ReplayPath is null && gameStatesAddress != 0)
        {
            // The letterbox bar width. UI positions are scaled by the window MINUS these
            // bars and then shifted by one, so a missing cull misplaces every UI-derived
            // coordinate on anything that is not 16:10.
            ResolvedStatic? cullStatic = result.Statics.FirstOrDefault(s => s.Name == "GameCullSize" && s.Found);
            int cull = cullStatic is { Address: not 0 } found ? reader.Read<int>(found.Address) : 0;
            if (cull is < 0 or > 2000)
            {
                cull = 0; // a mis-resolved pattern would poison every width-scaled rect
            }

            // The within-tile terrain heights need these two engine tables. A scan that
            // comes up empty leaves the map on tile-level heights, so this is read
            // permissively rather than checked.
            var rotation = new PoEformance.Game.World.TerrainRotationTables(
                result.Statics.FirstOrDefault(s => s.Name == "TerrainRotationSelector" && s.Found)?.Address ?? 0,
                result.Statics.FirstOrDefault(s => s.Name == "TerrainRotatorHelper" && s.Found)?.Address ?? 0);

            RunOverlay(
                reader, SchemaJson.Load(schemaPath), gameStatesAddress, gameWindow, cull, autoFlask, rotation,
                debug: options.Debug, settings: overlaySettings, handle: overlayHandle,
                uiBrowser: options.ShowUiBrowser,
                fileRoot: result.Statics.FirstOrDefault(s => s.Name == "FileRoot" && s.Found)?.Address ?? 0,
                areaCounter: result.Statics.FirstOrDefault(s => s.Name == "AreaChangeCounter" && s.Found)?.Address ?? 0,
                recorder: recorder);
        }

        configWindow?.Join();

        if (options.Watch && options.ReplayPath is null)
        {
            WatchSchema(reader, scanner, schemaPath, recorder, options.Verbose);
        }

        if (recorder is not null && options.RecordPath is not null)
        {
            recorder.Dispose(); // flush before measuring
            long bytes = new FileInfo(options.RecordPath).Length;
            Console.WriteLine();
            Console.WriteLine($"recorded {bytes / 1024.0:F0} KB to {options.RecordPath} "
                + $"({recorder.SkippedLargeReads} oversized reads skipped - the module image is not needed for replay)");
            Console.WriteLine($"        {recorder.RecordedBytes / 1024.0:F0} KB of reads written, "
                + $"{recorder.SkippedUnchangedReads:N0} reads left out because nothing about them had moved");
            if (recorder.ReachedSizeLimit)
            {
                Console.WriteLine("        the size cap stopped recording partway - the file still holds the "
                    + "startup chain and diagnostics, which is what replays.");
            }
        }

        return result.GameStatesResolved ? 0 : 2;
    }

    /// <summary>
    /// Loads the schema fresh from disk (so <c>--watch</c> picks up edits) and runs one
    /// report to the console via the Core engine.
    /// </summary>
    private static DriftReportResult RunReportOnce(IMemoryReader reader, PatternScanner scanner, string schemaPath, RecordingMemoryReader? recorder, bool verbose)
    {
        OffsetSchema schema = SchemaJson.Load(schemaPath);
        Console.WriteLine($"{schema.Structs.Count} structs, {schema.Statics.Count} statics, game version \"{schema.GameVersion}\"");
        Console.WriteLine();
        recorder?.MarkFrame();

        IReadOnlyDictionary<string, ulong>? knownStatics =
            reader is ReplayMemoryReader replay && replay.ResolvedStatics.Count > 0
                ? replay.ResolvedStatics
                : null;

        DriftReportResult result = DriftReport.Run(reader, scanner, schema, Console.Out, verbose, knownStatics);

        recorder?.MarkFrame();
        return result;
    }

    /// <summary>
    /// Reads the whole entity map once and prints a breakdown, so the entity layer can be
    /// sanity-checked from the console before any pixels are drawn.
    /// </summary>
    private static PoEformance.Game.World.WorldSnapshot ReportWorldScan(IMemoryReader reader, OffsetSchema schema, ulong gameStatesStatic)
    {
        var world = new PoEformance.Game.World.WorldReader(reader, schema);
        PoEformance.Game.World.WorldSnapshot snapshot = world.Read(gameStatesStatic);

        Console.WriteLine();
        Console.WriteLine("world scan");
        if (!snapshot.InGame)
        {
            Console.WriteLine("  --    not in an area.");
            return snapshot;
        }

        Console.WriteLine($"  entities with a position: {snapshot.Entities.Count}");
        foreach (IGrouping<PoEformance.Game.World.EntityKind, PoEformance.Game.World.WorldEntity> group
                 in snapshot.Entities.GroupBy(e => e.Kind).OrderByDescending(g => g.Count()))
        {
            Console.WriteLine($"    {group.Key,-10} {group.Count()}");
        }

        // A couple of concrete monsters make it obvious at a glance that real entities were
        // read, not just counted.
        foreach (PoEformance.Game.World.WorldEntity monster
                 in snapshot.Entities.Where(e => e.Kind == PoEformance.Game.World.EntityKind.Monster).Take(5))
        {
            Console.WriteLine($"    monster  {monster.ShortName} @ ({monster.WorldX:F0}, {monster.WorldY:F0})");
        }

        return snapshot;
    }

    /// <summary>
    /// Scores every candidate matrix offset against the scene and prints the ranking.
    /// </summary>
    /// <remarks>
    /// The single-point check ("is the player centred?") cannot be trusted on its own: a
    /// matrix that inflates w collapses EVERY point onto the centre, so the player looks
    /// perfect while the scene is unusable. Requiring the other entities to spread out is
    /// what makes the answer decisive.
    /// </remarks>
    private static void RunMatrixHunt(
        IMemoryReader reader, OffsetSchema schema, ulong gameStatesStatic, PoEformance.Game.World.WorldSnapshot snapshot)
    {
        if (!snapshot.InGame)
        {
            return;
        }

        GameChainAddresses chain = GameChain.Resolve(reader, schema, gameStatesStatic);
        int current = schema.Structs["WorldData"].OffsetOf("W2SMatrix");
        List<PoEformance.Game.Diagnostics.ProjectionCandidate> candidates =
            PoEformance.Game.Diagnostics.MatrixHunt.Find(reader, chain.WorldData, snapshot);

        PoEformance.Game.Diagnostics.MatrixHunt.Report(candidates, current, Console.Out);
    }

    /// <summary>
    /// Runs the ImGui overlay until it is closed. The snapshot is re-read per frame; the
    /// renderer itself never touches game memory.
    /// </summary>
    /// <summary>
    /// Holds the running overlay so the config window can reach it.
    /// </summary>
    /// <remarks>
    /// The config window is started BEFORE the overlay - it should be usable while the
    /// overlay runs, which means it cannot be handed a reference that does not exist yet.
    /// A field the overlay fills in on start is the whole mechanism; the config thread only
    /// ever assigns an enum through it, and an enum write is atomic.
    /// </remarks>
    private sealed class OverlayHandle
    {
        public PoEformance.Overlay.EntityOverlay? Overlay { get; set; }
    }

    private static void RunOverlay(
        IMemoryReader reader, OffsetSchema schema, ulong gameStatesStatic, IntPtr gameWindow, int cull,
        PoEformance.Features.AutoFlask autoFlask,
        PoEformance.Game.World.TerrainRotationTables rotation, bool debug,
        PoEformance.Features.OverlaySettings settings, OverlayHandle handle, bool uiBrowser,
        ulong fileRoot, ulong areaCounter, RecordingMemoryReader? recorder = null)
    {
        var world = new PoEformance.Game.World.WorldReader(reader, schema, rotation)
        {
            // Names for tiles somebody has described. Missing is fine: the boss arenas are
            // found from the shape of the ground either way, this only names them.
            LandmarkNames = PoEformance.Game.World.LandmarkNames.Load(FindDataFile("landmarks.json")),
        };
        Console.WriteLine();
        Console.WriteLine(gameWindow != IntPtr.Zero
            ? "overlay running - it follows the game window and hides while the game is not in front"
            : "overlay running - no game window found, using a default size; Ctrl+C to quit");
        Console.WriteLine(debug
            ? "        --debug: projection measurements and calibration aids are ON"
            : "        drawing living monsters, chests, items and NPCs. --debug adds the"
              + " projection measurements and the entity-kind filter.");

        // Reads happen on their own thread at their own rate; the renderer only ever picks
        // up the newest finished snapshot. 30 Hz because entities move at the game's tick
        // rate - reading once per drawn frame bought nothing and cost the frame rate.
        //
        // Auto-flask runs HERE, inside the read, for two reasons: it needs the freshest
        // vitals rather than whatever the last drawn frame saw, and reacting at the read
        // rate rather than the frame rate keeps it working when the overlay is hidden.
        // Served from the reader thread alongside the world read, and idle until the browser
        // is opened: an interface tree read per tick is cheap next to the entity map, and
        // still pure waste while nobody is looking at it.
        var uiTree = new PoEformance.Features.UiTreeInspector(reader, schema, gameStatesStatic);

        // The same arrangement for raw memory: the window says where to look, this looks. Idle
        // until it is opened, and one window of a few kilobytes when it is - nothing next to
        // the entity map.
        var structures = new PoEformance.Features.StructureInspector(reader, schema, gameStatesStatic);

        // Every stash tab and everything in it. ON DEMAND rather than per tick: a full read is
        // thousands of entities taken down to their stats, which is orders of magnitude past
        // anything else here and answers a question nobody asks sixty times a second.
        // Each item's own picture. The item carries the PATH of its art, and that file is in
        // the game's own packed bundles - so the install is read for it, which needs nobody's
        // permission and works offline. poe2db is the fallback for whatever that cannot give
        // up, and THAT stays off until somebody turns it on: nothing else here talks to the
        // network while playing.
        using var itemArt = new PoEformance.Features.ItemArtStore
        {
            Install = PoEformance.Overlay.InstalledArt.Source(describe: Console.WriteLine),
        };

        var stash = new PoEformance.Features.StashInspector(
            reader,
            schema,
            gameStatesStatic,
            PoEformance.Game.Items.ItemNames.Load(
                FindDataFile("item-stats.json"),
                FindDataFile("item-names.json"),
                FindDataFile("unique_ivi_name_map.tsv")));

        // What things are worth. OFF until somebody switches it on in the stash window - and
        // more firmly than the pictures, because there is no local copy of a price to prefer:
        // they exist only on somebody else's server. Which league to ask about comes from the
        // game rather than from a setting, so it cannot go stale at a league start.
        using var prices = new PoEformance.Features.PriceStore();

        // And the other half of "what is this worth": the game's own trade site, for the
        // uniques poe.ninja has nothing on - which on Standard is all of them. It cannot be
        // asked over plain HTTP (the endpoints are Cloudflare-gated), so the query runs inside
        // a browser the player signs in to once, and only asking prices come back out.
        using var tradeSession = new PoEformance.Config.TradeSession();
        using var trade = new PoEformance.Features.TradePrices(ask: tradeSession.Query);

        // The endgame atlas, which is INTERFACE rather than world: it reads nothing at all
        // while the panel is closed, which is almost all of a session. Its two data files are
        // what turn a node's raw id and content numbers into a name and a list of what is in
        // it - both optional, and missing either only costs the words.
        // The ritual line rides the atlas read. Its own first read is one byte - the mode
        // flag - so having it switched on costs nothing while no line is being drawn, which is
        // all but a few seconds of a session.
        var ritual = new PoEformance.Features.RitualWatch(
            new PoEformance.Game.Ui.RitualLineReader(
                reader, schema, new PoEformance.Game.Ui.UiElementReader(reader, schema)),
            PoEformance.Game.World.RitualMods.Load(FindDataFile("ritual-mods.json")),
            PoEformance.Game.World.AtlasMapNames.Load(FindDataFile("atlas-maps.json")));

        // Loaded ONCE and handed to both, because the ratings are resolved against it: a second
        // load would be a second table for the same file and a second chance for them to differ.
        PoEformance.Game.World.AtlasMapNames mapNames =
            PoEformance.Game.World.AtlasMapNames.Load(FindDataFile("atlas-maps.json"));

        var atlas = new PoEformance.Features.AtlasWatch(
            reader,
            schema,
            gameStatesStatic,
            PoEformance.Game.World.AtlasContentNames.Load(FindDataFile("atlas-content.json")),
            mapNames,
            PoEformance.Game.World.AtlasRatings.Load(FindDataFile("atlas-ratings.json"), mapNames))
        {
            Settings = PoEformance.Features.AtlasStore.Load(),
            Ritual = ritual,
        };
        atlas.RitualWorth = atlas.Settings.Worth;

        // And the entity browser, which is the shortest route to something not yet
        // understood: the game names every component an entity carries, and most of them
        // still have nothing reading them.
        // With the game's own names for the stat ids, when the table is next to the binary.
        // Missing is fine - the ids still read, they are just numbers then.
        var entityParts = new PoEformance.Features.EntityInspector(
            reader, schema, PoEformance.Game.Components.StatNames.Load(FindDataFile("stat_name_map.tsv")));

        // Finding a way across the area is a search over millions of cells - measured at about
        // 1.8 seconds right across a real map - so it runs on the thread pool and the renderer
        // draws whatever has come back. Asking for one from the read loop costs nothing, which
        // matters because that loop also drives auto-flask.
        var route = new PoEformance.Features.RoutePlanner();

        // Recorded on the READER thread, so a read is sampled whether or not a frame was
        // drawn for it - a graph that only holds what the renderer happened to see would
        // thin out exactly when the reads got slow.
        var costs = new PoEformance.Features.CostHistory();

        // On the reader thread too, and for the same reason: it marks where the player has
        // BEEN, so sampling it only on drawn frames would lose ground walked while the
        // overlay was hidden behind an inventory screen.
        var coverage = new PoEformance.Features.MapCoverage();

        // On the reader thread for a stricter version of the same reason - see where it is
        // fed. It watches monster health fall, so it needs one sample per READ; a sample per
        // drawn frame would mostly be the same snapshot again, reporting no damage.
        var damage = new PoEformance.Features.DamageMeter();

        // What the area LOADED, which names the encounters in it before anybody has walked
        // there. The walk is thousands of pointers and a string each - far too much for a
        // tick - so it runs once when the area changes, on a thread of its own.
        var preload = new PoEformance.Features.PreloadWatch();
        var preloadReader = new PoEformance.Game.World.PreloadReader(reader, schema);
        uint preloadArea = 0;
        int preloadBusy = 0;

        // What a loaded path MEANS is the user's list rather than this file's. It grows every
        // league, from whoever is reading the raw paths on the day the tool has nothing to say
        // about a new thing - so it is read from disk here and added to from that same window.
        PoEformance.Features.PreloadSettings preloadSettings = PoEformance.Features.PreloadStore.Load();
        preload.UseRules(preloadSettings.Watching);

        void SweepForTheCountField()
        {
            if (fileRoot == 0 || areaCounter == 0)
            {
                preload.Swept(["the file root or the counter did not resolve - nothing to sweep"]);
                return;
            }

            _ = Task.Run(() =>
            {
                try
                {
                    int counter = preloadReader.AreaChangeCount(areaCounter);
                    PoEformance.Game.World.PreloadReader.PreloadSweep swept =
                        preloadReader.Sweep(fileRoot, counter);

                    var lines = new List<string>
                    {
                        $"counter static reads {counter}",
                        $"{swept.Records} records read, {swept.Named} with a readable path"
                            + (swept.Named == 0 ? "  <- the RECORD is wrong, not the count" : string.Empty),
                    };

                    lines.AddRange(swept.Samples.Select(sample => $"    {sample}"));

                    lines.Add($"at the offset in use (+0x{swept.Chosen:X}) the records hold:");
                    lines.AddRange(swept.NearbyValues.Select(v => $"    {v.Value}  in {v.Records} records"));

                    lines.Add("offsets holding the counter's value:");
                    lines.AddRange(swept.CountAt.Count > 0
                        ? swept.CountAt.Select(c => $"    +0x{c.Offset:X}  in {c.Agreeing} records")
                        : ["    none - no field in the first 0x100 bytes holds it"]);

                    preload.Swept(lines);
                }
                catch (Exception exception)
                {
                    preload.Swept([exception.Message]);
                }
            });
        }

        void LookAtWhatLoaded(uint area)
        {
            // One at a time, and never twice for the same area. Two walks at once would be
            // two thousand reads competing with the one the renderer is waiting for.
            if (fileRoot == 0 || areaCounter == 0
                || System.Threading.Interlocked.CompareExchange(ref preloadBusy, 1, 0) != 0)
            {
                return;
            }

            _ = Task.Run(() =>
            {
                try
                {
                    int counter = preloadReader.AreaChangeCount(areaCounter);
                    HashSet<string> files = preloadReader.Read(fileRoot, counter);

                    // The two numbers side by side when they disagree, because that IS the
                    // finding - the table stamps its own generation and the static is only a
                    // cross-check, so a mismatch says the static is reading something else.
                    string note = preloadReader.LastError;
                    if (files.Count > 0 && preloadReader.Newest != counter)
                    {
                        note = $"area {preloadReader.Newest} by the table, {counter} by the counter static";
                    }

                    preload.Took(area, files, note);
                }
                catch (Exception exception)
                {
                    preload.Took(area, [], exception.Message);
                }
                finally
                {
                    System.Threading.Volatile.Write(ref preloadBusy, 0);
                }
            });
        }

        // Which stash read has already been offered to the trade layer.
        PoEformance.Features.StashView tradeAsked = PoEformance.Features.StashView.Nothing;

        using var feed = new PoEformance.Features.SnapshotFeed(
            scale =>
            {
                // The recording's clock, and its only one once the startup report is over.
                // Without this every read the overlay ever makes lands in the same frame:
                // a recording of a whole map clear replayed as one instant, with no way to
                // ask what memory held at second forty - which is the entire reason for
                // recording a map clear. It is also where the file is flushed, so a session
                // that ends by being killed still ends somewhere.
                recorder?.MarkFrame();

                PoEformance.Game.World.WorldSnapshot snapshot = world.Read(gameStatesStatic, scale: scale);
                uiTree.Service(scale);
                structures.Service();
                entityParts.Service();
                atlas.Service(scale, Environment.TickCount64);
                stash.Service(Environment.TickCount64);

                // On the READER thread, and never waiting: the league is read here every few
                // seconds, and this only starts a fetch when it is news or the book has aged.
                prices.Watching(stash.League);

                trade.Watching(stash.League);
                trade.Book = prices.Book;
                trade.Service();

                // Once per stash READ rather than per frame: the view is published whole, so a
                // new one is a different object, and asking the trade site is one request per
                // name at three and a half seconds apiece.
                if (!ReferenceEquals(tradeAsked, stash.View))
                {
                    tradeAsked = stash.View;
                    trade.Ask(tradeAsked, prices.Book);
                }

                route.Service(snapshot, Environment.TickCount64);
                costs.Add(snapshot.Cost, snapshot.AreaHash, Environment.TickCount64);
                coverage.Look(snapshot);

                // HERE rather than in the overlay, and that is the whole difference between a
                // damage figure and a wrong one: the renderer redraws at VSync while these
                // snapshots arrive at about 30Hz, so sampling there would read the same
                // unchanged snapshot twice and count the second as a moment in which no
                // damage happened. One sample per read is one sample per thing that changed.
                damage.Look(snapshot, Environment.TickCount64);

                // On the area CHANGE rather than on a timer. The list cannot change while
                // you stand in a zone, so looking again would be thousands of reads to
                // confirm what is already on screen.
                // Not in town, and only on a change of INSTANCE. Both come from the
                // reference, which learned them: the walk is thousands of reads and a town
                // has nothing worth listing, while the hash is what distinguishes a new map
                // from a portal back into the one you were already in.
                if (snapshot.InGame && snapshot.AreaHash != 0 && snapshot.AreaHash != preloadArea
                    && snapshot.Area.IsHostile)
                {
                    preloadArea = snapshot.AreaHash;
                    LookAtWhatLoaded(snapshot.AreaHash);
                }

                // Evaluated even when the feature is off: it costs a bool check, and its
                // reason string is what the overlay's status line shows - including the
                // word "disabled", which is the answer to the question actually asked.
                PoEformance.Features.FlaskTick tick = autoFlask.Evaluate(
                    snapshot.PlayerVitals,
                    FlaskKeySender.IsForeground(gameWindow),
                    Environment.TickCount64,
                    snapshot.PlayerBuffs,
                    snapshot.FlaskBelt,
                    snapshot.State == PoEformance.Core.Diagnostics.GameStateKind.InGame);

                foreach (PoEformance.Features.FlaskUse use in tick.Used)
                {
                    FlaskKeySender.Press(use.Rule.Key);
                }

                return snapshot;
            },
            TimeSpan.FromMilliseconds(33));

        using var overlay = new PoEformance.Overlay.EntityOverlay(
            scale =>
            {
                feed.SetViewport(scale);
                return feed.Latest;
            },
            gameWindow,
            cull);
        overlay.ReadStats = () => (feed.LastReadMilliseconds, feed.ReadCount, feed.FailureCount);
        overlay.FlaskStatus = () => autoFlask.LastTick.Reason;
        overlay.ShowDiagnostics = debug;
        overlay.ShowCalibration = debug;
        overlay.ShowWorldDots = debug;
        overlay.AttachUiBrowser(uiTree, uiBrowser);
        overlay.AttachAtlas(atlas, changed => PoEformance.Features.AtlasStore.Save(changed));
        overlay.AttachStash(stash, itemArt, prices, trade, () => tradeSession.Show(stash.League));
        overlay.AttachRitual(
            ritual,
            () => atlas.RitualWorth,
            worth => atlas.RitualWorth = worth,
            worth =>
            {
                // Kept on the settings the atlas already holds, so the two cannot drift apart -
                // and written only once the typing has stopped.
                PoEformance.Features.AtlasSettings kept = atlas.Settings with { RitualWorth = worth };
                atlas.Settings = kept;
                PoEformance.Features.AtlasStore.Save(kept);
            });
        overlay.Noise = world.Noise;

        // The effects debug switch, as a pair of callbacks: the overlay draws and has no other
        // business with the reader, and this is the one bit of it worth reaching from up there.
        overlay.KeepingEffects = () => world.KeepEffects;
        overlay.KeepEffects = keep => world.KeepEffects = keep;

        overlay.Costs = costs;
        overlay.Coverage = coverage;
        overlay.Damage = damage;
        overlay.AttachPreload(
            preload,
            () => LookAtWhatLoaded(preloadArea),
            SweepForTheCountField,
            preloadSettings,
            changed => PoEformance.Features.PreloadStore.Save(changed));
        overlay.AttachDissector(structures);
        overlay.AttachEntityBrowser(entityParts);
        overlay.AttachPointsOfInterest(route);

        // Before the editor, which is handed this exact instance - and before the layers read
        // it, which they do every frame. The setter passes it on to whatever is attached.
        overlay.Style = PoEformance.Features.OverlayStyleStore.Load();
        overlay.AttachStyleEditor(() => PoEformance.Features.OverlayStyleStore.Save(overlay.Style));

        PoEformance.Features.AlertSettings alerts = PoEformance.Features.AlertStore.Load();
        var watcher = new PoEformance.Features.AlertWatcher
        {
            Rules = alerts.Watching,
            Enabled = alerts.Enabled,
            QuietInTown = alerts.QuietInTown,
        };
        overlay.AttachAlerts(
            watcher,
            () => PoEformance.Features.AlertStore.Save(
                new PoEformance.Features.AlertSettings(watcher.Enabled, watcher.QuietInTown, watcher.Rules)));

        // AFTER the parts it configures are attached, or half of it would be applied to
        // things that do not exist yet - and silently, since every one of them is optional.
        overlay.Apply(settings);
        overlay.SettingsChanged = () =>
            PoEformance.Features.OverlaySettingsStore.Save(overlay.CurrentSettings(settings));
        handle.Overlay = overlay;
        overlay.Start().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Prints how auto-flask is configured, one line per belt slot.
    /// </summary>
    /// <remarks>
    /// The key column is the point of this. Bindings read from the game's config and
    /// bindings assumed from the default 1-5 layout produce an identical-looking tool right
    /// up until a flask has been rebound, at which point the only symptom is that nothing
    /// happens. Printing WHERE the keys came from turns a silent failure into a visible one.
    /// </remarks>
    private static void ReportAutoFlask(
        PoEformance.Features.AutoFlask engine,
        PoEformance.Features.FlaskKeys keys,
        PoEformance.Features.AutoFlaskSettings settings,
        PoEformance.Game.Components.FlaskBelt? belt,
        bool forcedOn)
    {
        Console.WriteLine();
        if (!engine.Enabled)
        {
            Console.WriteLine("auto-flask off - turn it on in the config window (--config), or with --autoflask");
        }
        else if (engine.AnyRuleEnabled())
        {
            Console.WriteLine("auto-flask ARMED - it presses keys only while the GAME window has focus");
        }
        else
        {
            // The exact state that looks armed and does nothing. Say it plainly: the master
            // switch is on, every slot is off, so no key can ever be pressed.
            Console.WriteLine("auto-flask ON, but NO SLOT IS ENABLED - nothing will be pressed.");
            Console.WriteLine($"           Enable a slot in the config window (--config), or in");
            Console.WriteLine($"           {PoEformance.Features.AutoFlaskSettingsStore.DefaultPath}");
        }

        if (forcedOn && !settings.Enabled)
        {
            Console.WriteLine("           (--autoflask flips the master switch only; the saved setting is off)");
        }

        Console.WriteLine($"  keys     {keys.Detail}");
        switch (keys.Source)
        {
            case PoEformance.Features.KeyBindingSource.GameDefaults:
                // Not a problem: the game writes this file when a binding is CHANGED, so
                // an empty one means the game is on the same 1-5 defaults used below.
                Console.WriteLine("           The game has no saved bindings, so it is using its own 1-5");
                Console.WriteLine("           defaults - which is what the slots below match. Rebind a flask");
                Console.WriteLine("           in game and the file appears; this reads it from then on.");
                break;

            case PoEformance.Features.KeyBindingSource.Unmatched:
                Console.WriteLine("           The config HAS content but no flask binding was found in it, so");
                Console.WriteLine("           the 1-5 defaults are assumed. If a flask key is rebound, the tool");
                Console.WriteLine("           would press the wrong one - run --keys to dump what is in there.");
                break;
        }

        foreach (PoEformance.Features.FlaskRule rule in engine.Rules)
        {
            string trigger = rule.TriggerBuff.Length > 0
                ? $"on \"{rule.TriggerBuff}\""
                : $"{rule.Vital,-12} at or below {rule.ThresholdPercent,3}%";

            keys.BySlot.TryGetValue(rule.Slot, out ushort key);

            // A charm slot has no key BY DESIGN - the game fires it on its own condition, so
            // there is nothing to bind and nothing wrong. Saying "this slot can never fire"
            // there would report the normal state as a fault, and send someone looking for a
            // binding that is not supposed to exist.
            bool charm = belt?.InSlot(rule.Slot)?.IsCharm ?? false;

            string keyName = charm
                ? "charm - the game triggers it, no key needed"
                : key == 0
                    ? "no usable key - this slot can never fire"
                    : $"key {PoEformance.Features.FlaskKeyBindings.Describe(key)}";

            Console.WriteLine($"  slot {rule.Slot}   {(rule.Enabled ? "ON " : "off")}  {trigger}   {keyName}");
        }
    }

    /// <summary>
    /// Opens the WebView2 configuration window on its own thread and returns it.
    /// </summary>
    /// <remarks>
    /// Every "getState" from the page re-reads the world FRESH - the page's refresh button
    /// is therefore also a liveness check of the whole read chain, not a cached echo. This
    /// window is the Native AOT risk probe: it must keep working in the AOT-published build,
    /// which the CI publish job verifies at compile level and a live run verifies fully.
    ///
    /// Both callbacks run on the window's own thread. The state read is fine there (reads
    /// are thread-safe and each call builds its own reader stack); the change applies to the
    /// engine in a single atomic swap, so a reader tick sees the old configuration or the
    /// new one but never half of each.
    /// </remarks>
    private static Thread StartConfigWindow(
        IMemoryReader reader,
        string schemaPath,
        DriftReportResult report,
        ulong gameStatesAddress,
        PoEformance.Features.AutoFlask autoFlask,
        PoEformance.Features.FlaskKeys flaskKeys,
        PoEformance.Features.AutoFlaskSettings flaskSettings,
        PoEformance.Features.OverlaySettings overlaySettings,
        OverlayHandle overlayHandle,
        bool alwaysOnTop)
    {
        Console.WriteLine();
        Console.WriteLine("config window open" + (alwaysOnTop ? " (kept on top, so the game cannot bury it)" : "")
            + " - the tool exits once it (and the overlay) are closed");

        // The live settings. Only the window's thread touches these: it reads them to build
        // a state and replaces them when the page saves.
        PoEformance.Features.AutoFlaskSettings settings = flaskSettings;
        PoEformance.Features.OverlaySettings overlay = overlaySettings;

        // One reader for the window's whole life. The terrain grid is cached inside it, so
        // building a fresh one per request would re-read megabytes on every poll.
        var worldReader = new PoEformance.Game.World.WorldReader(reader, SchemaJson.Load(schemaPath));
        PoEformance.Game.World.WorldSnapshot ReadSnapshot()
            => gameStatesAddress == 0
                ? PoEformance.Game.World.WorldSnapshot.Empty
                : worldReader.Read(gameStatesAddress);

        PoEformance.Config.ConfigState BuildState()
        {
            OffsetSchema schema = SchemaJson.Load(schemaPath);
            PoEformance.Game.World.WorldSnapshot snapshot = ReadSnapshot();
            bool inGame = snapshot.InGame;
            int entityCount = snapshot.Entities.Count;
            PoEformance.Game.Components.FlaskBelt? belt = snapshot.FlaskBelt;

            return new PoEformance.Config.ConfigState(
                Type: "state",
                ToolVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "dev",
                GameVersion: schema.GameVersion,
                Attached: reader.IsAttached,
                ProcessId: reader.ProcessId,
                StaticsFound: report.Statics.Count(s => s.Found),
                StaticsTotal: report.Statics.Count,
                InGame: inGame,
                EntityCount: entityCount,
                AutoFlask: BuildAutoFlaskView(settings, flaskKeys, autoFlask, belt),
                Overlay: new PoEformance.Config.OverlayView(
                    overlay.MinLootRarity.ToString(),
                    overlay.ShowTerrain,
                    overlay.TerrainColour,
                    overlay.TerrainThickness,
                    DescribeTerrain(overlayHandle)),
                Map: BuildMapView(snapshot, overlay.MinLootRarity));
        }

        // Rebuilding the outline is a pass over megabytes, so it is done once per area and
        // handed out from here - the page only asks on an area change, but a page bug must
        // not be able to turn that into a per-second cost.
        uint layoutArea = 0;
        PoEformance.Features.MapLayout layout = PoEformance.Features.MapLayout.Empty;

        string? Apply(PoEformance.Config.ConfigRequest request)
        {
            if (request.Type == "getMapLayout")
            {
                PoEformance.Game.World.WorldSnapshot snapshot = ReadSnapshot();
                if (snapshot.Terrain is PoEformance.Game.World.TerrainGrid grid)
                {
                    if (layoutArea != snapshot.AreaHash || layout.Width == 0)
                    {
                        layout = PoEformance.Features.MapLayout.From(grid);
                        layoutArea = snapshot.AreaHash;
                    }
                }
                else
                {
                    layout = PoEformance.Features.MapLayout.Empty;
                    layoutArea = snapshot.AreaHash;
                }

                return JsonSerializer.Serialize(
                    new PoEformance.Config.MapLayoutMessage("mapLayout", layoutArea, layout),
                    PoEformance.Config.ConfigJsonContext.Default.MapLayoutMessage);
            }

            if (request.Payload.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            switch (request.Type)
            {
                case "setFlaskSettings":
                    PoEformance.Features.AutoFlaskSettings? flasks = request.Payload.Deserialize(
                        PoEformance.Config.ConfigJsonContext.Default.AutoFlaskSettings);
                    if (flasks is null)
                    {
                        return null;
                    }

                    // Normalise BEFORE anything else sees it: the page is editable on disk
                    // and the file is hand-editable, so this is where a value becomes
                    // trustworthy.
                    settings = flasks.Normalised();
                    autoFlask.Configure(settings.ToRules(flaskKeys), settings.Enabled);
                    SaveWarning(
                        PoEformance.Features.AutoFlaskSettingsStore.Save(settings),
                        PoEformance.Features.AutoFlaskSettingsStore.DefaultPath);
                    return string.Empty;

                case "setOverlaySettings":
                    PoEformance.Features.OverlaySettings? sent = request.Payload.Deserialize(
                        PoEformance.Config.ConfigJsonContext.Default.OverlaySettings);
                    if (sent is null)
                    {
                        return null;
                    }

                    // Merged, not replaced: the page sends only the settings it shows, and
                    // swapping the whole record in would reset the overlay's own switches to
                    // their defaults - silently, and only noticed later.
                    overlay = overlay.MergeFromPage(sent).Normalised();

                    // Null until the overlay actually starts, which is the normal state for
                    // a --config run without --overlay: the setting is still saved, it just
                    // has nothing to apply to yet.
                    if (overlayHandle.Overlay is PoEformance.Overlay.EntityOverlay live)
                    {
                        live.MinimumLootRarity = overlay.MinLootRarity;
                        live.ShowTerrain = overlay.ShowTerrain;
                        live.ApplyTerrainStyle(
                            PoEformance.Features.OverlaySettings.ParseColour(overlay.TerrainColour),
                            overlay.TerrainThickness);
                    }

                    SaveWarning(
                        PoEformance.Features.OverlaySettingsStore.Save(overlay),
                        PoEformance.Features.OverlaySettingsStore.DefaultPath);
                    return string.Empty;

                default:
                    return null;
            }
        }

        return PoEformance.Config.ConfigWindowHost.Start("PoEformance", BuildState, Apply, alwaysOnTop);
    }

    /// <summary>Says so when settings could not be written, instead of losing them quietly.</summary>
    private static void SaveWarning(bool saved, string path)
    {
        if (!saved)
        {
            Console.Error.WriteLine($"could not write {path} - the change applies to this session only.");
        }
    }

    /// <summary>
    /// Builds the auto-flask panel: what is configured, next to what is actually equipped.
    /// </summary>
    /// <remarks>
    /// Showing the equipped flask beside each row is what makes the choice concrete. "Slot
    /// 2" means nothing on its own; "Slot 2 - Ultimate Mana Flask, 42/12 charges" is the
    /// question the setting is actually answering.
    /// </remarks>
    private static PoEformance.Config.AutoFlaskView BuildAutoFlaskView(
        PoEformance.Features.AutoFlaskSettings settings,
        PoEformance.Features.FlaskKeys keys,
        PoEformance.Features.AutoFlask engine,
        PoEformance.Game.Components.FlaskBelt? belt)
    {
        var slots = new List<PoEformance.Config.FlaskSlotView>(settings.Slots.Count);
        foreach (PoEformance.Features.FlaskSlotSettings slot in settings.Slots)
        {
            keys.BySlot.TryGetValue(slot.Slot, out ushort key);
            PoEformance.Game.Components.EquippedFlask? equipped = belt?.InSlot(slot.Slot);

            slots.Add(new PoEformance.Config.FlaskSlotView(
                Slot: slot.Slot,
                Enabled: slot.Enabled,
                Vital: slot.Vital.ToString(),
                ThresholdPercent: slot.ThresholdPercent,
                Key: PoEformance.Features.FlaskKeyBindings.Describe(key),
                Item: equipped is { } item ? ShortItemName(item.Path) : string.Empty,
                Charges: equipped is { } charges ? $"{charges.Charges}/{charges.ChargesPerUse}" : string.Empty,
                IsCharm: equipped?.IsCharm ?? false));
        }

        return new PoEformance.Config.AutoFlaskView(
            Enabled: settings.Enabled,
            KeySource: $"{keys.Source} - {keys.Detail}",
            Status: engine.LastTick.Reason,
            Slots: slots);
    }

    /// <summary>
    /// Builds the map panel's per-frame half: where the player is, and what is around them.
    /// </summary>
    /// <remarks>
    /// Positions are in GRID CELLS, not in the outline's pixels. The page divides by the
    /// layout's step, which means a marker stays correct even if the layout is later rebuilt
    /// at a different thinning - the two messages travel separately and can be one apart.
    /// </remarks>
    private static PoEformance.Config.MapStateView BuildMapView(
        PoEformance.Game.World.WorldSnapshot snapshot, PoEformance.Game.Components.ItemRarity minLoot)
    {
        var markers = new List<PoEformance.Config.MapMarker>();
        foreach (PoEformance.Game.World.WorldEntity entity in snapshot.Entities)
        {
            string? kind = entity.Kind switch
            {
                PoEformance.Game.World.EntityKind.Monster => "monster",
                PoEformance.Game.World.EntityKind.Chest => "chest",
                PoEformance.Game.World.EntityKind.Npc => "npc",
                PoEformance.Game.World.EntityKind.WorldItem when Worth(entity.Rarity, minLoot) => "loot",
                _ => null,
            };

            if (kind is not null)
            {
                markers.Add(new PoEformance.Config.MapMarker(
                    entity.WorldX / PoEformance.Game.Ui.MapView.WorldToGrid,
                    entity.WorldY / PoEformance.Game.Ui.MapView.WorldToGrid,
                    kind));
            }
        }

        PoEformance.Game.World.WorldEntity? player = snapshot.Player;
        return new PoEformance.Config.MapStateView(
            Area: snapshot.AreaHash,
            HasLayout: snapshot.Terrain is not null,
            Status: snapshot.Terrain is null ? "terrain loading" : snapshot.Area.Describe(),
            PlayerX: (player?.WorldX ?? 0) / PoEformance.Game.Ui.MapView.WorldToGrid,
            PlayerY: (player?.WorldY ?? 0) / PoEformance.Game.Ui.MapView.WorldToGrid,
            Markers: markers);
    }

    /// <summary>The same loot rule the overlay uses - currency always, unknown until it resolves.</summary>
    private static bool Worth(
        PoEformance.Game.Components.ItemRarity rarity, PoEformance.Game.Components.ItemRarity minimum)
        => rarity is PoEformance.Game.Components.ItemRarity.Currency
            or PoEformance.Game.Components.ItemRarity.Unknown
            || rarity >= minimum;

    /// <summary>What the terrain layer currently holds, for the config page.</summary>
    /// <remarks>
    /// Terrain populates well after an area loads - a minute or more on a large map - so
    /// "nothing yet" is a normal state that needs saying rather than an empty box.
    /// </remarks>
    private static string DescribeTerrain(OverlayHandle handle)
        => handle.Overlay is null ? "overlay not running" : handle.Overlay.DescribeTerrain();

    /// <summary>The last segment of a metadata path - the readable half of an item name.</summary>
    private static string ShortItemName(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash >= 0 && slash < path.Length - 1 ? path[(slash + 1)..] : path;
    }

    /// <summary>
    /// Keeps the process attached and re-runs the report whenever the schema file changes.
    /// This is the hot-reload dev loop: edit an offset in the JSON, save, and the new
    /// report appears - no rebuild, no re-attach.
    /// </summary>
    private static void WatchSchema(IMemoryReader reader, PatternScanner scanner, string schemaPath, RecordingMemoryReader? recorder, bool verbose)
    {
        string fullPath = Path.GetFullPath(schemaPath);
        string directory = Path.GetDirectoryName(fullPath) ?? ".";
        string fileName = Path.GetFileName(fullPath);

        Console.WriteLine();
        Console.WriteLine($"watching {fileName} - edit + save to re-run, Ctrl+C to quit");

        using var watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };

        // Editors save in several ways (write-in-place, or write-temp-then-rename) and
        // often fire two events per save. A short debounce collapses that into one re-run.
        using var pending = new ManualResetEventSlim(false);
        long lastFireTicks = 0;

        void OnChanged(object? sender, FileSystemEventArgs e)
        {
            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref lastFireTicks) < 150)
            {
                return;
            }

            Interlocked.Exchange(ref lastFireTicks, now);
            pending.Set();
        }

        watcher.Changed += OnChanged;
        watcher.Created += OnChanged;
        watcher.Renamed += OnChanged;
        watcher.EnableRaisingEvents = true;

        using var quit = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            quit.Set();
            pending.Set();
        };

        while (!quit.IsSet)
        {
            pending.Wait();
            pending.Reset();
            if (quit.IsSet)
            {
                break;
            }

            // Give the editor a moment to finish writing before we read the file.
            Thread.Sleep(80);

            Console.WriteLine();
            Console.WriteLine($"── re-run {DateTime.Now:HH:mm:ss} ─────────────────────────────");
            try
            {
                RunReportOnce(reader, scanner, schemaPath, recorder, verbose);
            }
            catch (Exception ex)
            {
                // A malformed edit must not kill the watch session - report and wait for the fix.
                Console.Error.WriteLine($"  schema error: {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("stopped.");
    }

    private static Process? FindGameProcess()
    {
        // The same list that finding the game's FILES uses - it is one question, and two copies
        // of the answer drift the moment a launcher adds a name.
        foreach (string name in PoEformance.Game.Files.GameInstall.Names)
        {
            Process[] found = Process.GetProcessesByName(name);
            if (found.Length > 0)
            {
                return found[0];
            }
        }

        return null;
    }

    private static string FindSchemaFile()
    {
        // Next to the exe (deployed layout), else walk up to the repo root (dev layout).
        string local = Path.Combine(AppContext.BaseDirectory, "schema", "poe2.offsets.json");
        if (File.Exists(local))
        {
            return local;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "schema", "poe2.offsets.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent!;
        }

        throw new FileNotFoundException("schema/poe2.offsets.json not found next to the executable or in any parent directory.");
    }

    /// <summary>Finds a shipped data file next to the executable, or in a parent directory.</summary>
    /// <remarks>
    /// The same walk the schema uses, so running from a build output and running from the
    /// repository both work - and a missing file is a path, not an exception, because every
    /// caller of this treats absence as "do without".
    /// </remarks>
    private static string FindDataFile(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "data", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "data", name);
    }

    /// <summary>Parsed command line. Kept tiny and explicit - no arg-parsing library.</summary>
    private sealed record CliOptions(
        string? SchemaPath,
        string? ReplayPath,
        string? RecordPath,
        bool Watch,
        bool Verbose,
        bool ShowOverlay,
        bool ShowConfig,
        bool AutoFlask,
        bool ProbeFlasks,
        bool ProbeKeys,
        bool Debug,
        bool ShowUiBrowser)
    {
        public static CliOptions Parse(string[] args)
        {
            string? schema = null, replay = null, record = null;
            bool watch = false, verbose = false, overlay = false, config = false;
            bool autoFlask = false, probeFlasks = false, probeKeys = false, debug = false;
            bool uiBrowser = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--schema" when i + 1 < args.Length:
                        schema = args[++i];
                        break;
                    case "--replay" when i + 1 < args.Length:
                        replay = args[++i];
                        break;
                    case "--record" when i + 1 < args.Length:
                        record = args[++i];
                        break;
                    case "--watch":
                        watch = true;
                        break;
                    case "--overlay":
                        overlay = true;
                        break;
                    case "--config":
                        config = true;
                        break;
                    case "--autoflask":
                        autoFlask = true;
                        break;
                    case "--flasks":
                        probeFlasks = true;
                        break;
                    case "--keys":
                        probeKeys = true;
                        break;
                    case "--debug":
                        debug = true;
                        break;
                    case "--uibrowser":
                        uiBrowser = true;
                        break;
                    case "-v" or "--verbose":
                        verbose = true;
                        break;
                }
            }

            return new CliOptions(
                schema, replay, record, watch, verbose, overlay, config, autoFlask, probeFlasks, probeKeys,
                debug, uiBrowser);
        }
    }
}
