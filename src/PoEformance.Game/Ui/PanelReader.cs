using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Game.Ui;

/// <summary>
/// The panels that take over the screen, one bit each.
/// </summary>
/// <remarks>
/// FLAGS RATHER THAN A BOOL, though every caller so far asks the same yes-or-no question. The
/// paths these are found at are unverified, and the way a wrong one fails is that something is
/// reported open forever and the overlay never comes back. A bool cannot say which; this can,
/// and the status window prints it, so a stuck panel names itself instead of looking like an
/// overlay that stopped working.
/// </remarks>
[Flags]
public enum GamePanel
{
    None = 0,

    /// <summary>Character, skills, quests - whichever left-side panel is open.</summary>
    Left = 1 << 0,

    /// <summary>Inventory, stash, a vendor's window.</summary>
    Right = 1 << 1,

    /// <summary>The passive tree.</summary>
    SkillTree = 1 << 2,

    /// <summary>The world-travel map: the act screens, not the endgame atlas.</summary>
    WorldMap = 1 << 3,

    /// <summary>The endgame atlas.</summary>
    Atlas = 1 << 4,

    /// <summary>The atlas passive tree, which is a panel of its own.</summary>
    AtlasSkills = 1 << 5,

    /// <summary>The temple console.</summary>
    Temple = 1 << 6,

    /// <summary>The currency exchange.</summary>
    Exchange = 1 << 7,

    /// <summary>The Trial of the Sekhemas map.</summary>
    Trial = 1 << 8,
}

/// <summary>
/// Which of the game's screen-filling panels are open.
/// </summary>
/// <remarks>
/// WHAT THIS IS FOR: an overlay that draws in world space is drawing UNDERNEATH whatever the
/// player is actually looking at the moment one of these opens. Markers over a stash, health
/// bars across the passive tree - the information is correct and in the way, which is worse
/// than not drawing it. Ported from GameHelper2's <c>IsAnyLargePanelOpen</c>, which its own
/// HealthBars and Radar plugins use for exactly this.
///
/// TWO KINDS OF ANSWER, and the difference is worth knowing before trusting one. The left
/// panel, the right panel, the world map and the skill tree are POINTERS on the interface
/// root: the game nulls them when nothing is open, so a wrong offset reads as a bad pointer
/// and the panel simply never reports open. The other four are CHILD PATHS - a position in a
/// list somebody else's client built - and a patch that inserts one panel above them moves
/// every one. That usually lands on nothing, which is harmless; it can land on a real element
/// that is always showing, and then the overlay is hidden with nothing to say why.
///
/// VISIBILITY IS THE WHOLE CHAIN. Every check here goes through
/// <see cref="UiElementReader.IsVisible"/> rather than reading one flag, because a panel shut
/// by its container keeps its own bit set - the same rule the atlas is read by.
/// </remarks>
public sealed class PanelReader
{
    private readonly IMemoryReader _reader;
    private readonly UiElementReader _elements;

    private readonly int _left;
    private readonly int _right;
    private readonly int _worldMap;
    private readonly int _skillTree;
    private readonly int _skillTreeChild;

    private readonly (GamePanel Panel, int[] Path)[] _paths;

    public PanelReader(IMemoryReader reader, OffsetSchema schema, UiElementReader elements)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(elements);
        _reader = reader;
        _elements = elements;

        StructDef root = schema.Structs["ImportantUiElements"];
        _left = root.OffsetOf("LeftPanelPtr");
        _right = root.OffsetOf("RightPanelPtr");
        _worldMap = root.OffsetOf("WorldMapPanelPtr");
        _skillTree = root.OffsetOf("PassiveSkillTreePanel");

        StructDef big = schema.Structs["BigPanels"];
        _skillTreeChild = (int)big.Constants["SkillTreeNodesChild"];

        // The atlas path is READ FROM THE ATLAS's own block rather than copied here. It is the
        // one path in this list something else already depends on, and two copies of a number
        // that drifts is two places to correct and one to forget.
        StructDef atlas = schema.Structs["AtlasPanel"];

        _paths =
        [
            (GamePanel.Atlas, [
                (int)atlas.Constants["PathFromUiRoot0"],
                (int)atlas.Constants["PathFromUiRoot1"],
                (int)atlas.Constants["PathFromUiRoot2"],
            ]),
            (GamePanel.AtlasSkills, [
                (int)big.Constants["AtlasSkillsPath0"],
                (int)big.Constants["AtlasSkillsPath1"],
            ]),
            (GamePanel.Temple, [
                (int)big.Constants["TemplePath0"],
                (int)big.Constants["TemplePath1"],
            ]),
            (GamePanel.Exchange, [
                (int)big.Constants["ExchangePath0"],
                (int)big.Constants["ExchangePath1"],
                (int)big.Constants["ExchangePath2"],
                (int)big.Constants["ExchangePath3"],
            ]),
            (GamePanel.Trial, [
                (int)big.Constants["TrialPath0"],
                (int)big.Constants["TrialPath1"],
            ]),
        ];
    }

    /// <summary>Every screen-filling panel currently open, or <see cref="GamePanel.None"/>.</summary>
    /// <remarks>
    /// All of them rather than the first one found, though every caller so far only asks
    /// whether the set is empty: stopping early would make the readout report whichever panel
    /// happens to be checked first, which is the opposite of what it is for.
    /// </remarks>
    public GamePanel Open(ulong uiRoot)
    {
        if (uiRoot == 0)
        {
            return GamePanel.None;
        }

        GamePanel open = GamePanel.None;

        if (Showing(_reader.ReadPointer(uiRoot + (ulong)_left)))
        {
            open |= GamePanel.Left;
        }

        if (Showing(_reader.ReadPointer(uiRoot + (ulong)_right)))
        {
            open |= GamePanel.Right;
        }

        if (Showing(_reader.ReadPointer(uiRoot + (ulong)_worldMap)))
        {
            open |= GamePanel.WorldMap;
        }

        // The tree's NODE CONTAINER, not the panel: the reference records that in 0.5.x the
        // per-node pointers read null while this one's visible bit stays reliable.
        if (Showing(_elements.Child(_reader.ReadPointer(uiRoot + (ulong)_skillTree), _skillTreeChild)))
        {
            open |= GamePanel.SkillTree;
        }

        foreach ((GamePanel panel, int[] path) in _paths)
        {
            if (Showing(Resolve(uiRoot, path)))
            {
                open |= panel;
            }
        }

        return open;
    }

    /// <summary>Walks a child path from the interface root, or 0 when it leads nowhere.</summary>
    private ulong Resolve(ulong uiRoot, int[] path)
    {
        ulong at = uiRoot;
        foreach (int index in path)
        {
            if (at == 0)
            {
                return 0;
            }

            at = _elements.Child(at, index);
        }

        return at;
    }

    /// <summary>
    /// Whether an element is on the screen - which a null or a wrong address is not.
    /// </summary>
    /// <remarks>
    /// Fails towards NOT OPEN, deliberately. Everything here is unverified, and the two ways
    /// to be wrong are not equally bad: a check that misses a panel leaves an overlay drawn
    /// over it, which is what happens today; one that invents a panel takes the overlay away
    /// and gives no reason. <see cref="UiElementReader.IsVisible"/> refuses anything that is
    /// not a UiElement, so a path into nothing lands on the harmless side of that.
    /// </remarks>
    private bool Showing(ulong element) => element != 0 && _elements.IsVisible(element);
}
