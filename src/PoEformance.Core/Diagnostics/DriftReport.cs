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

        void Report(string label, ulong baseAddress, ulong parentAddress = 0, int parentFieldOffset = -1)
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
                        if (def.Field(check.FieldName)?.Invariant is Invariant.UnitVector3)
                        {
                            RunMatrixScan(reader, output, def, check, baseAddress, parentAddress, parentFieldOffset);
                        }

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
    /// When a matrix invariant fails, hunt for the cause in the two places it can hide:
    /// the matrix moved WITHIN this struct, or the POINTER that led to this struct drifted
    /// so the whole struct is being read at the wrong base.
    /// </summary>
    /// <remarks>
    /// Only diagonal unit vectors count as camera candidates. The first live run returned
    /// four perfect unit vectors that were all axis-aligned ((0,1,0), (1,0,0), ...) - rows
    /// of identity matrices, which memory is full of. Reporting those as answers would send
    /// the reader chasing noise, so they are labelled and ranked last.
    ///
    /// When no camera-like candidate exists in the struct, the far more likely explanation
    /// is the parent-pointer drift (see <see cref="PointerDriftScan"/>), so we sweep the
    /// neighbouring pointer slots of the parent and report any that yield a struct
    /// containing a real camera matrix at the schema's offset.
    /// </remarks>
    private static void RunMatrixScan(
        IMemoryReader reader,
        TextWriter output,
        StructDef def,
        FieldCheck check,
        ulong baseAddress,
        ulong parentAddress,
        int parentFieldOffset)
    {
        // 1. Did the matrix move inside this struct? Sweep generously - the struct is small
        //    and the scan is a handful of reads.
        List<MatrixCandidate> inStruct = MatrixScan.Scan(reader, baseAddress, 0, 0x600);
        PrintCandidates(output, "in struct", inStruct, reader, baseAddress, check.Offset);

        // 2. Did the POINTER to this struct drift? This is the +0x08-wave signature: a base
        //    that is a few bytes off makes every field inside read misaligned, which is
        //    exactly how a huge value lands in the matrix's direction row.
        if (parentAddress != 0 && parentFieldOffset >= 0)
        {
            int matrixOffset = check.Offset;
            List<PointerCandidate> bases = PointerDriftScan.Scan(
                reader, parentAddress, parentFieldOffset, radius: 0x40,
                probe: candidateBase => MatrixScan.HasCameraLike(
                    MatrixScan.Scan(reader, candidateBase, matrixOffset, matrixOffset + 4)));

            foreach (PointerCandidate candidate in bases.Take(3))
            {
                MatrixCandidate best = MatrixScan
                    .Scan(reader, candidate.Target, matrixOffset, matrixOffset + 4)
                    .First(c => !c.IsAxisAligned);
                string delta = candidate.Delta >= 0 ? $"+0x{candidate.Delta:X}" : $"-0x{-candidate.Delta:X}";
                output.WriteLine(
                    $"        parent: reading this struct from +0x{candidate.FieldOffset:X} ({delta}) gives a REAL camera "
                    + $"matrix at +0x{matrixOffset:X}  dir ({best.X:F3}, {best.Y:F3}, {best.Z:F3})");
                output.WriteLine(
                    $"                -> the parent pointer drifted; fix that offset, not this one.");
            }

            if (bases.Count == 0 && !MatrixScan.HasCameraLike(inStruct))
            {
                output.WriteLine("        parent: no neighbouring pointer slot yields a camera matrix either.");
            }
        }

        if (!MatrixScan.HasCameraLike(inStruct))
        {
            output.WriteLine(
                "        note: no DIAGONAL unit vector found in this struct - the axis-aligned hits above are "
                + "identity/basis rows, not a camera. Make sure the game is rendering a 3D scene (in an area, not a menu).");
        }
    }

    /// <summary>
    /// Prints the best few scan hits with their FULL matrix, marking axis-aligned ones as
    /// probable noise.
    /// </summary>
    /// <remarks>
    /// The full matrix is printed because the unit-direction test cannot, on its own, tell a
    /// projection from a rotation - and a wrong pick would still make the invariant go green,
    /// which is worse than a visible failure. Seeing scale and translation makes the choice
    /// evidence-based instead of a guess.
    /// </remarks>
    private static void PrintCandidates(TextWriter output, string where, List<MatrixCandidate> candidates, IMemoryReader reader, ulong baseAddress, int currentOffset)
    {
        foreach (MatrixCandidate c in candidates.Where(c => !c.IsAxisAligned).Take(4))
        {
            int delta = c.Offset - currentOffset;
            string deltaText = delta >= 0 ? $"+0x{delta:X}" : $"-0x{-delta:X}";
            output.WriteLine($"        {where}: CAMERA-LIKE at +0x{c.Offset:X} ({deltaText})  dir ({c.X:F3}, {c.Y:F3}, {c.Z:F3})");

            float[]? m = MatrixScan.ReadMatrix(reader, baseAddress, c.Offset);
            if (m is not null)
            {
                output.WriteLine($"                {MatrixScan.Describe(m)}");
                for (int row = 0; row < 4; row++)
                {
                    output.WriteLine($"                [{m[row * 4],12:G6} {m[(row * 4) + 1],12:G6} {m[(row * 4) + 2],12:G6} {m[(row * 4) + 3],12:G6} ]");
                }
            }
        }

        int axisAligned = candidates.Count(c => c.IsAxisAligned);
        if (axisAligned > 0)
        {
            output.WriteLine($"        {where}: {axisAligned} axis-aligned unit rows ignored (identity/basis matrices, not cameras)");
        }
    }

    /// <summary>
    /// Walks static GameStates -> GameState -> InGameState -> AreaInstance / WorldData ->
    /// LocalPlayerStruct, invoking <paramref name="report"/> for each struct name with the
    /// resolved base address (0 when a pointer on the way was null - the game may not be
    /// in an area, which the caller surfaces honestly rather than hiding).
    /// </summary>
    private static void WalkChain(IMemoryReader reader, OffsetSchema schema, ulong gameStatesStatic, Action<string, ulong, ulong, int> report)
    {
        ulong gameState = reader.ReadPointer(gameStatesStatic);
        report("GameState", gameState, 0, -1);
        if (gameState == 0)
        {
            return;
        }

        StructDef gs = schema.Structs["GameState"];
        ulong statesBase = gameState + (ulong)gs.OffsetOf("States");
        ulong inGameState = reader.ReadPointer(statesBase + (ulong)(gs.Constants["InGameStateIndex"] * gs.Constants["StateEntrySize"]));
        report("InGameState", inGameState, 0, -1);
        if (inGameState == 0)
        {
            return;
        }

        StructDef igs = schema.Structs["InGameState"];
        int areaOffset = igs.OffsetOf("AreaInstanceData");
        int worldOffset = igs.OffsetOf("WorldData");
        ulong areaInstance = reader.ReadPointer(inGameState + (ulong)areaOffset);
        ulong worldData = reader.ReadPointer(inGameState + (ulong)worldOffset);

        // Pass the origin (parent + field offset) so a failing check can ask the far more
        // useful question: did the POINTER that led here drift, rather than the fields inside?
        report("AreaInstance", areaInstance, inGameState, areaOffset);
        report("WorldData", worldData, inGameState, worldOffset);

        if (areaInstance != 0)
        {
            // LocalPlayerStruct is INLINE in AreaInstance (proven by the first live run:
            // the value AT PlayerInfo is already ServerDataPtr, so dereferencing here
            // validated ServerData as a struct base and the player chain "broke"). The
            // struct base is the ADDRESS AreaInstance+PlayerInfo, not the value there.
            StructDef ai = schema.Structs["AreaInstance"];
            ulong playerInfo = areaInstance + (ulong)ai.OffsetOf("PlayerInfo");
            report("LocalPlayerStruct", playerInfo, 0, -1);
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
