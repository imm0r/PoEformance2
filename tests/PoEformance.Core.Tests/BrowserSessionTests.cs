using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Ui;

namespace PoEformance.Core.Tests;

/// <summary>
/// The 0.5.5 capture made with the UI browser open on the HUD and the inventory showing
/// (<c>tests/fixtures/session-2026-09-browser.rec</c>: 1,421 frames, 385 KB, 2026-09-11).
/// </summary>
/// <remarks>
/// Made to settle TextPtr and ItemPtr, and it settles neither, for a reason worth keeping:
/// the browser reads the nodes it SHOWS - the interface root's 124 children and the HUD's
/// 18 - and none of those is an item slot or carries visible text. The file holds eighteen
/// item entities, read by the inventory reader from ServerData, and not one UiElement in it
/// points at any of them at any offset. What it does show is that the item slot at 0x4E0 is
/// not zero on elements that are not slots (a third of the root's children hold small
/// integers, UTF-16 fragments or half a float there), which is why the tree reader now gates
/// the field on looking like a pointer.
///
/// What it confirms instead: RightPanelPtr at its 0.5.5 offset resolves, with the inventory
/// open, to a visible element measured at the right edge of the screen - the inventory - and
/// the StringId slot names all 143 elements the browser read.
/// </remarks>
public class BrowserSessionTests
{
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
            return Path.Combine(dir.FullName, "tests", "fixtures", "session-2026-09-browser.rec");
        }
    }

    private static (ReplayMemoryReader Replay, OffsetSchema Schema, GameChainAddresses Chain) Open()
    {
        ReplayMemoryReader replay = ReplayMemoryReader.Load(File.OpenRead(FixturePath));
        OffsetSchema schema = RealSessionTests.LiveSchema();
        replay.Seek((uint)(replay.FrameCount - 1));
        return (replay, schema, GameChain.Resolve(replay, schema, replay.ResolvedStatics["GameStates"]));
    }

    [Fact]
    public void TheRightPanelPointer_ResolvesToTheOpenInventory()
    {
        (ReplayMemoryReader replay, OffsetSchema schema, GameChainAddresses chain) = Open();
        var elements = new UiElementReader(replay, schema);
        var scale = new UiScale(3440, 1440, 0);

        PanelsOnScreen panels = new PanelReader(replay, schema, elements).Read(chain.UiRoot, scale);
        Assert.Equal(GamePanel.Right, panels.Panels);
        PanelArea inventory = Assert.Single(panels.Areas);
        Assert.True(inventory.Left > 2500f && inventory.Right >= 3439f, $"inventory measured at {inventory}");

        StructDef important = schema.Structs["ImportantUiElements"];
        ulong right = replay.ReadPointer(chain.UiRoot + (ulong)important.OffsetOf("RightPanelPtr"));
        Assert.True(elements.IsVisible(right));
        Assert.Equal(0UL, replay.ReadPointer(chain.UiRoot + (ulong)important.OffsetOf("LeftPanelPtr")));

        // The maps resolve the reference's way here too.
        Assert.NotEqual((0UL, 0UL), new MapRadarReader(replay, schema).Resolve(chain.UiRoot));
    }

    [Fact]
    public void TheBrowserReadTheRootAndTheHud_AndEveryOneOfThemHasAnIdAt0x098()
    {
        (ReplayMemoryReader replay, OffsetSchema schema, GameChainAddresses chain) = Open();
        StructDef ui = schema.Structs["UiElementBase"];
        int stringId = ui.OffsetOf("StringIdPtr");

        List<ulong> rootChildren = ChildrenOf(replay, ui, chain.UiRoot);
        Assert.Equal(124, rootChildren.Count);
        ulong hud = rootChildren[(int)schema.Structs["HudElement"].Constants["ChildFromUiRoot"]];
        Assert.Equal("HUD", replay.ReadStdWString(hud + (ulong)stringId));
        List<ulong> hudChildren = ChildrenOf(replay, ui, hud);
        Assert.Equal(18, hudChildren.Count);

        var header = new byte[32];
        foreach (ulong element in rootChildren.Concat(hudChildren).Append(chain.UiRoot))
        {
            Assert.True(replay.TryRead(element + (ulong)stringId, header));
        }

        var names = new HashSet<string>(hudChildren.Select(c => replay.ReadStdWString(c + (ulong)stringId)));
        Assert.Contains("life_orb", names);
        Assert.Contains("mana_orb", names);
        Assert.Contains("experience_bar", names);
    }

    [Fact]
    public void TheItemSlotIsNotZeroOnNonSlots_SoTheTreeReaderGatesIt()
    {
        (ReplayMemoryReader replay, OffsetSchema schema, GameChainAddresses chain) = Open();
        StructDef ui = schema.Structs["UiElementBase"];
        int itemPtr = ui.OffsetOf("ItemPtr");

        int nonZero = 0, pointerLike = 0;
        foreach (ulong element in ChildrenOf(replay, ui, chain.UiRoot))
        {
            Assert.True(replay.TryRead(element + (ulong)itemPtr, out ulong value));
            if (value != 0)
            {
                nonZero++;
            }

            if (MemoryReaderExtensions.IsPlausiblePointer(value))
            {
                pointerLike++;
            }
        }

        Assert.True(nonZero >= 25, $"{nonZero} non-zero");
        Assert.True(pointerLike < nonZero, $"{pointerLike} of {nonZero} look like pointers");

        // And the tree reader shows none of the non-pointers as an item.
        var tree = new UiTreeReader(replay, schema);
        foreach (ulong element in ChildrenOf(replay, ui, chain.UiRoot))
        {
            UiNode? node = tree.ReadOne(element, new UiScale(3440, 1440, 0));
            if (node is not null)
            {
                Assert.True(node.ItemPtr == 0 || MemoryReaderExtensions.IsPlausiblePointer(node.ItemPtr));
            }
        }
    }

    [Fact]
    public void TheItemsAreInTheFile_ButNoElementPointsAtThem_SoItemPtrStaysOpen()
    {
        (ReplayMemoryReader replay, OffsetSchema _, GameChainAddresses _) = Open();
        List<MemoryRegion> regions = [.. replay.Regions()];

        // Every item entity the inventory reader brought home.
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

        Assert.Equal(18, items.Count);

        // No UiElement holds any of them: for every slot holding an item address, no
        // self-pointing object begins within 0x600 bytes above it.
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
                    Assert.False(replay.TryRead(candidate + 0x08, out ulong self) && self == candidate,
                        $"element 0x{candidate:X} holds an item at +0x{back:X}");
                }
            }
        }
    }

    private static List<ulong> ChildrenOf(ReplayMemoryReader replay, StructDef ui, ulong element)
    {
        var children = new List<ulong>();
        ulong first = replay.ReadPointer(element + (ulong)ui.OffsetOf("ChildrenFirst"));
        ulong last = replay.ReadPointer(element + (ulong)ui.OffsetOf("ChildrenLast"));
        for (ulong at = first; at < last; at += 8)
        {
            children.Add(replay.ReadPointer(at));
        }

        return children;
    }
}
