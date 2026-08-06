using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Features;
using PoEformance.Game.Components;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>Building snapshots to watch, without a game.</summary>
internal static class Scene
{
    internal static WorldEntity Player(float x = 0f, float y = 0f)
        => new(1, 0x1000, "Metadata/Characters/Player", EntityKind.Player, x, y, 0f);

    internal static WorldEntity Monster(
        uint id, ItemRarity rarity = ItemRarity.Normal, float x = 0f, float y = 0f, string path = "")
        => new(
            id,
            0x2000 + id,
            path.Length > 0 ? path : "Metadata/Monsters/Skeleton/Skeleton01",
            EntityKind.Monster,
            x,
            y,
            0f,
            Rarity: rarity);

    internal static WorldEntity Drop(uint id, ItemRarity rarity, float x = 0f, float y = 0f)
        => new(id, 0x3000 + id, "Metadata/Items/Rings/Ring01", EntityKind.WorldItem, x, y, 0f, Rarity: rarity);

    internal static WorldEntity Place(uint id, PoiKind poi, string icon = "", float x = 0f, float y = 0f)
        => new(id, 0x4000 + id, "Metadata/Terrain/Thing", EntityKind.Terrain, x, y, 0f, Poi: poi, MapIcon: icon);

    internal static WorldSnapshot In(uint area, params WorldEntity[] entities)
        => new(true, Player(), entities, new float[16], Area: Hostile, AreaHash: area);

    internal static WorldSnapshot Town(uint area, params WorldEntity[] entities)
        => new(true, Player(), entities, new float[16], Area: InTown, AreaHash: area);

    private static AreaInfo Hostile => new("MapRiverhold", "Riverhold", 10, false, false);

    private static AreaInfo InTown => new("G1_town", "Clearfell Encampment", 1, true, false);
}

/// <summary>
/// Being told when something worth knowing about turned up - and, mostly, being left alone.
/// </summary>
/// <remarks>
/// The failure mode of an alert is SPAM, not a missed alert: a radar that interrupts
/// constantly gets turned off within a map, at which point it also misses everything. So most
/// of what is checked here is silence.
/// </remarks>
public class AlertWatcherTests
{
    private static AlertWatcher Watching(params AlertRule[] rules)
        => new() { Rules = rules.Length > 0 ? rules : [.. DefaultAlerts.Rules] };

    private static readonly AlertRule Uniques =
        new("Unique monster", Priority: 100, Kind: EntityKind.Monster, MinRarity: ItemRarity.Unique);

