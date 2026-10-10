using PoEformance.Features;
using PoEformance.Game.Components;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// The one condition that asks about its neighbours: whether any monster the range conditions
/// beside it select can be targeted right now.
/// </summary>
/// <remarks>
/// Alive is not hittable. A boss between phases and a monster still spawning both stand in
/// every count with a full bar and read untargetable, and a skill cast at either is mana spent
/// on something the game refuses to hit. The byte is the game's own answer, and these tests
/// are about which monsters the condition asks it of - the ones its neighbours count, no more.
/// </remarks>
public class RuleTargetableTests
{
    private static readonly RuleTimers Timers = new();

    private static NearMonster Monster(
        double distance, bool targetable, ItemRarity rarity = ItemRarity.Normal, float x = 0, float y = 0, double? life = 50)
        => new(distance, rarity, x, y, life, 0, 1, 50, 100, targetable);

    private static RuleState Fighting(params NearMonster[] monsters) => new()
    {
        InGame = true,
        GameFocused = true,
        Alive = true,
        PlayerAt = (0f, 0f),
        CursorGround = (100f, 100f),
        Monsters = [.. monsters.OrderBy(m => m.Distance)],
    };

    private static RuleCondition Within(RuleFact fact, double radius)
        => RuleCondition.Of(fact, Compare.AtLeast, 1) with { Argument = radius };

    private static RuleCondition Targetable() => RuleCondition.Of(RuleFact.IsTargetable);

    [Fact]
    public void AsksTheMonstersItsNeighbourCounts()
    {
        // Beside "within 100 of the player": a targetable monster at 50 says yes, and the same
        // monster at 150 is nobody's business.
        RuleCondition rule = RuleCondition.All(Within(RuleFact.MonsterCountWithin, 100), Targetable());

        Assert.True(rule.Holds(Fighting(Monster(50, targetable: true)), Timers, "r"));
        Assert.False(rule.Holds(Fighting(Monster(150, targetable: true)), Timers, "r"));
        Assert.False(rule.Holds(Fighting(Monster(50, targetable: false)), Timers, "r"));
    }

    [Fact]
    public void AnUnreadableByteIsNotTargetable()
    {
        // The default, which is what RuleState.From hands a monster whose component did not
        // resolve: the rule presses keys on a yes, so a shrug is a no.
        RuleCondition rule = RuleCondition.All(Within(RuleFact.MonsterCountWithin, 100), Targetable());
        Assert.False(rule.Holds(Fighting(new NearMonster(50, ItemRarity.Normal)), Timers, "r"));
    }

    [Fact]
    public void MeasuresFromTheCursorWhenItsNeighbourDoes()
    {
        // Two monsters equally near the PLAYER; only the one where the cursor points counts,
        // and whether it is targetable is the whole answer.
        RuleCondition rule = RuleCondition.All(Within(RuleFact.MonsterCountAtCursor, 30), Targetable());

        Assert.True(rule.Holds(
            Fighting(Monster(10, targetable: true, x: 105, y: 100), Monster(10, targetable: false, x: -100, y: -100)),
            Timers, "r"));
        Assert.False(rule.Holds(
            Fighting(Monster(10, targetable: false, x: 105, y: 100), Monster(10, targetable: true, x: -100, y: -100)),
            Timers, "r"));
    }

    [Fact]
    public void ACursorRuleSaysNoWhileThePointerIsElsewhere()
    {
        RuleCondition rule = RuleCondition.All(Within(RuleFact.MonsterCountAtCursor, 1000), Targetable());
        RuleState away = Fighting(Monster(10, targetable: true, x: 5, y: 5)) with { CursorGround = null };

        Assert.False(rule.Holds(away, Timers, "r"));
    }

    [Fact]
    public void KeepsTheNeighboursRarityFilter()
    {
        // Beside a rares-and-uniques count a targetable white monster is not in the question;
        // the untargetable rare beside it is, and it says no.
        RuleCondition rule = RuleCondition.All(Within(RuleFact.RareOrUniqueCountWithin, 100), Targetable());

        Assert.False(rule.Holds(
            Fighting(Monster(10, targetable: true), Monster(20, targetable: false, ItemRarity.Rare)),
            Timers, "r"));
        Assert.True(rule.Holds(
            Fighting(Monster(10, targetable: false), Monster(20, targetable: true, ItemRarity.Unique)),
            Timers, "r"));
    }

