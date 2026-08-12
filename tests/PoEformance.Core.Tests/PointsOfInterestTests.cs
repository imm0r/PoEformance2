using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Recognising the places worth walking to, from an entity's metadata path.
/// </summary>
public class PointsOfInterestTests
{
    [Theory]
    // Exits under their own folder, which is the easy half.
    [InlineData("Metadata/Terrain/Gallows/Act1/AreaTransitions/AreaTransition_Animate", PoiKind.AreaTransition)]
    // ...and the half that a folder check silently loses. This exact path is why the AHK tool
    // matches the keyword rather than the folder: Lightless Passage's real exit lives under
    // Objects/, so anything narrower classifies it as scenery and the way out disappears.
    [InlineData("Metadata/Terrain/Gallows/Act2/2_5/Objects/LightlessPassageTransition", PoiKind.AreaTransition)]
    [InlineData("Metadata/MiscellaneousObjects/Checkpoints/Checkpoint_Endgame_Boss", PoiKind.Checkpoint)]
    [InlineData("Metadata/Terrain/Waypoints/Waypoint", PoiKind.Waypoint)]
    [InlineData("Metadata/MiscellaneousObjects/Shrines/ShrineOfPower", PoiKind.Shrine)]
    [InlineData("Metadata/Chests/StrongBoxes/Arcanist", PoiKind.Chest)]
    [InlineData("Metadata/MiscellaneousObjects/Expedition/ExpeditionMarker", PoiKind.Mechanic)]
    [InlineData("Metadata/Terrain/Leagues/Ritual/RitualRuneObject", PoiKind.Mechanic)]
    // A person is a destination too, and in campaign content usually THE destination: what a
    // quest wants is most often an exit or somebody to talk to.
    [InlineData("Metadata/NPC/Act1/Renly", PoiKind.Npc)]
    public void ThePathSaysWhatAPlaceIs(string path, PoiKind expected)
        => Assert.Equal(expected, PointsOfInterest.Classify(path));

    [Theory]
    // Living things carry league names in their paths constantly, and marking every Delirium
    // monster as an encounter would bury the altar that is the actual point. Taken from a real
    // recorded session, not invented.
    [InlineData("Metadata/Monsters/LeagueDelirium/DeliriumMinion1@70")]
    [InlineData("Metadata/Monsters/LeagueDelirium/Minions/DeliriumEliteDisgust@70")]
    [InlineData("Metadata/Characters/Int/IntFourb")]
    [InlineData("Metadata/Pet/BetaKiwis/FaridunKiwi")]
    [InlineData("Metadata/MiscellaneousObjects/WorldItem")]
    [InlineData("Metadata/Terrain/Gallows/Act1/Rocks/Rock_01")]
    [InlineData("Metadata/Chests/Boxes/Barrel")]
    public void OrdinaryThingsAreNotPlaces(string path)
        => Assert.Equal(PoiKind.None, PointsOfInterest.Classify(path));

    [Fact]
    public void TheMonsterGuardComesBeforeTheKeywords()
    {
        // Order, not coincidence: a monster path CAN contain a landmark keyword, and if the
        // keyword were checked first every one of them would become a marker.
        Assert.Equal(PoiKind.None, PointsOfInterest.Classify("Metadata/Monsters/Ritual/RitualDemon"));
        Assert.Equal(PoiKind.Mechanic, PointsOfInterest.Classify("Metadata/Terrain/Leagues/Ritual/RitualRuneObject"));
    }

    [Theory]
    [InlineData("Metadata/Terrain/Gallows/Act1/AreaTransitions/AreaTransition_Animate_02", "Area Transition Animate")]
    [InlineData("Metadata/Terrain/Gallows/Act2/2_5/Objects/LightlessPassageTransition", "Lightless Passage Transition")]
    [InlineData("Metadata/Chests/StrongBoxes/Arcanist", "Arcanist")]
    [InlineData("Metadata/MiscellaneousObjects/Checkpoints/Checkpoint_Endgame_Boss", "Checkpoint Endgame Boss")]
    public void TheNameIsReadableRatherThanThePath(string path, string expected)
        => Assert.Equal(expected, PointsOfInterest.Name(path, PointsOfInterest.Classify(path)));

    [Theory]
    // The game's own names, from MinimapIcons.dat. This is the game answering the question
    // rather than this guessing at it, so it decides.
    [InlineData("Waypoint", PoiKind.Waypoint)]
    [InlineData("QuestObject", PoiKind.Quest)]
    [InlineData("AreaTransition", PoiKind.AreaTransition)]
    [InlineData("RewardChestExpedition", PoiKind.Chest)]
    [InlineData("NPCMaster", PoiKind.Npc)]
    // Unrecognised is NOT unimportant: the game marked it, so there is something there. The
    // names run to dozens and change every league, so anything else would go stale.
    [InlineData("SomeIconNobodyHasSeenYet", PoiKind.Marked)]
    public void TheGamesOwnMapIconDecides(string icon, PoiKind expected)
        => Assert.Equal(expected, PointsOfInterest.Classify("Metadata/Terrain/Whatever/Thing", icon));

