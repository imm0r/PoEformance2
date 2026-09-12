using System.Numerics;
using PoEformance.Core.Schema;
using PoEformance.Game.Components;
using PoEformance.Game.Ui;

namespace PoEformance.Core.Tests;

/// <summary>
/// Each skill's DPS, read off the Skills panel's rows and remembered against the skill.
/// </summary>
/// <remarks>
/// The tree below is shaped after the interface browser's picture of the panel (2026-09, 0.5.5):
/// the open left panel, a list five levels under it at the schema's path, rows under the list -
/// most of them hidden - and on each showing row a header whose children are four frames, the
/// icon, the name, "Level:", the level, a frame, and a block whose third child is the DPS text.
/// The icon carries the skill object's address at the slot's own offset.
/// </remarks>
public class SkillPanelReaderTests
{
    private const ulong Actor = 0x0000_0500_0000_0000;
    private const ulong Spark = 0x0000_0500_2000_0000;
    private const ulong Orb = 0x0000_0500_2001_0000;

    private const int Root = 0;
    private const int Panel = 30;
    private const int List = 39;
    private const int SparkRow = 40;
    private const int OrbRow = 60;
    private const int SparkIcon = 46;
    private const int SparkDps = 56;
    private const int OrbDps = 76;

    private static OffsetSchema Schema() => RealSessionTests.LiveSchema();

    /// <summary>Where a row's icon keeps no pointer at all - the case the game turned out to be.</summary>
    private const int NoPointer = -1;

    private static (UiTree Tree, PlayerSkills Skills) Window(
        OffsetSchema schema, bool open = true, bool onThePath = true, int? pointerAt = null)
    {
        var tree = new UiTree(schema);
        int at = pointerAt ?? schema.Structs["SkillBarSlot"].OffsetOf("ActiveSkillPtr");

        // The interface root, with the left panel pointer on it - the panel being root child 47
        // in game, and any child here.
        tree.Add(Root, children: [Panel]);
        tree.Reader.Place<ulong>(
            UiTree.At(Root) + (ulong)schema.Structs["ImportantUiElements"].OffsetOf("LeftPanelPtr"),
            UiTree.At(Panel));

        // panel > [2][0][1][1][0] = the list. Off the path, the list hangs elsewhere and the
        // path lands on a frame with nothing under it.
        tree.Add(Panel, parent: Root, stringId: "SkillPanel", visible: open, size: new Vector2(900, 1400),
            children: onThePath ? [31, 32, 33] : [31, 32, 33, 80]);
        tree.Add(31, parent: Panel, stringId: "title");
        tree.Add(32, parent: Panel, stringId: "close_button");
        tree.Add(33, parent: Panel, children: [34]);
        tree.Add(34, parent: 33, children: [35, 36]);
        tree.Add(35, parent: 34);
        tree.Add(36, parent: 34, children: [37, 38]);
        tree.Add(37, parent: 36);
        tree.Add(38, parent: 36, children: onThePath ? [List] : [90]);
        tree.Add(90, parent: 38, stringId: "an_empty_frame");
        if (!onThePath)
        {
            tree.Add(80, parent: Panel, children: [81]);
            tree.Add(81, parent: 80, children: [List]);
        }

        // The list: two showing rows, a showing row that is not a skill, and the hidden rows
        // of the gems not in use - in game there is one per gem the character has ever had.
        tree.Add(
            List, parent: onThePath ? 38 : 81,
            children: [SparkRow, 100, OrbRow, 101, 103, 104, 105, 106, 107, 108]);
        Row(tree, SparkRow, "Spark", "DPS: 53.838", Spark, at);
        Row(tree, OrbRow, "Orb of Storms", "DPS: 77.522", Orb, at);
        tree.Add(101, parent: List, children: [102]);
        tree.Add(102, parent: 101, text: "Support Gem Capacity");
        foreach (int hidden in (ReadOnlySpan<int>)[100, 103, 104, 105, 106, 107, 108])
        {
            tree.Add(hidden, parent: List, visible: false, size: new Vector2(700, 90));
        }

        SkillTableFixture.Place(
            tree.Reader, schema, Actor,
            new SkillTableFixture.Skill(Spark, "spark", "Spark"),
            new SkillTableFixture.Skill(Orb, "orb_of_storms", "Orb of Storms"));
        var skills = new PlayerSkills(tree.Reader, schema);
        skills.Refresh(Actor, 0);

        return (tree, skills);
    }

