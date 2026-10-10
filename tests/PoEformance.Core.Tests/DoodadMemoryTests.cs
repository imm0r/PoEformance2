using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// The scripted objects of the rooms the frame's read has listed in an instance, kept by id for the placing - see DoodadMemory.
/// </summary>
public class DoodadMemoryTests
{
    private const string Checkpoint = "Metadata/MiscellaneousObjects/Checkpoints/Checkpoint_Endgame";
    private const string PowerLine = "Metadata/Terrain/Gallows/Act3/3_6_2/Objects/GlyphPowerLine";
    private const string Plain = "Metadata/MiscellaneousObjects/Doodad";

    private static readonly HashSet<string> Stubs = new([Checkpoint, PowerLine, Plain], StringComparer.OrdinalIgnoreCase);

    /// <summary>A scripted object the rooms name is kept where it first stood; a prop, a monster and a path no room names are not; the same id twice is one sighting.</summary>
    [Fact]
    public void THEBUBBLESScriptedObjectsAreKeptByIdAndNothingElseIs()
    {
        var memory = new DoodadMemory();
        WorldEntity[] frame =
        [
            Entity(177, Checkpoint, 10309.78f, 10461.96f, -370f),
            Entity(3221225588, Plain, 100f, 100f, 0f),
            Entity(500, "Metadata/Monsters/VaalBossStatue/VaalBossStatue", 200f, 200f, 0f),
            Entity(901, PowerLine, 300f, 300f, -10f),
        ];

        Assert.Equal(2, memory.Notice(0xD8B5907D, frame, Stubs));
        Assert.Equal(2, memory.Count);
        Assert.Equal(1, memory.Version);

        // The same frame again, the checkpoint moved: nothing new, and the first place stands.
        Assert.Equal(0, memory.Notice(0xD8B5907D, [Entity(177, Checkpoint, 0f, 0f, 0f)], Stubs));
        Assert.Equal(1, memory.Version);
        DoodadSighting kept = Assert.Single(memory.Held(), one => one.Id == 177);
        Assert.Equal((Checkpoint, string.Empty, 10309.78f, 10461.96f, false, true), (kept.Path, kept.Model, kept.X, kept.Y, kept.Asleep, kept.Remembered));
    }

    /// <summary>A new instance clears what was held; a frame that read no area changes nothing.</summary>
    [Fact]
    public void ANEWInstanceForgetsAndANoughtHashDoesNot()
    {
        var memory = new DoodadMemory();
        memory.Notice(1, [Entity(177, Checkpoint, 1f, 1f, 0f)], Stubs);
        memory.Notice(0, [Entity(178, Checkpoint, 2f, 2f, 0f)], Stubs);
        Assert.Equal(1, memory.Count);

        memory.Notice(2, [Entity(178, Checkpoint, 2f, 2f, 0f)], Stubs);
        Assert.Equal(178u, Assert.Single(memory.Held()).Id);
    }

    /// <summary>Merging appends what the survey does not hold, the survey's own sightings first and untouched, and hands the survey's list back where there is nothing to add.</summary>
    [Fact]
    public void MERGINGAppendsOnlyWhatTheSurveyLacks()
    {
        DoodadSighting[] found =
        [
            new(3221225588, Plain, "Metadata/Doodads/Pot.ao", 1f, 1f, 0f, true),
            new(177, Checkpoint, "Metadata/Terrain/Doodads/Checkpoints/VaalCheckpoint.ao", 2f, 2f, 0f, true),
        ];
        DoodadSighting[] remembered =
        [
            new(177, Checkpoint, string.Empty, 9f, 9f, 0f, false) { Remembered = true },
            new(901, PowerLine, string.Empty, 3f, 3f, 0f, false) { Remembered = true },
        ];

        IReadOnlyList<DoodadSighting> merged = DoodadMemory.Merged(found, remembered);
        Assert.Equal([3221225588u, 177u, 901u], merged.Select(one => one.Id));
        Assert.Equal("Metadata/Terrain/Doodads/Checkpoints/VaalCheckpoint.ao", merged[1].Model);
        Assert.True(merged[2].Remembered);

        Assert.Same(found, DoodadMemory.Merged(found, []));
        Assert.Same(found, DoodadMemory.Merged(found, [remembered[0]]));
    }

    private static WorldEntity Entity(uint id, string path, float x, float y, float z)
        => new(id, 0x1000 + id, path, EntityKind.Terrain, x, y, z);
}
