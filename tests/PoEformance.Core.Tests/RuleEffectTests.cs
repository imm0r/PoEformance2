using PoEformance.Features;
using PoEformance.Game.Components;

namespace PoEformance.Core.Tests;

/// <summary>
/// What an effect does before it is performed: its values made safe, its caption filled in,
/// and the key it presses resolved.
/// </summary>
public class RuleEffectTests
{
    [Fact]
    public void BringsAHandEditedEffectIntoRange()
    {
        // The file is meant to be hand-editable, so it can arrive holding any of this - and
        // none of it should reach a renderer or a key sender.
        RuleEffect wild = new RuleEffect(RuleEffectKind.Text)
        {
            X = 12f,
            Y = -9f,
            Scale = 400f,
            Slot = -3,
            Pitch = 1,
            SoundMs = 90_000,
            CooldownMs = -5,
            Colour = "not a colour",
        }.Normalised();

        Assert.InRange(wild.X, -0.5f, 1.5f);
        Assert.InRange(wild.Y, -0.5f, 1.5f);
        Assert.InRange(wild.Scale, 0.25f, 8f);
        Assert.InRange(wild.Slot, 0, AutoFlaskSettings.SlotCount);
        Assert.InRange(wild.Pitch, 37, 32_767);
        Assert.InRange(wild.SoundMs, 1, 5_000);
        Assert.Equal(0, wild.CooldownMs);
        Assert.Equal(RuleColours.DefaultText, wild.Colour);
    }

    [Fact]
    public void ReadsAColourTheWayTheRestOfTheToolSpellsOne()
    {
        // One colour vocabulary across the tool. A second parser reading #rrggbbaa would draw
        // every caption invisible from a file that reads perfectly to the style editor.
        Assert.Equal(OverlaySettings.ParseColour("#FF3040"), RuleColours.Packed("#FF3040", 0));
        Assert.Equal(OverlaySettings.ParseColour("#80FF3040"), RuleColours.Packed("#80FF3040", 0));

        // And an unreadable one falls back rather than drawing nothing.
        Assert.Equal(123u, RuleColours.Packed("purple-ish", 123u));
    }

    [Fact]
    public void KnowsWhichEffectsReachOutsideTheProcess()
    {
        // The property the engine gates every input on, so a kind added to the enum and
        // forgotten in one place cannot become one that bypasses the focus check.
        foreach (RuleEffectKind kind in Enum.GetValues<RuleEffectKind>())
        {
            var effect = new RuleEffect(kind);
            bool harmless = kind is RuleEffectKind.Text or RuleEffectKind.Bar or RuleEffectKind.Sound;

            Assert.Equal(!harmless, effect.Sends);
            Assert.Equal(kind is RuleEffectKind.Text or RuleEffectKind.Bar, effect.Draws);
        }
    }

    [Fact]
    public void FillsACaptionFromAnyFactTheEditorOffers()
    {
        // One vocabulary: anything a condition can ask about, a caption can show. The
        // reference plugin has ten hard-coded replacements and no way to add an eleventh.
        var state = new RuleState
        {
            InGame = true,
            AreaName = "The Copper Citadel",
            Vitals = new Vitals(new Vital(40, 100, 0, 0), default, default),
        };

        Assert.Equal("40% in The Copper Citadel", RuleText.Fill("{LifePercent}% in {AreaName}", state));
        Assert.Equal("in game: yes", RuleText.Fill("in game: {InGame}", state));
    }

    [Fact]
    public void ShowsADashForSomethingThatCouldNotBeRead()
    {
        // Not 0. "Life 0%" on a loading screen is a bug report.
        Assert.Equal("life -", RuleText.Fill("life {LifePercent}", new RuleState { InGame = true }));
    }

    [Fact]
    public void LeavesAMisspeltPlaceholderVisible()
    {
        // Left as written, brackets and all: an empty string looks like a fact that answered
        // nothing, where "{Helth}" says plainly that the name is wrong.
        Assert.Equal("{Helth}", RuleText.Fill("{Helth}", RuleState.Nothing));
    }

    [Fact]
    public void ACaptionNeverConsumesAnInterval()
    {
        // An EverySeconds placeholder drawn every frame would tick the timer sixty times a
        // second, and the rule sharing that interval would never come round.
        var state = new RuleState { InGame = true };
        var timers = new RuleTimers();
        timers.Tick(0);

        for (int frame = 0; frame < 10; frame++)
        {
            RuleText.Fill("{EverySeconds}", state);
        }

        var every = new RuleCondition { Fact = RuleFact.EverySeconds, Argument = 1 };
        Assert.True(every.Holds(state, timers, "rule"));
    }

