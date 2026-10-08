using System.Text;
using PoEformance.Features;
using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// The room dump's lighting section: every environment that could apply, whole, and every doodad light's bytes - the data the light's formats are to be worked out from.
/// </summary>
public class LightingDumpTests
{
    private const string Room = "Metadata/Terrain/Rooms/channel_1open_01.arm";
    private const string Tileset = "Metadata/Terrain/Test/test.tsi";
    private const string Named = "Metadata/EnvironmentSettings/test.env";
    private const string Loaded = "Metadata/EnvironmentSettings/other.env";
    private const string Rig = "art/lit.ast";

    private static string Fixture(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine([dir.FullName, "tests", "fixtures", .. parts]);
    }

    /// <summary>
    /// A loaded tileset's environment lines and the file they name, a loaded environment, and a doodad whose rig carries a light - each where it belongs in the section.
    /// </summary>
    [Fact]
    public void THESECTIONPrintsTheEnvironmentsAndEachLightsBytes()
    {
        byte[] arm = File.ReadAllBytes(Fixture("rooms", "channel_1open_01.arm"));
        RoomLayout layout = RoomLayout.Read(arm);
        string doodad = Assert.IsType<string>(layout.Doodads.Select(one => one.Ao).FirstOrDefault(one => one.Length > 0));
        byte[] lit = File.ReadAllBytes(Fixture("ballexplode-light.ast"));
        string ao = "version 3\nclient\n{\n\tClientAnimationController\n\t{\n\t\tskeleton = \"" + Rig + "\"\n\t}\n}\n";

        byte[]? Read(string path) => path switch
        {
            Room => arm,
            Tileset => Encoding.UTF8.GetBytes("TileSet \"tiles.tst\"\nEnvironment \"" + Named + "\"\n"),
            Named => Encoding.UTF8.GetBytes("{ \"directional_light\": { \"colour\": [1, 0.9, 0.8] } }"),
            Rig => lit,
            _ when path == doodad => Encoding.UTF8.GetBytes(ao),
            _ => null,
        };

        string said = ModelDump.Lighting(Read, Room, [Tileset, Loaded, "Metadata/Terrain/Rooms/a.arm"]);

        Assert.Contains("--- loaded .tsi " + Tileset, said, StringComparison.Ordinal);
        Assert.Contains("  Environment \"" + Named + "\"", said, StringComparison.Ordinal);
        Assert.Contains("=== .env " + Named + "  (named by " + Tileset + ")", said, StringComparison.Ordinal);
        Assert.Contains("\"directional_light\"", said, StringComparison.Ordinal);
        Assert.Contains("=== .env " + Loaded + "  (in the area's loaded files)", said, StringComparison.Ordinal);
        Assert.Contains("--- " + doodad, said, StringComparison.Ordinal);
        Assert.Contains("  rig " + Rig + "  version 11, 8 bones, 1 lights", said, StringComparison.Ordinal);
        Assert.Contains("  light 0 PointLightShape1  (59 bytes after the name's length)", said, StringComparison.Ordinal);
        Assert.Contains("    0000  00 E8 FB 79 3F 2D 39 A8 3F 00 00 C0 3F 00 00 00", said, StringComparison.Ordinal);
        Assert.Contains("    f32 @1:", said, StringComparison.Ordinal);
    }

    /// <summary>With nothing to go on, the section says so rather than staying empty.</summary>
    [Fact]
    public void WITHNothingNamedItSaysSo()
    {
        string said = ModelDump.Lighting(_ => null, Room, null);
        Assert.Contains("no loaded-file list to hand", said, StringComparison.Ordinal);
        Assert.Contains("(no .env named by the room, by a loaded tileset, or among the area's loaded files)", said, StringComparison.Ordinal);
        Assert.Contains("0 doodad files read", said, StringComparison.Ordinal);
    }
}
