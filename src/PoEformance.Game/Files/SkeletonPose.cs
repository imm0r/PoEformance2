using System.Numerics;

namespace PoEformance.Game.Files;

/// <summary>
/// A skeleton posed at a moment: where every bone has moved, and what that does to a vertex.
/// </summary>
/// <remarks>
/// THE BIND MATRICES ARE PARENT-RELATIVE, which is the fact the whole of this turns on and which
/// was measured rather than assumed. On a real 43-bone rig a thigh's bind translation is
/// (52.01, 0, 0) and a foot's is (19.37, 0, 0) - bone lengths along a local axis, which is what a
/// parent-relative rest pose looks like. Read as model space they would put a knee at x=52 with
/// the root at z=-107, which is not a leg.
///
/// SO THERE ARE TWO COMPOSITIONS DOWN THE SAME TREE, and the difference between them is the pose:
///
///     bindModel(bone) = bind(bone) * bindModel(parent)          the rest pose, once
///     world(bone, t)  = local(bone, t) * world(parent, t)        the animation, per frame
///     skin(bone, t)   = inverse(bindModel(bone)) * world(bone, t)
///
/// A vertex is stored in MODEL SPACE in its bind pose, so the inverse takes it into the bone's own
/// space and the world matrix brings it back out to where the animation has put that bone. With no
/// animation the two compositions are the same and every skin matrix is the identity, which is the
/// check <see cref="AtRest"/> makes and the one that catches a wrong multiplication order: the
/// other order also produces matrices, and they also look like matrices.
///
/// ROW VECTORS THROUGHOUT, because that is what the file stores and what System.Numerics does: a
/// bone's translation is in M41..M43, and <c>Vector3.Transform(v, m)</c> is v*M. So a child's
/// local matrix goes on the LEFT of its parent's. Swapping that is the classic way to get a
/// skeleton that explodes outward from the origin as it plays.
///
/// THE ARRAYS ARE HELD AND REUSED. A pose is computed every frame while something plays, and a rig
/// runs to 255 bones; allocating four matrix arrays per frame is a megabyte a second through the
/// collector, which this project has already paid for once - see MeshPicture.Canvas.
/// </remarks>
public sealed class SkeletonPose
{
    private readonly AnimationSkeleton _skeleton;
    private readonly int[] _parent;
    private readonly int[] _order;
    private readonly Matrix4x4[] _inverseBind;
    private readonly Matrix4x4[] _world;
    private readonly Matrix4x4[] _skin;
    private readonly Vector3[] _restScale;
    private readonly Quaternion[] _restTurn;
    private readonly Vector3[] _restPlace;

    private SkeletonPose(AnimationSkeleton skeleton, int[] parent, int[] order)
    {
        _skeleton = skeleton;
        _parent = parent;
        _order = order;

        int count = skeleton.Bones.Count;
        _inverseBind = new Matrix4x4[count];
        _world = new Matrix4x4[count];
        _skin = new Matrix4x4[count];
        _restScale = new Vector3[count];
        _restTurn = new Quaternion[count];
        _restPlace = new Vector3[count];

        var bindModel = new Matrix4x4[count];
        foreach (int one in order)
        {
            Matrix4x4 bind = skeleton.Bones[one].Bind;
            bindModel[one] = parent[one] < 0 ? bind : bind * bindModel[parent[one]];

            // The rest pose, pulled apart once, so an unkeyed channel has something to fall back
            // on that is the bone's own and not an invented identity.
            if (!Matrix4x4.Decompose(bind, out Vector3 scale, out Quaternion turn, out Vector3 place))
            {
                (scale, turn, place) = (Vector3.One, Quaternion.Identity, bind.Translation);
            }

            _restScale[one] = scale;
            _restTurn[one] = turn;
            _restPlace[one] = place;

            _inverseBind[one] = Matrix4x4.Invert(bindModel[one], out Matrix4x4 undo)
                ? undo
                : Matrix4x4.Identity;
        }

        BindModel = bindModel;
        AtRest();
    }

    /// <summary>Every bone's rest pose in MODEL space, the tree already composed.</summary>
    public IReadOnlyList<Matrix4x4> BindModel { get; }

    /// <summary>Each bone's parent, or -1 for a root. Worked out from the sibling and child links.</summary>
    public IReadOnlyList<int> Parents => _parent;

    /// <summary>Where each bone has ended up, in model space, as of the last pose taken.</summary>
    public IReadOnlyList<Matrix4x4> World => _world;