    [Fact]
    public void CountsWhatIsUnderTheCursor_NotWhatIsNearThePlayer()
    {
        // The AHK tool's cursor radius, which the reference plugin has no equivalent for. For a
        // skill placed where you aim, "three monsters near me" is the wrong question entirely:
        // the pack behind the character does not make a wall in front of it worth casting.
        var state = new RuleState
        {
            InGame = true,
            CursorGround = (100f, 100f),
            Monsters =
            [
                // Two where the cursor points, three clustered elsewhere - all equally near the
                // PLAYER, which is what makes this a different answer rather than a narrower one.
                new NearMonster(10, ItemRarity.Normal, 105, 100),
                new NearMonster(12, ItemRarity.Rare, 100, 92),
                new NearMonster(14, ItemRarity.Normal, -80, -60),
                new NearMonster(16, ItemRarity.Normal, -75, -70),
                new NearMonster(18, ItemRarity.Normal, -90, -55),
            ],
        };

        Assert.Equal(5, state.MonsterCount);
        Assert.Equal(2, state.MonsterCountAtCursor(30));
        Assert.Equal(1, state.RareOrUniqueCountAtCursor(30));
        Assert.Equal(5, state.MonsterCountAtCursor(1000));
        Assert.Equal(5, state.NearestMonsterAtCursor ?? -1, 3);
    }

    [Fact]
    public void ACursorRadiusIsMeasuredOnTheGround_NotOnTheScreen()
    {
        // The correction this shipped with. A circle of screen pixels around the mouse is an
        // ELLIPSE on the ground, stretched away from the camera by the tilt - so it counts
        // monsters in a region no skill has. Two monsters the same world distance from the
        // cursor must count the same, whichever direction they lie in; measured in pixels the
        // one further from the camera would fall outside while the other stayed in.
        var state = new RuleState
        {
            InGame = true,
            CursorGround = (0f, 0f),
            Monsters =
            [
                new NearMonster(0, ItemRarity.Normal, 25, 0),
                new NearMonster(0, ItemRarity.Normal, 0, 25),
                new NearMonster(0, ItemRarity.Normal, -25, 0),
                new NearMonster(0, ItemRarity.Normal, 0, -25),
            ],
        };

        Assert.Equal(4, state.MonsterCountAtCursor(26));
        Assert.Equal(0, state.MonsterCountAtCursor(24));
    }

    [Fact]
    public void ACursorRuleAnswersNothingWhileThePointerIsElsewhere()
    {
        // Null rather than clamped to an edge: a pointer parked over the inventory would
        // otherwise read as aiming at the edge of the world, and the rule would fire on
        // whatever monster happened to stand nearest it.
        var state = new RuleState
        {
            InGame = true,
            Monsters = [new NearMonster(10, ItemRarity.Normal, 5, 5)],
        };

        Assert.Equal(0, state.MonsterCountAtCursor(500));
        Assert.Null(state.NearestMonsterAtCursor);

        // And a comparison against an absent number says no, whichever way it is written.
        var leaf = new RuleCondition { Fact = RuleFact.NearestMonsterAtCursor, Compare = Compare.AtMost, Value = 100 };
        Assert.False(leaf.Holds(state, new RuleTimers(), "rule"));
    }

    [Fact]
    public void WatchesTheWeakestMonsterInRange_NotTheNearestOne()
    {
        // The cull question: "is anything in range nearly dead". Keyed on the LOWEST bar
        // rather than the closest monster, because a full-health one stepping in front of the
        // one that was about to die must not silence the rule - which is exactly the moment a
        // cull is wanted.
        var state = new RuleState
        {
            InGame = true,
            Monsters =
            [
                new NearMonster(10, ItemRarity.Normal, 105, 100, 100),
                new NearMonster(20, ItemRarity.Rare, 100, 92, 12),

                // Out of the radius below, and lower than either of them: the radius is what
                // decides, not the whole area.
                new NearMonster(40, ItemRarity.Normal, -80, -60, 5),
            ],
        };

        Assert.Equal(12, state.LowestMonsterLifePercent(30));
        Assert.Equal(5, state.LowestMonsterLifePercent(100));
        Assert.Equal(10, state.NearestMonster);

        var cull = new RuleCondition
        {
            Fact = RuleFact.LowestMonsterLifePercent,
            Compare = Compare.Below,
            Value = 35,
            Argument = 30,
        };

        Assert.True(cull.Holds(state, new RuleTimers(), "rule"));
    }

