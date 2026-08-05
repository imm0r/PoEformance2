using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Game.Components;

/// <summary>
/// Reads what the game calls the marker it puts on its own map for an entity.
/// </summary>
/// <remarks>
/// The single most useful component for finding things, because it is the game answering the
/// question instead of this guessing at it: an entity with a MinimapIcon is one the game
/// itself considers worth marking, and the icon's name is its own category for it -
/// "Waypoint", "QuestObject", "RewardChestExpedition".
///
/// It matters most where a path cannot help. A quest objective is a lever, a brazier, a
/// corpse; nothing in its metadata path says it is what the quest wants, and the icon says so
/// directly. Source: GameHelper2's MinimapIcon component.
///
/// Two hops, and both are guarded: the component points at a row of MinimapIcons.dat, whose
/// first field points at the name. Names are static game data, so they are cached by pointer
/// and each one is read once per session rather than once per entity per tick.
/// </remarks>
public sealed class MinimapIconReader
{
    private readonly IMemoryReader _reader;
    private readonly int _iconRow;
    private readonly int _name;
    private readonly Dictionary<ulong, string> _names = [];

    public MinimapIconReader(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;
        _iconRow = schema.Structs["MinimapIcon"].OffsetOf("IconRowPtr");
        _name = schema.Structs["MinimapIconRow"].OffsetOf("NamePtr");
    }

    /// <summary>The icon's name, or empty when there is none to read.</summary>
    public string Read(ulong component)
    {
        if (!MemoryReaderExtensions.IsPlausiblePointer(component))
        {
            return string.Empty;
        }

        ulong row = _reader.ReadPointer(component + (ulong)_iconRow);
        if (!MemoryReaderExtensions.IsPlausiblePointer(row))
        {
            return string.Empty;
        }

        // Cached by the ROW, not the component: the rows are static game data shared by every
        // entity using that icon, so a map full of the same marker costs one read.
        if (_names.TryGetValue(row, out string? known))
        {
            return known;
        }

        ulong text = _reader.ReadPointer(row + (ulong)_name);
        string name = MemoryReaderExtensions.IsPlausiblePointer(text)
            ? _reader.ReadUnicodeString(text, 64)
            : string.Empty;

        _names[row] = name;
        return name;
    }
}
