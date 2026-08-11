using PoEformance.Features;
using PoEformance.Game.Components;

namespace PoEformance.Core.Tests;

/// <summary>
/// Pinning a window in place, or handing it to the mouse - and surviving a restart.
/// </summary>
/// <remarks>
/// The drawing half of this is ImGui flags and cannot be tested here. What can is the part
/// that is a decision: which windows are remembered, which are dropped, and that a settings
/// file written by one half of the tool does not silently reset the other half's.
/// </remarks>
public class WindowRuleTests
{
    [Fact]
    public void AWINDOWNobodyPinnedDownSaysNothing()
    {
        // Which is what lets the settings file stay a record of decisions rather than a list
        // of every window that has ever existed.
        Assert.False(WindowRule.Free.Anything);
        Assert.True(new WindowRule(Locked: true).Anything);
        Assert.True(new WindowRule(ClickThrough: true).Anything);
    }

    [Fact]
    public void ANDNobodyHasUntilTheyDo()
    {
        // Absent, not empty: a fresh settings file gains no key at all, and the overlay must
        // read that as "everything draggable" rather than tripping over a null.
        Assert.Null(OverlaySettings.Default.Windows);
        Assert.Empty(OverlaySettings.Default.WindowsOrEmpty);
    }

    [Fact]
    public void WHATEachWindowWasToldSurvivesARestart()
    {
        string path = Path.Combine(Path.GetTempPath(), $"poeformance-windows-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new OverlaySettings(ItemRarity.Rare)
            {
                Windows = new Dictionary<string, WindowRule>
                {
                    ["tools"] = new(Locked: true),
                    ["preload"] = new(Locked: true, ClickThrough: true),
                },
            };

            Assert.True(OverlaySettingsStore.Save(settings, path));
            IReadOnlyDictionary<string, WindowRule> read = OverlaySettingsStore.Load(path).WindowsOrEmpty;

            Assert.Equal(2, read.Count);
            Assert.Equal(new WindowRule(Locked: true), read["tools"]);
            Assert.Equal(new WindowRule(Locked: true, ClickThrough: true), read["preload"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ANDTheConfigurationPageDoesNotResetThem()
    {
        // The failure this guards is invisible: the page sends the four settings it shows,
        // that deserialises into a whole record with everything else at its default, and
        // saving it quietly throws away every window somebody pinned down. The page did what
        // it was asked, the file is valid, and the choices are simply gone.
        var kept = new OverlaySettings(ItemRarity.Magic)
        {
            Windows = new Dictionary<string, WindowRule> { ["tools"] = new(Locked: true) },
        };

        OverlaySettings merged = kept.MergeFromPage(new OverlaySettings(ItemRarity.Unique));

        Assert.Equal(ItemRarity.Unique, merged.MinLootRarity);
        Assert.Equal(new WindowRule(Locked: true), merged.WindowsOrEmpty["tools"]);
    }
}
