using System.Text.Json;
using System.Text.Json.Serialization;
using PoEformance.Features;
using PoEformance.Game.Components;

namespace PoEformance.Core.Tests;

/// <summary>
/// The config window's serializer settings, mirrored so a wire name can be checked here.
/// </summary>
/// <remarks>
/// A COPY OF ConfigJsonContext's OPTIONS, and the missing naming policy is the whole point:
/// the real context has none either, so every record spells its own JSON names and one that
/// forgets goes over in PascalCase. Camel-casing here would make this check pass whether the
/// attributes exist or not.
/// </remarks>
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(SeenBuff))]
internal sealed partial class BuffWireContext : JsonSerializerContext;

/// <summary>
/// Remembering which buffs a character has had on.
/// </summary>
/// <remarks>
/// This exists because the name a rule matches is not the name the game shows: a rule matches
/// the ENGINE identifier - "fire_wall" - where the game paints "Flame Wall". Both are read, so
/// somebody can find the buff by the name they recognise and end up with the id a rule needs;
/// matching stays on the id, which is the same on every client.
/// </remarks>
public class BuffWatchTests
{
    private static ActiveBuffs On(params string[] names)
        => new([.. names.Select(name => new ActiveBuff(name, 5f, 10f, 2, 0, false))]);

    [Fact]
    public void CarriesTheReadableNameBesideTheIdItMatches()
    {
        // The whole reason both are read. Nobody looking at "Flame Wall" on their own screen
        // would guess "fire_wall", and a rule that matched the readable one would stop working
        // the moment somebody changed their game's language - so it is shown, never matched.
        var watch = new BuffWatch();
        watch.Look(
            new ActiveBuffs([new ActiveBuff(
                "fire_wall", 6f, 8f, 0, 0, false, "Flame Wall", "Deals fire damage over time.")]),
            0);

        SeenBuff buff = Assert.Single(watch.Seen);
        Assert.Equal("fire_wall", buff.Name);
        Assert.Equal("Flame Wall", buff.DisplayName);
        Assert.Equal("Deals fire damage over time.", buff.Description);
    }

    [Fact]
    public void AReadableNameThatCouldNotBeReadIsSimplyAbsent()
    {
        // Its offset is COMPUTED rather than observed, so the id - which comes from an offset
        // that is proven - has to stand on its own when the other one does not resolve.
        var watch = new BuffWatch();
        watch.Look(On("fire_wall"), 0);

        SeenBuff buff = Assert.Single(watch.Seen);
        Assert.Equal("fire_wall", buff.Name);
        Assert.Equal(string.Empty, buff.DisplayName);
    }

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

    [Fact]
    public void EveryFieldGoesOverTheWireUnderTheNameThePageReads()
    {
        // The config window's serializer sets no naming policy - every record that crosses to
        // the page spells its own JSON names - and this one did not, so it went over as
        // "Name"/"Active" while the page read name/active. Nothing failed: the list arrived
        // with the right number of rows and every field in it undefined, so the picker showed
        // the word "undefined" as a buff name and wrote it into the field somebody clicked.
        //
        // Through a context with the real one's options - see BuffWireContext for why that
        // matters more than it looks.
        string json = JsonSerializer.Serialize(
            new SeenBuff("fire_wall", true, 4.5f, 2, 3, 1234, "Flame Wall", "Burns things."),
            BuffWireContext.Default.SeenBuff);

        using JsonDocument document = JsonDocument.Parse(json);
        var keys = document.RootElement.EnumerateObject().Select(p => p.Name).ToList();

        // Every name ui/js/rules.js reads off a buff.
        foreach (string expected in
                 (string[])["name", "active", "timeLeft", "charges", "flaskSlot", "displayName", "description"])
        {
            Assert.Contains(expected, keys);
        }

        Assert.DoesNotContain(keys, key => char.IsUpper(key[0]));
    }

    [Fact]
    public void APermanentBuffStillCrossesTheWire()
    {
        // A permanent buff - an aura, an ascendancy effect, most of what is on a character
        // standing in town - reports INFINITY as its remaining time, and JSON cannot say
        // infinity. One such value in the list killed EVERY state the config window asked
        // for: the page sat on its initial HTML and the rules looked deleted. The clamp is
        // huge rather than zero because the page shows no clock for anything huge, and a
        // buff that never runs out has no clock to show.
        var watch = new BuffWatch();
        watch.Look(new ActiveBuffs(
        [
            new ActiveBuff("player_aura_armour", float.PositiveInfinity, float.PositiveInfinity, 0, 0, false),
            new ActiveBuff("garbage_read", float.NaN, float.NegativeInfinity, 0, 0, false),
        ]), 0);

        string json = JsonSerializer.Serialize(watch.Seen[0], BuffWireContext.Default.SeenBuff);
        Assert.Contains("timeLeft", json, StringComparison.Ordinal);
        Assert.All(watch.Seen, buff => Assert.True(float.IsFinite(buff.TimeLeft)));
    }
}
