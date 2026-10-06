using PoEformance.Features;

namespace PoEformance.Core.Tests;

/// <summary>What the Runecraft settings keep, and what a hand-edited file cannot make them do.</summary>
public class RunecraftSettingsTests
{
    [Fact]
    public void TheDefaultsAreOffAndRelative()
    {
        RunecraftSettings settings = RunecraftSettings.Default;

        Assert.False(settings.Enabled);
        Assert.Equal(RunecraftColourMode.Relative, settings.ColourMode);
        Assert.Equal(0f, settings.XOffset);
        Assert.Equal(1f, settings.Writing);
        Assert.True(settings.FrameBest);
    }

    [Fact]
    public void NormalisedKeepsEveryValueDrawable()
    {
        RunecraftSettings wild = new RunecraftSettings(
            Enabled: true,
            XOffset: 9_999f,
            TextScale: 40f,
            GoodFrom: -1f,
            BadBelow: float.NaN).Normalised();

        Assert.Equal(RunecraftSettings.FurthestOffset, wild.XOffset);
        Assert.Equal(RunecraftSettings.LargestText, wild.TextScale);
        Assert.Equal(RunecraftSettings.Default.GoodFrom, wild.GoodFrom);
        Assert.Equal(RunecraftSettings.Default.BadBelow, wild.BadBelow);

        // Zero text is "as the row", and stays zero rather than being clamped up to the floor.
        Assert.Equal(0f, new RunecraftSettings(TextScale: 0f).Normalised().TextScale);
        Assert.Equal(0f, new RunecraftSettings(TextScale: -2f).Normalised().TextScale);
    }

    [Fact]
    public void SavedSettingsComeBackAsWritten()
    {
        string path = Path.Combine(Path.GetTempPath(), $"runecraft-{Guid.NewGuid():N}.json");
        try
        {
            var written = new RunecraftSettings(
                Enabled: true,
                ColourMode: RunecraftColourMode.Absolute,
                XOffset: -40f,
                TextScale: 1.5f,
                FrameBest: false,
                GoodFrom: 12f,
                BadBelow: 2f,
                ShowUnpriced: true);

            Assert.True(RunecraftStore.Save(written, path));
            Assert.Equal(written, RunecraftStore.Load(path));

            // The enum is written as a word, so the file can be read and edited by a person.
            Assert.Contains("\"Absolute\"", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheMapAndListSettingsRoundTrip_AndNormalise()
    {
        var written = new RunecraftSettings(Enabled: true, MapLabels: false, MapSockets: false, ListMinEx: 12.5f);
        string path = Path.Combine(Path.GetTempPath(), $"runecraft-{Guid.NewGuid():N}.json");
        try
        {
            Assert.True(RunecraftStore.Save(written, path));
            Assert.Equal(written, RunecraftStore.Load(path));
            Assert.Contains("\"mapLabels\"", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }

        Assert.True(RunecraftSettings.Default.MapLabels);
        Assert.True(RunecraftSettings.Default.MapSockets);
        Assert.Equal(0f, new RunecraftSettings(ListMinEx: -3f).Normalised().ListMinEx);
        Assert.Equal(0f, new RunecraftSettings(ListMinEx: float.NaN).Normalised().ListMinEx);
    }

    [Fact]
    public void AMissingOrBrokenFileLeavesTheDefaults()
    {
        string path = Path.Combine(Path.GetTempPath(), $"runecraft-{Guid.NewGuid():N}.json");
        Assert.Equal(RunecraftSettings.Default, RunecraftStore.Load(path));

        try
        {
            File.WriteAllText(path, "{ not json");
            Assert.Equal(RunecraftSettings.Default, RunecraftStore.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
