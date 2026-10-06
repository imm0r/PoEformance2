using System.Numerics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;

namespace PoEformance.Game.Ui;

/// <summary>One recipe row of the Runeshape Combinations panel, as the game holds it.</summary>
/// <param name="Address">The row element, which is what the geometry is read against each tick.</param>
/// <param name="LabelAddress">
/// The row's text element - its child at LabelChild - which is placed each tick beside the
/// row, because WHERE THE TEXT STARTS is where a price goes. The interface browser settled
/// its shape on 0.5.5: a text element sized to its own text (305x30 window pixels for
/// "4x Blacksmith's Whetstone"), set 386 UI units into a row whose left edge is the rune
/// icons', so the gap between the icons and the text is the room a price has.
/// </param>
/// <param name="Label">
/// The text the panel paints on the row - "3x Exalted Orb", or on a Russian client
/// "Деталь доспеха (6)". LOCALISED, so it is the last thing to match a price on; it is kept
/// because it is the only thing a row has when the recipe behind it did not resolve.
/// </param>
/// <param name="RecipeId">
/// The recipe's engine id - "4SlotExaltedOrb1" - or empty when the row's back-pointer did not
/// lead to a recipe. Empty is a DRIFT SIGNAL rather than an ordinary answer: every row the
/// panel draws has one.
/// </param>
/// <param name="RewardPath">
/// The reward's metadata path, "Metadata/Items/Currency/CurrencyAddModToRare". Empty when the
/// recipe has no fixed reward (a gem of the area's level, a random currency, a unique of a
/// slot) - and, of course, when the recipe did not resolve.
/// </param>
/// <param name="RewardName">
/// What the client calls the reward, read off its BaseItemTypes row. Localised, like the
/// label; a fallback for the table that names rewards in English.
/// </param>
/// <param name="RewardArt">
/// The reward's inventory picture, "Art/2DItems/Currency/CurrencyAddModToRare.dds", or empty.
/// Accepted only when it reads as a path under Art/, because the offset it hangs off is
/// computed rather than measured - see BaseItemTypesRow.VisualIdentityPtr in the schema.
/// </param>
/// <param name="RewardCount">How many of the reward the recipe pays. Zero when unknown.</param>
/// <param name="RewardGemLevel">For the gem recipes with no fixed reward, the level rolled. Zero otherwise.</param>
public sealed record RunecraftRow(
    ulong Address,
    ulong LabelAddress,
    string Label,
    string RecipeId,
    string RewardPath,
    string RewardName,
    string RewardArt,
    int RewardCount,
    int RewardGemLevel,
    int MinLevel,
    int MaxLevel);

/// <summary>Where one row is on the screen this tick.</summary>
/// <param name="Address">The row element.</param>
/// <param name="Where">The row's rectangle, rune icons to right edge - what a frame goes round.</param>
/// <param name="Text">
/// The row's text element's rectangle, or null when it did not read - what a price sits
/// before. Null is a missing anchor, not a missing row: the row is still drawn, at its edge.
/// </param>
public readonly record struct RunecraftPlace(ulong Address, ScreenRect Where, ScreenRect? Text);

/// <summary>The panel as last resolved: open, with its three elements, or shut with the reason.</summary>
/// <param name="Gate">The window container, whose visible bit is the panel-open signal.</param>
/// <param name="Viewport">The scroll clip the list slides under.</param>
/// <param name="Container">The element whose children are the rows. Zero means shut.</param>
/// <param name="Why">What happened, in words, for the readout.</param>
/// <param name="Named">
/// The StringIds of the gate and the container, when they were (re)walked this tick - the
/// readout prints them so that, if the game names these elements, somebody can replace the
/// fingerprint walk with the name after one look. Empty when nothing was walked.
/// </param>
public sealed record RunecraftPanelState(ulong Gate, ulong Viewport, ulong Container, string Why, string Named = "")
{
    /// <summary>Whether the panel is open and its rows can be read.</summary>
    public bool Open => Container != 0;

    public static RunecraftPanelState Shut(string why) => new(0, 0, 0, why);
}

