using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Ui;

namespace PoEformance.Core.Tests;

/// <summary>
/// The 0.5.5 capture made with F8 pressed over an inventory item
/// (<c>tests/fixtures/session-2026-09-itempick.rec</c>: 3,262 frames, 230 KB, 2026-09-11).
/// </summary>
/// <remarks>
/// The capture the one before it asked for, and it settles ItemPtr: the picked slot element
/// - 140x211 UI units, a two-by-three cell - holds at 0x4E0 the entity whose metadata path is
/// Metadata/Items/Armours/BodyArmours/FourBodyInt2, the body armour the tooltip in the
/// screenshot names. Across the whole file, every UiElement that holds an item entity holds
/// it at exactly +0x4E0 and nowhere else. TextPtr stays open: nothing on the picked chain
/// displays text, and the tooltip was not picked.
/// </remarks>
public class ItemPickSessionTests
{
    /// <summary>The slot the owner pressed F8 over, as the browser printed its address.</summary>
    private const ulong PickedSlot = 0x599C004F010;

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
            return Path.Combine(dir.FullName, "tests", "fixtures", "session-2026-09-itempick.rec");
        }
    }

    private static (ReplayMemoryReader Replay, OffsetSchema Schema) Open()
    {
        ReplayMemoryReader replay = ReplayMemoryReader.Load(File.OpenRead(FixturePath));
        replay.Seek((uint)(replay.FrameCount - 1));
        return (replay, RealSessionTests.LiveSchema());
    }

    [Fact]
    public void ThePickedSlotHoldsTheBodyArmourAtItemPtr()
    {
        (ReplayMemoryReader replay, OffsetSchema schema) = Open();
        var elements = new UiElementReader(replay, schema);
        Assert.True(elements.IsUiElement(PickedSlot));

        UiNode? slot = new UiTreeReader(replay, schema).ReadOne(PickedSlot, new UiScale(3440, 1440, 0));
        Assert.NotNull(slot);
        Assert.Equal(140.4f, slot.Size.X, 1);
        Assert.Equal(210.6f, slot.Size.Y, 1);
        Assert.Equal(string.Empty, slot.Text);

        Assert.NotEqual(0UL, slot.ItemPtr);
        ulong details = replay.ReadPointer(slot.ItemPtr + 0x08);
        Assert.Equal("Metadata/Items/Armours/BodyArmours/FourBodyInt2", replay.ReadStdWString(details + 0x08, 96));
    }

    [Fact]
    public void EveryElementHoldingAnItemHoldsItAt0x4E0()
    {
        (ReplayMemoryReader replay, OffsetSchema schema) = Open();
        int itemPtr = schema.Structs["UiElementBase"].OffsetOf("ItemPtr");
        Assert.Equal(0x4E0, itemPtr);
        List<MemoryRegion> regions = [.. replay.Regions()];

        var items = new HashSet<ulong>();
        foreach (MemoryRegion region in regions)
        {
            var bytes = new byte[region.Size];
            if (!replay.TryRead(region.Address, bytes))
            {
                continue;
            }

            for (int at = 0; at + 8 <= bytes.Length; at += 8)
            {
                ulong value = BitConverter.ToUInt64(bytes, at);
                if (items.Contains(value) || !MemoryReaderExtensions.IsPlausiblePointer(value))
                {
                    continue;
                }

                ulong details = replay.ReadPointer(value + 0x08);
                if (details != 0 && replay.ReadStdWString(details + 0x08, 128).StartsWith("Metadata/Items/", StringComparison.Ordinal))
                {
                    items.Add(value);
                }
            }
        }

        Assert.Equal(23, items.Count);

        var holders = new Dictionary<int, int>();
        foreach (MemoryRegion region in regions)
        {
            var bytes = new byte[region.Size];
            if (!replay.TryRead(region.Address, bytes))
            {
                continue;
            }

            for (int at = 0; at + 8 <= bytes.Length; at += 8)
            {
                if (!items.Contains(BitConverter.ToUInt64(bytes, at)))
                {
                    continue;
                }

                ulong slot = region.Address + (ulong)at;
                for (int back = 0x10; back <= 0x600; back += 8)
                {
                    ulong candidate = slot - (ulong)back;
                    if (replay.TryRead(candidate + 0x08, out ulong self) && self == candidate)
                    {
                        holders[back] = holders.GetValueOrDefault(back) + 1;
                        break;
                    }
                }
            }
        }

        Assert.Equal([itemPtr], holders.Keys);
        Assert.Equal(3, holders[itemPtr]);
    }
}
