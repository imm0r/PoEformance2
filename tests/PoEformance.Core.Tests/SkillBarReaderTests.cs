using System.Numerics;
using PoEformance.Core.Schema;
using PoEformance.Game.Components;
using PoEformance.Game.Ui;

namespace PoEformance.Core.Tests;

/// <summary>
/// Where the skill bar's slots are on the HUD, and which skill each one shows.
/// </summary>
/// <remarks>
/// The tree below is shaped after the game's own hud.ui and the AHK tool's live dump: HUD, then
/// HUDRight, then skills_bar, whose direct children are the slots - 64x64, three in a top row and
/// five in a bottom one - plus the other weapon set's hidden duplicates of the same size and a
/// wide indicator panel. Each slot carries the address of the skill object it shows.
/// </remarks>
public class SkillBarReaderTests
{
    private const ulong Actor = 0x0000_0500_0000_0000;
    private const ulong Spark = 0x0000_0500_2000_0000;
    private const ulong Orb = 0x0000_0500_2001_0000;
    private const ulong Wall = 0x0000_0500_2002_0000;

    private static UiScale Window() => new(2560, 1600, 0);

    private static OffsetSchema Schema() => RealSessionTests.LiveSchema();

    /// <summary>The bar with three skills in the top row's slots, at the given pointer offset.</summary>
    private static (UiTree Tree, ulong Hud, PlayerSkills Skills) Bar(
        OffsetSchema schema, int pointerAt, bool barVisible = true)
    {
        var tree = new UiTree(schema);

        tree.Add(0, children: [1]);
        tree.Add(1, parent: 0, stringId: InterfaceReader.Id, children: [2, 3]);
        tree.Add(2, parent: 1, stringId: "HUDLeft", children: [21]);
        tree.Add(21, parent: 2, stringId: "menu_button", size: new Vector2(40, 40));
        tree.Add(3, parent: 1, stringId: SkillBarReader.ClusterId, children: [22, 4]);
        tree.Add(22, parent: 3, stringId: "create_portal_button", size: new Vector2(73, 43));
        tree.Add(
            4, parent: 3, stringId: SkillBarReader.BarId, visible: barVisible,
            relative: new Vector2(1500, 1300), children: [5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15]);

        // The top row: mouse-bound. The bottom row: the keyboard's five.
        tree.Add(5, parent: 4, relative: new Vector2(0, 0), size: new Vector2(64, 64));
        tree.Add(6, parent: 4, relative: new Vector2(72, 0), size: new Vector2(64, 64));
        tree.Add(7, parent: 4, relative: new Vector2(144, 0), size: new Vector2(64, 64));
        for (int i = 0; i < 5; i++)
        {
            tree.Add(8 + i, parent: 4, relative: new Vector2(72 * i, 72), size: new Vector2(64, 64));
        }

        // The other weapon set's duplicates, hidden, and the indicator panel, wide.
        tree.Add(13, parent: 4, visible: false, relative: new Vector2(0, 0), size: new Vector2(64, 64));
        tree.Add(14, parent: 4, visible: false, relative: new Vector2(72, 0), size: new Vector2(64, 64));
        tree.Add(15, parent: 4, relative: new Vector2(0, 150), size: new Vector2(200, 40));

        tree.Reader.Place<ulong>(UiTree.At(5) + (ulong)pointerAt, Spark);
        tree.Reader.Place<ulong>(UiTree.At(6) + (ulong)pointerAt, Orb);
        tree.Reader.Place<ulong>(UiTree.At(7) + (ulong)pointerAt, Wall);
        tree.Reader.Place<ulong>(UiTree.At(13) + (ulong)pointerAt, Spark); // the hidden twin shows the same skill

        SkillTableFixture.Place(
            tree.Reader, schema, Actor,
            new SkillTableFixture.Skill(Spark, "spark"),
            new SkillTableFixture.Skill(Orb, "orb_of_storms"),
            new SkillTableFixture.Skill(Wall, "flame_wall"));
        var skills = new PlayerSkills(tree.Reader, schema);
        skills.Refresh(Actor, 0);

        return (tree, UiTree.At(1), skills);
    }

    private static SkillBarReader Reader(UiTree tree, OffsetSchema schema)
        => new(tree.Reader, schema, new UiElementReader(tree.Reader, schema));

    private static int PointerOffset(OffsetSchema schema)
        => schema.Structs["SkillBarSlot"].OffsetOf("ActiveSkillPtr");

    private static int OlderPointerOffset(OffsetSchema schema)
        => (int)schema.Structs["SkillBarSlot"].Constants["ActiveSkillPtrBeforeShift"];

