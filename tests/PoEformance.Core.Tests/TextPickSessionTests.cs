using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Ui;

namespace PoEformance.Core.Tests;

/// <summary>
/// The 0.5.5 capture made with F8 pressed over the area-name label
/// (<c>tests/fixtures/session-2026-09-text.rec</c>: 778 frames, 194 KB, 2026-09-11).
/// </summary>
/// <remarks>
/// The picked element is the label reading "Clearfell Encampment" in the screenshot: 236x27
/// UI units and no children, so whatever text it shows is its own. At the offset the schema
/// carried until now (0x378, the base tail's -0x18 applied to the 0.5.4 value) the bytes are
/// {0x17, pointer, pointer, pointer} - not a std::wstring header, but the END of one: 0x17 is
/// the capacity MSVC gives a wstring of 20 characters, which is the label's length, and the
/// capacity is a header's last qword. So the string begins at 0x360. Its pointer and size
/// there were not read by this build; the test pins what the file holds and what follows
/// from it, and the browser showing the text at 0x360 is the confirmation.
///
/// The same element carries a plausible heap pointer in the item slot at 0x4E0 without being
/// an item slot, which is why the tree reader now checks the target is an item entity before
/// reporting one.
/// </remarks>
public class TextPickSessionTests
{
    /// <summary>The label the owner pressed F8 over, as the browser printed its address.</summary>
    private const ulong PickedLabel = 0x598E0F0D930;

    /// <summary>What a std::wstring of 20 characters has for capacity under MSVC's growth.</summary>
    private const ulong CapacityOfTwentyChars = 0x17;

    internal static string FixturePath
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return Path.Combine(dir.FullName, "tests", "fixtures", "session-2026-09-text.rec");
        }
    }

    private static (ReplayMemoryReader Replay, OffsetSchema Schema) Open()
    {
        ReplayMemoryReader replay = ReplayMemoryReader.Load(File.OpenRead(FixturePath));
        replay.Seek((uint)(replay.FrameCount - 1));
        return (replay, RealSessionTests.LiveSchema());
    }

    [Fact]
    public void TheOldTextOffsetHoldsAWstringsCapacity_NotAWstring()
    {
        (ReplayMemoryReader replay, OffsetSchema schema) = Open();
        StructDef ui = schema.Structs["UiElementBase"];

        // The label, as the browser measured it: 236x27 UI units, no children.
        Span<float> size = stackalloc float[2];
        Assert.True(replay.TryRead(PickedLabel + (ulong)ui.OffsetOf("UnscaledSize"), System.Runtime.InteropServices.MemoryMarshal.AsBytes(size)));
        Assert.Equal(262f, size[0], 0);
        Assert.Equal(30f, size[1], 0);
        ulong first = replay.ReadPointer(PickedLabel + (ulong)ui.OffsetOf("ChildrenFirst"));
        ulong last = replay.ReadPointer(PickedLabel + (ulong)ui.OffsetOf("ChildrenLast"));
        Assert.Equal(first, last);

        // At 0x378: 0x17 then three pointers. The 0x17 is the capacity of the string that
        // starts 0x18 bytes earlier; the pointers are whatever follows it.
        Assert.True(replay.TryRead(PickedLabel + 0x378, out ulong capacity));
        Assert.Equal(CapacityOfTwentyChars, capacity);
        Assert.Equal(20, "Clearfell Encampment".Length);
        Assert.True(replay.TryRead(PickedLabel + 0x380, out ulong next));
        Assert.True(MemoryReaderExtensions.IsPlausiblePointer(next));
        Assert.Equal(string.Empty, replay.ReadStdWString(PickedLabel + 0x378));

        // And so the schema now says 0x360, whose header this build never read.
        Assert.Equal(0x360, ui.OffsetOf("TextPtr"));
        Assert.False(replay.TryRead(PickedLabel + 0x360, out ulong _));
        Assert.False(replay.TryRead(PickedLabel + 0x370, out ulong _));
    }

    [Fact]
    public void TheLabelsItemSlotHoldsAPointerThatIsNotAnItem()
    {
        (ReplayMemoryReader replay, OffsetSchema schema) = Open();
        StructDef ui = schema.Structs["UiElementBase"];

        Assert.True(replay.TryRead(PickedLabel + (ulong)ui.OffsetOf("ItemPtr"), out ulong value));
        Assert.True(MemoryReaderExtensions.IsPlausiblePointer(value));

        // Not an item entity: nothing behind it reads as one, and the tree reader says so.
        ulong details = replay.ReadPointer(value + (ulong)schema.Structs["Entity"].OffsetOf("EntityDetailsPtr"));
        Assert.False(replay.ReadStdWString(details + (ulong)schema.Structs["EntityDetails"].OffsetOf("Path"), 32).StartsWith("Metadata/Items/", StringComparison.Ordinal));

        var tree = new UiTreeReader(new SelfAsserting(replay, PickedLabel), schema);
        UiNode? node = tree.ReadOne(PickedLabel, new UiScale(3440, 1440, 0));
        Assert.NotNull(node);
        Assert.Equal(0UL, node.ItemPtr);
    }

    /// <summary>
    /// The pick read every field of the label but its Self pointer, so the replay cannot
    /// vouch for it as an element; this answers that one read from the browser's own
    /// knowledge and passes everything else through.
    /// </summary>
    private sealed class SelfAsserting(ReplayMemoryReader inner, ulong element) : IMemoryReader
    {
        public bool IsAttached => inner.IsAttached;

        public int ProcessId => inner.ProcessId;

        public ulong ModuleBase => inner.ModuleBase;

        public uint ModuleSize => inner.ModuleSize;

        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address == element + 0x08 && destination.Length == 8)
            {
                BitConverter.TryWriteBytes(destination, element);
                return true;
            }

            return inner.TryRead(address, destination);
        }

        public void Dispose()
        {
            // The replay is owned by the test, which disposes nothing on purpose: xUnit
            // tears the process down, and the file handle goes with it.
        }
    }
}
