using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Diagnostics;

namespace PoEformance.Core.Tests;

/// <summary>
/// The hunt for the two candidates nothing in this tool reads.
/// </summary>
/// <remarks>
/// There is nothing here to verify the OFFSETS with, and that is the point rather than a gap:
/// no build has ever read `InGameState+0x300`'s sub-object or `Monster+0x27`, so not one of the
/// nineteen committed recordings contains those bytes. The hunt exists to change that, and
/// until a capture is made with it the only thing testable is the property that matters most
/// for a hunt - THAT IT REPORTS BEING UNABLE TO ANSWER, rather than reading absent memory as a
/// result.
///
/// That is the failure this project keeps paying for in other forms: an unread field returning
/// zero looks exactly like a field that means zero. A hunt which announced "the byte is 0 on
/// every monster, so 0x27 is wrong" from a file that never read the byte would be worse than
/// no hunt at all.
/// </remarks>
public class HoverHuntTests
{
    private static string Fixture(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "tests", "fixtures", name);
    }

    private static List<HoverSample> Replay(string fixture)
    {
        using var replay = ReplayMemoryReader.Load(File.OpenRead(Fixture(fixture)));
        OffsetSchema schema = RealSessionTests.Schema();
        var hunt = new HoverHunt(replay, schema);
        ulong gameStates = replay.ResolvedStatics["GameStates"];

        var samples = new List<HoverSample>();
        for (uint frame = 0; frame < replay.FrameCount; frame += 5)
        {
            replay.Seek(frame);
            if (hunt.SampleFrame(gameStates) is { } sample)
            {
                samples.Add(sample);
            }
        }

        return samples;
    }

    [Fact]
    public void ItSamplesASessionWithoutInventingAnything()
    {
        List<HoverSample> samples = Replay("session-2026-08-frustum.rec");
        Assert.NotEmpty(samples);

        // Not even the FIRST hop is in an ordinary session, which is worth pinning because the
        // obvious assumption is wrong: the chain walk reads InGameState at 0x290, 0x2F0 and
        // 0x368 by name, not as a block, so 0x300 is untouched like everything else. It shows
        // up only in the --questflags captures, which sweep the struct wholesale.
        Assert.All(samples, s => Assert.Equal(0ul, s.Host));

        // And everything past it: the sub-object and the Monster component are separate
        // allocations nobody has read, so a replay cannot serve them, and the hunt must come
        // back empty-handed instead of reporting zeros as findings.
        Assert.All(samples, s => Assert.Equal(string.Empty, s.EntityPath));
        Assert.All(samples, s => Assert.Empty(s.BossBytes));

        // The one file that DOES hold the first hop, so "the chain resolves as far as the
        // bytes go" is demonstrated rather than assumed - and so a future change that stops
        // the host pointer resolving at all is caught.
        Assert.Contains(Replay("session-2026-08-areamarkers.rec"), s => s.Host != 0);
    }

    [Fact]
    public void OnAFileThatCannotAnswer_TheReportSaysSoRatherThanConcluding()
    {
        var text = new StringWriter();
        HoverHunt.Report(Replay("session-2026-08-frustum.rec"), text);
        string report = text.ToString();

        // It must not announce the hypothesis refuted from a file that never asked it. "the
        // byte read ZERO on every monster" is the sentence this test exists to keep out of a
        // report built on absent data.
        Assert.DoesNotContain("read ZERO on every monster", report, StringComparison.Ordinal);
        Assert.Contains("no monster carried a readable Monster component", report, StringComparison.Ordinal);

        // And it must not claim the chain named anything.
        Assert.DoesNotContain("DISTINCT ENTITIES", report, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptySessionIsReportedAsNoFrames()
    {
        var text = new StringWriter();
        HoverHunt.Report([], text);
        Assert.Contains("no frames", text.ToString(), StringComparison.Ordinal);
    }
}