    [Fact]
    public void TheShowingSlotSizedChildrenAreTheSlots_PlacedAndMatched()
    {
        OffsetSchema schema = Schema();
        (UiTree tree, ulong hud, PlayerSkills skills) = Bar(schema, PointerOffset(schema));
        SkillBarReader reader = Reader(tree, schema);

        IReadOnlyList<SkillSlotOnScreen> slots = reader.Read(hud, Window(), 0, skills);

        // Eight slots: not the hidden twins, not the indicator panel.
        Assert.Equal(8, slots.Count);
        Assert.Equal(new ScreenRect(1500f, 1300f, 1564f, 1364f), slots[0].Where);
        Assert.Equal(new ScreenRect(1572f, 1300f, 1636f, 1364f), slots[1].Where);
        Assert.Equal(new ScreenRect(1500f, 1372f, 1564f, 1436f), slots[3].Where);

        Assert.Equal(Spark, slots[0].Skill);
        Assert.Equal(skills.KeyOf(Spark), slots[0].Key);
        Assert.Equal(Orb, slots[1].Skill);
        Assert.Equal(Wall, slots[2].Skill);
        Assert.Equal(0UL, slots[3].Skill);                      // an empty slot points at nothing known
        Assert.Equal(0UL, slots[3].Key);
        Assert.Equal($"+0x{PointerOffset(schema):X}", reader.SkillOffsetNote);
    }

    [Fact]
    public void ThePointerIsFoundByContent_AtEitherOffset()
    {
        // The reference measured the pointer before 0.5.5 moved the base's tail; whichever of
        // the two places yields a pointer that is in the skill table is the right one, and the
        // readout says which.
        OffsetSchema schema = Schema();
        (UiTree tree, ulong hud, PlayerSkills skills) = Bar(schema, OlderPointerOffset(schema));
        SkillBarReader reader = Reader(tree, schema);

        IReadOnlyList<SkillSlotOnScreen> slots = reader.Read(hud, Window(), 0, skills);

        Assert.Equal(Spark, slots[0].Skill);
        Assert.Equal(Orb, slots[1].Skill);
        Assert.Equal($"+0x{OlderPointerOffset(schema):X}", reader.SkillOffsetNote);
    }

    [Fact]
    public void NothingWhileTheBarIsHidden_OrThereIsNoInterface()
    {
        OffsetSchema schema = Schema();
        (UiTree tree, ulong hud, PlayerSkills skills) = Bar(schema, PointerOffset(schema), barVisible: false);

        Assert.Empty(Reader(tree, schema).Read(hud, Window(), 0, skills));
        Assert.Empty(Reader(tree, schema).Read(0, Window(), 0, skills));
    }

    [Fact]
    public void ReadOnTheClock_TheSameAnswerStandsBetweenReadings()
    {
        // The bar does not move and a weapon swap is the only thing that changes which slots
        // show, so the slots are re-read on a short clock rather than per tick: within it the
        // same list comes back, and a slot hidden meanwhile is still listed until it runs out.
        OffsetSchema schema = Schema();
        (UiTree tree, ulong hud, PlayerSkills skills) = Bar(schema, PointerOffset(schema));
        SkillBarReader reader = Reader(tree, schema);

        IReadOnlyList<SkillSlotOnScreen> first = reader.Read(hud, Window(), 0, skills);
        Assert.Same(first, reader.Read(hud, Window(), SkillBarReader.RefreshMs - 1, skills));

        int flags = schema.Structs["UiElementBase"].OffsetOf("Flags");
        tree.Reader.Place<uint>(UiTree.At(7) + (ulong)flags, 0u);

        Assert.Same(first, reader.Read(hud, Window(), SkillBarReader.RefreshMs - 1, skills));
        IReadOnlyList<SkillSlotOnScreen> then = reader.Read(hud, Window(), SkillBarReader.RefreshMs, skills);
        Assert.Equal(7, then.Count);
        Assert.DoesNotContain(then, slot => slot.Element == UiTree.At(7));
    }

    [Fact]
    public void ANewSkillTableIsReadAgainBeforeTheClockSaysSo()
    {
        // The slots' pointers can only be matched against a table that has been read, so a
        // table that changes - the first reading after attach, a gem socketed - is a reason to
        // look at the slots again at once.
        OffsetSchema schema = Schema();
        (UiTree tree, ulong hud, PlayerSkills skills) = Bar(schema, PointerOffset(schema));
        SkillBarReader reader = Reader(tree, schema);

        var empty = new PlayerSkills(tree.Reader, schema);      // never refreshed: knows nothing
        IReadOnlyList<SkillSlotOnScreen> blind = reader.Read(hud, Window(), 0, empty);
        Assert.All(blind, slot => Assert.Equal(0UL, slot.Skill));
        Assert.Equal("unmatched", reader.SkillOffsetNote);

        IReadOnlyList<SkillSlotOnScreen> seeing = reader.Read(hud, Window(), 1, skills);
        Assert.Equal(Spark, seeing[0].Skill);
    }
}
