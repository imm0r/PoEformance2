using PoEformance.Features;
using PoEformance.Game.Components;

namespace PoEformance.Core.Tests;

/// <summary>
/// The settings file: what it defaults to, and what it refuses.
/// </summary>
/// <remarks>
/// The file is hand-editable and arms KEY PRESSES, so every value has a way of being wrong that
/// is invisible from the outside - a radius of zero means nothing is ever aimed at you, an empty
/// filter string matches every monster, and a key code out of range presses nothing while
/// looking configured. Normalisation is where those stop, and this is where normalisation is
/// checked.
/// </remarks>
public class EvasionSettingsTests
{
    [Fact]
    public void EverythingIsOffByDefault()
    {
        // The only defensible default for a feature that can press a key on its own. Asserted
        // rather than assumed, because a default flipped by accident would ship a tool that
        // starts acting the first time it is run.
        EvasionSettings settings = EvasionSettings.Default;

        Assert.False(settings.WarnOrDefault.Enabled);
        Assert.False(settings.ActOrDefault.Enabled);
        Assert.False(settings.NeedsActions);
        Assert.Equal(0, settings.DodgeKey);
    }

    [Fact]
    public void TheActFloorStartsStricterThanTheWarnFloor()
    {
        // The two are separate settings for a reason, and their defaults say what that reason
        // is: a marker for a white monster costs a ring on screen, a keystroke for one costs a
        // roll charge - and white monsters are most of what an area contains.
        Assert.Equal(ItemRarity.Normal, EvasionSettings.Default.WarnOrDefault.FromRarity);
        Assert.Equal(ItemRarity.Rare, EvasionSettings.Default.ActOrDefault.FromRarity);
    }

    [Fact]
    public void TheReaderIsOnlyAskedWhenAGateIsOpen()
    {
        // The priced setting. Four reads per hostile monster per tick that buy nothing while
        // both halves are switched off.
        Assert.False(new EvasionSettings().NeedsActions);
        Assert.True(new EvasionSettings(Warn: new EvasionGate(true)).NeedsActions);
        Assert.True(new EvasionSettings(Act: new EvasionGate(true)).NeedsActions);
    }

    [Fact]
    public void ARadiusOfZeroIsRefused()
    {
        // It would mean nothing is ever close enough to count, so the feature would draw
        // everything and act on nothing - which reads as broken rather than as configured.
        Assert.True(new EvasionSettings(DangerRadius: 0f).Normalised().DangerRadius >= 10f);
        Assert.True(new EvasionSettings(DangerRadius: 999_999f).Normalised().DangerRadius <= 2000f);
    }

    [Fact]
    public void AnEmptyFilterStringIsDroppedRatherThanMatchingEverything()
    {
        // "" is a substring of every path, so a stray comma in the config page would silently
        // turn "only these monsters" into "all of them".
        EvasionGate gate = new EvasionGate(true, ItemRarity.Normal, OnlyPaths: ["", "  ", "Goatman"]).Normalised();

        Assert.Equal(["Goatman"], gate.OnlyPaths);
        Assert.True(gate.Admits(ItemRarity.Normal, "Metadata/Monsters/Goatman/X"));
        Assert.False(gate.Admits(ItemRarity.Normal, "Metadata/Monsters/Skeleton/X"));
    }

    [Fact]
    public void AKeyCodeOutOfRangeIsClamped()
    {
        Assert.Equal(0, new EvasionSettings(DodgeKey: -5).Normalised().DodgeKey);
        Assert.Equal(0xFF, new EvasionSettings(DodgeKey: 9999).Normalised().DodgeKey);
    }

    [Fact]
    public void ADisabledGateAdmitsNothingHoweverItIsConfigured()
    {
        EvasionGate off = new(Enabled: false, FromRarity: ItemRarity.Normal);
        Assert.False(off.Admits(ItemRarity.Unique, "Metadata/Monsters/Anything"));
    }

