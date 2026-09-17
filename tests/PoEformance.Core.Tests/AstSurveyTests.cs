using PoEformance.Game.Diagnostics;
using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// The skeleton survey, and above all the one check it exists for.
/// </summary>
/// <remarks>
/// THE SURVEY ITSELF NEEDS AN INSTALL AND THESE TESTS DO NOT, which is deliberate rather than a
/// limitation. What cannot be checked here is what a real install holds - that is the whole reason
/// the diagnostic exists. What CAN be checked is the arithmetic it reports, and that is where a
/// wrong answer would be believed: a tiling check that says "fine" on a file whose offsets do not
/// chain would read, in the output, exactly like the finding that makes playback worth building.
///
/// SO THE FIXTURES BREAK THE FILES ON PURPOSE. Each one is a skeleton laid out correctly except in
/// the one way the check is supposed to catch, which is the only way to know the check is doing
/// anything at all.
/// </remarks>
public class AstSurveyTests
{
    /// <summary>
    /// A file whose animations chain end to end and account for the bundle exactly.
    /// </summary>
    /// <remarks>
    /// THE SHAPE MEASURED ON THE REAL RIG: offsets 0, then 42117, then 92686, each one the
    /// previous one's end, and the last end equal to what the embedded bundle says it unpacks to.
    /// </remarks>
    [Fact]
    public void ARegionThatChainsEndToEndTiles()
    {
        AnimationSkeleton said = AnimationSkeleton.Read(
            Packed.Skeleton(
                [("root_jntBnd", 255, 255, 0f)],
                [
                    ("approach_01", string.Empty, 1, 30, 0x6f, 0, 400),
                    ("walk_01", string.Empty, 1, 60, 0x6c, 400, 500),
                ],
                Packed.Bundle(new byte[900], chunkSize: 64)));

        Assert.True(said.Ready);
        Assert.Equal(string.Empty, AstSurvey.Tiling(said));
    }

    /// <summary>A gap between two animations is caught, and the report names the one that moved.</summary>
    [Fact]
    public void AGapBetweenAnimationsIsCaught()
    {
        AnimationSkeleton said = AnimationSkeleton.Read(
            Packed.Skeleton(
                [("root_jntBnd", 255, 255, 0f)],
                [
                    ("approach_01", string.Empty, 1, 30, 0x6f, 0, 400),
                    ("walk_01", string.Empty, 1, 60, 0x6c, 408, 492),
                ],
                Packed.Bundle(new byte[900], chunkSize: 64)));

        string wrong = AstSurvey.Tiling(said);

        Assert.NotEmpty(wrong);
        Assert.Contains("walk_01", wrong, StringComparison.Ordinal);
        Assert.Contains("408", wrong, StringComparison.Ordinal);
        Assert.Contains("400", wrong, StringComparison.Ordinal);
    }

    /// <summary>An overlap is caught too, which a check on gaps alone would let through.</summary>
    [Fact]
    public void AnOverlapIsCaught()
    {
        AnimationSkeleton said = AnimationSkeleton.Read(
            Packed.Skeleton(
                [("root_jntBnd", 255, 255, 0f)],
                [
                    ("approach_01", string.Empty, 1, 30, 0x6f, 0, 400),
                    ("walk_01", string.Empty, 1, 60, 0x6c, 380, 520),
                ],
                Packed.Bundle(new byte[900], chunkSize: 64)));

        Assert.Contains("walk_01", AstSurvey.Tiling(said), StringComparison.Ordinal);
    }

    /// <summary>
    /// Headers that chain perfectly but do not reach the end of the bundle are caught.
    /// </summary>
    /// <remarks>
    /// THE ONE A WRONG READER PASSES. Offsets that chain prove only that the headers agree with
    /// each other; it is the agreement with the BUNDLE'S OWN SIZE that says the offsets index the
    /// unpacked track region and not something else. Drop this assertion and a reader that read
    /// the right fields out of the wrong file would still look correct.
    /// </remarks>
    [Fact]
    public void HeadersThatDoNotAccountForTheBundleAreCaught()
    {
        AnimationSkeleton said = AnimationSkeleton.Read(
            Packed.Skeleton(
                [("root_jntBnd", 255, 255, 0f)],
                [("walk_01", string.Empty, 1, 30, 0x6c, 0, 400)],
                Packed.Bundle(new byte[900], chunkSize: 64)));

        string wrong = AstSurvey.Tiling(said);

        Assert.Contains("400", wrong, StringComparison.Ordinal);
        Assert.Contains("900", wrong, StringComparison.Ordinal);
    }

