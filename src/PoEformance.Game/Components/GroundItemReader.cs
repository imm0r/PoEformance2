using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Entities;

namespace PoEformance.Game.Components;

/// <summary>How good a ground drop is, in the game's own rarity numbering.</summary>
/// <remarks>
/// Currency is 5 rather than a rarity at all, matching the reference: currency, fragments
/// and essences carry NO rarity component, so they are identified by path. Reading a rarity
/// of -1 off them and calling it Normal is how a filter set to "magic and above" silently
/// hides every Divine Orb on the floor.
/// </remarks>
public enum ItemRarity
{
    Unknown = -1,
    Normal = 0,
    Magic = 1,
    Rare = 2,
    Unique = 3,
    Currency = 5,
}

/// <summary>
/// Resolves what a ground drop actually IS, through the wrapper entity it arrives as.
/// </summary>
/// <remarks>
/// A drop is not one entity but two. What sits in the entity map is a WRAPPER -
/// <c>Metadata/MiscellaneousObjects/WorldItem</c> - with no rarity, no name and no art of
/// its own; the real item hangs off its WorldItem component at +0x28. Anything that reads
/// the wrapper directly gets "rarity 0" for every drop in the game, which reads as a working
/// filter that lets everything through.
///
/// The result is cached per wrapper: a drop's rarity cannot change while it lies there, and
/// resolving it costs a component lookup plus a full read of the inner entity - far too much
/// to repeat for every item on the floor, every frame.
/// </remarks>
public sealed class GroundItemReader
{
    /// <summary>Drop a cache entry when its item has not been seen for this long.</summary>
    private const int ForgetMs = 30_000;

    private readonly IMemoryReader _reader;
    private readonly EntityReader _entities;
    private readonly int _innerItem;
    private readonly int _modsRarity;

    private readonly Dictionary<ulong, (ItemRarity Rarity, long Seen)> _cache = [];
    private long _lastPrune;

    public GroundItemReader(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;
        _entities = new EntityReader(reader, schema);
        _innerItem = schema.Structs["WorldItemComponent"].OffsetOf("InnerItem");
        _modsRarity = schema.Structs["Mods"].OffsetOf("Rarity");
    }

    /// <summary>The rarity of the item inside a ground-drop wrapper.</summary>
    /// <remarks>
    /// An unresolved drop is NOT cached. A freshly dropped item can be in the map a frame
    /// before its inner entity is readable, and caching that miss would leave it marked
    /// Unknown for as long as it lies there.
    /// </remarks>
    public ItemRarity RarityOf(Entity wrapper, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(wrapper);
        Prune(nowMs);

        if (_cache.TryGetValue(wrapper.Address, out (ItemRarity Rarity, long Seen) cached))
        {
            _cache[wrapper.Address] = (cached.Rarity, nowMs);
            return cached.Rarity;
        }

        ItemRarity rarity = Resolve(wrapper);
        if (rarity != ItemRarity.Unknown)
        {
            _cache[wrapper.Address] = (rarity, nowMs);
        }

        return rarity;
    }

    private ItemRarity Resolve(Entity wrapper)
    {
        ulong component = wrapper.Component("WorldItem");
        if (component == 0)
        {
            return ItemRarity.Unknown;
        }

        ulong innerAddress = _reader.ReadPointer(component + (ulong)_innerItem);
        if (innerAddress == 0)
        {
            return ItemRarity.Unknown;
        }

        Entity? item = _entities.Read(innerAddress);
        if (item is null || item.Path.Length == 0)
        {
            return ItemRarity.Unknown;
        }

        // Path FIRST, rarity second. Currency, fragments and essences have no rarity
        // component at all, so asking for one gets nothing and they would fall through as
        // Normal - the single most valuable class of drop, filtered out as junk.
        if (item.Path.Contains("/Currency/", StringComparison.OrdinalIgnoreCase))
        {
            return ItemRarity.Currency;
        }

        ulong mods = item.Component("Mods");
        if (mods == 0 || !_reader.TryRead(mods + (ulong)_modsRarity, out int rarity))
        {
            return ItemRarity.Unknown;
        }

        return rarity switch
        {
            0 => ItemRarity.Normal,
            1 => ItemRarity.Magic,
            2 => ItemRarity.Rare,
            >= 3 => ItemRarity.Unique,
            _ => ItemRarity.Unknown,
        };
    }

    /// <summary>Forgets drops that have been picked up or left behind.</summary>
    private void Prune(long nowMs)
    {
        if (nowMs - _lastPrune < 5000)
        {
            return;
        }

        _lastPrune = nowMs;
        foreach ((ulong address, (ItemRarity _, long seen)) in _cache)
        {
            if (nowMs - seen > ForgetMs)
            {
                _cache.Remove(address);
            }
        }
    }
}
