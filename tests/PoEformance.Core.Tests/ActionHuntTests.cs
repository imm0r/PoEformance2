using PoEformance.Game.Diagnostics;

namespace PoEformance.Core.Tests;

/// <summary>
/// The action-field hunt's analyzer, against synthetic sessions whose right answer is known.
/// </summary>
/// <remarks>
/// The analyzer is pure over samples on purpose, so the classifier that will judge a REAL
/// recording can be proven on sessions where every decoy is planted deliberately: a vtable
/// that never moves, a toggler that ignores activity, a coordinate pair that is constant
/// everywhere, one that never sits still. Each is a shape that fooled somebody before -
/// see MatrixHunt for the family history - and each must lose here before the live run is
/// worth anybody's game time.
///
/// No fixture recording yet, deliberately: a recording can only contain reads the running
/// build performed, so the first real fixture can only exist AFTER this build has run
/// <c>--actionhunt --record</c> against the game. These tests are what make that session
/// worth recording.
/// </remarks>
public class ActionHuntTests
{
    /// <summary>Where AnimationId sits, from the shipped schema so a drift moves the tests too.</summary>
    private static readonly int Anim = RealSessionTests.Schema().Structs["Actor"].OffsetOf("AnimationId");

    /// <summary>The planted action-pointer slot. PoE1's number, but any slot would do.</summary>
    private const int ActionSlot = 0x1A8;

    /// <summary>A plausible heap pointer with a non-zero LOW dword, the harder case for the
    /// id table - a pointer whose low half reads as a busy i32 must still not qualify there.</summary>
    private const ulong ActionPointer = 0x19000001000;

    /// <summary>A vtable-like slot: always the same plausible pointer, idle or not.</summary>
    private const int VtableSlot = 0x040;
    private const ulong VtablePointer = 0x7FF700001000;

    /// <summary>A slot that toggles on its own clock, uncorrelated with activity.</summary>
    private const int NoiseSlot = 0x300;

    private static byte[] Window(int animationId, params (int Offset, ulong Value)[] slots)
    {
        var window = new byte[ActionHunt.WindowSize];
        BitConverter.TryWriteBytes(window.AsSpan(Anim), animationId);
        foreach ((int offset, ulong value) in slots)
        {
            BitConverter.TryWriteBytes(window.AsSpan(offset), value);
        }

        return window;
    }

    private static ActionHuntSample At(byte[] window, float x = 0, float y = 0, Dictionary<int, byte[]>? followed = null)
        => new(0x1000, window, x, y, followed ?? new Dictionary<int, byte[]>());

    /// <summary>A session of quiet and acting stretches with the planted slots behaving.</summary>
    private static List<ActionHuntSample> PlantedSession()
    {
        var samples = new List<ActionHuntSample>();
        for (int i = 0; i < 30; i++)
        {
            samples.Add(At(Window(
                0,
                (ActionSlot, 0UL),
                (VtableSlot, VtablePointer),
                (NoiseSlot, i % 2 == 0 ? 0x7FF800002000UL : 0UL))));
        }

        for (int i = 0; i < 30; i++)
        {
            samples.Add(At(Window(
                i % 2 == 0 ? 268 : 472, // DodgeRoll and Flamewall - two distinct acting ids
                (ActionSlot, ActionPointer),
                (VtableSlot, VtablePointer),
                (NoiseSlot, i % 2 == 0 ? 0x7FF800002000UL : 0UL),
                (0x208, i % 2 == 0 ? 3UL : 5UL)))); // a planted id field, PoE1's offset
        }

        return samples;
    }

    [Fact]
    public void PointerTableNamesTheCorrelatedTogglerAndNothingElse()
    {
        ActionHuntFindings findings = ActionHunt.Analyze(PlantedSession(), Anim, []);

        // The vtable never toggles and the noise slot toggles without caring what the actor
        // does; only the planted slot separates the two states. One candidate, not "the best
        // of several" - anything else in this table on this input is a classifier bug.
        ActionPointerCandidate found = Assert.Single(findings.Pointers);
        Assert.Equal(ActionSlot, found.Offset);
        Assert.Equal(1.0, found.ActingNonNull, 3);
        Assert.Equal(0.0, found.QuietNonNull, 3);
    }

    [Fact]
    public void IdTableKeepsAnimationIdAsTheControlAndFindsThePlantedField()
    {
        ActionHuntFindings findings = ActionHunt.Analyze(PlantedSession(), Anim, []);

        // AnimationId qualifies BY CONSTRUCTION - acting is defined off it - and staying in
        // the table is its job: it is the built-in proof the classifier finds the one field
        // of this shape that is already known.
        Assert.Contains(findings.Ids, c => c.Offset == Anim && c.Kind == "i32");
        Assert.Contains(findings.Ids, c => c.Offset == 0x208 && c.Kind == "i32");

        // The action POINTER must not moonlight here, even though its low dword is a busy
        // non-zero i32 during every acting frame - one distinct value is a flag, not an id.
        Assert.DoesNotContain(findings.Ids, c => c.Offset == ActionSlot);
    }

