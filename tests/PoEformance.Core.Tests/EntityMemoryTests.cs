using PoEformance.Core.Memory;
using PoEformance.Game.Components;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// The rules that decide what survives the game's entity list dropping it.
/// </summary>
/// <remarks>
/// Synthetic entities here, on purpose: these are questions about the RULE, and a rule is
/// easiest to be sure of when the input says exactly one thing. What the rule does against
/// real memory is the other half and lives in <see cref="MapMemoryTests"/>, which walks a
/// recorded map and checks the answers against what the game itself did.
/// </remarks>
public class EntityMemoryTests
{
    private const uint Area = 0x3FA91659;

    private static WorldEntity Place(uint id, float x, float y, PoiKind poi = PoiKind.Chest)
        => new(id, 0x1000 + id, "Metadata/Chests/StrongboxDivination", EntityKind.Chest,
            x, y, 0f, Poi: poi, Render: 0x2000 + id);

    private static WorldEntity Monster(uint id, float x, float y)
        => new(id, 0x1000 + id, "Metadata/Monsters/Skeleton", EntityKind.Monster,
            x, y, 0f, Render: 0x2000 + id);

    private static WorldEntity Player(float x, float y)
        => new(1, 0x900, "Metadata/Characters/Player", EntityKind.Player, x, y, 0f);

    /// <summary>A distance well outside anything the game keeps listing.</summary>
    private static float FarAway => (EntityMemory.ForgetWithinCells + 200f) * MapView.WorldToGrid;

    [Fact]
    public void APlaceTheGameStopsListing_IsKeptAndSaysHowLongAgo()
    {
        var memory = new EntityMemory();
        WorldEntity chest = Place(7, 0f, 0f);

        Assert.Empty(memory.Update(Area, [chest], Player(0f, 0f), 1_000));

        // Walked away, and the game has stopped listing it.
        IReadOnlyList<WorldEntity> kept = memory.Update(Area, [], Player(FarAway, 0f), 4_000);

        WorldEntity remembered = Assert.Single(kept);
        Assert.Equal(chest.Id, remembered.Id);
        Assert.Equal(chest.WorldX, remembered.WorldX);
        Assert.True(remembered.IsRemembered);
        Assert.Equal(3_000, remembered.RememberedForMs);
    }

    [Fact]
    public void AThingThatVanishesUnderfoot_IsGoneForGood()
    {
        var memory = new EntityMemory();
        WorldEntity chest = Place(7, 0f, 0f);

        memory.Update(Area, [chest], Player(0f, 0f), 1_000);

        // Opened, picked up, taken by somebody else - whatever it was, it happened where the
        // player could see it, so the absence is the answer rather than a gap in the reading.
        Assert.Empty(memory.Update(Area, [], Player(30f, 30f), 2_000));

        // And it does not come back once the player walks off again.
        Assert.Empty(memory.Update(Area, [], Player(FarAway, 0f), 3_000));
    }

    [Fact]
    public void AMonsterIsNeverRemembered()
    {
        var memory = new EntityMemory();

        memory.Update(Area, [Monster(7, 0f, 0f)], Player(0f, 0f), 1_000);

        // The whole reason the memory is narrow: a monster moves, so a remembered dot would be
        // a claim about where something is that is wrong the moment it is drawn.
        Assert.Empty(memory.Update(Area, [], Player(FarAway, 0f), 2_000));
    }

    [Fact]
    public void OrdinaryContainersAreNotPlacesAndAreNotKept()
    {
        var memory = new EntityMemory();
        var pot = new WorldEntity(
            7, 0x1007, "Metadata/Chests/DryRuinPots/DryRuinPot1", EntityKind.Chest,
            0f, 0f, 0f, Render: 0x2007);

        Assert.Equal(PoiKind.None, PointsOfInterest.Classify(pot.Path));

        memory.Update(Area, [pot], Player(0f, 0f), 1_000);
        Assert.Empty(memory.Update(Area, [], Player(FarAway, 0f), 2_000));
    }

    [Fact]
    public void ADropIsKept_BecauseTheFloorDoesNotMove()
    {
        var memory = new EntityMemory();
        var drop = new WorldEntity(
            7, 0x1007, "Metadata/MiscellaneousObjects/WorldItem", EntityKind.WorldItem,
            0f, 0f, 0f, Rarity: ItemRarity.Rare, Render: 0x2007);

        memory.Update(Area, [drop], Player(0f, 0f), 1_000);

        WorldEntity kept = Assert.Single(memory.Update(Area, [], Player(FarAway, 0f), 2_000));
        Assert.Equal(ItemRarity.Rare, kept.Rarity);
    }

