using System.Numerics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Game.Ui;

/// <summary>One part of the game's on-screen interface, measured in window pixels.</summary>
/// <param name="Address">The element itself, which is what identifies an unnamed part.</param>
/// <param name="Name">
/// The element's StringId - "life_orb", "experience_bar", "HUDRight". Empty for the parts the
/// game does not name, and there are three of those under the HUD.
/// </param>
/// <param name="Where">Where it is on screen.</param>
/// <param name="From">How the rectangle was arrived at, which is worth reporting.</param>
public readonly record struct InterfacePart(ulong Address, string Name, ScreenRect Where, PanelExtent From)
{
    /// <summary>
    /// What to call it - in a list, and in the settings file that says which parts to ignore.
    /// </summary>
    /// <remarks>
    /// AN ADDRESS FOR THE UNNAMED ONES, which is honest rather than useful: it changes every
    /// launch, so a part switched off by address does not stay off. Naming it anything steadier
    /// would mean inventing a name for an element the game does not name, and then a patch that
    /// reorders the children would silently move the setting onto a different part.
    /// </remarks>
    public string Label => Name.Length > 0 ? Name : $"0x{Address:X}";
}

/// <summary>
/// Measures the game's own interface, part by part, so the map overlay can stay off it.
/// </summary>
/// <remarks>
/// THE INTERFACE IS AN ORDINARY ELEMENT and this was very nearly missed. The large map is drawn
/// across the whole window with the HUD painted on top, and it looked as though there was
/// nothing to measure - the reference tool has no equivalent either, and GameHelper2's Radar
/// solves the same problem by having the user drag a "culling window" over the game once. But
/// the interface is a single UiElement with StringId "HUD" sitting among the UI root's own
/// children, and its parts - both orbs, the experience bar, the button rows, the left and right
/// clusters - are its children, each carrying its own position and size like anything else in
/// the tree. So the region the map keeps off is READ, at whatever resolution and interface scale
/// the game is running, rather than described by somebody looking at their screen.
///
/// FOUND BY ITS ID, not by its index. It sits at child 97 of the root in this build, and a
/// position in a list of 156 siblings is the most fragile thing this project could depend on:
/// a patch inserting one element above it moves everything below, the wrong element is measured,
/// and the map is kept off a rectangle that is not there - silently, because a rectangle is a
/// rectangle. The index is used as a first guess and verified against the id, so the common case
/// is one string read and the drift case is a scan rather than a wrong answer.
///
/// CACHED ACROSS FRAMES for the same reason it is scanned at all: the scan is a string read per
/// root child, which is fine once and not fine sixty times a second. The cached address is
/// re-checked every frame - it must still be a UiElement, still say "HUD", still hang off the
/// root it was found under - so an area change or a patch costs one scan, not a wrong answer.
///
/// THE MAPS ARE NEVER PARTS. Whatever the tree turns out to look like, an element the maps live
/// under must not become a keep-out zone: doing so would take the minimap out of the region it
/// is meant to be drawn on and the radar would simply stop working, with the readout reporting
/// a perfectly healthy HUD. The maps' ancestors are excluded by address rather than by name.
///
/// AND THE ATLAS SCREEN HAS A SECOND ONE. The HUD is not the only thing painted over something
/// this tool draws on: the world screen the atlas is a page of carries its own furniture - the
/// title bar with the act tabs, the search box, the quest selector, the map legend - and those
/// are drawn over the atlas exactly as the orbs are drawn over the map. They are ordinary
/// elements too, siblings of the branch the atlas panel hangs in, so the SAME measurement
/// answers both; see <see cref="AtlasChrome"/>. What differs is only how the container is
/// found, which is why that is the one thing split out per caller.
/// </remarks>
public sealed class InterfaceReader
{
    /// <summary>The interface element's own id. What the scan is looking for.</summary>
    public const string Id = "HUD";

    private readonly IMemoryReader _reader;
    private readonly UiElementReader _elements;
    private readonly int _stringId;
    private readonly int _firstGuess;
    private readonly int _mostParts;
    private readonly int _mostRootChildren;

    private ulong _hud;
    private ulong _under;

    public InterfaceReader(IMemoryReader reader, OffsetSchema schema, UiElementReader elements)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(elements);
        _reader = reader;
        _elements = elements;

        _stringId = schema.Structs["UiElementBase"].OffsetOf("StringIdPtr");

