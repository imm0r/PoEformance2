using PoEformance.Features;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Recognising the projectiles in flight, and following them across snapshots.
/// </summary>
/// <remarks>
/// The drawing is ImGui and cannot be tested here. What can is everything the drawing rests
/// on, and the two halves fail in different ways. Classification fails LOUDLY - nothing is
/// marked, and the tab says so. The trail fails QUIETLY: a line drawn between two projectiles
/// that merely shared an id looks exactly like a projectile that turned a corner, and there is
/// nothing on screen to say otherwise. So most of what is here is about the second.
/// </remarks>
public class ProjectileTests
{
    private const uint Area = 0x51C0FFEE;

    private static WorldEntity Shot(
        uint id, float x, float y, bool mine = true, ulong render = 0, string path = "Metadata/Projectiles/Fireball")
        => new(
            Id: id,
            Address: 0x1000 + id,
            Path: path,
            Kind: EntityKind.Projectile,
            WorldX: x,
            WorldY: y,
            WorldZ: 12f,
            IsFriendly: mine,
            Render: render == 0 ? 0x2000 + id : render);

    private static WorldSnapshot Snapshot(params WorldEntity[] entities)
        => new(true, null, entities, new float[16], AreaHash: Area);

    [Theory]
    [InlineData("Metadata/Projectiles/Fireball")]
    [InlineData("Metadata/Projectiles/ShockingArrowFaridun")]
    public void WhatTheGameFilesUnderProjectilesIsOne(string path)
        => Assert.Equal(EntityKind.Projectile, WorldReader.ClassifyPath(path));

    [Fact]
    public void AMonstersOwnProjectileStaysAMonsterPath()
    {
        // Deliberate, and the comment on the rule says why. This is what a monster's skill
        // spawns - seen 36 times in one recorded map - and it is filed under the monster
        // rather than under Projectiles. Reclassifying it here would take it out of the
        // hostile-effect rule that currently drops it, and put a screen of arrows back into
        // every monster count and health bar that rule exists to protect.
        Assert.Equal(
            EntityKind.Monster,
            WorldReader.ClassifyPath("Metadata/Monsters/VaalHumanoids/VaalHumanoidBow/objects/LightningArrow"));
    }

    [Fact]
    public void AProjectileIsNeverRemembered()
    {
        // The one sighting that would be absurd: a fireball hanging in the air at the last
        // place the game listed it, minutes after it hit something.
        Assert.False(EntityMemory.WorthRemembering(Shot(1, 0f, 0f)));
    }

    [Fact]
    public void ATrailFollowsAProjectileThatMoves()
    {
        var watch = new ProjectileWatch();

        watch.Update(Snapshot(Shot(1, 0f, 0f)), 100);
        watch.Update(Snapshot(Shot(1, 10f, 0f)), 116);
        watch.Update(Snapshot(Shot(1, 20f, 0f)), 132);

        IReadOnlyList<TrailPoint> trail = watch.TrailOf(Shot(1, 20f, 0f));
        Assert.Equal(3, trail.Count);
        Assert.Equal(0f, trail[0].X);
        Assert.Equal(20f, trail[^1].X);
    }

    [Fact]
    public void TheSAMESnapshotDrawnAgainAddsNothing()
    {
        // The reason the rule is "where the position changed" rather than "once per call".
        // The overlay redraws the same snapshot on every VSync frame while the reader produces
        // new ones far more slowly, so a trail built per call would be a hundred copies of one
        // point - and would run its short history out before the projectile had moved once.
        var watch = new ProjectileWatch();
        WorldSnapshot frame = Snapshot(Shot(1, 5f, 5f));

        for (int i = 0; i < 50; i++)
        {
            watch.Update(frame, 100 + i);
        }

        Assert.Single(watch.TrailOf(Shot(1, 5f, 5f)));
    }

