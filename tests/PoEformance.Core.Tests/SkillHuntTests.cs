using PoEformance.Game.Diagnostics;

namespace PoEformance.Core.Tests;

/// <summary>
/// The skill-name hunt's two judgements: what counts as text, and what counts as an identity.
/// </summary>
/// <remarks>
/// BOTH ARE FILTERS AGAINST A CONFIDENT WRONG ANSWER, which is the only kind this hunt can
/// produce. Following pointers out of an object and printing whatever looks like a string will
/// always find SOMETHING - a class name, a file path, a label the engine hangs on everything -
/// and every one of those looks exactly like a skill name until it is asked to tell two skills
/// apart. So the report marks a chain only when it gave a DIFFERENT name to every skill, and
/// these tests are what says that rule is the rule.
/// </remarks>
public class SkillHuntTests
{
    private const ulong Somewhere = 0x1_4000_0000;

    private static SkillHuntSample Frame(int animation, ulong skill, params (string Chain, string Text)[] texts)
        => new(
            animation,
            ActionId: 2,
            SkillObject: skill,
            Wrapper: Somewhere,
            Texts: [.. texts.Select(t => new SkillText(t.Chain, t.Text))],
            CastTypeHits: new Dictionary<int, int>(),
            SkillTableEntries: 41);

    [Fact]
    public void AChainThatNamesEverySkillDifferentlyIsAnIdentity()
    {
        SkillHuntFindings findings = SkillHunt.Analyze(
        [
            Frame(299, 0x1_4000_1000, ("skill+0x000+0x000", "Spark")),
            Frame(299, 0x1_4000_1000, ("skill+0x000+0x000", "Spark")),
            Frame(472, 0x1_4000_2000, ("skill+0x000+0x000", "Flamewall")),
            Frame(474, 0x1_4000_3000, ("skill+0x000+0x000", "OrbOfStorms")),
        ]);

        SkillNameCandidate candidate = Assert.Single(findings.Candidates);
        Assert.True(candidate.IsAFunction);
        Assert.Equal(3, candidate.Animations);
        Assert.Equal(["Spark"], candidate.ByAnimation[299]);
    }

    [Fact]
    public void AChainThatGivesEverySkillTheSameNameIsNot()
    {
        // The failure this hunt is most likely to produce, and the reason the rule is "different
        // for each" rather than "found a string": a class name, a module path or an engine label
        // is present on every skill object and reads like a perfectly good answer.
        SkillHuntFindings findings = SkillHunt.Analyze(
        [
            Frame(299, 0x1_4000_1000, ("skill+0x008+0x010", "ActiveSkill")),
            Frame(472, 0x1_4000_2000, ("skill+0x008+0x010", "ActiveSkill")),
            Frame(474, 0x1_4000_3000, ("skill+0x008+0x010", "ActiveSkill")),
        ]);

        SkillNameCandidate candidate = Assert.Single(findings.Candidates);
        Assert.False(candidate.IsAFunction);
    }

    [Fact]
    public void AChainThatIsAmbiguousForOneSkillIsNot()
    {
        // Two different strings for the same animation - a per-cast label, or a chain that walks
        // through something that moves. One name per skill is half the rule.
        SkillHuntFindings findings = SkillHunt.Analyze(
        [
            Frame(299, 0x1_4000_1000, ("skill+0x000+0x000", "Spark")),
            Frame(299, 0x1_4000_1000, ("skill+0x000+0x000", "SparkCast17")),
            Frame(472, 0x1_4000_2000, ("skill+0x000+0x000", "Flamewall")),
        ]);

        Assert.False(Assert.Single(findings.Candidates).IsAFunction);
    }

    [Fact]
    public void TheBestChainsComeFirst()
    {
        SkillHuntFindings findings = SkillHunt.Analyze(
        [
            Frame(299, 0x1_4000_1000, ("noise", "Engine"), ("real", "Spark")),
            Frame(472, 0x1_4000_2000, ("noise", "Engine"), ("real", "Flamewall")),
        ]);

        Assert.Equal("real", findings.Candidates[0].Chain);
        Assert.True(findings.Candidates[0].IsAFunction);
        Assert.False(findings.Candidates[1].IsAFunction);
    }

