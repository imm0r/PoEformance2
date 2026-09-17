using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// The animation skeleton, and the container the keyframes are kept in.
/// </summary>
/// <remarks>
/// WHAT MADE THIS READABLE WAS SPACING, not the published diagram. That diagram gives the header
/// as nine bytes; it is eight, and the difference puts every bone's name inside the next bone's
/// matrix - which does not look like an off-by-one, it looks like a mangled string. What settled
/// it on the real file was measuring from one name's first byte to the next: always the name's own
/// length plus 68, over all 49 bones, which pins the fixed part and the header with it.
///
/// SO THE TESTS BUILD FILES RATHER THAN ASSERT AGAINST ONE. A fixture here is bytes this project
/// laid out itself, which proves the reader agrees with a written-down layout - and that layout is
/// the one the real rig was measured into. What a synthetic file cannot prove is that the layout
/// matches the GAME; that came from three independent numbers agreeing on the real file, recorded
/// in AnimationSkeleton's own remarks.
/// </remarks>
public class AnimationSkeletonTests
{
    /// <summary>The version PoE 2 ships, and the only one measured against a real file.</summary>
    private const int Shipped = 12;

    [Fact]
    public void NothingToReadIsAReasonRatherThanAThrow()
    {
        Assert.False(AnimationSkeleton.Read((byte[]?)null).Ready);
        Assert.False(AnimationSkeleton.Read([]).Ready);
        Assert.False(AnimationSkeleton.Read(new byte[AnimationSkeleton.HeaderBytes]).Ready);
        Assert.Equal(AnimationSkeleton.None.Ready, AnimationSkeleton.Read((GameFiles?)null, "x.ast").Ready);

        Assert.NotEmpty(AnimationSkeleton.Read(new byte[4]).Why);
    }

    /// <summary>A skeleton claiming no bones at all is not one.</summary>
    [Fact]
    public void ASkeletonOfNoBonesIsRefused()
    {
        // A file with a bone in it, whose COUNT is then zeroed - so the length check passes and
        // the count is what the reader has to object to. Building an empty one instead makes an
        // eight-byte file, which is refused for being short and proves nothing about the count.
        byte[] file = Packed.Skeleton([("root_jntBnd", 255, 255, 0f)], []);
        file[1] = 0;

        AnimationSkeleton said = AnimationSkeleton.Read(file);

        Assert.False(said.Ready);
        Assert.Contains("0 bones", said.Why, StringComparison.Ordinal);
    }

    /// <summary>
    /// The bones come back with their names, their tree and where they rest.
    /// </summary>
    /// <remarks>
    /// THE HIERARCHY IS SIBLING-AND-CHILD, not parent, and 255 means none - the same sentinel the
    /// header uses for "no lights". Turning it into -1 here is the one liberty this reader takes,
    /// because 255 as a bone index is a real index in a file with 255 bones.
    /// </remarks>
    [Fact]
    public void TheBonesComeBackWithTheirNamesTreeAndRestingPlaces()
    {
        (string, int, int, float)[] bones =
        [
            ("root_jntBnd", 255, 1, 0f),
            ("hip_jntBnd", 2, 18, -97.55f),
            ("chest_jntBnd", 255, 255, -139.69f),
        ];

        AnimationSkeleton said = AnimationSkeleton.Read(Packed.Skeleton(bones, []));

        Assert.True(said.Ready);
        Assert.Equal(Shipped, said.Version);
        Assert.Equal(3, said.Bones.Count);

        Assert.Equal("root_jntBnd", said.Bones[0].Name);
        Assert.Equal(-1, said.Bones[0].Sibling);
        Assert.Equal(1, said.Bones[0].Child);

        Assert.Equal("hip_jntBnd", said.Bones[1].Name);
        Assert.Equal(2, said.Bones[1].Sibling);
        Assert.Equal(18, said.Bones[1].Child);

        // BOTH ENDS OF THE SENTINEL, because a bone with neither is the ordinary leaf.
        Assert.Equal(-1, said.Bones[2].Sibling);
        Assert.Equal(-1, said.Bones[2].Child);

        // The matrix is row major and so is System.Numerics, so the translation is the last row -
        // and it lands in the model's own negative-z space, where a hip sits below a root.
        Assert.Equal(-97.55f, said.Bones[1].Bind.M43, 3);
        Assert.Equal(-139.69f, said.Bones[2].Bind.M43, 3);
        Assert.Equal(1f, said.Bones[0].Bind.M11, 3);
    }

    /// <summary>The animations come back with their names, rate and where their frames are.</summary>
    [Fact]
    public void TheAnimationsComeBackWithTheirNamesRateAndWhereTheirFramesAre()
    {
        (string, string, int, int, int, int, int)[] moves =
        [
            ("approach_sword_sword_01", "", 49, 30, 0x6f, 0, 42117),
            ("walk_sword_shield_02", "", 49, 60, 0x6c, 42117, 85657),
            ("run_01", "walk_01", 49, 30, 0x6e, 127774, 1000),
        ];

        AnimationSkeleton said = AnimationSkeleton.Read(Packed.Skeleton([("root_jntBnd", 255, 255, 0f)], moves));

        Assert.True(said.Ready);
        Assert.Equal(3, said.Animations.Count);

        Assert.Equal("approach_sword_sword_01", said.Animations[0].Name);
        Assert.Equal(30, said.Animations[0].Rate);
        Assert.Equal(0x6f, said.Animations[0].Kind);
        Assert.Equal(49, said.Animations[0].Tracks);
        Assert.Equal(0, said.Animations[0].At);
        Assert.Equal(42117, said.Animations[0].Length);

        Assert.Equal(60, said.Animations[1].Rate);
        Assert.Equal(42117, said.Animations[1].At);

        // THE PARENT NAME IS THE ONE THAT WOULD GO UNNOTICED: four of 252 on the real rig carry
        // one, so a reader that skipped it would walk every other file perfectly and drift on
        // those four - and drift, here, is a plausible wrong animation rather than a crash.
        Assert.Equal(string.Empty, said.Animations[0].Parent);
        Assert.Equal("walk_01", said.Animations[2].Parent);
        Assert.Equal("run_01", said.Animations[2].Name);
    }

