using PoEformance.Features;
using PoEformance.Game.Components;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Counting only the monsters a skill could actually reach.
/// </summary>
/// <remarks>
/// The half an autocast rule was missing. "Three monsters within 1000" is true of a pack on
/// the other side of a wall, and a rule written on it spends the mana anyway - the skill goes
/// off, the wall takes it, and from outside that is indistinguishable from the rule working.
/// </remarks>
public class RuleSightTests
{
    /// <summary>A grid from rows of '.' (walkable) and '#' (solid), packed as the game packs it.</summary>
    private static TerrainGrid Grid(params string[] rows)
    {
        int width = rows[0].Length;
        int stride = (width + 1) / 2;
        var cells = new byte[stride * rows.Length];

        for (int y = 0; y < rows.Length; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (rows[y][x] == '.')
                {
                    cells[(y * stride) + (x / 2)] |= (byte)((x & 1) == 0 ? 0x01 : 0x10);
                }
            }
        }

        return new TerrainGrid(cells, stride, rows.Length);
    }

    /// <summary>The middle of grid cell (x, y), in world units.</summary>
    private static (float X, float Y) At(int cellX, int cellY)
        => ((cellX + 0.5f) * PoEformance.Game.Ui.MapView.WorldToGrid,
            (cellY + 0.5f) * PoEformance.Game.Ui.MapView.WorldToGrid);

    private static NearMonster Monster((float X, float Y) at, (float X, float Y) player)
    {
        float dx = at.X - player.X;
        float dy = at.Y - player.Y;
        return new NearMonster(
            MathF.Sqrt((dx * dx) + (dy * dy)), ItemRarity.Normal, at.X, at.Y, 100, 0, 1, 100, 100);
    }

    private static RuleState Around(TerrainGrid? grid, (float X, float Y) player, params (float X, float Y)[] monsters)
        => new()
        {
            InGame = true,
            GameFocused = true,
            Alive = true,
            Terrain = grid,
            PlayerAt = player,
            Monsters = [.. monsters.Select(m => Monster(m, player)).OrderBy(m => m.Distance)],
        };

    [Fact]
    public void AMonsterBehindAWallIsNotCounted()
    {
        // The whole point. Both monsters are the same distance away and only one of them is
        // somewhere a skill can land.
        TerrainGrid grid = Grid(
            "..........",
            "..........",
            "....#.....",
            "....#.....",
            "....#.....",
            "..........");

        (float X, float Y) player = At(1, 3);
        RuleState state = Around(grid, player, At(8, 3), At(1, 0));

        Assert.Equal(2, state.MonsterCountWithin(10_000));
        Assert.Equal(1, state.MonsterCountInSight(10_000));
    }

    [Fact]
    public void TheRadiusStillApplies()
    {
        // In sight AND in range: a monster across an open room is visible from anywhere, and
        // that is not a reason for a rule with a short radius to fire at it.
        TerrainGrid grid = Grid(
            "....................",
            "....................",
            "....................");

        (float X, float Y) player = At(0, 1);
        RuleState state = Around(grid, player, At(2, 1), At(18, 1));

        Assert.Equal(2, state.MonsterCountInSight(10_000));
        Assert.Equal(1, state.MonsterCountInSight(50));
    }

    [Fact]
    public void TerrainThatHasNotLoadedIsNoAnswerRatherThanNone()
    {
        // The one place this parts company with the other counts, and it is deliberate. They
        // answer 0 because an empty room is a real reading; here 0 would claim the view is
        // blocked to everything when the truth is that the map has not arrived - and on a
        // large map that state lasts a minute. A rule written the other way round,
        // "MonsterCountInSight < 1", would act on that guess for the whole first minute.
        (float X, float Y) player = At(1, 1);
        RuleState state = Around(null, player, At(2, 1));

        Assert.Null(state.MonsterCountInSight(10_000));

        // And a null makes every comparison say no, which is the doctrine the rest of the
        // sheet follows - so the rule stays quiet rather than pressing a key.
        var condition = new RuleCondition
        {
            Fact = RuleFact.MonsterCountInSight,
            Argument = 10_000,
            Compare = Compare.AtLeast,
            Value = 1,
        };

        Assert.False(condition.Holds(state, new RuleTimers(), "r"));

        // Including the comparison written the other way, which is the one that would have
        // fired on a confident zero.
        Assert.False((condition with { Compare = Compare.Below, Value = 1 })
            .Holds(state, new RuleTimers(), "r"));
    }

    [Fact]
    public void AnEmptyRoomIsAZeroOnceTheGroundIsKnown()
    {
        // Terrain present and nothing in it: a real reading, and a 0 rather than a null.
        TerrainGrid grid = Grid("....", "....");

        Assert.Equal(0, Around(grid, At(0, 0)).MonsterCountInSight(10_000));
    }

    [Fact]
    public void WithoutAPlayerThereIsNowhereToLookFrom()
    {
        TerrainGrid grid = Grid("....", "....");
        var state = new RuleState { InGame = true, Terrain = grid, Monsters = [] };

        Assert.Null(state.MonsterCountInSight(10_000));
    }

    [Fact]
    public void ItIsOfferedToTheEditorLikeEveryOtherFact()
    {
        // The catalogue is generated from the fact table, so a new fact reaches the config
        // page without a line of UI - but only if the table, the enum and the answer switch
        // agree, which is what this actually checks.
        FactInfo info = RuleFacts.All.Single(f => f.Fact == RuleFact.MonsterCountInSight);

        Assert.Equal(FactShape.Number, info.Shape);

        // A Distance argument is what makes the radius drawable on the ground, which is the
        // only practical way to settle what a number like 1000 means.
        Assert.Equal(FactArgument.Distance, info.Argument);
        Assert.False(info.AtCursor);
    }
}
