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
    // id,                 act, town,  hideout, markers wanted
    [InlineData("MapOvergrown", 10, false, false, true)]  // a real T6, reported by the owner
    [InlineData("MapRiverhold", 0, false, false, true)]   // endgame, the case this exists for
    [InlineData("MapBluff", 2, false, false, true)]       // a map that DOES report an act still counts
    [InlineData("Trial_Sekhema", 10, false, false, true)] // endgame that is not named Map*
    [InlineData("Abyss_Hub", 2, false, false, false)]     // a real campaign area, reported by the owner
    [InlineData("G1_2", 1, false, false, false)]          // act zone
    [InlineData("G1_town", 1, true, false, false)]        // town
    [InlineData("HideoutFelled", 1, false, true, false)]  // hideout
    public void MarkersAreWantedInEndgameAreasOnly(string id, int act, bool town, bool hideout, bool wanted)
    {
        Assert.Equal(wanted, new AreaInfo(id, id, act, town, hideout).WantsMarkers);
    }

    [Fact]
    public void AnEndgameMapReportsActTen_NotActZero()
    {
        // The observation that decided the rule. A T6 came back as MapOvergrown, act 10 -
        // so "belongs to an act" does NOT separate campaign from endgame, and a rule built
        // on the act alone would have hidden the overlay in exactly the content it is for.
        Assert.False(new AreaInfo("MapOvergrown", "Overgrown", 10, false, false).IsCampaign);
        Assert.True(new AreaInfo("Abyss_Hub", "The Well of Souls", 2, false, false).IsCampaign);
    }

    [Fact]
    public void BothSignalsHaveToAgreeBeforeAnythingIsHidden()
    {
        // Asymmetric on purpose: hiding the overlay in endgame content is the failure this
        // feature exists to avoid, while showing it in a campaign zone is a mild annoyance.
        // So either signal saying "endgame" is enough to keep the markers.
        Assert.False(new AreaInfo("MapBluff", "Bluff", 2, false, false).IsCampaign);        // id says map
        Assert.False(new AreaInfo("Trial_Sekhema", "Trial", 10, false, false).IsCampaign);  // act says endgame
        Assert.True(new AreaInfo("G1_2", "Clearfell", 1, false, false).IsCampaign);         // neither does
    }

    [Fact]
    public void UnresolvedArea_StillWantsMarkers()
    {
        // The failure mode of a read that went wrong must be "the overlay is still there",
        // not "the overlay silently stopped working and you find out during a fight".
        Assert.True(AreaInfo.Unknown.WantsMarkers);
        Assert.Equal("unknown", AreaInfo.Unknown.Describe());
    }

    [Fact]
    public void Describe_NamesTheAreaAndWhyItIsHidden()
    {
        Assert.Contains("hideout", new AreaInfo("HideoutFelled", "Felled", 1, false, true).Describe(),
            StringComparison.Ordinal);
        Assert.Contains("campaign", new AreaInfo("Abyss_Hub", "The Well of Souls", 2, false, false).Describe(),
            StringComparison.Ordinal);
        Assert.Contains("map", new AreaInfo("MapRiverhold", "Riverhold", 0, false, false).Describe(),
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
