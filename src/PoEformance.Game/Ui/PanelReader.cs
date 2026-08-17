using System.Numerics;
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

/// <summary>One open panel and the screen it is sitting on, in window pixels.</summary>
/// <remarks>
/// WHY A RECTANGLE AND NOT JUST A YES. Anything drawn in world space is underneath the whole
/// panel wherever it is, so a bit per panel is all that decides. A WINDOW of the tool's own is
/// somewhere in particular: the readout parked in the top-left corner is not in the way of an
/// inventory panel on the right, and hiding it there would be taking a window away for no
/// reason the user can see. So a window asks about the ground it covers rather than about the
/// panel - see <c>WindowChrome.Covered</c>.
/// </remarks>
public readonly record struct PanelArea(GamePanel Panel, float Left, float Top, float Right, float Bottom)
{
    /// <summary>Whether a rectangle in window pixels touches this one at all.</summary>
    /// <remarks>
    /// ANY overlap rather than a share of it, because that is the question a window is asking:
    /// a corner over an open stash is a corner over an open stash. Edges that merely touch do
    /// not count - a window resting exactly against a panel's edge covers none of it.
    /// </remarks>
    public bool Overlaps(float left, float top, float right, float bottom)
        => left < Right && right > Left && top < Bottom && bottom > Top;
}

/// <summary>Which panels are open, and where they are.</summary>
/// <param name="Panels">Every panel currently open, or <see cref="GamePanel.None"/>.</param>
/// <param name="Areas">
/// Where each of them is on screen. EMPTY EVEN WHEN PANELS ARE OPEN if the read was given no
/// viewport to scale into - a UI position means nothing without one - so a caller that wants
/// rectangles must pass a <see cref="UiScale"/>, and one that only wants the bits need not.
/// </param>
public readonly record struct PanelsOnScreen(GamePanel Panels, IReadOnlyList<PanelArea> Areas)
{
    /// <summary>Nothing open: between areas, or the interface did not resolve.</summary>
    public static PanelsOnScreen Shut { get; } = new(GamePanel.None, []);
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
/// AND WHERE THEY ARE, which the reference has no equivalent of and this tool needs because it
/// also has WINDOWS of its own. A world-space layer is underneath a panel wherever the panel
/// is, so the bit is the whole answer; a window sits in one place, and whether it is in the way
/// depends on which part of the screen the panel took. See <see cref="PanelArea"/>.
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

    /// <summary>
    /// Which screen-filling panels are open and where they are, from one walk of the interface.
    /// </summary>
    /// <remarks>
    /// All of them rather than the first one found, though the world overlay only asks whether
    /// the set is empty: stopping early would make the readout report whichever panel happens
    /// to be checked first, which is the opposite of what it is for.
    ///
    /// ONE WALK for both answers, rather than a second pass for the rectangles. Which panels
    /// are open and where they sit come from the same elements, and resolving them twice is
    /// twice the pointer chasing for a question already answered.
    /// </remarks>
    /// <param name="scale">
    /// The viewport to place the panels in, or null for the bits only. Everything a UiElement
    /// stores is in the interface's own 2560x1600 space, so without one there is nothing to
    /// say about where a panel is.
    /// </param>
    public PanelsOnScreen Read(ulong uiRoot, UiScale? scale)
    {
        if (uiRoot == 0)
        {
            return PanelsOnScreen.Shut;
        }

        GamePanel open = GamePanel.None;
        List<PanelArea> areas = [];

        foreach ((GamePanel panel, ulong element) in Candidates(uiRoot))
        {
            if (!Showing(element))
            {
                continue;
            }

            open |= panel;

            if (scale is UiScale viewport)
            {
                Where(panel, element, viewport, areas);
            }
        }

        return new PanelsOnScreen(open, areas);
    }

    /// <summary>Every panel worth asking about, and the element that answers for it.</summary>
    private IEnumerable<(GamePanel Panel, ulong Element)> Candidates(ulong uiRoot)
    {
        yield return (GamePanel.Left, _reader.ReadPointer(uiRoot + (ulong)_left));
        yield return (GamePanel.Right, _reader.ReadPointer(uiRoot + (ulong)_right));
        yield return (GamePanel.WorldMap, _reader.ReadPointer(uiRoot + (ulong)_worldMap));

        // The tree's NODE CONTAINER, not the panel: the reference records that in 0.5.x the
        // per-node pointers read null while this one's visible bit stays reliable.
        yield return (
            GamePanel.SkillTree,
            _elements.Child(_reader.ReadPointer(uiRoot + (ulong)_skillTree), _skillTreeChild));

        foreach ((GamePanel panel, int[] path) in _paths)
        {
            yield return (panel, Resolve(uiRoot, path));
        }
    }

    /// <summary>
    /// The panels that take the WHOLE screen when they are open.
    /// </summary>
    /// <remarks>
    /// Everything here except the two side panels, which is what the list is for: it is the
    /// fallback when an element's own size cannot be read, and a guess of "all of it" is only
    /// safe where the panel really does cover all of it. The left panel (character, skills,
    /// quests) and the right one (inventory, stash, a vendor) leave most of the screen alone,
    /// so a missing size there means no rectangle at all rather than a screen's worth.
    /// </remarks>
    private const GamePanel WholeScreen =
        GamePanel.SkillTree | GamePanel.WorldMap | GamePanel.Atlas | GamePanel.AtlasSkills
        | GamePanel.Temple | GamePanel.Exchange | GamePanel.Trial;

    /// <summary>Records where an open panel is, if that can be said at all.</summary>
    /// <remarks>
    /// A SIZE OF ZERO IS A REAL ANSWER HERE, not a broken read: the large map's UnscaledSize
    /// reads 0 in PoE2 and its position is fine, which is a container that lays its children
    /// out without claiming any extent of its own. So a panel that cannot say how big it is
    /// falls back to the whole screen where the panel is known to cover it (see
    /// <see cref="WholeScreen"/>), and to NOTHING where it is not - a side panel of unknown
    /// extent hides nothing, which is the state before any of this existed.
    ///
    /// CLIPPED TO THE WINDOW, which does two jobs: an absurd rectangle from a wrong offset
    /// becomes "the screen" rather than a region reaching to infinity, and one that resolves
    /// entirely off-screen becomes nothing at all and is dropped. What a caller gets is
    /// therefore always a piece of the screen somebody can point at.
    /// </remarks>
    private void Where(GamePanel panel, ulong element, UiScale scale, List<PanelArea> areas)
    {
        UiElement? read = _elements.Read(element, scale, withStringId: false);

        if (read is null || !Sane(read.Position) || !Sane(read.Size)
            || read.Size.X < 1f || read.Size.Y < 1f)
        {
            if ((panel & WholeScreen) != 0)
            {
                areas.Add(new PanelArea(panel, 0f, 0f, scale.WindowWidth, scale.WindowHeight));
            }

            return;
        }

        float left = Math.Max(read.Left, 0f);
        float top = Math.Max(read.Top, 0f);
        float right = Math.Min(read.Right, scale.WindowWidth);
        float bottom = Math.Min(read.Bottom, scale.WindowHeight);

        if (right - left < 1f || bottom - top < 1f)
        {
            return; // off the side of the screen, or nothing left of it once clipped
        }

        areas.Add(new PanelArea(panel, left, top, right, bottom));
    }

    /// <summary>Whether a pair of floats came from memory rather than from a torn read.</summary>
    private static bool Sane(Vector2 pair)
        => float.IsFinite(pair.X) && float.IsFinite(pair.Y)
           && Math.Abs(pair.X) < 100_000f && Math.Abs(pair.Y) < 100_000f;

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