    [Fact]
    public void KeepsTheNeighboursLifeFilter()
    {
        // LowestUniqueMonsterLifePercent is about uniques with a reading. A targetable unique
        // whose pool did not resolve is not among them.
        RuleCondition rule = RuleCondition.All(Within(RuleFact.LowestUniqueMonsterLifePercent, 100), Targetable());

        Assert.False(rule.Holds(Fighting(Monster(10, targetable: true, ItemRarity.Unique, life: null)), Timers, "r"));
        Assert.True(rule.Holds(Fighting(Monster(10, targetable: true, ItemRarity.Unique, life: 30)), Timers, "r"));
        Assert.False(rule.Holds(Fighting(Monster(10, targetable: true, ItemRarity.Rare, life: 30)), Timers, "r"));
    }

    [Fact]
    public void KeepsTheNeighboursLineOfSight()
    {
        // Beside MonsterCountInSight a targetable monster behind a wall is not in the question
        // - the wall takes the skill whether or not the game would let a click land.
        TerrainGrid grid = RuleSightTests.Grid(
            "..........",
            "....#.....",
            "....#.....",
            "....#.....",
            "..........");

        (float X, float Y) player = RuleSightTests.At(1, 2);
        (float X, float Y) behind = RuleSightTests.At(8, 2);
        (float X, float Y) clear = RuleSightTests.At(1, 0);
        RuleCondition rule = RuleCondition.All(Within(RuleFact.MonsterCountInSight, 10_000), Targetable());

        RuleState walled = Fighting(
            Monster(Distance(player, behind), targetable: true, x: behind.X, y: behind.Y),
            Monster(Distance(player, clear), targetable: false, x: clear.X, y: clear.Y))
            with { PlayerAt = player, Terrain = grid };
        Assert.False(rule.Holds(walled, Timers, "r"));

        RuleState seen = walled with
        {
            Monsters = [Monster(Distance(player, clear), targetable: true, x: clear.X, y: clear.Y)],
        };
        Assert.True(rule.Holds(seen, Timers, "r"));

        // No terrain yet is no sight, which is what the count beside it answers too.
        Assert.False(rule.Holds(seen with { Terrain = null }, Timers, "r"));
    }

    [Fact]
    public void AnyNeighboursRegionIsEnough()
    {
        // "Within 50 or at the cursor": a targetable monster in either region answers yes,
        // because the rule that wired them is about either.
        // The leaf asked with its neighbours, as the engine asks it - the AND around them
        // would also want both counts satisfied, which is not the question here.
        RuleCondition rule = RuleCondition.All(
            Within(RuleFact.MonsterCountWithin, 50),
            Within(RuleFact.MonsterCountAtCursor, 30),
            Targetable());
        RuleCondition leaf = rule.Children[2];

        Assert.True(leaf.Holds(Fighting(Monster(200, targetable: true, x: 105, y: 100)), Timers, "r", rule.Children));
        Assert.True(leaf.Holds(Fighting(Monster(20, targetable: true, x: -20, y: 0)), Timers, "r", rule.Children));
        Assert.False(leaf.Holds(Fighting(Monster(200, targetable: true, x: -200, y: 0)), Timers, "r", rule.Children));
    }

    [Fact]
    public void SeesIntoTheBoxesWiredIntoItsOwn()
    {
        // The editor's shape: AND(AND(MonsterCountAtCursor, InGame), OR(InMap, InSight)) with
        // IsTargetable on the outer AND. Its neighbours are two boxes, and the range conditions
        // inside them are what it asks.
        RuleCondition rule = RuleCondition.All(
            RuleCondition.All(Within(RuleFact.MonsterCountAtCursor, 30), RuleCondition.Of(RuleFact.InGame)),
            RuleCondition.Any(RuleCondition.Of(RuleFact.InGame), Within(RuleFact.MonsterCountWithin, 50)),
            Targetable());
        RuleCondition leaf = rule.Children[2];

        // In the OR's region only - the cursor box counts nothing there, and the whole rule
        // would say no for that reason, which is the AND doing its job and not the question.
        Assert.True(leaf.Holds(Fighting(Monster(20, targetable: true, x: -20, y: 0)), Timers, "r", rule.Children));
        Assert.False(leaf.Holds(Fighting(Monster(20, targetable: false, x: -20, y: 0)), Timers, "r", rule.Children));

        // And the whole rule, once both boxes are satisfied by a targetable monster at the
        // cursor: the rule fires, and stops firing when that monster goes untargetable.
        Assert.True(rule.Holds(Fighting(Monster(10, targetable: true, x: 105, y: 100)), Timers, "r"));
        Assert.False(rule.Holds(Fighting(Monster(10, targetable: false, x: 105, y: 100)), Timers, "r"));
    }

