using PoEformance.Features;
using PoEformance.Game.Components;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// The tracker's decisions, away from anything that draws.
/// </summary>
/// <remarks>
/// Everything checked here is a rule that decides what appears on screen, and every one of them
/// has a failure that looks like the feature simply not working: a rule that matches nothing
/// draws nothing, and so does a feature that is switched off, a sheet that did not load, and a
/// monster whose buffs were never read. Those are four different problems with one symptom,
/// which is why the matching is a testable thing of its own rather than a condition inside a
/// drawing loop.
/// </remarks>
public class TrackerTests
{
    private static ActiveBuffs Carrying(params ActiveBuff[] buffs) => new(buffs);

    private static ActiveBuff Buff(string name, float left = 0f, float total = 0f, int charges = 0)
        => new(name, left, total, charges, 0, false);

    /// <summary>
    /// The documented divergence from the reference: loose matching, not the exact key.
    /// </summary>
    /// <remarks>
    /// The reference looks the name up in a dictionary, so "shocked" finds nothing while the
    /// game is carrying "shocked_70". Nobody is ever shown either spelling, so an exact match
    /// means the feature does nothing until somebody has found the string - and this codebase
    /// already answers the same question loosely for the flask buffs.
    /// </remarks>
    [Fact]
    public void ARuleMatchesABuffWhoseNameMerelyContainsIt()
    {
        IReadOnlyList<StatusIconHit> found = StatusIcons.On(
            [new StatusIconRule("SHOCKED")],
            Carrying(Buff("shocked_70")));

        Assert.Single(found);
        Assert.Equal("SHOCKED", found[0].Rule.Name);
    }

    /// <summary>One rule is one icon, however many buffs it happens to match.</summary>
    /// <remarks>
    /// Three stacks of the same debuff are three entries in the game's own list, and a row that
    /// grew an icon per entry would push the others off the sides of the monster.
    /// </remarks>
    [Fact]
    public void ARuleThatMatchesSeveralBuffsIsStillOneIcon()
    {
        IReadOnlyList<StatusIconHit> found = StatusIcons.On(
            [new StatusIconRule("bleed")],
            Carrying(Buff("bleed_01"), Buff("bleed_02"), Buff("bleed_03")));

        Assert.Single(found);
    }

    /// <summary>
    /// The row is in the order the rules are, not the order the game lists buffs in.
    /// </summary>
    /// <remarks>
    /// The game's list changes as things come and go, so a row built from it reshuffles itself
    /// mid-fight - and an icon that moves is one that has to be read again every time it is
    /// looked at, which is the whole thing this feature is for.
    /// </remarks>
    [Fact]
    public void TheRowFollowsTheRulesRatherThanTheGamesOrder()
    {
        IReadOnlyList<StatusIconHit> found = StatusIcons.On(
            [new StatusIconRule("frozen"), new StatusIconRule("shocked")],
            Carrying(Buff("shocked_70"), Buff("frozen_10")));

        Assert.Equal(["frozen", "shocked"], found.Select(hit => hit.Rule.Name));
    }

    /// <summary>A rule with no name matches NOTHING, which is the important half.</summary>
    /// <remarks>
    /// An empty name is a substring of every string, so the loose matching above would make an
    /// unnamed rule draw an icon for every buff on everything. It arrives from the editor's own
    /// "add" button, so it is checked rather than trusted.
    /// </remarks>
    [Fact]
    public void ARuleWithNoNameAndADisabledOneBothMatchNothing()
    {
        Assert.Empty(StatusIcons.On([new StatusIconRule("")], Carrying(Buff("shocked_70"))));
        Assert.Empty(StatusIcons.On(
            [new StatusIconRule("shocked", Enabled: false)], Carrying(Buff("shocked_70"))));
    }

    /// <summary>Nothing read and nothing on it are both empty, and neither is an error.</summary>
    [Fact]
    public void AnEntityWhoseBuffsWereNeverReadHasNoIcons()
        => Assert.Empty(StatusIcons.On([new StatusIconRule("shocked")], buffs: null));