    /// <summary>One row, twenty indices wide: the row, its header, and the header's ten children.</summary>
    private static void Row(UiTree tree, int row, string name, string dps, ulong skill, int pointerAt)
    {
        int header = row + 1;
        int icon = row + 6;
        int block = row + 13;
        int text = row + 16;

        // The row's first 0x600 bytes exist as a whole, under the fields placed after: the
        // fake serves a read only where every byte was placed, and the block search reads the
        // row in one piece. Placed first, so the fields win where they overlap.
        tree.Reader.Place(UiTree.At(row), new byte[SkillPanelReader.HuntBytes]);

        tree.Add(row, parent: List, children: [header, row + 18, row + 19]);
        tree.Add(row + 18, parent: row, size: new Vector2(700, 90));
        tree.Add(row + 19, parent: row, size: new Vector2(20, 20));
        tree.Add(header, parent: row, children: [row + 2, row + 3, row + 4, row + 5, icon, row + 7, row + 8, row + 9, row + 10, block]);
        for (int frame = row + 2; frame <= row + 5; frame++)
        {
            tree.Add(frame, parent: header, size: new Vector2(30, 30));
        }

        tree.Add(icon, parent: header, size: new Vector2(64, 64), children: [row + 11, row + 12]);
        tree.Add(row + 11, parent: icon);
        tree.Add(row + 12, parent: icon);
        tree.Add(row + 7, parent: header, text: name);
        tree.Add(row + 8, parent: header, text: "Level:");
        tree.Add(row + 9, parent: header, text: "<augmented>{35}");
        tree.Add(row + 10, parent: header, size: new Vector2(30, 30));
        tree.Add(block, parent: header, children: [row + 14, row + 15, text, row + 17]);
        tree.Add(row + 14, parent: block);
        tree.Add(row + 15, parent: block);
        tree.Add(text, parent: block, text: dps, size: new Vector2(95, 29));
        tree.Add(row + 17, parent: block, visible: false);

        if (pointerAt != NoPointer)
        {
            tree.Reader.Place<ulong>(UiTree.At(icon) + (ulong)pointerAt, skill);
        }
    }

    private static SkillPanelReader Reader(UiTree tree, OffsetSchema schema)
        => new(tree.Reader, schema, new UiElementReader(tree.Reader, schema));

    private static void PlaceText(UiTree tree, OffsetSchema schema, int element, string text)
        => tree.Reader.PlaceStdWString(
            UiTree.At(element) + (ulong)schema.Structs["UiElementBase"].OffsetOf("TextPtr"),
            text, 0x0000_0300_5000_0000 + ((ulong)element * 0x1000));

    [Fact]
    public void EachShowingRowYieldsItsSkillsDps_KeyedByTheSkill()
    {
        OffsetSchema schema = Schema();
        (UiTree tree, PlayerSkills skills) = Window(schema);
        SkillPanelReader reader = Reader(tree, schema);

        reader.Read(UiTree.At(Root), 0, skills);

        Assert.Equal(2, reader.Dps.Count);
        Assert.Equal(53838, reader.Dps[skills.KeyOf(Spark)]);
        Assert.Equal(77522, reader.Dps[skills.KeyOf(Orb)]);
        Assert.Equal(UiTree.At(List), reader.Element);
        Assert.Equal("SkillPanel", reader.PanelName);

        // Where the pointer was found: on the icon, the header's fifth child, at the slot's
        // offset - and the names as the rows spell them, quoted, for the readout.
        int at = schema.Structs["SkillBarSlot"].OffsetOf("ActiveSkillPtr");
        Assert.Equal(
            $"2 rows read, 2 by pointer (header child 4+0x{at:X}), 0 by name; rows \"Spark\" \"Orb of Storms\"",
            reader.Note);
    }

    [Fact]
    public void ARowWithNoPointerIsMatchedByItsName()
    {
        // What the game turned out to do: no skill pointer anywhere on a row. The row prints
        // the skill's displayed name, the skill's dat row spells it the same, and that is the
        // join - reported as such. The spaces the interface pads a text with do not count.
        OffsetSchema schema = Schema();
        (UiTree tree, PlayerSkills skills) = Window(schema, pointerAt: NoPointer);
        PlaceText(tree, schema, SparkRow + 7, " Spark ");
        SkillPanelReader reader = Reader(tree, schema);

        reader.Read(UiTree.At(Root), 0, skills);

        Assert.Equal(53838, reader.Dps[skills.KeyOf(Spark)]);
        Assert.Equal(77522, reader.Dps[skills.KeyOf(Orb)]);
        Assert.Equal("2 rows read, 0 by pointer (none), 2 by name; rows \" Spark \" \"Orb of Storms\"", reader.Note);
    }

    [Fact]
    public void WhatWasReadStandsWhileThePanelIsShut_AndNothingIsWalked()
    {
        OffsetSchema schema = Schema();
        (UiTree tree, PlayerSkills skills) = Window(schema);
        SkillPanelReader reader = Reader(tree, schema);
        reader.Read(UiTree.At(Root), 0, skills);
        IReadOnlyDictionary<ulong, int> remembered = reader.Dps;

        int flags = schema.Structs["UiElementBase"].OffsetOf("Flags");
        tree.Reader.Place<uint>(UiTree.At(Panel) + (ulong)flags, 0u);

        long before = tree.Reader.Reads;
        reader.Read(UiTree.At(Root), SkillPanelReader.RefreshMs, skills);

        Assert.Same(remembered, reader.Dps);

        // The pointer, the panel's validity and its chain of visible bits - a dozen at most,
        // and no row, no text.
        Assert.True(tree.Reader.Reads - before < 12, $"{tree.Reader.Reads - before} reads on a shut panel");
    }

