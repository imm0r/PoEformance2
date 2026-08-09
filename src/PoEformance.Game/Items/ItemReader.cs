using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Entities;

namespace PoEformance.Game.Items;

/// <summary>Which of an item's lists a mod came from.</summary>
public enum ModSlot
{
    Implicit,
    Explicit,
    Enchant,
    Crafted,
    Other,
}

/// <summary>One mod on an item.</summary>
/// <param name="Id">The game's own id - <c>Strength1</c>. Always readable.</param>
/// <param name="Name">What it is called - "of the Bear". Empty when the table has not heard of it.</param>
/// <param name="Kind">Prefix, suffix or unique. Empty when unknown.</param>
/// <param name="Slot">Which of the item's lists it was in.</param>
/// <param name="Rolls">
/// The numbers rolled for it. WHICH stat each belongs to is not here: the mod's dat row says
/// that, and this holds only the rolls - so a "+Life and +Mana" mod is two numbers in order.
/// </param>
public sealed record ItemMod(string Id, string Name, string Kind, ModSlot Slot, IReadOnlyList<int> Rolls);

/// <summary>One of an item's resolved stats, and what it says.</summary>
/// <param name="Key">The row in the game's stat table - what memory actually holds.</param>
/// <param name="Id">The stat's own name.</param>
/// <param name="Value">How much of it.</param>
/// <param name="Said">The game's wording with the number in it, or the id when there is none.</param>
public sealed record ItemStat(int Key, string Id, int Value, string Said);

/// <summary>Everything worth knowing about one item.</summary>
/// <param name="Entity">Where it is, for anything that wants to read more.</param>
/// <param name="Path">Its metadata path - always readable, and the thing to match on.</param>
/// <param name="Base">What its base type is called.</param>
/// <param name="Unique">
/// What a unique is called - "Astramentis". Empty for everything else, and for a unique the
/// shipped table has not heard of.
/// </param>
/// <param name="Ivi">
/// The ItemVisualIdentity id a unique carries - <c>FourUniquePinnacle1</c>. Empty for everything
/// else. Kept beside the name because it is the ROBUST key: it stays English on a localised
/// client, and it is all there is for a unique nobody has written down yet.
/// </param>
/// <param name="Rarity">0 normal, 1 magic, 2 rare, 3 unique, and up. -1 when it has no mods block.</param>
/// <param name="Identified">Whether it has been identified. Null when the question does not apply.</param>
/// <param name="Stack">How many are in the stack. 0 for anything that does not stack.</param>
/// <param name="StackMax">The most that fit in one, in an ordinary container.</param>
/// <param name="Art">
/// The path of its 2D picture - what the game draws in the grid. The art itself is inside the
/// game's packed archive, so this is the key to it rather than the thing.
/// </param>
/// <param name="Mods">The mods on it.</param>
/// <param name="Stats">
/// What those mods add up to, as the game resolved them. This is the ORIGINAL: not recomputed
/// from the mods, but the game's own answer read off the item.
/// </param>
public sealed record InspectedItem(
    ulong Entity,
    string Path,
    string Base,
    string Unique,
    string Ivi,
    int Rarity,
    bool? Identified,
    int Stack,
    int StackMax,
    string Art,
    IReadOnlyList<ItemMod> Mods,
    IReadOnlyList<ItemStat> Stats)
{
    /// <summary>Nothing readable - what a stale pointer leaves.</summary>
    public static InspectedItem Unreadable { get; } =
        new(0, string.Empty, string.Empty, string.Empty, string.Empty, -1, null, 0, 0, string.Empty, [], []);

    /// <summary>What to call it in a list.</summary>
    /// <remarks>
    /// The unique's own name wins over its base type, because "Astramentis" is what somebody is
    /// looking for and "Stellar Amulet" is what fifty other things are also called.
    /// </remarks>
    public string Called => Unique.Length > 0 ? Unique : Base.Length > 0 ? Base : Path;

    /// <summary>What the rarity number means.</summary>
    public string RarityName => Rarity switch
    {
        0 => "Normal",
        1 => "Magic",
        2 => "Rare",
        3 => "Unique",
        4 => "Quest",
        5 => "Currency",
        _ => "",
    };
}

