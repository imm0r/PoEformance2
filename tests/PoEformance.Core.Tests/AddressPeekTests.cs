using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;

namespace PoEformance.Core.Tests;

/// <summary>
/// Looking at one address somebody already found.
/// </summary>
/// <remarks>
/// The case these are written around is a real one. A Cheat Engine four-byte scan reported a
/// value of 999 while the cursor sat on an inventory item and 1000 while it did not - a clean,
/// repeatable, two-state signal that was entirely an artefact of where the four-byte window
/// was placed. 999 is 0x3E7 and 1000 is 0x3E8: the top halves of two 64-bit heap pointers.
/// A tool that shows the aligned slot says that in one line; one that does not costs a session.
/// </remarks>
public class AddressPeekTests
{
    private const ulong Module = 0x7FF6_14C1_0000;
    private const uint ModuleSize = 0x4C23000;

    /// <summary>The static, the object it points at, and the two slots - as they really were.</summary>
    private const ulong StaticRva = 0x468C3A8;
    private const ulong ObjectBase = 0x3E7_1C53_0010;
    private const ulong Slot = ObjectBase + 0x2358;

    /// <summary>A pointer whose top half reads 999, which is what the four-byte scan saw.</summary>
    private const ulong Hovering = 0x3E7_1AB1_2340;

    /// <summary>And one whose top half reads 1000.</summary>
    private const ulong Idle = 0x3E8_0C44_5560;

    private static FakeMemoryReader Game(ulong stored)
    {
        var reader = new FakeMemoryReader { ModuleBase = Module, ModuleSize = ModuleSize };
        reader.Place(ObjectBase, new byte[0x2400]);
        reader.Place(Module + StaticRva, ObjectBase);
        reader.Place(Slot, stored);
        return reader;
    }

    [Fact]
    public void ACheatEnginePathIsTakenAsWritten()
    {
        Assert.True(PointerPath.TryParse("PathOfExileSteam.exe+468C3A8,235C", out PointerPath? path, out string error));
        Assert.Equal(string.Empty, error);
        Assert.NotNull(path);
        Assert.True(path.ModuleRelative);
        Assert.Equal(StaticRva, path.BaseAddress);
        Assert.Equal([0x235C], path.Offsets);
    }

    [Fact]
    public void EverythingIsHexadecimalBecauseThatIsHowAddressesAreWritten()
    {
        // 235C read as decimal is a real, readable, wrong address - which is the failure worth
        // preventing, because nothing about the output would look wrong.
        Assert.True(PointerPath.TryParse("+468C3A8,235C", out PointerPath? path, out _));
        Assert.Equal(0x235C, path!.Offsets[0]);
    }

    [Fact]
    public void AnAbsoluteAddressNeedsNoModule()
    {
        Assert.True(PointerPath.TryParse("0x3E71C53236C", out PointerPath? path, out _));
        Assert.False(path!.ModuleRelative);
        Assert.Empty(path.Offsets);
    }

    [Fact]
    public void NonsenseIsRejectedWithAReason()
    {
        Assert.False(PointerPath.TryParse("the third one", out _, out string error));
        Assert.NotEqual(string.Empty, error);
    }

    [Fact]
    public void ThePathResolvesThroughTheStaticToTheSlot()
    {
        PointerPath.TryParse($"+{StaticRva:X},235C", out PointerPath? path, out _);

        PathResolution at = path!.Resolve(Game(Hovering));

        Assert.True(at.Ok);
        Assert.Equal(Slot + 4, at.Target);          // +0x235C is four bytes into the slot
        Assert.Equal(ObjectBase, at.ObjectBase);
    }

    [Fact]
    public void ABrokenHopStopsTheWalkInsteadOfAnsweringZero()
    {
        // "The static holds nothing" and "the object has nothing there" are different problems
        // with different fixes, and a chain that only ever answers 0 cannot tell them apart.
        var reader = new FakeMemoryReader { ModuleBase = Module, ModuleSize = ModuleSize };
        PointerPath.TryParse($"+{StaticRva:X},235C", out PointerPath? path, out _);

        PathResolution at = path!.Resolve(reader);

        Assert.False(at.Ok);
        Assert.False(at.Hops[^1].Read);
    }