        StructDef hud = schema.Structs["HudElement"];
        _firstGuess = (int)hud.Constants["ChildFromUiRoot"];
        _mostParts = (int)hud.Constants["MostParts"];
        _mostRootChildren = (int)hud.Constants["MostRootChildren"];
    }

    /// <summary>The interface element as last resolved, or 0 - for the readouts.</summary>
    public ulong Element => _hud;

    /// <summary>
    /// Every visible part of the interface, in window pixels.
    /// </summary>
    /// <param name="uiRoot">The interface root, whose children the HUD element sits among.</param>
    /// <param name="scale">The viewport to place the parts in.</param>
    /// <param name="notThese">
    /// Addresses that must never come back as parts, however the tree is arranged - the map
    /// elements and everything they hang under. See the remarks on this class.
    /// </param>
    public List<InterfacePart> Read(ulong uiRoot, UiScale scale, IReadOnlyCollection<ulong> notThese)
        => PartsOf(Resolve(uiRoot), scale, notThese);

    /// <summary>
    /// The furniture of the world screen the atlas is a page of, in window pixels.
    /// </summary>
    /// <remarks>
    /// THE SAME PROBLEM AS THE HUD, one screen along. The atlas panel is drawn across the whole
    /// window and the screen it lives on paints its own furniture over the top: a title bar
    /// carrying the act tabs and a close button, the search box, the quest selector, the map
    /// legend, the pin editor. The atlas overlay's web and labels landed on every one of them.
    ///
    /// FOUND BY WHERE THE ATLAS IS, not by name and not by a list. The atlas panel sits at a
    /// child path from the interface root - root, then the screen, then the page - so the
    /// furniture is exactly the SCREEN's other children: the ones the atlas does not hang under.
    /// That is computed rather than assumed, by walking up from the atlas element and excluding
    /// its whole ancestry, so no rearrangement of the pages can turn the atlas itself into a
    /// piece of furniture and blank the overlay.
    ///
    /// Confirmed in game 2026-08 from this tool's own interface browser, with the atlas open:
    /// the screen is root child 22, the atlas page hangs under its child 0, and its other
    /// children are named header (close_button, title_label, act_buttons_layout with an
    /// ActButton each), search_bar_frame, quest_selector, map_content_list, map_legends_frame,
    /// legend_anchor_frame, atlas_master_selector, endgame_map_pin_editor, favourite_pins_
    /// container_frame, special_mode_title_frame, vignette, fade_to_black and consume_input_
    /// frame, with a handful the game does not name. The screen-sized three are the reason only
    /// the VISIBLE ones are taken: honouring one while it is idle would blank the atlas overlay
    /// entirely.
    ///
    /// AND THE PANEL OVER A HOVERED MAP IS ONE OF THE UNNAMED ONES, which is what makes the
    /// children rule in <see cref="Measure"/> load-bearing rather than a nicety. Seen with a map
    /// hovered: screen child 17 is a nameless anchor with no extent of its own that moves to
    /// whichever map the cursor is on, and the panel - "Blooming Field", its biome, its flavour
    /// line, 658x194 - is its child, at relative 0,0 inside it. Measuring the anchor from its
    /// visible children is therefore the whole of how that panel gets kept off, and it is also
    /// what makes it come and go with the hover; see <c>AtlasHoverPanel</c>, which reads that
    /// coming and going rather than a name the game does not give it.
    /// </remarks>
    /// <param name="uiRoot">The interface root, which is where the walk upward stops counting.</param>
    /// <param name="atlas">The atlas panel element, as resolved from its own path.</param>
    /// <param name="scale">The viewport to place the furniture in.</param>
    public List<InterfacePart> AtlasChrome(ulong uiRoot, ulong atlas, UiScale scale)
    {
        List<InterfacePart> parts = [];
        if (!_elements.IsUiElement(atlas))
        {
            return parts;
        }

        // The atlas's whole ancestry, which is both the exclusion list and the way the screen
        // is found.
        var ancestry = new List<ulong>();
        var chain = new HashSet<ulong>();
        _elements.AndAncestors(atlas, chain, ancestry);

        // THE SCREEN IS THE ANCESTOR DIRECTLY UNDER THE ROOT WE WERE GIVEN, and it has to be
        // found by that root's position rather than by counting back from the end of the chain.
        // The chain does NOT stop where the caller's root does: the interface root is itself a
        // child of the game's real UI root, so "the second-to-last entry" lands one or more
        // levels ABOVE the screen - and that element's children are every panel in the game.
        // Keeping the atlas off all of those left the whole overlay drawing nothing at all,
        // which is exactly what it did the first time this shipped. A synthetic tree cannot
        // catch it, because a fixture's root has no parent; see the test that gives it one.
        int root = ancestry.IndexOf(uiRoot);
        return root < 1 ? parts : PartsOf(ancestry[root - 1], scale, chain);
    }

    /// <summary>
    /// Every visible child of one container, measured - the shape both readings above take.
    /// </summary>
    private List<InterfacePart> PartsOf(
        ulong container, UiScale scale, IReadOnlyCollection<ulong> notThese)
    {
        ArgumentNullException.ThrowIfNull(notThese);

        List<InterfacePart> parts = [];
        if (container == 0 || !_elements.IsVisible(container))
        {
            return parts; // nothing on screen: a loading screen, or a state without this part
        }

        List<ulong> children = _elements.Children(container, _mostParts);
        if (children.Count == 0)
        {
            return parts;
        }

        // One walk of the chain above for all of them rather than one per child - the same
        // saving PanelReader makes, and the difference between measuring the interface every
        // tick and measuring it occasionally.
        Dictionary<ulong, (Vector2 Position, Vector2 Size)> placed =
            _elements.ReadSiblings(container, children, scale);

        foreach (ulong child in children)
        {
            if (notThese.Contains(child) || !_elements.IsShowingItself(child))
            {
                continue;
            }

            // Its own bit is enough here: the HUD's whole chain was just checked, and asking
            // again per child would re-walk the same ancestors a dozen times over. Hidden parts
            // have to be left out or a cluster's other layout - the same size, sitting behind
            // the one on screen - decides the answer as much as what is drawn does.
            (ScreenRect where, PanelExtent from) = Measure(child, placed, scale, notThese);
            if (where.HasArea)
            {
                parts.Add(new InterfacePart(child, NameOf(child), where, from));
            }
        }

        return parts;
    }

    /// <summary>
    /// A part's rectangle: its own, or what its visible children cover between them.
    /// </summary>
    /// <remarks>
    /// A SIZE OF ZERO IS A REAL READING, the same as it is for a panel: several of these parts
    /// are containers that lay their children out and claim no extent themselves. HUDLeft and
    /// HUDRight are exactly that shape, and taking them at their word would keep the map off
    /// nothing at all while the buttons they hold sit under it.
    ///
    /// ONE LEVEL DOWN AND NO FURTHER. A container's children are where it put what it draws; a
    /// container's grandchildren are a tree, and descending it would turn a per-frame
    /// measurement into a per-frame walk of the interface. Anything still unmeasured after one
    /// level contributes nothing, which is the safe direction: an unmeasured part leaves the map
    /// drawn where it was, and the readout names it.
    ///
    /// One level is also exactly what the atlas's hover panel needs, and that is luck worth
    /// knowing about rather than a rule to relax: the panel is a child of a nameless anchor
    /// which is a child of the world screen, so it is measured here and would not be one level
    /// deeper. If a later screen buries something a level further down, the answer is a part
    /// that measures as nothing and says so - not a deeper walk taken every frame for all of it.
    /// </remarks>
    private (ScreenRect Where, PanelExtent From) Measure(
        ulong part,
        Dictionary<ulong, (Vector2 Position, Vector2 Size)> placed,
        UiScale scale,
        IReadOnlyCollection<ulong> notThese)
    {
        if (placed.TryGetValue(part, out (Vector2 Position, Vector2 Size) own)
            && Measurable(own.Position, own.Size))
        {
            return (Rect(own.Position, own.Size), PanelExtent.Element);
        }

        List<ulong> inside = _elements.Children(part, _mostParts);
        if (inside.Count == 0)
        {
            return (default, PanelExtent.Unmeasured);
        }

        float left = float.MaxValue;
        float top = float.MaxValue;
        float right = float.MinValue;
        float bottom = float.MinValue;

        foreach ((ulong child, (Vector2 position, Vector2 size)) in
                 _elements.ReadSiblings(part, inside, scale))
        {
            if (notThese.Contains(child) || !Measurable(position, size)
                || !_elements.IsShowingItself(child))
            {
                continue;
            }

            left = Math.Min(left, position.X);
            top = Math.Min(top, position.Y);
            right = Math.Max(right, position.X + size.X);
            bottom = Math.Max(bottom, position.Y + size.Y);
        }

        return right > left && bottom > top
            ? (new ScreenRect(left, top, right, bottom), PanelExtent.Children)
            : (default, PanelExtent.Unmeasured);
    }

    /// <summary>The interface element, from the cache when it still answers to its id.</summary>
    private ulong Resolve(ulong uiRoot)
    {
        if (uiRoot == 0)
        {
            _hud = 0;
            _under = 0;
            return 0;
        }

        if (_hud != 0 && _under == uiRoot && NameOf(_hud) == Id)
        {
            return _hud;
        }

        _under = uiRoot;
        _hud = Find(uiRoot);
        return _hud;
    }

    /// <summary>Looks where it was last seen, then scans the root's children for the id.</summary>
    private ulong Find(ulong uiRoot)
    {
        ulong guess = _elements.Child(uiRoot, _firstGuess);
        if (guess != 0 && NameOf(guess) == Id)
        {
            return guess;
        }

        foreach (ulong child in _elements.Children(uiRoot, _mostRootChildren))
        {
            if (NameOf(child) == Id)
            {
                return child;
            }
        }

        return 0;
    }

    /// <summary>An element's StringId, or empty when it has none or is not an element.</summary>
    private string NameOf(ulong element)
        => _elements.IsUiElement(element)
            ? _reader.ReadStdWString(element + (ulong)_stringId)
            : string.Empty;

    private static ScreenRect Rect(Vector2 position, Vector2 size)
        => new(position.X, position.Y, position.X + size.X, position.Y + size.Y);

    /// <summary>Whether a position and size describe a rectangle rather than a torn read.</summary>
    private static bool Measurable(Vector2 position, Vector2 size)
        => Sane(position) && Sane(size) && size.X >= 1f && size.Y >= 1f;

    private static bool Sane(Vector2 pair)
        => float.IsFinite(pair.X) && float.IsFinite(pair.Y)
           && Math.Abs(pair.X) < 100_000f && Math.Abs(pair.Y) < 100_000f;
}
