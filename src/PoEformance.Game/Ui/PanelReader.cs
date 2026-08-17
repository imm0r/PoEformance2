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

/// <summary>How a panel's rectangle was arrived at, which is worth reporting.</summary>
/// <remarks>
/// NOT DECORATION. The three differ in how much they can be trusted, and a screenshot of the
/// status readout has to be able to say which one produced the rectangle it is showing - that
/// is the difference between "the panel measures 1200x900" and "nobody could measure it, so the
/// whole screen was assumed". The first recording of an open panel arrived with the position and
/// size never read at all, which is exactly the sort of thing this makes visible.
/// </remarks>
public enum PanelExtent
{
    /// <summary>The panel element's own position and size.</summary>
    Element,

    /// <summary>What its visible children cover between them, the element claiming nothing.</summary>
    Children,

    /// <summary>Nothing could be measured, so the whole window is assumed.</summary>
    Screen,
}

/// <summary>One open panel and the screen it is sitting on, in window pixels.</summary>
/// <remarks>
/// WHY A RECTANGLE AND NOT JUST A YES. Anything drawn in world space is underneath the whole
/// panel wherever it is, so a bit per panel is all that decides. A WINDOW of the tool's own is
/// somewhere in particular: a readout in the corner is not in the way of a panel that does not
/// reach the corner, and hiding it there would be taking a window away for no reason the user
/// can see. So a window asks about the ground it covers rather than about the panel - see
/// <c>WindowChrome.Covered</c>.
/// </remarks>
public readonly record struct PanelArea(
    GamePanel Panel, float Left, float Top, float Right, float Bottom, PanelExtent From)
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
    /// How many of a panel's children are worth measuring. See <see cref="Drawn"/>.
    /// </summary>
    /// <remarks>
    /// A panel's own child list is a handful of pages and frames, not the hundreds of nodes the
    /// atlas hangs under one element - so a cap this low costs nothing real and keeps a wrong
    /// pointer from turning one panel into a thousand reads a tick.
    /// </remarks>
    private const int MostChildren = 64;

    /// <summary>Records where an open panel is, by the best measurement available.</summary>
    /// <remarks>
    /// THREE SOURCES, TRIED IN ORDER, because the obvious one is empty in this game more often
    /// than not. A SIZE OF ZERO IS A REAL READING HERE - the large map's UnscaledSize is 0 and
    /// its position is fine - and it means a container that lays its children out without
    /// claiming any extent of its own. The first recording of PoE2's inventory panel read
    /// exactly that way, which left the whole feature inert: an open panel with no rectangle
    /// hides nothing.
    ///
    /// So: the element's own rectangle; failing that, WHAT ITS CHILDREN COVER BETWEEN THEM,
    /// which is where a container that draws nothing itself has put everything it draws; and
    /// failing both, the whole window. The last is the only step that is an assumption rather
    /// than a measurement, and it is the safe direction for these panels specifically - every
    /// one of them is a panel the player is looking AT, and PoE2's are far larger than their
    /// names suggest (the inventory covers the screen down to the hotbar). Which of the three
    /// answered is carried on the area and printed in the status readout, so an assumption never
    /// passes itself off as a measurement.
    ///
    /// CLIPPED TO THE WINDOW, which does two jobs: an absurd rectangle from a wrong offset
    /// becomes "the screen" rather than a region reaching to infinity, and one that resolves
    /// entirely off-screen becomes nothing at all and is dropped. What a caller gets is
    /// therefore always a piece of the screen somebody can point at.
    /// </remarks>
    private void Where(GamePanel panel, ulong element, UiScale scale, List<PanelArea> areas)
    {
        (float Left, float Top, float Right, float Bottom, PanelExtent From) measured =
            Own(element, scale)
            ?? Drawn(element, scale)
            ?? (0f, 0f, scale.WindowWidth, scale.WindowHeight, PanelExtent.Screen);

        float left = Math.Max(measured.Left, 0f);
        float top = Math.Max(measured.Top, 0f);
        float right = Math.Min(measured.Right, scale.WindowWidth);
        float bottom = Math.Min(measured.Bottom, scale.WindowHeight);

        if (right - left < 1f || bottom - top < 1f)
        {
            return; // off the side of the screen, or nothing left of it once clipped
        }

        areas.Add(new PanelArea(panel, left, top, right, bottom, measured.From));
    }

    /// <summary>The rectangle the panel element itself claims, or null when it claims none.</summary>
    private (float Left, float Top, float Right, float Bottom, PanelExtent From)? Own(
        ulong element, UiScale scale)
    {
        UiElement? read = _elements.Read(element, scale, withStringId: false);

        return Measurable(read?.Position, read?.Size)
            ? (read!.Left, read.Top, read.Right, read.Bottom, PanelExtent.Element)
            : null;
    }

    /// <summary>
    /// What the panel's visible children cover between them, for a container that claims nothing.
    /// </summary>
    /// <remarks>
    /// Their positions come from <see cref="UiElementReader.ReadSiblings"/>, which walks the
    /// chain above the panel ONCE for all of them rather than per child - the difference between
    /// a panel that can be measured every tick and one that cannot.
    ///
    /// Visibility is each child's OWN bit here, which is the one place that is enough: every
    /// ancestor was just checked by <see cref="Showing"/> on the panel itself, and re-walking
    /// the chain per child would ask the same question sixty times over. Hidden children have to
    /// be left out or a panel's other tab - the same size, sitting behind the open one - decides
    /// the answer as much as what is on screen does.
    /// </remarks>
    private (float Left, float Top, float Right, float Bottom, PanelExtent From)? Drawn(
        ulong element, UiScale scale)
    {
        List<ulong> children = _elements.Children(element, MostChildren);
        if (children.Count == 0)
        {
            return null;
        }

        float left = float.MaxValue;
        float top = float.MaxValue;
        float right = float.MinValue;
        float bottom = float.MinValue;

        foreach ((ulong child, (Vector2 position, Vector2 size)) in
                 _elements.ReadSiblings(element, children, scale))
        {
            if (!Measurable(position, size) || !_elements.IsShowingItself(child))
            {
                continue;
            }

            left = Math.Min(left, position.X);
            top = Math.Min(top, position.Y);
            right = Math.Max(right, position.X + size.X);
            bottom = Math.Max(bottom, position.Y + size.Y);
        }

        return right > left && bottom > top
            ? (left, top, right, bottom, PanelExtent.Children)
            : null;
    }

    /// <summary>Whether a position and size came from memory and describe a real rectangle.</summary>
    /// <remarks>
    /// A pair of coordinates in the millions is a torn or misaddressed read rather than a very
    /// large panel, and a size under a pixel is the "container claims nothing" case. Both mean
    /// the same thing to a caller: this is not the measurement, try the next one.
    /// </remarks>
    private static bool Measurable(Vector2? position, Vector2? size)
        => position is Vector2 at && size is Vector2 extent
           && Sane(at) && Sane(extent) && extent.X >= 1f && extent.Y >= 1f;

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
