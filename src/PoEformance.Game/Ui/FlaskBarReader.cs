using System.Numerics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Game.Ui;

/// <summary>One slot of the belt as the HUD draws it, and the item in it.</summary>
/// <param name="Element">The slot element itself.</param>
/// <param name="Item">The item entity it holds, or 0 while it is empty.</param>
/// <param name="Where">Where it is on screen, in window pixels.</param>
public readonly record struct FlaskSlotOnScreen(ulong Element, ulong Item, ScreenRect Where);

/// <summary>
/// Finds the belt's slots on the HUD, so something can be drawn on each flask.
/// </summary>
/// <remarks>
/// WHAT THE GAME NAMES, read from this tool's own interface browser (2026-09, 0.5.5): under the
/// HUD's HUDLeft cluster sits an element with StringId "flask_bar", and its children are the
/// belt - each flask slot inside an unnamed wrapper of its own as the wrapper's first child,
/// the charm slots directly, then a few decorations. Nothing under the bar is named, which is
/// why the slots are told apart by something better than a name: a slot element carries the
/// item it holds at UiElementBase.ItemPtr, the same field an inventory slot does, so a slot IS
/// whatever under the bar points at an item. That survives a patch re-ordering the bar's
/// children, where a child index would silently put the number on the wrong slot.
///
/// CACHED, THE WAY THE HUD ITSELF IS. The bar is found by name once and re-checked every tick -
/// it must still be an element and still say "flask_bar" - and the slot elements found under
/// it are kept, since a slot is the same element for the life of the HUD whatever goes in and
/// out of it. What is read per tick is what changes: each slot's item, its own visibility, and
/// where it is. The search itself runs on a clock, every couple of seconds, for two reasons. A
/// slot that was EMPTY when it ran points at nothing and so was never found - a flask equipped
/// in a map would otherwise go without its number until the next area. And a slot that STOPS
/// being an element is dropped until the clock brings it back, not searched for on the spot:
/// the bar has children that come and go (the browser caught one under a charm slot at a
/// different address on each of three looks), and had the loss of any remembered element sent
/// the search round, one such child carrying an item would have it run every tick. A rebuilt
/// interface is noticed sooner anyway, through a new HUD element or a bar that no longer
/// answers to its name, either of which forgets everything and searches at once.
///
/// TWO LEVELS UNDER THE BAR AND NO FURTHER, for the reason InterfaceReader goes one under the
/// HUD: that is where the browser found the slots, and a deeper walk every few seconds would
/// be a walk of the interface for two numbers. A charm's item, if it is anywhere, is not at
/// either level - and the game prints a charm's charges itself, so nothing is missed.
/// </remarks>
public sealed class FlaskBarReader
{
    /// <summary>The cluster the bar sits in, as the game names it.</summary>
    public const string ClusterId = "HUDLeft";

    /// <summary>The bar's own id.</summary>
    public const string BarId = "flask_bar";

    /// <summary>How long a found set of slots stands before the bar is searched again.</summary>
    /// <remarks>
    /// Long enough that the search - a few dozen reads - is nothing beside a tick, short enough
    /// that a flask equipped mid-map gets its number before anybody wonders where it is.
    /// </remarks>
    public const long SearchAgainMs = 2000;

    /// <summary>Most children looked at, at each level. A bound on a count read out of memory.</summary>
    private const int MostChildren = 64;

    /// <summary>The search clock before the first search under a bar.</summary>
    private const long Never = long.MinValue;

    private readonly IMemoryReader _reader;
    private readonly UiElementReader _elements;
    private readonly int _stringId;
    private readonly List<(ulong Parent, ulong Element)> _slots = [];

    private ulong _hud;
    private ulong _bar;
    private long _searchedAt = Never;