    [Fact]
    public void ItSaysWhenSomethingWatchedForTurnsUp()
    {
        AlertWatcher watcher = Watching(Uniques);

        Alert? alert = watcher.Look(Scene.In(1, Scene.Monster(7, ItemRarity.Unique)), 1000);

        Assert.NotNull(alert);
        Assert.Equal("Unique monster", alert.Value.Rule);
        Assert.Contains("Skeleton01", alert.Value.Line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(long.MaxValue / 2)]
    public void TheVERYFIRSTAlertFires(long at)
    {
        // Written after it did not. The quiet gap started from a "long ago" sentinel of
        // long.MinValue, and `now - long.MinValue` OVERFLOWS to a negative gap - which reads
        // as "too soon", so the first alert of a session was swallowed, forever, since the
        // sentinel was never replaced. Nothing about that is visible from the outside: the
        // feature simply never works, and the thing it failed to mention leaves no trace.
        Assert.NotNull(Watching(Uniques).Look(Scene.In(1, Scene.Monster(7, ItemRarity.Unique)), at));
    }

    [Fact]
    public void AndSaysItONCE()
    {
        // The one that decides whether this is usable. The same monster is in every snapshot
        // for as long as it stands there - announcing it per frame is sixty a second.
        AlertWatcher watcher = Watching(Uniques);
        WorldSnapshot snapshot = Scene.In(1, Scene.Monster(7, ItemRarity.Unique));

        Assert.NotNull(watcher.Look(snapshot, 1000));

        for (long at = 1_050; at < 60_000; at += 50)
        {
            Assert.Null(watcher.Look(snapshot, at));
        }
    }

    [Fact]
    public void SeveralAtOnceAreONEBanner()
    {
        // Walking into a room, or entering a map with things already in it. One line with a
        // count, not a queue of them.
        AlertWatcher watcher = Watching(Uniques);

        Alert? alert = watcher.Look(
            Scene.In(
                1,
                Scene.Monster(7, ItemRarity.Unique),
                Scene.Monster(8, ItemRarity.Unique),
                Scene.Monster(9, ItemRarity.Unique)),
            1000);

        Assert.NotNull(alert);
        Assert.Equal(2, alert.Value.Also);
        Assert.Contains("+2 more", alert.Value.Line, StringComparison.Ordinal);
        Assert.Equal(1, watcher.Raised);
    }

    [Fact]
    public void TheMOSTImportantOneIsTheOneShown()
    {
        AlertWatcher watcher = Watching(
            Uniques,
            new AlertRule("Way out", Priority: 30, Place: PoiKind.AreaTransition));

        Alert? alert = watcher.Look(
            Scene.In(1, Scene.Place(3, PoiKind.AreaTransition), Scene.Monster(7, ItemRarity.Unique)),
            1000);

        Assert.Equal("Unique monster", alert?.Rule);
    }

    [Fact]
    public void ABusyRoomIsNotAStrobe()
    {
        // Distinct things, so "once per entity" does not cover this. Without a gap between
        // banners a room full of them replaces the line faster than it can be read.
        AlertWatcher watcher = Watching(Uniques);

        Assert.NotNull(watcher.Look(Scene.In(1, Scene.Monster(1, ItemRarity.Unique)), 1000));
        Assert.Null(watcher.Look(Scene.In(1, Scene.Monster(2, ItemRarity.Unique)), 1100));
        Assert.Null(watcher.Look(Scene.In(1, Scene.Monster(3, ItemRarity.Unique)), 2000));
        Assert.NotNull(watcher.Look(Scene.In(1, Scene.Monster(4, ItemRarity.Unique)), 1000 + AlertWatcher.QuietMs));
    }

    [Fact]
    public void SomethingSwallowedByTheGapIsNotAnnouncedLATER()
    {
        // The subtle one. A thing matched while the gap was closed is still MARKED as
        // mentioned, so it cannot come back the moment the gap opens - otherwise the banner
        // announces something that has been standing there for twenty seconds.
        AlertWatcher watcher = Watching(Uniques);
        WorldSnapshot busy = Scene.In(1, Scene.Monster(1, ItemRarity.Unique), Scene.Monster(2, ItemRarity.Unique));

        Assert.NotNull(watcher.Look(busy, 1000));

        // Long past the gap, same scene, nothing new: silence.
        Assert.Null(watcher.Look(busy, 30_000));
    }

    [Fact]
    public void ARECYCLEDAddressIsStillANewThing()
    {
        // Why identity is the game's entity id and not the address. Addresses get reused
        // inside an area as things die and others spawn, so keying on one reads a brand-new
        // monster as "already told you about that" - and the alert is lost with nothing left
        // behind to notice. The reference tool keys on the address.
        AlertWatcher watcher = Watching(Uniques);

        var first = new WorldEntity(
            10, 0xDEAD, "Metadata/Monsters/A", EntityKind.Monster, 0, 0, 0, Rarity: ItemRarity.Unique);
        var second = first with { Id = 11, Path = "Metadata/Monsters/B" };

        Assert.NotNull(watcher.Look(Scene.In(1, first), 1000));
        Assert.Equal(first.Address, second.Address);
        Assert.NotNull(watcher.Look(Scene.In(1, second), 1000 + AlertWatcher.QuietMs));
    }

    [Fact]
    public void ANewInstanceStartsOver()
    {
        // The same monster in a fresh instance of the same map is a new monster.
        AlertWatcher watcher = Watching(Uniques);
        Assert.NotNull(watcher.Look(Scene.In(1, Scene.Monster(7, ItemRarity.Unique)), 1000));

        Assert.NotNull(watcher.Look(Scene.In(2, Scene.Monster(7, ItemRarity.Unique)), 1000 + AlertWatcher.QuietMs));
    }

    [Fact]
    public void NothingIsSaidInTown()
    {
        // A hideout full of banners is what makes somebody turn this off before they have
        // seen it do anything useful.
        AlertWatcher watcher = Watching(Uniques);

        Assert.Null(watcher.Look(Scene.Town(1, Scene.Monster(7, ItemRarity.Unique)), 1000));
    }

    [Fact]
    public void WalkingOutOfTownDoesNotCarryTownsSilence()
    {
        // The area reset runs BEFORE the town check, so entities seen in a hideout do not
        // count as already mentioned once you are somewhere they matter.
        AlertWatcher watcher = Watching(Uniques);
        Assert.Null(watcher.Look(Scene.Town(1, Scene.Monster(7, ItemRarity.Unique)), 1000));

        Assert.NotNull(watcher.Look(Scene.In(2, Scene.Monster(7, ItemRarity.Unique)), 2000));
    }

    [Fact]
    public void SwitchedOffMeansSilent()
    {
        AlertWatcher watcher = Watching(Uniques);
        watcher.Enabled = false;

        Assert.Null(watcher.Look(Scene.In(1, Scene.Monster(7, ItemRarity.Unique)), 1000));
    }

    [Fact]
    public void NotInAnAreaIsNotAnError()
        => Assert.Null(Watching(Uniques).Look(WorldSnapshot.Empty, 1000));

    [Fact]
    public void ForgettingLetsAnAreaAnnounceItselfAgain()
    {
        AlertWatcher watcher = Watching(Uniques);
        WorldSnapshot snapshot = Scene.In(1, Scene.Monster(7, ItemRarity.Unique));
        Assert.NotNull(watcher.Look(snapshot, 1000));

        watcher.Forget();

        Assert.NotNull(watcher.Look(snapshot, 1100));
    }

    [Fact]
    public void ItDoesNotGrowForeverInOneArea()
    {
        // A bound rather than a real limit - only matched entities land in it. It is here
        // because "a map never has that many" is a claim about the game, and an unbounded set
        // that grows for as long as somebody stands in an area is not worth betting a long
        // session on.
        AlertWatcher watcher = Watching(Uniques);

        for (uint id = 1; id <= AlertWatcher.MaxRemembered + 50; id++)
        {
            watcher.Look(Scene.In(1, Scene.Monster(id, ItemRarity.Unique)), id * 10_000);
        }

        // Nothing to assert but that it survived, which is the point: no runaway set.
        Assert.True(watcher.Raised > 0);
    }
}

/// <summary>What a rule matches, and - more importantly - what it does not.</summary>
public class AlertRuleTests
{
    private static readonly WorldEntity Rare = Scene.Monster(1, ItemRarity.Rare, x: 10f);

