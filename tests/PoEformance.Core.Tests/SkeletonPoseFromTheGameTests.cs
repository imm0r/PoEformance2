using System.Numerics;
using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// Posing a real skeleton with its own real keyframes.
/// </summary>
/// <remarks>
/// THESE RUN WITHOUT THE GAME, which is the one gift the old layout gives: before version 8 an
/// .ast keeps its frames loose in the file rather than in an Oodle-packed bundle, so a fixture of
/// a few kilobytes carries genuine keys that can be sampled offline. Every number below came out
/// of Path of Exile 2's own files.
///
/// WHAT THEY ARE FOR IS THE COMPOSITION ORDER. A wrong one still produces matrices - that is the
/// whole difficulty - so each check below is arranged so that the wrong order fails it:
///
///   - a bone whose keys hold it exactly at its bind pose must come out with the IDENTITY skin
///     matrix, because the two compositions down the tree are then the same one. Swap the order
///     and they are not, and the identity is the one matrix that cannot appear by luck.
///   - the posed skeleton must stay inside a box the size of the rest pose. The classic wrong
///     order sends bones outward by a factor of the tree's depth, which a box catches at once.
///   - bones must keep their length. Rotation cannot change the distance from a parent, and on
///     this rig most bones are not translated at all, so those distances are a fixed quantity the
///     file supplies and nothing here computes.
/// </remarks>
public class SkeletonPoseFromTheGameTests
{
    /// <summary>
    /// Art/Models/MONSTERS/blackguardminorinquisitor/rig.ast - 43 bones, its smallest animation.
    /// </summary>
    /// <remarks>
    /// The real file is 382,590 bytes over 13 animations; this is its header, all 43 bones, and
    /// <c>stand_guard_stone_01</c> with every one of its tracks - 9203 bytes. Two bytes are edited,
    /// the animation count at offset 3, from 13 to 1, so the walk still lands on the last byte of
    /// the file. Every key the pose is taken from is the game's own.
    /// </remarks>
    private const string Fixture = "blackguard-v6.one.ast";

    /// <summary>
    /// Art/Models/MONSTERS/animatedweapon/rig.ast - three bones, and one of them held still.
    /// </summary>
    /// <remarks>
    /// KEPT FOR ONE TEST, because that test needs a chain the animation does not move and this rig
    /// supplies one: in <c>attack1_2hsword</c> the root sits exactly on its bind and so does
    /// L_Weapon, while R_Weapon swings through 31 keys. The 43-bone rig has no such chain in any
    /// animation small enough to keep - its root moves in all of them, and a moved root moves
    /// every bone under it.
    /// </remarks>
    private const string Still = "animatedweapon-v7.three.ast";

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

    private static (AnimationSkeleton Rig, SkeletonPose Pose, AnimationTracks Tracks) Ready(
        string which = Fixture)
    {
        AnimationSkeleton rig = AnimationSkeleton.Read(
            File.ReadAllBytes(Path.Combine(DirectoryHolding("tests"), "tests", "fixtures", which)));
        Assert.True(rig.Ready, rig.Why);

        SkeletonPose? pose = SkeletonPose.Of(rig);
        Assert.NotNull(pose);

        SkeletonAnimation only = rig.Animations[0];
        byte[]? frames = rig.Tracks(only, (_, _) => null);
        Assert.NotNull(frames);

        AnimationTracks tracks = AnimationTracks.Read(frames, only.Tracks, rig.Version);
        Assert.True(tracks.Ready, tracks.Why);
        return (rig, pose, tracks);
    }

    /// <summary>The real keys read back, one track per bone, with the animation's own length.</summary>
    [Fact]
    public void TheRealKeyframesRead()
    {
        (AnimationSkeleton rig, _, AnimationTracks tracks) = Ready();

        Assert.Equal(43, rig.Bones.Count);
        Assert.Equal("flinch", rig.Animations[0].Name);
        Assert.Equal(rig.Bones.Count, tracks.Tracks.Count);

        // EVERY BONE, EXACTLY ONCE. A track names the bone it moves, and a reader off by a field
        // reports the same bone twice and misses another.
        Assert.Equal(
            Enumerable.Range(0, rig.Bones.Count),
            tracks.Tracks.Select(one => one.Bone).OrderBy(one => one));

        // The last key sits on the animation's frame count, so its length is that over the rate.
        Assert.True(tracks.Frames > 0f);
        Assert.Equal(tracks.Frames, MathF.Round(tracks.Frames));
    }