    public FlaskBarReader(IMemoryReader reader, OffsetSchema schema, UiElementReader elements)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(elements);
        _reader = reader;
        _elements = elements;
        _stringId = schema.Structs["UiElementBase"].OffsetOf("StringIdPtr");
    }

    /// <summary>The bar element as last resolved, or 0 - for the readouts.</summary>
    public ulong Element => _bar;

    /// <summary>
    /// The belt's slots as they stand this tick. Empty while the bar is not on screen.
    /// </summary>
    /// <param name="hud">The interface element as InterfaceReader resolved it, or 0.</param>
    /// <param name="scale">The viewport to place the slots in.</param>
    /// <param name="nowMs">A monotonic clock, for pacing the search.</param>
    public IReadOnlyList<FlaskSlotOnScreen> Read(ulong hud, UiScale scale, long nowMs)
    {
        ulong bar = Resolve(hud);
        if (bar == 0 || !_elements.IsVisible(bar))
        {
            return [];
        }

        // Checked against the clock alone, never against the count: a bar with nothing found
        // under it - no flask equipped, or the item pointer drifted - would otherwise be
        // searched every tick for as long as it stays that way.
        if (_searchedAt == Never || nowMs - _searchedAt >= SearchAgainMs)
        {
            Find(bar, nowMs);
        }
        else
        {
            DropTheDead();
        }

        var found = new List<FlaskSlotOnScreen>(_slots.Count);
        foreach ((ulong parent, ulong element) in _slots)
        {
            // Its own bit is enough: the bar's whole chain was checked above, and every slot
            // hangs one or two levels under it. That it IS still an element was settled by
            // the search or the cull just before.
            if (!_elements.IsShowingItself(element))
            {
                continue;
            }

            if (!_elements.ReadSiblings(parent, [element], scale).TryGetValue(
                    element, out (Vector2 Position, Vector2 Size) box))
            {
                continue;
            }

            var where = new ScreenRect(
                box.Position.X, box.Position.Y, box.Position.X + box.Size.X, box.Position.Y + box.Size.Y);
            if (where.HasArea)
            {
                found.Add(new FlaskSlotOnScreen(element, _elements.ItemOf(element), where));
            }
        }

        return found;
    }

    /// <summary>The bar, from the cache while it still answers to its name under this HUD.</summary>
    private ulong Resolve(ulong hud)
    {
        if (hud == 0)
        {
            Forget();
            return 0;
        }

        if (_bar != 0 && _hud == hud && NameOf(_bar) == BarId)
        {
            return _bar;
        }

        Forget();
        _hud = hud;
        _bar = FindBar(hud);
        return _bar;
    }

    /// <summary>The cluster by name among the HUD's parts, then the bar by name among its children.</summary>
    private ulong FindBar(ulong hud)
    {
        foreach (ulong part in _elements.Children(hud, MostChildren))
        {
            if (NameOf(part) != ClusterId)
            {
                continue;
            }

            foreach (ulong child in _elements.Children(part, MostChildren))
            {
                if (NameOf(child) == BarId)
                {
                    return child;
                }
            }
        }

        return 0;
    }

    /// <summary>Everything under the bar that holds an item, one or two levels down.</summary>
    private void Find(ulong bar, long nowMs)
    {
        _slots.Clear();
        _searchedAt = nowMs;

        foreach (ulong child in _elements.Children(bar, MostChildren))
        {
            if (_elements.ItemOf(child) != 0)
            {
                _slots.Add((bar, child));
                continue;
            }

            foreach (ulong grandchild in _elements.Children(child, MostChildren))
            {
                if (_elements.ItemOf(grandchild) != 0)
                {
                    _slots.Add((child, grandchild));
                }
            }
        }
    }

    /// <summary>Forgets any remembered slot that is no longer an element, until the next search.</summary>
    /// <remarks>
    /// One read per slot - the same read a search would open with - so a tick on which a slot
    /// dies costs that one read more than a settled one, and not a walk of the bar.
    /// </remarks>
    private void DropTheDead()
    {
        for (int i = _slots.Count - 1; i >= 0; i--)
        {
            if (!_elements.IsUiElement(_slots[i].Element))
            {
                _slots.RemoveAt(i);
            }
        }
    }

    private void Forget()
    {
        _hud = 0;
        _bar = 0;
        _slots.Clear();
        _searchedAt = Never;
    }

    /// <summary>An element's StringId, or empty when it has none or is not an element.</summary>
    private string NameOf(ulong element)
        => _elements.IsUiElement(element)
            ? _reader.ReadStdWString(element + (ulong)_stringId)
            : string.Empty;
}