    [Fact]
    public void CastCrossCheckCountsOnlyIdsThatAreOwnSkillCastTypes()
    {
        ActionHuntFindings findings = ActionHunt.Analyze(PlantedSession(), Anim, [472, 999]);

        Assert.Equal(new[] { 268, 472 }, findings.ActingAnimationIds);
        Assert.Equal(1, findings.CastTypeMatches);
    }

    /// <summary>World position for a planted grid-style destination: linear, with an offset.</summary>
    /// <remarks>
    /// The factor is deliberately NOT 1: the fit must recover the encoding's scale rather
    /// than assume world units, because nobody knows yet whether the real field stores grid
    /// or world coordinates - and the whole point of the fit is not having to.
    /// </remarks>
    private static float WorldOf(float pair) => (10.87f * pair) + 50f;

    [Fact]
    public void DestinationPairIsFoundScaleFreeAndDecoysAreNot()
    {
        var samples = new List<ActionHuntSample>();
        var random = new Random(7); // seeded: a flaky hunt test would poison real hunts' credibility

        for (int segment = 0; segment < 4; segment++)
        {
            // An idle beat between arrivals: pointer null, so no followed block at all.
            for (int i = 0; i < 6; i++)
            {
                samples.Add(At(Window(0), WorldOf(100 + (40 * (segment - 1))), WorldOf(200 + (30 * (segment - 1)))));
            }

            // One click-move: the pair sits still while the player converges onto it.
            float destX = 100 + (40 * segment);
            float destY = 200 + (30 * segment);
            for (int step = 0; step < 10; step++)
            {
                var block = new byte[ActionHunt.FollowSize];
                BitConverter.TryWriteBytes(block.AsSpan(0x170), destX);
                BitConverter.TryWriteBytes(block.AsSpan(0x174), destY);

                // Decoy one: a pair that never sits still - jitter beyond the segmenter's
                // tolerance every tick, so it can never accumulate an arrival.
                BitConverter.TryWriteBytes(block.AsSpan(0x100), (float)(random.NextDouble() * 500));
                BitConverter.TryWriteBytes(block.AsSpan(0x104), (float)(random.NextDouble() * 500));

                // Decoy two: perfectly still EVERYWHERE - one destination is no evidence.
                BitConverter.TryWriteBytes(block.AsSpan(0x080), 777f);
                BitConverter.TryWriteBytes(block.AsSpan(0x084), 888f);

                float share = step / 9f;
                samples.Add(At(
                    Window(195, (ActionSlot, ActionPointer)),
                    WorldOf(100 + (40 * (segment - 1))) + (share * (WorldOf(destX) - WorldOf(100 + (40 * (segment - 1))))),
                    WorldOf(200 + (30 * (segment - 1))) + (share * (WorldOf(destY) - WorldOf(200 + (30 * (segment - 1))))),
                    new Dictionary<int, byte[]> { [ActionSlot] = block }));
            }
        }

        ActionHuntFindings findings = ActionHunt.Analyze(samples, Anim, []);

        DestinationCandidate best = Assert.Single(findings.Destinations);
        Assert.Equal(ActionSlot, best.PointerOffset);
        Assert.Equal(0x170, best.PairOffset);
        Assert.Equal("f32", best.Kind);
        Assert.Equal(4, best.Segments);
        Assert.True(best.FitQuality > 0.999, $"fit {best.FitQuality}");
        Assert.Equal(10.87, best.Scale, 1);
    }

    [Fact]
    public void AnalyzeToleratesEmptyAndContrastlessInput()
    {
        Assert.Equal(0, ActionHunt.Analyze([], Anim, []).Frames);

        List<ActionHuntSample> allQuiet = [.. Enumerable.Range(0, 20).Select(_ => At(Window(0)))];
        ActionHuntFindings findings = ActionHunt.Analyze(allQuiet, Anim, []);
        Assert.Equal(20, findings.Frames);
        Assert.Equal(0, findings.ActingFrames);
        Assert.Empty(findings.Pointers);
        Assert.Empty(findings.Ids);
        Assert.Empty(findings.Destinations);
    }

    [Fact]
    public void ReportSurvivesEveryShapeOfFindings()
    {
        // The report is what a person acts on at two in the morning; it must not throw on
        // the empty case, the contrastless case, or the full one.
        using var sink = new StringWriter();
        ActionHunt.Report(ActionHunt.Analyze([], Anim, []), Anim, sink);
        ActionHunt.Report(ActionHunt.Analyze([At(Window(0)), At(Window(268))], Anim, []), Anim, sink);
        ActionHunt.Report(ActionHunt.Analyze(PlantedSession(), Anim, [472]), Anim, sink);

        Assert.Contains("AnimationId (schema; the control)", sink.ToString(), StringComparison.Ordinal);
    }
}