    /// <summary>The row cannot grow without bound, whatever the rules say.</summary>
    [Fact]
    public void TheRowStopsAtItsCap()
    {
        StatusIconRule[] rules = [.. Enumerable.Range(0, 40).Select(i => new StatusIconRule($"buff{i}"))];
        ActiveBuffs buffs = Carrying([.. Enumerable.Range(0, 40).Select(i => Buff($"buff{i}"))]);

        Assert.Equal(StatusIcons.MostIcons, StatusIcons.On(rules, buffs).Count);
    }

    /// <summary>
    /// A duration the game did not report fills the bar rather than emptying it.
    /// </summary>
    /// <remarks>
    /// "Unknown" and "expired" are not the same claim, and the empty bar is the one that lies:
    /// it says the debuff is about to end at the moment it was just applied. The reference
    /// cannot tell them apart at all - it hunts for a total duration by reflection and gives up
    /// at zero - which is why this is worth a test rather than a line of code.
    /// </remarks>
    [Fact]
    public void ATimerWithNoKnownTotalIsDrawnFull()
    {
        Assert.Equal(1f, new StatusIconHit(new StatusIconRule("x"), 4f, 0f, 0).Proportion);
        Assert.Equal(0.5f, new StatusIconHit(new StatusIconRule("x"), 3f, 6f, 0).Proportion);

        // And a game that reports more left than it started with is still a full bar, not a
        // bar drawn past its own end.
        Assert.Equal(1f, new StatusIconHit(new StatusIconRule("x"), 9f, 6f, 0).Proportion);
    }

    [Theory]
    [InlineData(0f, "0:00")]
    [InlineData(9.9f, "0:09")]
    [InlineData(61f, "1:01")]
    [InlineData(600f, "10:00")]
    public void TheTimerReadsAsMinutesAndSeconds(float left, string expected)
        => Assert.Equal(expected, new StatusIconHit(new StatusIconRule("x"), left, 0f, 0).Timer);

    /// <summary>A ground rule matches on the start of the path, as the reference does.</summary>
    [Fact]
    public void AGroundRuleMatchesTheStartOfAPath()
    {
        var rule = new GroundDangerRule("Metadata/Effects/Spells/ground_effects/");

        Assert.True(rule.Matches(Ground("Metadata/Effects/Spells/ground_effects/VisibleServerGroundEffect")));
        Assert.False(rule.Matches(Ground("Metadata/Effects/Spells/orb_of_storms/Thing")));
    }

    /// <summary>
    /// Your own ground effects are never ringed, however well they match.
    /// </summary>
    /// <remarks>
    /// A build that leaves burning ground behind it leaves a great deal of it, and a ring
    /// around each is the overlay covering the screen at exactly the moment it is needed. From
    /// the reference, which checks the same byte.
    /// </remarks>
    [Fact]
    public void AFriendlyGroundEffectIsNeverRinged()
    {
        var rule = new GroundDangerRule("Metadata/Effects/");

        Assert.True(rule.Matches(Ground("Metadata/Effects/Fire", friendly: false)));
        Assert.False(rule.Matches(Ground("Metadata/Effects/Fire", friendly: true)));
    }

    /// <summary>
    /// The shipped rule and the component ring aim at exactly the same ground.
    /// </summary>
    /// <remarks>
    /// The setup for the two tests below, and the reason they are not hypothetical. The shipped
    /// rule is not merely a prefix that happens to cover the tagged ground - it is spelled as
    /// the exact path that carries the component, so the overlap under DEFAULT settings is
    /// total. If this ever stops being true the overlap has moved rather than gone, and the fix
    /// below still applies: it is keyed on the component, not on this path.
    /// </remarks>
    [Fact]
    public void TheShippedRuleCoversTheGroundTheGameTagsItself()
    {
        WorldEntity tagged = Ground(
            "Metadata/Effects/Spells/ground_effects/VisibleServerGroundEffect") with { IsGroundEffect = true };

        Assert.Contains(TrackerSettings.Default.GroundDangerOrDefault, rule => rule.Matches(tagged));
    }

