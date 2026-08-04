using System.Text;
using PoEformance.Core.Memory;
using PoEformance.Core.Scanning;
using PoEformance.Core.Schema;

namespace PoEformance.Core.Diagnostics;

/// <summary>
/// The attach-time drift report: resolve the static anchors, walk the pointer chain to
/// every struct whose base address can be derived, validate each against its schema
/// invariants, and render the result.
/// </summary>
/// <remarks>
/// This lives in Core, not in the Windows-only App, on purpose: it depends only on
/// <see cref="IMemoryReader"/> and the schema, so it runs against a live attach, a
/// replay, or a synthetic test process, on any OS. The App is then a thin shell that
/// attaches (the one Windows-specific step) and calls <see cref="Run"/>. That keeps the
/// engine testable and the composition root readable.
/// </remarks>
public static class DriftReport
{
    /// <summary>
    /// Runs the full report against <paramref name="reader"/> using
    /// <paramref name="schema"/>, writing a human-readable rendering to
    /// <paramref name="output"/> and returning the structured result.
    /// </summary>
    /// <param name="scanner">
    /// A scanner over the same reader. Reused across calls (it caches the module image),
    /// so re-running the report in a watch loop is cheap.
    /// </param>
    /// <param name="verbose">When true, passing and skipped rows are printed too.</param>
    public static DriftReportResult Run(
        IMemoryReader reader,
        PatternScanner scanner,
        OffsetSchema schema,
        TextWriter output,
        bool verbose = false)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(output);

        // ── Resolve static anchors ───────────────────────────────────────────
        var resolver = new StaticResolver(scanner);
        List<ResolvedStatic> statics = resolver.ResolveAll(schema);
        var resolved = new Dictionary<string, ulong>();

        output.WriteLine("statics");
        foreach (ResolvedStatic result in statics)
        {
            output.WriteLine($"  {(result.Found ? "ok  " : "MISS")}  {result.Name,-26} {(result.Found ? $"0x{result.Address:X}" : result.Detail)}");
            if (result.Found)
            {
                resolved[result.Name] = result.Address;
            }
        }

        if (!resolved.TryGetValue("GameStates", out ulong gameStatesStatic))
        {
            output.WriteLine();
            output.WriteLine("GameStates did not resolve - no drift report possible.");
            return new DriftReportResult(statics, [], 0, 0, 0, GameStatesResolved: false);
        }

        // ── Walk the pointer chain and validate every struct on the way ──────
        output.WriteLine();
        output.WriteLine("drift report");
        var validator = new SchemaValidator(reader);
        var checks = new List<FieldCheck>();
        int passed = 0, failed = 0, skipped = 0;

        void Report(string label, ulong baseAddress)
        {
            if (!schema.Structs.TryGetValue(label, out StructDef? def))
            {
                return;
            }

            if (baseAddress == 0)
            {
                output.WriteLine($"  --    {label} not reachable (null pointer on the way - not in an area?)");
                return;
            }

            foreach (FieldCheck check in validator.ValidateStruct(def, baseAddress))
            {
                checks.Add(check);
                switch (check.Outcome)
                {
                    case CheckOutcome.Pass:
                        passed++;
                        if (verbose)
                        {
                            output.WriteLine($"  ok    {Row(check)}");
                        }

                        break;
                    case CheckOutcome.Fail:
                        failed++;
                        output.WriteLine($"  FAIL  {Row(check)}");
                        break;
                    default:
                        skipped++;
                        if (verbose)
                        {
                            output.WriteLine($"  --    {Row(check)}");
                        }

                        break;
                }
            }
        }

        WalkChain(reader, schema, gameStatesStatic, Report);

        output.WriteLine($"  {passed} pass, {failed} FAIL, {skipped} skipped");
        return new DriftReportResult(statics, checks, passed, failed, skipped, GameStatesResolved: true);
    }

    private static string Row(FieldCheck c) => $"{c.StructName}.{c.FieldName} (+0x{c.Offset:X}): {c.Detail}";

    /// <summary>
    /// Walks static GameStates -> GameState -> InGameState -> AreaInstance / WorldData ->
    /// LocalPlayerStruct, invoking <paramref name="report"/> for each struct name with the
    /// resolved base address (0 when a pointer on the way was null - the game may not be
    /// in an area, which the caller surfaces honestly rather than hiding).
    /// </summary>
    private static void WalkChain(IMemoryReader reader, OffsetSchema schema, ulong gameStatesStatic, Action<string, ulong> report)
    {
        ulong gameState = reader.ReadPointer(gameStatesStatic);
        report("GameState", gameState);
        if (gameState == 0)
        {
            return;
        }

        StructDef gs = schema.Structs["GameState"];
        ulong statesBase = gameState + (ulong)gs.OffsetOf("States");
        ulong inGameState = reader.ReadPointer(statesBase + (ulong)(gs.Constants["InGameStateIndex"] * gs.Constants["StateEntrySize"]));
        report("InGameState", inGameState);
        if (inGameState == 0)
        {
            return;
        }

        StructDef igs = schema.Structs["InGameState"];
        ulong areaInstance = reader.ReadPointer(inGameState + (ulong)igs.OffsetOf("AreaInstanceData"));
        ulong worldData = reader.ReadPointer(inGameState + (ulong)igs.OffsetOf("WorldData"));
        report("AreaInstance", areaInstance);
        report("WorldData", worldData);

        if (areaInstance != 0)
        {
            StructDef ai = schema.Structs["AreaInstance"];
            ulong playerInfo = reader.ReadPointer(areaInstance + (ulong)ai.OffsetOf("PlayerInfo"));
            report("LocalPlayerStruct", playerInfo);
        }
    }
}

/// <summary>Structured outcome of a drift report, for tests and non-console callers.</summary>
public sealed record DriftReportResult(
    IReadOnlyList<ResolvedStatic> Statics,
    IReadOnlyList<FieldCheck> Checks,
    int Passed,
    int Failed,
    int Skipped,
    bool GameStatesResolved)
{
    /// <summary>True when every static resolved and no field check failed.</summary>
    public bool AllGood => GameStatesResolved && Failed == 0 && Statics.All(s => s.Found);

    /// <summary>The failing checks, for a caller that wants to act on them.</summary>
    public IEnumerable<FieldCheck> Failures => Checks.Where(c => c.Outcome == CheckOutcome.Fail);

    /// <summary>Compact one-line summary, e.g. "6/6 statics, 24 pass, 0 FAIL, 9 skipped".</summary>
    public string Summary()
    {
        var sb = new StringBuilder();
        int found = Statics.Count(s => s.Found);
        sb.Append(found).Append('/').Append(Statics.Count).Append(" statics, ");
        sb.Append(Passed).Append(" pass, ").Append(Failed).Append(" FAIL, ").Append(Skipped).Append(" skipped");
        return sb.ToString();
    }
}
