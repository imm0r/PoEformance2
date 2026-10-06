using PoEformance.Core.Schema;
using PoEformance.Game.Components;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>From a monolith's device to its station, and every field the station holds.</summary>
public class MonolithReaderTests
{
    private static OffsetSchema Schema() => MonolithFixture.ShippedSchema();

    private static MonolithReader Reader(MonolithFixture fixture, OffsetSchema schema)
        => new(fixture.Reader, schema, new StateMachineReader(fixture.Reader, schema));

    [Fact]
    public void TheStationIsFoundPastADecoyListener_ByItsOwner()
    {
        OffsetSchema schema = Schema();
        var fixture = new MonolithFixture(schema);
        MonolithReader reader = Reader(fixture, schema);

        ulong station = reader.FindStation(MonolithFixture.Machine, MonolithFixture.Device, out string why);

        Assert.Equal(MonolithFixture.Station, station);
        Assert.Equal(string.Empty, why);

        // A device nothing listens for: not found, and the readout says how many were tried.
        Assert.Equal(0UL, reader.FindStation(MonolithFixture.Machine, 0x777, out why));
        Assert.Contains("2 checked", why, StringComparison.Ordinal);

        Assert.Equal(0UL, reader.FindStation(0, MonolithFixture.Device, out why));
        Assert.Contains("no StateMachine", why, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryFieldReads_AndTheAnchorIsArithmeticOverTheRuneTable()
    {
        OffsetSchema schema = Schema();
        var fixture = new MonolithFixture(
            schema, holes: 7, anchorRune: 26, anchorHole: 3, glow: [3, 5], mode: 1, empowered: true,
            selected: "7SlotBondOfTheRunes1", panelOpen: true);
        MonolithReader reader = Reader(fixture, schema);

        MonolithStation station = reader.Read(MonolithFixture.Station);

        Assert.True(station.Resolved);
        Assert.Equal(string.Empty, station.Why);
        Assert.Equal(7, station.HoleCount);
        Assert.Equal(26, station.AnchorRune);
        Assert.Equal(3, station.AnchorHole);
        Assert.False(station.Anchorless);
        Assert.Equal([3, 5], station.GlowSockets);
        Assert.Equal(1, station.RecipeMode);
        Assert.True(station.FramesASocket);
        Assert.True(station.Empowered);
        Assert.Equal("7SlotBondOfTheRunes1", station.SelectedRecipeId);
        Assert.True(station.Committed);
        Assert.True(station.PanelOpen);
    }

    [Fact]
    public void ThePanelIsOpenOnlyForAPointerIntoTheModule()
    {
        OffsetSchema schema = Schema();
        var fixture = new MonolithFixture(schema, panelOpen: true);
        MonolithReader reader = Reader(fixture, schema);
        Assert.True(reader.Read(MonolithFixture.Station).PanelOpen);

        // A heap address in that slot is not the listener - it is whatever was there.
        fixture.PanelListenerOffModule();
        Assert.False(reader.Read(MonolithFixture.Station).PanelOpen);

        // And a shut panel reads null there.
        Assert.False(Reader(new MonolithFixture(schema), schema).Read(MonolithFixture.Station).PanelOpen);
    }

    [Fact]
    public void AnAnchorlessStationSaysSo_AndAMisalignedAnchorIsRefusedWithAReason()
    {
        OffsetSchema schema = Schema();
        var unique = new MonolithFixture(schema, anchorless: true, mode: 3, holes: 10);
        MonolithStation station = Reader(unique, schema).Read(MonolithFixture.Station);
        Assert.True(station.Anchorless);
        Assert.Equal(-1, station.AnchorRune);
        Assert.Equal(string.Empty, station.Why);
        Assert.False(station.FramesASocket);

        var drifted = new MonolithFixture(schema);
        drifted.MisalignAnchor();
        MonolithStation refused = Reader(drifted, schema).Read(MonolithFixture.Station);
        Assert.True(refused.Resolved);
        Assert.False(refused.Anchorless);
        Assert.Equal(-1, refused.AnchorRune);
        Assert.Contains("stride", refused.Why, StringComparison.Ordinal);
        Assert.Equal(5, refused.HoleCount);
    }

    [Fact]
    public void AChosenRecipeThatDoesNotReadAsOneIsDropped_WithTheReason()
    {
        OffsetSchema schema = Schema();
        var fixture = new MonolithFixture(schema, selected: "garbage");
        MonolithStation station = Reader(fixture, schema).Read(MonolithFixture.Station);

        Assert.Equal(string.Empty, station.SelectedRecipeId);
        Assert.False(station.Committed);
        Assert.Contains("garbage", station.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDeviceStatesAreNamed()
    {
        OffsetSchema schema = Schema();
        var fixture = new MonolithFixture(schema, holes: 8, activated: 7, rerolled: true);
        MonolithStates states = Reader(fixture, schema).States(MonolithFixture.Machine);

        // The state caps at six where the station says eight - the plugin's finding, and why
        // the station's count is the one believed.
        Assert.Equal(6, states.Sockets);
        Assert.Equal(7, states.Activated);
        Assert.True(states.Rerolled);
        Assert.True(states.LooksCollected);
        Assert.Contains("sockets=6", states.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void AreaTagsRead_AnEmptyVectorIsASet_AndGarbageIsNull()
    {
        OffsetSchema schema = Schema();
        var tagged = new MonolithFixture(schema, areaTags: [54, 12]);
        MonolithReader reader = Reader(tagged, schema);
        IReadOnlySet<int>? tags = reader.AreaTags(MonolithFixture.Area);
        Assert.NotNull(tags);
        Assert.Equal(new HashSet<int> { 54, 12 }, tags);

        var untagged = new MonolithFixture(schema, areaTags: []);
        IReadOnlySet<int>? none = Reader(untagged, schema).AreaTags(MonolithFixture.Area);
        Assert.NotNull(none);
        Assert.Empty(none);

        // Nothing mapped where the vector should be: the gate switches OFF rather than dropping.
        Assert.Null(reader.AreaTags(MonolithFixture.Area + 0x10_0000));
        Assert.Null(reader.AreaTags(0));
    }

    [Fact]
    public void NothingIsAStationAndSaysSo()
    {
        OffsetSchema schema = Schema();
        MonolithStation none = Reader(new MonolithFixture(schema), schema).Read(0);
        Assert.False(none.Resolved);
        Assert.Equal("no station", none.Why);
    }
}
