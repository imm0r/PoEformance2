using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.Items;

namespace PoEformance.Overlay;

/// <summary>
/// Everything in every stash tab, down to each item's stats.
/// </summary>
/// <remarks>
/// A LIST, not an overlay, because the question is one nobody asks while fighting: what have I
/// got, and where. The game answers it one tab at a time and only for the tab in front of you;
/// this answers it for all of them at once, searchable.
///
/// THE STATS ARE THE ITEM'S OWN, as the game resolved them - so what a row says is what the
/// item says, not a recomputation that could drift from it. Unworded stats keep their id, which
/// is what makes this useful for reverse-engineering as well as for sorting through loot.
///
/// IT CANNOT SHOW A TAB NOBODY HAS OPENED. The client asks the server for a tab's contents when
/// it is opened and not before, so an unopened tab reads as an empty one - which is why nothing
/// here calls a total "your stash", and why an empty page says both things it might be.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class StashWindow
{
    /// <summary>How many items are listed at once. The rest are counted, not drawn.</summary>
    public const int MostRows = 400;

    /// <summary>
    /// What an item has to be worth before the grid says so.
    /// </summary>
    /// <remarks>
    /// A stash is mostly things worth a tenth of an Exalted, and a number on every one of two
    /// hundred cells is a wall of noise that hides the three that matter. The list shows every
    /// price it knows; the grid shows the ones worth walking over to.
    /// </remarks>
    public const double WorthSaying = 1.0;

    private static readonly Vector4 DimText = OverlayTheme.Quiet;
    private static readonly Vector4 GoodText = new(0.55f, 0.9f, 0.65f, 1f);
    private static readonly Vector4 WarnText = new(1f, 0.6f, 0.35f, 1f);
    private static readonly Vector4 MoneyText = new(1f, 0.83f, 0.42f, 1f);

    /// <summary>What each rarity looks like, in the game's own colours.</summary>
    private static readonly Vector4[] ByRarity =
    [
        new(0.86f, 0.86f, 0.86f, 1f),   // normal
        new(0.53f, 0.53f, 1f, 1f),      // magic
        new(1f, 1f, 0.46f, 1f),         // rare
        new(0.68f, 0.4f, 0.15f, 1f),    // unique
        new(0.29f, 0.78f, 0.29f, 1f),   // quest
        new(0.67f, 0.55f, 0.4f, 1f),    // currency
    ];

    private readonly StashInspector _inspector;
    private readonly ItemArtStore _art;
    private readonly PriceStore _prices;
    private readonly TradePrices _trade;
    private readonly Action _signIn;
    private readonly Func<string, IntPtr> _picture;

    private string _search = string.Empty;
    private int _page = -1;
    private float _cell = 44f;
    private readonly HashSet<ulong> _open = [];
    private ulong _hovered;

    // What the last read came to, per page and in total. Kept rather than recomputed because
    // it is a lookup per item and a frame draws the tab list for the WHOLE stash - which would
    // be the entire book walked sixty times a second to label a dozen rows.
    private StashView? _tallied;
    private PriceBook? _talliedWith;
    private int _talliedAfter = -1;
    private Valued[] _perPage = [];
    private Valued _total;

    /// <param name="art">Where each item's own picture comes from.</param>
    /// <param name="prices">What things are worth. Off until somebody switches it on here.</param>
    /// <param name="trade">
    /// What uniques the book could not price are being listed for. Off separately, because it
    /// wants a signed-in browser and poe.ninja wants nothing.
    /// </param>
    /// <param name="signIn">Opens the trade window, which lives in a layer this one cannot see.</param>
    /// <param name="picture">
    /// Turns a file on disk into something drawable. Handed in because uploading a texture
    /// belongs to whoever owns the renderer, not to a window.
    /// </param>
    public StashWindow(
        StashInspector inspector,
        ItemArtStore art,
        PriceStore prices,
        TradePrices trade,
        Action signIn,
        Func<string, IntPtr> picture)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        ArgumentNullException.ThrowIfNull(art);
        ArgumentNullException.ThrowIfNull(prices);
        ArgumentNullException.ThrowIfNull(trade);
        ArgumentNullException.ThrowIfNull(signIn);
        ArgumentNullException.ThrowIfNull(picture);
        _inspector = inspector;
        _art = art;
        _prices = prices;
        _trade = trade;
        _signIn = signIn;
        _picture = picture;
    }

    /// <summary>Draws the tab's content.</summary>
    public void DrawTab() => Draw();

    private void Draw()
    {
        StashView view = _inspector.View;

        // ONE book for the whole frame. Read per item instead, a refresh landing mid-draw
        // would show half a tab priced by the old economy and half by the new one.
        PriceBook book = _prices.Book;
        Tally(view, book);

        if (ImGui.Button("read the stash"))
        {
            _inspector.ReadAgain();
        }

        ImGui.SameLine();

        if (_inspector.Reading)
        {
            // Said, because a full read of a big stash takes long enough to look like nothing
            // happening - and pressing the button again would only queue another one.
            ImGui.TextColored(WarnText, "reading every tab - this takes a moment");
        }
        else if (view.Pages.Count > 0)
        {
            ImGui.TextColored(GoodText, $"{view.Items} items in {view.Pages.Count} inventories");
        }
        else
        {
            ImGui.TextColored(DimText, "nothing read yet");
        }

        if (view.Status.Length > 0)
        {
            ImGui.TextColored(DimText, view.Status);
        }

        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 16.5f);
        ImGui.InputTextWithHint("##search", "name, mod or stat...", ref _search, 128);
        ImGui.SameLine();
        ImGui.TextColored(DimText, "searches the stats too");

        // The switch is for the WEB only. Pictures out of the install need no permission - they
        // are files the player already has - so what is offered here is the fallback, and the
        // label says which of the two is being turned on.
        bool installed = _art.Install is not null;

        bool web = _art.Enabled;
        if (ImGui.Checkbox(installed ? "also ask poe2db" : "item pictures from poe2db", ref web))
        {
            _art.Enabled = web;
        }

        ImGui.SameLine();

        (int unpacked, int fetched, int missing) = _art.Tally;
        string coming = _art.Pending > 0 ? $", {_art.Pending} on the way" : string.Empty;

        ImGui.TextColored(DimText, (installed, web) switch
        {
            (true, true) => $"{unpacked} out of your install, {fetched} from poe2db, {missing} nowhere{coming}",
            (true, false) => $"{unpacked} out of your install, {missing} not in it{coming}",
            (false, true) => $"from poe2db, kept on disk - {fetched} fetched, {missing} not there{coming}",
            (false, false) => "off - and the game folder was not found, so there is nowhere else to get them",
        });

        ImGui.SameLine();
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 6.5f);
        ImGui.SliderFloat("cell", ref _cell, 24f, 96f, "%.0f px");

        Prices(book);

        ImGui.Separator();

        if (view.Pages.Count == 0)
        {
            return;
        }

        Tabs(view, book);
        ImGui.SameLine();

        if (_page >= 0 && _page < view.Pages.Count && _search.Trim().Length == 0)
        {
            Grid(view.Pages[_page], book);
        }
        else
        {
            // A search runs across every tab, and a grid can only show one - so looking for
            // something falls back to the list rather than pretending the other tabs are empty.
            Items(view, book);
        }
    }

    /// <summary>
    /// The price switch, and what asking for them got.
    /// </summary>
    /// <remarks>
    /// OFF UNTIL SOMEBODY TURNS IT ON, like the pictures - but for a stronger reason. There is
    /// no local copy of a price to prefer: they only exist on somebody else's server, so the
    /// switch is the difference between a tool that reads memory and a tool that talks to the
    /// internet. What goes out is a league name and nothing else.
    ///
    /// The league is shown whether or not it is on, because it is the answer to "prices for
    /// what" - and because seeing the wrong one there is how somebody finds out the tool is
    /// pricing their softcore stash against the hardcore economy.
    /// </remarks>
    private void Prices(PriceBook book)
    {
        bool asking = _prices.Enabled;
        if (ImGui.Checkbox("prices from poe.ninja", ref asking))
        {
            _prices.Enabled = asking;
        }

        ImGui.SameLine();

        string league = _inspector.League;
        if (league.Length == 0)
        {
            // The note over the guess. "Not in an area" is only one of the two reasons, and the
            // other one - the field has moved - is the one nobody would work out from a switch
            // that quietly does nothing.
            string note = _inspector.LeagueNote;
            ImGui.TextColored(
                note.Length > 0 ? WarnText : DimText,
                note.Length > 0 ? note : "no league read yet - the game has to be in an area");

            Trade();
            return;
        }

        ImGui.TextColored(DimText, $"{league} -");
        ImGui.SameLine();

        if (_prices.Busy)
        {
            ImGui.TextColored(WarnText, "asking...");
        }
        else if (book.Ready)
        {
            ImGui.TextColored(GoodText, _prices.Status);
        }
        else
        {
            ImGui.TextColored(DimText, asking ? _prices.Status : "off");
        }

        if (_total.Priced > 0)
        {
            ImGui.SameLine();

            // Both halves, always. The total on its own reads as "what this stash is worth",
            // and a book that could price a fifth of it would answer that with a number nobody
            // can see is wrong.
            ImGui.TextColored(
                MoneyText,
                $"{StashWorth.Money(_total.Exalted)} ex across {_total.Priced} of {_total.Items} items");
        }

        Trade();
    }

    /// <summary>
    /// The trade half: what uniques poe.ninja could not price are actually being listed for.
    /// </summary>
    /// <remarks>
    /// ITS OWN SWITCH, separate from poe.ninja's, and not because more switches are better.
    /// poe.ninja is one anonymous request for a league's prices. This opens a browser, asks
    /// somebody to sign in to their own account, and then asks the game's own trade site one
    /// question at a time. Those are different things to agree to.
    ///
    /// The sign-in never comes near this tool: it lives in that browser's profile, and what
    /// crosses back is asking prices.
    /// </remarks>
    private void Trade()
    {
        bool asking = _trade.Enabled;
        if (ImGui.Checkbox("ask the trade site about the uniques poe.ninja missed", ref asking))
        {
            _trade.Enabled = asking;
        }

        if (!asking)
        {
            ImGui.SameLine();
            ImGui.TextColored(DimText, "off - it opens a browser you sign in to once");
            return;
        }

        ImGui.SameLine();

        if (ImGui.SmallButton("open the trade window"))
        {
            _signIn();
        }

        ImGui.SameLine();

        string waiting = _trade.Waiting > 0 ? $", {_trade.Waiting} waiting" : string.Empty;
        ImGui.TextColored(DimText, $"{_trade.Known} asked{waiting} - {_trade.Status}");
    }

    /// <summary>
    /// Adds up what the read came to, when the read or the book has changed.
    /// </summary>
    /// <remarks>
    /// Both are published as whole immutable objects, so "has it changed" is a reference
    /// comparison and the totals are exactly as old as the two things they came out of.
    /// </remarks>
    private void Tally(StashView view, PriceBook book)
    {
        // The trade answers arrive one at a time and long after they were asked for, so the
        // count of them is the third thing the totals depend on.
        int answers = _trade.Known;
        if (ReferenceEquals(_tallied, view) && ReferenceEquals(_talliedWith, book) && _talliedAfter == answers)
        {
            return;
        }

        _tallied = view;
        _talliedWith = book;
        _talliedAfter = answers;
        _perPage = new Valued[view.Pages.Count];
        _total = Valued.Nothing;

        for (var i = 0; i < view.Pages.Count; i++)
        {
            _perPage[i] = book.Across(view.Pages[i].Items, _trade);
            _total = new Valued(
                _total.Exalted + _perPage[i].Exalted,
                _total.Priced + _perPage[i].Priced,
                _total.Unpriced + _perPage[i].Unpriced);
        }
    }

    /// <summary>The tabs down the left, with how much is in each.</summary>
    private void Tabs(StashView view, PriceBook book)
    {
        if (!ImGui.BeginChild("stash-tabs", new Vector2(220f, 0f), ImGuiChildFlags.Borders))
        {
            ImGui.EndChild();
            return;
        }

        try
        {
            // "Everything" first, because searching across the whole stash is the thing this
            // does that the game cannot. The trailing "###" is its identity: without one, a
            // tab whose total changes is a DIFFERENT widget to ImGui and loses its selection.
            if (ImGui.Selectable($"Everything ({view.Items}){Money(book, _total)}###everything", _page < 0))
            {
                _page = -1;
            }

            for (int i = 0; i < view.Pages.Count; i++)
            {
                StashPage page = view.Pages[i];
                string worth = i < _perPage.Length ? Money(book, _perPage[i]) : string.Empty;

                if (ImGui.Selectable($"{page.Called} ({page.Items.Count}){worth}###page{i}", _page == i))
                {
                    _page = i;
                }
            }
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    /// <summary>What a pile came to, as it goes in a label. Empty when nothing is known.</summary>
    private static string Money(PriceBook book, Valued valued)
        => book.Ready && valued.Priced > 0 ? $"  -  {StashWorth.Money(valued.Exalted)} ex" : string.Empty;

    /// <summary>The items, each opening to its full stats.</summary>
    private void Items(StashView view, PriceBook book)
    {
        if (!ImGui.BeginChild("stash-items", Vector2.Zero, ImGuiChildFlags.Borders))
        {
            ImGui.EndChild();
            return;
        }

        try
        {
            int shown = 0;
            int matched = 0;

            for (int i = 0; i < view.Pages.Count; i++)
            {
                if (_page >= 0 && _page != i)
                {
                    continue;
                }

                StashPage page = view.Pages[i];
                foreach (StashSlot slot in page.Items)
                {
                    if (!Matches(slot.Item))
                    {
                        continue;
                    }

                    matched++;
                    if (shown >= MostRows)
                    {
                        continue;
                    }

                    shown++;
                    One(slot.Item, _page < 0 ? page.Called : string.Empty, book);
                }
            }

            if (matched == 0)
            {
                // "Empty" is not the whole truth: a tab that has never been opened in game
                // holds nothing here either, and the two are indistinguishable from this side.
                ImGui.TextColored(DimText, _search.Trim().Length > 0
                    ? "nothing matches"
                    : "empty - or not opened in game yet, which looks the same from here");
            }
            else if (matched > shown)
            {
                // Said rather than silently truncated: a list that stops without saying so
                // reads as "that is all of them".
                ImGui.TextColored(DimText, $"...and {matched - shown} more, not listed");
            }
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    /// <summary>
    /// One tab drawn as the game draws it: a grid, with each item in its own rectangle.
    /// </summary>
    /// <remarks>
    /// A LIST IS THE WRONG SHAPE for a stash and always was. What somebody knows about their
    /// own tabs is where things are - the currency in the corner, the maps down the left - and
    /// a list throws exactly that away, then asks them to read four hundred names instead.
    ///
    /// The rectangles come from the game: each item carries its own, which is why a two-by-three
    /// piece of armour takes six cells here without anything having to know what it is.
    /// </remarks>
    private void Grid(StashPage page, PriceBook book)
    {
        if (!ImGui.BeginChild("stash-grid", Vector2.Zero, ImGuiChildFlags.Borders))
        {
            ImGui.EndChild();
            return;
        }

        try
        {
            if (page.Items.Count == 0)
            {
                ImGui.TextColored(DimText, "empty - or not opened in game yet, which looks the same from here");
                return;
            }

            ImDrawListPtr draw = ImGui.GetWindowDrawList();
            Vector2 origin = ImGui.GetCursorScreenPos();
            float cell = _cell;

            // The empty grid first, so a tab with three things in it still reads as a tab.
            uint line = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.10f));
            for (int x = 0; x <= page.Columns; x++)
            {
                draw.AddLine(
                    origin + new Vector2(x * cell, 0f),
                    origin + new Vector2(x * cell, page.Rows * cell),
                    line);
            }

            for (int y = 0; y <= page.Rows; y++)
            {
                draw.AddLine(
                    origin + new Vector2(0f, y * cell),
                    origin + new Vector2(page.Columns * cell, y * cell),
                    line);
            }

            ulong under = 0;
            foreach (StashSlot slot in page.Items)
            {
                if (Cell(draw, origin, cell, slot, book))
                {
                    under = slot.Item.Entity;
                }
            }

            _hovered = under;

            // The window has to be told how much room the drawing took, or it scrolls to
            // nothing and the grid is drawn over whatever comes next.
            ImGui.Dummy(new Vector2(page.Columns * cell, page.Rows * cell));

            if (under != 0)
            {
                StashSlot found = page.Items.First(slot => slot.Item.Entity == under);
                ImGui.BeginTooltip();
                try
                {
                    Detail(found.Item, book, _trade);
                }
                finally
                {
                    ImGui.EndTooltip();
                }
            }
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    /// <summary>One item in its rectangle. True when the cursor is over it.</summary>
    private bool Cell(ImDrawListPtr draw, Vector2 origin, float cell, StashSlot slot, PriceBook book)
    {
        var from = new Vector2(origin.X + (slot.At.Left * cell), origin.Y + (slot.At.Top * cell));
        var to = new Vector2(from.X + (slot.At.Width * cell), from.Y + (slot.At.Height * cell));

        int rarity = slot.Item.Rarity >= 0 && slot.Item.Rarity < ByRarity.Length ? slot.Item.Rarity : 0;
        Vector4 colour = ByRarity[rarity];

        // The rarity as a tinted backing and a border, which is how the game says it - and it
        // keeps saying it while a picture is still on its way.
        draw.AddRectFilled(from, to, ImGui.GetColorU32(colour with { W = 0.14f }));
        draw.AddRect(from, to, ImGui.GetColorU32(colour with { W = 0.65f }));

        IntPtr texture = _art.Enabled ? _picture(_art.Local(slot.Item.Art)) : IntPtr.Zero;
        if (texture != IntPtr.Zero)
        {
            // Inset a little, so the border stays visible and neighbouring items do not touch.
            var pad = new Vector2(2f, 2f);
            draw.AddImage(texture, from + pad, to - pad);
        }
        else
        {
            // The name, cut to what fits. Not a placeholder for the picture - it is what the
            // window showed before pictures existed, and it is still true.
            string name = slot.Item.Called;
            float room = (to.X - from.X) - 4f;
            string shown = Fitting(name, room);
            Vector2 size = ImGui.CalcTextSize(shown);
            draw.AddText(
                new Vector2(from.X + ((to.X - from.X - size.X) * 0.5f), from.Y + ((to.Y - from.Y - size.Y) * 0.5f)),
                ImGui.GetColorU32(colour),
                shown);
        }

        if (slot.Item.Stack > 1)
        {
            string count = slot.Item.Stack.ToString();
            Vector2 size = ImGui.CalcTextSize(count);
            var at = new Vector2(to.X - size.X - 3f, to.Y - size.Y - 2f);
            draw.AddRectFilled(at - new Vector2(2f, 1f), at + size + new Vector2(2f, 1f), 0xC0000000);
            draw.AddText(at, 0xFFFFFFFF, count);
        }

        if (slot.Item.Identified == false)
        {
            draw.AddText(from + new Vector2(3f, 1f), ImGui.GetColorU32(WarnText), "?");
        }

        // Bottom left, opposite the stack count, and only for what is worth walking over to -
        // see WorthSaying.
        if (book.Of(slot.Item, _trade) is { } exalted && exalted >= WorthSaying)
        {
            string worth = StashWorth.Money(exalted);
            Vector2 size = ImGui.CalcTextSize(worth);
            var at = new Vector2(from.X + 3f, to.Y - size.Y - 2f);
            draw.AddRectFilled(at - new Vector2(2f, 1f), at + size + new Vector2(2f, 1f), 0xC0000000);
            draw.AddText(at, ImGui.GetColorU32(MoneyText), worth);
        }

        return ImGui.IsMouseHoveringRect(from, to);
    }

    /// <summary>As much of a name as fits across a cell.</summary>
    private static string Fitting(string name, float room)
    {
        if (room <= 0f || ImGui.CalcTextSize(name).X <= room)
        {
            return name;
        }

        for (int keep = name.Length - 1; keep > 1; keep--)
        {
            string shorter = name[..keep];
            if (ImGui.CalcTextSize(shorter).X <= room)
            {
                return shorter;
            }
        }

        return name[..1];
    }

    /// <summary>One item: a line, and its stats when it is opened.</summary>
    private void One(InspectedItem item, string where, PriceBook book)
    {
        ImGui.PushID((int)(item.Entity & 0x7FFF_FFFF));

        try
        {
            bool open = _open.Contains(item.Entity);
            string arrow = open ? "v" : ">";

            Vector4 colour = item.Rarity >= 0 && item.Rarity < ByRarity.Length
                ? ByRarity[item.Rarity]
                : ByRarity[0];

            string stack = item.Stack > 1 ? $" x{item.Stack}" : string.Empty;

            // The selectable shows only the arrow, and the name is drawn beside it - because a
            // selectable takes one colour and an item's name has to be its rarity's. Written
            // into the label as well it would be drawn twice, one over the other.
            if (ImGui.Selectable(arrow, open, ImGuiSelectableFlags.SpanAllColumns))
            {
                if (!_open.Remove(item.Entity))
                {
                    _open.Add(item.Entity);
                }
            }

            ImGui.SameLine();
            ImGui.TextColored(colour, $"{item.Called}{stack}");

            // Every price it knows, unlike the grid: a list is read a row at a time, so a tenth
            // of an Exalted here is a fact rather than clutter.
            if (book.Of(item, _trade) is { } exalted)
            {
                ImGui.SameLine();
                ImGui.TextColored(MoneyText, $"{StashWorth.Money(exalted)} ex");
            }

            if (where.Length > 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(DimText, $"- {where}");
            }

            if (item.Identified == false)
            {
                ImGui.SameLine();
                ImGui.TextColored(WarnText, "unidentified");
            }

            if (open)
            {
                Detail(item, book, _trade);
            }
        }
        finally
        {
            ImGui.PopID();
        }
    }

    /// <summary>Everything the item says about itself.</summary>
    private static void Detail(InspectedItem item, PriceBook book, TradePrices trade)
    {
        ImGui.Indent();

        try
        {
            ImGui.TextColored(DimText, item.Path);

            // The unit price as well as the stack's, because they are different questions -
            // "what is this pile worth" and "what is one of them going for".
            if (book.Of(item, trade) is { } exalted)
            {
                // WHERE the price came from, because the two are different kinds of answer: a
                // league's trade averaged and gated, or ten listings on the site right now.
                string from = book.Unit(item) is null ? "  -  from the trade site" : string.Empty;

                ImGui.TextColored(
                    MoneyText,
                    item.Stack > 1
                        ? $"{StashWorth.Money(exalted)} ex  -  {StashWorth.Money(exalted / item.Stack)} ex each{from}"
                        : $"{StashWorth.Money(exalted)} ex{from}");
            }

            if (item.Unique.Length > 0)
            {
                // Once the unique's own name is the title, its BASE is not visible anywhere -
                // and half of what a unique is is what it is built on. The id beside it because
                // that is what anything else has to match on, the same rule as the mods.
                ImGui.TextColored(DimText, item.Base.Length > 0 ? $"{item.Base}  ({item.Ivi})" : item.Ivi);
            }

            if (item.RarityName.Length > 0)
            {
                ImGui.TextColored(DimText, item.StackMax > 0
                    ? $"{item.RarityName}  -  stacks to {item.StackMax}"
                    : item.RarityName);
            }

            foreach (ItemMod mod in item.Mods)
            {
                string rolls = mod.Rolls.Count > 0 ? $"  [{string.Join(", ", mod.Rolls)}]" : string.Empty;
                string called = mod.Name.Length > 0 ? $"{mod.Name} " : string.Empty;

                // The id as well as the name, always. The name is what the game shows and the
                // id is what anything else has to match on - and for a mod nobody wrote down,
                // the id is all there is.
                ImGui.TextColored(DimText, $"{mod.Slot}: {called}({mod.Id}){rolls}");
            }

            if (item.Stats.Count > 0)
            {
                ImGui.Separator();
            }

            foreach (ItemStat stat in item.Stats)
            {
                ImGui.TextUnformatted(stat.Said);
            }
        }
        finally
        {
            ImGui.Unindent();
        }
    }

    /// <summary>Whether an item is what somebody is looking for.</summary>
    /// <remarks>
    /// Searches the STATS as well as the name, which is the point: "what have I got with
    /// movement speed on it" is the question a stash cannot answer for itself.
    /// </remarks>
    private bool Matches(InspectedItem item)
    {
        string looking = _search.Trim();
        if (looking.Length == 0)
        {
            return true;
        }

        // The BASE as well as what it is called: a unique shows its own name, so looking for
        // "Stellar Amulet" would otherwise miss every unique built on one.
        if (item.Called.Contains(looking, StringComparison.OrdinalIgnoreCase)
            || item.Base.Contains(looking, StringComparison.OrdinalIgnoreCase)
            || item.Path.Contains(looking, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (ItemStat stat in item.Stats)
        {
            if (stat.Said.Contains(looking, StringComparison.OrdinalIgnoreCase)
                || stat.Id.Contains(looking, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (ItemMod mod in item.Mods)
        {
            if (mod.Id.Contains(looking, StringComparison.OrdinalIgnoreCase)
                || mod.Name.Contains(looking, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