    /// <summary>
    /// A skeleton with nothing hung on it does NOT tile - there was nothing to tile.
    /// </summary>
    /// <remarks>
    /// THIS TEST USED TO ASSERT THE OPPOSITE, and asserting the opposite is what let a survey of
    /// the whole install report "ALL 1613 TILE THEIR TRACK REGION EXACTLY" with sixty-nine of them
    /// having no track region at all. Nought equals nought is true and it is not evidence: those
    /// files are version 6 and 7, whose animation lists this reader will not walk, and counting
    /// them as passes put them behind the one number the feature is supposed to rest on.
    ///
    /// The honest answer is a reason, and a column of its own in the report - see Checkable.
    /// </remarks>
    [Fact]
    public void ASkeletonWithNothingHungOnItProvesNothing()
    {
        AnimationSkeleton said = AnimationSkeleton.Read(
            Packed.Skeleton([("root_jntBnd", 255, 255, 0f)], []));

        Assert.True(said.Ready);
        Assert.False(AstSurvey.Checkable(said));
        Assert.NotEmpty(AstSurvey.Tiling(said));
    }

    /// <summary>Animations with no keyframes behind them prove nothing either.</summary>
    [Fact]
    public void AnimationsWithNoKeyframesBehindThemProveNothing()
    {
        AnimationSkeleton said = AnimationSkeleton.Read(
            Packed.Skeleton(
                [("root_jntBnd", 255, 255, 0f)],
                [("walk_01", string.Empty, 1, 30, 0x6c, 0, 400)]));

        Assert.True(said.Ready);
        Assert.Equal(0, said.TrackBytes);
        Assert.False(AstSurvey.Checkable(said));
        Assert.Contains("no keyframes", AstSurvey.Tiling(said), StringComparison.Ordinal);
    }

    /// <summary>A light between the bones and the animations does not disturb the arithmetic.</summary>
    [Fact]
    public void ARigWithALightStillTiles()
    {
        AnimationSkeleton said = AnimationSkeleton.Read(
            Packed.Skeleton(
                [("root", 255, 255, 0f)],
                [
                    ("start", string.Empty, 2, 30, 0x6f, 0, 400),
                    ("idle", string.Empty, 2, 30, 0x6e, 400, 500),
                ],
                Packed.Bundle(new byte[900], chunkSize: 64),
                lights: ["minisun_pointlightShape"]));

        Assert.True(AstSurvey.Checkable(said));
        Assert.Equal(string.Empty, AstSurvey.Tiling(said));
        Assert.Equal(["minisun_pointlightShape"], said.Lights);
    }

    /// <summary>Nothing read is a reason rather than a pass, which is the direction that matters.</summary>
    [Fact]
    public void NothingReadIsNotTiling()
    {
        Assert.NotEmpty(AstSurvey.Tiling(null));
        Assert.NotEmpty(AstSurvey.Tiling(AnimationSkeleton.None));
    }

    /// <summary>No install is an empty result rather than a throw.</summary>
    [Fact]
    public void NoInstallSurveysNothing()
    {
        AstSurveyResult said = AstSurvey.Read(null, null);

        Assert.Equal(0, said.Asked);
        Assert.Equal(AstSurveyResult.Nothing.Read, said.Read);

        var wrote = new StringWriter();
        AstSurvey.Report(said, wrote);
        Assert.Contains("nothing to read", wrote.ToString(), StringComparison.Ordinal);
    }

    /// <summary>The spread is the smallest, the middle one and the largest, with the sum beside them.</summary>
    [Fact]
    public void TheSpreadIsLeastMiddleAndMost()
    {
        AstSpread said = AstSpread.Of([118, 12, 49, 49, 220]);

        Assert.Equal(12, said.Least);
        Assert.Equal(49, said.Middle);
        Assert.Equal(220, said.Most);
        Assert.Equal(448, said.Total);
        Assert.Equal(5, said.Files);
        Assert.Contains("12 least", said.Say, StringComparison.Ordinal);
    }

    /// <summary>The spread of nothing is nothing, and says so rather than dividing by it.</summary>
    [Fact]
    public void TheSpreadOfNothingIsNothing()
    {
        Assert.Equal(0, AstSpread.Of(null).Files);
        Assert.Equal(0, AstSpread.Of([]).Files);
        Assert.Equal("none", AstSpread.Of([]).Say);
    }

