using PoEformance.Features;
using PoEformance.Game.Components;

namespace PoEformance.Core.Tests;

/// <summary>
/// Asking the game when the roll has started, instead of guessing how long that takes.
/// </summary>
/// <remarks>
/// THE PROBLEM THIS SOLVES. Steering a dodge roll means holding a movement key across the frame
/// in which the game resolves the roll's direction, and until now nothing told the tool when that
/// frame had been and gone - so <c>EvasionSettings.SteerHoldMs</c> held for a guessed length of
/// time. One frame is 16.7 ms at 60 fps and 62 ms at 16, so any single number is either too long
/// on a fast machine or too short on a slow one, and too short fails SILENTLY: the roll goes
/// where the player was already pointing.
///
/// WHY NOT READ THE FRAME RATE, which is the natural way to size that number and the one the
/// owner proposed. It was looked for first: GameHelper2's FPS is its own overlay's
/// (<c>ImGui.GetIO().Framerate</c>), the AHK tool's is its own profiler's, and neither reads a
/// frame rate out of the game - so there is no reference to check a hunt against. But the deeper
/// reason is that the frame rate is only ever a PROXY for "has the game seen the keys yet", and
/// the game answers that question itself: the roll starting IS the input having been used. An
/// average frame rate is also wrong exactly when it matters - through the stutter, the load
/// spike, the one frame that took 200 ms.
///
/// WHAT MAKES IT SAFE TO PUT IN FRONT OF SOMETHING THAT WORKS is that every way it can fail lands
/// on the old behaviour. No table, no roll ids, an unreadable animation, a roll chained out of
/// another roll - in all of them <see cref="RollWatch.Started"/> never says yes and the caller
/// holds for the full ceiling, which is what it did before any of this existed.
/// </remarks>
public class RollWatchTests
{
    /// <summary>The animation table as it ships, found the way the app finds it.</summary>
    private static AnimationNames Table()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "data", "animations.tsv")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return AnimationNames.Load(Path.Combine(dir.FullName, "data", "animations.tsv"));
    }

    /// <summary>
    /// The two rolls the AHK tool observed live are in the set the game's own names produce.
    /// </summary>
    /// <remarks>
    /// 268 DodgeRoll and 402 DodgeRollBack are two of the eight ids that tool recorded while
    /// playing (see <see cref="AnimationNames"/>), so they are the pair with an outside reading
    /// behind them rather than only a row in a file. If a query on the game's names cannot find
    /// the animation a player demonstrably rolls with, nothing downstream of it can work.
    /// </remarks>
    [Fact]
    public void TheRollsSeenInGameAreFoundByName()
    {
        IReadOnlySet<int> rolls = Table().IdsNamed(RollWatch.Word);

        Assert.Contains(268, rolls);
        Assert.Contains(402, rolls);
    }

    /// <summary>
    /// "dodgeroll" and not "roll", because RollingMagma is a spell.
    /// </summary>
    /// <remarks>
    /// THE WHOLE POINT OF THE LONGER WORD, and it is one id away from being wrong: 403 is
    /// RollingMagma, which sits directly beside 402 DodgeRollBack. Matching "roll" would let a
    /// monster - or the player - casting Rolling Magma read as a dodge roll having started, and
    /// the steering would hand the keys back a frame early on the strength of it.
    /// </remarks>
    [Fact]
    public void RollingMagmaIsNotADodgeRoll()
    {
        AnimationNames table = Table();

        Assert.Equal("RollingMagma", table.Of(403));
        Assert.DoesNotContain(403, table.IdsNamed(RollWatch.Word));
        Assert.Contains(403, table.IdsNamed("roll"));
    }

    /// <summary>The game's table has more than one roll, which is why this is a query.</summary>
    /// <remarks>
    /// FloatDodgeRoll, DodgeRollSprint, CannonDodgeRollBack, DodgeRollMoveCancel and the rest -
    /// a list of them written by hand would be a list somebody has to maintain against a game
    /// that adds animations every league, which is the same reason <c>AnimationKind</c> matches
    /// on words rather than on ids.
    /// </remarks>
    [Fact]
    public void ThereAreSeveralRollsAndNoneOfThemWasTypedOutHere()
    {
        IReadOnlySet<int> rolls = Table().IdsNamed(RollWatch.Word);

        Assert.True(rolls.Count >= 10, $"only {rolls.Count} roll animations found");
    }

    /// <summary>A roll starting is the id CHANGING to a roll, not merely being one.</summary>
    /// <remarks>
    /// Chain-rolling is the case: a second roll out of a first plays the same animation id
    /// throughout, so a watch that only asked "is this a roll" would confirm instantly, before
    /// the game had seen anything, and the keys would go back before they had done their job.
    /// Comparing against what was playing when the key went down is what stops that.
    /// </remarks>
    [Fact]
    public void AlreadyRollingDoesNotCountAsHavingStarted()
    {
        var watch = RollWatch.For(Table(), before: 268);

        Assert.False(watch.Started(268));
        Assert.True(watch.Started(402));
    }

    /// <summary>Running, then rolling: the ordinary case, and the one that has to work.</summary>
    [Fact]
    public void RunningTurningIntoARollIsTheRollHavingStarted()
    {
        var watch = RollWatch.For(Table(), before: 195);

        Assert.True(watch.CanWatch);
        Assert.False(watch.Started(195));
        Assert.True(watch.Started(268));
    }

    /// <summary>Anything that is not a roll is not the roll starting.</summary>
    /// <remarks>
    /// Being hit mid-sequence changes the animation too, and it is not permission to let go of
    /// the keys - the roll has not been resolved yet, so the direction has not been read yet.
    /// </remarks>
    [Fact]
    public void SomeOtherAnimationIsNotTheRoll()
    {
        var watch = RollWatch.For(Table(), before: 0);

        Assert.False(watch.Started(403));
        Assert.False(watch.Started(195));
        Assert.False(watch.Started(4));
    }

    /// <summary>An unreadable animation is never a yes.</summary>
    /// <remarks>
    /// -1 is what a failed read produces, and it must fall to the ceiling rather than either
    /// confirming or throwing - the sequence it sits inside is holding the player's keys down.
    /// </remarks>
    [Fact]
    public void AnUnreadableAnimationConfirmsNothing()
    {
        var watch = RollWatch.For(Table(), before: 195);

        Assert.False(watch.Started(-1));
    }

    /// <summary>No table at all switches the watch off rather than crashing it.</summary>
    [Fact]
    public void NoTableMeansNothingToWatchFor()
    {
        var watch = RollWatch.For(null, before: 195);

        Assert.False(watch.CanWatch);
        Assert.False(watch.Started(268));

        Assert.False(RollWatch.None.CanWatch);
    }

    /// <summary>An empty table is a table with no roll in it, and says so.</summary>
    /// <remarks>
    /// <c>CanWatch</c> exists so the caller can decline to poll for an answer that cannot come,
    /// and take the plain sleep instead of spinning for the whole ceiling.
    /// </remarks>
    [Fact]
    public void AnEmptyTableCannotBeWatched()
    {
        var watch = RollWatch.For(AnimationNames.Empty, before: 195);

        Assert.False(watch.CanWatch);
        Assert.False(watch.Started(268));
    }

    /// <summary>
    /// A name learned from the running game changes the answer, in both directions.
    /// </summary>
    /// <remarks>
    /// THE GAME BEATS THE FILE, which is the rule <see cref="AnimationNames"/> already applies to
    /// <c>Of</c> and has to apply here too - a query that consulted only the shipped table would
    /// go on believing a row the game has already corrected. Both directions, because the file
    /// drifting means an id can stop being a roll as easily as start being one: 500 of the old
    /// table's 1084 rows named the wrong animation.
    /// </remarks>
    [Fact]
    public void WhatTheGameSaysBeatsWhatTheFileSays()
    {
        AnimationNames table = Table();

        table.Learn(7777, "SomeNewDodgeRollVariant");
        Assert.Contains(7777, table.IdsNamed(RollWatch.Word));

        // And the other way: 268 ships as DodgeRoll, and the game calling it something else has
        // to take it out of the set rather than leave the file's answer standing.
        table.Learn(268, "SomethingElseEntirely");
        Assert.DoesNotContain(268, table.IdsNamed(RollWatch.Word));
    }

    /// <summary>An empty or blank word matches nothing rather than everything.</summary>
    /// <remarks>
    /// <c>string.Contains("")</c> is true for every string, so the careless version of this
    /// query would call every animation in the game a dodge roll - which would confirm the roll
    /// on the first poll, every time, and quietly undo the whole thing.
    /// </remarks>
    [Fact]
    public void ABlankWordMatchesNothing()
    {
        AnimationNames table = Table();

        Assert.Empty(table.IdsNamed(string.Empty));
        Assert.Empty(table.IdsNamed("   "));
    }
}