    /// <summary>
    /// The keyframes come out of the bundle at the end, one animation at a time.
    /// </summary>
    /// <remarks>
    /// A RANGE AND NOT THE WHOLE THING. One real rig holds twelve megabytes of keyframes across
    /// 252 animations; unpacking all of it to play a walk cycle would be most of a second and all
    /// of that memory. The bundle already knows how to unpack only the chunks a range falls in -
    /// what this checks is that the offsets in the headers index the UNPACKED bytes, which is what
    /// makes that possible.
    /// </remarks>
    [Fact]
    public void TheKeyframesComeOutOfTheBundleOneAnimationAtATime()
    {
        byte[] frames = new byte[900];
        for (var at = 0; at < frames.Length; at++)
        {
            frames[at] = (byte)(at % 251);
        }

        (string, string, int, int, int, int, int)[] moves =
        [
            ("walk_01", "", 1, 30, 0x6c, 0, 400),
            ("run_01", "", 1, 30, 0x6c, 400, 500),
        ];

        byte[] file = Packed.Skeleton([("root_jntBnd", 255, 255, 0f)], moves, Packed.Bundle(frames, chunkSize: 64));
        AnimationSkeleton said = AnimationSkeleton.Read(file);

        Assert.True(said.Ready);
        Assert.True(said.TracksAt > 0);
        Assert.Equal(frames.Length, said.TrackBytes);

        Assert.Equal(frames[..400], said.Tracks(said.Animations[0], Packed.AsIs));
        Assert.Equal(frames[400..], said.Tracks(said.Animations[1], Packed.AsIs));
    }

    /// <summary>A file with no bundle behind it is a skeleton with no keyframes, not a broken one.</summary>
    [Fact]
    public void ASkeletonWithNoKeyframesIsStillASkeleton()
    {
        AnimationSkeleton said = AnimationSkeleton.Read(Packed.Skeleton([("root_jntBnd", 255, 255, 0f)], []));

        Assert.True(said.Ready);
        Assert.Equal(-1, said.TracksAt);
        Assert.Equal(0, said.TrackBytes);
        Assert.Null(said.Tracks(default, Packed.AsIs));
    }

    /// <summary>
    /// A file that stops in the middle says where, and does not walk off the end.
    /// </summary>
    /// <remarks>
    /// EVERY LENGTH, because the interesting cut is not one of them but all of them: a reader that
    /// checks its bounds in three places out of four is one truncation away from an exception in
    /// the middle of a survey over the whole install.
    /// </remarks>
    [Fact]
    public void AFileThatStopsInTheMiddleSaysWhere()
    {
        byte[] whole = Packed.Skeleton(
            [("root_jntBnd", 255, 1, 0f), ("hip_jntBnd", 255, 255, -97.55f)],
            [("walk_01", "", 1, 30, 0x6c, 0, 10)]);

        for (var cut = 1; cut < whole.Length; cut++)
        {
            AnimationSkeleton said = AnimationSkeleton.Read(whole[..cut]);

            if (said.Ready)
            {
                // A cut past the headers is a skeleton with a short or missing bundle, which is
                // allowed - but it must never claim keyframes it does not have.
                Assert.True(said.TrackBytes >= 0);
                continue;
            }

            Assert.NotEmpty(said.Why);
        }
    }

    /// <summary>
    /// An older version is read to the older layout, byte for byte.
    /// </summary>
    /// <remarks>
    /// NOT MEASURED, AND THE TEST SAYS SO. Only version 12 was read off a real file; the gates for
    /// 8, 10 and 11 come from the published diagram. What this pins is that the reader HONOURS
    /// them - that a version 7 file is walked with a 67-byte bone and a 6-byte animation header
    /// rather than the 68 and 15 that 12 uses - so if the diagram is ever corrected, exactly one
    /// place changes and this test fails loudly instead of the reader drifting quietly.
    /// </remarks>
    [Fact]
    public void AnOlderVersionIsReadToTheOlderLayout()
    {
        AnimationSkeleton said = AnimationSkeleton.Read(
            Packed.Skeleton(
                [("root_jntBnd", 255, 255, 0f)],
                [("walk_01", "ignored", 1, 30, 0x6c, 0, 0)],
                tail: null,
                version: 7));

        Assert.True(said.Ready);
        Assert.Equal(7, said.Version);
        Assert.Equal("root_jntBnd", said.Bones[0].Name);

        Assert.Single(said.Animations);
        Assert.Equal("walk_01", said.Animations[0].Name);

        // Before version 8 there are no offsets in the header and before 11 no parent name, so a
        // v7 file cannot carry either however the fixture was asked to build it.
        Assert.Equal(string.Empty, said.Animations[0].Parent);
        Assert.Equal(0, said.Animations[0].At);
    }
}
