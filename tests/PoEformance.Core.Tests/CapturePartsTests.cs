using PoEformance.Features;

namespace PoEformance.Core.Tests;

/// <summary>
/// The capture's catalogue: every part once, under a listed heading, writing its own file; what the ticked ones ask of the recording; and the unticked keys kept with the settings.
/// </summary>
public class CapturePartsTests
{
    /// <summary>Keys and files are unique, every part sits under a heading the page lists, and the recording's own parts are where the page puts them last.</summary>
    [Fact]
    public void THECATALOGUEListsEveryPartOnceUnderAListedHeading()
    {
        Assert.Equal(CaptureParts.All.Count, CaptureParts.All.Select(part => part.Key).Distinct(StringComparer.Ordinal).Count());
        string[] files = [.. CaptureParts.All.Where(part => part.File.Length > 0).Select(part => part.File)];
        Assert.Equal(files.Length, files.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(CaptureParts.All, part => Assert.Contains(part.Group, CaptureParts.Groups));
        Assert.Equal(CaptureParts.All.Count, CaptureParts.Groups.Sum(group => CaptureParts.In(group).Count()));

        Assert.Equal(CaptureReport.GameShot, CaptureParts.All.Single(part => part.Key == CaptureParts.GamePicture).File);
        Assert.Equal(CaptureReport.MemoryFile, CaptureParts.All.Single(part => part.Key == CaptureParts.Memory).File);
        Assert.Equal(string.Empty, CaptureParts.All.Single(part => part.Key == CaptureParts.RawSweep).File);
        Assert.Equal("memory.rec: ", CaptureParts.All.Single(part => part.Key == CaptureParts.Memory).Hover[..12]);
        Assert.Equal("Memory", CaptureParts.Groups[^1]);
    }

    /// <summary>What the pass reads is the union over the ticked parts, and nothing at all once the recording itself is off.</summary>
    [Fact]
    public void THERECORDINGReadsWhatTheTickedPartsAsk()
    {
        var none = new HashSet<string>(StringComparer.Ordinal);
        Assert.Equal(CaptureReads.World | CaptureReads.Loaded | CaptureReads.Doodads | CaptureReads.Sweep | CaptureReads.Monsters, CaptureParts.Reads(none));

        var doodadsOnly = new HashSet<string>(
            CaptureParts.All.Select(part => part.Key).Where(key => key is not (CaptureParts.Doodads or CaptureParts.RoomsPlaced or CaptureParts.Memory)),
            StringComparer.Ordinal);
        Assert.Equal(CaptureReads.Doodads, CaptureParts.Reads(doodadsOnly));
        Assert.True(CaptureParts.IsOn(CaptureParts.Doodads, doodadsOnly));
        Assert.False(CaptureParts.IsOn(CaptureParts.Entities, doodadsOnly));

        Assert.Equal(CaptureReads.Loaded | CaptureReads.Doodads, CaptureParts.Reads(new HashSet<string>([CaptureParts.Entities, CaptureParts.RawSweep, CaptureParts.Monsters], StringComparer.Ordinal)));

        // The monsters bring the world read they list from, and live only inside the recording.
        Assert.True(CaptureMemory.ReadsWorld(CaptureReads.Monsters));
        Assert.True(CaptureParts.NeedsRecording(CaptureParts.Monsters));
        Assert.True(CaptureParts.NeedsRecording(CaptureParts.RawSweep));
        Assert.False(CaptureParts.NeedsRecording(CaptureParts.Entities));
        Assert.Equal(CaptureReads.None, CaptureParts.Reads(new HashSet<string>([CaptureParts.Memory], StringComparer.Ordinal)));

        Assert.True(CaptureMemory.ReadsWorld(CaptureReads.Sweep));
        Assert.False(CaptureMemory.ReadsWorld(CaptureReads.Doodads | CaptureReads.Loaded));
        Assert.Equal("the loaded files, both entity maps with every named entity's model", CaptureMemory.Said(CaptureReads.Loaded | CaptureReads.Doodads));
    }

    /// <summary>The unticked keys come back from the settings file; a key the catalogue no longer has is dropped; an untouched file switches nothing off.</summary>
    [Fact]
    public void THEUNTICKEDPartsAreKeptWithTheSettings()
    {
        Assert.Equal(
            [CaptureParts.Entities, CaptureParts.RawSweep],
            CaptureParts.Kept(new HashSet<string>(["gone-part", CaptureParts.RawSweep, CaptureParts.Entities], StringComparer.Ordinal)));

        string path = Path.Combine(Path.GetTempPath(), $"capture-off-{Guid.NewGuid():N}.json");
        try
        {
            Assert.True(OverlaySettingsStore.Save(OverlaySettings.Default with { CaptureOff = [CaptureParts.RawSweep, CaptureParts.GamePicture] }, path));
            Assert.Equal([CaptureParts.RawSweep, CaptureParts.GamePicture], OverlaySettingsStore.Load(path).CaptureOffOrEmpty);
            Assert.Contains("captureOff", File.ReadAllText(path), StringComparison.Ordinal);

            File.WriteAllText(path, """{"minLootRarity":2}""");
            Assert.Empty(OverlaySettingsStore.Load(path).CaptureOffOrEmpty);
            Assert.Empty(OverlaySettings.Default.CaptureOffOrEmpty);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