    /// <summary>
    /// The bone tree comes out as a body: one root, and every bone reachable from it.
    /// </summary>
    /// <remarks>
    /// A BONE NAMES ITS NEXT SIBLING AND ITS FIRST CHILD, never its parent, so the tree is worked
    /// out rather than read. Taking a sibling for a child builds one chain 43 deep - which still
    /// poses, and still looks like a skeleton until it moves.
    /// </remarks>
    [Fact]
    public void TheBonesFormOneBody()
    {
        (AnimationSkeleton rig, SkeletonPose pose, _) = Ready();

        Assert.Single(pose.Parents, one => one < 0);
        Assert.Equal("root", rig.Bones[pose.Parents.ToList().IndexOf(-1)].Name);

        // The named bones sit where a body puts them: a thigh under its leg's root, a foot under
        // a knee. These names and links are the file's; the parentage is what is being checked.
        int thigh = Named(rig, "jnt_Leg_l_02");
        Assert.Equal("jnt_Leg_l_01", rig.Bones[pose.Parents[thigh]].Name);

        // AND THE REST POSE IS A BODY TOO. Composed down the tree, the bones spread through the
        // model's space instead of piling up at the origin - which is what a parent-relative bind
        // read as model space would do.
        var spread = new List<float>();
        foreach (Matrix4x4 one in pose.BindModel)
        {
            spread.Add(one.Translation.Length());
        }

        Assert.True(spread.Max() > 50f, "the rest pose reaches away from the root");
        Assert.True(spread.Max() < 1000f, $"and not absurdly far: {spread.Max()}");
    }

    /// <summary>
    /// The rig comes out left-right symmetric, which is what says the tree was read as a tree.
    /// </summary>
    /// <remarks>
    /// THE ONLY CHECK HERE THAT CATCHES A SIBLING READ AS A CHILD, and it took a measurement to
    /// find one that does. The obvious guesses all fail: a wrong tree still has exactly one root,
    /// still reaches a plausible distance (190 units against 174), and is not even much deeper
    /// (14 against 9) - the comment on SkeletonPose used to claim it builds one chain 43 long,
    /// which is simply not what happens. Worse, the bone-length check cannot see it either,
    /// because that compares against the parent this same walk chose: both sides move together
    /// and the test quietly measures nothing.
    ///
    /// WHAT IS INDEPENDENT IS THE GAME'S OWN NAMING. The rig names mirrored bones jnt_Leg_l_02 and
    /// jnt_Leg_r_02, and a skeleton is built symmetrically: the parent of one must be the mirror of
    /// the parent of the other, or the same bone where a limb joins the body. That holds for all
    /// eleven mirrored pairs on this rig and for eight of them once siblings are read as children -
    /// so the assertion is ALL of them, and the three that break are what it lives on. Nothing in
    /// SkeletonPose reads a bone's name.
    /// </remarks>
    [Fact]
    public void TheRigComesOutSymmetric()
    {
        (AnimationSkeleton rig, SkeletonPose pose, _) = Ready();

        var at = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var one = 0; one < rig.Bones.Count; one++)
        {
            at[rig.Bones[one].Name] = one;
        }

        var pairs = 0;
        for (var left = 0; left < rig.Bones.Count; left++)
        {
            string name = rig.Bones[left].Name;
            if (!name.Contains("_l_", StringComparison.Ordinal)
                || !at.TryGetValue(Mirrored(name), out int right))
            {
                continue;
            }

            pairs++;
            int over = pose.Parents[left];
            int under = pose.Parents[right];
            Assert.True(over >= 0 && under >= 0, $"{name} or its mirror has no parent");

            string said = rig.Bones[over].Name;
            Assert.True(
                over == under || rig.Bones[under].Name == Mirrored(said),
                $"{name} hangs off {said} but {Mirrored(name)} hangs off {rig.Bones[under].Name}");
        }

