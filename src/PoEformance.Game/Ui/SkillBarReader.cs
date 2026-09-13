using System.Numerics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Components;

namespace PoEformance.Game.Ui;

/// <summary>One slot of the skill bar as the HUD draws it, and the skill in it.</summary>
/// <param name="Element">The slot element itself.</param>
/// <param name="Skill">
/// The skill object the slot shows - one of the player's, see <see cref="PlayerSkills"/> - or 0
/// while the slot's pointer matched none of them.
/// </param>
/// <param name="Key">The skill's lasting key, see <see cref="SkillIdentity.Key"/>, or 0.</param>
/// <param name="Where">Where it is on screen, in window pixels.</param>
public readonly record struct SkillSlotOnScreen(ulong Element, ulong Skill, ulong Key, ScreenRect Where);

/// <summary>
/// Finds the skill bar's slots on the HUD, and which skill each one shows.
/// </summary>
/// <remarks>
/// WHAT THE GAME NAMES, from its own hud.ui and confirmed live by the AHK tool's SkillBarReader
/// (2026-06): under the HUD's HUDRight cluster sits an element with StringId "skills_bar", and
/// its slots are its direct children - the visible ones about 64x64 UI units square, in two rows
/// (the mouse-bound slots above, the keyboard ones below). The same-sized HIDDEN children are
/// the other weapon set's duplicates, and a wider child is an indicator panel; both are left
/// out by the two rules that define a slot here: showing, and slot-sized.
///
/// WHICH SKILL IS IN A SLOT IS A POINTER, not an icon: each slot element carries the address of
/// the ActiveSkillDetails object it shows, the same object that sits in the actor's own skill
/// table. The AHK tool's probe found it at +0x2F0 on every visible slot; 0.5.5 has since moved
/// the base's tail by 0x18, so the schema declares the shifted place and this reader SETTLES IT
/// BY CONTENT: a candidate offset is right when the pointer it yields is in the player's table,
/// and the one that matched is reported, so a wrong answer names itself rather than putting a
/// number on the wrong skill.
///
/// CACHED THE WAY THE FLASK BAR IS: the bar by name, re-checked every tick; the slots re-read
/// on a short clock rather than per tick, because the bar does not move and a weapon swap is
/// the only thing that changes which slots show.
/// </remarks>
public sealed class SkillBarReader
{
    /// <summary>The cluster the bar sits in, as the game names it.</summary>
    public const string ClusterId = "HUDRight";

    /// <summary>The bar's own id.</summary>
    public const string BarId = "skills_bar";

    /// <summary>How long a reading of the slots stands.</summary>
    public const long RefreshMs = 250;

    /// <summary>The size a slot is, in UI units, with room either side of the 64 the browser shows.</summary>
    public const float SmallestSlot = 40f;
    public const float LargestSlot = 96f;

    /// <summary>Most children looked at, at each level. A bound on a count read out of memory.</summary>
    private const int MostChildren = 64;

    private const long Never = long.MinValue;

    private readonly IMemoryReader _reader;
    private readonly UiElementReader _elements;
    private readonly int _stringId;
    private readonly int _unscaledSize;
    private readonly int _skillAt;
    private readonly int _skillAtBefore;
    private readonly string _noteAt;
    private readonly string _noteBefore;
    private readonly List<ulong> _candidates = [];

    private ulong _hud;
    private ulong _bar;
    private long _refreshedAt = Never;
    private int _skillsVersion = -1;
    private int _offset;
    private IReadOnlyList<SkillSlotOnScreen> _last = [];

    public SkillBarReader(IMemoryReader reader, OffsetSchema schema, UiElementReader elements)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(elements);
        _reader = reader;
        _elements = elements;

        StructDef ui = schema.Structs["UiElementBase"];
        _stringId = ui.OffsetOf("StringIdPtr");
        _unscaledSize = ui.OffsetOf("UnscaledSize");

