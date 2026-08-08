using System.Numerics;
using PoEformance.Features;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Which maps a ritual line may take, and which it may start from.
/// </summary>
/// <remarks>
/// A rule of the GAME rather than a detail of this tool, which is why it is reachable by a test
/// at all: getting it wrong plans routes the game refuses to let anybody walk, and the symptom
/// is a click that does nothing rather than an error.
/// </remarks>
public class RitualWatchTests
{
    private static AtlasMapNames Names()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "data", "atlas-maps.json")))
        {
            dir = dir.Parent;
        }

        return AtlasMapNames.Load(Path.Combine(dir!.FullName, "data", "atlas-maps.json"));
    }

    private static AtlasNode Node(int x, int y, string mapId, AtlasNodeState state)
        => new(
            Index: (x * 100) + y,
            Address: 0x1000,
            MapId: mapId,
            Grid: (x, y),
            State: state,
            Biome: 0,
            Connections: [],
            Screen: new Vector2(x * 10, y * 10),
            Size: Vector2.Zero,
            BadgeIds: [],
            ContentTokens: []);

    [Fact]
    public void AFinishedMapCanNeverJoinALine()
    {
        AtlasNode[] atlas =
        [
            Node(0, 0, "MapAugury", AtlasNodeState.Open),
            Node(1, 0, "MapArroyo", AtlasNodeState.Completed),
        ];

        HashSet<(int X, int Y)> blocked = RitualWatch.Blocked(atlas, Names());

        Assert.Contains((1, 0), blocked);
        Assert.DoesNotContain((0, 0), blocked);
    }

    [Fact]
    public void ANDNeitherCanAUniqueATowerOrAHideout()
    {
        // The game tests a field on the map's own row for this; the reference falls back to the
        // same categories by tag, which is what is available here. All three are ordinary maps
        // by state - nothing but the category tells them apart.
        AtlasNode[] atlas =
        [
            Node(0, 0, "MapAugury", AtlasNodeState.Open),                     // ordinary
            Node(1, 0, "MapUniqueReactor_04", AtlasNodeState.Open),           // unique
            Node(2, 0, "MapLostTowers", AtlasNodeState.Open),                 // tower
            Node(3, 0, "MapHideoutCanal_Claimable", AtlasNodeState.Open),     // hideout
        ];

        HashSet<(int X, int Y)> blocked = RitualWatch.Blocked(atlas, Names());

        Assert.DoesNotContain((0, 0), blocked);
        Assert.Contains((1, 0), blocked);
        Assert.Contains((2, 0), blocked);
        Assert.Contains((3, 0), blocked);
    }

    [Fact]
    public void ALINEStartsOnlyFromAMapThatCanBeEnteredNow()
    {
        AtlasNode[] atlas =
        [
            Node(0, 0, "MapAugury", AtlasNodeState.Open),
            Node(1, 0, "MapArroyo", AtlasNodeState.Locked),
            Node(2, 0, "MapBastille", AtlasNodeState.Completed),
        ];

        Assert.Equal([(0, 0)], RitualWatch.Startable(atlas, Names()));
    }

    [Fact]
    public void ANDNotFromOneItCouldNotTakeAnyway()
    {
        // A tower can be entered and cannot join a line, so it is reachable AND not a start.
        // Missing this plans a whole atlas of routes from somewhere the first click is refused.
        AtlasNode[] atlas =
        [
            Node(0, 0, "MapLostTowers", AtlasNodeState.Open),
            Node(1, 0, "MapAugury", AtlasNodeState.Open),
        ];

        Assert.Equal([(1, 0)], RitualWatch.Startable(atlas, Names()));
    }

    [Fact]
    public void AMapNobodyHasWrittenDownIsStillOrdinary()
    {
        // A league adds maps the table has not heard of. Treating an unknown map as special
        // would quietly stop the line being planned through anything new.
        AtlasNode[] atlas = [Node(0, 0, "MapSomethingNewNextLeague", AtlasNodeState.Open)];

        Assert.Empty(RitualWatch.Blocked(atlas, Names()));
        Assert.Equal([(0, 0)], RitualWatch.Startable(atlas, Names()));
    }

    [Fact]
    public void WITHOUTARewardListNothingIsPredictedAndItSaysSo()
    {
        // The ordinary state after a league changes the rewards and before somebody updates the
        // file. It must not read as the line being unreadable.
        var view = RitualView.Idle with
        {
            Drawing = true,
            Status = "no reward list - data/ritual-mods.json is missing, so nothing can be predicted",
        };

        Assert.True(view.Drawing);
        Assert.False(view.Anything);
        Assert.Contains("ritual-mods.json", view.Status);
    }

    [Fact]
    public void THESettingsRememberWhatTheRewardsAreWorth()
    {
        string path = Path.Combine(Path.GetTempPath(), $"atlas-ritual-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new AtlasSettings(RitualWorth: new Dictionary<string, int>
            {
                ["Exalted Orbs x4"] = 50,
                ["+Free Reroll"] = -10,
            });

            Assert.True(AtlasStore.Save(settings, path));

            AtlasSettings back = AtlasStore.Load(path);
            Assert.Equal(50, back.Worth["Exalted Orbs x4"]);
            Assert.Equal(-10, back.Worth["+Free Reroll"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ANDShipNoOpinionAboutThemAtAll()
    {
        // Unlike the groups, which ship a list: what a reward is worth depends entirely on what
        // this player needs, and a shipped opinion would sort everybody's routes by mine.
        Assert.Empty(AtlasSettings.Default.Worth);
    }
}
