using PoEformance.Features;
using PoEformance.Game.Components;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// The evasion decision: what gets a marker, what gets a keystroke, and what stops both.
/// </summary>
/// <remarks>
/// THE HALF WORTH TESTING IS THE DECISION, which is why it is pure. This is the second feature
/// in the tool that can press a key, and every gate that stands between a monster twitching and
/// a keystroke leaving the process is checked here rather than by playing: the two rarity
/// floors, the type filters, the focus check, the cooldown, and the unset key.
///
/// THE FOCUS GATE IS A SAFETY PROPERTY, not a feature, and it has its own test for that reason.
/// Keystrokes land wherever focus is, so acting while the player has alt-tabbed types a dodge
/// key into a browser or a chat window.
/// </remarks>
public class EvasionPlannerTests
{
    private const ushort SomeKey = 0x20; // space

    /// <summary>A snapshot with a player at the origin and the given monsters.</summary>
    private static WorldSnapshot World(params WorldEntity[] monsters)
    {
        var player = new WorldEntity(1, 0x1000, "Metadata/Characters/Int", EntityKind.Player, 0, 0, 0);
        return new WorldSnapshot(true, player, [player, .. monsters], new float[16]);
    }

    /// <summary>A hostile monster doing something, aimed at a point.</summary>
    private static WorldEntity Monster(
        float x, float y, float targetX, float targetY,
        ItemRarity rarity = ItemRarity.Normal,
        string path = "Metadata/Monsters/Goatman/GoatmanLeaper",
        int animation = 195,
        ActionKind kind = ActionKind.Skill,
        uint id = 7)
        => new(
            id, 0x2000 + id, path, EntityKind.Monster, x, y, 0,
            Rarity: rarity,
            Name: "Goatman",
            Action: new ActorAction(kind, kind == ActionKind.Move ? 4224 : 2, targetX, targetY, x, y, 0, animation));

    private static EvasionSettings Settings(
        bool warn = true, bool act = true,
        ItemRarity warnFrom = ItemRarity.Normal, ItemRarity actFrom = ItemRarity.Normal,
        float radius = 90f, int cooldown = 0, int key = SomeKey,
        bool onlyDangerous = false)
        => new(
            Warn: new EvasionGate(warn, warnFrom),
            Act: new EvasionGate(act, actFrom),
            DangerRadius: radius,
            CooldownMs: cooldown,
            DodgeKey: key,
            OnlyDangerousAnimations: onlyDangerous);

    private static EvasionTick Run(
        EvasionSettings settings, WorldSnapshot world, bool focused = true, long now = 1000,
        AnimationNames? animations = null)
        => new EvasionPlanner(settings).Evaluate(world, animations ?? AnimationNames.Empty, focused, now);

