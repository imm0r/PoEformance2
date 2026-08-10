using System.Text.RegularExpressions;

namespace PoEformance.Core.Tests;

/// <summary>
/// That the overlay hands its pictures to the renderer in the format the renderer draws in.
/// </summary>
/// <remarks>
/// A TEXT CHECK, AND ON PURPOSE. The overlay layer is Windows-only and this suite runs on Linux,
/// so nothing here can reference <c>IconCache</c>, let alone a Direct3D device - and the flag this
/// is about is a bool that compiles either way and changes nothing except how every picture looks.
///
/// It is worth pinning anyway, because it went wrong for the whole life of the item pictures and
/// nothing could have caught it. Handing the renderer an sRGB texture makes the sampler decode
/// each texel into linear light, and ClickableTransparentOverlay's swap chain is plain
/// <c>R8G8B8A8_UNorm</c>, so nothing encodes back: every midtone reaches the screen about a stop
/// and a half down. Item art that is duller than the same art in the game, on every icon, from
/// every source - which is exactly how it was reported, and which no compiler, analyzer or unit
/// test in this repo would have said a word about.
/// </remarks>
public class IconColourTests
{
    /// <summary>Where the overlay's sources are.</summary>
    private static string OverlaySources
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "schema", "poe2.offsets.json")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return Path.Combine(dir.FullName, "src", "PoEformance.Overlay");
        }
    }

    /// <summary>A call into the renderer's upload, and its arguments.</summary>
    /// <remarks>
    /// No nested brackets, because there are none at these call sites and a regex that tried to
    /// allow them would match half the file when somebody adds one. If that changes, this stops
    /// finding the calls and the count check below fails - which is the intended failure, and
    /// louder than quietly matching nothing.
    /// </remarks>
    private static readonly Regex Uploads = new(@"_upload\((?<args>[^()]*)\)", RegexOptions.Compiled);

    [Fact]
    public void EVERYPictureIsUploadedInTheFormatTheSwapChainDrawsIn()
    {
        var found = new List<(string File, string Flag)>();

        foreach (string file in Directory.EnumerateFiles(OverlaySources, "*.cs", SearchOption.AllDirectories))
        {
            foreach (Match call in Uploads.Matches(File.ReadAllText(file)))
            {
                string[] arguments = call.Groups["args"].Value.Split(',');
                found.Add((Path.GetFileName(file), arguments[^1].Trim()));
            }
        }

        // Both of them: the icons and the terrain mask. Written as a floor rather than an exact
        // number so adding a third picture does not fail this, but a RENAME - which would leave
        // the check matching nothing at all and passing - does.
        Assert.True(found.Count >= 2, $"found {found.Count} upload calls in {OverlaySources}, expected at least 2");

        foreach ((string file, string flag) in found)
        {
            // false, or the named constant that is false. Nothing else: the last argument is the
            // sRGB flag, and true is the bug this test exists for.
            Assert.True(
                flag is "false" or "Srgb" or "IconCache.Srgb",
                $"{file} uploads a texture with srgb: {flag}. The swap chain is R8G8B8A8_UNorm, "
                + "so an sRGB texture is decoded to linear on the way in and never encoded back - "
                + "see IconCache.Srgb.");
        }
    }

    [Fact]
    public void ANDTheNamedConstantIsTheOneThatSaysNo()
    {
        // The flag above is allowed to be a name, so the name has to be checked too - otherwise
        // "Srgb" passes while meaning true, which is the whole bug wearing a different spelling.
        string source = File.ReadAllText(Path.Combine(OverlaySources, "IconCache.cs"));

        Assert.Contains("public const bool Srgb = false;", source, StringComparison.Ordinal);
    }
}
