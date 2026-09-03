using PoEformance.Features;
using PoEformance.Game.Components;

namespace PoEformance.Core.Tests;

/// <summary>
/// What survives closing the tool about the layout's rooms.
/// </summary>
/// <remarks>
/// A pinned room is a statement about ONE area's layout, so the picks are filed under the
/// area's own id. The rest of the file has to be untouched by that: a visit to one map must
/// not take another map's picks with it, and a person who never opens this must gain no key.
/// </remarks>
public class RoomSettingsTests
{
    [Fact]
    public void PicksAreKeptPerArea()
    {
        RoomSettings settings = RoomSettings.Default
            .With("G2_8", ["a.tdt@1,1"])
            .With("G1_2", ["b.tdt@2,2"]);

        Assert.Equal(["a.tdt@1,1"], settings.In("G2_8"));
        Assert.Equal(["b.tdt@2,2"], settings.In("G1_2"));
        Assert.Empty(settings.In("MapRiverhold"));
    }

    [Fact]
    public void AnAreaWithNothingLeftPinnedLosesItsEntry()
    {
        // Unpinning what you pinned has to leave the file as it was found, or every area ever
        // visited accumulates an empty list forever.
        RoomSettings settings = RoomSettings.Default.With("G2_8", ["a.tdt@1,1"]).With("G2_8", []);

        Assert.Null(settings.Picked);
    }

    [Fact]
    public void AnAreaThatDidNotResolveIsNotAKey()
        => Assert.Null(RoomSettings.Default.With(string.Empty, ["a.tdt@1,1"]).Picked);

    [Theory]
    [InlineData(0, 1)]
    [InlineData(3, 3)]
    [InlineData(9000, 64)]
    public void TheSmallestRoomStaysInRange(int written, int expected)
    {
        // Capped rather than merely floored: a threshold above the biggest room in the area
        // hides every label, which reads as the feature being broken.
        Assert.Equal(expected, new RoomSettings(MinTiles: written).Normalised().MinTiles);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(4, 4)]
    [InlineData(100000, 1000)]
    public void HowOftenAFileMayBePlacedStaysInRange(int written, int expected)
        => Assert.Equal(expected, new RoomSettings(MaxPlacements: written).Normalised().MaxPlacements);

    [Fact]
    public void TheDefaultsAreOffAndPastTheScenery()
    {
        // Off because this writes a name on every room in the area, and two tiles because a
        // one-tile room is a rock or a strip of wall - there are hundreds of those. Four
        // placements is TerrainLandmarks' own number, and it is the one that does the thinning:
        // size cannot, because an area is built from one module repeated.
        Assert.False(RoomSettings.Default.Show);
        Assert.Equal(2, RoomSettings.Default.MinTiles);
        Assert.Equal(4, RoomSettings.Default.MaxPlacements);
        Assert.Null(RoomSettings.Default.Picked);
    }

    [Fact]
    public void PicksSurviveTheSettingsFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"poeformance-rooms-{Guid.NewGuid():N}.json");
        try
        {
            var written = new OverlaySettings(ItemRarity.Magic)
            {
                Rooms = RoomSettings.Default with
                {
                    Show = true,
                    MinTiles = 4,
                    MaxPlacements = 7,
                    Filter = "exit",
                    Picked = new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["G2_8"] = ["Metadata/Terrain/X/exit_01.tdt@12,34"],
                    },
                },
            };

            Assert.True(OverlaySettingsStore.Save(written, path));

            RoomSettings read = OverlaySettingsStore.Load(path).RoomsOrDefault;
            Assert.True(read.Show);
            Assert.Equal(4, read.MinTiles);
            Assert.Equal(7, read.MaxPlacements);
            Assert.Equal("exit", read.Filter);
            Assert.Equal(["Metadata/Terrain/X/exit_01.tdt@12,34"], read.In("G2_8"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AFileThatSaysNothingAboutRoomsGetsTheDefaults()
    {
        // An untouched settings file gains no key, and the defaults keep coming from the code
        // where a correction can still reach somebody who never opened the switches.
        var settings = new OverlaySettings(ItemRarity.Magic);

        Assert.Null(settings.Rooms);
        Assert.Equal(RoomSettings.Default, settings.RoomsOrDefault);
        Assert.Null(settings.Normalised().Rooms);
    }
}