/// <summary>
/// Finds the Runeshape Combinations panel and reads its rows.
/// </summary>
/// <remarks>
/// PORTED FROM yokkenUA's RunecraftHelper, a GameHelper2 plugin, and what it settled is worth
/// more than its code: the panel has to be found by FINGERPRINT because its child indices move
/// between game restarts, each row's label is its first child's text, each row carries a pointer
/// to the recipe it displays, and rows scrolled out of the viewport keep their visible bit while
/// the game clips them with a scissor rectangle. All of that is in the schema's RunecraftPanel
/// block, with the measurements that back it.
///
/// WHAT IS DIFFERENT HERE, and why. The reference walks the whole tree from the root on EVERY
/// FRAME, open or shut - a hundred and fifty flag reads to learn that the panel is still closed,
/// sixty times a second. This keeps the gate once it has found it, re-checks it with two reads a
/// tick (still an element, still that fingerprint) and reads its visible bit for the answer; the
/// full scan runs only while no gate is known, and then at most four times a second. The rows
/// are read in TWO RATES like the atlas: what a row IS - its label, its recipe, its reward -
/// cannot change while the panel sits open, so <see cref="Rows"/> is for the caller's interval,
/// and WHERE it is moves with every scroll, so <see cref="Place"/> is for every tick.
///
/// THE SCROLL IS ADDED BY HAND, and this is the one place the reference's arithmetic departs from
/// <see cref="UiElementReader"/>'s. The viewport's PositionModifier is the scroll offset, and the
/// game applies it to the content under the viewport whether or not that content carries the
/// ShouldModifyPos flag - the reference measured prices freezing on scroll until it added the
/// offset for the viewport's child unconditionally. The ordinary walk adds a parent's modifier
/// only when the child asks, so this reader adds it when the child does NOT, and leaves the walk
/// to it when the child does: either way it is counted once.
///
/// EVERY OFFSET IS 0.5.5 AND UNSEEN BY THIS TOOL until the panel is opened with the readout up.
/// The two that drift silently are guarded by content: a recipe is believed only when its id
/// reads as "&lt;digits&gt;Slot...", and art only when its path starts with "Art/". A wrong offset
/// then reads as "no recipe" or "no art" in the readout, not as a price that happens to be right
/// on an English client.
/// </remarks>
public sealed class RunecraftPanelReader
{
    /// <summary>How long between scans of the root while no gate is known.</summary>
    /// <remarks>
    /// A scan is a flags read per root child - about a hundred and fifty - so unthrottled it
    /// would cost more while the panel is SHUT than reading it costs while it is open. A quarter
    /// of a second is the most the first opening can lag, once per session.
    /// </remarks>
    public const long SearchAgainMs = 250;

    /// <summary>Most rows looked at to decide whether a candidate is the recipes container.</summary>
    /// <remarks>
    /// Enough to find a labelled row in the real list, whose first rows are the ones the game
    /// draws; a bound so a decoy sibling with hundreds of unlabelled children costs a handful of
    /// reads rather than all of them.
    /// </remarks>
    private const int MostRowsProbed = 32;

    /// <summary>Longest a label is read. A recipe label is a count and a name.</summary>
    private const int MostLabelChars = 96;

    /// <summary>Longest an engine id or a path is read.</summary>
    private const int MostIdChars = 128;

    private const long Never = long.MinValue;

    private readonly IMemoryReader _reader;
    private readonly UiElementReader _elements;

    private readonly uint[] _fingerprints;
    private readonly int _gateStep;
    private readonly int _viewportStep;
    private readonly int _labelChild;
    private readonly int _mostRootChildren;
    private readonly int _mostRows;
    private readonly int _mostSiblings;
    private readonly int _recipePtr;

    private readonly int _flags;
    private readonly int _text;
    private readonly int _parent;
    private readonly int _stringId;
    private readonly uint _visible;

    private readonly int _recipeId;
    private readonly int _recipeMinLevel;
    private readonly int _recipeMaxLevel;
    private readonly int _recipeReward;
    private readonly int _recipeCount;
    private readonly int _recipeGemLevel;

