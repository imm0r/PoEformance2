using System.Numerics;
using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// Moving a mesh's vertices with a posed skeleton.
/// </summary>
/// <remarks>
/// THE MESH IS BUILT AND THE ANIMATION IS REAL, which is the honest split: no real mesh can be
/// fetched onto a machine without the game (the .smd files live in streaming bundles the mirror
/// does not serve), but the 43-bone blackguard rig and its flinch are in the repository with
/// genuine keyframes. So the tests hang synthetic vertices on real bones and let real keys move
/// them - and the check is one the pose itself supplies: a vertex sitting exactly on a bone, bound
/// wholly to it, must land exactly where that bone's world matrix says the bone went.
///
/// THAT CHECK IS NOT CIRCULAR. The vertex goes through inverse(bindModel) then world; the bone's
/// own position is world's translation. They agree only if the inverse bind really undoes the
/// bind composition for the point the bone sits at, which is what skinning depends on and what a
/// wrong inverse, a wrong order or a transposed matrix each break.
/// </remarks>
public class SkeletonPoseSkinningTests
{
    private const string Fixture = "blackguard-v6.one.ast";

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

    private static (AnimationSkeleton Rig, SkeletonPose Pose, AnimationTracks Tracks) Ready()
    {
        AnimationSkeleton rig = AnimationSkeleton.Read(
            File.ReadAllBytes(Path.Combine(DirectoryHolding("tests"), "tests", "fixtures", Fixture)));
        Assert.True(rig.Ready, rig.Why);

        SkeletonPose? pose = SkeletonPose.Of(rig);
        Assert.NotNull(pose);

        SkeletonAnimation only = rig.Animations[0];
        AnimationTracks tracks = AnimationTracks.Read(rig.Tracks(only, (_, _) => null), only.Tracks, rig.Version);
        Assert.True(tracks.Ready, tracks.Why);
        return (rig, pose, tracks);
    }

    /// <summary>A mesh with one vertex on every bone, each bound wholly to its own bone.</summary>
    private static SkinnedMesh OnEveryBone(SkeletonPose pose)
    {
        var vertices = new Packed.Vertex[pose.BindModel.Count];
        for (var bone = 0; bone < vertices.Length; bone++)
        {
            vertices[bone] = new Packed.Vertex(
                pose.BindModel[bone].Translation, [(byte)bone, 0, 0, 0], [255, 0, 0, 0]);
        }

        SkinnedMesh mesh = SkinnedMesh.Read(Packed.Mesh(vertices));
        Assert.True(mesh.Ready, mesh.Why);
        Assert.Equal(vertices.Length, mesh.Positions.Length);
        return mesh;
    }

    /// <summary>At rest, skinning changes nothing - every skin matrix is the identity.</summary>
    /// <remarks>
    /// THE DULL CHECK, AND THE ONE THAT CATCHES THE MOST: a transposed inverse, a wrong bone index
    /// stride, weights read as bones - all of them move a resting vertex. The comparison is exact
    /// because nothing here should have done any arithmetic at all.
    /// </remarks>
    [Fact]
    public void AtRestNothingMoves()
    {
        (_, SkeletonPose pose, _) = Ready();
        SkinnedMesh mesh = OnEveryBone(pose);
        pose.AtRest();

        var positions = new Vector3[mesh.Positions.Length];
        var normals = new Vector3[mesh.Positions.Length];
        pose.Move(mesh, positions, normals);

        for (var one = 0; one < positions.Length; one++)
        {
            Assert.Equal(mesh.Positions[one], positions[one]);
            Assert.Equal(mesh.Normals[one], normals[one]);
        }
    }

    /// <summary>
    /// Posed, a vertex bound wholly to a bone goes exactly where the bone went.
    /// </summary>
    [Fact]
    public void AVertexOnABoneFollowsTheBone()
    {
        (_, SkeletonPose pose, AnimationTracks tracks) = Ready();
        SkinnedMesh mesh = OnEveryBone(pose);

        var positions = new Vector3[mesh.Positions.Length];
        var normals = new Vector3[mesh.Positions.Length];

        foreach (float frame in new[] { 0f, tracks.Frames / 3f, tracks.Frames })
        {
            pose.Take(tracks, frame);
            pose.Move(mesh, positions, normals);

            for (var bone = 0; bone < positions.Length; bone++)
            {
                Vector3 went = pose.World[bone].Translation;
                Assert.True(
                    Vector3.Distance(positions[bone], went) < 0.01f,
                    $"bone {bone} at frame {frame}: vertex {positions[bone]} but bone {went}");

                // AND THE NORMAL STAYS UNIT LENGTH, which a blend does not do by itself.
                Assert.Equal(1f, normals[bone].Length(), 3);
            }
        }
    }

