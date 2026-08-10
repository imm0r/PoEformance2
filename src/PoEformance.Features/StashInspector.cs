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
    /// <summary>
    /// How often the league is looked at.
    /// </summary>
    /// <remarks>
    /// It can only change by going out to character selection and back, so this is about not
    /// making the answer wait for somebody to press "read the stash" - it is a handful of
    /// pointer reads and a short string, next to a full read of thousands of entities.
    /// </remarks>
    public static readonly TimeSpan LeagueEvery = TimeSpan.FromSeconds(5);

    private readonly IMemoryReader _reader;
    private readonly OffsetSchema _schema;
    private readonly ulong _gameStatesStatic;
    private readonly StashReader _stash;
    private readonly ItemReader _items;
    private readonly int _playerInfo;
    private readonly int _serverData;

    /// <summary>How much of the struct is swept when the league is not where the schema says.</summary>
    /// <remarks>
    /// Generously either side of where it was. A field that moves usually moves by a wave of
    /// 0x08, but the cost of looking further is one read of a few kilobytes, once - and the
    /// cost of looking too narrowly is another round trip to somebody with the game running.
    /// </remarks>
    public const int SweepFrom = 0x300;

    /// <summary>And to here.</summary>
    public const int SweepTo = 0x3000;

    private StashView _view = StashView.Nothing;
    private string _league = string.Empty;
    private string _leagueNote = string.Empty;
    private long _leagueAt;
    private bool _swept;
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

    /// <summary>
    /// Which league this character is in, or empty until it has been read once.
    /// </summary>
    /// <remarks>
    /// HERE rather than on a reader of its own because it is server data reached by the same
    /// two-hop resolve the inventories are, and that hop is fiddly enough to be worth having
    /// once. What wants it is prices: it is spelled exactly as poe.ninja spells it.
    ///
    /// The last known league SURVIVES leaving the game. Cleared instead, every loading screen
    /// would look like "no league" and throw away the book for the league being played.
    /// </remarks>
    public string League => Volatile.Read(ref _league);

    /// <summary>
    /// Why there is no league, when there is none - and where it looks like it went.
    /// </summary>
    /// <remarks>
    /// Empty while everything is fine. A silent "no league" is indistinguishable from not being
    /// in the game yet, and it costs the whole price feature: nothing can be asked for without
    /// one. So the moment the read comes back empty from a struct that DID resolve, the strings
    /// in that struct are listed - and the league name is obvious among them.
    /// </remarks>
    public string LeagueNote => Volatile.Read(ref _leagueNote);

    /// <summary>Serves a requested read, and keeps the league current. Called on the reader thread.</summary>
    public void Service(long now)
    {
        Leagued(now);

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

    /// <summary>Reads the league, at most every <see cref="LeagueEvery"/>.</summary>
    /// <remarks>
    /// Failures are silent and cost nothing: not being in an area is the ordinary state at
    /// character selection, and a league that cannot be read yet is simply not known yet.
    /// </remarks>
    private void Leagued(long now)
    {
        if (_leagueAt > 0 && now - _leagueAt < (long)LeagueEvery.TotalMilliseconds)
        {
            return;
        }

        _leagueAt = now;

        try
        {
            GameChainAddresses chain = GameChain.Resolve(_reader, _schema, _gameStatesStatic);
            if (chain.AreaInstance == 0)
            {
                return;
            }

            ulong serverData = ServerData(chain);
            if (serverData == 0)
            {
                return;
            }

            if (_stash.League(serverData) is { Length: > 0 } league)
            {
                Volatile.Write(ref _league, league);
                return;
            }

            // Nothing there. If the struct itself resolved, that is not "not in the game yet" -
            // it is the field having moved, which costs every price in the tool and says so
            // nowhere. Swept ONCE, because the answer cannot change while the game runs.
            if (!_swept && _stash.Resolve(serverData) != 0)
            {
                _swept = true;
                Sweep(serverData);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A stale league beats a dead reader thread, and this runs beside a read the game
            // can invalidate between two pointers.
        }
    }

    /// <summary>
    /// Lists the strings in the struct the league should be in, and says where it is not.
    /// </summary>
    /// <remarks>
    /// The note is written for somebody who will paste it back: it names the offset the schema
    /// is using, so a reply that says "it is at +0x2210 now" is one edit to a data file and no
    /// rebuild. And because this runs against live memory, the read is in the recording too -
    /// which is the only way the question can be answered without the game.
    /// </remarks>
    private void Sweep(ulong serverData)
    {
        int was = _schema.Structs["ServerDataOffsets"].OffsetOf("League");

        // BOTH structs, because "which one" is the question this exists to settle - reading the
        // league off the inner one instead of the outer is exactly how it came back empty in
        // the first place, and a sweep of only the struct already believed in cannot say so.
        var lines = new List<string>();
        foreach ((string which, ulong at) in new[]
                 { ("outer", serverData), ("inner", _stash.Resolve(serverData)) })
        {
            if (at == 0 || (which == "inner" && at == serverData))
            {
                continue;
            }

            IReadOnlyList<StashReader.FoundText> found = _stash.StringsIn(at, SweepFrom, SweepTo);
            lines.Add(found.Count == 0
                ? $"{which}: no strings at all"
                : $"{which}: " + string.Join(", ", found.Select(one => $"+0x{one.Offset:X}=\"{one.Text}\"")));
        }

        Volatile.Write(
            ref _leagueNote,
            $"no league at +0x{was:X}. Strings in the server-data structs - "
            + string.Join("  |  ", lines));
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
