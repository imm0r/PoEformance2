using System.Numerics;
using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// Joining a monster's body to the pieces it hangs off itself.
/// </summary>
/// <remarks>
/// WHAT CAN GO SILENTLY WRONG HERE, which is why it is worth a test rather than a look: an index
/// that is not shifted by the vertices already written points at the body's triangles and draws a
/// piece made of somebody else's geometry, and a shape whose From is not shifted the same way
/// paints the wrong stretch of the mesh. Both produce a picture - a wrong one - and neither
/// throws.
/// </remarks>
public class MeshJoinTests
{
    /// <summary>A triangle, offset so the two under test are not the same numbers.</summary>
    private static SkinnedMesh Triangle(float at, string shape)
        => SkinnedMesh.Of(
            [new Vector3(at, 0f, 0f), new Vector3(at + 1f, 0f, 0f), new Vector3(at, 1f, 0f)],
            [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            [0, 1, 2],
            new Vector3(at, 0f, 0f),
            new Vector3(at + 1f, 1f, 0f),
            shapes: [new MeshShape(shape, 0, 3)]);

    [Fact]
    public void TheIndicesAndShapesOfEveryPieceAreShiftedPastWhatCameBefore()
    {
        SkinnedMesh body = Triangle(0f, "BodyShape");
        SkinnedMesh skirt = Triangle(10f, "SkirtShape");

        SkinnedMesh joined = SkinnedMesh.Joined([new MeshJoin(body), new MeshJoin(skirt)]);

        Assert.Equal(6, joined.Positions.Length);
        Assert.Equal([0, 1, 2, 3, 4, 5], joined.Indices);
        Assert.Equal(["BodyShape", "SkirtShape"], joined.Shapes.Select(one => one.Name));
        Assert.Equal([0, 3], joined.Shapes.Select(one => one.From));

        // Every index still addresses a vertex of the joined mesh, which is the property a
        // renderer relies on and an unshifted index would break without saying so.
        Assert.All(joined.Indices, one => Assert.InRange(one, 0, joined.Positions.Length - 1));
    }

    /// <summary>The box is the union, or the camera frames the body and cuts the skirt off.</summary>
    [Fact]
    public void TheBoxCoversEveryPiece()
    {
        SkinnedMesh joined = SkinnedMesh.Joined(
            [new MeshJoin(Triangle(0f, "a")), new MeshJoin(Triangle(10f, "b"))]);

        Assert.Equal(new Vector3(0f, 0f, 0f), joined.Least);
        Assert.Equal(new Vector3(11f, 1f, 0f), joined.Most);
    }

    /// <summary>One mesh with nothing to remap comes back untouched rather than copied.</summary>
    /// <remarks>
    /// THE ORDINARY MONSTER IS THE ONE WITH NO ATTACHMENTS, and it must not pay for this: the
    /// same instance back means no arrays were allocated and nothing downstream can tell the
    /// joining code was ever in the path.
    /// </remarks>
    [Fact]
    public void ABodyOnItsOwnIsNotCopied()
    {
        SkinnedMesh body = Triangle(0f, "BodyShape");

        Assert.Same(body, SkinnedMesh.Joined([new MeshJoin(body)]));
    }

    /// <summary>Pieces that did not read are left out rather than refused.</summary>
    /// <remarks>
    /// A MONSTER WITH ONE UNREADABLE ATTACHMENT IS STILL A MONSTER. Refusing the whole join over
    /// one missing file would take the body away too, which is the opposite of what a partial
    /// read should cost.
    /// </remarks>
    [Fact]
    public void APieceThatDidNotReadIsSkipped()
    {
        SkinnedMesh body = Triangle(0f, "BodyShape");

        SkinnedMesh joined = SkinnedMesh.Joined(
            [new MeshJoin(body), new MeshJoin(SkinnedMesh.None), new MeshJoin(null)]);

        Assert.Same(body, joined);
    }

    [Fact]
    public void NothingUsableGivesNothing()
        => Assert.Same(SkinnedMesh.None, SkinnedMesh.Joined([new MeshJoin(SkinnedMesh.None)]));
}