    [Fact]
    public void WhatTheGameSaysNow_ReplacesWhatItSaidBefore()
    {
        var memory = new EntityMemory();
        WorldEntity shut = Place(7, 0f, 0f) with { Opened = false };

        memory.Update(Area, [shut], Player(0f, 0f), 1_000);
        memory.Update(Area, [shut with { Opened = true }], Player(0f, 0f), 2_000);

        // The sighting is the LATEST reading of it, not the first: a chest remembered as still
        // shut would send somebody back across a map for nothing.
        WorldEntity kept = Assert.Single(memory.Update(Area, [], Player(FarAway, 0f), 3_000));
        Assert.True(kept.Opened);
        Assert.True(kept.IsSpent);
    }

    [Fact]
    public void AListedEntityIsNeverAlsoRemembered()
    {
        var memory = new EntityMemory();
        WorldEntity chest = Place(7, 0f, 0f);

        memory.Update(Area, [chest], Player(0f, 0f), 1_000);
        memory.Update(Area, [], Player(FarAway, 0f), 2_000);

        // Back in range. One marker, from the game, and no ghost beside it.
        Assert.Empty(memory.Update(Area, [chest], Player(0f, 0f), 3_000));
    }

    [Fact]
    public void TheSameObjectUnderANewId_ReplacesTheSighting()
    {
        var memory = new EntityMemory();
        WorldEntity chest = Place(7, 0f, 0f);

        memory.Update(Area, [chest], Player(0f, 0f), 1_000);
        memory.Update(Area, [], Player(FarAway, 0f), 2_000);

        // Same Render component, a different entity id and a different address - which is how
        // the game hands the same object back. Without the Render check this would draw a
        // second marker on the same spot.
        WorldEntity again = chest with { Id = 99, Address = 0x5555 };
        Assert.Empty(memory.Update(Area, [again], Player(FarAway, 0f), 3_000));
    }

    [Fact]
    public void LeavingTheArea_ForgetsEverything()
    {
        var memory = new EntityMemory();

        memory.Update(Area, [Place(7, 0f, 0f)], Player(0f, 0f), 1_000);

        // Ids are handed out again in the next area and its positions are somewhere else, so
        // a sighting carried across would be a marker on an unrelated spot.
        Assert.Empty(memory.Update(0x11112222, [], Player(FarAway, 0f), 2_000));
        Assert.Equal(0, memory.Count);
    }

    [Fact]
    public void WithNoPlayer_NothingIsForgotten()
    {
        var memory = new EntityMemory();
        WorldEntity chest = Place(7, 0f, 0f);

        memory.Update(Area, [chest], Player(0f, 0f), 1_000);

        // A frame that could not resolve the player cannot apply the up-close rule, and
        // guessing would throw away a landmark on the strength of one bad read.
        Assert.Single(memory.Update(Area, [], null, 2_000));
    }

    [Fact]
    public void TurnedOff_ItKeepsNothingAndHoldsNothing()
    {
        var memory = new EntityMemory { Enabled = true };
        memory.Update(Area, [Place(7, 0f, 0f)], Player(0f, 0f), 1_000);

        memory.Enabled = false;
        Assert.Empty(memory.Update(Area, [], Player(FarAway, 0f), 2_000));
        Assert.Equal(0, memory.Count);

        // Back on, and it starts from what the game is saying rather than from what it said
        // before the switch was thrown.
        memory.Enabled = true;
        Assert.Empty(memory.Update(Area, [], Player(FarAway, 0f), 3_000));
    }

    [Fact]
    public void PastTheCap_TheFarthestGiveWay()
    {
        var memory = new EntityMemory();
        var listed = new List<WorldEntity>();

        // One more than fits, spread out along a line away from the player.
        for (uint id = 1; id <= EntityMemory.MostRemembered + 1; id++)
        {
            listed.Add(Place(id, id * MapView.WorldToGrid, 0f));
        }

        memory.Update(Area, listed, Player(0f, 0f), 1_000);
        Assert.Equal(EntityMemory.MostRemembered, memory.Count);

        // The one dropped is the far end of the line, not the near one.
        IReadOnlyList<WorldEntity> kept = memory.Update(
            Area, [], Player(-FarAway, 0f), 2_000);

        Assert.DoesNotContain(kept, entity => entity.Id == EntityMemory.MostRemembered + 1);
        Assert.Contains(kept, entity => entity.Id == 1);
    }
}

