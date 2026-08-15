using PoEformance.Features;

namespace PoEformance.Core.Tests;

/// <summary>
/// Reading an element out of a projectile's name.
/// </summary>
/// <remarks>
/// A guess, and tested as one: what matters is not that every skill in the game is recognised
/// - it will not be - but that the two ways of being wrong stay shut. Painting Sacrifice blue
/// because it contains "ice" is the first. Calling FireballIncusionLightning fire because
/// "Fireball" came first is the second. Both are real names from the game's own lists.
/// </remarks>
public class ProjectileElementTests
{
    [Theory]
    [InlineData("Metadata/Projectiles/Spark", ProjectileElement.Lightning)]
    [InlineData("Metadata/Projectiles/Fireball", ProjectileElement.Fire)]
    [InlineData("Metadata/Projectiles/IceSpear", ProjectileElement.Cold)]
    [InlineData("Metadata/Projectiles/FrostBolt", ProjectileElement.Cold)]
    [InlineData("Metadata/Projectiles/DragonFireball", ProjectileElement.Fire)]
    [InlineData("Metadata/Projectiles/ShockingArrowFaridun", ProjectileElement.Lightning)]
    [InlineData("Metadata/Projectiles/ElectrocutingArrow", ProjectileElement.Lightning)]
    [InlineData("Metadata/Projectiles/PoisonBurst", ProjectileElement.Chaos)]
    [InlineData("Metadata/Projectiles/SplinterShot", ProjectileElement.Physical)]
    public void TheNameSaysWhatItIs(string path, ProjectileElement expected)
        => Assert.Equal(expected, ProjectileElements.Of(path));

    [Theory]
    [InlineData("Metadata/Projectiles/SacrificeDagger")]
    [InlineData("Metadata/Projectiles/JusticeBolt")]
    [InlineData("Metadata/Projectiles/SliceThrow")]
    [InlineData("Metadata/Projectiles/DeviceShot")]
    public void AWordThatMERELYCONTAINSAnElementIsNotOne(string path)
    {
        // The "/ao/" lesson again: "ice" inside Sacrifice, Justice, Slice and Device would
        // paint half the game blue. A word has to BEGIN with the keyword.
        Assert.Equal(ProjectileElement.Unknown, ProjectileElements.Of(path));
    }

    [Theory]
    [InlineData("FireballIncusionFire", ProjectileElement.Fire)]
    [InlineData("FireballIncursionChaos", ProjectileElement.Chaos)]
    [InlineData("FireballIncusionLightning", ProjectileElement.Lightning)]
    public void TheVariantOnTheEndWinsOverTheBaseName(string name, ProjectileElement expected)
    {
        // Three real ids from the game's skill list, spelling included. One base name with the
        // variant's element stuck on the end - so taking the FIRST match calls two of the
        // three fire.
        Assert.Equal(expected, ProjectileElements.Of($"Metadata/Projectiles/{name}"));
    }

    [Fact]
    public void TheFOLDERSAboveTheNameDoNotDecide()
    {
        // A cold projectile filed in a fire monster's directory. The name is the half that is
        // about the projectile.
        Assert.Equal(
            ProjectileElement.Cold,
            ProjectileElements.Of("Metadata/Monsters/FireMonsters/objects/IceBolt"));
    }

    [Fact]
    public void TheLevelSuffixDoesNotJoinItselfToTheName()
    {
        // The game appends @70 to plenty of paths, and a splitter that swallowed it would be
        // matching against "Spark70".
        Assert.Equal(ProjectileElement.Lightning, ProjectileElements.Of("Metadata/Projectiles/Spark@70"));
    }

    [Fact]
    public void AnAllCapitalsNameIsOneWordRatherThanNine()
    {
        Assert.Equal(ProjectileElement.Fire, ProjectileElements.Of("Metadata/Projectiles/FIREBALL"));
    }

    [Fact]
    public void ANameThatSaysNothingStaysUnknown()
    {
        // And that is a real answer rather than a gap: the trail keeps the mark's own colour,
        // so nothing is stated about a projectile nobody could classify.
        Assert.Equal(ProjectileElement.Unknown, ProjectileElements.Of("Metadata/Projectiles/Arrow"));
        Assert.Equal(ProjectileElement.Unknown, ProjectileElements.Of(string.Empty));
    }

    [Fact]
    public void EveryElementTheGuessCanReturnHasAColour()
    {
        // The catalogue's promise, for this corner of it: a drawn thing with no entry draws
        // white, which is visible only in the game and only to somebody who wonders why.
        foreach (ProjectileElement element in ProjectileElements.Known)
        {
            string key = StyleCatalogue.ForElement(element);
            Assert.NotEqual(string.Empty, key);
            Assert.Contains(StyleCatalogue.Entries, entry => entry.Key == key);
        }

        Assert.Equal(string.Empty, StyleCatalogue.ForElement(ProjectileElement.Unknown));
    }
}