    [Fact]
    public void OnlyAnOffsetUniqueInTheTableCountsAsTheCastType()
    {
        // A skill's cast type is unique in its own granted-skill table, so an offset that held
        // the live animation id on forty entries is holding a common constant - a level, a
        // flag, a zero - and matching it is a coincidence the report must not rank.
        var samples = new List<SkillHuntSample>
        {
            Frame(299, 0x1_4000_1000) with { CastTypeHits = new Dictionary<int, int> { [0x0C] = 40, [0x5C] = 1 } },
            Frame(472, 0x1_4000_2000) with { CastTypeHits = new Dictionary<int, int> { [0x5C] = 1 } },
        };

        SkillHuntFindings findings = SkillHunt.Analyze(samples);

        (int offset, int frames) = Assert.Single(findings.CastTypeOffsets);
        Assert.Equal(0x5C, offset);
        Assert.Equal(2, frames);
    }

    [Fact]
    public void TheSessionCountsAreReported()
    {
        // "Nothing was cast" and "cast plenty, found no string" are different failures with
        // different answers, and the report can only tell them apart if the counts are kept.
        SkillHuntFindings findings = SkillHunt.Analyze(
        [
            Frame(299, 0x1_4000_1000, ("real", "Spark")),
            Frame(0, 0) with { Wrapper = 0 },
        ]);

        Assert.Equal(2, findings.Frames);
        Assert.Equal(1, findings.CastingFrames);
        Assert.Equal(1, findings.FramesWithSkillObject);
        Assert.Equal(41, findings.SkillTableEntries);
    }

    [Fact]
    public void ASessionWithNoCastsSaysSoRatherThanListingNothing()
    {
        var report = new StringWriter();
        SkillHunt.Report(SkillHunt.Analyze([Frame(0, 0) with { Wrapper = 0 }]), report);

        Assert.Contains("NOTHING WAS CAST", report.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A string in a region big enough to read past, the way real memory is.
    /// </summary>
    /// <remarks>
    /// The fake reader refuses any span not covered EXACTLY, which is stricter than the thing it
    /// stands in for: a real page is 4 KB, so reading a few bytes past a short string succeeds.
    /// Placing bare strings would therefore test the harness rather than the code.
    /// </remarks>
    private static void PlaceInAPage(FakeMemoryReader memory, ulong address, string text)
    {
        var page = new byte[128];
        System.Text.Encoding.Unicode.GetBytes(text, page);
        memory.Place(address, page);
    }

    [Fact]
    public void PrintableWideTextIsReadAndEverythingElseIsRefused()
    {
        var memory = new FakeMemoryReader();
        PlaceInAPage(memory, 0x1_4000_1000, "OrbOfStorms");

        // A pointer, a float and a counter must not read as a name. The high-byte-zero rule is
        // what does it: real UTF-16 ASCII has every second byte zero, and a block of numbers
        // essentially never does.
        var numbers = new byte[128];
        BitConverter.TryWriteBytes(numbers, 0x0000_7FF6_1234_5678UL);
        memory.Place(0x1_4000_2000, numbers);

        var tooShort = new byte[128];
        System.Text.Encoding.Unicode.GetBytes("AB", tooShort);
        memory.Place(0x1_4000_3000, tooShort);

        Assert.Equal("OrbOfStorms", SkillHunt.TextAt(memory, 0x1_4000_1000));
        Assert.Null(SkillHunt.TextAt(memory, 0x1_4000_2000));
        Assert.Null(SkillHunt.TextAt(memory, 0x1_4000_3000));

        // An address nothing was placed at, and a null pointer.
        Assert.Null(SkillHunt.TextAt(memory, 0x1_4000_9999));
        Assert.Null(SkillHunt.TextAt(memory, 0));
    }

    [Fact]
    public void AStringRunningOffTheEndOfMappedMemoryComesBackShortRatherThanEmpty()
    {
        // The documented consequence of reading in chunks, pinned so nobody has to rediscover
        // it from a puzzling report. It is the SAFE direction to be wrong in: the analysis
        // treats two different strings for one skill as a disqualification, so a truncated name
        // can only cost a chain its mark - it can never promote a wrong chain to an identity.
        var memory = new FakeMemoryReader();
        byte[] whole = System.Text.Encoding.Unicode.GetBytes("OrbOfStorms");
        memory.Place(0x1_4000_4000, whole[..16]);

        Assert.Equal("OrbOfSto", SkillHunt.TextAt(memory, 0x1_4000_4000));
    }
}
