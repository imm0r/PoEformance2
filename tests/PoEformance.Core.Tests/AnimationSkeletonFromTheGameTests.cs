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

    /// <summary>
    /// A version 11 particle rig WITH A LIGHT IN IT, whole - all 1827 bytes of it.
    /// </summary>
    /// <remarks>
    /// Art/particles/monster_particles/Act2_FOUR/MastadonBoss/models/necromancer_ball_explode.
    /// Kept whole because it is smaller than this comment's share of the repository, and because
    /// the keyframes being present means the bundle's declared size is a real number to check the
    /// one animation's offsets against rather than a truncation artefact.
    /// </remarks>
    private const string Lit = "ballexplode-light.ast";

    /// <summary>
    /// Art/Models/MONSTERS/animatedweapon/rig.ast - a version 7 rig, cut off after its bones.
    /// </summary>
    /// <remarks>
    /// THE BONES ARE ALL THAT IS KEPT because the bones are all that is read. The real file is
    /// 276,698 bytes of inline keyframes; 400 carries its three bones and the beginning of the
    /// first animation header, which is exactly as far as this reader is willing to go.
    /// </remarks>
    private const string Old = "animatedweapon-v7.bones.ast";

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

    private static AnimationSkeleton Rig(string which = Fixture)
        => AnimationSkeleton.Read(
            File.ReadAllBytes(Path.Combine(DirectoryHolding("tests"), "tests", "fixtures", which)));

    /// <summary>The real file walks, and comes out with the bones and animations it claims.</summary>
    [Fact]
    public void TheRealFileWalks()
    {
        AnimationSkeleton said = Rig();

        Assert.True(said.Ready, said.Why);
        Assert.Equal(12, said.Version);
        Assert.Equal(Bones, said.Bones.Count);
        Assert.Equal(Animations, said.Animations.Count);

        // NO LIGHT ON THIS ONE, which is exactly why it could not catch the light bug on its own -
        // see ARealRigWithALightInItReadsPastIt. Worth asserting so the pair reads as deliberate.
        Assert.Empty(said.Lights);
        Assert.Equal(Bones, said.Animations[0].Tracks);
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

    /// <summary>
    /// A real rig with a light in it reads past the light and accounts for its bundle exactly.
    /// </summary>
    /// <remarks>
    /// THIS FILE IS HERE BECAUSE BasicSkeleton COULD NOT CATCH THE BUG. It has no light, so a
    /// reader that walked straight from the bones to the animations passed every assertion above
    /// while being wrong about fifteen of the install's rigs. A survey of the whole game found
    /// them; this fixture is so that finding cannot be lost again.
    ///
    /// THE TRACK COUNT IS THE INDEPENDENT WITNESS. Eight bones, one light, and every animation
    /// header says NINE tracks - the game animates the light like a joint, and nothing in this
    /// reader produces that agreement.
    /// </remarks>
    [Fact]
    public void ARealRigWithALightInItReadsPastIt()
    {
        AnimationSkeleton said = Rig(Lit);

        Assert.True(said.Ready, said.Why);
        Assert.Equal(11, said.Version);
        Assert.Equal(8, said.Bones.Count);
        Assert.Equal(["PointLightShape1"], said.Lights);

        Assert.Single(said.Animations);
        SkeletonAnimation only = said.Animations[0];
        Assert.Equal("animate", only.Name);
        Assert.Equal(30, only.Rate);
        Assert.Equal(0x6f, only.Kind);
        Assert.Equal(said.Bones.Count + said.Lights.Count, only.Tracks);

        // And the arithmetic closes on a file whose keyframes are actually present.
        Assert.Equal(string.Empty, AstSurvey.Tiling(said));
        Assert.Equal(3945, said.TrackBytes);
        Assert.Equal(3945, only.At + only.Length);
    }

    /// <summary>
    /// A real version 7 rig gives up its bones and refuses to guess at its animations.
    /// </summary>
    /// <remarks>
    /// THE FILE CLAIMS 142 ANIMATIONS AND THIS READER RETURNS NONE, on purpose. Their headers are
    /// perfectly readable - hunting them by hand in the real 276 KB file finds all 142, at the
    /// layout this reader already implements - but between one and the next sit that animation's
    /// own keyframes, at a length no field has been shown to give: 1539, 2727, 1283, 1539, on an
    /// unchanging three tracks. Striding over them blind is what a survey of the install caught,
    /// as framerates of 191 and 232 and a spray of one-off kind bytes.
    ///
    /// SO THE ASSERTION IS THAT IT SAYS SO. An empty list with a reason can be acted on; a list of
    /// plausible rubbish is indistinguishable from data until somebody tries to play it.
    /// </remarks>
    [Fact]
    public void ARealOldRigGivesUpItsBonesAndRefusesItsAnimations()
    {
        AnimationSkeleton said = Rig(Old);

        Assert.True(said.Ready, said.Why);
        Assert.Equal(7, said.Version);

        // Read at version 12's stride these come back as float data; the older bone is a byte
        // shorter, and all three names being real is what says the gate is honoured.
        Assert.Equal(3, said.Bones.Count);
        Assert.Equal(["root", "R_Weapon", "L_Weapon"], said.Bones.Select(one => one.Name));

        Assert.Empty(said.Animations);
        Assert.Contains("version 7", said.Why, StringComparison.Ordinal);

        // And the survey must not count it as proof of anything.
        Assert.False(AstSurvey.Checkable(said));
        Assert.NotEmpty(AstSurvey.Tiling(said));
    }
}