    [Fact]
    public void ARuleThatSaysNOTHINGMatchesNothing()
    {
        // The worst thing this feature can do is fire on every entity in the area, and an
        // empty rule is how that happens: from a hand-edited file, from an editor's "add"
        // button, from a field a future version introduces and nobody fills in.
        var empty = new AlertRule("whatever");

        Assert.True(empty.SaysNothing);
        Assert.False(empty.Matches(Rare, 5f));
    }

    [Fact]
    public void ADistanceIsNotACondition()
    {
        // "Anything within thirty units" fires on every scrap of scenery walked past. On its
        // own it counts as having said nothing.
        var near = new AlertRule("close things", WithinDistance: 30f);

        Assert.True(near.SaysNothing);
        Assert.False(near.Matches(Rare, 5f));
    }

    [Fact]
    public void RarityIsAFLOOR()
    {
        var rule = new AlertRule("good monsters", Kind: EntityKind.Monster, MinRarity: ItemRarity.Rare);

        Assert.True(rule.Matches(Scene.Monster(1, ItemRarity.Rare), 0f));
        Assert.True(rule.Matches(Scene.Monster(2, ItemRarity.Unique), 0f));
        Assert.False(rule.Matches(Scene.Monster(3, ItemRarity.Magic), 0f));
    }

