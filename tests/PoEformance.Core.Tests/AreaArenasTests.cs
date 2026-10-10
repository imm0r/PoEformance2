using System.Text;
using PoEformance.Features;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// A boss's arena found in the install from the area's terrain folder: the tile index under it, and its masters' tileset lists.
/// </summary>
public class AreaArenasTests
{
    private const string Folder = "Metadata/Terrain/Maps/VaalFactory/";

    /// <summary>A made-up install: the master names a tile list, which includes a shared one.</summary>
    private static readonly Dictionary<string, string> Files = new(StringComparer.OrdinalIgnoreCase)
    {
        [Folder + "master.tsi"] = "version 2\nRoomSet \"generate.rs\"\nTileSet \"tiles.tst\"\n",
        [Folder + "tiles.tst"] = "include \"Metadata/Terrain/Maps/Shared/arenas.tst\"\n10 \"Metadata/Terrain/Maps/VaalFactory/Tiles/floor_01.tdt\" R90\n\"Metadata/Terrain/Maps/VaalFactory/Tiles/BossArena_02.tdt\"\n",
        ["Metadata/Terrain/Maps/Shared/arenas.tst"] = "\"Metadata/Terrain/Maps/Shared/SharedBossRoom_01.tdt\"\n\"Metadata/Terrain/Maps/Shared/wall_01.tdt\"\n",
        [Folder + "Graphs/not_a_master.tsi"] = "TileSet \"Metadata/Terrain/Maps/Elsewhere/arena.tst\"\n",
        ["Metadata/Terrain/Maps/Elsewhere/arena.tst"] = "\"Metadata/Terrain/Maps/Elsewhere/DeepArena_01.tdt\"\n",
    };

    private static byte[]? Read(string path) => Files.TryGetValue(path, out string? text) ? Encoding.UTF8.GetBytes(text) : null;

    private static readonly string[] Tiles =
    [
        Folder + "Tiles/BossArena_01.tdt",
        Folder + "Tiles/floor_01.tdt",
        "Metadata/Terrain/Maps/Other/BossArena_01.tdt",
    ];

    private static readonly string[] Masters = [Folder + "master.tsi", Folder + "Graphs/not_a_master.tsi", "Metadata/Terrain/Maps/Other/master.tsi"];

    [Fact]
    public void TheFoldersOwnArenasComeFirstThenTheTilesetsIncludesFollowed()
    {
        IReadOnlyList<string> found = AreaArenas.Of(Read, [Folder], Tiles, Masters);

        Assert.Equal(
            [
                Folder + "Tiles/BossArena_01.tdt",
                Folder + "Tiles/BossArena_02.tdt",
                "Metadata/Terrain/Maps/Shared/SharedBossRoom_01.tdt",
            ],
            found);
    }

    [Fact]
    public void WithoutAnInstallOnlyTheIndexAnswers()
    {
        Assert.Equal([Folder + "Tiles/BossArena_01.tdt"], AreaArenas.Of(null, [Folder], Tiles, Masters));
        Assert.Empty(AreaArenas.Of(Read, [], Tiles, Masters));
    }

    [Fact]
    public void AGraphsFolderIsTheOneAboveItsGraphs()
    {
        Assert.Equal(Folder, AreaGraphs.FolderOf("Metadata/Terrain/Maps/VaalFactory/Graphs/VaalFactory_01.dgr"));
        Assert.Equal("Metadata/Terrain/Hideouts/ArenaKulemak/", AreaGraphs.FolderOf("Metadata/Terrain/Hideouts/ArenaKulemak/HideoutArenaKulemak.dgr"));
        Assert.Equal(string.Empty, AreaGraphs.FolderOf("alone.dgr"));
    }

    [Fact]
    public void TheShippedFileNamesTheAssemblysGraph()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "data", "area-graphs.json")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        AreaGraphs graphs = AreaGraphs.Load(Path.Combine(dir.FullName, "data", "area-graphs.json"));
        Assert.True(graphs.Count > 400, $"only {graphs.Count} areas");
        Assert.Equal([Folder], graphs.FoldersOf("MapVaalFactory"));
        Assert.Empty(graphs.FoldersOf("NoSuchArea"));
        Assert.Empty(AreaGraphs.Load("no/such/file.json").Of("MapVaalFactory"));
    }
}
