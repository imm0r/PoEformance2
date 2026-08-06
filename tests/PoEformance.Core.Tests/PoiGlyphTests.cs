using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Choosing which shape a place is drawn as.
/// </summary>
/// <remarks>
/// The shapes themselves are taste and are not tested. THIS is the substance: which marker a
/// place gets, and it comes from the game's own icon name wherever there is one - the same
/// principle the kinds already follow. A breach and a ritual are one kind and one colour
/// between them, and they are the two markers most worth telling apart on sight.
/// </remarks>
public class PoiGlyphTests
{
    [Theory]
    [InlineData("Breach", PoiGlyph.Breach)]
    [InlineData("Ritual", PoiGlyph.Ritual)]
    [InlineData("ExpeditionMarker", PoiGlyph.Expedition)]
    [InlineData("Essence", PoiGlyph.Essence)]
    [InlineData("DeliriumMirror", PoiGlyph.Delirium)]
    [InlineData("Shrine", PoiGlyph.Shrine)]
    [InlineData("ArmourerStrongbox", PoiGlyph.Strongbox)]
    [InlineData("Waypoint", PoiGlyph.Waypoint)]
    [InlineData("Checkpoint", PoiGlyph.Checkpoint)]
    [InlineData("QuestObject", PoiGlyph.Quest)]
    public void TheGamesOwnNameForTheMarkerDecides(string icon, PoiGlyph expected)
        => Assert.Equal(expected, PoiGlyphs.For(icon, PoiKind.Marked));

    [Fact]
    public void TheNameBeatsTheKindWhereTheyDisagree()
    {
        // The whole point. A breach and a ritual are BOTH PoiKind.Mechanic - one kind, one
        // colour - so with only the kind to go on they draw identically and the feature does
        // nothing.
        Assert.Equal(PoiGlyph.Breach, PoiGlyphs.For("Breach", PoiKind.Mechanic));
        Assert.Equal(PoiGlyph.Ritual, PoiGlyphs.For("Ritual", PoiKind.Mechanic));
    }

    [Fact]
    public void WhatItISBeatsWhichMechanicItBelongsTo()
    {
        // "RewardChestExpedition" contains both words. It is a chest: something you walk over
        // and open, which is the actionable fact - that it belongs to the expedition is not
        // news by the time you are standing in one.
        Assert.Equal(PoiGlyph.Chest, PoiGlyphs.For("RewardChestExpedition", PoiKind.Mechanic));
    }

    [Fact]
    public void SomethingWithNoIconFallsBackToWhatKindOfPlaceItIs()
    {
        // A terrain boss arena is the case: it is found in the SHAPE of the ground, long
        // before the boss exists as an entity, so there is no icon to read and there will not
        // be one until the fight starts.
        Assert.Equal(PoiGlyph.Boss, PoiGlyphs.For(string.Empty, PoiKind.BossArena));
        Assert.Equal(PoiGlyph.Portal, PoiGlyphs.For(string.Empty, PoiKind.AreaTransition));
    }

    [Fact]
    public void AnIconNobodyHasHeardOfStillDrawsAsAMarker()
    {
        // There are hundreds of these names and most are one encounter's reward chest, so the
        // list will always be short of the game. An unknown one is not nothing - the game
        // marked something there - it is just not known what.
        Assert.Equal(PoiGlyph.Marker, PoiGlyphs.For("SomeLeagueNobodyHasPlayedYet", PoiKind.Marked));
    }

    [Fact]
    public void TheNameIsMatchedWhateverItsCase()
        => Assert.Equal(PoiGlyph.Breach, PoiGlyphs.For("breachSplinterPile", PoiKind.None));

    [Fact]
    public void EveryKindThatIsDrawnHasAShapeOfItsOwn()
    {
        // Two kinds sharing a shape means two things on the map a player cannot tell apart,
        // which is the state this feature exists to leave behind.
        PoiKind[] drawn =
        [
            PoiKind.AreaTransition, PoiKind.Waypoint, PoiKind.Checkpoint,
            PoiKind.Chest, PoiKind.Shrine, PoiKind.Npc, PoiKind.Quest, PoiKind.BossArena,
        ];

        List<PoiGlyph> shapes = [.. drawn.Select(kind => PoiGlyphs.For(string.Empty, kind))];

        Assert.Equal(shapes.Count, shapes.Distinct().Count());
        Assert.DoesNotContain(PoiGlyph.Marker, shapes);
    }
}
