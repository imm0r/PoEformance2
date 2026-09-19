namespace PoEformance.Core.Tests;

/// <summary>
/// That every face the interface pushes is popped again.
/// </summary>
/// <remarks>
/// A TEXT CHECK, for the reason <see cref="IconDecodeTests"/> gives: the overlay layer is
/// Windows-only and this suite runs on Linux, so nothing here can call <c>OverlayFonts</c>.
///
/// WHAT IT IS PINNING, in that class's own words: an unbalanced pair is a font stack that never
/// unwinds, and the symptom of that is THE WHOLE INTERFACE SILENTLY GROWING, one frame at a time,
/// with nothing to point at. It is the failure that made <c>OverlayFonts.Heading</c> take a
/// callback instead of offering a pair; the three pairs that are still pairs - the monospace, the
/// display figures, and the heading where a branch has to sit between the push and the pop - are
/// what this counts. The monster book is why it is written now: it puts its whole tab in the
/// monospace, so the pair there wraps everything the window draws and a missed pop would be the
/// worst-placed one in the tool.
///
/// COUNTING IS NOT EVERYTHING and the honest limit is worth stating: a pop that exists but is not
/// in a <c>finally</c> counts the same as one that is, so this catches the forgotten pop and not
/// the one an exception jumps over. That second case is guarded by the shape the callers already
/// use, which this does not enforce.
/// </remarks>
public class OverlayFontStackTests
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

    [Theory]
    [InlineData("Mono")]
    [InlineData("Figures")]
    [InlineData("Heading")]
    public void EveryFacePushedIsPoppedAgain(string face)
    {
        var pushes = 0;
        var pops = 0;

        foreach (string file in Sources())
        {
            string source = File.ReadAllText(file);
            int push = Count(source, $"OverlayFonts.Push{face}(");
            int pop = Count(source, $"OverlayFonts.Pop{face}(");
            pushes += push;
            pops += pop;

            // PER FILE, because that is where the mistake is made and where it has to be found.
            // A tool-wide total would let one file's spare pop cover another file's missing one,
            // which is two faults reading as none.
            Assert.True(
                push == pop,
                $"{Path.GetFileName(file)} pushes the {face} face {push} time(s) and pops it {pop} time(s). "
                + "An unbalanced pair is a font stack that never unwinds, and the symptom is the "
                + "whole interface growing a little on every frame - see OverlayFonts.");
        }

        // A FLOOR, so a rename that leaves this matching nothing at all fails rather than passes.
        // Each of the three faces is pushed somewhere: the monospace by the status bar and the
        // monster book, the display figures by the flask and skill layers, the heading by the tab
        // bar and the monster book's own name line.
        Assert.True(pushes > 0, $"nothing pushes the {face} face any more, so this check guards nothing");
        Assert.Equal(pushes, pops);
    }

    /// <summary>Every file that could push a face: the overlay's own, and the configuration window's.</summary>
    private static IEnumerable<string> Sources()
    {
        foreach (string layer in new[] { "PoEformance.Overlay", "PoEformance.Config" })
        {
            string folder = Path.Combine(RepositoryRoot, "src", layer);
            if (Directory.Exists(folder))
            {
                foreach (string file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
                {
                    yield return file;
                }
            }
        }
    }

    private static int Count(string source, string what)
    {
        var found = 0;
        for (var at = source.IndexOf(what, StringComparison.Ordinal); at >= 0;
             at = source.IndexOf(what, at + what.Length, StringComparison.Ordinal))
        {
            found++;
        }

        return found;
    }
}
