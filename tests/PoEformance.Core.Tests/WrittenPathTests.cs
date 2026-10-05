using PoEformance.Features;
using Xunit;

namespace PoEformance.Core.Tests;

/// <summary>
/// The written path in a status line, found by asking whether it exists.
/// </summary>
/// <remarks>
/// THE DISK IS A SET OF PATHS HERE, so every claim is about where the line is cut and never about
/// what is on this machine. The lines are the tool's own, as the dump, the export and the
/// diagnostics write them.
/// </remarks>
public class WrittenPathTests
{
    private static readonly HashSet<string> Disk = new(StringComparer.OrdinalIgnoreCase)
    {
        @"C:\Games\PoEformance\exports\cliffcvmstroma1.files.txt",
        @"C:\Program Files\PoEformance\exports",
        @"D:\tool\preloads\area-17.txt",
    };

    private static string Path(string line)
    {
        (int start, int length) = WrittenPath.Find(line, Disk.Contains);
        return line.Substring(start, length);
    }

    [Fact]
    public void AWRITTENFileAtTheEndOfTheLineIsThePath()
        => Assert.Equal(@"C:\Games\PoEformance\exports\cliffcvmstroma1.files.txt", Path(@"wrote C:\Games\PoEformance\exports\cliffcvmstroma1.files.txt"));

    /// <summary>A path with spaces, and plain words after it with no separator: the longest cut that exists.</summary>
    [Fact]
    public void ANDAPathWithSpacesIsCutWhereTheLineGoesOn()
        => Assert.Equal(@"C:\Program Files\PoEformance\exports", Path(@"nothing in C:\Program Files\PoEformance\exports to send, 3 left"));

    [Fact]
    public void ANDAFolderAtTheEndIsThePathToo()
        => Assert.Equal(@"C:\Program Files\PoEformance\exports", Path(@"wrote 4 files as BossActive/Inactive in C:\Program Files\PoEformance\exports"));

    [Fact]
    public void ANDAFullStopAfterThePathIsNotPartOfIt()
        => Assert.Equal(@"D:\tool\preloads\area-17.txt", Path(@"written to D:\tool\preloads\area-17.txt."));

    /// <summary>A file not written yet is no link, and neither is a drive letter inside a word or a line with no path.</summary>
    [Theory]
    [InlineData(@"writing C:\Games\PoEformance\exports\other.files.txt - reading every tileset, this takes a moment")]
    [InlineData(@"ratio X:\ is not a path")]
    [InlineData(@"could not write the dump: access denied")]
    [InlineData("")]
    public void NOLINKWhereNothingExistingIsNamed(string line)
        => Assert.Equal((0, 0), WrittenPath.Find(line, Disk.Contains));
}
