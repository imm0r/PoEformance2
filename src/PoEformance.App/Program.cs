using System.Diagnostics;
using System.Runtime.Versioning;
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
        if (gameStatesAddress != 0)
        {
            recorder?.MarkFrame();
            OffsetSchema worldSchema = SchemaJson.Load(schemaPath);
            PoEformance.Game.World.WorldSnapshot snapshot = ReportWorldScan(reader, worldSchema, gameStatesAddress);
            recorder?.MarkFrame();

            // Verify the matrix against the whole scene, not just the player. Reading the
            // scan window also puts it into a --record session, so the same hunt can be
            // re-run offline against the recording.
            RunMatrixHunt(reader, worldSchema, gameStatesAddress, snapshot);
            recorder?.MarkFrame();
        }

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

            RunOverlay(reader, SchemaJson.Load(schemaPath), gameStatesAddress, gameWindow, cull);
        }

        if (options.ShowConfig)
        {
            RunConfigWindow(reader, schemaPath, result, gameStatesAddress);
        }

        if (options.Watch && options.ReplayPath is null)
        {
            WatchSchema(reader, scanner, schemaPath, recorder, options.Verbose);
        }

        if (recorder is not null && options.RecordPath is not null)
        {
            // Store the resolved statics so the replay never needs the module image - the
            // one read big enough to dominate the file size.
            foreach (ResolvedStatic s in result.Statics.Where(s => s.Found))
            {
                recorder.NoteStatic(s.Name, s.Address);
            }

            recorder.Dispose(); // flush before measuring
            long bytes = new FileInfo(options.RecordPath).Length;
            Console.WriteLine();
            Console.WriteLine($"recorded {bytes / 1024.0:F0} KB to {options.RecordPath} "
                + $"({recorder.SkippedLargeReads} oversized reads skipped - the module image is not needed for replay)");
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
    private static void RunOverlay(
        IMemoryReader reader, OffsetSchema schema, ulong gameStatesStatic, IntPtr gameWindow, int cull)
    {
        var world = new PoEformance.Game.World.WorldReader(reader, schema);
        Console.WriteLine();
        Console.WriteLine(gameWindow != IntPtr.Zero
            ? "overlay running - it follows the game window; close it (or Ctrl+C) to quit"
            : "overlay running - no game window found, using a default size; Ctrl+C to quit");

        using var overlay = new PoEformance.Overlay.EntityOverlay(
            scale => world.Read(gameStatesStatic, scale: scale), gameWindow, cull);
        overlay.Start().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Opens the WebView2 configuration window and blocks until it closes.
    /// </summary>
    /// <remarks>
    /// Every "getState" from the page re-reads the world FRESH - the page's refresh button
    /// is therefore also a liveness check of the whole read chain, not a cached echo. This
    /// window is the Native AOT risk probe: it must keep working in the AOT-published build,
    /// which the CI publish job verifies at compile level and a live run verifies fully.
    /// </remarks>
    private static void RunConfigWindow(IMemoryReader reader, string schemaPath, DriftReportResult report, ulong gameStatesAddress)
    {
        Console.WriteLine();
        Console.WriteLine("config window open - close it to continue");

        PoEformance.Config.ConfigWindowHost.Run("PoEformance", () =>
        {
            OffsetSchema schema = SchemaJson.Load(schemaPath);
            int entityCount = 0;
            bool inGame = false;
            if (gameStatesAddress != 0)
            {
                PoEformance.Game.World.WorldSnapshot snapshot =
                    new PoEformance.Game.World.WorldReader(reader, schema).Read(gameStatesAddress);
                inGame = snapshot.InGame;
                entityCount = snapshot.Entities.Count;
            }

            return new PoEformance.Config.ConfigState(
                Type: "state",
                ToolVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "dev",
                GameVersion: schema.GameVersion,
                Attached: reader.IsAttached,
                ProcessId: reader.ProcessId,
                StaticsFound: report.Statics.Count(s => s.Found),
                StaticsTotal: report.Statics.Count,
                InGame: inGame,
                EntityCount: entityCount);
        });
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
        foreach (string name in (string[])["PathOfExileSteam", "PathOfExile", "PathOfExile_x64", "PathOfExile_x64Steam"])
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

    /// <summary>Parsed command line. Kept tiny and explicit - no arg-parsing library.</summary>
    private sealed record CliOptions(
        string? SchemaPath,
        string? ReplayPath,
        string? RecordPath,
        bool Watch,
        bool Verbose,
        bool ShowOverlay,
        bool ShowConfig)
    {
        public static CliOptions Parse(string[] args)
        {
            string? schema = null, replay = null, record = null;
            bool watch = false, verbose = false, overlay = false, config = false;

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
                    case "-v" or "--verbose":
                        verbose = true;
                        break;
                }
            }

            return new CliOptions(schema, replay, record, watch, verbose, overlay, config);
        }
    }
}
