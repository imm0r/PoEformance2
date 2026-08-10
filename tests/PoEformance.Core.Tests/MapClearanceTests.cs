using PoEformance.Core.Memory;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// One walk of the recorded map, so the tests below can share it.
/// </summary>
/// <remarks>
/// Replaying 6,878 frames takes about ten seconds, which is fine once and not fine three
/// times: the same walk answers every question here, so it happens in a class fixture and
/// the tests read the findings off it.
/// </remarks>
public sealed class MapWalk
{
    public const int LastFrame = 6878;

    /// <summary>The last thirteen seconds, where the map is cleared and being left.</summary>
    public const int TailFrom = 6600;

    public MapWalk()
    {
        var replay = ReplayMemoryReader.Load(File.OpenRead(RealSessionTests.MapFixturePath));
        var world = new WorldReader(replay, RealSessionTests.Schema());
        ulong gameStates = replay.ResolvedStatics["GameStates"];

        var firstSeen = new Dictionary<uint, int>();

        for (int frame = 0; frame <= LastFrame; frame++)
        {
            replay.Seek((uint)frame);
            WorldSnapshot snapshot = world.Read(gameStates);

            UntargetableSightings += snapshot.Corpses.Untargetable;
            Collapsed += snapshot.Collapsed;

            if (snapshot.Corpses.Tracking > MostOnTheClock)
            {
                MostOnTheClock = snapshot.Corpses.Tracking;
                MostOnTheClockAt = frame;
            }

            if (!snapshot.InGame)
            {
                continue;
            }

            var hostile = snapshot.Entities
                .Where(e => e.Kind == EntityKind.Monster && !e.IsFriendly && !e.IsEffect)
                .ToList();

            MonsterSightings += hostile.Count;

            if (frame >= TailFrom)
            {
                Tail.Add((frame, snapshot.Entities.Count, hostile.Count,
                    string.Join(", ", hostile.Take(5).Select(m => $"{m.FileName} at {m.Life.Percent}%"))));
            }

            var here = new HashSet<uint>();
            foreach (WorldEntity monster in hostile)
            {
                here.Add(monster.Id);
                if (!firstSeen.TryGetValue(monster.Id, out int first))
                {
                    firstSeen[monster.Id] = first = frame;
                }

                if (frame - first > LongestStay)
                {
                    LongestStay = frame - first;
                    LongestStayWas = $"{monster.FileName} at {monster.Life.Percent}%";
                }
            }

            foreach (uint id in firstSeen.Keys.Where(id => !here.Contains(id)).ToList())
            {
                firstSeen.Remove(id);
            }
        }
    }

    /// <summary>Most monsters on the untargetable clock at any one moment.</summary>
    public int MostOnTheClock { get; }

    public int MostOnTheClockAt { get; }

    /// <summary>Every untargetable reading the corpse check made, added up.</summary>
    public int UntargetableSightings { get; }

    /// <summary>Repeat entities dropped for sharing a monster's Render component.</summary>
    public int Collapsed { get; }

    /// <summary>Hostile, non-effect monsters shown, added up over every frame.</summary>
    public int MonsterSightings { get; }

    /// <summary>The longest any one monster stayed on the overlay, in frames.</summary>
    public int LongestStay { get; }

    public string LongestStayWas { get; } = string.Empty;

    /// <summary>Frame, entities, hostile monsters and their names, over the cleared tail.</summary>
    public List<(int Frame, int Entities, int Hostile, string Names)> Tail { get; } = [];
}

