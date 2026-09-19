using System.Text.RegularExpressions;

namespace PoEformance.Core.Tests;

/// <summary>
/// That the art the model pane's orbit button is drawn with ships inside the overlay's assembly,
/// under the name the portrait asks for it by.
/// </summary>
/// <remarks>
/// A TEXT CHECK, for the reason <see cref="IconDecodeTests"/> gives: the overlay is Windows-only
/// and this suite runs on Linux. What it pins is the three-way agreement a linked resource needs
/// and nothing checks at build time - the file in the repository, the project linking it under a
/// pinned LogicalName (a linked file's generated name is derived from its path outside the project
/// and is neither stable nor predictable), and the portrait matching on the END of that name. Any
/// one of the three drifting leaves the button falling back to its word, under a green build.
/// </remarks>
public class PortraitArtTests
{
    /// <summary>The repository root, found by a file only it has.</summary>
    private static string RepositoryRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "schema", "poe2.offsets.json")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return dir.FullName;
        }
    }

    /// <summary>The name the portrait matches the resource by, read out of its source.</summary>
    private static readonly Regex Asked = new(@"OrbitArt\s*=\s*""(?<name>[^""]+)""", RegexOptions.Compiled);

    /// <summary>A linked resource and the name it is pinned under.</summary>
    private static readonly Regex Linked = new(
        @"<EmbeddedResource\s+Include=""(?<path>[^""]+)""\s+LogicalName=""(?<logical>[^""]+)""",
        RegexOptions.Compiled);

    [Fact]
    public void TheOrbitButtonsArtShipsUnderTheNameThePortraitAsksFor()
    {
        string overlay = Path.Combine(RepositoryRoot, "src", "PoEformance.Overlay");
        Match asked = Asked.Match(File.ReadAllText(Path.Combine(overlay, "MonsterPortrait.cs")));
        Assert.True(asked.Success, "MonsterPortrait no longer names the art it draws its orbit button with as OrbitArt");
        string name = asked.Groups["name"].Value;

        Match? link = null;
        foreach (Match one in Linked.Matches(File.ReadAllText(Path.Combine(overlay, "PoEformance.Overlay.csproj"))))
        {
            if (one.Groups["logical"].Value.EndsWith(name, StringComparison.OrdinalIgnoreCase))
            {
                link = one;
            }
        }

        Assert.True(link is not null, $"PoEformance.Overlay.csproj embeds nothing whose LogicalName ends in {name}");

        // The link points at a real file, and it is a PNG: the eight-byte signature, read rather
        // than decoded so this needs no image library.
        string file = Path.GetFullPath(Path.Combine(overlay, link!.Groups["path"].Value.Replace('\\', Path.DirectorySeparatorChar)));
        Assert.True(File.Exists(file), $"the project links {file}, which is not there");

        byte[] head = new byte[8];
        using (FileStream art = File.OpenRead(file))
        {
            Assert.Equal(head.Length, art.ReadAtLeast(head, head.Length, throwOnEndOfStream: false));
        }

        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, head);
    }
}
