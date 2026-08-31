using System.Text.Json;
using System.Text.Json.Serialization;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Features;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// The config window's serializer settings, mirrored so a wire name can be checked here.
/// </summary>
/// <remarks>
/// A COPY OF ConfigJsonContext's OPTIONS, and the missing naming policy is the whole point - see
/// BuffWireContext, which exists for the same reason and caught the same bug first.
/// </remarks>
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(SeenGround))]
internal sealed partial class GroundWireContext : JsonSerializerContext;

/// <summary>
/// Remembering what dangerous-looking ground a session has walked past.
/// </summary>
/// <remarks>
/// This exists because a ground rule matches a METADATA PATH, which is written nowhere a player
/// can see and differs per skill and per league mechanic. Typing one from memory is how the
/// feature ends up drawing nothing; picking one off a list of what the game just showed you is
/// the workflow that works.
///
/// The behavioural half of this file runs against the recorded session rather than against
/// hand-made entities, so what it proves is that the watch lists the paths a real area
/// contains - the part a synthetic snapshot cannot say anything about.
/// </remarks>
public class GroundWatchTests
{
    private static string Fixture(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "tests", "fixtures", name);
    }

    /// <summary>A watch that has looked at the whole sweep capture, and the snapshots it saw.</summary>
    private static (GroundWatch Watch, List<WorldSnapshot> Snapshots) OverTheCapture(uint step = 25)
    {
        using var replay = ReplayMemoryReader.Load(File.OpenRead(Fixture("session-2026-08-sweep.rec")));
        OffsetSchema schema = RealSessionTests.Schema();
        var world = new WorldReader(replay, schema);
        ulong gameStates = replay.ResolvedStatics["GameStates"];

        var watch = new GroundWatch();
        var seen = new List<WorldSnapshot>();
        for (uint frame = 0; frame < replay.FrameCount; frame += step)
        {
            replay.Seek(frame);
            WorldSnapshot snapshot = world.Read(gameStates);
            seen.Add(snapshot);

            // The frame's own timestamp, not a counter: the expiry is in wall-clock
            // milliseconds and a counter would make an hour of capture look like 150 ms.
            watch.Look(snapshot, replay.FrameTimes[(int)frame]);
        }

        return (watch, seen);
    }

    private static WorldSnapshot Area(params WorldEntity[] entities)
        => new(InGame: true, Player: null, Entities: entities, Matrix: new float[16]);

    private static WorldEntity Ground(string path, bool component = true, float radius = 0f)
        => new(1, 0x1000, path, EntityKind.Effect, 0, 0, 0,
            IsEffect: true, IsGroundEffect: component,
            GroundRadius: radius > 0 ? radius : null);

    // ── Against the recorded session ───────────────────────────────────────

    [Fact]
    public void ListsTheGroundEffectsTheCaptureActuallyContains()
    {
        (GroundWatch watch, _) = OverTheCapture();

        // The capture is a map with ground effects in it - HazardReadingTests reads their
        // countdowns out of the same file - so a watch that ran over all of it and remembered
        // nothing would mean the filter rejects the very things it exists to collect.
        Assert.NotEmpty(watch.Seen);

        // At least one of them is the game's own tagged kind. This is the row that tells a
        // person "no rule needed here", so a list where nothing is ever tagged would send
        // everybody to write rules for ground that is already ringed.
        Assert.Contains(watch.Seen, g => g.HasComponent);
    }

    [Fact]
    public void TheTaggedOnesAreTheOnesTheGameTagged()
    {
        (GroundWatch watch, List<WorldSnapshot> snapshots) = OverTheCapture();

        // Not a restatement of the flag: taken from the SNAPSHOTS, so it says the collapse from
        // entities to paths did not smear one path's component onto another. Two paths carry
        // the component in this capture and the GroundOnDeath monster mods carry none, and a
        // person deciding which rules to write is reading exactly this column.
        var tagged = snapshots
            .SelectMany(s => s.Entities)
            .Where(e => e.IsGroundEffect)
            .Select(e => e.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.NotEmpty(tagged);
        foreach (SeenGround row in watch.Seen)
        {
            Assert.Equal(tagged.Contains(row.Path), row.HasComponent);
        }
    }

    [Fact]
    public void CollapsesManyPatchesOfOneKindIntoOneRow()
    {
        (GroundWatch watch, List<WorldSnapshot> snapshots) = OverTheCapture();

        // What makes this a dropdown rather than a log. The capture holds far more ground
        // entities than kinds of ground, and a list keyed by entity would offer the same path
        // dozens of times with nothing to choose between the copies.
        int entities = snapshots
            .SelectMany(s => s.Entities)
            .Count(e => e.IsGroundEffect);

        Assert.True(entities > watch.Seen.Count,
            $"{entities} ground entities collapsed to {watch.Seen.Count} rows - no collapsing happened");

        // And the high-water mark survived the collapse, which is the column that says whether
        // a rule on this path will ring one patch or forty.
        Assert.All(watch.Seen, row => Assert.True(row.Most >= 1));
    }

    [Fact]
    public void CarriesTheCandidateRadiusWhereTheGameGaveOne()
    {
        (GroundWatch watch, _) = OverTheCapture();

        // Only where the component supplied one. A radius invented for a path that never had
        // one would be a number somebody sizes a rule against, and it would be made up.
        foreach (SeenGround row in watch.Seen)
        {
            if (row.Radius != 0)
            {
                Assert.True(row.HasComponent,
                    $"{row.Path} carries a radius but no component - it can only have come from another path");
                Assert.InRange(row.Radius, 0f, 500f);
            }
        }
    }

    // ── The list's own rules ───────────────────────────────────────────────

    [Fact]
    public void EveryFieldGoesOverTheWireUnderTheNameThePageReads()
    {
        // The bug SeenBuff shipped with, which this record was written to avoid: the config
        // window's serializer sets no naming policy, so a record that forgets its JSON names
        // crosses as "Path"/"HasComponent" while the page reads path/hasComponent. Nothing
        // throws - the dropdown arrives with the right number of rows, every one of them
        // reading "undefined", and clicking one writes the word "undefined" into a rule.
        string json = JsonSerializer.Serialize(
            new SeenGround("Metadata/Effects/Spells/ground_effects/fire", true, false, 4, 18.67f, 1234),
            GroundWireContext.Default.SeenGround);

        using JsonDocument document = JsonDocument.Parse(json);
        var keys = document.RootElement.EnumerateObject().Select(p => p.Name).ToList();

        // Every name ui/js/app.js reads off a row.
        foreach (string expected in
                 (string[])["path", "hasComponent", "onScreen", "most", "radius", "lastSeenMs"])
        {
            Assert.Contains(expected, keys);
        }

        Assert.DoesNotContain(keys, key => char.IsUpper(key[0]));
    }

    [Fact]
    public void SaysWhetherAnyoneHasLookedYet()
    {
        // "Nobody has looked" and "there is nothing there" are different answers and the
        // dropdown looks identical under both. It matters more here than for buffs: an empty
        // list is the normal state in a hideout and the alarming state in a map.
        var watch = new GroundWatch();
        Assert.Contains("not looked", watch.LastRead, StringComparison.OrdinalIgnoreCase);

        watch.Look(Area(), 0);
        Assert.DoesNotContain("not looked", watch.LastRead, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ALoadingScreenIsNotAnEmptyArea()
    {
        // A snapshot taken between areas has no entities because there is no area, not because
        // the area is clear - and clearing the list on one would empty the dropdown every time
        // somebody took a portal to go and write the rules.
        var watch = new GroundWatch();
        watch.Look(Area(Ground("Metadata/Effects/Spells/ground_effects/fire")), 0);
        watch.Look(new WorldSnapshot(false, null, [], new float[16]), 100);

        Assert.Single(watch.Seen);
    }

    [Fact]
    public void GroundThatBurnedOutStopsClaimingToBeOnScreen()
    {
        // Ground effects run out. A row that stayed marked present forever would put "here now"
        // beside every kind the session ever met, which is worse than not saying it at all.
        var watch = new GroundWatch();
        watch.Look(Area(Ground("Metadata/Effects/Spells/ground_effects/fire")), 0);
        Assert.True(watch.Seen[0].OnScreen);

        watch.Look(Area(), 100);
        Assert.False(watch.Seen[0].OnScreen);
    }

    [Fact]
    public void PresentOnesComeFirst()
    {
        // The one somebody is standing in is the one they are about to write a rule about.
        var watch = new GroundWatch();
        watch.Look(Area(Ground("old/ground")), 0);
        watch.Look(Area(Ground("new/ground")), 5000);

        Assert.Equal("new/ground", watch.Seen[0].Path);
        Assert.True(watch.Seen[0].OnScreen);
    }

    [Fact]
    public void OnceTaggedAlwaysTagged()
    {
        // A frame where the component read failed must not un-mark a whole path in the list
        // somebody is reading to decide whether a rule is needed. An entity of a kind that
        // carries a component always does, so the flag only ever goes one way.
        var watch = new GroundWatch();
        watch.Look(Area(Ground("Metadata/Effects/Spells/ground_effects/fire")), 0);
        watch.Look(Area(Ground("Metadata/Effects/Spells/ground_effects/fire", component: false)), 100);

        Assert.True(watch.Seen[0].HasComponent);
    }

    [Fact]
    public void KeepsTheHighWaterMarkRatherThanTheLastCount()
    {
        // How many turn up AT ONCE is what says whether a rule will ring one patch or forty,
        // and the frame that shows forty is not the frame somebody happens to alt-tab on.
        var watch = new GroundWatch();
        watch.Look(Area(
            Ground("a/ground") with { Address = 1 },
            Ground("a/ground") with { Address = 2 },
            Ground("a/ground") with { Address = 3 }), 0);
        watch.Look(Area(Ground("a/ground")), 100);

        Assert.Equal(3, watch.Seen[0].Most);
    }

    [Fact]
    public void ListsTheUntaggedGroundThatOnlyARuleCanMark()
    {
        // The reason the filter is wider than "carries a component". The GroundOnDeath monster
        // mods leave burning, shocked and chilled ground, carry no component and are not
        // classified as effects - so a list of tagged ground only would be a list of things
        // already ringed. Note the caveat the next test states: with the noise filter on, an
        // entity of this path never reaches a snapshot in the first place.
        var watch = new GroundWatch();
        watch.Look(Area(new WorldEntity(
            1, 0x2000,
            "Metadata/Monsters/MonsterMods/GroundOnDeath/BurningGroundDaemonParent@75",
            EntityKind.Monster, 0, 0, 0)), 0);

        Assert.Single(watch.Seen);
        Assert.False(watch.Seen[0].HasComponent);
    }

    [Fact]
    public void TheReaderNeverOffersDaemonCarriedGroundWhileTheFilterIsOn()
    {
        // NOT A TEST OF THIS CLASS - a test of the limit it lives under, pinned here because it
        // is invisible from the panel and expensive to rediscover. The burning ground a rare
        // monster leaves behind hangs off an invisible entity under Metadata/Monsters/
        // MonsterMods/..., NoiseFilter's Daemon class matches "monstermods", and WorldReader
        // drops it before a snapshot exists. So the dropdown cannot offer one - and, the part
        // that actually costs somebody an evening, neither can a GroundDangerRule typed against
        // such a path ever fire. If this test ever goes red, both of those became possible and
        // the remarks on GroundWatch and this feature's section in docs/architecture.md are the
        // things to correct.
        var filter = new NoiseFilter();

        Assert.True(filter.IsNoise("Metadata/Monsters/MonsterMods/GroundOnDeath/BurningGroundDaemonParent"));
        Assert.Equal(NoiseKind.Daemon, filter.Explain("Metadata/Monsters/MonsterMods/GroundOnDeath/BurningGroundDaemonParent"));

        // And the shipped default rule's own path is NOT caught, which is why that one works.
        Assert.False(filter.IsNoise("Metadata/Effects/Spells/ground_effects/VisibleServerGroundEffect"));

        // Turning the class off is the switch that makes those paths reachable, and it is what
        // the list's third clause is kept for.
        filter.Set(NoiseKind.Daemon, false);
        Assert.False(filter.IsNoise("Metadata/Monsters/MonsterMods/GroundOnDeath/BurningGroundDaemonParent"));
    }

    [Fact]
    public void YourOwnGroundEffectsAreNotOffered()
    {
        // A rule cannot fire on a friendly entity - GroundDangerRule.Matches refuses them,
        // because your own burning ground is under your own feet and ringing it covers the
        // screen at the moment it is needed. Offering one here would offer a rule that does
        // nothing, which is the worst kind of list entry: it looks like it worked.
        var watch = new GroundWatch();
        watch.Look(Area(Ground("mine/ground") with { IsFriendly = true }), 0);

        Assert.Empty(watch.Seen);
    }

    [Fact]
    public void RememberedGroundIsNotOffered()
    {
        // A remembered entity is one the reader is drawing from memory because the game stopped
        // listing it. For ground that means it has burned out, so listing it as present would
        // put "here now" on a patch that is gone.
        var watch = new GroundWatch();
        watch.Look(Area(Ground("gone/ground") with { RememberedForMs = 500 }), 0);

        Assert.Empty(watch.Seen);
    }

    [Fact]
    public void ForgetsWhatHasNotBeenSeenForAges()
    {
        var watch = new GroundWatch();
        watch.Look(Area(Ground("old/ground")), 0);
        watch.Look(Area(), GroundWatch.RememberMs + 1);

        Assert.Empty(watch.Seen);
    }

    [Fact]
    public void GroundInTheAreaIsNeverForgotten()
    {
        // The bound and the expiry are both allowed to drop rows, and neither may drop one that
        // is under the player's feet right now.
        var watch = new GroundWatch();
        watch.Look(Area(Ground("here/ground")), 0);
        watch.Look(Area(Ground("here/ground")), GroundWatch.RememberMs * 4);

        Assert.Single(watch.Seen);
    }

    [Fact]
    public void IsBoundedHoweverManyPathsTurnUp()
    {
        // A long session through many leagues must not grow this without limit - it is polled
        // once a second and serialized whole on every poll.
        var watch = new GroundWatch();
        for (int i = 0; i < GroundWatch.MaxRemembered * 3; i++)
        {
            watch.Look(Area(Ground($"ground/kind{i}")), i);
        }

        Assert.True(watch.Seen.Count <= GroundWatch.MaxRemembered);
    }
}