    [Fact]
    public void ATrailNeverGrowsPastItsLimit()
    {
        var watch = new ProjectileWatch();
        for (int i = 0; i < ProjectileWatch.MostPoints * 3; i++)
        {
            watch.Update(Snapshot(Shot(1, i, 0f)), 100 + i);
        }

        IReadOnlyList<TrailPoint> trail = watch.TrailOf(Shot(1, 0f, 0f));
        Assert.Equal(ProjectileWatch.MostPoints, trail.Count);

        // The OLDEST goes, not the newest - a trail that dropped its head would lag behind the
        // thing it belongs to.
        Assert.Equal((ProjectileWatch.MostPoints * 3) - 1, trail[^1].X);
    }

    [Fact]
    public void AReusedIdDoesNotInheritTheLastProjectilesTrail()
    {
        // Ids are handed out again as fast as arrows are fired. Without the Render guard the
        // new one is drawn with a line reaching back to wherever the old one died, which reads
        // as a projectile that came from somewhere it was never near.
        var watch = new ProjectileWatch();

        watch.Update(Snapshot(Shot(1, 0f, 0f, render: 0xAAAA)), 100);
        watch.Update(Snapshot(Shot(1, 50f, 0f, render: 0xAAAA)), 116);

        WorldEntity next = Shot(1, 200f, 200f, render: 0xBBBB);
        watch.Update(Snapshot(next), 132);

        TrailPoint only = Assert.Single(watch.TrailOf(next));
        Assert.Equal(200f, only.X);
    }

    [Fact]
    public void AProjectileTheGameStopsListingIsForgotten()
    {
        var watch = new ProjectileWatch { ForgetMs = 200 };

        watch.Update(Snapshot(Shot(1, 0f, 0f)), 1_000);
        Assert.Equal(1, watch.Tracking);

        watch.Update(Snapshot(), 1_100);
        Assert.Equal(1, watch.Tracking); // still within the grace period

        watch.Update(Snapshot(), 1_400);
        Assert.Equal(0, watch.Tracking);
        Assert.Empty(watch.TrailOf(Shot(1, 0f, 0f)));
    }

    [Fact]
    public void AnAreaChangeThrowsEverythingAway()
    {
        // Every address and every id is handed out again in the next area, so anything held
        // could be matched against something entirely unrelated.
        var watch = new ProjectileWatch();
        watch.Update(Snapshot(Shot(1, 0f, 0f)), 100);

        var elsewhere = new WorldSnapshot(true, null, [], new float[16], AreaHash: Area + 1);
        watch.Update(elsewhere, 116);

        Assert.Equal(0, watch.Tracking);
    }

    [Fact]
    public void ARememberedSightingIsNotFollowed()
    {
        // Nothing remembers a projectile, so this is a guard rather than a filter - but a
        // remembered entity's position has not been read since it was last listed, and
        // extending a trail with one would draw movement that never happened.
        var watch = new ProjectileWatch();
        watch.Update(Snapshot(Shot(1, 0f, 0f) with { RememberedForMs = 800 }), 100);

        Assert.Equal(0, watch.Tracking);
    }

    [Fact]
    public void TheListSaysWhatIsInTheAirAndHowMuchOfItIsYours()
    {
        // The half the "only mine" switch depends on: whether the game marks a build's own
        // projectiles as being on its side is a question this list answers by looking.
        WorldSnapshot snapshot = Snapshot(
            Shot(1, 0f, 0f),
            Shot(2, 1f, 0f),
            Shot(3, 2f, 0f, mine: false, path: "Metadata/Projectiles/ShockingArrowFaridun"),
            new WorldEntity(4, 0x99, "Metadata/Monsters/Skeleton", EntityKind.Monster, 0f, 0f, 0f));

        IReadOnlyList<ProjectileSort> sorts = ProjectileWatch.Sorts(snapshot);

        Assert.Equal(2, sorts.Count);
        Assert.Equal("Metadata/Projectiles/Fireball", sorts[0].Path);
        Assert.Equal(2, sorts[0].Count);
        Assert.Equal(2, sorts[0].Mine);
        Assert.Equal(1, sorts[1].Count);
        Assert.Equal(0, sorts[1].Mine);
    }
}