    /// <summary>
    /// Ground a rule speaks for is ringed once, by the rule - not also by the component.
    /// </summary>
    /// <remarks>
    /// IT USED TO BE RINGED TWICE, and then for one commit it was ringed by the wrong one.
    ///
    /// Both passes walk the same entity list and neither knew about the other, so a tagged patch
    /// got a world-radius ring lying on the floor with an X and a countdown AND a flat pixel
    /// circle of whatever size the rule carried - two circles of different sizes on one patch,
    /// from the settings the tool ships with. The first fix gave the component the win, on the
    /// belief that reading a radius out of memory beats a number somebody typed.
    ///
    /// THAT BELIEF WAS WRONG, and this is the test that now holds the correction in place. The
    /// component marks a server-spawned ground DECAL, not damage - 5880 of 5916 readings in the
    /// sweep capture are in a hideout - so it cannot outrank the one signal in the system that
    /// is actually about danger, which is a path somebody deliberately wrote down.
    /// </remarks>
    [Fact]
    public void GroundARuleSpeaksForIsRingedByTheRuleAndNotAlsoByTheComponent()
    {
        WorldEntity tagged = Ground(
            "Metadata/Effects/Spells/ground_effects/VisibleServerGroundEffect") with { IsGroundEffect = true };
        var settings = TrackerSettings.Default with { ShowGroundEffects = true };

        Assert.True(settings.RulesShouldRing(tagged));
        Assert.False(settings.ComponentShouldRing(tagged));
    }

    /// <summary>Where no rule speaks, the component ring is what shows the decal at all.</summary>
    /// <remarks>
    /// The awareness half. Finding what to write a rule ABOUT needs something that marks ground
    /// nobody has described yet, and this is it - which is the honest job for a signal that says
    /// "the server put a decal here" and nothing more.
    /// </remarks>
    [Fact]
    public void WhereNoRuleSpeaksTheComponentRingsIt()
    {
        WorldEntity tagged = Ground("Metadata/Effects/Spells/ground_effects/Fire") with { IsGroundEffect = true };
        var settings = TrackerSettings.Default with { ShowGroundEffects = true };

        Assert.DoesNotContain(settings.GroundDangerOrDefault, rule => rule.Matches(tagged));
        Assert.True(settings.ComponentShouldRing(tagged));
    }

    /// <summary>A rule pass switched off cannot be the reason the component stands aside.</summary>
    /// <remarks>
    /// The deference is to a ring that will actually be DRAWN. Standing aside for a rule that is
    /// switched off would leave the entity marked by nothing at all, which is the one outcome
    /// neither pass is allowed to produce between them.
    /// </remarks>
    [Fact]
    public void ARuleThatCannotDrawDoesNotHoldTheComponentBack()
    {
        WorldEntity tagged = Ground(
            "Metadata/Effects/Spells/ground_effects/VisibleServerGroundEffect") with { IsGroundEffect = true };

        Assert.True((TrackerSettings.Default with { ShowGroundEffects = true, ShowGroundDanger = false })
            .ComponentShouldRing(tagged));
    }

    /// <summary>Untagged ground is always the rules' to ring - it is the only thing that will.</summary>
    [Fact]
    public void UntaggedGroundIsAlwaysLeftToTheRules()
    {
        WorldEntity untagged = Ground("Metadata/Effects/Spells/ground_effects/Fire");
        var settings = TrackerSettings.Default with { ShowGroundEffects = true };

        Assert.True(settings.RulesShouldRing(untagged));
        Assert.False(settings.ComponentShouldRing(untagged));
    }

