using System.Numerics;
using System.Text;
using PoEformance.Features;
using PoEformance.Game.Files;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// What the game's camera can see of a laid room, from every place the player can stand - and what a picture may therefore leave out.
/// </summary>
/// <remarks>
/// A CAMERA BUILT THE WAY THE GAME'S BEHAVES: a perspective standing back and above the player, up
/// being minus z, its matrix flattened into the 16 floats WorldToScreen reads. The scenes are ones
/// whose answers are known without the code - a floor and a box under it, a wall behind the camera.
/// </remarks>
public class CameraSightTests
{
    /// <summary>The area: eight tiles a side, 2000 world units.</summary>
    private const int Tiles = 8;

    /// <summary>Where the player stands when the matrix is read.</summary>
    private static readonly Vector3 Player = new(1000f, 1000f, 0f);

    /// <summary>A perspective 1000 units back and up from where it looks - minus z is up - as a row-vector matrix.</summary>
    private static Matrix4x4 Camera(Vector3 target)
    {
        Vector3 eye = target + new Vector3(0f, 600f, -800f);
        Matrix4x4 view = Matrix4x4.CreateLookAt(eye, target, -Vector3.UnitZ);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, 16f / 9f, 10f, 20000f);
        return view * projection;
    }

    /// <summary>The matrix as the game's memory holds it: clip x is a dot with flat[0], flat[4], flat[8], flat[12].</summary>
    private static float[] Flat(Matrix4x4 m) =>
    [
        m.M11, m.M12, m.M13, m.M14,
        m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34,
        m.M41, m.M42, m.M43, m.M44,
    ];

    /// <summary>A flat area, every cell walkable unless <paramref name="walkable"/> says otherwise.</summary>
    private static TerrainGrid Area(Func<int, int, bool>? walkable = null)
    {
        int cells = Tiles * TerrainGrid.CellsPerTile;
        int row = cells / 2;
        var packed = new byte[row * cells];
        for (var y = 0; y < cells; y++)
        {
            for (var x = 0; x < cells; x++)
            {
                if (walkable?.Invoke(x, y) ?? true)
                {
                    packed[(y * row) + (x / 2)] |= (byte)((x & 1) == 0 ? 0x01 : 0x10);
                }
            }
        }

        return new TerrainGrid(packed, row, cells, Tiles, Tiles, new float[Tiles * Tiles]);
    }

    /// <summary>A floor at z nought in squares of fifty units, two triangles each.</summary>
    private static void Floor(List<Vector3> places, List<int> indices)
    {
        const float side = 50f;
        var across = (int)(Tiles * 250f / side);
        for (var y = 0; y < across; y++)
        {
            for (var x = 0; x < across; x++)
            {
                int at = places.Count;
                places.Add(new Vector3(x * side, y * side, 0f));
                places.Add(new Vector3((x + 1) * side, y * side, 0f));
                places.Add(new Vector3(x * side, (y + 1) * side, 0f));
                places.Add(new Vector3((x + 1) * side, (y + 1) * side, 0f));
                indices.AddRange([at, at + 1, at + 2, at + 1, at + 3, at + 2]);
            }
        }
    }

    /// <summary>A box's twelve triangles.</summary>
    private static void Box(List<Vector3> places, List<int> indices, Vector3 least, Vector3 most)
    {
        int at = places.Count;
        for (var one = 0; one < 8; one++)
        {
            places.Add(new Vector3((one & 1) == 0 ? least.X : most.X, (one & 2) == 0 ? least.Y : most.Y, (one & 4) == 0 ? least.Z : most.Z));
        }

        int[] faces = [0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5, 0, 4, 5, 0, 5, 1, 2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3];
        indices.AddRange(faces.Select(one => one + at));
    }

    private static CameraSight Sight(TerrainGrid? grid = null, Vector2 origin = default)
    {
        CameraSight? sight = CameraSight.Over(
            new CameraShot(Flat(Camera(Player)), Player), grid ?? Area(), origin, new Vector3(-origin, 0f), new Vector3(new Vector2(Tiles * 250f) - origin, 0f), out string why);
        Assert.True(sight is not null, why);
        return sight;
    }

    [Fact]
    public void THESCREENSShapeIsReadOffTheMatrix()
        => Assert.Equal(16f / 9f, new CameraShot(Flat(Camera(Player)), Player).Aspect, 3);

    [Fact]
    public void ABOXUnderTheFloorIsHiddenAndOneOnItIsSeenAndTheWholeFloorIsSeen()
    {
        var places = new List<Vector3>();
        var indices = new List<int>();
        Floor(places, indices);
        int floor = indices.Count / 3;

        // DOWN IS PLUS Z: one box a hundred units under the floor, one standing on it.
        Box(places, indices, new Vector3(990f, 990f, 90f), new Vector3(1010f, 1010f, 110f));
        Box(places, indices, new Vector3(1200f, 990f, -40f), new Vector3(1220f, 1010f, 0f));
        int triangles = indices.Count / 3;

        CameraSight sight = Sight();
        Assert.InRange(sight.Views, 4, CameraSight.MostViews);
        bool[] seen = sight.Seen([.. places], [.. indices], Enumerable.Repeat(true, triangles).ToArray(), new bool[triangles], threads: 4);

        Assert.All(seen[..floor], Assert.True);
        Assert.All(seen[floor..(floor + 12)], Assert.False);
        Assert.Contains(true, seen[(floor + 12)..]);
    }

    [Fact]
    public void WHATISKeptIsKeptAndWhatHidesNothingDoesNotHide()
    {
        var places = new List<Vector3>();
        var indices = new List<int>();
        Floor(places, indices);
        int floor = indices.Count / 3;
        Box(places, indices, new Vector3(990f, 990f, 90f), new Vector3(1010f, 1010f, 110f));
        int triangles = indices.Count / 3;

        // KEPT - a shadow caster - stays though no view sees it.
        var kept = new bool[triangles];
        kept[floor] = kept[floor + 1] = true;
        var hides = Enumerable.Repeat(true, triangles).ToArray();
        bool[] seen = Sight().Seen([.. places], [.. indices], hides, kept, threads: 2);
        Assert.True(seen[floor] && seen[floor + 1]);
        Assert.False(seen[floor + 2]);

        // A FLOOR THAT HIDES NOTHING - a mixed ground layer - lets the box show.
        Array.Fill(hides, false);
        Assert.All(Sight().Seen([.. places], [.. indices], hides, new bool[triangles], threads: 2)[floor..], Assert.True);
    }

    [Fact]
    public void AWALLBehindTheCameraHidesNothingThoughItStandsBetweenTheCameraAndTheGroundAlongItsDirection()
    {
        // THE CAMERA LOOKS TOWARD MINUS Y FROM 600 UNITS BACK. A wall at y 1700, three thousand units
        // tall, lies along the camera's direction from all the ground behind it - drawn orthographically
        // it would hide everything up to 2250 units in front of it. From a player standing at the box
        // the camera is in front of the wall, so the box is seen.
        var places = new List<Vector3>();
        var indices = new List<int>();
        Floor(places, indices);
        int floor = indices.Count / 3;
        Box(places, indices, new Vector3(990f, 990f, -20f), new Vector3(1010f, 1010f, 0f));
        int box = indices.Count / 3;
        Box(places, indices, new Vector3(0f, 1700f, -3000f), new Vector3(2000f, 1710f, 0f));
        int triangles = indices.Count / 3;

        bool[] seen = Sight().Seen([.. places], [.. indices], Enumerable.Repeat(true, triangles).ToArray(), new bool[triangles], threads: 4);
        Assert.Contains(true, seen[floor..box]);
    }

    [Fact]
    public void ADOODADSBoxIsAskedAgainstTheTilesBeforeItIsLoaded()
    {
        var places = new List<Vector3>();
        var indices = new List<int>();
        Floor(places, indices);
        CameraSight sight = Sight();

        // NOTHING DRAWN YET: everything counts as seen.
        Assert.True(sight.BoxSeen(new Vector3(990f, 990f, 90f), new Vector3(1010f, 1010f, 110f)));

        sight.See([.. places], [.. indices], threads: 2);
        Assert.False(sight.BoxSeen(new Vector3(990f, 990f, 90f), new Vector3(1010f, 1010f, 110f)));
        Assert.True(sight.BoxSeen(new Vector3(990f, 990f, -30f), new Vector3(1010f, 1010f, -10f)));

        // A BOX REACHING ABOVE THE FLOOR from under it is seen: its top shows.
        Assert.True(sight.BoxSeen(new Vector3(990f, 990f, -5f), new Vector3(1010f, 1010f, 300f)));

        // AND ONE FAR OUTSIDE EVERY VIEW is not.
        Assert.False(sight.BoxSeen(new Vector3(90000f, 90000f, -30f), new Vector3(90010f, 90010f, -10f)));
    }

    [Fact]
    public void THECAMERAStandsOnlyWhereThePlayerCanAndTheModelsOwnFrameMovesEveryViewAlike()
    {
        // ONE CORNER WALKABLE: fewer places than the whole area.
        CameraSight all = Sight();
        CameraSight corner = Sight(Area((x, y) => x < 30 && y < 30));
        Assert.InRange(corner.Views, 1, all.Views - 1);

        // NOWHERE TO STAND: no views, and the reason.
        Assert.Null(CameraSight.Over(new CameraShot(Flat(Camera(Player)), Player), Area((_, _) => false), Vector2.Zero, Vector3.Zero, new Vector3(2000f, 2000f, 0f), out string why));
        Assert.Contains("stand", why, StringComparison.Ordinal);
        Assert.Null(CameraSight.Over(null, Area(), Vector2.Zero, Vector3.Zero, Vector3.One, out why));
        Assert.Contains("camera", why, StringComparison.Ordinal);

        // THE SAME PLACES, the room's corner at 500 across: a model point there is the world point less it.
        var origin = new Vector2(500f, 0f);
        CameraSight moved = Sight(origin: origin);
        Assert.Equal(all.Views, moved.Views);
        var world = new Vector3(1234f, 567f, -89f);
        for (var one = 0; one < all.Views; one++)
        {
            Vector4 expected = Vector4.Transform(new Vector4(world, 1f), all.Matrices[one]);
            Vector4 actual = Vector4.Transform(new Vector4(world - new Vector3(origin, 0f), 1f), moved.Matrices[one]);
            Assert.Equal(expected.X, actual.X, 2);
            Assert.Equal(expected.Y, actual.Y, 2);
            Assert.Equal(expected.W, actual.W, 2);
        }
    }

    [Fact]
    public void AVIEWISTheCameraFollowingThePlayerThere()
    {
        // EVERY VIEW IS THE MATRIX MOVED: some point on the ground lands in the middle of its screen,
        // and that point is a walkable cell's middle on the area's ground.
        CameraSight sight = Sight();
        foreach (Matrix4x4 view in sight.Matrices)
        {
            Assert.True(Matrix4x4.Invert(view, out Matrix4x4 back));
            var near = Vector4.Transform(new Vector4(0f, 0f, 0f, 1f), back);
            var far = Vector4.Transform(new Vector4(0f, 0f, 1f, 1f), back);
            Vector3 a = new Vector3(near.X, near.Y, near.Z) / near.W, b = new Vector3(far.X, far.Y, far.Z) / far.W;
            Vector3 middle = a + ((b - a) * (-a.Z / (b.Z - a.Z)));
            float cell = 250f / 23f;
            float offX = (middle.X / cell) - MathF.Floor(middle.X / cell), offY = (middle.Y / cell) - MathF.Floor(middle.Y / cell);
            Assert.InRange(offX, 0.4f, 0.6f);
            Assert.InRange(offY, 0.4f, 0.6f);
        }
    }

    [Fact]
    public void AMESHKeepsOnlyWhatIsKeptEveryShapeInItsPlace()
    {
        Vector3[] positions = [.. Enumerable.Range(0, 12).Select(one => new Vector3(one, one * 2f, -one))];
        Vector3[] normals = [.. Enumerable.Repeat(Vector3.UnitZ, 12)];
        int[] indices = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];
        SkinnedMesh mesh = SkinnedMesh.Of(
            positions, normals, indices, Vector3.Zero, new Vector3(11f, 22f, 0f),
            coordinates: [.. Enumerable.Range(0, 12).Select(one => new Vector2(one, 0f))],
            shapes: [new MeshShape("a", 0, 6), new MeshShape("b", 6, 3), new MeshShape("c", 9, 3)],
            colours: [.. Enumerable.Range(0, 48).Select(one => (byte)one)]);

        Assert.Same(mesh, mesh.Keeping([true, true, true, true]));

        SkinnedMesh kept = mesh.Keeping([false, true, false, true]);
        Assert.Equal(["a", "b", "c"], kept.Shapes.Select(one => one.Name));
        Assert.Equal([(0, 3), (3, 0), (3, 3)], kept.Shapes.Select(one => (one.From, one.Count)));
        Assert.Equal([0, 1, 2, 3, 4, 5], kept.Indices);
        Assert.Equal([positions[3], positions[4], positions[5], positions[9], positions[10], positions[11]], kept.Positions);
        Assert.Equal(new Vector2(9f, 0f), kept.Coordinates[3]);
        Assert.Equal(new byte[] { 12, 13, 14, 15 }, kept.Colours[..4]);
        Assert.Equal(new Vector3(3f, 6f, -11f), kept.Least);
        Assert.Equal(new Vector3(11f, 22f, -3f), kept.Most);
    }

    [Fact]
    public void AROOMKeyCarriesTheHiddenAndTheCut()
    {
        const string path = "Metadata/Terrain/Maps/Port/Rooms/Unique/boss_01.arm";
        string laid = RoomKey.LaidAt(1, 2, 3, 4);

        Assert.Equal(path, new RoomKey(path, Hidden: false, Cut: 0).ToString());
        var key = new RoomKey(path, Laid: laid, Hidden: true, Cut: 300);
        Assert.Equal(path + "|laid=1,2,3,4+hidden+cut=300", key.ToString());
        Assert.Equal(key, RoomKey.Read(key.ToString()));
        Assert.Equal(0, RoomKey.Read(path + "|cut=99999").Cut);
        Assert.Equal(0, RoomKey.Read(path + "|cut=deep").Cut);
        Assert.False(RoomKey.Read(path + "|laid=1,2,3,4").Hidden);
    }

    [Fact]
    public void ASIEVEThatSeesNoPlaceLoadsNoDoodadAndOneThatSeesOnlyTheMarginLaysNone()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Metadata/Doodads/Rock.ao"] = Encoding.UTF8.GetBytes("version 3\nclient\n{\n\tFixedMesh\n\t{\n\t\tfixed_mesh = \"art/rock.fmt\"\n\t}\n}\n"),
            ["Metadata/Doodads/Tree.ao"] = Encoding.UTF8.GetBytes("version 3\nclient\n{\n\tFixedMesh\n\t{\n\t\tfixed_mesh = \"art/rock.fmt\"\n\t}\n}\n"),
            ["art/rock.fmt"] = Packed.Fmt("art/rock.mat"),
        };
        RoomLayout room = RoomLayout.Read(Encoding.Unicode.GetBytes(Room));
        Assert.True(room.Ready, room.Why);

        var read = new List<string>();
        byte[]? Read(string path)
        {
            read.Add(path);
            return files.GetValueOrDefault(path);
        }

        var pile = new ModelPile();
        RoomModels.Doodads none = RoomModels.Lay(
            room, Read, new MonsterModels.Paints(), pile, 0, tools: false, beyond: null, sieve: new DoodadSieve((_, _) => false, 250f));
        Assert.Equal((0, 2, 0), (none.Placed, none.Unloaded, none.Unseen));
        Assert.Empty(read);
        Assert.Contains(none.Said(), one => one.Contains("2 doodads left out unseen - 2 never loaded", StringComparison.Ordinal));

        // THE MARGIN'S BOX - HUNDREDS OF UNITS - PASSES, THE DOODAD'S OWN - A PROP A FEW UNITS ACROSS - DOES NOT: read, and not laid.
        Assert.All(room.Doodads, one => Assert.InRange(one.Scale <= 0f ? 1f : one.Scale, 0.2f, 10f));
        RoomModels.Doodads margin = RoomModels.Lay(
            room, Read, new MonsterModels.Paints(), pile, 0, tools: false, beyond: null,
            sieve: new DoodadSieve((least, most) => most.X - least.X >= 100f, 250f));
        Assert.Equal((0, 0, 2), (margin.Placed, margin.Unloaded, margin.Unseen));
        Assert.Contains("art/rock.fmt", read);
        Assert.Empty(pile.Joins);
    }

    /// <summary>A room placing a rock and a tree - RoomLayoutTests' own.</summary>
    private const string Room = """
        version 31
        2
        "Metadata/Terrain/Woods/edge.et"
        "Metadata/Terrain/Woods/ground.gt"
        5 3
        1 1
        "roomtag"
        0 1
        k 2 1 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0

        1 0
        1 0
        1 0
        1 0
        0
        1
        3 4 0.5 "spawn"
        0
        0
        0
        0
        k 2 1 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 n
        2
        10 20 1.5 0 0 0 1 0 1 1 7.5 2 "Metadata/Doodads/Rock.ao" "stub"
        30 5 0 0 0 0 1 1 0 0 1 "Metadata/Doodads/Tree.ao" "stub2"
        0
        0
        """;
}
