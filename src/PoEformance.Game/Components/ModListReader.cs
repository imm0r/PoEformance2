using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Game.Components;

/// <summary>
/// Reads the ids of the mods on an ObjectMagicProperties component.
/// </summary>
/// <remarks>
/// One walk for everything that carries mods - a relic, a monster, a strongbox - because the
/// layout is the same on all of them: five vectors in a row (implicit, explicit, enchant and
/// two league ones), each entry pointing at its Mods.dat row, whose first field is the mod's
/// id string. Written first for Expedition relics, and kept there until a monster needed it:
/// an essence-imprisoned monster is a rare with a full health bar and a targetable byte, and
/// what tells it from a free one, if anything does, is a mod the game puts on it.
///
/// Ids, not names. A mod's id is what the game keys it by and never renumbers, and most of the
/// mods on a monster have no display name at all - the one above is called nothing. Whoever
/// shows these looks the name up if they want one.
/// </remarks>
public sealed class ModListReader
{
    /// <summary>The vectors a component carries, in a row.</summary>
    private const int MostModLists = 5;

    /// <summary>Entries one list may hold before it is disbelieved.</summary>
    private const int MostModsPerList = 64;

    private readonly IMemoryReader _reader;
    private readonly int _allMods;
    private readonly int _vectorSize;
    private readonly int _modEntrySize;
    private readonly int _modRow;

    public ModListReader(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);

        _reader = reader;
        _allMods = schema.Structs["ObjectMagicProperties"].OffsetOf("AllMods");
        _vectorSize = (int)schema.Structs["StdVector"].Constants["StructSize"];
        StructDef mod = schema.Structs["ModArray"];
        _modEntrySize = (int)mod.Constants["EntrySize"];
        _modRow = mod.OffsetOf("ModsPtr");
    }

    /// <summary>The ids of the mods on a component, in list order. Empty for no component.</summary>
    public IReadOnlyList<string> Ids(ulong magic)
    {
        if (magic == 0)
        {
            return [];
        }

        var ids = new List<string>();
        for (int list = 0; list < MostModLists; list++)
        {
            ulong vector = magic + (ulong)_allMods + (ulong)(list * _vectorSize);
            ulong first = _reader.ReadPointer(vector);
            ulong last = _reader.ReadPointer(vector + 8);
            if (!MemoryReaderExtensions.IsPlausiblePointer(first) || last <= first)
            {
                continue;
            }

            ulong bytes = last - first;
            if (bytes % (ulong)_modEntrySize != 0 || bytes > (ulong)(_modEntrySize * MostModsPerList))
            {
                continue;
            }

            for (ulong i = 0; i < bytes / (ulong)_modEntrySize; i++)
            {
                ulong row = _reader.ReadPointer(first + (i * (ulong)_modEntrySize) + (ulong)_modRow);
                if (!MemoryReaderExtensions.IsPlausiblePointer(row))
                {
                    continue;
                }

                // The dat row's first field is a pointer to the mod's id string.
                ulong text = _reader.ReadPointer(row);
                string id = MemoryReaderExtensions.IsPlausiblePointer(text) ? _reader.ReadUnicodeString(text, 128) : string.Empty;
                if (id.Length > 0)
                {
                    ids.Add(id);
                }
            }
        }

        return ids;
    }
}