    /// <summary>What to put a bind-pose vertex through, per bone, as of the last pose taken.</summary>
    public IReadOnlyList<Matrix4x4> Skin => _skin;

    /// <summary>
    /// Builds a pose for a skeleton, or null where its tree does not hold together.
    /// </summary>
    /// <remarks>
    /// THE TREE IS WALKED RATHER THAN TRUSTED. A bone lists its next SIBLING and its first CHILD,
    /// so a parent is implied and not stored - and a file whose links form a cycle, or leave a
    /// bone with two parents, would otherwise be composed into an infinite loop or a silently
    /// wrong pose. Both are refused here, where the reason can be said.
    /// </remarks>
    public static SkeletonPose? Of(AnimationSkeleton? skeleton)
    {
        if (skeleton is not { Ready: true })
        {
            return null;
        }

        int count = skeleton.Bones.Count;
        var parent = new int[count];
        Array.Fill(parent, -1);

        var order = new List<int>(count);
        var seen = new bool[count];
        var stack = new Stack<(int Bone, int Parent)>();

        // Every bone that nothing names as a child is a root. Most rigs have exactly one; a file
        // is not obliged to, and starting only from bone nought would quietly drop the rest.
        var named = new bool[count];
        foreach (SkeletonBone bone in skeleton.Bones)
        {
            if (bone.Child >= 0 && bone.Child < count)
            {
                named[bone.Child] = true;
            }

            if (bone.Sibling >= 0 && bone.Sibling < count)
            {
                named[bone.Sibling] = true;
            }
        }

        for (int one = count - 1; one >= 0; one--)
        {
            if (!named[one])
            {
                stack.Push((one, -1));
            }
        }

        while (stack.Count > 0)
        {
            (int bone, int over) = stack.Pop();
            if (bone < 0 || bone >= count || seen[bone])
            {
                return null;
            }

            seen[bone] = true;
            parent[bone] = over;
            order.Add(bone);

            SkeletonBone one = skeleton.Bones[bone];

            // A SIBLING SHARES THIS BONE'S PARENT and a child does not, which is the whole of the
            // format's hierarchy - and getting it wrong is remarkably quiet. A rig read with
            // siblings as children still has one root, still reaches about as far (190 units
            // against 174 on the rig measured) and is barely deeper (14 against 9). What gives it
            // away is SYMMETRY: the game names mirrored bones _l_ and _r_, and only a correctly
            // read tree hangs jnt_Leg_r_02 off jnt_Leg_r_01 the way it hangs the left one off the
            // left. See SkeletonPoseFromTheGameTests.TheRigComesOutSymmetric.
            if (one.Sibling >= 0)
            {
                stack.Push((one.Sibling, over));
            }

            if (one.Child >= 0)
            {
                stack.Push((one.Child, bone));
            }
        }

        return order.Count == count ? new SkeletonPose(skeleton, parent, [.. order]) : null;
    }

    /// <summary>
    /// The highest bone a mesh's vertices are weighted to, or -1 where none are.
    /// </summary>
    /// <remarks>
    /// THE CHECK THAT A VERTEX'S BONE BYTES INDEX THE SKELETON, made on the machine that has the
    /// files because no machine without them can. Nothing in the chain from .ao to .smd carries
    /// any bone list but the skeleton's - poe_data_tools' smd parser reads shape names, a box and
    /// counts from the header and four bone bytes with four weights from a vertex, and no palette
    /// anywhere - so the bytes can only index the rig. A mesh whose highest weighted bone is past
    /// the rig's count says otherwise, and the model then refuses to animate and says why.
    /// </remarks>
    public static int Highest(SkinnedMesh? mesh)
    {
        if (mesh is null)
        {
            return -1;
        }

        var most = -1;
        int count = Math.Min(mesh.Bones.Length, mesh.Weights.Length);
        for (var one = 0; one < count; one++)
        {
            if (mesh.Weights[one] > 0)
            {
                most = Math.Max(most, mesh.Bones[one]);
            }
        }

        return most;
    }