    [Fact]
    public void AChangedFigureIsPickedUpOnTheClock_AndPublishedAsANewDictionary()
    {
        // The dictionary handed out is never written: a snapshot holding the old one keeps
        // seeing the old figures, and the next one sees the new.
        OffsetSchema schema = Schema();
        (UiTree tree, PlayerSkills skills) = Window(schema);
        SkillPanelReader reader = Reader(tree, schema);
        reader.Read(UiTree.At(Root), 0, skills);
        IReadOnlyDictionary<ulong, int> old = reader.Dps;

        PlaceText(tree, schema, SparkDps, "DPS: 61.250");

        reader.Read(UiTree.At(Root), SkillPanelReader.RefreshMs - 1, skills);
        Assert.Same(old, reader.Dps);

        reader.Read(UiTree.At(Root), SkillPanelReader.RefreshMs, skills);
        Assert.NotSame(old, reader.Dps);
        Assert.Equal(53838, old[skills.KeyOf(Spark)]);
        Assert.Equal(61250, reader.Dps[skills.KeyOf(Spark)]);
        Assert.Equal(77522, reader.Dps[skills.KeyOf(Orb)]);
    }

    [Fact]
    public void TheListIsSearchedForWhenThePathMissesIt()
    {
        // The path is the browser's picture of one build; a patch that moves the list costs a
        // search of the panel, not a silence.
        OffsetSchema schema = Schema();
        (UiTree tree, PlayerSkills skills) = Window(schema, onThePath: false);
        SkillPanelReader reader = Reader(tree, schema);

        reader.Read(UiTree.At(Root), 0, skills);

        Assert.Equal(UiTree.At(List), reader.Element);
        Assert.Equal(2, reader.Dps.Count);
    }

    [Fact]
    public void ThePointerIsFoundWhereverTheRowKeepsIt()
    {
        // Not at the slot's offset and not on the icon: somewhere in the row element itself.
        // The block search finds it, and the rule it yields is reported.
        OffsetSchema schema = Schema();
        (UiTree tree, PlayerSkills skills) = Window(schema, pointerAt: 0x700); // off every element's first 0x600
        tree.Reader.Place<ulong>(UiTree.At(SparkRow) + 0x1A8, Spark);
        tree.Reader.Place<ulong>(UiTree.At(OrbRow) + 0x1A8, Orb);
        SkillPanelReader reader = Reader(tree, schema);

        reader.Read(UiTree.At(Root), 0, skills);

        Assert.Equal(53838, reader.Dps[skills.KeyOf(Spark)]);
        Assert.Equal(77522, reader.Dps[skills.KeyOf(Orb)]);
        Assert.Contains("row+0x1A8", reader.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void ARowReusedForAnotherGemIsMatchedAgain()
    {
        // The rows are the panel's, not the skills': socket a different gem and the same row
        // element shows it. The name changing is what says the pointer behind it may have.
        OffsetSchema schema = Schema();
        (UiTree tree, PlayerSkills skills) = Window(schema);
        SkillPanelReader reader = Reader(tree, schema);
        reader.Read(UiTree.At(Root), 0, skills);

        // The gem moves: the row that showed Spark now shows Orb of Storms, and the row that
        // showed Orb of Storms goes dark.
        int at = schema.Structs["SkillBarSlot"].OffsetOf("ActiveSkillPtr");
        int flags = schema.Structs["UiElementBase"].OffsetOf("Flags");
        PlaceText(tree, schema, SparkRow + 7, "Orb of Storms");
        PlaceText(tree, schema, SparkDps, "DPS: 80.000");
        tree.Reader.Place<ulong>(UiTree.At(SparkIcon) + (ulong)at, Orb);
        tree.Reader.Place<uint>(UiTree.At(OrbRow) + (ulong)flags, 0u);

        reader.Read(UiTree.At(Root), SkillPanelReader.RefreshMs, skills);

        Assert.Equal(80000, reader.Dps[skills.KeyOf(Orb)]);
        Assert.Equal(53838, reader.Dps[skills.KeyOf(Spark)]); // remembered, until the panel says otherwise
    }

    [Fact]
    public void NothingIsReadWithoutAnInterface()
    {
        OffsetSchema schema = Schema();
        (UiTree tree, PlayerSkills skills) = Window(schema);
        SkillPanelReader reader = Reader(tree, schema);

        reader.Read(0, 0, skills);

        Assert.Empty(reader.Dps);
        Assert.Equal("not seen yet", reader.Note);
    }
}
