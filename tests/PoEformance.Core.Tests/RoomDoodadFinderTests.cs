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
            Assert.Equal(5, place.Entities.Count);
            Assert.Equal(place.Entities.Order(), place.Entities);
        });

        // Each place stands on its own five entities, and the two share none.
        Assert.Equal(0, found.Places[0].Shared(found.Places[1]));
        Assert.Equal(5, found.Places[0].Shared(found.Places[0]));
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

        // One more taken away: three of five is under two thirds, and the room has no place - and says
        // how near the nearest tile came.
        sightings.RemoveAt(1);
        RoomDoodadPlaces less = RoomDoodadFinder.Find(Lines, Width, Height, sightings, 40, 40);
        Assert.Empty(less.Places);
        Assert.StartsWith("no tile collects two thirds of its props - the most, 3 of 4 needed, at tile ", less.Why, StringComparison.Ordinal);
        Assert.Equal(5, less.Props);
    }

    /// <summary>
    /// The props decide and the scripted objects only count: a room whose power lines are lifted keeps its place on its props, with the lines reported beside them and no ring for one; a room with too few props is placed by every line.
    /// </summary>
    [Fact]
    public void THEPROPSDecideAndTheScriptedObjectsOnlyCount()
    {
        const string PowerLine = "Metadata/Terrain/Gallows/Act3/3_6_2/Objects/GlyphPowerLine";
        RoomDoodad[] lines =
        [
            new(10, 5, 0f, 1f, "Metadata/Doodads/Pillar_01.ao", Plain),
            new(50, 20, 0f, 1f, "Metadata/Doodads/Pillar_02.ao", Plain),
            new(80, 60, 0f, 1f, "Metadata/Doodads/Crate_01.ao", Plain),
            new(30, 65, 0f, 1f, "Metadata/Doodads/Brazier_01.ao", Plain),
            new(12, 40, 0f, 1f, "Metadata/Doodads/Rail.ao", PowerLine),
            new(35, 40, 0f, 1f, "Metadata/Doodads/Rail.ao", PowerLine),
            new(58, 40, 0f, 1f, "Metadata/Doodads/Rail.ao", PowerLine),
            new(81, 40, 0f, 1f, "Metadata/Doodads/Rail.ao", PowerLine),
        ];
        Assert.Equal([true, true, true, true, false, false, false, false], lines.Select(line => line.IsProp));

        // Every prop stands, one rail of four: four of four props is a place; four of eight lines would not have been.
        var sightings = new List<DoodadSighting>();
        uint id = 1;
        foreach (RoomDoodad line in lines[..5])
        {
            sightings.Add(At(line, 6, 4, turn: 2, id++));
        }

        // The other rails stand elsewhere in the area, so their lines are matchable.
        sightings.Add(At(lines[5], 30, 30, turn: 0, id++));
        RoomDoodadPlaces found = RoomDoodadFinder.Find(lines, Width, Height, sightings, 60, 60);
        Assert.Equal((8, 8, 4), (found.Lines, found.Matchable, found.Props));
        RoomDoodadPlace place = Assert.Single(found.Places);
        Assert.Equal((6, 4, 2, 5, 8, 4, 4), (place.Where.X, place.Where.Y, place.Where.Turn, place.Hits, place.Lines, place.PropHits, place.Props));
        Assert.Empty(place.Missing);

        // One prop standing elsewhere instead: three of four props is still a place, and the prop missing
        // here gets its ring - the rails none. (Taken out of the area altogether it would not be matchable,
        // and a line that stands nowhere is not counted against the room.)
        sightings[0] = At(lines[0], 40, 40, turn: 0, 99);
        RoomDoodadPlace fewer = Assert.Single(RoomDoodadFinder.Find(lines, Width, Height, sightings, 60, 60).Places);
        Assert.Equal((4, 3, 4), (fewer.Hits, fewer.PropHits, fewer.Props));
        Assert.Equal(Expected(lines[0], 6, 4, turn: 2), Assert.Single(fewer.Missing));

        // Two props elsewhere: two of four is under two thirds, however many rails stand.
        sightings[1] = At(lines[1], 40, 40, turn: 0, 98);
        RoomDoodadPlaces none = RoomDoodadFinder.Find(lines, Width, Height, sightings, 60, 60);
        Assert.Empty(none.Places);
        Assert.StartsWith("no tile collects two thirds of its props - the most, 2 of 3 needed", none.Why, StringComparison.Ordinal);

        // A room of two props and four rails has too few props to decide by: every line counts, as before.
        RoomDoodad[] railRoom = [lines[0], lines[1], lines[4], lines[5], lines[6], lines[7]];
        var rails = new List<DoodadSighting>();
        id = 1;
        foreach (RoomDoodad line in railRoom[..5])
        {
            rails.Add(At(line, 6, 4, turn: 2, id++));
        }

        RoomDoodadPlaces byAll = RoomDoodadFinder.Find(railRoom, Width, Height, rails, 60, 60);
        Assert.Equal(2, byAll.Props);
        // Six lines matchable - the sixth rail by the other rails' entities, which carry its model - five hit, the sixth ringed: every line counts here.
        RoomDoodadPlace all = Assert.Single(byAll.Places);
        Assert.Equal((5, 6, 2, 2), (all.Hits, all.Lines, all.PropHits, all.Props));
        Assert.Single(all.Missing);
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

    /// <summary>
    /// Where two rooms' places stand on the same tiles and the same entities, the one with more of its doodads there keeps them; rims may still meet; and a room standing inside another on doodads of its own keeps its place.
    /// </summary>
    [Fact]
    public void SETTLINGGivesDoodadsToTheRoomWithMoreOfThemAndLetsRoomsOnTheirOwnDoodadsStand()
    {
        static RoomDoodadPlace Place(int x, int y, int wide, int tall, int hits, int lines, int firstEntity)
            => new(new RoomCandidate(x, y, 0, wide, tall, hits, lines), hits, lines, 1f, []) { Entities = [.. Enumerable.Range(firstEntity, hits)] };
        static RoomDoodadPlaces Of(params RoomDoodadPlace[] places)
            => new(places, places.Max(place => place.Lines), places.Max(place => place.Lines), places.Max(place => place.Lines), string.Empty);

        (string Room, RoomDoodadPlaces Places)[] rooms =
        [
            // A variant not laid: only its shared lines are hit, at the tile where the laid one stands - on the laid one's entities.
            ("grass.arm", Of(Place(5, 5, 4, 4, 72, 83, firstEntity: 0))),

            // The laid one: every line it has is hit, on the same tile.
            ("desert.arm", Of(Place(5, 5, 4, 4, 72, 72, firstEntity: 0))),

            // A wall laid twice, each sharing one rim row or column with the desert's rim - rooms that join.
            ("wall.arm", Of(Place(8, 5, 4, 4, 20, 20, firstEntity: 100), Place(5, 8, 4, 4, 20, 20, firstEntity: 120))),

            // A small room inside the desert's footprint on doodads of its own - The Assembly's Expedition encounter - stands.
            ("inside.arm", Of(Place(6, 6, 2, 2, 14, 14, firstEntity: 200))),

            // And a small variant inside the desert's footprint on the desert's own doodads is the desert.
            ("part.arm", Of(Place(6, 6, 2, 2, 30, 30, firstEntity: 10))),
        ];

        List<(string Room, RoomDoodadPlaces Places)> settled = RoomDoodadFinder.Settle(rooms, 20, 20);

        Assert.Equal(rooms.Select(one => one.Room), settled.Select(one => one.Room));
        Assert.Single(settled[1].Places.Places);
        Assert.Empty(settled[1].Places.Yielded);
        Assert.Empty(settled[0].Places.Places);
        Assert.Equal("desert.arm", Assert.Single(settled[0].Places.Yielded).To);
        Assert.Equal("every place it found stands on another room's doodads", settled[0].Places.Why);
        Assert.Equal(2, settled[2].Places.Places.Count);
        Assert.Single(settled[3].Places.Places);
        Assert.Empty(settled[3].Places.Yielded);
        Assert.Equal("desert.arm", Assert.Single(settled[4].Places.Yielded).To);
    }

    /// <summary>
    /// A variant whose one extra line is a scripted object outside the network bubble loses nothing for it: the props tie, the tiles decide; with the object present, its hit decides for the variant; and a room with more props standing keeps the doodads over one with more lines of any kind.
    /// </summary>
    [Fact]
    public void SETTLINGGoesByThePropsFirstSoAnAbsentScriptedObjectCostsItsVariantNothing()
    {
        // One place at the one tile: its props' entities are the first of the sightings, the scripted objects' the rest.
        static RoomDoodadPlaces Of(int hits, int lines, int propHits, int props, int tilesAgree, params int[] scripted)
            => new(
                [new RoomDoodadPlace(new RoomCandidate(5, 5, 0, 4, 4, hits, lines), hits, lines, 1f, [])
                {
                    TilesAgree = tilesAgree,
                    PropHits = propHits,
                    Props = props,
                    Entities = [.. Enumerable.Range(0, propHits), .. scripted],
                }],
                lines, lines, lines, string.Empty);

        // The checkpoint outside the bubble: 27 of 28 against the plain room's 27 of 27, the same 27 props - the tiles say the checkpoint room.
        List<(string Room, RoomDoodadPlaces Places)> absent = RoomDoodadFinder.Settle(
            [("workshop_wall_cc_01.arm", Of(27, 27, 27, 27, tilesAgree: 54)), ("checkpoint_wall_cc_01.arm", Of(27, 28, 27, 27, tilesAgree: 60))], 20, 20);
        Assert.Single(absent[1].Places.Places);
        Assert.Equal("checkpoint_wall_cc_01.arm", Assert.Single(absent[0].Places.Yielded).To);

        // The tiles tied as well: the plain room, every line of it present, and the same way every time.
        List<(string Room, RoomDoodadPlaces Places)> tied = RoomDoodadFinder.Settle(
            [("checkpoint_wall_cc_01.arm", Of(27, 28, 27, 27, tilesAgree: 54)), ("workshop_wall_cc_01.arm", Of(27, 27, 27, 27, tilesAgree: 54))], 20, 20);
        Assert.Single(tied[1].Places.Places);
        Assert.Equal("workshop_wall_cc_01.arm", Assert.Single(tied[0].Places.Yielded).To);

        // The checkpoint inside the bubble: its hit decides for its room, the props and tiles tied.
        List<(string Room, RoomDoodadPlaces Places)> present = RoomDoodadFinder.Settle(
            [("workshop_ledge_cc_01.arm", Of(18, 18, 18, 18, tilesAgree: 70)), ("checkpoint_ledge_cc_01.arm", Of(19, 19, 18, 18, tilesAgree: 70, 177))], 20, 20);
        Assert.Single(present[1].Places.Places);
        Assert.Equal("checkpoint_ledge_cc_01.arm", Assert.Single(present[0].Places.Yielded).To);

        // More hits of any kind do not outrank more props: a variant with two props gone and six scripted
        // objects present stands on the plain room's doodads and yields to it.
        List<(string Room, RoomDoodadPlaces Places)> props = RoomDoodadFinder.Settle(
            [("busy.arm", Of(14, 16, 8, 10, tilesAgree: 30, 100, 101, 102, 103, 104, 105)), ("plain.arm", Of(10, 10, 10, 10, tilesAgree: 30))], 20, 20);
        Assert.Single(props[1].Places.Places);
        Assert.Equal("plain.arm", Assert.Single(props[0].Places.Yielded).To);
    }

    /// <summary>Two variants with every doodad in common, every one standing: the tiles laid at the place decide, and the name only after that.</summary>
    [Fact]
    public void VARIANTSSharingEveryDoodadAreToldApartByTheirTiles()
    {
        static RoomDoodadPlaces Of(int tilesAgree)
            => new([new RoomDoodadPlace(new RoomCandidate(5, 5, 0, 4, 4, 97, 97), 97, 97, 1f, []) { TilesAgree = tilesAgree, Entities = [.. Enumerable.Range(0, 97)] }], 97, 97, 97, string.Empty);

        List<(string Room, RoomDoodadPlaces Places)> settled = RoomDoodadFinder.Settle([("a_3open.arm", Of(40)), ("b_4open.arm", Of(44))], 20, 20);
        Assert.Empty(settled[0].Places.Places);
        Assert.Equal("b_4open.arm", Assert.Single(settled[0].Places.Yielded).To);
        Assert.Single(settled[1].Places.Places);

        // Unlisted by the tile search on both: the name decides, and the same way every time.
        List<(string Room, RoomDoodadPlaces Places)> unlisted = RoomDoodadFinder.Settle([("b_4open.arm", Of(-1)), ("a_3open.arm", Of(-1))], 20, 20);
        Assert.Single(unlisted[1].Places.Places);
        Assert.Equal("a_3open.arm", Assert.Single(unlisted[0].Places.Yielded).To);
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
