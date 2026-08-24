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
