using PoEformance.Features;
using PoEformance.Game.Components;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// "Everything drawn can be changed" - and what it takes for that to stay true.
/// </summary>
public class OverlayStyleTests
{
    [Fact]
    public void AKeyNobodyTouchedDrawsTheWayItAlwaysDid()
    {
        var style = new OverlayStyle();

        Assert.True(style.Visible("entity.monster.rare"));
        Assert.Equal(StyleCatalogue.Fallback("entity.monster.rare"), style.Colour("entity.monster.rare"));
        Assert.Equal(4f, style.Width("terrain.outline", 4f));
        Assert.Equal(6f, style.Sized("entity.monster.rare", 6f));
    }

    [Fact]
    public void WhatWasChosenIsWhatIsDrawn()
    {
        var style = new OverlayStyle();
        style.Set("entity.monster.rare", new LayerStyle(Colour: "#FF0000", Scale: 2f));

        Assert.Equal(0xFF0000FFu, style.Colour("entity.monster.rare"));
        Assert.Equal(12f, style.Sized("entity.monster.rare", 6f));
    }

    [Fact]
    public void SettingSomethingBackToItsDefaultREMOVESIt()
    {
        // Not a tidiness point. A stored default looks identical today and quietly PINS the
        // value: correct a badly chosen colour in a release, and everybody who once opened
        // the editor keeps the old one with no way to tell why.
        var style = new OverlayStyle();
        style.Set("poi.shrine", new LayerStyle(Colour: "#123456"));
        style.Set("poi.shrine", LayerStyle.Default);

        Assert.DoesNotContain("poi.shrine", style.Changed.Keys);
    }

    [Fact]
    public void OnlyWhatWasChangedIsHeld()
    {
        var style = new OverlayStyle();
        style.Set("poi.breach", new LayerStyle(Hidden: true));

        Assert.Equal(["poi.breach"], style.Changed.Keys);
    }

    [Fact]
    public void ResettingPutsAKeyBack()
    {
        var style = new OverlayStyle();
        style.Set("route.1", new LayerStyle(Width: 9f));
        style.Reset("route.1");

        Assert.Equal(2f, style.Width("route.1", 2f));
        Assert.Empty(style.Changed);
    }

    [Fact]
    public void ResetAllPutsEverythingBack()
    {
        var style = new OverlayStyle();
        style.Set("route.1", new LayerStyle(Width: 9f));
        style.Set("poi.boss", new LayerStyle(Hidden: true));
        style.ResetAll();

        Assert.Empty(style.Changed);
        Assert.True(style.Visible("poi.boss"));
    }

    [Fact]
    public void SomethingHiddenStaysHidden()
    {
        var style = new OverlayStyle();
        style.Set("poi.boss", new LayerStyle(Hidden: true));

        Assert.False(style.Visible("poi.boss"));
    }

    [Fact]
    public void AnUnreadableColourFallsBackRatherThanDrawingNothing()
    {
        // Somebody hand-edits the file and mistypes. Falling through to a transparent black
        // makes the marker VANISH, which reads as the tool being broken rather than as a
        // typo in a colour.
        var style = new OverlayStyle();
        style.Set("poi.boss", new LayerStyle(Colour: "not a colour"));

        Assert.Equal(StyleCatalogue.Fallback("poi.boss"), style.Colour("poi.boss"));
    }

    [Fact]
    public void ANonsenseScaleDoesNotShrinkAMarkerToNothing()
    {
        var style = new OverlayStyle();
        style.Set("poi.boss", new LayerStyle(Scale: 0f));

        Assert.Equal(6f, style.Sized("poi.boss", 6f));
    }

    [Fact]
    public void AlphaSurvivesTheRoundTrip()
    {
        // Half-transparent is a real thing to want for a marker that would otherwise cover
        // the map, so the colour text has to carry alpha rather than assuming opaque.
        var style = new OverlayStyle();
        style.Set("terrain.outline", new LayerStyle(Colour: "#8096C8FF"));

        uint packed = style.Colour("terrain.outline");

        Assert.Equal(0x80u, (packed >> 24) & 0xFF);
        Assert.Equal("#8096C8FF", OverlaySettings.FormatColour(packed));
    }

    [Fact]
    public void TheALLZEROStyleIsTheOrdinaryAppearance()
    {
        // The one that made the flag `Hidden` rather than `Visible`. A struct's zero value is
        // reachable without going through any constructor - `default`, an array slot, a
        // TryGetValue miss, a JSON payload missing a key - so a constructor parameter's
        // default is NOT the default the type has. Written the other way round, every one of
        // those paths produced "invisible at zero scale", and the symptom would have been
        // markers quietly missing from the map.
        //
        // `new()` on a struct is that same zero value, which is what this caught: the "safe
        // default" was reading as invisible from the day it was written.
        LayerStyle zero = default;

        Assert.True(zero.Visible);
        Assert.True(zero.SaysNothing);
        Assert.Equal(zero, LayerStyle.Default);
        Assert.Equal(6f, zero.Sized(6f));
        Assert.Equal(2f, zero.WidthOr(2f));
        Assert.Equal(0xFF00FF00u, zero.ColourOr(0xFF00FF00u));
        Assert.True(new OverlayStyle()["anything at all"].Visible);
    }