    [Fact]
    public void AnUnalignedAddressIsNamedAsHalfOfThePointerAroundIt()
    {
        IReadOnlyList<string> lines = AddressPeek.Describe(Game(Idle), Slot + 4, ObjectBase);
        string report = string.Join('\n', lines);

        Assert.Contains("MID-SLOT", report, StringComparison.Ordinal);
        Assert.Contains($"0x{Slot:X}", report, StringComparison.Ordinal);
        Assert.Contains("POINTER", report, StringComparison.Ordinal);

        // The number the four-byte scan reported, said out loud next to what it really is.
        Assert.Contains("(1000)", report, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOtherStateOfTheSameSlotReportsTheOtherNumber()
    {
        string report = string.Join('\n', AddressPeek.Describe(Game(Hovering), Slot + 4, ObjectBase));

        Assert.Contains("(999)", report, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAlignedPointerStillWarnsWhatAFourByteWatchWouldRead()
    {
        string report = string.Join('\n', AddressPeek.Describe(Game(Idle), Slot, ObjectBase));

        Assert.DoesNotContain("MID-SLOT", report, StringComparison.Ordinal);
        Assert.Contains("1000", report, StringComparison.Ordinal);
    }

    [Fact]
    public void SlotsAreNamedRelativeToTheObjectSoTheyOutliveTheProcess()
    {
        string report = string.Join('\n', AddressPeek.Describe(Game(Idle), Slot + 4, ObjectBase));

        Assert.Contains("+0x2358", report, StringComparison.Ordinal);
    }

    [Fact]
    public void AVTableIsReportedAsAModuleOffsetBecauseThatIsThePartThatSurvives()
    {
        FakeMemoryReader reader = Game(Idle);
        reader.Place(ObjectBase, Module + 0x1234560);

        string report = string.Join('\n', AddressPeek.Describe(reader, Slot + 4, ObjectBase));

        Assert.Contains("vtable module+0x1234560", report, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnmappedNeighbourhoodCostsOnlyItsOwnSlots()
    {
        // Slot by slot rather than one block read: the addresses worth peeking at are the ones
        // nobody has mapped, and a window that fails as a whole shows nothing at all.
        var reader = new FakeMemoryReader { ModuleBase = Module, ModuleSize = ModuleSize };
        reader.Place(Slot, Hovering);

        string report = string.Join('\n', AddressPeek.Describe(reader, Slot + 4));

        Assert.Contains("unreadable", report, StringComparison.Ordinal);
        Assert.Contains($"{Hovering:X16}", report, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoSamplesNameTheSlotThatMoved()
    {
        // The half that answers questions: hover the item, take the hand off, and what prints
        // is the short list.
        (ulong start, int slots, _) = AddressPeek.Window(Slot + 4, AddressPeek.DefaultBefore, AddressPeek.DefaultAfter);

        FakeMemoryReader idle = Game(Idle);
        ulong?[] before = AddressPeek.Sample(idle, start, slots);
        ulong?[] after = AddressPeek.Sample(Game(Hovering), start, slots);

        IReadOnlyList<string> changes = AddressPeek.Changes(idle, before, after, start, ObjectBase);

        Assert.Single(changes);
        Assert.Contains("+0x2358", changes[0], StringComparison.Ordinal);
        Assert.Contains($"{Idle:X16}", changes[0], StringComparison.Ordinal);
        Assert.Contains($"{Hovering:X16}", changes[0], StringComparison.Ordinal);
    }

    [Fact]
    public void NothingMovingPrintsNothing()
    {
        (ulong start, int slots, _) = AddressPeek.Window(Slot, AddressPeek.DefaultBefore, AddressPeek.DefaultAfter);
        FakeMemoryReader reader = Game(Idle);

        Assert.Empty(AddressPeek.Changes(
            reader,
            AddressPeek.Sample(reader, start, slots),
            AddressPeek.Sample(reader, start, slots),
            start,
            ObjectBase));
    }

    [Fact]
    public void TheWindowPutsTheTargetWhereItWasAskedFor()
    {
        (ulong start, int slots, int target) = AddressPeek.Window(0x1_0000_0044, 0x20, 0x40);

        Assert.Equal(0x1_0000_0020UL, start);
        Assert.Equal(12, slots);
        Assert.Equal(4, target);
    }

    [Fact]
    public void TheReportWalksThePathBeforeItLooksAtAnything()
    {
        PointerPath.TryParse($"+{StaticRva:X},235C", out PointerPath? path, out _);

        string report = string.Join('\n', AddressPeek.Report(Game(Hovering), path!));

        Assert.Contains($"module+0x{StaticRva:X}", report, StringComparison.Ordinal);
        Assert.Contains($"0x{ObjectBase:X}", report, StringComparison.Ordinal);
        Assert.Contains("MID-SLOT", report, StringComparison.Ordinal);
    }
}