    [Fact]
    public void AnActionLandingOnThePlayerIsAThreatAndIsDodged()
    {
        EvasionTick tick = Run(Settings(), World(Monster(500, 0, targetX: 10, targetY: 10)));

        Threat threat = Assert.Single(tick.Draw);
        Assert.True(threat.Aimed);
        Assert.Equal(10, threat.TargetX, 1);
        Assert.True(tick.Dodge);
        Assert.Contains("aimed at you", tick.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnActionLandingElsewhereIsDrawnButNotDodged()
    {
        // The distinction the whole feature turns on: something IS winding up, and it is not
        // going to land on you. A marker is right; a keystroke is not.
        EvasionTick tick = Run(Settings(), World(Monster(500, 0, targetX: 800, targetY: 800)));

        Threat threat = Assert.Single(tick.Draw);
        Assert.False(threat.Aimed);
        Assert.False(tick.Dodge);
        Assert.Equal(0, tick.AimedCount);
    }

    [Fact]
    public void TheTwoGatesHaveSeparateRarityFloors()
    {
        // The case the user asked for: see everything, act only on rares and above.
        EvasionSettings settings = Settings(warnFrom: ItemRarity.Normal, actFrom: ItemRarity.Rare);

        EvasionTick white = Run(settings, World(Monster(100, 0, 10, 10, ItemRarity.Normal)));
        Assert.Single(white.Draw);
        Assert.True(white.Draw[0].Aimed, "the white monster IS aimed at the player");
        Assert.False(white.Dodge);

        EvasionTick rare = Run(settings, World(Monster(100, 0, 10, 10, ItemRarity.Rare)));
        Assert.Single(rare.Draw);
        Assert.True(rare.Dodge);
    }

    [Fact]
    public void AWarnFloorHidesMonstersBelowIt()
    {
        EvasionSettings settings = Settings(warnFrom: ItemRarity.Unique, actFrom: ItemRarity.Unique);
        Assert.Empty(Run(settings, World(Monster(100, 0, 10, 10, ItemRarity.Rare))).Draw);
        Assert.Single(Run(settings, World(Monster(100, 0, 10, 10, ItemRarity.Unique))).Draw);
    }

    [Fact]
    public void AnUnknownRarityIsShownButNotActedOn()
    {
        // The safe way round for each half: a monster whose rarity would not read is still
        // worth a marker, and is not worth a keystroke.
        EvasionSettings settings = Settings(warnFrom: ItemRarity.Normal, actFrom: ItemRarity.Normal);
        EvasionTick tick = Run(settings, World(Monster(100, 0, 10, 10, ItemRarity.Unknown)));

        Assert.Single(tick.Draw);
        Assert.True(tick.Dodge, "an unknown rarity passes a Normal floor");

        EvasionSettings stricter = Settings(warnFrom: ItemRarity.Normal, actFrom: ItemRarity.Magic);
        EvasionTick refused = Run(stricter, World(Monster(100, 0, 10, 10, ItemRarity.Unknown)));
        Assert.Single(refused.Draw);
        Assert.False(refused.Dodge);
    }

    [Fact]
    public void PathFiltersSelectMonsterTypes()
    {
        WorldSnapshot world = World(
            Monster(100, 0, 10, 10, path: "Metadata/Monsters/Goatman/GoatmanLeaper", id: 1),
            Monster(120, 0, 10, 10, path: "Metadata/Monsters/Skeleton/SkeletonArcher", id: 2));

        EvasionSettings only = Settings() with { Warn = new EvasionGate(true, ItemRarity.Normal, OnlyPaths: ["Goatman"]) };
        Threat kept = Assert.Single(Run(only, world).Draw);
        Assert.Contains("Goatman", kept.Path, StringComparison.Ordinal);

        EvasionSettings ignore = Settings() with
        {
            Warn = new EvasionGate(true, ItemRarity.Normal, IgnorePaths: ["Goatman"]),
        };
        Threat left = Assert.Single(Run(ignore, world).Draw);
        Assert.Contains("Skeleton", left.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void IgnoreBeatsOnlyWhenBothMatch()
    {
        // Stated as a rule rather than left to ordering: an exclusion a person wrote down is
        // the more specific intent, and a monster matching both must not slip through.
        WorldSnapshot world = World(Monster(100, 0, 10, 10, path: "Metadata/Monsters/Goatman/GoatmanLeaper"));
        EvasionSettings settings = Settings() with
        {
            Warn = new EvasionGate(true, ItemRarity.Normal, OnlyPaths: ["Goatman"], IgnorePaths: ["Leaper"]),
        };

        Assert.Empty(Run(settings, world).Draw);
    }

    [Fact]
    public void NothingIsPressedWhileTheGameIsNotFocused()
    {
        // The safety property. Keystrokes land wherever focus is.
        EvasionTick tick = Run(Settings(), World(Monster(100, 0, 10, 10)), focused: false);

        Assert.Single(tick.Draw);
        Assert.False(tick.Dodge);
        Assert.Equal("game not focused", tick.Reason);
    }

    [Fact]
    public void NothingIsPressedWithoutADodgeKey()
    {
        // The one misconfiguration that otherwise looks exactly like a working tool: armed,
        // seeing threats, and never doing anything.
        EvasionTick tick = Run(Settings(key: 0), World(Monster(100, 0, 10, 10)));

        Assert.False(tick.Dodge);
        Assert.Contains("no dodge key", tick.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCooldownStopsOneAttackSpendingEveryRoll()
    {
        var planner = new EvasionPlanner(Settings(cooldown: 1000));
        WorldSnapshot world = World(Monster(100, 0, 10, 10));

        Assert.True(planner.Evaluate(world, AnimationNames.Empty, true, 10_000).Dodge);

        // The same threat is still committed on the next read - a wind-up lasts longer than a
        // tick - so without the cooldown this would roll again immediately.
        EvasionTick second = planner.Evaluate(world, AnimationNames.Empty, true, 10_500);
        Assert.False(second.Dodge);
        Assert.Contains("cooling down", second.Reason, StringComparison.Ordinal);

        Assert.True(planner.Evaluate(world, AnimationNames.Empty, true, 11_200).Dodge);
    }

    [Fact]
    public void ConfiguringDoesNotHandOutAFreePress()
    {
        // Editing a setting must not reset the cooldown, on the same argument the flask engine
        // makes: otherwise every keystroke typed into a field is a free roll.
        var planner = new EvasionPlanner(Settings(cooldown: 5000));
        WorldSnapshot world = World(Monster(100, 0, 10, 10));

        Assert.True(planner.Evaluate(world, AnimationNames.Empty, true, 1_000).Dodge);
        planner.Configure(Settings(cooldown: 5000, radius: 120f));
        Assert.False(planner.Evaluate(world, AnimationNames.Empty, true, 1_100).Dodge);
    }

    [Fact]
    public void BothGatesOffMeansNoWorkAndNoMarkers()
    {
        EvasionTick tick = Run(Settings(warn: false, act: false), World(Monster(100, 0, 10, 10)));
        Assert.Empty(tick.Draw);
        Assert.False(tick.Dodge);
        Assert.Equal("disabled", tick.Reason);
    }

    [Fact]
    public void AMonsterWithNoActionReadIsNotAQuietMonster()
    {
        // Action null means the reader was never asked - a different answer from "doing
        // nothing", and one that must not be drawn as a threat either way.
        var idle = new WorldEntity(9, 0x2009, "Metadata/Monsters/X", EntityKind.Monster, 100, 0, 0);
        Assert.Empty(Run(Settings(), World(idle)).Draw);
    }

    [Fact]
    public void FriendlyMinionsAreNeverThreats()
    {
        WorldEntity minion = Monster(50, 0, 10, 10) with { IsFriendly = true };
        Assert.Empty(Run(Settings(), World(minion)).Draw);
    }

    [Fact]
    public void TheQuietAnimationFilterIsOptionalAndAsksTheSafeWayRound()
    {
        // With the filter on, a walking monster is not a threat - but an animation the table
        // has no name for still is, because "unrecognised" must not read as "harmless".
        AnimationNames names = AnimationNames.Empty;
        EvasionSettings filtered = Settings(onlyDangerous: true);

        // AnimationNames.Empty knows no ids, so every id is Unknown - which IsQuiet says no to.
        Assert.Single(Run(filtered, World(Monster(100, 0, 10, 10, animation: 195)), animations: names).Draw);

        // And with a table that DOES know 195 as a run, the same monster drops out.
        AnimationNames loaded = LoadedAnimations();
        Assert.Empty(Run(filtered, World(Monster(100, 0, 10, 10, animation: 195)), animations: loaded).Draw);

        // While a slam stays in.
        Assert.Single(Run(filtered, World(Monster(100, 0, 10, 10, animation: SlamId(loaded))), animations: loaded).Draw);
    }

    [Fact]
    public void ThreatsComeBackNearestFirst()
    {
        WorldSnapshot world = World(
            Monster(100, 0, 400, 400, id: 1),
            Monster(120, 0, 20, 20, id: 2),
            Monster(140, 0, 200, 200, id: 3));

        IReadOnlyList<Threat> draw = Run(Settings(), world).Draw;
        Assert.Equal(3, draw.Count);
        Assert.True(draw[0].DistanceToPlayer < draw[1].DistanceToPlayer);
        Assert.True(draw[1].DistanceToPlayer < draw[2].DistanceToPlayer);
    }

    [Fact]
    public void OutOfGameNothingHappens()
    {
        var nothing = new WorldSnapshot(false, null, [], new float[16]);
        EvasionTick tick = Run(Settings(), nothing);
        Assert.False(tick.Dodge);
        Assert.Equal("not in game", tick.Reason);
    }

    /// <summary>The shipped animation table, so the filter test uses real names.</summary>
    private static AnimationNames LoadedAnimations()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "data", "animations.tsv")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return AnimationNames.Load(Path.Combine(dir.FullName, "data", "animations.tsv"));
    }

    /// <summary>An id the table calls a slam, so the test names no magic number.</summary>
    private static int SlamId(AnimationNames names)
    {
        for (int id = 0; id < 4096; id++)
        {
            if (names.KindOf(id) == AnimationKind.Slam)
            {
                return id;
            }
        }

        Assert.Fail("the animation table has no slam in it");
        return 0;
    }
}