/// <summary>
/// What the overlay shows over a whole map, and what it stops showing.
/// </summary>
/// <remarks>
/// The question these settle is the last one left from the dots-on-cleared-ground hunt: does
/// the corpse check recognise a monster's death at the right moment, or does the screen keep
/// its markers? Measured over the recorded map rather than argued about:
///
///   - Of 72 monsters that were hurt and then left the overlay, only THREE were last seen at
///     zero health. Sixteen went between 1% and 10%, thirty-six between 11% and 50%. The game
///     takes a killed monster out of its entity list before the next reading at 21 Hz, so the
///     corpse check is not the path by which most kills leave the screen - it is the residue
///     it exists for.
///   - That residue is real: 1,518 untargetable sightings over 1,150 frames, which is roughly
///     190 monsters passing through the state the check is timing. Without it those are dots.
///   - The longest any monster stayed on the overlay was 22 seconds, and the map ends with
///     nothing hostile on it at all.
///
/// So there was nothing to fix, and these are here to notice if that changes.
///
/// One thing the recording cannot answer: it holds no duplicate entities whatsoever, so
/// nothing here exercises the collapsing or the identity the corpse timer is keyed on. See
/// <see cref="TheCorpseClockNeverPilesUp"/>.
/// </remarks>
public class MapClearanceTests(MapWalk map) : IClassFixture<MapWalk>
{
    /// <summary>The untargetable clock does not pile up.</summary>
    /// <remarks>
    /// A clock that never completes is what dots on cleared ground look like from the inside:
    /// the check times a monster from its first untargetable reading, and if that timer keeps
    /// restarting it never reaches its threshold, nothing is recognised as a corpse, and the
    /// entries accumulate. Over this map it never held more than six monsters at once.
    ///
    /// WHAT THIS DOES NOT TEST, checked rather than assumed. The restart that caused the
    /// original shadows came from keying the timer on the entity address instead of the
    /// Render component - the game wears several entities on one monster, and which of them
    /// the map walk reaches first shifts as the tree rebalances. Swapping the key back and
    /// re-running this leaves it green, because THIS RECORDING CONTAINS NO DUPLICATES AT ALL:
    /// 72,034 monster sightings over the map, not one of them collapsed. With one entity per
    /// monster the two keys are the same key.
    ///
    /// So the duplicate collapsing and the identity it depends on are still only covered by
    /// the synthetic tests, and a recording of whatever situation produces duplicates - it
    /// was twelve entities for four monsters when it was first seen - would be worth having.
    /// </remarks>
    [Fact]
    public void TheCorpseClockNeverPilesUp()
    {
        // Generous against the six it actually reaches, because the number depends on how
        // many things die at once; a clock that is not completing does not stop at 30.
        Assert.True(map.MostOnTheClock <= 30,
            $"{map.MostOnTheClock} monsters were on the untargetable clock at once, at frame {map.MostOnTheClockAt}");

        // And the clock has something to do - a version of this that timed nothing would
        // pass the assertion above for the wrong reason.
        Assert.True(map.UntargetableSightings > 500,
            $"only {map.UntargetableSightings} untargetable sightings, so the check is barely running");

        // The recording's own limit, stated rather than left to be rediscovered.
        Assert.Equal(0, map.Collapsed);
    }

    /// <summary>A cleared map ends with nothing hostile drawn on it.</summary>
    /// <remarks>
    /// End to end, and deliberately not a test OF the corpse check: by the last seconds of
    /// this map there is almost nothing left for it to judge, so it would pass with the check
    /// switched off. What it pins is the whole chain - path classification, the duplicate
    /// collapsing, the effect rules and the corpse check together - producing an empty screen
    /// on ground that is empty. That is the thing anybody actually complains about.
    /// </remarks>
    [Fact]
    public void TheEndOfAClearedMapHasNoMonstersLeftOnIt()
    {
        Assert.NotEmpty(map.Tail);

        foreach ((int frame, int entities, int hostile, string names) in map.Tail)
        {
            Assert.True(hostile == 0, $"frame {frame} still shows {hostile} hostile monsters: {names}");

            // Not an empty world, though - the test would pass on a snapshot that read
            // nothing at all, and that is the failure it is least able to notice.
            Assert.True(entities > 10, $"frame {frame} read only {entities} entities, so nothing was read at all");
        }
    }

    /// <summary>Nothing stays on the overlay for the length of a map.</summary>
    /// <remarks>
    /// The other shape a shadow takes: not a screen full of them, but one marker that never
    /// goes. The longest-lived monster in this recording stayed 472 frames, about 22 seconds,
    /// which is a pack waiting in a room somebody walked past twice.
    /// </remarks>
    [Fact]
    public void NoMonsterOutstaysTheMap()
    {
        Assert.True(map.LongestStay < 1500, $"{map.LongestStayWas} stayed for {map.LongestStay} frames");

        // The walk saw monsters at all, so the assertion above is about something.
        Assert.True(map.MonsterSightings > 1000, $"only {map.MonsterSightings} monster sightings in the whole map");
    }
}