        StructDef slot = schema.Structs["SkillBarSlot"];
        _skillAt = slot.OffsetOf("ActiveSkillPtr");
        _skillAtBefore = (int)slot.Constants["ActiveSkillPtrBeforeShift"];
        _noteAt = $"+0x{_skillAt:X}";
        _noteBefore = $"+0x{_skillAtBefore:X}";
    }

    /// <summary>The bar element as last resolved, or 0 - for the readouts.</summary>
    public ulong Element => _bar;

    /// <summary>Where on a slot the skill pointer was found, for the readouts - or that it was not.</summary>
    public string SkillOffsetNote
        => _offset == 0 ? "unmatched" : _offset == _skillAt ? _noteAt : _noteBefore;

    /// <summary>
    /// The bar's slots as they stand. Empty while the bar is not on screen.
    /// </summary>
    /// <param name="hud">The interface element as InterfaceReader resolved it, or 0.</param>
    /// <param name="scale">The viewport to place the slots in.</param>
    /// <param name="nowMs">A monotonic clock, for pacing the reads.</param>
    /// <param name="skills">The player's skill table, which decides what a slot's pointer means.</param>
    public IReadOnlyList<SkillSlotOnScreen> Read(ulong hud, UiScale scale, long nowMs, PlayerSkills skills)
    {
        ArgumentNullException.ThrowIfNull(skills);

        ulong bar = Resolve(hud);
        if (bar == 0 || !_elements.IsVisible(bar))
        {
            _last = [];
            _refreshedAt = Never;
            return _last;
        }

        // A new skill table is a reason to look again before the clock says so: the slots'
        // pointers may only now have something to match.
        if (_refreshedAt != Never && nowMs - _refreshedAt < RefreshMs && skills.Version == _skillsVersion)
        {
            return _last;
        }

        _refreshedAt = nowMs;
        _skillsVersion = skills.Version;

        _candidates.Clear();
        foreach (ulong child in _elements.Children(bar, MostChildren))
        {
            // Its own bit: the bar's whole chain was checked above.
            if (_elements.IsShowingItself(child) && IsSlotSized(child))
            {
                _candidates.Add(child);
            }
        }

        if (_candidates.Count == 0)
        {
            _last = [];
            return _last;
        }

        Dictionary<ulong, Placed> placed = _elements.ReadSiblings(bar, _candidates, scale);

        var slots = new List<SkillSlotOnScreen>(_candidates.Count);
        int matched = 0;
        foreach (ulong slot in _candidates)
        {
            if (!placed.TryGetValue(slot, out Placed box))
            {
                continue;
            }

            var where = new ScreenRect(
                box.Position.X, box.Position.Y, box.Position.X + box.Size.X, box.Position.Y + box.Size.Y);
            if (!where.HasArea)
            {
                continue;
            }

            ulong skill = SkillOf(slot, skills);
            if (skill != 0)
            {
                matched++;
            }

            slots.Add(new SkillSlotOnScreen(slot, skill, skills.KeyOf(skill), where));
        }

        // An offset that matches nothing while the table has skills in it is a lock on the
        // wrong place - a patch, a rebuilt bar - and is given up so the next reading tries both.
        if (matched == 0 && skills.Count > 0)
        {
            _offset = 0;
        }

        _last = slots;
        return _last;
    }

    /// <summary>The skill a slot points at, at the offset that matched - or, until one has, at either.</summary>
    private ulong SkillOf(ulong slot, PlayerSkills skills)
    {
        if (_offset != 0)
        {
            ulong pointer = _reader.ReadPointer(slot + (ulong)_offset);
            return skills.Contains(pointer) ? pointer : 0;
        }

        ulong at = _reader.ReadPointer(slot + (ulong)_skillAt);
        if (skills.Contains(at))
        {
            _offset = _skillAt;
            return at;
        }

        ulong before = _reader.ReadPointer(slot + (ulong)_skillAtBefore);
        if (skills.Contains(before))
        {
            _offset = _skillAtBefore;
            return before;
        }

        return 0;
    }

    /// <summary>Whether an element is the size of a slot, in the interface's own units.</summary>
    private bool IsSlotSized(ulong element)
    {
        Span<float> size = stackalloc float[2];
        return _reader.TryRead(element + (ulong)_unscaledSize, System.Runtime.InteropServices.MemoryMarshal.AsBytes(size))
               && size[0] >= SmallestSlot && size[0] <= LargestSlot
               && size[1] >= SmallestSlot && size[1] <= LargestSlot;
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
    /// <remarks>
    /// The named cluster first, then every other part's children, in case a patch moves the bar
    /// between clusters: the AHK tool keeps the same fallback, and it is one scan, once.
    /// </remarks>
    private ulong FindBar(ulong hud)
    {
        List<ulong> parts = _elements.Children(hud, MostChildren);
        foreach (ulong part in parts)
        {
            if (NameOf(part) == ClusterId && Under(part) is ulong bar and not 0)
            {
                return bar;
            }
        }

        foreach (ulong part in parts)
        {
            if (Under(part) is ulong bar and not 0)
            {
                return bar;
            }
        }

        return 0;
    }

    private ulong Under(ulong part)
    {
        foreach (ulong child in _elements.Children(part, MostChildren))
        {
            if (NameOf(child) == BarId)
            {
                return child;
            }
        }

        return 0;
    }

    private void Forget()
    {
        _hud = 0;
        _bar = 0;
        _refreshedAt = Never;
        _last = [];
    }

    /// <summary>An element's StringId, or empty when it has none or is not an element.</summary>
    private string NameOf(ulong element)
        => _elements.IsUiElement(element)
            ? _reader.ReadStdWString(element + (ulong)_stringId)
            : string.Empty;
}