    [Fact]
    public void AnEmptyEntryAndAZeroOneMeanTheSameThing()
    {
        // They are NOT equal - the zero value's strings are null and a written one's are
        // empty - and treating that difference as a change is how an entry that says nothing
        // ends up stored, pinning a default that was never chosen.
        Assert.True(new LayerStyle(Colour: "", Icon: "").SaysNothing);
        Assert.True(default(LayerStyle).SaysNothing);
        Assert.NotEqual(default, new LayerStyle(Colour: "", Icon: ""));
    }
}

/// <summary>
/// The catalogue is the promise: what is not in it is not configurable.
/// </summary>
/// <remarks>
/// Which only holds if the catalogue and the drawing code agree on the keys. These tests are
/// the agreement - a key built by the drawing code that the catalogue does not have would
/// otherwise show up as a white marker in-game and nowhere else.
/// </remarks>
public class StyleCatalogueTests
{
    [Fact]
    public void EveryKeyAppearsOnce()
    {
        // A duplicate means one of the two entries can never be reached from the editor,
        // and which one is a detail of list ordering.
        string[] keys = [.. StyleCatalogue.Entries.Select(entry => entry.Key)];

        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryEntryIsUsable()
    {
        foreach (StyleEntry entry in StyleCatalogue.Entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Key));
            Assert.False(string.IsNullOrWhiteSpace(entry.Group));
            Assert.False(string.IsNullOrWhiteSpace(entry.Label));
            Assert.NotEqual(StyleTraits.None, entry.Traits);

            // Fully transparent is not a default anybody chose on purpose, and it would draw
            // as nothing while looking like a colour in the editor.
            Assert.NotEqual(0u, (entry.Fallback >> 24) & 0xFF);
        }
    }

    [Fact]
    public void AnUnknownKeyIsGLARINGRatherThanInvisible()
    {
        // The failure this catches is a drawn thing whose key was never added here. White on
        // a dark map is noticed; a default of black or transparent is not, and the missing
        // entry then survives to a release.
        Assert.Equal(0xFFFFFFFFu, StyleCatalogue.Fallback("nothing.like.this"));
        Assert.Null(StyleCatalogue.Find("nothing.like.this"));
    }

    [Theory]
    [InlineData(ItemRarity.Normal)]
    [InlineData(ItemRarity.Magic)]
    [InlineData(ItemRarity.Rare)]
    [InlineData(ItemRarity.Unique)]
    public void EveryRarityTheOverlayDrawsHasAnEntry(ItemRarity rarity)
    {
        Assert.NotNull(StyleCatalogue.Find(StyleCatalogue.ForRarity("entity.monster", rarity)));
        Assert.NotNull(StyleCatalogue.Find(StyleCatalogue.ForRarity("entity.item", rarity)));
    }

    [Fact]
    public void CurrencyDropsHaveOneToo()
        => Assert.NotNull(StyleCatalogue.Find(StyleCatalogue.ForRarity("entity.item", ItemRarity.Currency)));

    [Fact]
    public void EveryShapeThePlaceLayerCanDrawHasAnEntry()
    {
        // The one that would actually break: a glyph is added for a new mechanic, the layer
        // draws it, and it has no colour of its own because nobody thought of this file.
        foreach (PoiGlyph glyph in Enum.GetValues<PoiGlyph>())
        {
            string key = StyleCatalogue.ForGlyph(glyph);
            Assert.True(StyleCatalogue.Find(key) is not null, $"no catalogue entry for {glyph} (expected {key})");
        }
    }

    [Fact]
    public void EveryKeyTheDrawingCodeNamesIsAReadEntry()
    {
        // These are the ones spelled out at the point of use rather than built. A typo would
        // otherwise be a key the catalogue does not have, which draws WHITE - visible, but
        // only in the game, and only to whoever wonders why one marker looks wrong. This is
        // what moves that to the build.
        foreach (string key in StyleCatalogue.Keys.All)
        {
            Assert.True(StyleCatalogue.Find(key) is not null, $"the drawing code names {key}, the catalogue does not have it");
        }
    }

    [Fact]
    public void EveryKindOfEntityTheOverlayCanDrawHasAnEntry()
    {
        // Including the ones switched off by default. They are reachable from the kind
        // filter, so they get drawn, so a missing entry here is a white dot and a puzzle.
        foreach (EntityKind kind in Enum.GetValues<EntityKind>())
        {
            string key = StyleCatalogue.ForKind(kind);
            Assert.True(StyleCatalogue.Find(key) is not null, $"no catalogue entry for {kind} (expected {key})");
        }
    }

    [Fact]
    public void ARarityNobodyChoseDrawsTheORDINARYWayRatherThanWhite()
    {
        // A drop one frame old has not resolved its rarity yet, and it is SHOWN - missing a
        // unique costs more than glancing at a white one. That is a real case rather than a
        // forgotten catalogue entry, so it must not take the glaring unknown-key colour.
        Assert.Equal("entity.item.normal", StyleCatalogue.ForRarity("entity.item", ItemRarity.Unknown));
        Assert.Equal("entity.monster.normal", StyleCatalogue.ForRarity("entity.monster", (ItemRarity)77));
    }

    [Fact]
    public void EveryRouteTheOverlayWillDrawHasAColour()
    {
        for (int slot = 0; slot < RoutePlanner.MaxRoutes; slot++)
        {
            Assert.NotNull(StyleCatalogue.Find(StyleCatalogue.ForRoute(slot)));
        }
    }

    [Fact]
    public void TheEditorCanBeBuiltFromIt()
    {
        // The editor is GENERATED from this, which is the only arrangement where "all of it
        // can be changed" stays true instead of being true once and then drifting.
        List<IGrouping<string, StyleEntry>> groups = [.. StyleCatalogue.Grouped()];

        Assert.NotEmpty(groups);
        Assert.Equal(StyleCatalogue.Entries.Count, groups.Sum(group => group.Count()));
    }
}