    [Fact]
    public void NothingInRangeIsNoAnswer_NotAMonsterAtZeroHealth()
    {
        // The one trap this fact carries that the counts do not. An empty radius answering 0
        // would satisfy "below 35" forever, so a rule whose whole job is to press a key would
        // press it at the floor - and it would look like the feature working.
        var state = new RuleState
        {
            InGame = true,
            Monsters =
            [
                new NearMonster(80, ItemRarity.Normal, 0, 0, 4),

                // In range, but its pool did not resolve. Skipped rather than counted as a
                // monster at zero: Vital.Percent answers -1 for that, and the sentinel must not
                // reach a comparison.
                new NearMonster(10, ItemRarity.Normal, 5, 5),
            ],
        };

        Assert.Null(state.LowestMonsterLifePercent(30));

        var cull = new RuleCondition
        {
            Fact = RuleFact.LowestMonsterLifePercent,
            Compare = Compare.Below,
            Value = 35,
            Argument = 30,
        };

        Assert.False(cull.Holds(state, new RuleTimers(), "rule"));

        // And the other way round, which is the half a sentinel would get wrong quietly: an
        // absent number satisfies no comparison, whichever direction it is written in.
        Assert.False((cull with { Compare = Compare.AtLeast }).Holds(state, new RuleTimers(), "rule"));
    }

    [Fact]
    public void EachRarityIsWatchedAtItsOwnThreshold()
    {
        // A cull executes each rarity from a different share of its life, so the three
        // thresholds have to look at three different monsters. One threshold over everything
        // would answer about the white monster that is always the weakest thing on screen -
        // and fire while the rare it was written for stands at half health.
        var state = new RuleState
        {
            InGame = true,
            Vitals = new Vitals(default, new Vital(1500, 1500, 0, 0), default),
            Monsters =
            [
                new NearMonster(10, ItemRarity.Normal, 0, 0, 2),
                new NearMonster(20, ItemRarity.Magic, 0, 0, 55),
                new NearMonster(30, ItemRarity.Rare, 0, 0, 8),
                new NearMonster(40, ItemRarity.Unique, 0, 0, 40),
            ],
        };

        // The white monster at 2% is the weakest of the lot, and it is the answer only to the
        // fact that asks about anything.
        Assert.Equal(2, state.LowestMonsterLifePercent(100));
        Assert.Equal(55, state.LowestMagicMonsterLifePercent(100));
        Assert.Equal(8, state.LowestRareMonsterLifePercent(100));
        Assert.Equal(40, state.LowestUniqueMonsterLifePercent(100));

        // The Power Siphon rule: the rare is low enough, the magic and the unique are not.
        RuleCondition cull = RuleExpression.Parse(
            "InMap && Mana > 1000 && ("
            + "LowestMagicMonsterLifePercent(100) < 20"
            + " || LowestRareMonsterLifePercent(100) < 10"
            + " || LowestUniqueMonsterLifePercent(100) < 5)").Condition!;

        Assert.True(cull.Holds(state, new RuleTimers(), "rule"));

        // Take the rare away and the rule goes quiet, even though a monster at 2% is still
        // standing there: a rarity that is not in range is no answer, not a zero.
        RuleState withoutRare = state with { Monsters = [state.Monsters[0], state.Monsters[1], state.Monsters[3]] };
        Assert.Null(withoutRare.LowestRareMonsterLifePercent(100));
        Assert.False(cull.Holds(withoutRare, new RuleTimers(), "rule"));
    }

    [Fact]
    public void MeasuresTheWeakestMonsterWhereTheCursorPoints()
    {
        // The same split the counts have: for a skill aimed at something, the weakest thing
        // near the character is the wrong question.
        var state = new RuleState
        {
            InGame = true,
            CursorGround = (100f, 100f),
            Monsters =
            [
                new NearMonster(10, ItemRarity.Normal, 105, 100, 60),
                new NearMonster(12, ItemRarity.Rare, 100, 92, 20),
                new NearMonster(14, ItemRarity.Normal, -80, -60, 3),
            ],
        };

        Assert.Equal(20, state.LowestMonsterLifePercentAtCursor(30));
        Assert.Equal(3, state.LowestMonsterLifePercent(30));

        // A pointer that is not over the game answers nothing rather than answering about
        // whatever stands nearest the edge of the world.
        Assert.Null((state with { CursorGround = null }).LowestMonsterLifePercentAtCursor(500));
    }