    [Fact]
    public void CurrencyIsNotBetterThanUnique()
    {
        // It sits at 5 on the game's scale, above Unique, while being the plainest thing that
        // drops. Compared as a number, "unique and better" fires on every stack of gold.
        var uniques = new AlertRule("uniques", Kind: EntityKind.WorldItem, MinRarity: ItemRarity.Unique);
        var currency = new AlertRule("currency", Kind: EntityKind.WorldItem, MinRarity: ItemRarity.Currency);

        Assert.False(uniques.Matches(Scene.Drop(1, ItemRarity.Currency), 0f));
        Assert.True(currency.Matches(Scene.Drop(1, ItemRarity.Currency), 0f));
        Assert.False(currency.Matches(Scene.Drop(2, ItemRarity.Unique), 0f));
    }

    [Fact]
    public void ADropWhoseRarityHasNotResolvedIsNotGuessedAt()
    {
        // Unlike the DRAWING side, which shows it - missing a unique costs more than glancing
        // at a white one. A rule with a rarity in it is asking a question about rarity, and
        // "not yet known" is not an answer worth interrupting somebody with.
        var rule = new AlertRule("uniques", Kind: EntityKind.WorldItem, MinRarity: ItemRarity.Unique);

        Assert.False(rule.Matches(Scene.Drop(1, ItemRarity.Unknown), 0f));
    }

    [Fact]
    public void DistanceNarrowsARuleThatAlreadySaysSomething()
    {
        var rule = new AlertRule(
            "close rares", Kind: EntityKind.Monster, MinRarity: ItemRarity.Rare, WithinDistance: 50f);

        Assert.True(rule.Matches(Rare, 40f));
        Assert.False(rule.Matches(Rare, 60f));
    }

    [Fact]
    public void APathIsMatchedLoosely()
    {
        // What somebody types is "strongbox", and the path says
        // "Metadata/Chests/StrongBoxes/Arcanist".
        var rule = new AlertRule("boxes", PathContains: "strongbox");

        Assert.True(rule.Matches(Scene.Monster(1, path: "Metadata/Chests/StrongBoxes/Arcanist"), 0f));
        Assert.False(rule.Matches(Scene.Monster(2, path: "Metadata/Chests/Chest01"), 0f));
    }

    [Fact]
    public void ASwitchedOffRuleMatchesNothing()
        => Assert.False(
            new AlertRule("off", Enabled: false, Kind: EntityKind.Monster, MinRarity: ItemRarity.Rare)
                .Matches(Rare, 0f));

    [Fact]
    public void TheDefaultsDoNotFireOnOrdinaryThings()
    {
        // What makes the feature switchable-on-and-left-on. Ordinary monsters and ordinary
        // drops are almost everything in an area; a rule that catches them makes the banner a
        // permanent fixture, and a permanent banner is one nobody reads.
        foreach (AlertRule rule in DefaultAlerts.Rules.Where(r => r.Enabled))
        {
            Assert.False(rule.Matches(Scene.Monster(1, ItemRarity.Normal), 0f), $"{rule.Name} fires on an ordinary monster");
            Assert.False(rule.Matches(Scene.Drop(2, ItemRarity.Normal), 0f), $"{rule.Name} fires on an ordinary drop");
            Assert.False(rule.Matches(Scene.Drop(3, ItemRarity.Magic), 0f), $"{rule.Name} fires on a magic drop");
        }
    }

    [Fact]
    public void EveryDefaultActuallySaysSomething()
    {
        foreach (AlertRule rule in DefaultAlerts.Rules)
        {
            Assert.False(rule.SaysNothing, $"{rule.Name} has no conditions - it would match everything");
            Assert.False(string.IsNullOrWhiteSpace(rule.Name));
        }
    }
}

/// <summary>The alert settings have to survive a restart, and stay safe on the way in.</summary>
public class AlertStoreTests
{
    private static string TempPath()
        => Path.Combine(Path.GetTempPath(), $"poeformance-alerts-{Guid.NewGuid():N}.json");