    /// <summary>
    /// Moves a mesh's vertices to where the last pose put their bones.
    /// </summary>
    /// <param name="mesh">The mesh, in its bind pose, with four bones and four weights a vertex.</param>
    /// <param name="positions">Where each vertex ends up. As long as the mesh has vertices.</param>
    /// <param name="normals">Which way each vertex faces afterwards. The same length.</param>
    /// <remarks>
    /// LINEAR BLEND SKINNING, WHICH IS WHAT FOUR WEIGHTS SUMMING TO 255 MEANS: each bone's skin
    /// matrix moves the vertex on its own, and the results are mixed in the weights' proportion.
    /// The weights are bytes out of 255 rather than floats, so a fully bound vertex is (255,0,0,0)
    /// and a joint's blend is something like (128,127,0,0).
    ///
    /// A NORMAL IS TURNED AND NOT MOVED - TransformNormal drops the translation - and is then made
    /// unit length again, because a blend of two unit vectors is shorter than one and the shading
    /// reads that as darker. A vertex with no weight at all is left where the file put it.
    ///
    /// A BONE PAST THE RIG IS SKIPPED, not thrown on: this runs on the draw thread thirty times a
    /// second over every vertex of the model, and <see cref="Highest"/> is where a mesh that does
    /// not fit its rig is meant to be caught, once, with a reason.
    /// </remarks>
    public void Move(SkinnedMesh? mesh, Span<Vector3> positions, Span<Vector3> normals)
    {
        if (mesh is not { Ready: true })
        {
            return;
        }

        int count = Math.Min(mesh.Positions.Length, Math.Min(positions.Length, normals.Length));
        int bones = _skin.Length;

        for (var one = 0; one < count; one++)
        {
            Vector3 at = mesh.Positions[one];
            Vector3 facing = one < mesh.Normals.Length ? mesh.Normals[one] : Vector3.UnitZ;

            var moved = Vector3.Zero;
            var turned = Vector3.Zero;
            var weight = 0f;

            int slot = one * 4;
            for (var part = 0; part < 4 && slot + part < mesh.Weights.Length; part++)
            {
                byte heavy = mesh.Weights[slot + part];
                int bone = mesh.Bones[slot + part];
                if (heavy == 0 || bone >= bones)
                {
                    continue;
                }

                float share = heavy / 255f;
                moved += Vector3.Transform(at, _skin[bone]) * share;
                turned += Vector3.TransformNormal(facing, _skin[bone]) * share;
                weight += share;
            }

            if (weight <= 0f)
            {
                positions[one] = at;
                normals[one] = facing;
                continue;
            }

            positions[one] = moved / weight;
            float length = turned.Length();
            normals[one] = length > 1e-6f ? turned / length : facing;
        }
    }

    /// <summary>
    /// Puts the skeleton back in its bind pose, where every skin matrix is the identity.
    /// </summary>
    public void AtRest()
    {
        for (var one = 0; one < _world.Length; one++)
        {
            _world[one] = BindModel[one];
            _skin[one] = Matrix4x4.Identity;
        }
    }

    /// <summary>
    /// Poses the skeleton at a moment of an animation.
    /// </summary>
    /// <param name="tracks">That animation's keyframes.</param>
    /// <param name="time">Where in it to look, in FRAMES - see <see cref="AnimationTracks"/>.</param>
    /// <remarks>
    /// A BONE THE ANIMATION DOES NOT MENTION KEEPS ITS REST POSE, which matters more than it
    /// sounds: the rigs measured give every bone a track, but a file is not obliged to, and a
    /// bone left at the identity instead of at its bind would collapse that limb onto its parent.
    /// </remarks>
    public void Take(AnimationTracks? tracks, float time)
    {
        if (tracks is not { Ready: true })
        {
            AtRest();
            return;
        }

        Span<bool> moved = _world.Length <= 256 ? stackalloc bool[_world.Length] : new bool[_world.Length];
        Span<Matrix4x4> local = new Matrix4x4[_world.Length];

        for (var one = 0; one < local.Length; one++)
        {
            local[one] = _skeleton.Bones[one].Bind;
        }

        foreach (BoneTrack track in tracks.Tracks)
        {
            if (track.Bone < 0 || track.Bone >= local.Length || moved[track.Bone])
            {
                continue;
            }

            moved[track.Bone] = true;
            Vector3 scale = tracks.Scale(track, time, _restScale[track.Bone]);
            Quaternion turn = tracks.Rotation(track, time, _restTurn[track.Bone]);
            Vector3 place = tracks.Position(track, time, _restPlace[track.Bone]);

            // Scale, then turn, then move - left to right, because these are row vectors.
            local[track.Bone] = Matrix4x4.CreateScale(scale)
                * Matrix4x4.CreateFromQuaternion(turn)
                * Matrix4x4.CreateTranslation(place);
        }

        foreach (int one in _order)
        {
            _world[one] = _parent[one] < 0 ? local[one] : local[one] * _world[_parent[one]];
            _skin[one] = _inverseBind[one] * _world[one];
        }
    }
}