    /// <summary>A vertex shared between two bones lands between where each would put it.</summary>
    /// <remarks>
    /// LINEAR BLEND SKINNING IS THE WEIGHTED MEAN, so a half-and-half vertex is the midpoint of the
    /// two positions it would have if bound to either bone alone. That is the contract the weights
    /// express, and it is what makes a joint bend rather than tear.
    /// </remarks>
    [Fact]
    public void ASharedVertexIsBlended()
    {
        (_, SkeletonPose pose, AnimationTracks tracks) = Ready();
        pose.Take(tracks, tracks.Frames / 2f);

        // Two bones and a point that is not on either of them.
        const byte Left = 3;
        const byte Right = 7;
        var place = new Vector3(5f, -4f, 20f);

        SkinnedMesh only = SkinnedMesh.Read(Packed.Mesh(
        [
            new Packed.Vertex(place, [Left, 0, 0, 0], [255, 0, 0, 0]),
            new Packed.Vertex(place, [Right, 0, 0, 0], [255, 0, 0, 0]),
            new Packed.Vertex(place, [Left, Right, 0, 0], [128, 127, 0, 0]),
        ]));
        Assert.True(only.Ready, only.Why);

        var positions = new Vector3[3];
        var normals = new Vector3[3];
        pose.Move(only, positions, normals);

        // The two singly-bound copies went different ways, or the test proves nothing.
        Assert.True(Vector3.Distance(positions[0], positions[1]) > 0.5f, "the two bones moved the point apart");

        Vector3 expected = (positions[0] * (128f / 255f)) + (positions[1] * (127f / 255f));
        Assert.True(
            Vector3.Distance(positions[2], expected) < 0.01f,
            $"blended {positions[2]} but the weighted mean is {expected}");
    }

    /// <summary>The highest weighted bone is reported, and a zero-weight index does not count.</summary>
    [Fact]
    public void TheHighestWeightedBoneIsWhatCounts()
    {
        SkinnedMesh mesh = SkinnedMesh.Read(Packed.Mesh(
        [
            new Packed.Vertex(Vector3.Zero, [2, 9, 0, 0], [200, 55, 0, 0]),
            new Packed.Vertex(Vector3.One, [4, 200, 0, 0], [255, 0, 0, 0]),
            new Packed.Vertex(Vector3.UnitX, [1, 0, 0, 0], [255, 0, 0, 0]),
        ]));

        // Bone 200 is named but carries no weight, so it is not a bone the mesh uses.
        Assert.Equal(9, SkeletonPose.Highest(mesh));
        Assert.Equal(-1, SkeletonPose.Highest(null));
    }

    /// <summary>The tracks know how many bytes of keyframes they came out of, for the line that says so.</summary>
    [Fact]
    public void TheTracksKnowTheirSize()
    {
        (AnimationSkeleton rig, _, AnimationTracks tracks) = Ready();
        byte[]? frames = rig.Tracks(rig.Animations[0], (_, _) => null);

        Assert.NotNull(frames);
        Assert.True(frames.Length > 0);
        Assert.Equal(frames.Length, tracks.Bytes);
        Assert.Equal(0, AnimationTracks.None.Bytes);
    }

    /// <summary>
    /// The renderer draws posed vertices where it is told, and the bind pose where it is not.
    /// </summary>
    /// <remarks>
    /// HANDED ITS OWN VERTICES, THE POSED OVERLOAD IS THE STILL ONE - pixel for pixel. Handed a
    /// different set it draws those instead, which is what shows a change of pose at all. And
    /// handed a set of the wrong length it falls back to the mesh's own rather than reading past
    /// the end on the draw thread.
    /// </remarks>
    [Fact]
    public void TheRendererTakesPosedVertices()
    {
        (_, SkeletonPose pose, AnimationTracks tracks) = Ready();
        SkinnedMesh mesh = OnEveryBone(pose);

        GamePicture still = MeshPicture.Of(mesh, 64);
        GamePicture same = MeshPicture.Of(mesh, new MeshPicture.Canvas(64), mesh.Positions, mesh.Normals);
        Assert.Equal(still.Rgba, same.Rgba);

        var positions = new Vector3[mesh.Positions.Length];
        var normals = new Vector3[mesh.Positions.Length];
        pose.Take(tracks, tracks.Frames / 2f);
        pose.Move(mesh, positions, normals);

        GamePicture moved = MeshPicture.Of(mesh, new MeshPicture.Canvas(64), positions, normals);
        Assert.NotEqual(still.Rgba, moved.Rgba);

        GamePicture short_ = MeshPicture.Of(mesh, new MeshPicture.Canvas(64), positions.AsSpan(0, 3), normals.AsSpan(0, 3));
        Assert.Equal(still.Rgba, short_.Rgba);
    }
}