/// <summary>
/// Reads one item down to its stats.
/// </summary>
/// <remarks>
/// TWO COMPONENTS FOR ONE JOB. An item's mods live on either a <c>Mods</c> component or an
/// <c>ObjectMagicProperties</c> one - flasks and gems carry the second, most gear the first -
/// with the same three fields at different offsets. Which an item has cannot be told from its
/// path, so both are read and whichever decodes better wins. That is the reference's rule and
/// the AHK tool's, arrived at separately.
///
/// THE STATS ARE THE GAME'S OWN ANSWER, not a recomputation. Adding up the mods' rolls would
/// need each mod's dat row to say which stat each roll feeds, which is not available - and it
/// would be a reimplementation of the game's own arithmetic besides. What is read here is the
/// resolved list the game itself keeps.
///
/// Offsets from the AHK tool (imm0r/PoEformance), which is battle-tested against PoE2
/// specifically and carries its drift history.
/// </remarks>
public sealed class ItemReader
{
    /// <summary>Most mods taken from one list. A guard on a count out of memory.</summary>
    public const int MostMods = 64;

    /// <summary>And most stats, which a heavily-modded item genuinely approaches.</summary>
    public const int MostStats = 256;

    /// <summary>How many mod lists an item has, in order.</summary>
    private static readonly ModSlot[] Lists =
        [ModSlot.Implicit, ModSlot.Explicit, ModSlot.Enchant, ModSlot.Crafted, ModSlot.Other];

    private readonly IMemoryReader _reader;
    private readonly EntityReader _entities;
    private readonly ItemNames _names;

    private readonly int _vectorSize;
    private readonly int _modEntrySize;
    private readonly int _modValues;
    private readonly int _modValue0;
    private readonly int _modRow;
    private readonly int _statSize;
    private readonly int _statKey;
    private readonly int _statValue;

    private readonly int _modsRarity;
    private readonly int _modsAll;
    private readonly int _modsStats;
    private readonly int _modsIdentified;

    private readonly int _magicRarity;
    private readonly int _magicAll;
    private readonly int _magicStats;

    private readonly int _art;
    private readonly int _stackCount;
    private readonly int _stackData;
    private readonly int _stackMax;

    private readonly int _uniqueIviRow;
    private readonly int _iviId;

    public ItemReader(IMemoryReader reader, OffsetSchema schema, ItemNames? names = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;
        _entities = new EntityReader(reader, schema);
        _names = names ?? ItemNames.Empty;

        _vectorSize = (int)schema.Structs["StdVector"].Constants["StructSize"];

        StructDef mod = schema.Structs["ModArray"];
        _modEntrySize = (int)mod.Constants["EntrySize"];
        _modValues = mod.OffsetOf("Values");
        _modValue0 = mod.OffsetOf("Value0");
        _modRow = mod.OffsetOf("ModsPtr");

        StructDef stat = schema.Structs["StatPair"];
        _statSize = (int)stat.Constants["EntrySize"];
        _statKey = stat.OffsetOf("Key");
        _statValue = stat.OffsetOf("Value");

        StructDef mods = schema.Structs["Mods"];
        _modsRarity = mods.OffsetOf("Rarity");
        _modsAll = mods.OffsetOf("AllMods");
        _modsStats = mods.OffsetOf("StatsFromMods");
        _modsIdentified = mods.OffsetOf("Identified");

        StructDef magic = schema.Structs["ObjectMagicProperties"];
        _magicRarity = magic.OffsetOf("Rarity");
        _magicAll = magic.OffsetOf("AllMods");
        _magicStats = magic.OffsetOf("StatsFromMods");

        _art = schema.Structs["RenderItemComponent"].OffsetOf("ResourcePath");
        _stackCount = schema.Structs["Stack"].OffsetOf("Count");
        _stackData = schema.Structs["Stack"].OffsetOf("StackSizeDataPtr");
        _stackMax = schema.Structs["StackSizeData"].OffsetOf("MaxStack");

        _uniqueIviRow = schema.Structs["ItemBaseComponent"].OffsetOf("UniqueIviRow");
        _iviId = schema.Structs["ItemVisualIdentityRow"].OffsetOf("IdPtr");
    }

    /// <summary>Reads one item, or <see cref="InspectedItem.Unreadable"/> when it is not there.</summary>
    public InspectedItem Read(ulong entity)
    {
        if (_entities.ReadIdentity(entity) is not { } identity)
        {
            return InspectedItem.Unreadable;
        }

        IReadOnlyDictionary<string, ulong> components =
            _entities.ReadComponents(identity.Address, identity.Details);

        (int rarity, bool? identified, IReadOnlyList<ItemMod> mods, IReadOnlyList<ItemStat> stats) =
            Modded(components);

        (int stack, int stackMax) = Stacked(components);

        string ivi = Ivi(components);

        return new InspectedItem(
            entity,
            identity.Path,
            _names.Base(identity.Path),
            _names.Unique(ivi),
            ivi,
            rarity,
            identified,
            stack,
            stackMax,
            Art(components),
            mods,
            stats);
    }

