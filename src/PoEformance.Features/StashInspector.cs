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

/// <summary>
/// Which slots of one inventory held currency, and the arrangement that was true of.
/// </summary>
/// <param name="Print">
/// A fingerprint of the whole inventory - every item's entity and its rectangle. Cheap because
/// all of it is already in hand: reading the inventory produced these, so checking them costs no
/// further look at the game's memory, which is the entire point.
/// </param>
/// <param name="Currency">
/// The slots that came back currency last time this arrangement was seen. Their CONTENTS are
/// deliberately not kept - see <c>StashInspector._known</c> for why a cached stack size would be
/// wrong exactly when it mattered.
/// </param>
public sealed record PurseMemo(ulong Print, IReadOnlyList<StashedItem> Currency);

/// <summary>
/// The currency a character is carrying, and how stale the stash half of it is.
/// </summary>
/// <remarks>
/// TWO HALVES WITH DIFFERENT LIFETIMES, and that is forced by the game rather than chosen. The
/// backpack hangs off the player and is readable wherever they are; the stash tabs only hold
/// anything when the client has a stash loaded, which means standing near one.
///
/// AND THE TABS DO NOT DISAPPEAR WHEN THEY ARE UNLOADED - they are still listed, just empty.
/// That was measured rather than assumed, and assuming otherwise is what the first version of
/// this got wrong: see <see cref="StashInspector.CanSeeAStash"/>. It means a purse read cannot
/// tell "the tabs are empty" from "the tabs are not loaded" by looking at the tabs.
///
/// The stash half is therefore REMEMBERED, replaced only where a stash can exist at all, and
/// stamped with when it was last actually seen - so a readout can say "plus what the stash held
/// twenty minutes ago" instead of implying it is looking at it now.
/// </remarks>
/// <param name="Pages">
/// The carried currency and the last-seen stashed currency together, which is what wants
/// valuing. Only currency is in here - see <see cref="PoEformance.Game.Items.CurrencyPaths"/> -
/// and worn gear never is.
/// </param>
/// <param name="StashSeenAt">
/// Unix milliseconds of when the stash tabs were last actually read, or 0 when they have not
/// been seen at all since the tool started.
/// </param>
/// <param name="Carried">How many of the pages are the player's own backpack.</param>
/// <param name="Status">What happened, in words. Empty when there is nothing to say.</param>
public sealed record PurseView(
    IReadOnlyList<StashPage> Pages,
    long StashSeenAt,
    int Carried,
    string Status)
{
    public static PurseView Nothing { get; } = new([], 0, 0, string.Empty);

    /// <summary>Whether the stash half has ever been seen.</summary>
    public bool SawStash => StashSeenAt > 0;
}

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

    /// <summary>
    /// How often the currency is re-counted.
    /// </summary>
    /// <remarks>
    /// UNLIKE THE FULL READ, THIS ONE RUNS BY ITSELF, and it can because it is a different order
    /// of work. A full read takes every item in every tab down to its mods and resolved stats; a
    /// purse read goes that far only on the handful that are currency.
    ///
    /// AND IT COSTS ALMOST NOTHING WHILE NOTHING MOVES. It used to ask every item in every
    /// inventory for its metadata path on every tick - thousands of reads to rediscover that a
    /// stash of gear is still a stash of gear. Now that question is asked once per ARRANGEMENT
    /// (see <c>_known</c>), so a tab nobody has touched costs one fingerprint over what the
    /// inventory read already returned, and a backpack costs a real read only when something in
    /// it actually moved.
    ///
    /// Five seconds because what it feeds is a graph whose finest resolution is thirty (see
    /// <see cref="WealthHistory.MinGapMs"/>), and a readout that lags a pickup by half a minute
    /// reads as broken even when the record it is writing is perfect.
    /// </remarks>
    public static readonly TimeSpan PurseEvery = TimeSpan.FromSeconds(5);

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

    private PurseView _purse = PurseView.Nothing;
    private long _purseAt;
    private IReadOnlyList<StashPage> _stashed = [];
    private long _stashedAt;
    private bool _watchPurse;
    private bool _canSeeAStash;

    /// <summary>
    /// WHICH SLOTS OF EACH INVENTORY HELD CURRENCY, and the arrangement that was true of.
    /// </summary>
    /// <remarks>
    /// The saving this exists for: a count used to pay ONE IDENTITY READ PER ITEM across every
    /// inventory, every five seconds - thousands of reads a tick for a stash of gear, to rediscover
    /// each time that a helmet is still not money. An inventory whose items have not moved cannot
    /// have changed which of them are currency, so the answer is remembered per inventory and the
    /// reads are skipped entirely while it stands.
    ///
    /// WHAT IS NOT CACHED IS WHAT THE CURRENCY IS WORTH, and that distinction is the whole
    /// correctness of this. A stack GROWS WITHOUT MOVING - dropping ten Chaos onto a stack of five
    /// changes neither the entity nor the slot - so the count of every remembered currency slot is
    /// still read in full on every tick. Caching that too would freeze a purse the moment its owner
    /// stopped rearranging it, which is precisely when they are filling it.
    /// </remarks>
    private readonly Dictionary<int, PurseMemo> _known = [];

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

    /// <summary>The currency being carried, as of the last purse read. Never blocks, never null.</summary>
    public PurseView Purse => Volatile.Read(ref _purse);

    /// <summary>
    /// Whether the purse is counted at all.
    /// </summary>
    /// <remarks>
    /// OFF UNTIL SOMETHING WANTS IT. It is cheap next to a full read and it is not free: it walks
    /// every inventory the game holds, every five seconds, forever. Nobody who has not opened the
    /// wealth tracker should pay for it.
    /// </remarks>
    public bool WatchPurse
    {
        get => Volatile.Read(ref _watchPurse);
        set => Volatile.Write(ref _watchPurse, value);
    }

    /// <summary>
    /// Whether the player is somewhere a stash can exist at all - a town or a hideout.
    /// </summary>
    /// <remarks>
    /// SET FROM OUTSIDE because the area is already read every tick by the world reader, and a
    /// second read of it here would be the same answer bought twice.
    ///
    /// IT IS LOAD-BEARING, not a nicety, and it is here because the first version guessed and
    /// was wrong. That version replaced the remembered stash contents whenever the game listed
    /// any stash inventory at all, on the assumption that in a map the tabs are simply absent
    /// from the list. THE RECORDED DATA SAYS OTHERWISE: a purse of 74 stacks worth 1.28M Exalted
    /// dropped to 3 stacks worth 15 the moment the player left the hideout, and climbed back the
    /// moment they returned - over and over. The tabs ARE listed in a map. They are just empty.
    ///
    /// So "the game listed a tab" cannot mean "I have seen the stash". Being where a stash can
    /// be is a fact about the world rather than about a pointer, which is why the test moved
    /// here. An empty currency tab in a hideout is still a real reading of zero and still
    /// replaces what was remembered - that case was right before and stays right.
    /// </remarks>
    public bool CanSeeAStash
    {
        get => Volatile.Read(ref _canSeeAStash);
        set => Volatile.Write(ref _canSeeAStash, value);
    }

    /// <summary>
    /// Whether a count may replace what the stash tabs were last seen to hold.
    /// </summary>
    /// <remarks>
    /// Named and public so the rule can be argued with in a test rather than only in a hideout.
    /// BOTH operands are needed and the first is the one that was missing: see
    /// <see cref="CanSeeAStash"/> for the recorded purse that proved it.
    /// </remarks>
    public static bool ReplacesTheStash(bool canSeeAStash, bool sawStashInventory)
        => canSeeAStash && sawStashInventory;

    /// <summary>Serves a requested read, and keeps the league current. Called on the reader thread.</summary>
    public void Service(long now)
    {
        Leagued(now);
        Pursed(now);

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

    /// <summary>Re-counts the currency, at most every <see cref="PurseEvery"/>.</summary>
    /// <remarks>
    /// Failures are silent and leave the last count standing, like the league read beside it. Not
    /// being in an area is the ordinary state at character selection, and a purse that cannot be
    /// read this tick is not a purse that emptied.
    /// </remarks>
    private void Pursed(long now)
    {
        if (!WatchPurse || (_purseAt > 0 && now - _purseAt < (long)PurseEvery.TotalMilliseconds))
        {
            return;
        }

        _purseAt = now;

        try
        {
            Volatile.Write(ref _purse, Count());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Volatile.Write(ref _purse, Purse with { Status = $"count failed: {exception.Message}" });
        }
    }

    /// <summary>
    /// Counts the currency in the backpack and, when they are loaded, the stash tabs.
    /// </summary>
    /// <remarks>
    /// THE PATH IS ASKED FOR FIRST AND USUALLY LAST. Everything in every inventory costs one
    /// identity read; only what comes back currency costs the full item walk. That ratio is what
    /// makes this affordable on a timer - a stash of gear is thousands of identity reads and
    /// nothing else.
    ///
    /// A WALL-CLOCK STAMP, not a tick count, because what consumes it writes a record that spans
    /// sessions - see <see cref="WealthPoint"/>. Taken once per count rather than per item.
    /// </remarks>
    private PurseView Count()
    {
        GameChainAddresses chain = GameChain.Resolve(_reader, _schema, _gameStatesStatic);
        if (chain.AreaInstance == 0)
        {
            return Purse with { Status = "not in an area" };
        }

        ulong serverData = ServerData(chain);
        if (serverData == 0)
        {
            return Purse with { Status = "the server data did not resolve" };
        }

        IReadOnlyList<StashInventory> inventories = _stash.Read(serverData);
        if (inventories.Count == 0)
        {
            return Purse with { Status = "no inventories" };
        }

        var carried = new List<StashPage>();
        var stashed = new List<StashPage>();
        var sawStash = false;

        foreach (StashInventory inventory in inventories)
        {
            // Worn gear is never money, and skipping it here saves an identity read per slot on
            // every count rather than filtering it out after paying for it.
            if (inventory.Kind == InventoryKind.Equipped)
            {
                continue;
            }

            if (inventory.Kind == InventoryKind.Stash)
            {
                sawStash = true;
            }

            // WHICH SLOTS ARE MONEY, asked once per arrangement rather than once per tick. See
            // _known: an inventory whose items have not moved cannot have changed which of them
            // are currency, and the identity read that answers it is the expensive part.
            ulong print = Print(inventory);
            IReadOnlyList<StashedItem> money;
            if (_known.TryGetValue(inventory.Id, out PurseMemo? memo) && memo.Print == print)
            {
                money = memo.Currency;
            }
            else
            {
                var sifted = new List<StashedItem>();
                foreach (StashedItem item in inventory.Items)
                {
                    if (CurrencyPaths.IsCurrency(_items.PathOf(item.Entity)))
                    {
                        sifted.Add(item);
                    }
                }

                money = sifted;
                _known[inventory.Id] = new PurseMemo(print, sifted);
            }

            // AND WHAT THEY HOLD, every time. A stack grows without moving, so this is the read
            // that must not be cached - it is the one that sees a pickup at all.
            var found = new List<StashSlot>();
            foreach (StashedItem item in money)
            {
                InspectedItem inspected = _items.Read(item.Entity);
                if (inspected.Path.Length > 0)
                {
                    found.Add(new StashSlot(item, inspected));
                }
            }

            var page = new StashPage(
                inventory.Id, inventory.Kind, inventory.Called,
                inventory.Columns, inventory.Rows, found);

            (inventory.Kind == InventoryKind.Backpack ? carried : stashed).Add(page);
        }

        // WHERE A STASH CAN BE, and only then. The game lists the tab inventories in a map as
        // well - empty - so "a tab came back" says nothing about whether it was read. See
        // CanSeeAStash for the measurements that settled this.
        //
        // Within a town or hideout the old rule still holds: seen AT ALL rather than "held
        // currency", because a currency tab somebody has just emptied is a real reading of zero
        // and has to replace what was remembered.
        if (ReplacesTheStash(CanSeeAStash, sawStash))
        {
            _stashed = stashed;
            _stashedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        var pages = new List<StashPage>(carried.Count + _stashed.Count);
        pages.AddRange(carried);
        pages.AddRange(_stashed);

        return new PurseView(
            pages,
            _stashedAt,
            carried.Count,
            ReplacesTheStash(CanSeeAStash, sawStash)
                ? string.Empty
                : "away from the stash - showing what the tabs last held");
    }

    /// <summary>
    /// A fingerprint of one inventory's arrangement, from what reading it already produced.
    /// </summary>
    /// <remarks>
    /// FNV-1a over each item's entity AND its rectangle. The entity alone would do almost as well,
    /// but position comes free and closes the one way an entity address could lie: the game reuses
    /// freed addresses, so an item replaced by another that happened to land on the same address
    /// would otherwise read as unchanged. Same address AND same rectangle AND same neighbours is
    /// not a coincidence that happens.
    ///
    /// COUNTED IN, because an inventory losing its last item would otherwise fingerprint the same
    /// as one that never had any - both being the empty product.
    /// </remarks>
    public static ulong Print(StashInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        const ulong Seed = 14695981039346656037;
        const ulong Wave = 1099511628211;

        ulong print = Seed;
        print = (print ^ (uint)inventory.Items.Count) * Wave;

        foreach (StashedItem item in inventory.Items)
        {
            print = (print ^ item.Entity) * Wave;
            print = (print ^ (uint)item.Left) * Wave;
            print = (print ^ (uint)item.Top) * Wave;
            print = (print ^ (uint)item.Width) * Wave;
            print = (print ^ (uint)item.Height) * Wave;
        }

        return print;
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