        Assert.True(pairs >= 8, $"the rig should have mirrored bones to check; found {pairs}");
    }

    /// <summary>The same bone on the other side, by the rig's own naming.</summary>
    private static string Mirrored(string name)
        => name.Replace("_l_", "", StringComparison.Ordinal)
            .Replace("_r_", "_l_", StringComparison.Ordinal)
            .Replace("", "_r_", StringComparison.Ordinal);

    /// <summary>
    /// A bone held at its bind pose by its own keys comes out with the identity skin matrix.
    /// </summary>
    /// <remarks>
    /// THE ONE THAT CATCHES A SWAPPED MULTIPLICATION. Where a track's keys reproduce the bone's
    /// rest transform, the animated composition down the tree IS the bind composition, so the
    /// inverse of one against the other is the identity - exactly, not approximately. Nothing in
    /// the pose arranges that: it needs the parentage, the order of the multiply and the decompose
    /// all to agree with the file. The other order also yields matrices, and they look like these.
    ///
    /// This rig supplies such bones by itself; the test finds them rather than assuming which.
    ///
    /// TWO THINGS THIS ASKED WRONGLY BEFORE IT ASKED RIGHTLY, both worth keeping written down.
    /// "At its bind pose" is not "at the identity": a bone's rest transform carries a rotation of
    /// its own, so a track whose quaternion is the identity is holding the bone somewhere the bind
    /// does not put it. And a bone at its own bind is still MOVED BY ITS PARENT - the identity
    /// needs the whole chain up to the root to be held, which is the composition doing exactly
    /// what it should and looked, at first, like it failing.
    /// </remarks>
    [Fact]
    public void ABoneHeldAtItsBindPoseSkinsToTheIdentity()
    {
        (AnimationSkeleton rig, SkeletonPose pose, AnimationTracks tracks) = Ready(Still);
        pose.Take(tracks, 0f);

        var held = 0;
        for (var bone = 0; bone < rig.Bones.Count; bone++)
        {
            // Up the tree to the root: one moved ancestor and this bone has moved with it.
            var still = true;
            for (int up = bone; up >= 0 && still; up = pose.Parents[up])
            {
                still = AtBind(rig, tracks, up);
            }

            if (!still)
            {
                continue;
            }

            held++;
            Matrix4x4 skin = pose.Skin[bone];
            for (var row = 0; row < 4; row++)
            {
                for (var cell = 0; cell < 4; cell++)
                {
                    Assert.Equal(row == cell ? 1f : 0f, Cell(skin, row, cell), 3);
                }
            }
        }

        Assert.True(held >= 1, "the rig should hold at least one whole chain at rest");
    }

    /// <summary>Whether this bone's own keys reproduce its rest transform exactly at frame nought.</summary>
    private static bool AtBind(AnimationSkeleton rig, AnimationTracks tracks, int bone)
    {
        Matrix4x4 bind = rig.Bones[bone].Bind;
        if (!Matrix4x4.Decompose(bind, out Vector3 was, out Quaternion faced, out Vector3 sat))
        {
            return false;
        }

        foreach (BoneTrack track in tracks.Tracks)
        {
            if (track.Bone != bone)
            {
                continue;
            }

            // A quaternion and its negation are the same rotation, hence the absolute value.
            return Vector3.Distance(tracks.Position(track, 0f, sat), sat) <= 0.001f
                && Vector3.Distance(tracks.Scale(track, 0f, was), was) <= 0.001f
                && Math.Abs(Quaternion.Dot(tracks.Rotation(track, 0f, faced), faced)) >= 0.9999f;
        }

        // A bone the animation does not mention keeps its rest pose, which is what this asks.
        return true;
    }

    /// <summary>
    /// Through the whole animation the skeleton stays a skeleton: in the box, and bones keep their
    /// length.
    /// </summary>
    /// <remarks>
    /// EVERY FRAME, not a sample of them: a bracket that is wrong only at a key boundary passes a
    /// test that steps over the boundaries, and that is the likeliest thing to be wrong in a
    /// keyframe reader. A bone's distance from its parent is the file's own number and the pose
    /// never touches it, so it is a quantity to check against rather than one to compute.
    /// </remarks>
    [Fact]
    public void ThroughTheWholeAnimationItStaysASkeleton()
    {
        (AnimationSkeleton rig, SkeletonPose pose, AnimationTracks tracks) = Ready();

        float reach = pose.BindModel.Max(one => one.Translation.Length());

        for (var frame = 0f; frame <= tracks.Frames; frame += 0.5f)
        {
            pose.Take(tracks, frame);

            for (var bone = 0; bone < rig.Bones.Count; bone++)
            {
                Vector3 where = pose.World[bone].Translation;
                Assert.True(
                    float.IsFinite(where.X) && float.IsFinite(where.Y) && float.IsFinite(where.Z),
                    $"bone {bone} left the numbers at frame {frame}");

                // Four times the rest pose's reach is generous for an animation and nowhere near
                // what a wrong composition produces, which grows with the depth of the tree.
                Assert.True(
                    where.Length() < (reach * 4f) + 100f,
                    $"bone {rig.Bones[bone].Name} is {where.Length():F0} from the root at frame {frame}");

                // A BONE CANNOT STRETCH. Where the animation does not translate it, its distance
                // from its parent is the bind's and stays the bind's however it turns.
                int over = pose.Parents[bone];
                BoneTrack track = tracks.Tracks.First(one => one.Bone == bone);
                Matrix4x4 bind = rig.Bones[bone].Bind;
                if (over < 0
                    || Vector3.Distance(
                        tracks.Position(track, frame, bind.Translation), bind.Translation) > 0.001f)
                {
                    continue;
                }

                float posed = Vector3.Distance(where, pose.World[over].Translation);
                float rest = bind.Translation.Length();
                Assert.Equal(rest, posed, 2);
            }
        }
    }

    /// <summary>The pose moves, which a test of everything staying put would not notice.</summary>
    /// <remarks>
    /// THE COUNTERWEIGHT TO EVERY CHECK ABOVE. A pose that ignored its keyframes entirely would
    /// satisfy the identity check, the box and the bone lengths perfectly - it would simply be the
    /// rest pose at every frame. So something has to actually have moved.
    /// </remarks>
    [Fact]
    public void AndTheAnimationActuallyMovesIt()
    {
        (AnimationSkeleton rig, SkeletonPose pose, AnimationTracks tracks) = Ready();

        pose.Take(tracks, 0f);
        Vector3[] first = [.. pose.World.Select(one => one.Translation)];

        pose.Take(tracks, tracks.Frames / 2f);
        Vector3[] middle = [.. pose.World.Select(one => one.Translation)];

        float moved = first.Zip(middle, Vector3.Distance).Max();
        Assert.True(moved > 0.5f, $"nothing moved between the ends of the animation ({moved:F3})");

        // And going back to where it started puts it back exactly, which a sampler that carried
        // state between calls would fail.
        pose.Take(tracks, 0f);
        Vector3[] again = [.. pose.World.Select(one => one.Translation)];
        Assert.Equal(first.Length, again.Length);
        for (var one = 0; one < first.Length; one++)
        {
            Assert.Equal(first[one], again[one]);
        }
    }

    private static int Named(AnimationSkeleton rig, string name)
    {
        for (var one = 0; one < rig.Bones.Count; one++)
        {
            if (rig.Bones[one].Name == name)
            {
                return one;
            }
        }

        Assert.Fail($"the rig has no bone called {name}");
        return -1;
    }

    private static float Cell(Matrix4x4 one, int row, int cell)
        => row switch
        {
            0 => cell switch { 0 => one.M11, 1 => one.M12, 2 => one.M13, _ => one.M14 },
            1 => cell switch { 0 => one.M21, 1 => one.M22, 2 => one.M23, _ => one.M24 },
            2 => cell switch { 0 => one.M31, 1 => one.M32, 2 => one.M33, _ => one.M34 },
            _ => cell switch { 0 => one.M41, 1 => one.M42, 2 => one.M43, _ => one.M44 },
        };
}