    [Theory]
    [InlineData("Q", 0x51)]
    [InlineData("q", 0x51)]
    [InlineData("1", 0x31)]
    [InlineData("F5", 0x74)]
    [InlineData("Space", 0x20)]
    [InlineData("esc", 0x1B)]
    [InlineData("vk81", 0x51)]
    [InlineData("", 0)]
    [InlineData("nonsense", 0)]
    public void ReadsAKeyByName(string name, int expected) => Assert.Equal(expected, RuleKeys.Code(name));

    [Fact]
    public void DoesNotReadABareNumberAsAKeyCode()
    {
        // The AHK tool's history: the game's own config stores 81 for Q, and reading a rule's
        // "1" as virtual-key 1 would press something nobody asked for. Digits are digits here,
        // and a code is only reachable through the explicit vk spelling.
        Assert.Equal(0x31, RuleKeys.Code("1"));
        Assert.NotEqual(1, RuleKeys.Code("1"));
    }

    [Fact]
    public void EveryOfferedKeyNameSurvivesBeingShownAndReadBack()
    {
        // The editor shows a saved binding as a name. A one-way lookup is how a tool ends up
        // displaying "81" where the user typed "Q".
        foreach (string name in RuleKeys.Names)
        {
            ushort code = RuleKeys.Code(name);
            Assert.NotEqual(0, code);
            Assert.Equal(code, RuleKeys.Code(RuleKeys.Name(code)));
        }
    }

    [Fact]
    public void ASequenceDropsWhatItCannotPressAndKeepsTheRest()
    {
        // Refusing the whole sequence would turn one typo into a macro that silently does
        // nothing at all - harder to notice, and harder to find.
        Assert.Equal([(ushort)0x51, (ushort)0x57, (ushort)0x45], RuleKeys.Sequence("Q, W, Wat, E"));
        Assert.Empty(RuleKeys.Sequence(""));
    }

    [Fact]
    public void ASequenceIsBounded()
    {
        string many = string.Join(",", Enumerable.Repeat("Q", RuleKeys.MaxSequence + 10));
        Assert.Equal(RuleKeys.MaxSequence, RuleKeys.Sequence(many).Count);
    }

    [Fact]
    public void SettingsSurviveBeingSavedAndLoaded()
    {
        RuleCondition condition = RuleExpression.Parse("InMap && NearestRareMonster <= 45").Condition!;
        var settings = new RuleSettings(
            Enabled: true,
            Profile: "Mapping",
            Profiles:
            [
                new RuleProfile("Mapping",
                [
                    new RuleGroup("Combat",
                    [
                        new Rule("id-1", "Rare close", condition,
                        [
                            new RuleEffect(RuleEffectKind.Sound) { Pitch = 1050, SoundMs = 90 },
                            new RuleEffect(RuleEffectKind.Text, "RARE") { Colour = "#FFFF8800" },
                        ])
                        {
                            Priority = 70,
                            AllowLower = false,
                        },
                    ]),
                ]),
            ]);

        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            Assert.True(RuleSettingsStore.Save(settings, path));
            RuleSettings loaded = RuleSettingsStore.Load(path);

            Rule rule = loaded.Profiles[0].Groups[0].Rules[0];
            Assert.Equal("Mapping", loaded.Profile);
            Assert.Equal(condition, rule.Condition);
            Assert.Equal(2, rule.Effects.Count);
            Assert.Equal(1050, rule.Effects[0].Pitch);
            Assert.Equal("#ffff8800", rule.Effects[1].Colour, ignoreCase: true);
            Assert.False(rule.AllowLower);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ACorruptFileLoadsAsNothingArmed()
    {
        // The settings file arms key presses, so the way for it to fail is a tool that does
        // nothing - never a throw out of startup and never a half-read profile.
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            File.WriteAllText(path, "{ this is not json");
            RuleSettings loaded = RuleSettingsStore.Load(path);

            Assert.False(loaded.Enabled);
            Assert.Empty(loaded.Profiles[0].Groups);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ASettingsFileIsBoundedHoweverItArrives()
    {
        var huge = new RuleSettings(
            true,
            "P",
            [new RuleProfile("P", [new RuleGroup("G", Enumerable.Range(0, RuleGroup.MaxRules + 50)
                .Select(n => new Rule($"id{n}", "R", RuleCondition.Of(RuleFact.InGame),
                    Enumerable.Repeat(new RuleEffect(), Rule.MaxEffects + 5).ToArray()))
                .ToArray())])]).Normalised();

        RuleGroup group = huge.Profiles[0].Groups[0];
        Assert.Equal(RuleGroup.MaxRules, group.Rules.Count);
        Assert.Equal(Rule.MaxEffects, group.Rules[0].Effects.Count);
    }
}