    /// <summary>
    /// The mods, from whichever of the two components carries them.
    /// </summary>
    /// <remarks>
    /// BOTH are read and the better answer wins, because which one an item has is not knowable
    /// from anything cheaper. "Better" is more mods, then a higher rarity - an item that has
    /// both components usually has one of them empty.
    /// </remarks>
    private (int Rarity, bool? Identified, IReadOnlyList<ItemMod> Mods, IReadOnlyList<ItemStat> Stats) Modded(
        IReadOnlyDictionary<string, ulong> components)
    {
        int rarity = -1;
        bool? identified = null;
        IReadOnlyList<ItemMod> mods = [];
        IReadOnlyList<ItemStat> stats = [];
        int best = -1;

        if (components.TryGetValue("Mods", out ulong plain) && MemoryReaderExtensions.IsPlausiblePointer(plain))
        {
            int theirs = _reader.Read<int>(plain + (ulong)_modsRarity);
            if (theirs is >= 0 and <= 5)
            {
                IReadOnlyList<ItemMod> found = ModsIn(plain + (ulong)_modsAll);
                int score = (found.Count * 10) + theirs;
                if (score > best)
                {
                    best = score;
                    rarity = theirs;
                    mods = found;
                    stats = StatsIn(plain + (ulong)_modsStats);
                    identified = _reader.Read<byte>(plain + (ulong)_modsIdentified) != 0;
                }
            }
        }

        if (components.TryGetValue("ObjectMagicProperties", out ulong magic)
            && MemoryReaderExtensions.IsPlausiblePointer(magic))
        {
            int theirs = _reader.Read<int>(magic + (ulong)_magicRarity);
            if (theirs is >= 0 and <= 5)
            {
                IReadOnlyList<ItemMod> found = ModsIn(magic + (ulong)_magicAll);
                int score = (found.Count * 10) + theirs;
                if (score > best)
                {
                    rarity = theirs;
                    mods = found;
                    stats = StatsIn(magic + (ulong)_magicStats);

                    // Left unknown rather than guessed: this component has no such field, and
                    // the things that carry it - flasks, gems, currency - have no unidentified
                    // state to report.
                    identified = null;
                }
            }
        }

        return (rarity, identified, mods, stats);
    }

    /// <summary>Every mod, across the five lists that sit one after another.</summary>
    private IReadOnlyList<ItemMod> ModsIn(ulong lists)
    {
        var found = new List<ItemMod>();
        for (int i = 0; i < Lists.Length; i++)
        {
            OneList(lists + (ulong)(i * _vectorSize), Lists[i], found);
        }

        return found;
    }

    /// <summary>One of them.</summary>
    private void OneList(ulong vector, ModSlot slot, List<ItemMod> into)
    {
        ulong first = _reader.ReadPointer(vector);
        ulong last = _reader.ReadPointer(vector + 8);
        if (!MemoryReaderExtensions.IsPlausiblePointer(first) || last <= first)
        {
            return;
        }

        ulong bytes = last - first;
        if (bytes % (ulong)_modEntrySize != 0 || bytes > (ulong)_modEntrySize * MostMods)
        {
            return;
        }

        for (ulong i = 0; i < bytes / (ulong)_modEntrySize; i++)
        {
            ulong at = first + (i * (ulong)_modEntrySize);
            ulong row = _reader.ReadPointer(at + (ulong)_modRow);
            if (!MemoryReaderExtensions.IsPlausiblePointer(row))
            {
                continue;
            }

            // The dat row's first field is a pointer to the mod's id string.
            ulong text = _reader.ReadPointer(row);
            string id = MemoryReaderExtensions.IsPlausiblePointer(text)
                ? _reader.ReadUnicodeString(text, 128)
                : string.Empty;

            if (id.Length == 0)
            {
                continue;
            }

            ModMeaning meaning = _names.Mod(id);
            into.Add(new ItemMod(id, meaning.Name, meaning.Kind, slot, Rolls(at)));
        }
    }

    /// <summary>The numbers rolled for one mod.</summary>
    /// <remarks>
    /// From the mod's own vector, falling back to the single value beside it - which is what
    /// the entries with an empty vector carry.
    /// </remarks>
    private IReadOnlyList<int> Rolls(ulong mod)
    {
        ulong first = _reader.ReadPointer(mod + (ulong)_modValues);
        ulong last = _reader.ReadPointer(mod + (ulong)_modValues + 8);

        if (MemoryReaderExtensions.IsPlausiblePointer(first) && last > first && (last - first) % 4 == 0)
        {
            long count = Math.Min((long)(last - first) / 4, 16);
            var rolls = new List<int>((int)count);
            for (long i = 0; i < count; i++)
            {
                rolls.Add(_reader.Read<int>(first + (ulong)(i * 4)));
            }

            return rolls;
        }

        int single = _reader.Read<int>(mod + (ulong)_modValue0);
        return single != 0 ? [single] : [];
    }

