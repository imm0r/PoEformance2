using PoEformance.Game.Diagnostics;
using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// The reader against a real skeleton, out of the game's own files.
/// </summary>
/// <remarks>
/// THE OTHER TESTS CHECK THE READER AGAINST A LAYOUT THIS PROJECT WROTE DOWN. This one checks the
/// layout against the GAME, which is the half a synthetic fixture cannot reach - a fixture built
/// to the same diagram as the reader agrees with it by construction and would keep agreeing if
/// both were wrong.
///
/// THE FIXTURE IS THE REAL FILE WITH ITS KEYFRAMES CUT OFF. BasicSkeleton's rig is 6,548,879 bytes
/// and 6,534,630 of those are compressed animation data - too big for a repository and not needed
/// for the question. What is kept is every byte up to the end of the embedded bundle's chunk table:
/// the header, all 49 bones, all 252 animation headers, and the bundle's own statement of how much
/// it unpacks to. That last number is what makes the cut file still worth something, because it is
/// what the offsets are checked against.
///
/// WHAT IT PINS, AND WHY NONE OF IT IS ARRANGED BY THE READER:
///   - the header is EIGHT bytes, where the published diagram says nine. Read as nine, every bone's
///     name lands inside the next bone's matrix and the names come out as rubbish.
///   - the animation offsets chain end to end with no gap, and the last one's end is exactly the
///     size the embedded bundle declares. Nothing in AnimationSkeleton computes either side of that.
///   - the bones' resting places land in the same negative-z space the mesh uses.
/// A reader off by one byte anywhere passes none of these.
/// </remarks>
public class AnimationSkeletonFromTheGameTests
{
    /// <summary>Art/Models/MONSTERS/BasicSkeleton/rig.ast, cut off after the bundle's chunk table.</summary>
    private const string Fixture = "basicskeleton-rig.headers.ast";

    /// <summary>What the file says about itself, measured before any of this was written.</summary>
    private const int Bones = 49;

    private const int Animations = 252;

    /// <summary>What the embedded bundle declares its keyframes unpack to.</summary>
    private const int Keyframes = 11_954_976;

    private static string DirectoryHolding(string child)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, child)))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir.FullName;
    }

    private static AnimationSkeleton Rig()
        => AnimationSkeleton.Read(
            File.ReadAllBytes(Path.Combine(DirectoryHolding("tests"), "tests", "fixtures", Fixture)));

    /// <summary>The real file walks, and comes out with the bones and animations it claims.</summary>
    [Fact]
    public void TheRealFileWalks()
    {
        AnimationSkeleton said = Rig();

        Assert.True(said.Ready, said.Why);
        Assert.Equal(12, said.Version);
        Assert.Equal(Bones, said.Bones.Count);
        Assert.Equal(Animations, said.Animations.Count);
        Assert.Equal(0, said.Lights);
    }

    /// <summary>
    /// Every bone's name is one, which is what an eight-byte header buys.
    /// </summary>
    /// <remarks>
    /// THE CHECK THAT CAUGHT THE OFF-BY-ONE. A nine-byte header reads the first bone perfectly and
    /// then drifts: from bone two on, the name length is picked out of the matrix and the "name" is
    /// whatever bytes follow. So a test on the first name alone would pass. This asks that ALL of
    /// them are printable, and that the ones the tree hangs off are spelled the way the .sm file
    /// beside them spells them - hip_jntBnd and chest_jntBnd are in BasicSkeleton.sm's BoneGroups,
    /// which is a second source for the same names.
    /// </remarks>
    [Fact]
    public void EveryBoneHasARealName()
    {
        AnimationSkeleton said = Rig();

        foreach (SkeletonBone bone in said.Bones)
        {
            Assert.NotEmpty(bone.Name);
            Assert.All(bone.Name, one => Assert.InRange(one, ' ', '~'));
        }

        Assert.Equal("root_jntBnd", said.Bones[0].Name);
        Assert.Contains(said.Bones, one => one.Name == "hip_jntBnd");
        Assert.Contains(said.Bones, one => one.Name == "chest_jntBnd");
        Assert.Contains(said.Bones, one => one.Name == "aux_R_foot_jntBnd");
    }

    /// <summary>The bones rest in the model's own space, where a chest is above a hip.</summary>
    /// <remarks>
    /// NEGATIVE Z IS UP HERE, which is the mesh's convention too - BasicSkeleton's bounding box
    /// runs z -189 to -0.4 with the feet at the end nearest zero. A reader that transposed the
    /// matrix would put the translation in the wrong row and these would all be zero.
    /// </remarks>
    [Fact]
    public void TheBonesRestWhereTheMeshDoes()
    {
        AnimationSkeleton said = Rig();

        float hip = said.Bones.First(one => one.Name == "hip_jntBnd").Bind.M43;
        float chest = said.Bones.First(one => one.Name == "chest_jntBnd").Bind.M43;

        Assert.InRange(hip, -120f, -80f);
        Assert.True(chest < hip, "the chest sits above the hip in the model's negative-z space");
    }

    /// <summary>
    /// The animation offsets chain end to end and account for the bundle exactly.
    /// </summary>
    /// <remarks>
    /// THE ONE THAT MATTERS, and the one nothing in this project arranges: 252 headers, each
    /// starting where the one before it ended, and the last ending on the number the embedded
    /// bundle's own header carries. Three independent statements agreeing - and the agreement is
    /// what says the offsets index the UNPACKED track region, which is what makes playing one
    /// animation a small read rather than a twelve-megabyte one.
    /// </remarks>
    [Fact]
    public void TheAnimationsTileTheTrackRegionExactly()
    {
        AnimationSkeleton said = Rig();

        Assert.Equal(Keyframes, said.TrackBytes);
        Assert.Equal(string.Empty, AstSurvey.Tiling(said));

        // Said again without the survey, because the survey is what would be wrong together with
        // the reader: the last header's own end is the bundle's size.
        SkeletonAnimation last = said.Animations[^1];
        Assert.Equal(Keyframes, last.At + last.Length);
    }

    /// <summary>The animations carry names, framerates and the three kind bytes that occur.</summary>
    /// <remarks>
    /// THE FRAMERATES AND KINDS ARE A SMALL CLOSED SET on this file - 30 and 60, and 0x6c, 0x6e,
    /// 0x6f - which is itself evidence the walk is aligned: a drifted reader picks those bytes out
    /// of a name or an offset and gets a spray of values instead of three.
    /// </remarks>
    [Fact]
    public void TheAnimationsAreNamedAndTimed()
    {
        AnimationSkeleton said = Rig();

        foreach (SkeletonAnimation move in said.Animations)
        {
            Assert.NotEmpty(move.Name);
            Assert.All(move.Name, one => Assert.InRange(one, ' ', '~'));
            Assert.Equal(Bones, move.Tracks);
            Assert.True(move.Rate is 30 or 60, $"{move.Name} runs at {move.Rate} fps");
            Assert.True(move.Kind is 0x6c or 0x6e or 0x6f, $"{move.Name} is kind {move.Kind}");
        }

        // FOUR OF 252 NAME A PARENT, which is the field a reader could drop and still walk every
        // other file in the install perfectly - see AnimationSkeleton's own remarks.
        Assert.Equal(4, said.Animations.Count(one => one.Parent.Length > 0));
    }
}