/// <summary>
/// One walk of the recorded map with the memory running, so the tests below can share it.
/// </summary>
/// <remarks>
/// Replaying the whole map takes about twenty seconds, which is worth paying once. Everything
/// here is a question the RECORDING settles and a synthetic test cannot: how far away the game
/// really stops listing things, and whether the rule that decides "gone" rather than "out of
/// range" ever throws away something that was still there.
/// </remarks>
public sealed class MemoryWalk
{
    public MemoryWalk()
    {
        var replay = ReplayMemoryReader.Load(File.OpenRead(RealSessionTests.MapFixturePath));
        var world = new WorldReader(replay, RealSessionTests.Schema());
        ulong gameStates = replay.ResolvedStatics["GameStates"];

        var held = new Dictionary<uint, string>();
        var listedOn = new Dictionary<uint, List<int>>();

        for (int frame = 0; frame <= LastFrame; frame++)
        {
            replay.Seek((uint)frame);
            WorldSnapshot snapshot = world.Read(gameStates);
            if (!snapshot.InGame)
            {
                continue;
            }

            var listedNow = new HashSet<uint>();
            foreach (WorldEntity entity in snapshot.Listed)
            {
                listedNow.Add(entity.Id);
                if (!EntityMemory.WorthRemembering(entity))
                {
                    continue;
                }

                if (!listedOn.TryGetValue(entity.Id, out List<int>? frames))
                {
                    listedOn[entity.Id] = frames = [];
                }

                frames.Add(frame);
                Standing.Add(entity.Id);
            }

            var rememberedNow = new Dictionary<uint, string>();
            foreach (WorldEntity entity in snapshot.Entities.Where(e => e.IsRemembered))
            {
                rememberedNow[entity.Id] = $"{entity.Kind}/{entity.PoiName}";
                Assert.NotEqual(EntityKind.Monster, entity.Kind);
                Assert.NotEqual(EntityKind.Effect, entity.Kind);
            }

            // Everything the memory dropped this frame, and whether the game ever listed it
            // again - which is the only way to be told the rule was wrong about it.
            foreach ((uint id, string what) in held)
            {
                if (rememberedNow.ContainsKey(id) || listedNow.Contains(id))
                {
                    continue;
                }

                Forgotten++;
                int next = listedOn.TryGetValue(id, out List<int>? frames)
                    ? frames.FirstOrDefault(seen => seen > frame, -1)
                    : -1;

                // Listed again later means it was still there when it was dropped. A gap of
                // one frame is a hole in the read rather than a mistake by the rule; anything
                // longer is a marker somebody lost.
                if (next > frame + 1)
                {
                    LostForLonger++;
                    Lost.Add($"{what} id {id} dropped at {frame}, listed again at {next}");
                }
            }

            held = rememberedNow;
            MostAtOnce = Math.Max(MostAtOnce, snapshot.Remembered);
            LastListed = snapshot.Entities.Count - snapshot.Remembered;
            LastRemembered = snapshot.Remembered;
            LastStandingListed = snapshot.Listed.Count(EntityMemory.WorthRemembering);
            LastStandingHeld = snapshot.Entities.Count(EntityMemory.WorthRemembering);
        }
    }

    public const int LastFrame = 6878;

    /// <summary>Every standing thing the game listed at any point in the map.</summary>
    public HashSet<uint> Standing { get; } = [];

    public int MostAtOnce { get; }
    public int Forgotten { get; }
    public int LostForLonger { get; }
    public List<string> Lost { get; } = [];

    /// <summary>What the last frame held: the game's own list, and the memory's addition.</summary>
    public int LastListed { get; }
    public int LastRemembered { get; }

    /// <summary>Standing things at the last frame - as the game lists them, and as held.</summary>
    public int LastStandingListed { get; }
    public int LastStandingHeld { get; }
}

/// <summary>What the memory does against a real recorded map.</summary>
public class MapMemoryTests(MemoryWalk walk) : IClassFixture<MemoryWalk>
{
    [Fact]
    public void TheMapEndsHoldingWhatTheGameHasStoppedListing()
    {
        // The measurement this feature exists for. Over that map the game listed 143 standing
        // things at one point or another; at the last frame it was listing 12 of them, and the
        // snapshot held 28. Everything else is behind the player and out of the bubble.
        Assert.True(walk.LastStandingListed < walk.LastStandingHeld,
            $"the game listed {walk.LastStandingListed} standing things at the end and the"
            + $" snapshot held {walk.LastStandingHeld}");

        Assert.True(walk.LastRemembered >= 10,
            $"only {walk.LastRemembered} remembered at the end of the map");
        Assert.True(walk.MostAtOnce >= 40, $"never held more than {walk.MostAtOnce} at once");
    }

    [Fact]
    public void TheUpCloseRuleHardlyEverThrowsAwaySomethingStillThere()
    {
        // 130 sightings were dropped over this map and NONE of them was a mistake that lasted:
        // every id the rule let go was either genuinely gone or listed again on the very next
        // frame, which is a hole in one read rather than a wrong decision.
        //
        // The number that makes that safe is the gap in the recording between the two ways a
        // thing can leave the list. Consumed under the player's feet: 1 to 26 cells, and one
        // outlier at 49 which was the one-frame hole. Out of range: nothing at all until 165
        // cells, and then hundreds of them. The threshold sits in the empty band between,
        // which is why it can be wrong in neither direction - see EntityMemory.
        Assert.True(walk.Forgotten > 50, $"the rule never fired: {walk.Forgotten} drops");
        Assert.True(walk.LostForLonger == 0,
            $"{walk.LostForLonger} sightings were dropped and listed again later:\n"
            + string.Join("\n", walk.Lost.Take(10)));
    }
}