    /// <summary>The item's resolved stats - the game's own answer, not a recomputation.</summary>
    private IReadOnlyList<ItemStat> StatsIn(ulong vector)
    {
        var found = new List<ItemStat>();

        ulong first = _reader.ReadPointer(vector);
        ulong last = _reader.ReadPointer(vector + 8);
        if (!MemoryReaderExtensions.IsPlausiblePointer(first) || last <= first)
        {
            return found;
        }

        ulong bytes = last - first;
        if (bytes % (ulong)_statSize != 0 || bytes > (ulong)_statSize * MostStats)
        {
            return found;
        }

        for (ulong i = 0; i < bytes / (ulong)_statSize; i++)
        {
            ulong at = first + (i * (ulong)_statSize);
            int key = _reader.Read<int>(at + (ulong)_statKey);
            int value = _reader.Read<int>(at + (ulong)_statValue);

            StatMeaning meaning = _names.Stat(key);
            found.Add(new ItemStat(key, meaning.Id, value, meaning.Say(value)));
        }

        return found;
    }

    /// <summary>Where the item's own picture lives.</summary>
    /// <remarks>
    /// Empty for anything without a RenderItem component, which is not a failure - the caller
    /// draws the name instead, which is what it would have done anyway before the picture
    /// arrived.
    /// </remarks>
    private string Art(IReadOnlyDictionary<string, ulong> components)
    {
        if (!components.TryGetValue("RenderItem", out ulong render)
            || !MemoryReaderExtensions.IsPlausiblePointer(render))
        {
            return string.Empty;
        }

        string path = _reader.ReadStdWString(render + (ulong)_art, 260);
        return path.Length is > 0 and <= 260 ? path : string.Empty;
    }

    /// <summary>
    /// The ItemVisualIdentity id a unique carries, or empty for anything else.
    /// </summary>
    /// <remarks>
    /// Base + 0x30 is the IVI dat row, and its first field points at the id as a wide string.
    /// The row is null for everything that is not unique, so an empty answer here is the normal
    /// case rather than a failure.
    ///
    /// WHY NOT THE METADATA PATH: uniques share base types. Morior Invictus and Tabula Rasa are
    /// the same path, and a path lookup has to pick one of them - which is wrong half the time,
    /// silently, on the two items in the game most worth telling apart.
    ///
    /// Chain and offsets from the AHK tool, live-verified there 2026-06-24.
    /// </remarks>
    private string Ivi(IReadOnlyDictionary<string, ulong> components)
    {
        if (!components.TryGetValue("Base", out ulong itemBase)
            || !MemoryReaderExtensions.IsPlausiblePointer(itemBase))
        {
            return string.Empty;
        }

        ulong row = _reader.ReadPointer(itemBase + (ulong)_uniqueIviRow);
        if (!MemoryReaderExtensions.IsPlausiblePointer(row))
        {
            return string.Empty;
        }

        ulong text = _reader.ReadPointer(row + (ulong)_iviId);
        if (!MemoryReaderExtensions.IsPlausiblePointer(text))
        {
            return string.Empty;
        }

        // Read SHORT. An id is one word; a long read past a drifted offset comes back as
        // something that looks like an id nobody has heard of rather than as nothing.
        string id = _reader.ReadUnicodeString(text, 128);
        return id.Length is > 0 and <= 128 ? id : string.Empty;
    }

    /// <summary>How many are in the stack, and how many fit.</summary>
    /// <remarks>
    /// Both zero for anything that does not stack, which is most things - the component is
    /// simply absent, and that is not a failure.
    /// </remarks>
    private (int Stack, int Max) Stacked(IReadOnlyDictionary<string, ulong> components)
    {
        if (!components.TryGetValue("Stack", out ulong stack) || !MemoryReaderExtensions.IsPlausiblePointer(stack))
        {
            return (0, 0);
        }

        int count = _reader.Read<int>(stack + (ulong)_stackCount);
        ulong data = _reader.ReadPointer(stack + (ulong)_stackData);

        int max = MemoryReaderExtensions.IsPlausiblePointer(data)
            ? _reader.Read<int>(data + (ulong)_stackMax)
            : 0;

        return (count, max);
    }
}