    private readonly int _itemId;
    private readonly int _itemName;
    private readonly int _itemVisual;
    private readonly int _visualDds;

    // What was found, kept across ticks: the gate outlives the panel being shut, the two below
    // it are re-walked from the gate when they stop answering.
    private ulong _under;
    private ulong _gate;
    private int _gateIndex = -1;
    private ulong _viewport;
    private ulong _container;
    private long _searchedAt = Never;

    // Set by a successful walk, read by Resolve to fill the state.
    private ulong _walkedViewport;

    public RunecraftPanelReader(IMemoryReader reader, OffsetSchema schema, UiElementReader elements)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(elements);
        _reader = reader;
        _elements = elements;

        StructDef panel = schema.Structs["RunecraftPanel"];
        _fingerprints =
        [
            (uint)panel.Constants["Fingerprint0"],
            (uint)panel.Constants["Fingerprint1"],
            (uint)panel.Constants["Fingerprint2"],
            (uint)panel.Constants["Fingerprint3"],
            (uint)panel.Constants["Fingerprint4"],
        ];
        _gateStep = (int)panel.Constants["GateStep"];
        _viewportStep = (int)panel.Constants["ViewportStep"];
        _labelChild = (int)panel.Constants["LabelChild"];
        _mostRootChildren = (int)panel.Constants["MostRootChildren"];
        _mostRows = (int)panel.Constants["MostRows"];
        _mostSiblings = (int)panel.Constants["MostSiblings"];
        _recipePtr = panel.OffsetOf("RecipeRowPtr");

        StructDef ui = schema.Structs["UiElementBase"];
        _flags = ui.OffsetOf("Flags");
        _text = ui.OffsetOf("TextPtr");
        _parent = ui.OffsetOf("ParentPtr");
        _stringId = ui.OffsetOf("StringIdPtr");
        _visible = (uint)ui.Constants["FlagIsVisible"];

        StructDef recipe = schema.Structs["Expedition2RecipesRow"];
        _recipeId = recipe.OffsetOf("IdPtr");
        _recipeMinLevel = recipe.OffsetOf("MinLevelReq");
        _recipeMaxLevel = recipe.OffsetOf("MaxLevelReq");
        _recipeReward = recipe.OffsetOf("RewardRowPtr");
        _recipeCount = recipe.OffsetOf("RewardCount");
        _recipeGemLevel = recipe.OffsetOf("RewardGemLevel");

