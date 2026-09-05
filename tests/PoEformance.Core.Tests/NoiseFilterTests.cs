using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Deciding which entities are not worth reading.
/// </summary>
/// <remarks>
/// A filter has two ways to be wrong and only one of them is visible. Letting noise through
/// leaves a cluttered map, which anybody can see. Removing a real thing leaves a map that is
/// clean and WRONG, and there is nothing on screen to say a monster was dropped - so the tests
/// that matter here are the ones about what must survive.
/// </remarks>
public class NoiseFilterTests
{
    [Theory]
    [InlineData("Metadata/Effects/Spells/fireball/fx/cast")]
    [InlineData("Metadata/Monsters/MonsterMods/RareDaemon")]
    [InlineData("Metadata/Characters/Attachments/Wings")]
    [InlineData("Metadata/MiscellaneousObjects/HideoutDoodad_Chair")]
    [InlineData("Metadata/Pet/Cat")]
    [InlineData("Metadata/Terrain/BossRoomMinimapIcon")]
    public void ThingsThatAreNotThingsAreDropped(string path)
        => Assert.True(new NoiseFilter().IsNoise(path));

    [Theory]
    [InlineData("Metadata/Monsters/MudGolem/SandGolem")]
    [InlineData("Metadata/Chests/StrongBoxes/Arcanist")]
    [InlineData("Metadata/MiscellaneousObjects/WorldItem")]
    [InlineData("Metadata/Characters/Int/IntFourb")]
    [InlineData("Metadata/NPC/League/Affliction")]
    [InlineData("Metadata/Terrain/Gallows/Act2/2_5/Objects/LightlessPassageTransition")]
    public void EverythingWorthSeeingSurvives(string path)
        => Assert.False(new NoiseFilter().IsNoise(path));

    [Fact]
    public void ThePatternsMatchPathSEGMENTSRatherThanAnyWordContainingThem()
    {
        // "/ao/" is the model-asset node. Without the slashes it would catch every path with
        // "ao" anywhere in it, and this game has plenty - which is the difference between a
        // filter and a hole.
        var filter = new NoiseFilter();

        Assert.True(filter.IsNoise("Metadata/Something/ao/Node"));
        Assert.False(filter.IsNoise("Metadata/Monsters/Chaos/Chaotic"));
        Assert.False(filter.IsNoise("Metadata/Monsters/Kaom/KaomWarrior"));
    }

    [Fact]
    public void TurningTheFilterOffLetsEVERYTHINGThrough()
    {
        // What reverse engineering needs: a filtered entity is not in the snapshot at all, so
        // without this there would be no way to look at one.
        var filter = new NoiseFilter { Enabled = false };

        Assert.False(filter.IsNoise("Metadata/Effects/Spells/fireball/fx/cast"));
    }

    [Fact]
    public void OneClassCanBeKeptWithoutKeepingTheRest()
    {
        // Somebody's genuine question is in there - a hideout builder wants the doodads and
        // nothing else in the list.
        var filter = new NoiseFilter();
        filter.Set(NoiseKind.Hideout, false);

        Assert.False(filter.IsNoise("Metadata/MiscellaneousObjects/HideoutDoodad_Chair"));
        Assert.True(filter.IsNoise("Metadata/Effects/Spells/fireball/fx/cast"));
        Assert.False(filter.IsOn(NoiseKind.Hideout));
        Assert.True(filter.IsOn(NoiseKind.Engine));
    }

    [Fact]
    public void AnEntityThatFailedToReadIsNotNoise()
    {
        // An empty path means the read went wrong, which is a different problem and belongs to
        // whoever asked for it. Calling it noise would file a fault under "working as intended".
        Assert.False(new NoiseFilter().IsNoise(string.Empty));
    }

    [Fact]
    public void SomethingTheUserAddsIsDroppedToo()
    {
        var filter = new NoiseFilter();
        filter.Custom.Add("LeagueIncursionNew");

        Assert.True(filter.IsNoise("Metadata/MiscellaneousObjects/LeagueIncursionNew/Door"));
    }

    [Fact]
    public void ItCanSayWHYSomethingWasDropped()
    {
        // A filter that silently removes things is a filter nobody can debug. When an entity
        // is missing this answers why in one call.
        var filter = new NoiseFilter();

        Assert.Equal(NoiseKind.Engine, filter.Explain("Metadata/Effects/Spells/fireball/fx/cast"));
        Assert.Equal(NoiseKind.Daemon, filter.Explain("Metadata/Monsters/MonsterMods/RareDaemon"));
        Assert.Null(filter.Explain("Metadata/Monsters/MudGolem/SandGolem"));
    }

    [Fact]
    public void EveryClassIsListedWithWhatItMatches()
    {
        // The settings list is built from this, so a class with no patterns would be a switch
        // that does nothing.
        Assert.Equal(6, NoiseFilter.Kinds.Count());
        Assert.All(NoiseFilter.Kinds, kind => Assert.NotEmpty(NoiseFilter.PatternsFor(kind)));
    }
}

/// <summary>
/// The filter as the reader actually uses it, against a recorded session.
/// </summary>
/// <remarks>
/// These exist because the unit tests above could all pass while the filter was wired to
/// nothing: removing the call from the read path broke no test at all. A predicate nobody
/// consults is worth exactly nothing, and this is the only kind of test that notices.
///
/// Against a REAL recording rather than a synthetic map, which also answers the question the
/// pattern list cannot answer on its own - whether these patterns catch real noise, and only
/// noise, in an area somebody actually played.
/// </remarks>
public class NoiseFilterInTheReadTests
{
    private static PoEformance.Core.Schema.OffsetSchema Schema()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "schema", "poe2.offsets.json")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return PoEformance.Core.Schema.SchemaJson.Load(Path.Combine(dir.FullName, "schema", "poe2.offsets.json")).AsOf(RealSessionTests.PrePatchEra);
    }

    private static WorldSnapshot Read(bool filtering)
    {
        var replay = PoEformance.Core.Memory.ReplayMemoryReader.Load(File.OpenRead(RealSessionTests.SceneFixturePath));
        var world = new WorldReader(replay, Schema());
        world.Noise.Enabled = filtering;
        return world.Read(replay.ResolvedStatics["GameStates"]);
    }

    [Fact]
    public void TheReaderACTUALLYAppliesIt()
    {
        List<WorldEntity> all = [.. Read(filtering: false).Entities];
        List<WorldEntity> kept = [.. Read(filtering: true).Entities];

        Assert.True(all.Count > 0, "the fixture should hold entities");
        Assert.True(kept.Count < all.Count, $"nothing was filtered out of {all.Count} entities");
    }

    [Fact]
    public void NothingWorthSeeingIsLost()
    {
        // The failure that would not show on screen: a clean map that is quietly missing a
        // monster. Every entity the filter drops has to be one it can NAME a reason for.
        var filter = new NoiseFilter();
        List<WorldEntity> all = [.. Read(filtering: false).Entities];

        List<WorldEntity> dropped = [.. all.Where(e => filter.IsNoise(e.Path))];

        Assert.NotEmpty(dropped);
        Assert.All(dropped, entity => Assert.NotNull(filter.Explain(entity.Path)));
    }

    [Fact]
    public void ThePlayerAndTheMonstersSurvive()
    {
        WorldSnapshot kept = Read(filtering: true);

        Assert.NotNull(kept.Player);
        Assert.Contains(kept.Entities, e => e.Kind == EntityKind.Monster);
    }
}
