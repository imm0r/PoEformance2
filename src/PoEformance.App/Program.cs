using System.Diagnostics;
using System.Runtime.Versioning;
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
/// Current milestone: the vertical slice (attach -> scan -> validate -> report).
/// It proves the whole Core stack against the real game. The overlay and the config
/// window dock onto the same objects in the next milestone - see docs/architecture.md.
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.WriteLine("PoEformance (C# port) - vertical slice");
        Console.WriteLine();

        // ── 1. Load the offset schema ────────────────────────────────────────
        // The schema ships next to the executable; a repo-relative fallback makes
        // `dotnet run` from the source tree work too.
        string schemaPath = FindSchemaFile();
        OffsetSchema schema = SchemaJson.Load(schemaPath);
        Console.WriteLine($"schema  {schemaPath}");
        Console.WriteLine($"        {schema.Structs.Count} structs, {schema.Statics.Count} statics, game version \"{schema.GameVersion}\"");
        Console.WriteLine();

        // ── 2. Attach (or replay) ────────────────────────────────────────────
        // `--replay <file>` runs the identical pipeline against a recorded session,
        // which is how the tool is developed without the game running.
        IMemoryReader reader;
        RecordingMemoryReader? recorder = null;
        string? replayPath = ArgValue(args, "--replay");

        if (replayPath is not null)
        {
            reader = ReplayMemoryReader.Load(File.OpenRead(replayPath));
            Console.WriteLine($"replay  {replayPath} ({((ReplayMemoryReader)reader).FrameCount} frames)");
        }
        else
        {
            Process? game = FindGameProcess();
            if (game is null)
            {
                Console.Error.WriteLine("PathOfExile process not found. Start the game (or pass --replay <file>).");
                return 1;
            }

            LiveMemoryReader? live = LiveMemoryReader.TryAttach(game);
            if (live is null)
            {
                Console.Error.WriteLine($"Found PoE2 (pid {game.Id}) but could not open it for reading - run elevated.");
                return 1;
            }

            Console.WriteLine($"attach  pid {live.ProcessId}, module 0x{live.ModuleBase:X} ({live.ModuleSize / (1024 * 1024)} MB)");

            // `--record <file>` captures the session for later replay.
            string? recordPath = ArgValue(args, "--record");
            if (recordPath is not null)
            {
                recorder = new RecordingMemoryReader(live, File.Create(recordPath));
                reader = recorder;
                Console.WriteLine($"record  {recordPath}");
            }
            else
            {
                reader = live;
            }
        }

        using IMemoryReader _ = reader;

        // ── 3. Resolve static anchors ────────────────────────────────────────
        var scanner = new PatternScanner(reader);
        var resolver = new StaticResolver(scanner);
        Console.WriteLine();
        Console.WriteLine("statics");
        var resolved = new Dictionary<string, ulong>();
        foreach (ResolvedStatic result in resolver.ResolveAll(schema))
        {
            Console.WriteLine($"  {(result.Found ? "ok  " : "MISS")}  {result.Name,-26} {(result.Found ? $"0x{result.Address:X}" : result.Detail)}");
            if (result.Found)
            {
                resolved[result.Name] = result.Address;
            }
        }

        recorder?.MarkFrame();

        // ── 4. Walk to the structs the schema can validate and run the report ─
        if (resolved.TryGetValue("GameStates", out ulong gameStatesStatic))
        {
            RunDriftReport(reader, schema, gameStatesStatic);
        }
        else
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("GameStates did not resolve - no drift report possible.");
            return 2;
        }

        recorder?.MarkFrame();
        return 0;
    }

    /// <summary>
    /// The attach-time drift report: resolves the pointer chain to the structs whose
    /// base addresses we can derive, validates each against its schema invariants,
    /// and prints the failures plus a summary line.
    /// </summary>
    private static void RunDriftReport(IMemoryReader reader, OffsetSchema schema, ulong gameStatesStatic)
    {
        Console.WriteLine();
        Console.WriteLine("drift report");

        var validator = new SchemaValidator(reader);
        int passed = 0, failed = 0, skipped = 0;

        void Report(string structName, ulong baseAddress)
        {
            if (baseAddress == 0 || !schema.Structs.TryGetValue(structName, out StructDef? def))
            {
                return;
            }

            foreach (FieldCheck check in validator.ValidateStruct(def, baseAddress))
            {
                switch (check.Outcome)
                {
                    case CheckOutcome.Pass:
                        passed++;
                        break;
                    case CheckOutcome.Fail:
                        failed++;
                        Console.WriteLine($"  FAIL  {check.StructName}.{check.FieldName} (+0x{check.Offset:X}): {check.Detail}");
                        break;
                    default:
                        skipped++;
                        break;
                }
            }
        }

        // GameStates static -> GameState struct.
        ulong gameState = reader.ReadPointer(gameStatesStatic);
        Report("GameState", gameState);

        // GameState -> InGameState (well-known index into the state array).
        ulong inGameState = 0;
        if (gameState != 0)
        {
            StructDef gs = schema.Structs["GameState"];
            long entrySize = gs.Constants["StateEntrySize"];
            long index = gs.Constants["InGameStateIndex"];
            ulong statesBase = gameState + (ulong)gs.OffsetOf("States");
            inGameState = reader.ReadPointer(statesBase + (ulong)(index * entrySize));
            Report("InGameState", inGameState);
        }

        // InGameState -> AreaInstance / WorldData (null while not in an area - the
        // validator reports that honestly rather than pretending).
        if (inGameState != 0)
        {
            StructDef igs = schema.Structs["InGameState"];
            ulong areaInstance = reader.ReadPointer(inGameState + (ulong)igs.OffsetOf("AreaInstanceData"));
            ulong worldData = reader.ReadPointer(inGameState + (ulong)igs.OffsetOf("WorldData"));
            Report("AreaInstance", areaInstance);
            Report("WorldData", worldData);

            if (areaInstance != 0)
            {
                StructDef ai = schema.Structs["AreaInstance"];
                ulong playerInfo = reader.ReadPointer(areaInstance + (ulong)ai.OffsetOf("PlayerInfo"));
                Report("LocalPlayerStruct", playerInfo);
            }
        }

        Console.WriteLine($"  {passed} pass, {failed} FAIL, {skipped} skipped (no invariant / needs samples)");
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

    private static string? ArgValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
