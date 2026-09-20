using PoEformance.Features;

namespace PoEformance.Core.Tests;

/// <summary>
/// The version the overlay's title bar carries.
/// </summary>
/// <remarks>
/// WHAT THESE ARE FOR, and it is not the formatting. The number is maintained by hand and the
/// one way it fails is by not being maintained - so what is worth checking is that it is a
/// real version rather than a placeholder, and that a build with no stamp says so out loud
/// instead of reading like a released one. See CLAUDE.md, "Raise the version on every push".
/// </remarks>
public class ToolVersionTests
{
    [Fact]
    public void TheNumberIsThreeParts()
    {
        string[] parts = ToolVersion.Number.Split('.');

        Assert.Equal(3, parts.Length);
        Assert.All(parts, part => Assert.True(
            part.Length > 0 && part.All(char.IsAsciiDigit),
            $"\"{ToolVersion.Number}\" is not a version number"));

        Assert.Equal("v" + ToolVersion.Number, ToolVersion.Said);
    }

    [Fact]
    public void ABuildWithNoStampSaysItIsLocal()
    {
        // The state a developer's own build is in, and the one answer that stops its version
        // being mistaken for a released one.
        Assert.Equal($"v{ToolVersion.Number} · local", ToolVersion.With(null));
        Assert.Equal($"v{ToolVersion.Number} · local", ToolVersion.With(BuildStamp.Unknown));

        // A stamp with half of what it needs cannot say which build it is either - see
        // BuildStamp.Known, which wants the commit AND the time.
        Assert.Equal(
            $"v{ToolVersion.Number} · local",
            ToolVersion.With(new BuildStamp { Commit = "4659c188d5e896071f383eaf646b8e1bcd46a922" }));
    }

    [Fact]
    public void AStampedBuildCarriesItsCommit()
    {
        var stamp = new BuildStamp
        {
            Tag = "latest-dev",
            Commit = "4659c188d5e896071f383eaf646b8e1bcd46a922",
            BuiltUtc = DateTimeOffset.UtcNow,
            RunNumber = 412,
        };

        // Seven characters, the way the commit is written everywhere else in this tool.
        Assert.Equal($"v{ToolVersion.Number} · 4659c18", ToolVersion.With(stamp));
    }
}