    [Fact]
    public void RulesComeBack()
    {
        var wanted = new AlertSettings(
            Enabled: false,
            QuietInTown: false,
            Rules: [new AlertRule("Breaches", Priority: 90, Place: PoiKind.Mechanic, PathContains: "breach")]);

        string path = TempPath();
        try
        {
            Assert.True(AlertStore.Save(wanted, path));
            AlertSettings loaded = AlertStore.Load(path);

            Assert.False(loaded.Enabled);
            Assert.False(loaded.QuietInTown);
            Assert.Equal(wanted.Rules, loaded.Rules);

            // By NAME, so inserting a value into one of these enums later cannot silently
            // turn a saved rule into a different one.
            Assert.Contains("Mechanic", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ARuleThatSaysNothingIsDroppedOnTheWayIn()
    {
        // It would match every entity in the area. A hand-edited file is exactly where one
        // comes from - and so is a file written by a version with a field this one lacks.
        string path = TempPath();
        File.WriteAllText(path, """{"rules":[{"name":"oops","enabled":true,"priority":50}]}""");

        try
        {
            Assert.Empty(AlertStore.Load(path).Watching);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void NoFileMeansTheShippedRules()
        => Assert.Equal(
            DefaultAlerts.Rules,
            AlertStore.Load(Path.Combine(Path.GetTempPath(), "poeformance-alerts-absent.json")).Watching);

    [Fact]
    public void DeletingEVERYRuleIsRespected()
    {
        // An absent list means "never chosen" and takes the defaults; an empty one means
        // "watch for nothing". A list that grows its defaults back is a list nobody can empty.
        string path = TempPath();
        try
        {
            AlertStore.Save(AlertSettings.Default with { Rules = [] }, path);
            Assert.Empty(AlertStore.Load(path).Watching);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ABrokenFileDoesNotStopTheTool()
    {
        string path = TempPath();
        File.WriteAllText(path, "{ not json");

        try
        {
            Assert.Equal(DefaultAlerts.Rules, AlertStore.Load(path).Watching);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>
/// The defaults, against a real captured area.
/// </summary>
/// <remarks>
/// The question no synthetic scene can answer: with these rules switched on, how much does an
/// actual map say? A rule set that looks reasonable in isolation and fires on a third of a
/// real area is the whole failure mode, and this is the only place it shows.
/// </remarks>
public class AlertsAgainstARealAreaTests
{
    private static WorldSnapshot Scene()
    {
        var replay = ReplayMemoryReader.Load(File.OpenRead(RealSessionTests.SceneFixturePath));
        return new WorldReader(replay, RealSessionTests.Schema())
            .Read(replay.ResolvedStatics["GameStates"]);
    }

    [Fact]
    public void ARealAreaIsMOSTLYQuiet()
    {
        WorldSnapshot snapshot = Scene();
        Assert.True(snapshot.Entities.Count > 50, "the fixture should hold a full area");

        var watcher = new AlertWatcher();
        int fired = 0;
        for (long at = 0; at < 120_000; at += 100)
        {
            if (watcher.Look(snapshot, at) is not null)
            {
                fired++;
            }
        }

        // Two minutes of standing still in a real area. Whatever was there is announced once
        // and then it is over - a count that climbs with time is the feature announcing the
        // same things forever.
        Assert.True(fired <= 2, $"{fired} alerts from standing still - the defaults are too noisy");
    }

    [Fact]
    public void TheDefaultsDoNotMatchMostOfWhatIsThere()
    {
        // The blunt version of the same question. If a large share of a real area matches,
        // the rules are wrong however quiet the gap makes them look.
        WorldSnapshot snapshot = Scene();
        WorldEntity player = snapshot.Player!;

        int matched = snapshot.Entities.Count(entity =>
            DefaultAlerts.Rules.Any(rule => rule.Enabled && rule.Matches(entity, Distance(entity, player))));

        Assert.True(
            matched * 4 < snapshot.Entities.Count,
            $"{matched} of {snapshot.Entities.Count} entities match a default rule");
    }

    private static float Distance(WorldEntity entity, WorldEntity player)
    {
        float dx = entity.WorldX - player.WorldX;
        float dy = entity.WorldY - player.WorldY;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }
}