    /// <summary>
    /// A remembered sighting is never ringed, by either pass.
    /// </summary>
    /// <remarks>
    /// Ground effects expire. A ring on a remembered one is a ring around ground that is SAFE
    /// again, which is the one mistake this feature must not make - it would send somebody
    /// around the long way past nothing at all.
    /// </remarks>
    [Fact]
    public void RememberedGroundIsNeverRinged()
    {
        WorldEntity gone = Ground("Metadata/Effects/Spells/ground_effects/Fire")
            with { IsGroundEffect = true, RememberedForMs = 500 };
        var settings = TrackerSettings.Default with { ShowGroundEffects = true };

        Assert.False(settings.RulesShouldRing(gone));
        Assert.False(settings.ComponentShouldRing(gone));
    }

    /// <summary>An empty prefix would ring every entity in the area, so it matches none.</summary>
    [Fact]
    public void AGroundRuleWithNoPathAndADisabledOneBothMatchNothing()
    {
        WorldEntity anything = Ground("Metadata/Effects/Spells/ground_effects/Thing");

        Assert.False(new GroundDangerRule("").Matches(anything));
        Assert.False(new GroundDangerRule("   ").Matches(anything));
        Assert.False(new GroundDangerRule("Metadata/", Enabled: false).Matches(anything));
    }

    /// <summary>A colour without its switch draws nothing, and the pair is asked as one.</summary>
    [Fact]
    public void LinesAreDrawnOnlyForTheRaritiesThatWereAskedFor()
    {
        var lines = new MonsterLineSettings(Unique: true, Rare: false);

        Assert.True(lines.For(ItemRarity.Unique).On);
        Assert.False(lines.For(ItemRarity.Rare).On);

        // Normal monsters have no switch at all - there is no colour to draw them in, and a
        // line to every skeleton in a pack is the fan of lines this exists to avoid.
        Assert.False(lines.For(ItemRarity.Normal).On);
        Assert.False(lines.For(ItemRarity.Unknown).On);
    }

    /// <summary>
    /// The layout profile is chosen by HEIGHT, which is what makes an ultrawide work.
    /// </summary>
    /// <remarks>
    /// 3440x1440 is a 1440p screen for this purpose: the icons are a fixed pixel size and the
    /// characters are drawn to the screen's height, so the width says nothing about how far
    /// above a monster's feet its head is.
    /// </remarks>
    [Theory]
    [InlineData(1080f, "1080p")]
    [InlineData(1200f, "1080p")]
    [InlineData(1440f, "1440p")]
    [InlineData(2160f, "4K")]
    [InlineData(2880f, "4K")]
    public void TheLayoutProfileIsChosenByScreenHeight(float height, string expected)
        => Assert.Equal(expected, StatusLayoutProfile.ForHeight(height).Name);

    /// <summary>
    /// The reader only pays for monster buffs while something is actually drawing them.
    /// </summary>
    /// <remarks>
    /// THE ONE SETTING IN HERE WITH A PRICE ON IT - a Buffs component walk per rare-or-better
    /// monster, which nothing else in the tool wants. Switched on with no enabled rule it would
    /// be paid for nothing at all, which is the case worth checking: that is what a list
    /// somebody emptied looks like.
    /// </remarks>
    [Fact]
    public void MonsterBuffsAreReadOnlyWhenSomethingWillDrawThem()
    {
        TrackerSettings off = TrackerSettings.Default with { ShowMonsterStatus = false };
        Assert.False(off.NeedsMonsterBuffs);

        TrackerSettings on = TrackerSettings.Default with { ShowMonsterStatus = true };
        Assert.True(on.NeedsMonsterBuffs);

        TrackerSettings nothingEnabled = on with
        {
            MonsterStatus = [new StatusIconRule("shocked", Enabled: false)],
        };
        Assert.False(nothingEnabled.NeedsMonsterBuffs);

        TrackerSettings emptied = on with { MonsterStatus = [] };
        Assert.False(emptied.NeedsMonsterBuffs);
    }

