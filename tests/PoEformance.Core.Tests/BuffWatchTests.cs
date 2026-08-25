using PoEformance.Features;
using PoEformance.Game.Components;

namespace PoEformance.Core.Tests;

/// <summary>
/// Remembering which buffs a character has had on.
/// </summary>
/// <remarks>
/// This exists because the name a rule matches is not the name the game shows: memory carries
/// the ENGINE identifier - "flask_effect_life" - and the localised display name is a column
/// nothing here reads. So somebody looking at "Lightning Infusion" on their own screen has no
/// way to work out what to type, and a buff condition is written by guessing at a spelling.
/// </remarks>
public class BuffWatchTests
{
    private static ActiveBuffs On(params string[] names)
        => new([.. names.Select(name => new ActiveBuff(name, 5f, 10f, 2, 0, false))]);

    [Fact]
    public void ListsWhatIsOnTheCharacter()
    {
        var watch = new BuffWatch();
        watch.Look(On("lightning_infusion", "flask_effect_life"), 0);

        Assert.Equal(2, watch.Seen.Count);
        Assert.All(watch.Seen, buff => Assert.True(buff.Active));
        Assert.Contains(watch.Seen, buff => buff.Name == "lightning_infusion");
    }

    [Fact]
    public void KeepsABuffAfterItEnds()
    {
        // The whole point. A buff worth writing a rule about lasts a few seconds, so by the
        // time somebody has switched to the config window to type its name it has gone.
        var watch = new BuffWatch();
        watch.Look(On("lightning_infusion"), 0);
        watch.Look(ActiveBuffs.None, 1000);

        SeenBuff buff = Assert.Single(watch.Seen);
        Assert.Equal("lightning_infusion", buff.Name);
        Assert.False(buff.Active);
    }

    [Fact]
    public void ABuffThatEndedStopsClaimingTimeLeft()
    {
        // Otherwise it sits in the list saying "5.0s" forever, which reads as live - worse
        // than not listing it, because it is a readout somebody would act on.
        var watch = new BuffWatch();
        watch.Look(On("haste"), 0);
        watch.Look(On("other"), 1000);

        SeenBuff haste = watch.Seen.Single(buff => buff.Name == "haste");
        Assert.False(haste.Active);
    }

    [Fact]
    public void ActiveBuffsComeFirst()
    {
        // How somebody finds the one they just cast: it appears at the top, lit.
        var watch = new BuffWatch();
        watch.Look(On("old"), 0);
        watch.Look(On("fresh"), 5000);

        Assert.Equal("fresh", watch.Seen[0].Name);
        Assert.True(watch.Seen[0].Active);
    }

    [Fact]
    public void ForgetsWhatHasNotBeenSeenForAges()
    {
        var watch = new BuffWatch();
        watch.Look(On("gone"), 0);
        watch.Look(ActiveBuffs.None, BuffWatch.RememberMs + 1000);

        Assert.Empty(watch.Seen);
    }

    [Fact]
    public void ARunningBuffIsNeverForgotten()
    {
        // Age is measured from when it was last SEEN, so something on for an hour stays.
        var watch = new BuffWatch();
        watch.Look(On("permanent"), 0);
        watch.Look(On("permanent"), BuffWatch.RememberMs + 1000);

        Assert.True(Assert.Single(watch.Seen).Active);
    }

    [Fact]
    public void IsBoundedHoweverManyNamesTurnUp()
    {
        // Every unique id in an area lands here, including the ground effects and the monster
        // auras that wash over the player. A long session must not grow it without limit.
        var watch = new BuffWatch();
        for (int i = 0; i < BuffWatch.MaxRemembered * 2; i++)
        {
            watch.Look(On($"buff_{i}"), i);
        }

        Assert.InRange(watch.Seen.Count, 1, BuffWatch.MaxRemembered);
    }

    [Fact]
    public void AnUnreadableBuffListLeavesTheNamesAlone()
    {
        // Null is "could not be read", not "nothing is on". Clearing on it would empty the
        // list every time a read failed - which happens on every area change.
        var watch = new BuffWatch();
        watch.Look(On("haste"), 0);
        watch.Look(null, 1000);

        Assert.Single(watch.Seen);
    }

    [Fact]
    public void ABuffWithNoNameIsNotListed()
    {
        // A definition pointer that did not resolve. An empty row in a list of names to pick
        // from is a row somebody would click.
        var watch = new BuffWatch();
        watch.Look(new ActiveBuffs([new ActiveBuff(string.Empty, 1f, 1f, 0, 0, false)]), 0);

        Assert.Empty(watch.Seen);
    }

    [Fact]
    public void AFlaskBuffSaysWhichBeltSlotItCameFrom()
    {
        var watch = new BuffWatch();
        watch.Look(new ActiveBuffs([new ActiveBuff("flask_effect_life", 4f, 5f, 0, 3, true)]), 0);

        Assert.Equal(3, Assert.Single(watch.Seen).FlaskSlot);
    }
}
