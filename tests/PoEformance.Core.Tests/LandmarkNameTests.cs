using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// The written-down names for particular tiles, and whether they can be found again.
/// </summary>
/// <remarks>
/// This file is the one place the tool's idea of a key has to agree with data somebody else
/// wrote. Get that wrong and nothing breaks loudly: every lookup misses, and a feature with
/// working data looks like a feature with none. So the tests here go through the SHIPPED file
/// rather than a convenient fake.
/// </remarks>
public class LandmarkNameTests
{
    private static string DataPath
    {
        get
        {
            // Walk up from the test bin directory to the repo root.
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "data", "landmarks.json")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return Path.Combine(dir.FullName, "data", "landmarks.json");
        }
    }

    [Fact]
    public void TheShippedFileLoads()
    {
        LandmarkNames names = LandmarkNames.Load(DataPath);

        Assert.Equal(91, names.Areas);
        Assert.NotNull(names.For("G1_2"));
        Assert.Null(names.For("MapNobodyWroteDown"));
    }

    [Fact]
    public void AnAreaIsFoundWhateverTheCaseOfItsId()
    {
        // Area ids arrive from the game's own data, not from this file.
        Assert.NotNull(LandmarkNames.Load(DataPath).For("g1_2"));
    }

    [Fact]
    public void TheKeyThisToolBUILDSIsTheKeyTheFileWROTE()
    {
        // The test that earns its place. A tile read from memory has to produce, character for
        // character, the key someone typed into the data file - and the two were arrived at
        // independently, so nothing but a comparison against the real file proves it.
        IReadOnlyDictionary<string, string>? names = LandmarkNames.Load(DataPath).For("G1_2");
        Assert.NotNull(names);

        // One real entry of G1_2, as the file has it.
        string key = names.Keys.First(k => k.Contains(".tdtx:", StringComparison.Ordinal));

        // Split it back into what the reader would see: a tile path, and the sub-ids of one
        // placement. The "x" belongs to the "x:" that joins them.
        int at = key.IndexOf("x:", StringComparison.Ordinal);
        string path = key[..at];
        string[] ids = key[(at + 2)..].Split("-y:");

        var tile = new TerrainTile(path, 0, 0, int.Parse(ids[0]), int.Parse(ids[1]), Rotation: 0);

        Assert.Equal(key, TerrainLandmarks.KeyFor(tile));
        Assert.True(names.ContainsKey(TerrainLandmarks.KeyFor(tile)));
    }

    [Fact]
    public void ATileMatchesItsNameWhateverTheCaseOfItsPath()
    {
        // The reference lowercases both sides before comparing, which says plainly that the
        // path out of memory and the path in the file agree on the letters and not always on
        // their case. Matching case-sensitively would find nothing at all.
        IReadOnlyDictionary<string, string>? names = LandmarkNames.Load(DataPath).For("G1_2");
        Assert.NotNull(names);

        string key = names.Keys.First(k => k.Contains(".tdtx:", StringComparison.Ordinal));

        Assert.True(names.ContainsKey(key.ToLowerInvariant()));
        Assert.True(names.ContainsKey(key.ToUpperInvariant()));
    }

    [Fact]
    public void AMissingFileIsNotAFailure()
    {
        // Names are a decoration on top of finding the arena. Losing them must not cost the
        // arena, so absence has to be ordinary rather than an exception.
        LandmarkNames names = LandmarkNames.Load(Path.Combine(Path.GetTempPath(), "no-such-landmarks.json"));

        Assert.Equal(0, names.Areas);
        Assert.Null(names.For("G1_2"));
    }

    [Fact]
    public void RubbishInTheFileIsNotAFailureEither()
    {
        string path = Path.Combine(Path.GetTempPath(), $"landmarks-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ this is not json");

        try
        {
            Assert.Equal(0, LandmarkNames.Load(path).Areas);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