    [Fact]
    public void BesideNoRangeConditionItIsNeverTrue()
    {
        // Not "any monster anywhere": a targetable monster three screens away is not a reason
        // to press a key. The preview is where the difference from a room full of untargetable
        // monsters shows.
        RuleState state = Fighting(Monster(10, targetable: true));
        RuleCondition alone = RuleCondition.All(RuleCondition.Of(RuleFact.InGame), Targetable());

        Assert.False(alone.Holds(state, Timers, "r"));
        Assert.False(Targetable().Holds(state, Timers, "r"));

        PreviewFact fact = Assert.Single(RulePreview.Facts(alone, state), f => f.Label.Contains("IsTargetable", StringComparison.Ordinal));
        Assert.Contains("no range condition", fact.Label, StringComparison.Ordinal);
        Assert.False(fact.Known);
    }

    [Fact]
    public void ThePreviewAnswersItWithItsNeighbours()
    {
        RuleCondition rule = RuleCondition.All(Within(RuleFact.MonsterCountWithin, 100), Targetable());
        RuleState state = Fighting(Monster(50, targetable: true));

        PreviewFact fact = Assert.Single(RulePreview.Facts(rule, state), f => f.Label == "IsTargetable");
        Assert.True(fact.Holds);
        Assert.True(fact.Known);
    }

    [Fact]
    public void NegatesLikeAnyOtherFlag()
    {
        RuleCondition rule = RuleCondition.All(
            Within(RuleFact.MonsterCountWithin, 100),
            Targetable() with { Negate = true });

        Assert.False(rule.Holds(Fighting(Monster(50, targetable: true)), Timers, "r"));
        Assert.True(rule.Holds(Fighting(Monster(50, targetable: false)), Timers, "r"));
    }

    [Fact]
    public void ReadsFromTheTextForm()
    {
        ExpressionResult parsed = RuleExpression.Parse("MonsterCountAtCursor(30) >= 1 && IsTargetable");
        Assert.True(parsed.Ok, parsed.Error);

        Assert.True(parsed.Condition!.Holds(Fighting(Monster(10, targetable: true, x: 105, y: 100)), Timers, "r"));
        Assert.False(parsed.Condition!.Holds(Fighting(Monster(10, targetable: false, x: 105, y: 100)), Timers, "r"));
        Assert.Equal("MonsterCountAtCursor(30) >= 1 && IsTargetable", RuleExpression.Write(parsed.Condition!));
    }

    [Fact]
    public void TheStateCarriesTheByteOnlyAsAnExplicitYes()
    {
        // What the reader hands over: true, false, or null for a component nobody could read.
        // Only the first becomes a targetable monster on the fact sheet.
        WorldSnapshot snapshot = Snapshot(
            Entity(1, 10, targetable: true),
            Entity(2, 20, targetable: false),
            Entity(3, 30, targetable: null));

        RuleState state = RuleState.From(snapshot, focused: true, new RuleHistory(), 0);

        Assert.Equal(3, state.MonsterCount);
        Assert.Collection(
            state.Monsters,
            m => Assert.True(m.Targetable),
            m => Assert.False(m.Targetable),
            m => Assert.False(m.Targetable));
    }

    private static double Distance((float X, float Y) from, (float X, float Y) to)
        => Math.Sqrt(((to.X - from.X) * (to.X - from.X)) + ((to.Y - from.Y) * (to.Y - from.Y)));

    private static WorldEntity Entity(uint id, float x, bool? targetable)
        => new(id, id, "Metadata/Monsters/Test", EntityKind.Monster, x, 0, 0, Life: Pool(100), Targetable: targetable);

    private static Vital Pool(int current) => new(current, 100, 0, 0);

    private static WorldSnapshot Snapshot(params WorldEntity[] monsters)
    {
        var player = new WorldEntity(0, 0xF0, "Metadata/Characters/Test", EntityKind.Player, 0, 0, 0);
        return new WorldSnapshot(true, player, [player, .. monsters], new float[16]);
    }
}
