using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Items;

namespace PoEformance.Features;

/// <summary>One item, where it sits and everything it is.</summary>
/// <param name="At">Its rectangle in the grid - which is how a stash is drawn as a stash.</param>
public sealed record StashSlot(StashedItem At, InspectedItem Item);

/// <summary>One inventory with its items already read.</summary>
/// <param name="Id">The game's own id for it.</param>
/// <param name="Kind">Backpack, worn, or a stash tab.</param>
/// <param name="Columns">How wide its grid is.</param>
/// <param name="Rows">And how deep.</param>
/// <param name="Items">What is in it, each read down to its stats.</param>
public sealed record StashPage(
    int Id,
    InventoryKind Kind,
    string Called,
    int Columns,
    int Rows,
    IReadOnlyList<StashSlot> Items);

/// <summary>What the inspector found. Immutable, published whole, drawn as-is.</summary>
/// <param name="Pages">Every inventory, in the order the game holds them.</param>
/// <param name="Status">What happened, in words. Empty when there is nothing to say.</param>
public sealed record StashView(IReadOnlyList<StashPage> Pages, string Status)
{
    public static StashView Nothing { get; } = new([], string.Empty);

    /// <summary>How many items across everything.</summary>
    public int Items
    {
        get
        {
            int total = 0;
            foreach (StashPage page in Pages)
            {
                total += page.Items.Count;
            }

            return total;
        }
    }

    /// <summary>How many of the pages are stash tabs.</summary>
    public int Tabs => Pages.Count(page => page.Kind == InventoryKind.Stash);
}

/// <summary>
/// Reads every stash tab and everything in it, when asked.
/// </summary>
/// <remarks>
/// ON DEMAND, and that is the whole design. A full read is every item in every tab taken down
/// to its stats - thousands of entities, each a component walk and a pair of vectors - which is
/// orders of magnitude past anything else in the tool, and it answers a question nobody asks
/// sixty times a second. So it runs when somebody presses a button, on the reader thread, and
/// publishes what it found.
///
/// A TAB HOLDS NOTHING UNTIL IT HAS BEEN OPENED IN GAME - confirmed by the owner, 2026-08. The
/// client asks the server for a tab's contents when it is opened and not before, so an empty tab
/// and one never opened are the same thing from here. Every count this publishes is therefore
/// "what has been opened", and the window says so rather than leaving it to be assumed.
/// </remarks>
public sealed class StashInspector
{
    private readonly IMemoryReader _reader;
    private readonly OffsetSchema _schema;
    private readonly ulong _gameStatesStatic;
    private readonly StashReader _stash;
    private readonly ItemReader _items;
    private readonly int _playerInfo;
    private readonly int _serverData;

    private StashView _view = StashView.Nothing;
    private int _wanted;
    private int _served;
    private int _busy;

    public StashInspector(IMemoryReader reader, OffsetSchema schema, ulong gameStatesStatic, ItemNames? names = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;
        _schema = schema;
        _gameStatesStatic = gameStatesStatic;
        _stash = new StashReader(reader, schema);
        _items = new ItemReader(reader, schema, names);

        // ServerData is the INLINE LocalPlayerStruct's first field, and that struct's base is
        // the ADDRESS of AreaInstance's PlayerInfo rather than the value in it. Getting that
        // wrong reads a plausible pointer to the wrong thing - which is why this copies the
        // world reader's hop rather than inventing one.
        _playerInfo = schema.Structs["AreaInstance"].OffsetOf("PlayerInfo");
        _serverData = schema.Structs["LocalPlayerStruct"].OffsetOf("ServerDataPtr");
    }

    /// <summary>The newest answer. Never blocks, never null, never partially built.</summary>
    public StashView View => Volatile.Read(ref _view);

    /// <summary>Whether a read is in progress, so a window can say "reading..." rather than "empty".</summary>
    public bool Reading => Volatile.Read(ref _busy) != 0;

    /// <summary>
    /// Asks for a read, served on the next tick.
    /// </summary>
    /// <remarks>
    /// A sequence number rather than a flag, like the atlas check: a flag would be re-served
    /// every tick, and this is the most expensive thing the tool can be asked to do.
    /// </remarks>
    public void ReadAgain() => Interlocked.Increment(ref _wanted);

    /// <summary>Serves a requested read. Called on the reader thread.</summary>
    public void Service()
    {
        int wanted = Volatile.Read(ref _wanted);
        if (wanted == _served)
        {
            return;
        }

        _served = wanted;
        Volatile.Write(ref _busy, 1);

        try
        {
            Volatile.Write(ref _view, Build());
        }
        catch (Exception exception)
        {
            // A stale view beats a dead reader thread. Every pointer here belongs to a stash
            // the game can rearrange between two reads, so this is an ordinary event.
            Volatile.Write(ref _view, View with { Status = $"read failed: {exception.Message}" });
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private StashView Build()
    {
        GameChainAddresses chain = GameChain.Resolve(_reader, _schema, _gameStatesStatic);
        if (chain.AreaInstance == 0)
        {
            return StashView.Nothing with { Status = "not in an area" };
        }

        ulong serverData = ServerData(chain);
        if (serverData == 0)
        {
            return StashView.Nothing with { Status = "the server data did not resolve" };
        }

        IReadOnlyList<StashInventory> inventories = _stash.Read(serverData);
        if (inventories.Count == 0)
        {
            return StashView.Nothing with { Status = "no inventories - are you in a hideout with the stash loaded?" };
        }

        var pages = new List<StashPage>(inventories.Count);
        foreach (StashInventory inventory in inventories)
        {
            var read = new List<StashSlot>(inventory.Items.Count);
            foreach (StashedItem item in inventory.Items)
            {
                InspectedItem inspected = _items.Read(item.Entity);
                if (inspected.Path.Length > 0)
                {
                    read.Add(new StashSlot(item, inspected));
                }
            }

            pages.Add(new StashPage(
                inventory.Id, inventory.Kind, inventory.Called,
                inventory.Columns, inventory.Rows, read));
        }

        int tabs = pages.Count(page => page.Kind == InventoryKind.Stash);
        int empty = pages.Count(page => page.Kind == InventoryKind.Stash && page.Items.Count == 0);

        // The empty count is the useful half. An empty tab and one that has never been opened
        // in game read the same here, so naming the number is what stops it being taken for a
        // complete picture of the stash.
        string caveat = empty > 0
            ? $" - {empty} of them empty, which is also what a tab you have not opened in game looks like"
            : " - tabs hold nothing here until they have been opened in game at least once";

        return new StashView(pages, $"{pages.Count} inventories, {tabs} stash tabs{caveat}");
    }

    /// <summary>Where the inventories hang off.</summary>
    private ulong ServerData(GameChainAddresses chain)
    {
        ulong pointer = _reader.ReadPointer(chain.AreaInstance + (ulong)_playerInfo + (ulong)_serverData);
        return MemoryReaderExtensions.IsPlausiblePointer(pointer) ? pointer : 0;
    }
}