        StructDef item = schema.Structs["BaseItemTypesRow"];
        _itemId = item.OffsetOf("IdPtr");
        _itemName = item.OffsetOf("NamePtr");
        _itemVisual = item.OffsetOf("VisualIdentityPtr");
        _visualDds = schema.Structs["ItemVisualIdentityRow"].OffsetOf("DdsFilePtr");
    }

    /// <summary>The gate as last found, or 0 - for the readout and the tests.</summary>
    public ulong Gate => _gate;

    /// <summary>Which child of the root the gate was found at, or -1.</summary>
    public int GateIndex => _gateIndex;

    /// <summary>
    /// Whether the panel is open, and the elements to read it from.
    /// </summary>
    /// <remarks>
    /// CHEAP WHILE SHUT, which is almost always: with a gate known this is two reads to trust
    /// it and a visibility walk to answer, and with none it is a bounded scan at most four times
    /// a second. Open, the viewport and the container are trusted while they still answer to
    /// their fingerprints and re-walked from the gate when they do not.
    /// </remarks>
    public RunecraftPanelState Resolve(ulong uiRoot, long nowMs)
    {
        if (uiRoot == 0)
        {
            Forget();
            return RunecraftPanelState.Shut("no interface root");
        }

        if (_gate != 0)
        {
            if (_under != uiRoot || !Matches(_gate, _gateStep))
            {
                // A rebuilt interface, or an element freed and reused: the gate is looked for
                // again, on the clock, rather than read as whatever now sits at its address.
                Forget();
            }
            else if (!_elements.IsVisible(_gate))
            {
                // The list and the viewport are KEPT across a shut panel: both are re-checked
                // against their fingerprints before they are trusted again, and keeping them
                // makes the next opening two reads each instead of a walk down from the gate.
                return RunecraftPanelState.Shut("panel shut");
            }
            else if (_container != 0 && Matches(_container, _fingerprints.Length - 1)
                     && Matches(_viewport, _viewportStep))
            {
                return new RunecraftPanelState(_gate, _viewport, _container, "panel open");
            }
            else
            {
                // The gate is open and the list under it has moved: walk down from the gate,
                // which is a few dozen reads, not a scan of the root.
                _walkedViewport = 0;
                ulong found = Walk(_gate, _gateStep + 1);
                if (found != 0)
                {
                    _viewport = _walkedViewport;
                    _container = found;
                    return new RunecraftPanelState(_gate, _viewport, _container, "panel open", Names());
                }

                Forget();
                return RunecraftPanelState.Shut("the panel's gate is open but no recipe list is under it");
            }
        }

        if (_searchedAt != Never && nowMs - _searchedAt < SearchAgainMs && nowMs >= _searchedAt)
        {
            return RunecraftPanelState.Shut("panel not found yet");
        }

        _searchedAt = nowMs;
        _under = uiRoot;
        return Scan(uiRoot);
    }

    /// <summary>
    /// Reads the rows the panel is showing: their labels, recipes and rewards.
    /// </summary>
    /// <remarks>
    /// ONLY THE ROWS WHOSE OWN VISIBLE BIT IS SET. The container holds every recipe in the game
    /// as a child - three hundred odd - and the game shows the handful this monolith offers by
    /// flipping their bits, so the flag is read for all and everything else for the few. The
    /// chain above was just checked by <see cref="Resolve"/>, so a row's own bit is enough
    /// here, as it is for a panel's children everywhere else.
    ///
    /// A shown row with no label is not a row, and is left out rather than reported blank.
    /// </remarks>
    public List<RunecraftRow> Rows(ulong container)
    {
        var rows = new List<RunecraftRow>();
        if (container == 0)
        {
            return rows;
        }

        foreach (ulong row in _elements.Children(container, _mostRows))
        {
            if ((_reader.Read<uint>(row + (ulong)_flags) & _visible) == 0)
            {
                continue;
            }

            (ulong label, string text) = LabelOf(row);
            if (text.Length == 0)
            {
                continue;
            }

            rows.Add(Describe(row, label, text));
        }

        return rows;
    }

    /// <summary>
    /// Where the rows and their texts are this tick, and the rectangle they are clipped to.
    /// </summary>
    /// <remarks>
    /// The rows are placed with ONE walk of the chain above their container, nudged by the
    /// viewport's scroll where the ordinary walk would not have added it - see the remarks on
    /// the class - and each row's text element is placed under its row from there, with no
    /// second walk. A row the game reports hidden, or one whose rectangle did not read, is
    /// simply absent: a stale position on a scrolled list is a price on the wrong row. A text
    /// element that did not read leaves its row in, with no anchor.
    /// </remarks>
    /// <param name="panel">The panel as resolved this tick.</param>
    /// <param name="rows">The rows to place - the ones <see cref="Rows"/> reported.</param>
    /// <param name="scale">The viewport to place them in.</param>
    public (List<RunecraftPlace> Rows, ScreenRect? Viewport) Place(
        RunecraftPanelState panel, IReadOnlyList<RunecraftRow> rows, UiScale scale)
    {
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(rows);

        var placed = new List<RunecraftPlace>(rows.Count);
        if (!panel.Open || rows.Count == 0)
        {
            return (placed, null);
        }

        ScreenRect? clip = null;
        if (_elements.Read(panel.Viewport, scale, withStringId: false) is { } viewport
            && Sane(viewport.Position) && Sane(viewport.Size)
            && viewport.Size.X >= 1f && viewport.Size.Y >= 1f)
        {
            clip = new ScreenRect(viewport.Left, viewport.Top, viewport.Right, viewport.Bottom);
        }

        var addresses = new ulong[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            addresses[i] = rows[i].Address;
        }

        Vector2 nudge = Scroll(panel, scale);
        Dictionary<ulong, Placed> under = _elements.ReadSiblings(panel.Container, addresses, scale, nudge);
        foreach (RunecraftRow row in rows)
        {
            if (!under.TryGetValue(row.Address, out Placed at) || !at.Shown || Rect(at) is not { } where)
            {
                continue;
            }

            ScreenRect? text = row.LabelAddress != 0
                               && _elements.ReadUnder(row.Address, at.Unscaled, row.LabelAddress, scale) is { } label
                ? Rect(label)
                : null;

            placed.Add(new RunecraftPlace(row.Address, where, text));
        }

        return (placed, clip);
    }

    /// <summary>A placed element's rectangle, or null when it is too small or too far to be one.</summary>
    private static ScreenRect? Rect(in Placed at)
        => Sane(at.Position) && Sane(at.Size) && at.Size.X >= 1f && at.Size.Y >= 1f
            ? new ScreenRect(at.Position.X, at.Position.Y, at.Position.X + at.Size.X, at.Position.Y + at.Size.Y)
            : null;

    /// <summary>
    /// The viewport's scroll offset, in the container's own UI space, where the ordinary walk
    /// would not already have applied it.
    /// </summary>
    /// <remarks>
    /// The content frame is the viewport's child and the container's parent; its flag decides.
    /// With the flag set <see cref="UiElementReader.UnscaledPosition"/> adds the modifier on its
    /// way up and this adds nothing, so the offset is never counted twice whichever way the
    /// game has set the bit. The modifier is the viewport's and so in the viewport's scale
    /// space; the container may sit in another, and a translation crosses that boundary by the
    /// ratio of the two scales, as the position walk converts everything else.
    /// </remarks>
    private Vector2 Scroll(RunecraftPanelState panel, UiScale scale)
    {
        ulong content = _reader.ReadPointer(panel.Container + (ulong)_parent);
        if (content == 0 || _elements.TakesParentModifier(content))
        {
            return Vector2.Zero;
        }

        Vector2 offset = _elements.PositionModifierOf(panel.Viewport);
        if (offset == Vector2.Zero || !Sane(offset))
        {
            return Vector2.Zero;
        }

        (byte viewportIndex, float viewportMultiplier) = _elements.ScaleSpaceOf(panel.Viewport);
        (byte containerIndex, float containerMultiplier) = _elements.ScaleSpaceOf(panel.Container);
        if (viewportIndex == containerIndex && viewportMultiplier.Equals(containerMultiplier))
        {
            return offset;
        }

        (float fromW, float fromH) = scale.For(viewportIndex, viewportMultiplier);
        (float toW, float toH) = scale.For(containerIndex, containerMultiplier);
        return new Vector2(
            toW != 0f ? offset.X * fromW / toW : offset.X,
            toH != 0f ? offset.Y * fromH / toH : offset.Y);
    }

    /// <summary>Looks for an OPEN gate among the root's children, the remembered index first.</summary>
    private RunecraftPanelState Scan(ulong uiRoot)
    {
        if (_gateIndex >= 0 && TryGate(_elements.Child(uiRoot, _gateIndex), _gateIndex) is { } remembered)
        {
            return remembered;
        }

        List<ulong> children = _elements.Children(uiRoot, _mostRootChildren);
        for (int i = 0; i < children.Count; i++)
        {
            if (i != _gateIndex && TryGate(children[i], i) is { } found)
            {
                return found;
            }
        }

        return RunecraftPanelState.Shut($"no open panel among {children.Count} root children");
    }

    /// <summary>Whether this root child is the open gate with a recipe list under it; keeps it if so.</summary>
    private RunecraftPanelState? TryGate(ulong candidate, int index)
    {
        if (candidate == 0 || !_reader.TryRead(candidate + (ulong)_flags, out uint flags))
        {
            return null;
        }

        // ONLY A VISIBLE GATE IS WORTH WALKING: a shut one has the rows under it just the same,
        // and finding it would report the panel open. The visible bit is masked out of the
        // fingerprint and asked about separately for exactly this step.
        if ((flags & ~_visible) != (_fingerprints[_gateStep] & ~_visible) || (flags & _visible) == 0)
        {
            return null;
        }

        _walkedViewport = 0;
        ulong container = Walk(candidate, _gateStep + 1);
        if (container == 0)
        {
            return null;
        }

        _gate = candidate;
        _gateIndex = index;
        _viewport = _walkedViewport;
        _container = container;
        return new RunecraftPanelState(_gate, _viewport, _container, "panel open", Names());
    }

    /// <summary>
    /// The fingerprint walk: one step per level, every matching sibling tried, visible ones first.
    /// </summary>
    /// <remarks>
    /// BACKTRACKING IS THE WHOLE POINT. The reference records that siblings sharing a fingerprint
    /// are common in this tree, and that the greedy walk it started with stepped into the wrong
    /// one and dead-ended silently. So a branch that reaches the bottom without a recipes
    /// container is abandoned and the next sibling tried. Visible candidates go first so an open
    /// instance wins over a shut twin, and the invisible ones are still tried after, because a
    /// frame can be hidden while the list under it is what is being looked for.
    /// </remarks>
    private ulong Walk(ulong parent, int step)
    {
        if (step == _fingerprints.Length)
        {
            return IsRecipesContainer(parent) ? parent : 0;
        }

        List<ulong> children = _elements.Children(parent, _mostSiblings);
        if (children.Count == 0)
        {
            return 0;
        }

        uint wanted = _fingerprints[step] & ~_visible;

        // The flags once per child, kept for both passes - reading them twice would double the
        // cost of exactly the level with the most siblings.
        Span<uint> flags = children.Count <= 512 ? stackalloc uint[children.Count] : new uint[children.Count];
        for (int i = 0; i < children.Count; i++)
        {
            flags[i] = _reader.Read<uint>(children[i] + (ulong)_flags);
        }

        for (int pass = 0; pass < 2; pass++)
        {
            bool wantVisible = pass == 0;
            for (int i = 0; i < children.Count; i++)
            {
                if ((flags[i] & ~_visible) != wanted || ((flags[i] & _visible) != 0) != wantVisible)
                {
                    continue;
                }

                ulong deeper = Walk(children[i], step + 1);
                if (deeper != 0)
                {
                    if (step == _viewportStep)
                    {
                        _walkedViewport = children[i];
                    }

                    return deeper;
                }
            }
        }

        return 0;
    }

    /// <summary>
    /// Whether an element's children are recipe rows: one of the first few carries a label.
    /// </summary>
    /// <remarks>
    /// What tells the real list from a sibling that shares its fingerprint and holds no rows. The
    /// label is read whether or not the row is shown - a shut monolith's list still has its
    /// rows, and the walk is what finds the list, not what decides whether it is open.
    /// </remarks>
    private bool IsRecipesContainer(ulong element)
    {
        List<ulong> children = _elements.Children(element, MostRowsProbed);
        foreach (ulong row in children)
        {
            if (LabelOf(row).Text.Length > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The row's label element - its first child - and that element's text, or empty.</summary>
    private (ulong Element, string Text) LabelOf(ulong row)
    {
        ulong label = _elements.Child(row, _labelChild);
        return label == 0
            ? (0, string.Empty)
            : (label, _reader.ReadStdWString(label + (ulong)_text, MostLabelChars));
    }

    /// <summary>A row's recipe and reward, behind the back-pointer, with every hop checked.</summary>
    private RunecraftRow Describe(ulong row, ulong labelElement, string label)
    {
        ulong recipe = _reader.ReadPointer(row + (ulong)_recipePtr);
        string id = recipe == 0
            ? string.Empty
            : _reader.ReadUnicodeString(_reader.ReadPointer(recipe + (ulong)_recipeId), MostIdChars);

        // The content check that makes a drifted back-pointer read as "no recipe": the stale
        // slot the reference hit held a sentinel that looked live and decoded to nothing, so a
        // plausible pointer is not enough.
        if (!LooksLikeRecipeId(id))
        {
            return new RunecraftRow(
                row, labelElement, label, string.Empty, string.Empty, string.Empty, string.Empty, 0, 0, 0, 0);
        }

        int minLevel = _reader.Read<int>(recipe + (ulong)_recipeMinLevel);
        int maxLevel = _reader.Read<int>(recipe + (ulong)_recipeMaxLevel);
        int count = _reader.Read<int>(recipe + (ulong)_recipeCount);
        int gemLevel = _reader.Read<int>(recipe + (ulong)_recipeGemLevel);

        string path = string.Empty;
        string name = string.Empty;
        string art = string.Empty;

        ulong reward = _reader.ReadPointer(recipe + (ulong)_recipeReward);
        if (reward != 0)
        {
            path = _reader.ReadUnicodeString(_reader.ReadPointer(reward + (ulong)_itemId), MostIdChars);
            name = _reader.ReadUnicodeString(_reader.ReadPointer(reward + (ulong)_itemName), MostLabelChars);

            // Believed only when it reads as a path under Art/ - the offset is arithmetic, and a
            // reference disagrees with it by four bytes; see the schema. Wrong, it costs the art
            // key and nothing else, since the English name is tried before it anyway.
            ulong visual = _reader.ReadPointer(reward + (ulong)_itemVisual);
            if (visual != 0)
            {
                string dds = _reader.ReadUnicodeString(_reader.ReadPointer(visual + (ulong)_visualDds), MostIdChars);
                if (dds.StartsWith("Art/", StringComparison.OrdinalIgnoreCase))
                {
                    art = dds;
                }
            }
        }

        return new RunecraftRow(
            row, labelElement, label, id, path, name, art,
            count is > 0 and < 10_000 ? count : 0,
            gemLevel is > 0 and <= 100 ? gemLevel : 0,
            minLevel is >= 0 and <= 100 ? minLevel : 0,
            maxLevel is >= 0 and <= 100 ? maxLevel : 0);
    }

    /// <summary>
    /// Whether a string is a recipe id: a slot count, the word Slot, then a name.
    /// </summary>
    /// <remarks>
    /// Public so the shape is tested against real ids rather than trusted: "10SlotBait" and
    /// "2SlotOrbofAugmentation1" are the two ends of what the catalogue holds.
    /// </remarks>
    public static bool LooksLikeRecipeId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length < 6)
        {
            return false;
        }

        int digits = 0;
        while (digits < id.Length && char.IsAsciiDigit(id[digits]))
        {
            digits++;
        }

        return digits is >= 1 and <= 2
               && id.AsSpan(digits).StartsWith("Slot", StringComparison.Ordinal)
               && id.Length > digits + 4;
    }

    /// <summary>Whether an element is one and still carries the fingerprint of a step.</summary>
    private bool Matches(ulong element, int step)
        => _elements.IsUiElement(element)
           && (_reader.Read<uint>(element + (ulong)_flags) & ~_visible) == (_fingerprints[step] & ~_visible);

    /// <summary>What the game calls the gate and the container, for the readout.</summary>
    private string Names()
    {
        string gate = _reader.ReadStdWString(_gate + (ulong)_stringId, 64);
        string list = _reader.ReadStdWString(_container + (ulong)_stringId, 64);
        return $"gate \"{gate}\" at root child {_gateIndex}, list \"{list}\"";
    }

    /// <summary>Drops the gate. The index it was found at is kept as the next scan's first guess.</summary>
    private void Forget()
    {
        _gate = 0;
        _viewport = 0;
        _container = 0;
        _under = 0;
    }

    private static bool Sane(Vector2 pair)
        => float.IsFinite(pair.X) && float.IsFinite(pair.Y)
           && Math.Abs(pair.X) < 100_000f && Math.Abs(pair.Y) < 100_000f;
}
