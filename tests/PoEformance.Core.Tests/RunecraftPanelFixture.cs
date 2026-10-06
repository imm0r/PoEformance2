using System.Numerics;
using PoEformance.Core.Schema;

namespace PoEformance.Core.Tests;

/// <summary>
/// A Runeshape Combinations panel laid into fake memory, shaped after the reference's reading
/// of the real one: the five fingerprinted levels, a scrolled viewport, and rows that each
/// carry a label and a back-pointer to a recipe.
/// </summary>
/// <remarks>
/// Built THROUGH THE SHIPPED SCHEMA - fingerprints, offsets and all - so the fixture moves with
/// the file rather than vouching for a copy of it. Two decoys are deliberate, because the two
/// failure modes they stand for are the ones the reference recorded: a VISIBLE gate with the
/// right fingerprint and no recipe list under it, placed before the real one so a walk that does
/// not backtrack dead-ends in it; and a SHUT gate with a complete list under it, which a scan
/// that accepted invisible gates would report as open.
///
/// The rows: one priced by name, one whose art fails the Art/ guard, one with no fixed reward
/// (a rolled gem), one whose back-pointer is the all-0xFF sentinel the reference hit, one hidden,
/// and one scrolled far below the viewport.
/// </remarks>
internal sealed class RunecraftPanelFixture
{
    public const int Root = 0;
    public const int DecoyGate = 1;
    public const int ShutGate = 90;
    public const int Gate = 2;
    public const int Frame = 3;
    public const int Viewport = 4;
    public const int Content = 5;
    public const int Container = 6;

    public const int ExaltedRow = 10;
    public const int RegalRow = 11;
    public const int HiddenRow = 12;
    public const int GemRow = 13;
    public const int SentinelRow = 16;
    public const int FarRow = 17;

    /// <summary>Where the recipe and item rows go. Far from the elements and from each other.</summary>
    private const ulong Recipes = 0x0000_0300_4000_0000;
    private const ulong Items = 0x0000_0300_5000_0000;
    private const ulong Visuals = 0x0000_0300_6000_0000;
    private const ulong Strings = 0x0000_0300_7000_0000;

    public static readonly Vector2 ViewportAt = new(300, 200);
    public static readonly Vector2 ViewportSize = new(770, 800);
    public static readonly Vector2 Scroll = new(0, -120);

    private readonly OffsetSchema _schema;
    private ulong _strings = Strings;

