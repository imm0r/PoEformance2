using System.Numerics;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Where the overlay is allowed to draw: a hostile area, and only on a map that is open.
/// </summary>
public class WorldAreaTests
{
    [Theory]
    [InlineData("MapRiverhold", false, true)]
    [InlineData("G1_2", false, true)]
    [InlineData("G1_town", true, false)]
    [InlineData("HideoutFelled", false, false)]
    public void HostileMeansThereIsSomethingToFight(string id, bool isTown, bool hostile)
    {
        bool isHideout = id.Contains("hideout", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(hostile, new AreaInfo(id, id, 1, isTown, isHideout).IsHostile);
    }

    [Fact]
    public void UnresolvedArea_CountsAsHostile()
    {
        // The failure mode of a read that went wrong must be "the overlay is still there",
        // not "the overlay silently stopped working and you find out during a fight".
        Assert.True(AreaInfo.Unknown.IsHostile);
        Assert.Equal("unknown", AreaInfo.Unknown.Describe());
    }

    [Fact]
    public void Describe_NamesTheAreaAndWhyItIsHidden()
    {
        Assert.Contains("hideout", new AreaInfo("HideoutFelled", "Felled", 1, false, true).Describe(),
            StringComparison.Ordinal);
        Assert.Contains("hostile", new AreaInfo("MapRiverhold", "Riverhold", 0, false, false).Describe(),
            StringComparison.Ordinal);
    }

    // ── the map's own bounds ──────────────────────────────────────────────────

    private static MapView Minimap(float left, float top, float width, float height)
        => new(new Vector2(left + (width / 2), top + (height / 2)), 300f, 1f, IsLargeMap: false,
            Visible: true, left, top, width, height);

    [Fact]
    public void MarkersOutsideTheMinimap_AreClippedAway()
    {
        // The projection places a marker by world distance, so anything further off than
        // the map's edge lands beyond it - and a dot outside its map is a dot in the middle
        // of the game, which is exactly what "drawn everywhere" means.
        MapView map = Minimap(1600, 40, 300, 300);

        Assert.True(map.Contains(new Vector2(1750, 190)));    // centre
        Assert.True(map.Contains(new Vector2(1600, 40)));     // exactly the corner
        Assert.False(map.Contains(new Vector2(1400, 190)));   // off to the left
        Assert.False(map.Contains(new Vector2(1750, 500)));   // below it
    }

    [Fact]
    public void AMapWithNoMeasuredRectangle_AcceptsEverything()
    {
        // Rejecting everything would silently blank the radar the moment a size read fails.
        var unmeasured = new MapView(Vector2.Zero, 300f, 1f, IsLargeMap: true);

        Assert.True(unmeasured.Contains(new Vector2(-500, 9000)));
    }

    [Fact]
    public void AHiddenMapIsNotDrawnOn()
    {
        // Both map elements exist at all times; opening the large one hides the minimap.
        // Visibility is the only thing that says which one the player is looking at.
        MapView hidden = Minimap(1600, 40, 300, 300) with { Visible = false };

        Assert.False(hidden.Visible);
        Assert.True(hidden.IsUsable);   // usable but not on screen - the distinction that matters
    }
}