    /// <summary>A byte count comes out in units a person reads, and stays exact under a kilobyte.</summary>
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(900, "900 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(11_954_976, "11.4 MB")]
    [InlineData(2_000_000_000, "1.9 GB")]
    public void ByteCountsAreReadable(long count, string said)
        => Assert.Equal(said, AstSurvey.Bytes(count));

    /// <summary>
    /// A survey where every file tiles says so plainly; one where some do not leads with that.
    /// </summary>
    /// <remarks>
    /// THE HEADLINE IS THE FINDING, so it is worth a test of its own: somebody runs this once, on
    /// a machine this project will never see, and pastes what it printed. A run where the reader
    /// is wrong has to be obvious in that paste rather than four tables down.
    /// </remarks>
    [Fact]
    public void TheReportLeadsWithWhetherTheRegionsTile()
    {
        var clean = new StringWriter();
        AstSurvey.Report(Surveyed(read: 186, tiled: 186), clean);
        Assert.Contains("ALL 186 THAT CAN BE CHECKED TILE", clean.ToString(), StringComparison.Ordinal);

        var messy = new StringWriter();
        AstSurvey.Report(Surveyed(read: 186, tiled: 140), messy);

        string told = messy.ToString();
        Assert.Contains("140 of 186", told, StringComparison.Ordinal);
        Assert.DoesNotContain("ALL", told, StringComparison.Ordinal);
    }

    /// <summary>
    /// Files with nothing to check are counted apart from the ones that tile, and named as such.
    /// </summary>
    /// <remarks>
    /// THE HEADLINE'S DENOMINATOR IS WHAT COULD BE CHECKED. A run of 1615 files where 69 have no
    /// track region and 1546 tile must not read as "1546 of 1615" - that invites the reader to
    /// hunt 69 failures that do not exist - nor as "ALL 1615", which is the overstatement this
    /// whole change exists to remove. It reads as all 1546 that can be checked, and 69 more that
    /// cannot, on their own line.
    /// </remarks>
    [Fact]
    public void FilesWithNothingToCheckAreCountedApart()
    {
        var wrote = new StringWriter();
        AstSurvey.Report(Surveyed(read: 1615, tiled: 1546, unproven: 69), wrote);

        string told = wrote.ToString();
        Assert.Contains("ALL 1546 THAT CAN BE CHECKED TILE", told, StringComparison.Ordinal);
        Assert.Contains("69 more read but have NOTHING TO CHECK", told, StringComparison.Ordinal);
        Assert.DoesNotContain("1546 of 1615", told, StringComparison.Ordinal);
    }

    /// <summary>A result with nothing in it but the counts the headline needs.</summary>
    private static AstSurveyResult Surveyed(int read, int tiled, int unproven = 0)
        => new(
            Monsters: 2733,
            Files: 4102,
            Naming: 1204,
            Asked: read,
            Read: read,
            Tiled: tiled,
            Unproven: unproven,
            Named: new Dictionary<string, int> { ["ClientAnimationController.skeleton"] = read },
            Versions: new Dictionary<string, int> { ["12"] = read },
            Rates: new Dictionary<string, int>(),
            Kinds: new Dictionary<string, int>(),
            Animations: new Dictionary<string, int>(),
            Bones: AstSpread.Of([49]),
            Hung: AstSpread.Of([252]),
            Keyframes: 11_954_976,
            Unpacked: 0,
            Framed: 0,
            Faults: []);

    /// <summary>
    /// A block of keyframes walks as tracks when it is one, and refuses when it is not.
    /// </summary>
    /// <remarks>
    /// THE CHECK THAT ANSWERS THE LAST OPEN QUESTION, once somebody with the game runs it. Below
    /// version 8 the frames are in the file and this walk is proven against two real rigs; from
    /// version 8 they are in a bundle that needs Oodle, and whether THAT holds the same tracks is
    /// a guess. The survey unpacks one animation per rig and asks this - and the answer is only
    /// worth having if a block that is NOT tracks fails it, which is the second half below.
    /// </remarks>
    [Fact]
    public void KeyframesWalkAsTracksOrTheyDoNot()
    {
        // One track: a byte, the bone, six counts, then 2 scales, 3 rotations and 2 positions.
        byte[] one = Packed.Frames([(0, 2, 3, 2)], version: 12);
        Assert.Equal(one.Length, AnimationSkeleton.Walk(one, 1, 12));

        // Two of them, which is what an animation over two bones looks like.
        byte[] two = Packed.Frames([(0, 2, 3, 2), (1, 2, 31, 31)], version: 12);
        Assert.Equal(two.Length, AnimationSkeleton.Walk(two, 2, 12));

        // ASKED FOR THE WRONG NUMBER, the walk does not come to the block's length - which is what
        // makes the survey's comparison meaningful rather than automatic.
        Assert.NotEqual(two.Length, AnimationSkeleton.Walk(two, 1, 12));
        Assert.Equal(-1, AnimationSkeleton.Walk(two, 3, 12));

        // AND A BLOCK OF NOTHING PARSES AS A TRACK OF NOTHING, which is the sharp edge here: 64
        // zero bytes are a well-formed header claiming no frames at all, so the walk succeeds and
        // comes to 33. It is the COMPARISON WITH THE BLOCK'S LENGTH that rejects it, not the walk -
        // which is why the survey asks whether the two are equal rather than whether it parsed.
        Assert.Equal(33, AnimationSkeleton.Walk(new byte[64], 1, 12));
        Assert.NotEqual(64, AnimationSkeleton.Walk(new byte[64], 1, 12));
    }
}