    public RunecraftPanelFixture(OffsetSchema schema, bool contentTakesModifier = false, bool gateVisible = true)
    {
        ArgumentNullException.ThrowIfNull(schema);
        _schema = schema;
        Tree = new UiTree(schema);

        // THE VISIBLE BIT IS MASKED OUT of the fingerprints here, as the reader masks it on every
        // comparison: the reference captured them off elements that were showing, so every one
        // carries 0x800, and a fixture that placed them raw would make its shut twin read as
        // open - which is exactly the case the twin exists to catch.
        StructDef panel = schema.Structs["RunecraftPanel"];
        uint[] fp =
        [
            (uint)panel.Constants["Fingerprint0"] & ~UiTree.FlagVisible,
            (uint)panel.Constants["Fingerprint1"] & ~UiTree.FlagVisible,
            (uint)panel.Constants["Fingerprint2"] & ~UiTree.FlagVisible,
            (uint)panel.Constants["Fingerprint3"] & ~UiTree.FlagVisible,
            (uint)panel.Constants["Fingerprint4"] & ~UiTree.FlagVisible,
        ];

        // The root: the decoy first, the shut twin second, the real gate last - so a walk has
        // to step over both to find it.
        Tree.Add(Root, children: [DecoyGate, ShutGate, Gate]);

        // The decoy: the whole chain, ending in a container whose rows carry no label.
        Tree.Add(DecoyGate, parent: Root, flags: fp[0], children: [7]);
        Tree.Add(7, parent: DecoyGate, flags: fp[1], children: [8]);
        Tree.Add(8, parent: 7, flags: fp[2], size: ViewportSize, children: [9]);
        Tree.Add(9, parent: 8, flags: fp[3], children: [14]);
        Tree.Add(14, parent: 9, flags: fp[4], children: [15]);
        Tree.Add(15, parent: 14, size: new Vector2(700, 60), children: [18]);
        Tree.Add(18, parent: 15, text: string.Empty);

        // The shut twin: complete, labelled, and invisible at the gate.
        Tree.Add(ShutGate, parent: Root, visible: false, flags: fp[0], children: [91]);
        Tree.Add(91, parent: ShutGate, flags: fp[1], children: [92]);
        Tree.Add(92, parent: 91, flags: fp[2], size: ViewportSize, children: [93]);
        Tree.Add(93, parent: 92, flags: fp[3], children: [94]);
        Tree.Add(94, parent: 93, flags: fp[4], children: [95]);
        Tree.Add(95, parent: 94, size: new Vector2(700, 60), children: [96]);
        Tree.Add(96, parent: 95, text: "1x Shut Twin");

        // The real one.
        Tree.Add(Gate, parent: Root, visible: gateVisible, flags: fp[0], children: [Frame]);
        Tree.Add(Frame, parent: Gate, flags: fp[1], children: [Viewport]);
        Tree.Add(
            Viewport, parent: Frame, flags: fp[2],
            relative: ViewportAt, size: ViewportSize, positionModifier: Scroll, children: [Content]);
        Tree.Add(Content, parent: Viewport, flags: fp[3], modifiesPosition: contentTakesModifier, children: [Container]);
        Tree.Add(
            Container, parent: Content, flags: fp[4],
            children: [ExaltedRow, RegalRow, HiddenRow, GemRow, SentinelRow, FarRow]);

        Row(ExaltedRow, "3x Exalted Orb", new Vector2(0, 0), 20);
        Row(RegalRow, "1x Greater Regal Orb", new Vector2(0, 70), 21);
        Row(HiddenRow, "1x Hidden Reward", new Vector2(0, 140), 22, visible: false);
        Row(GemRow, "Uncut Skill Gem (Level 19)", new Vector2(0, 210), 23);
        Row(SentinelRow, "1x Mirror of Kalandra", new Vector2(0, 280), 24);
        Row(FarRow, "1x Chaos Orb", new Vector2(0, 1400), 25);

        Recipe(ExaltedRow, "4SlotExaltedOrb3", count: 3,
            reward: Item("Metadata/Items/Currency/CurrencyAddModToRare", "Exalted Orb", "Art/2DItems/Currency/CurrencyAddModToRare.dds"));
        Recipe(RegalRow, "4SlotGreaterRegalOrb1", count: 1,
            reward: Item("Metadata/Items/Currency/CurrencyUpgradeMagicToRare2", "Greater Regal Orb", "Metadata/NotArt/Elsewhere"));
        Recipe(HiddenRow, "2SlotHidden1", count: 1,
            reward: Item("Metadata/Items/Currency/CurrencyHidden", "Hidden Reward", ""));
        Recipe(GemRow, "2SlotUncutSkillGem1", count: 1, reward: 0, gemLevel: 19);
        Recipe(FarRow, "3SlotChaosOrb1", count: 1,
            reward: Item("Metadata/Items/Currency/CurrencyRerollRare", "Chaos Orb", "Art/2DItems/Currency/CurrencyRerollRare.dds"));

        // The sentinel: every byte 0xFF where the recipe pointer sits, which is not null and is
        // not a pointer either.
        Tree.Reader.Place<ulong>(UiTree.At(SentinelRow) + (ulong)panel.OffsetOf("RecipeRowPtr"), ulong.MaxValue);
    }

    public UiTree Tree { get; }

    public FakeMemoryReader Reader => Tree.Reader;

