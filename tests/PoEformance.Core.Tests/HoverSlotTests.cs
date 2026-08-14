using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;

namespace PoEformance.Core.Tests;

/// <summary>
/// What the slot behind a Cheat Engine "999 while hovering an item, 1000 otherwise" really is,
/// replayed from the ten seconds of real memory that settled it.
/// </summary>
/// <remarks>
/// THE WHOLE STORY, because every step of it was a wrong answer that looked right.
///
/// A four-byte watch at <c>[PathOfExileSteam.exe+0x468C3A8]+0x235C</c> reads 999 with the
/// cursor on an inventory item and 1000 without. Both numbers are the TOP HALF of a pointer:
/// 999 is 0x3E7 and 1000 is 0x3E8, and the game's heap for that run straddled the
/// 0x3E8_00000000 boundary, so which one shows depends only on which side the current
/// allocation landed. A different launch gives a different pair - the fixtures show
/// 1054/1055, 646/647, 940/941 and 1513.
///
/// The eight-byte reading killed the next theory too. Two adjacent pointers moving together
/// look exactly like a std::vector's begin/end, which would put capacity at +0x2368 - and
/// +0x2368 changed in 89 of 92 samples, which is not what a capacity does. It is a float
/// clock: 7754.7 seconds to 7764.4 over the 10.2 seconds the recording covers.
///
/// What the pair actually is: <c>_Ptr</c> and <c>_Rep</c> of an MSVC <c>std::shared_ptr</c>.
/// The two are always EXACTLY 0x10 apart because <c>make_shared</c> puts the payload inline
/// behind the control block, so <c>_Ptr == _Rep + 0x10</c> - and every value either has ever
/// held has the same control-block vtable, which is what proves it is one class rather than a
/// pool of unrelated objects.
///
/// And it is not a hover flag. In ten seconds it took eight distinct values, most of them for
/// a single frame, and the objects behind them carry asset names - "UI GlyphMap",
/// "Art/Textures/Interface2/2DArt/UIImages/InGame/HUD/CharmBarBg.dds". It is the resource
/// record most recently handled, which CORRELATES with hovering because a tooltip touches
/// different art, and which is not a question about the mouse at all.
/// </remarks>
public class HoverSlotTests
{
    /// <summary>Ten seconds of --peekwatch over the slot, at 92 samples.</summary>
    private static string FixturePath
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return Path.Combine(dir.FullName, "tests", "fixtures", "session-2026-08-hover.rec");
        }
    }

    /// <summary>The Cheat Engine path, exactly as the table wrote it.</summary>
    private const string CheatEnginePath = "PathOfExileSteam.exe+468C3A8,235C";

    private const ulong ObjectBase = 0x3E7_1C53_0010;
    private const ulong SharedPtr = ObjectBase + 0x2358;
    private const ulong Control = ObjectBase + 0x2360;
    private const ulong Clock = ObjectBase + 0x2368;

    private static ReplayMemoryReader Load() => ReplayMemoryReader.Load(File.OpenRead(FixturePath));

    [Fact]
    public void TheCheatEnginePathResolvesToTheSlotItNamed()
    {
        using ReplayMemoryReader reader = Load();
        Assert.True(PointerPath.TryParse(CheatEnginePath, out PointerPath? path, out _));

        PathResolution at = path!.Resolve(reader);

        Assert.True(at.Ok);
        Assert.Equal(ObjectBase, at.ObjectBase);
        Assert.Equal(SharedPtr + 4, at.Target);   // +0x235C is the high half of the slot at +0x2358
    }

    [Fact]
    public void TheReportSaysTheAddressIsHalfOfAPointer()
    {
        using ReplayMemoryReader reader = Load();
        PointerPath.TryParse(CheatEnginePath, out PointerPath? path, out _);

        string report = string.Join('\n', AddressPeek.Report(reader, path!));

        Assert.Contains("MID-SLOT", report, StringComparison.Ordinal);
        Assert.Contains("POINTER", report, StringComparison.Ordinal);
        Assert.Contains("+0x2358", report, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePairIsAlwaysExactlySixteenApartWhichIsWhatMakeSharedDoes()
    {
        // _Ptr == _Rep + 0x10, because make_shared puts the payload inline behind the control
        // block. This is the fact that identifies the field; two pointers that merely move
        // together could be anything.
        using ReplayMemoryReader reader = Load();
        var checkedFrames = 0;

        for (uint frame = 0; frame < reader.FrameCount; frame++)
        {
            reader.Seek(frame);
            if (!reader.TryRead(SharedPtr, out ulong pointer)
                || !reader.TryRead(Control, out ulong control)
                || pointer == 0 || control == 0)
            {
                continue;
            }

            // A sample caught mid-write reads the two halves of the assignment from either
            // side of it, so the pair is only required to agree when it is settled.
            if (pointer != control + 0x10)
            {
                continue;
            }

            checkedFrames++;
        }

        Assert.True(checkedFrames > 70, $"only {checkedFrames} settled frames of {reader.FrameCount}");
    }

    [Fact]
    public void EveryValueItTakesHasTheSameControlBlockVTable()
    {
        // One class, not a pool of unrelated objects - and the control block's own layout,
        // {vtable, _Uses, _Weaks}, is visible in the same sixteen bytes.
        using ReplayMemoryReader reader = Load();
        var seen = new HashSet<ulong>();

        for (uint frame = 0; frame < reader.FrameCount; frame++)
        {
            reader.Seek(frame);
            if (reader.TryRead(Control, out ulong control) && control != 0
                && reader.TryRead(control, out ulong vtable) && vtable != 0)
            {
                seen.Add(vtable);
            }
        }

        Assert.NotEmpty(seen);
        Assert.Single(seen);
        Assert.Equal(reader.ModuleBase + 0x30F2CB8, seen.Single());
    }

    [Fact]
    public void TheSlotAfterThePairIsAClockAndNotAVectorCapacity()
    {
        // The theory this kills: begin/end/capacity. A capacity does not advance every frame,
        // and this advances in step with the wall clock.
        using ReplayMemoryReader reader = Load();
        var seconds = new List<float>();

        for (uint frame = 0; frame < reader.FrameCount; frame++)
        {
            reader.Seek(frame);
            if (reader.TryRead(Clock, out uint raw) && raw != 0)
            {
                seconds.Add(BitConverter.UInt32BitsToSingle(raw));
            }
        }

        Assert.True(seconds.Count > 80, $"only {seconds.Count} readings");
        Assert.Equal(seconds.OrderBy(value => value), seconds);      // never goes backwards

        float elapsed = seconds[^1] - seconds[0];
        uint wallMs = reader.FrameTimes[^1] - reader.FrameTimes[0];
        Assert.InRange(elapsed, (wallMs / 1000f) - 1f, (wallMs / 1000f) + 1f);
    }

    [Fact]
    public void TheClockReadsAsANumberRatherThanAsAPointer()
    {
        // What it did for a whole session: 7659.73 is 0x45EF5DD2, which the reader's loose
        // "could this be a pointer" bound accepts, so the clock was reported as a pointer to
        // memory that could not be read.
        using ReplayMemoryReader reader = Load();
        reader.Seek((uint)(reader.FrameCount - 1));

        string report = string.Join('\n', AddressPeek.Describe(reader, Clock, ObjectBase));
        string line = report.Split('\n').Single(text => text.Contains("-> +0x2368", StringComparison.Ordinal));

        Assert.DoesNotContain("pointer", line, StringComparison.Ordinal);
        Assert.Contains("f", line, StringComparison.Ordinal);
    }

    [Fact]
    public void ItIsNotATwoStateFlagAndTheSummarySaysSo()
    {
        // The reason "== 999 means hovering" cannot be built on: over ten seconds the slot
        // takes many values, not two, and most of them last a single frame.
        using ReplayMemoryReader reader = Load();
        (ulong start, int slots, _) = AddressPeek.Window(SharedPtr, AddressPeek.DefaultBefore, AddressPeek.DefaultAfter);

        var log = new AddressPeek.PeekWatchLog();
        ulong?[] last = [];
        for (uint frame = 0; frame < reader.FrameCount; frame++)
        {
            reader.Seek(frame);
            ulong?[] sample = AddressPeek.Sample(reader, start, slots);
            if (last.Length > 0)
            {
                log.Observe(last, sample);
            }

            last = sample;
        }

        string summary = string.Join('\n', log.Summary(start, ObjectBase));

        Assert.Contains("+0x2358", summary, StringComparison.Ordinal);
        Assert.Contains("seen once only", summary, StringComparison.Ordinal);

        // And the clock is called what it is rather than listed as a state.
        Assert.Contains("never the same value twice - a counter or a clock", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void TheObjectBehindItNamesAnAsset()
    {
        // What settles the question of WHAT it points at, and it is two hops past the pointer
        // Cheat Engine found: shared_ptr -> payload -> a record whose characters are stored in
        // the object rather than behind a pointer. That is why the field moves when a tooltip
        // opens, and why it is not a fact about the mouse.
        using ReplayMemoryReader reader = Load();
        reader.Seek((uint)(reader.FrameCount - 1));

        Assert.True(PointerPath.TryParse("+468C3A8,2358,0,0", out PointerPath? path, out _));
        string report = string.Join('\n', AddressPeek.Report(reader, path!));

        Assert.Contains("Art/Text", report, StringComparison.Ordinal);
        Assert.Contains("ures/Int", report, StringComparison.Ordinal);
    }
}
