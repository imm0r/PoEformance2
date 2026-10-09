using System.Numerics;
using PoEformance.Game.Files;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>Where a room's doodads say it stands - every place, by the entities of its path and model.</summary>
public class RoomDoodadFinderTests
{
    private const string Plain = "Metadata/MiscellaneousObjects/Doodad";
    private const int Width = 4;
    private const int Height = 3;

    /// <summary>A four by three room: five doodads of one plain stub and five models, and a line that names no stub.</summary>
    private static readonly RoomDoodad[] Lines =
    [
        new(10, 5, 0f, 1f, "Metadata/Doodads/Pillar_01.ao", Plain),
        new(50, 20, 0f, 1f, "Metadata/Doodads/Pillar_02.ao", Plain),
        new(80, 60, 0f, 1f, "Metadata/Doodads/Crate_01.ao", Plain),
        new(30, 65, 0f, 1f, "Metadata/Doodads/Brazier_01.ao", Plain),
        new(70, 10, 0f, 1f, "Metadata/Doodads/Banner_01.ao", Plain),
        new(0, 0, 0f, 1f, "Metadata/Doodads/Nothing.ao", string.Empty),
    ];

    /// <summary>A room standing twice is found twice, a copy of two doodads is no place, and an entity of another model does not vote.</summary>
    [Fact]
    public void AROOMStandingTwiceIsFoundTwiceAndAPartialCopyIsNot()
    {
        var sightings = new List<DoodadSighting>();
        uint id = 1;
        foreach (RoomDoodad line in Lines[..5])
        {
            sightings.Add(At(line, 5, 7, turn: 1, id++));
            sightings.Add(At(line, 20, 3, turn: 0, id++));
        }

        sightings.Add(At(Lines[0], 30, 30, turn: 2, id++));
        sightings.Add(At(Lines[1], 30, 30, turn: 2, id++));
        sightings.Add(At(Lines[2], 12, 12, turn: 0, id++, model: "Metadata/Doodads/Other.ao"));
        sightings.Add(new DoodadSighting(id, "Metadata/Monsters/Something", string.Empty, 100f, 100f, 0f, true));

        RoomDoodadPlaces found = RoomDoodadFinder.Find(Lines, Width, Height, sightings, 60, 60);

        Assert.Equal((5, 5, 5, string.Empty), (found.Lines, found.Matchable, found.ByModel, found.Why));
        Assert.Equal(2, found.Places.Count);
        Assert.All(found.Places, place =>
        {
            Assert.Equal((5, 5), (place.Hits, place.Lines));
            Assert.Empty(place.Missing);
            Assert.InRange(place.MeanOff, 0f, 10f);
        });
        Assert.Equal(
            [(20, 3, 0, 4, 3), (5, 7, 1, 3, 4)],
            found.Places.Select(place => (place.Where.X, place.Where.Y, place.Where.Turn, place.Where.Width, place.Where.Height)).OrderBy(place => place.Turn));
    }

    /// <summary>An entity whose model chain did not read matches by its path alone, and a line without its entity is marked where it would stand.</summary>
    [Fact]
    public void ANEntityWithoutAModelMatchesByPathAndAMissingLineIsMarked()
    {
        var sightings = new List<DoodadSighting>();
        uint id = 1;
        foreach (RoomDoodad line in Lines[..4])
        {
            sightings.Add(At(line, 8, 2, turn: 3, id++, model: string.Empty));
        }

        RoomDoodadPlaces found = RoomDoodadFinder.Find(Lines, Width, Height, sightings, 40, 40);

        // With no model to say otherwise, every plain-path entity is a candidate of the fifth line too - so it is
        // matchable, and at the place it is the one line without an entity, marked where it would stand.
        Assert.Equal((5, 5, 0), (found.Lines, found.Matchable, found.ByModel));
        RoomDoodadPlace place = Assert.Single(found.Places);
        Assert.Equal((8, 2, 3, 4, 5), (place.Where.X, place.Where.Y, place.Where.Turn, place.Hits, place.Lines));
        Assert.Equal(Expected(Lines[4], 8, 2, turn: 3), Assert.Single(place.Missing));

        // One more taken away: three of five is under two thirds, and the room has no place.
        sightings.RemoveAt(1);
        RoomDoodadPlaces less = RoomDoodadFinder.Find(Lines, Width, Height, sightings, 40, 40);
        Assert.Empty(less.Places);
        Assert.Equal("no tile collects two thirds of its doodads", less.Why);
    }

    /// <summary>Too few of its doodads in the area, or none, is said rather than voted on.</summary>
    [Fact]
    public void TOOFewDoodadsInTheAreaIsSaidRatherThanVotedOn()
    {
        DoodadSighting[] two = [At(Lines[0], 1, 1, turn: 0, 1), At(Lines[1], 1, 1, turn: 0, 2)];
        RoomDoodadPlaces few = RoomDoodadFinder.Find(Lines, Width, Height, two, 40, 40);
        Assert.Empty(few.Places);
        Assert.Equal((5, 2), (few.Lines, few.Matchable));
        Assert.StartsWith("only 2 of its doodads", few.Why, StringComparison.Ordinal);

        RoomDoodadPlaces none = RoomDoodadFinder.Find(Lines, Width, Height, [], 40, 40);
        Assert.Equal("none of its doodads stands in the area", none.Why);

        RoomDoodadPlaces unnamed = RoomDoodadFinder.Find([Lines[5]], Width, Height, two, 40, 40);
        Assert.Equal("its doodad lines name no stub", unnamed.Why);
    }

    /// <summary>Where a line's doodad stands when the room is laid with its corner at a tile, one of the eight ways round.</summary>
    private static Vector2 Expected(RoomDoodad line, int cornerX, int cornerY, int turn)
    {
        Vector2 at = Vector2.Transform(new Vector2(line.X, line.Y) / TerrainGrid.CellsPerTile, RoomFinder.Laying(Width, Height, turn));
        return new Vector2(cornerX + at.X, cornerY + at.Y) * RoomDoodadFinder.WithinUnits;
    }

    /// <summary>The entity a line becomes with the room laid there - a few units off, as a real one stands off its cell's corner.</summary>
    private static DoodadSighting At(RoomDoodad line, int cornerX, int cornerY, int turn, uint id, string? model = null)
    {
        Vector2 at = Expected(line, cornerX, cornerY, turn);
        return new DoodadSighting(id, line.Stub, model ?? line.Ao, at.X + 3f, at.Y - 2f, -10f, true);
    }
}
