using PoEformance.Features;
using PoEformance.Game.Components;

namespace PoEformance.Core.Tests;

/// <summary>
/// What was decided about the ground-type names, and that it survives the settings file.
/// </summary>
/// <remarks>
/// The interesting one here is <see cref="GroundSettings.MaxPatches"/>, which this feature
/// shipped without. The reasoning it shipped on was that an area holds a HANDFUL of ground
/// types, so its regions must be few and large and a size threshold would do the whole job. An
/// Abyssal Depths screenshot disproved it: two types, and maelstrom_abyss written across the map
/// roughly twenty times. Few types does not mean few regions, and size cannot thin regions that
/// are not small.
/// </remarks>
public class GroundSettingsTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(4, 4)]
    [InlineData(9000, 4096)]
    public void TheSmallestPatchStaysInRange(int written, int expected)
    {
        // Capped rather than merely floored: a threshold above the biggest patch in the area
        // hides every label, which reads as the feature being broken rather than as a setting.
        Assert.Equal(expected, new GroundSettings(MinTiles: written).Normalised().MinTiles);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(3, 3)]
    [InlineData(9000, 64)]
    public void HowOftenATypeMayBeWrittenStaysInRange(int written, int expected)
    {
        // FLOORED AT ONE, not zero: a cap of zero draws nothing at all, and a layer that is
        // switched on and silent is indistinguishable from one that is broken.
        Assert.Equal(expected, new GroundSettings(MaxPatches: written).Normalised().MaxPatches);
    }

    [Fact]
    public void TheDefaultsAreOffAndThinned()
    {
        // Off because this writes a name on every block of ground in the area. Four tiles drops
        // the ragged edge where two types meet. Three patches is the one that does the thinning,
        // and it is a starting point rather than a measurement - the only figure in hand is the
        // twenty that made a map unreadable.
        Assert.False(GroundSettings.Default.Show);
        Assert.Equal(4, GroundSettings.Default.MinTiles);
        Assert.Equal(3, GroundSettings.Default.MaxPatches);
    }

    [Fact]
    public void TheChoicesSurviveTheSettingsFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"poeformance-ground-{Guid.NewGuid():N}.json");
        try
        {
            var written = new OverlaySettings(ItemRarity.Magic)
            {
                Ground = GroundSettings.Default with { Show = true, MinTiles = 9, MaxPatches = 7 },
            };

            Assert.True(OverlaySettingsStore.Save(written, path));

            GroundSettings read = OverlaySettingsStore.Load(path).GroundOrDefault;
            Assert.True(read.Show);
            Assert.Equal(9, read.MinTiles);
            Assert.Equal(7, read.MaxPatches);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AFileThatSaysNothingAboutGroundGetsTheDefaults()
    {
        // An untouched settings file gains no key, and the defaults keep coming from the code
        // where a correction can still reach somebody who never opened the switches.
        var settings = new OverlaySettings(ItemRarity.Magic);

        Assert.Null(settings.Ground);
        Assert.Equal(GroundSettings.Default, settings.GroundOrDefault);
        Assert.Null(settings.Normalised().Ground);
    }
}
