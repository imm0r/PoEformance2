using PoEformance.Core.Memory;

namespace PoEformance.Core.Tests;

/// <summary>
/// Recording size. A session file has to be small enough to actually send to someone -
/// that is the entire point of the record/replay layer. The first real capture came out
/// at 77 MB because it faithfully stored the game's 76 MB module image, read once for
/// pattern scanning and then never needed again.
/// </summary>
public class RecordingSizeTests
{
    [Fact]
    public void OversizedReads_AreServedButNotRecorded()
    {
        var live = new FakeMemoryReader();
        live.Place(0x10_0000, new byte[2 * 1024 * 1024]); // stand-in for the module image
        live.Place(0x20_0000, 0xCAFEBABEUL);              // an ordinary struct read

        var file = new MemoryStream();
        using (var recorder = new RecordingMemoryReader(live, file) { MaxRecordedReadBytes = 64 * 1024 })
        {
            recorder.MarkFrame();

            var big = new byte[2 * 1024 * 1024];
            Assert.True(recorder.TryRead(0x10_0000, big)); // still served in full
            Assert.Equal(0xCAFEBABEUL, recorder.Read<ulong>(0x20_0000));

            Assert.Equal(1, recorder.SkippedLargeReads);
        }

        // The 2 MB read must not be in the file - a few hundred bytes, not megabytes.
        // (ToArray works on a closed MemoryStream; Length does not.)
        int size = file.ToArray().Length;
        Assert.True(size < 4096, $"recording was {size} bytes");
    }

    [Fact]
    public void ResolvedStatics_SurviveAsNotes_SoReplayNeedsNoModuleImage()
    {
        var live = new FakeMemoryReader().Place(0x20_0000, 0x1234UL);

        var file = new MemoryStream();
        using (var recorder = new RecordingMemoryReader(live, file))
        {
            recorder.MarkFrame();
            recorder.Read<ulong>(0x20_0000);
            recorder.NoteStatic("GameStates", 0x7FF6D28F9C48);
            recorder.NoteStatic("GameCullSize", 0x7FF6D28AFCD0);
        }

        var replay = ReplayMemoryReader.Load(new MemoryStream(file.ToArray()));

        Assert.Equal(2, replay.ResolvedStatics.Count);
        Assert.Equal(0x7FF6D28F9C48UL, replay.ResolvedStatics["GameStates"]);
        Assert.Equal(0x7FF6D28AFCD0UL, replay.ResolvedStatics["GameCullSize"]);

        // Ordinary reads still replay normally alongside the notes.
        Assert.Equal(0x1234UL, replay.Read<ulong>(0x20_0000));
    }

    [Fact]
    public void OldFormatVersion_IsRejectedWithAClearMessage()
    {
        // Version 2 added notes; a version-1 file cannot be read as one. Better a named
        // refusal than a mysterious parse failure.
        var stale = new MemoryStream();
        var writer = new BinaryWriter(stale);
        writer.Write(RecordingFormat.Magic);
        writer.Write(1u); // old version
        writer.Write(0u);
        writer.Write(0UL);
        writer.Write(0u);
        writer.Write(0L);
        writer.Flush();

        var ex = Assert.Throws<InvalidDataException>(
            () => ReplayMemoryReader.Load(new MemoryStream(stale.ToArray())));
        Assert.Contains("version", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