    [Fact]
    public void SettingsSurviveASaveAndLoadRoundTrip()
    {
        // Source-generated JSON, so this is also the check that the AOT context knows the type -
        // a missing [JsonSerializable] fails at runtime and nowhere else.
        string path = Path.Combine(Path.GetTempPath(), $"evasion-{Guid.NewGuid():N}.json");
        try
        {
            var written = new EvasionSettings(
                Warn: new EvasionGate(true, ItemRarity.Magic, OnlyPaths: ["Goatman"]),
                Act: new EvasionGate(true, ItemRarity.Unique, IgnorePaths: ["Totem"]),
                DangerRadius: 120f,
                CooldownMs: 900,
                DodgeKey: 0x20);

            Assert.True(EvasionSettingsStore.Save(written, path));
            EvasionSettings read = EvasionSettingsStore.Load(path);

            Assert.True(read.WarnOrDefault.Enabled);
            Assert.Equal(ItemRarity.Magic, read.WarnOrDefault.FromRarity);
            Assert.Equal(["Goatman"], read.WarnOrDefault.OnlyPaths);
            Assert.Equal(ItemRarity.Unique, read.ActOrDefault.FromRarity);
            Assert.Equal(["Totem"], read.ActOrDefault.IgnorePaths);
            Assert.Equal(120f, read.DangerRadius);
            Assert.Equal(0x20, read.DodgeKey);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ACorruptFileFallsBackToTheDefaultsWhichAreOff()
    {
        // The correct way for a settings file to fail when the setting arms key presses.
        string path = Path.Combine(Path.GetTempPath(), $"evasion-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ this is not json");
            EvasionSettings read = EvasionSettingsStore.Load(path);
            Assert.False(read.WarnOrDefault.Enabled);
            Assert.False(read.ActOrDefault.Enabled);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheHintScannerSuggestsWithoutChoosing()
    {
        // It must find the plausible lines and say what they map to - and it must not pretend
        // to know which one is right. There is no "the answer is" in its output because nobody
        // has established what this game calls the dodge roll.
        IReadOnlyList<DodgeKeyHint> hints = DodgeKeyHints.Parse(
        [
            "[ACTION_KEYS]",
            "use_flask_in_slot1=49",
            "Input_dodge_roll=32",
            "dash_key=DIK_Q",
            "; a comment mentioning dodge",
            "unrelated=1",
        ]);

        Assert.Equal(2, hints.Count);
        Assert.Contains(hints, h => h.Setting == "Input_dodge_roll" && h.Key == 0x20);
        Assert.Contains(hints, h => h.Setting == "dash_key" && h.Key == (ushort)'Q');
        Assert.All(hints, h => Assert.Equal("ACTION_KEYS", h.Section));
        Assert.Contains("Space", hints.First(h => h.Key == 0x20).Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void AMouseBoundDodgeIsShownAsUnusableRatherThanAsAKey()
    {
        // The same safety the flask keys keep: a binding this cannot send must be visible as
        // one, never quietly turned into a near-miss keystroke.
        DodgeKeyHint hint = Assert.Single(DodgeKeyHints.Parse(["dodge_roll=1"]));
        Assert.Equal(0, hint.Key);
        Assert.Contains("not a key this can send", hint.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingConfigYieldsNoHintsRatherThanThrowing()
        => Assert.Empty(DodgeKeyHints.Find(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.ini")));

    [Fact]
    public void TheMovementHintsAreNarrowerThanTheDodgeOnes()
    {
        // Steering holds movement keys, so those get the same "here are the candidate lines,
        // pick one yourself" treatment - but with a tighter word list. Matching a bare "up"
        // would pull in the wheel bindings and half the interface, and a hint list that long is
        // one nobody reads.
        string[] config =
        [
            "[ACTION_KEYS]",
            "Input_move_up=87",
            "Input_move_left=65",
            "Input_ui_scroll_up=0",
            "Input_dodge_roll=32",
        ];

        string file = Path.Combine(Path.GetTempPath(), $"poe2-{Guid.NewGuid():N}.ini");
        try
        {
            File.WriteAllLines(file, config);

            IReadOnlyList<DodgeKeyHint> movement = DodgeKeyHints.FindMovement(file);
            Assert.Equal(2, movement.Count);
            Assert.Contains(movement, h => h.Setting == "Input_move_up" && h.Key == 0x57);
            Assert.DoesNotContain(movement, h => h.Setting.Contains("scroll", StringComparison.Ordinal));
            Assert.DoesNotContain(movement, h => h.Setting.Contains("dodge", StringComparison.Ordinal));

            // And the dodge list keeps to itself. "roll" is a substring of "SCROLL", so this
            // also pins the word boundary that stops every wheel binding in the game turning up
            // beside the one line somebody is looking for.
            DodgeKeyHint dodge = Assert.Single(DodgeKeyHints.Find(file));
            Assert.Equal("Input_dodge_roll", dodge.Setting);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void SteeringNeedsAllFourMovementKeys()
    {
        // Three of four would silently remove three of the eight directions, and the tool would
        // then roll the best of what was left with nothing to say a better one was never tried.
        Assert.True(MovementKeys.Default.IsComplete);
        Assert.False((MovementKeys.Default with { Down = 0 }).IsComplete);

        var settings = new EvasionSettings(Steer: true, DodgeKey: 0x20);
        Assert.True(settings.CanSteer);
        Assert.False((settings with { DodgeKey = 0 }).CanSteer);
        Assert.False((settings with { Steer = false }).CanSteer);
        Assert.False((settings with { Keys = MovementKeys.Default with { Up = 0 } }).CanSteer);
    }

    [Fact]
    public void ADirectionMapsToTheKeysThatMakeIt()
    {
        MovementKeys keys = MovementKeys.Default;

        Assert.Equal([0x57], keys.KeysFor(MoveDirection.Up));
        Assert.Equal([0x57, 0x44], keys.KeysFor(MoveDirection.Up | MoveDirection.Right));
        Assert.Empty(keys.KeysFor(MoveDirection.None));
        Assert.Equal(8, MovementKeys.Compass.Count);
        Assert.Equal(8, MovementKeys.Compass.Distinct().Count());
    }

    [Fact]
    public void TheSteeringNumbersAreClampedToWhatCanBeUsed()
    {
        EvasionSettings wild = new EvasionSettings(
            Steer: true, RollDistance: 99_999f, SteerHoldMs: 10_000).Normalised();

        Assert.Equal(2000f, wild.RollDistance);
        Assert.Equal(500, wild.SteerHoldMs);

        // Zero is a legal hold - "send the keys and release at once" - so it must survive
        // normalising rather than being rounded up to a default.
        Assert.Equal(0, (new EvasionSettings(SteerHoldMs: 0)).Normalised().SteerHoldMs);

        // A roll of nothing would make every direction score the same and the steering would
        // take whichever came first.
        Assert.Equal(50f, (new EvasionSettings(RollDistance: 1f)).Normalised().RollDistance);
    }
}