    private void Row(int index, string label, Vector2 relative, int labelIndex, bool visible = true)
    {
        Tree.Add(index, parent: Container, visible: visible, relative: relative, size: new Vector2(700, 60), children: [labelIndex]);
        Tree.Add(labelIndex, parent: index, text: label);
    }

    private void Recipe(int row, string id, int count, ulong reward, int gemLevel = 0)
    {
        StructDef recipe = _schema.Structs["Expedition2RecipesRow"];
        ulong at = Recipes + ((ulong)row * 0x200);

        Reader.Place<ulong>(at + (ulong)recipe.OffsetOf("IdPtr"), Text(id));
        Reader.Place<int>(at + (ulong)recipe.OffsetOf("MinLevelReq"), 1);
        Reader.Place<int>(at + (ulong)recipe.OffsetOf("MaxLevelReq"), 100);
        Reader.Place<ulong>(at + (ulong)recipe.OffsetOf("RewardRowPtr"), reward);
        Reader.Place<int>(at + (ulong)recipe.OffsetOf("RewardCount"), count);
        Reader.Place<int>(at + (ulong)recipe.OffsetOf("RewardGemLevel"), gemLevel);

        Reader.Place<ulong>(UiTree.At(row) + (ulong)_schema.Structs["RunecraftPanel"].OffsetOf("RecipeRowPtr"), at);
    }

    private int _items;

    private ulong Item(string path, string name, string dds)
    {
        StructDef item = _schema.Structs["BaseItemTypesRow"];
        ulong at = Items + ((ulong)_items * 0x1000);
        ulong visual = Visuals + ((ulong)_items * 0x1000);
        _items++;

        Reader.Place<ulong>(at + (ulong)item.OffsetOf("IdPtr"), Text(path));
        Reader.Place<ulong>(at + (ulong)item.OffsetOf("NamePtr"), Text(name));

        if (dds.Length > 0)
        {
            Reader.Place<ulong>(visual + (ulong)_schema.Structs["ItemVisualIdentityRow"].OffsetOf("DdsFilePtr"), Text(dds));
            Reader.Place<ulong>(at + (ulong)item.OffsetOf("VisualIdentityPtr"), visual);
        }
        else
        {
            Reader.Place<ulong>(at + (ulong)item.OffsetOf("VisualIdentityPtr"), 0UL);
        }

        return at;
    }

    /// <summary>A raw NUL-terminated UTF-16 string, the way a .dat table's string column points at one.</summary>
    /// <remarks>
    /// ON A MAPPED PAGE, which is what the zero block under it stands for. The reader asks for a
    /// whole string's worth of bytes at once and halves the ask on a refusal, as a string near
    /// the end of a real page makes it; a fake that mapped only the characters would refuse the
    /// first ask every time and hand back the first thirty-two characters of every path.
    /// </remarks>
    private ulong Text(string text)
    {
        ulong at = _strings;
        Reader.Place(at, new byte[0x400]);
        Reader.PlaceUtf16(at, text);
        _strings += 0x1000;
        return at;
    }

    /// <summary>Flips the real gate's visible bit, as the game does when the panel opens or shuts.</summary>
    public void SetOpen(bool open)
    {
        StructDef ui = _schema.Structs["UiElementBase"];
        uint fp = (uint)_schema.Structs["RunecraftPanel"].Constants["Fingerprint0"] & ~UiTree.FlagVisible;
        Reader.Place<uint>(UiTree.At(Gate) + (ulong)ui.OffsetOf("Flags"), fp | (open ? UiTree.FlagVisible : 0u));
    }

    /// <summary>Rewrites one row's label, as the game does when another monolith's panel opens.</summary>
    public void Relabel(int labelIndex, string text)
    {
        ulong at = UiTree.At(labelIndex) + (ulong)_schema.Structs["UiElementBase"].OffsetOf("TextPtr");
        Reader.PlaceStdWString(at, text, _strings);
        _strings += 0x1000;
    }
}