    /// <summary>
    /// An absent list takes the defaults; an emptied one is honoured.
    /// </summary>
    /// <remarks>
    /// The same distinction the alerts make, and for the same reason: deleting every rule has
    /// to be something a person can do, and a list that grows its defaults back is a list that
    /// cannot be emptied.
    /// </remarks>
    [Fact]
    public void AnEmptiedListStaysEmptyWhileAnAbsentOneTakesTheDefaults()
    {
        Assert.NotEmpty(TrackerSettings.Default.GroundDangerOrDefault);
        Assert.Empty((TrackerSettings.Default with { GroundDanger = [] }).GroundDangerOrDefault);
        Assert.Empty((TrackerSettings.Default with { MonsterStatus = [] }).MonsterStatusOrDefault);
    }

    /// <summary>What a hand-edited file cannot do, however it is written.</summary>
    /// <remarks>
    /// The tile size is a DIVISOR in the texture-coordinate arithmetic, so a zero in the file
    /// is not a bad-looking icon - it is a division by zero on the render thread, which ends
    /// the frame and, reported once, the session's usefulness.
    /// </remarks>
    [Fact]
    public void NormalisingKeepsEveryValueSomethingCanBeDrawnFrom()
    {
        TrackerSettings wild = TrackerSettings.Default with
        {
            IconTile = 0,
            ShadowAlpha = 40f,
            ShadowSize = 9,
            MonsterStatus = [new StatusIconRule("", "unnamed"), new StatusIconRule("shocked", IconScale: 900f)],
            GroundDanger = [new GroundDangerRule("")],
        };

        TrackerSettings safe = wild.Normalised();

        Assert.True(safe.IconTile > 0);
        Assert.Equal(1f, safe.ShadowAlpha);
        Assert.Equal(2, safe.ShadowSize);
        Assert.Empty(safe.GroundDangerOrDefault);

        StatusIconRule kept = Assert.Single(safe.MonsterStatusOrDefault);
        Assert.Equal("shocked", kept.Name);
        Assert.Equal(10f, kept.IconScale);
    }

    /// <summary>Settings survive the trip to disk and back.</summary>
    [Fact]
    public void SettingsRoundTripThroughAFile()
    {
        string file = Path.Combine(Path.GetTempPath(), $"tracker-{Guid.NewGuid():N}.json");
        try
        {
            var written = TrackerSettings.Default with
            {
                Lines = new MonsterLineSettings(Unique: true, UniqueColour: "#80FF0000"),
                ShowMonsterStatus = true,
                IconSheet = "sheets/status.png",
                MonsterStatus = [new StatusIconRule("shocked", "Shocked", IconColumn: 3, IconRow: 7)],
            };

            Assert.True(TrackerStore.Save(written, file));
            TrackerSettings read = TrackerStore.Load(file);

            Assert.True(read.LinesOrDefault.Unique);
            Assert.Equal("#80FF0000", read.LinesOrDefault.UniqueColour);
            Assert.True(read.ShowMonsterStatus);
            Assert.Equal("sheets/status.png", read.IconSheet);

            StatusIconRule rule = Assert.Single(read.MonsterStatusOrDefault);
            Assert.Equal(3, rule.IconColumn);
            Assert.Equal(7, rule.IconRow);
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>A file that is not there, or is not settings, leaves the defaults standing.</summary>
    [Fact]
    public void AnUnreadableFileLeavesTheDefaultsStanding()
    {
        string file = Path.Combine(Path.GetTempPath(), $"tracker-{Guid.NewGuid():N}.json");
        Assert.Equal(TrackerSettings.Default, TrackerStore.Load(file));

        try
        {
            File.WriteAllText(file, "this is not json");
            Assert.Equal(TrackerSettings.Default, TrackerStore.Load(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static WorldEntity Ground(string path, bool friendly = false)
        => new(9, 0x5000, path, EntityKind.Effect, 0f, 0f, 0f, IsFriendly: friendly);
}