    [Fact]
    public void AQuestObjectiveIsUnrecognisableFromItsPathAndObviousFromTheIcon()
    {
        // The case the icon exists for. Nothing in "Brazier_03" says a quest wants it, and no
        // list of path keywords could - it is a brazier. The game marking it says everything.
        const string path = "Metadata/Terrain/Act1/Objects/Brazier_03";

        Assert.Equal(PoiKind.None, PointsOfInterest.Classify(path));
        Assert.Equal(PoiKind.Quest, PointsOfInterest.Classify(path, "QuestObject"));
    }

    [Fact]
    public void WithoutAnIconThePathRulesStillApply()
    {
        // The fallback has to keep working: the game does not mark everything worth walking
        // to, and an exit it does not icon is still an exit.
        Assert.Equal(
            PoiKind.AreaTransition,
            PointsOfInterest.Classify("Metadata/Terrain/Gallows/Act2/2_5/Objects/LightlessPassageTransition", ""));
    }

    [Fact]
    public void TheIconsOwnNameIsTheLabel()
    {
        // What the GAME calls the marker, only spaced out. Rewording it would be replacing
        // information with an opinion.
        Assert.Equal("Quest Object", PointsOfInterest.Readable("QuestObject"));
        Assert.Equal("Reward Chest Expedition", PointsOfInterest.Readable("RewardChestExpedition"));
    }

    [Theory]
    // The one that was missing: a league mechanic's reward chest, carrying no MinimapIcon
    // component at all, so the path is the only thing that could have caught it.
    [InlineData("Metadata/Chests/LeagueIncursion/EncounterChest", PoiKind.Chest)]
    [InlineData("Metadata/Chests/StrongBoxes/ArmourerStrongboxHigh", PoiKind.Chest)]
    // ...and the thousands that must stay out, from the same recording.
    [InlineData("Metadata/Chests/TraitorsPassagePot_01", PoiKind.None)]
    [InlineData("Metadata/Chests/DryRuinPots/DryRuinsUrn_03", PoiKind.None)]
    public void OnlyChestsWorthWalkingToCount(string path, PoiKind expected)
        => Assert.Equal(expected, PointsOfInterest.Classify(path, ""));

    [Fact]
    public void TheGamesOwnIconSaysWhenAMechanicIsDoneWith()
    {
        // An abyss that has been run leaves its whole trail marked, and the game flips every
        // marker to Inactive. Same answer as a looted chest's own byte.
        var run = new WorldEntity(
            1, 0x1000, "Metadata/MiscellaneousObjects/Abyss/AbyssFinalNodeBase",
            EntityKind.Unknown, 0f, 0f, 0f, Poi: PoiKind.Mechanic, MapIcon: "AbyssPitInactive");

        Assert.True(run.IsSpent);

        // The counter-example that keeps this from being a rule about "not active": a
        // checkpoint you have NOT used is the one most worth walking to.
        var unused = new WorldEntity(
            2, 0x2000, "Metadata/MiscellaneousObjects/Checkpoints/Checkpoint_Endgame",
            EntityKind.Unknown, 0f, 0f, 0f, Poi: PoiKind.Checkpoint, MapIcon: "CheckpointNotActive");

        Assert.False(unused.IsSpent);
    }

    [Fact]
    public void APlaceTheGameSaysIsNotThereIsNotAPlace()
    {
        // The quest NPC that started this: awake, positioned, carrying the game's own NPC
        // minimap icon, and not in the area at all until somebody takes the quest.
        var absent = new WorldEntity(
            1, 0x1000, "Metadata/NPC/Hideout/JusticeOracleIdentifier/AbyssJusticeOracleIdentifierWild",
            EntityKind.Npc, 100f, 100f, 0f, Poi: PoiKind.Npc, Present: false);

        Assert.Equal(PoiKind.Npc, absent.Poi);
        Assert.False(absent.IsPlace);
    }

    [Fact]
    public void APlaceNobodyCouldJudgeIsStillDrawn()
    {
        // The let-out, and the reason it is not the other way round: most exits and
        // checkpoints carry no Targetable component at all, so an unknown that hid them
        // would empty the map of exactly the markers people navigate by.
        var unjudged = new WorldEntity(
            2, 0x2000, "Metadata/MiscellaneousObjects/Checkpoints/Checkpoint_Endgame",
            EntityKind.Unknown, 100f, 100f, 0f, Poi: PoiKind.Checkpoint);

        Assert.Null(unjudged.Present);
        Assert.True(unjudged.IsPlace);
    }

    [Fact]
    public void ANameThatWouldSayNothingFallsBackToTheKind()
    {
        // Trailing variant numbers are stripped, so a path can reduce to nothing at all - and
        // a blank label on a map is worse than a generic one.
        Assert.Equal("Area Transition", PointsOfInterest.Name("Metadata/Terrain/Transitions/_01", PoiKind.AreaTransition));
    }
}