/// <summary>The chosen appearance has to survive a restart, and only it.</summary>
public class OverlayStyleStoreTests
{
    private static string TempPath()
        => Path.Combine(Path.GetTempPath(), $"poeformance-style-{Guid.NewGuid():N}.json");

    [Fact]
    public void WhatWasChangedComesBack()
    {
        var wanted = new OverlayStyle();
        wanted.Set("entity.monster.rare", new LayerStyle(Colour: "#FF00FF", Scale: 1.5f, Icon: "rare.png"));
        wanted.Set("route.2", new LayerStyle(Hidden: true, Width: 3.5f));

        string path = TempPath();
        try
        {
            Assert.True(OverlayStyleStore.Save(wanted, path));
            OverlayStyle loaded = OverlayStyleStore.Load(path);

            Assert.Equal(wanted.Changed, loaded.Changed);
            Assert.Equal("rare.png", loaded["entity.monster.rare"].Icon);
            Assert.False(loaded.Visible("route.2"));
            Assert.Equal(3.5f, loaded.Width("route.2", 1f));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void NothingElseIsWritten()
    {
        // The file holds CHANGES. Writing every catalogue key would freeze today's defaults
        // into it, so correcting one later would reach nobody who had ever opened the editor.
        var style = new OverlayStyle();
        style.Set("poi.shrine", new LayerStyle(Colour: "#101010"));

        string path = TempPath();
        try
        {
            OverlayStyleStore.Save(style, path);
            string text = File.ReadAllText(path);

            Assert.Contains("poi.shrine", text, StringComparison.Ordinal);
            Assert.DoesNotContain("poi.breach", text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnEntryThatSaysNothingIsDroppedOnTheWayIn()
    {
        // Hand-edited files exist, and an entry equal to the default would pin the value the
        // same way saving one would.
        string path = TempPath();
        File.WriteAllText(path, """{"poi.boss":{"hidden":false,"colour":"","width":0,"scale":0,"icon":""}}""");

        try
        {
            Assert.Empty(OverlayStyleStore.Load(path).Changed);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnEntryThatOnlyMENTIONSOneThingKeepsTheRest()
    {
        // The shape somebody would write by hand, and the shape an OLDER version's file has
        // once this gains a field: everything unmentioned has to keep its ordinary
        // appearance. Which is the whole reason each field's zero means "not changed" - the
        // deserialiser fills the gaps with zeroes, and here that is the right answer.
        string path = TempPath();
        File.WriteAllText(path, """{"poi.boss":{"colour":"#ABCDEF"}}""");

        try
        {
            OverlayStyle loaded = OverlayStyleStore.Load(path);

            Assert.True(loaded.Visible("poi.boss"));
            Assert.Equal(6f, loaded.Sized("poi.boss", 6f));
            Assert.Equal(0xFFEFCDABu, loaded.Colour("poi.boss"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void NoFileMeansTheOrdinaryAppearance()
        => Assert.Empty(OverlayStyleStore.Load(Path.Combine(Path.GetTempPath(), "poeformance-style-absent.json")).Changed);

    [Fact]
    public void ABrokenFileDoesNotStopTheTool()
    {
        // Cosmetic settings are not worth refusing to start over.
        string path = TempPath();
        File.WriteAllText(path, "{ this is not json");

        try
        {
            Assert.Empty(OverlayStyleStore.Load(path).Changed);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
