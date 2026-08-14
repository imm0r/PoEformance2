using System.Numerics;
using PoEformance.Features;
using PoEformance.Game.Ui;

namespace PoEformance.Core.Tests;

/// <summary>
/// What terrain a map is, and the colour that says so.
/// </summary>
/// <remarks>
/// The border is drawn in the overlay, which the tests cannot reach - so what is checked here is
/// everything the drawing is HANDED: that a biome arrives on the mark at all, that switching it
/// off is distinguishable from a map whose biome is Water, and that the table answers with
/// nothing rather than with a confident wrong colour for a number it does not know.
/// </remarks>
public class AtlasBiomeTests
{
    private static AtlasNode Node(byte biome, string mapId = "MapAugury")
        => new(
            Index: 1,
            Address: 0x1000,
            MapId: mapId,
            Grid: (1, 1),
            State: AtlasNodeState.Open,
            Biome: biome,
            Connections: [],
            Screen: new Vector2(100, 100),
            Size: new Vector2(40, 20),
            BadgeIds: [],
            ContentTokens: []);

    private static AtlasView Compose(AtlasNode node, AtlasSettings? settings = null)
        => AtlasWatch.Compose(
            [node],
            settings ?? AtlasSettings.Default,
            AtlasGrouping.None,
            AtlasRoutes.None,
            new Dictionary<(int X, int Y), IReadOnlyList<string>>());

    [Fact]
    public void THEBiomeReachesTheDrawing()
    {
        AtlasView view = Compose(Node(5));

        Assert.Equal(5, Assert.Single(view.Marks).Biome);
    }

    [Fact]
    public void SwitchedOffIsNOTTheSameAsBiomeNought()
    {
        // Nought is Water, a real biome. Were "off" nought too, every map on the atlas would
        // wear a blue ring the moment somebody turned the borders off.
        AtlasView off = Compose(Node(0), AtlasSettings.Default with { Biomes = false });
        AtlasView on = Compose(Node(0));

        Assert.Equal(AtlasBiomes.None, Assert.Single(off.Marks).Biome);
        Assert.Equal(0, Assert.Single(on.Marks).Biome);
        Assert.Null(AtlasBiomes.Of(AtlasBiomes.None));
        Assert.NotNull(AtlasBiomes.Of(0));
    }

    [Fact]
    public void ANumberTheTableDoesNotKnowGetsNoRingAtAll()
    {
        // A league adding a biome is the ordinary way this happens, and the wrong answer to it
        // is a fallback colour: every map in the new biome would then be confidently ringed in
        // a colour that stands for something else.
        Assert.Null(AtlasBiomes.Of(200));
        Assert.Null(AtlasBiomes.Of(-7));
    }

    [Fact]
    public void THETerrainBiomesAreAllTellableApart()
    {
        // The reference gives Swamp byte-for-byte the same blue as Water, which makes the two
        // most common biomes indistinguishable - the one thing a colour has to do here. The
        // cities are deliberately left sharing white, so this asks only of the terrain.
        int[] terrain = [0, 1, 2, 3, 4, 5];
        var colours = new HashSet<uint>();

        foreach (int id in terrain)
        {
            AtlasBiome biome = Assert.IsType<AtlasBiome>(AtlasBiomes.Of(id));
            Assert.True(colours.Add(biome.Colour), $"biome {id} ({biome.Name}) repeats a colour");
        }
    }

    [Fact]
    public void EveryBiomeHasAColourThatCanActuallyBeSeen()
    {
        // Drawn as a two-pixel ring on a dark plate, so a colour parsed to nothing draws no ring
        // and a very dark one draws a ring nobody can see. Forest was the second of those in the
        // reference's own table.
        foreach ((int id, AtlasBiome biome) in AtlasBiomes.All)
        {
            Assert.NotEqual(0u, biome.Colour);
            Assert.False(string.IsNullOrWhiteSpace(biome.Name), $"biome {id} has no name");

            uint red = biome.Colour & 0xFF;
            uint green = (biome.Colour >> 8) & 0xFF;
            uint blue = (biome.Colour >> 16) & 0xFF;
            Assert.True(Math.Max(red, Math.Max(green, blue)) >= 0x80, $"{biome.Name} is too dark to read");
        }
    }
}
