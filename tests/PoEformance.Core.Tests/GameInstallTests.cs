using PoEformance.Game.Files;

namespace PoEformance.Core.Tests;

/// <summary>
/// Finding where the game is installed.
/// </summary>
/// <remarks>
/// What is worth pinning is that a folder counts because of what is IN it and not because of
/// what it is called. The fallback list is guesses at where somebody might have installed the
/// game, and a guess that is accepted on its name alone is how the tool ends up reading an
/// empty folder and reporting that the install cannot be read.
/// </remarks>
public class GameInstallTests
{
    private static string Somewhere() => Path.Combine(Path.GetTempPath(), $"install-{Guid.NewGuid():N}");

    [Fact]
    public void AFOLDERCountsWhenTheFilesAreInItAndNotBecauseOfItsName()
    {
        string folder = Somewhere();

        try
        {
            // Named exactly right and holding nothing.
            Directory.CreateDirectory(Path.Combine(folder, "Path of Exile 2"));
            Assert.False(GameInstall.Holds(Path.Combine(folder, "Path of Exile 2")));

            // A loose install: the bundles are in folders.
            Directory.CreateDirectory(Path.Combine(folder, "Bundles2"));
            File.WriteAllBytes(Path.Combine(folder, "Bundles2", "_.index.bin"), [1]);
            Assert.True(GameInstall.Holds(folder));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public void ANDTheOtherShapeCountsToo()
    {
        // A standalone install keeps the same files inside one container, so looking only for
        // the loose folder finds nothing on half of them.
        string folder = Somewhere();

        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "Content.ggpk"), [1]);

            Assert.True(GameInstall.Holds(folder));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public void ANDAFolderSomebodyNamedWinsWhenItHoldsOne()
    {
        string folder = Somewhere();

        try
        {
            Directory.CreateDirectory(Path.Combine(folder, "Bundles2"));
            File.WriteAllBytes(Path.Combine(folder, "Bundles2", "_.index.bin"), [1]);

            Assert.Equal(folder, GameInstall.Find(folder));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public void ANDOneThatHoldsNothingIsNotAnInstall()
    {
        Assert.False(GameInstall.Holds(null));
        Assert.False(GameInstall.Holds(string.Empty));
        Assert.False(GameInstall.Holds("   "));
        Assert.False(GameInstall.Holds(Path.GetTempPath()));
        Assert.False(GameInstall.Holds(Somewhere()));
    }

    [Fact]
    public void THENamesAreOneListRatherThanTwo()
    {
        // Finding the process to READ and asking a process where the files are is the same
        // question. Two copies of the answer drift the moment a launcher adds a name.
        Assert.NotEmpty(GameInstall.Names);
        Assert.Contains("PathOfExile", GameInstall.Names);
        Assert.Contains("PathOfExileSteam", GameInstall.Names);
        Assert.Equal(GameInstall.Names.Length, GameInstall.Names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
