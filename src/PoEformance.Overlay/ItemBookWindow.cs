using System.Globalization;
using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.Components;
using PoEformance.Game.Entities;

namespace PoEformance.Overlay;

/// <summary>
/// The item reference book: every item the game has a 3D model for, and the model itself.
/// </summary>
/// <remarks>
/// THE MONSTER BOOK'S PARTS, through <see cref="BookWindow{TBook}"/>; the model is drawn by a
/// <see cref="MonsterPortrait"/> of its own, because an item's .ao is the same file format as a
/// monster's and the portrait only ever needed the .ao paths - see MonsterModels.OfFiles. The item
/// is handed to it as a variety carrying nothing but a name and that one path, which is everything
/// the portrait reads off one.
///
/// THE LAYOUT IS WRITTEN DOWN like the monster book's - which columns, how wide, where the panes
/// split and whether the rail is open - under settings of its own, because the two tables have
/// different columns.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ItemBookWindow : BookWindow<ItemBook>
{
    /// <summary>
    /// The fields the rail offers, and what it calls them.
    /// </summary>
    /// <remarks>
    /// THE TWO WITH A HANDFUL OF ANSWERS. An item has one class and is base or unique, and those are
    /// the questions a click answers; names and file names have thousands of values and are typed.
    /// </remarks>
    private static readonly (string Label, string Field)[] Offered =
    [
        ("Classes", "class"),
        ("Kind", "kind"),
    ];

    /// <summary>A unique's row is in the game's own unique colour.</summary>
    private static readonly Vector4 UniqueInk = OverlayInk.Rarity(ItemRarity.Unique);

    private readonly Func<ItemVisuals> _table;

    private ItemVisuals _of = ItemVisuals.Empty;
    private ItemBook _page = ItemBook.Empty;

    /// <summary>Whether the pane shows the held model (AOFile2) rather than the dropped one (AOFile). See ItemVisuals.</summary>
    private bool _second;

    /// <summary>
    /// What the portrait is handed, made once per choice rather than once per frame.
    /// </summary>
    /// <remarks>
    /// The portrait compares the KEY to decide whether to reload, and keeps the variety it was
    /// handed with it; a fresh one each frame would be an allocation per frame for nothing.
    /// </remarks>
    private MonsterVariety? _subject;
    private string _subjectKey = string.Empty;

    public ItemBookWindow(Func<ItemVisuals> table)
        : base("item", ItemBook.Empty, 0.18f, 0.45f)
    {
        ArgumentNullException.ThrowIfNull(table);
        _table = table;
    }

    /// <inheritdoc/>
    protected override string Caption => "Search for any item with a model";

    /// <inheritdoc/>
    protected override string Grammar => "sword  ·  class:bow drop>40  ·  class:\"One Hand Swords\"  ·  kind:unique  ·  not unique";

    /// <inheritdoc/>
    protected override string Noun => "items";

    /// <inheritdoc/>
    protected override IReadOnlyList<(string Label, string Field)> Rails => Offered;

    /// <summary>Works the table into columns, once per table - see <see cref="BookWindow{TBook}.Current"/>.</summary>
    protected override ItemBook Current()
    {
        ItemVisuals all = _table();
        if (!ReferenceEquals(_of, all))
        {
            _of = all;
            _page = ItemBook.Of(all);
        }

        return _page;
    }

    /// <inheritdoc/>
    protected override string WhyEmpty()
        => _of.Say.Count > 0
            ? _of.Say[0]
            : "No item table yet - it is read from the install shortly after start-up.";

    /// <inheritdoc/>
    protected override Vector4? Ink(int row) => Page.Unique[row] ? UniqueInk : null;

    /// <summary>What the item is and which file it is drawn from, then the model under it.</summary>
    protected override bool PaneUnder => true;

    /// <summary>
    /// The bar under the list: the item, which of its models, the model window's switch - then its file and the model's own switches.
    /// </summary>
    protected override void Pane()
    {
        if (Chosen.Length == 0 || _of.Find(Chosen) is not { } one)
        {
            ImGui.TextDisabled("Choose an item above.");
            return;
        }

        if (one.Unique)
        {
            ImGui.TextColored(UniqueInk, ImGuiText.Escape(one.Name));
        }
        else
        {
            ImGui.TextUnformatted(one.Name);
        }

        ImGui.SameLine();
        ImGui.TextDisabled(one.Unique
            ? "unique"
            : ImGuiText.Escape(string.Create(
                CultureInfo.InvariantCulture,
                $"{(one.Class.Length > 0 ? one.Class : "no class")}  ·  drop {one.DropLevel}  ·  {one.Width}x{one.Height}")));

        // WHICH MODEL IS SHOWN IS A CHOICE AND SAID AS ONE: AOFile is the item lying on the ground -
        // its files end in Drop.ao - and AOFile2 the item in a hand, ending in Held.ao or naming a
        // .fmt outright. Only offered where both are filled.
        bool both = one.Ao.Length > 0 && one.Ao2.Length > 0;
        bool second = both ? _second : one.Ao.Length == 0;
        if (both)
        {
            ImGui.SameLine();
            if (ImGui.RadioButton("drop", !_second))
            {
                _second = false;
            }

            ImGui.SameLine();
            if (ImGui.RadioButton("held", _second))
            {
                _second = true;
            }

            second = _second;
        }

        string ao = second ? one.Ao2 : one.Ao;
        string key = one.Path + "|" + ao;
        if (!string.Equals(key, _subjectKey, StringComparison.Ordinal))
        {
            _subjectKey = key;
            _subject = new MonsterVariety(Name: one.Name, AoFiles: [ao]);
        }

        if (Model is { } model)
        {
            ImGui.SameLine();
            model.Show(_subject, _subjectKey, one.Name);
            model.DrawOpener();
        }

        // THE FILE'S NAME, its whole path and the item's art on hover - the path alone ran past the bar.
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(ImGuiText.Escape((second ? "held  " : "drop  ") + ao[(ao.Replace('\\', '/').LastIndexOf('/') + 1)..]));
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(ImGuiText.Escape($"{ao}\n{one.Path}\nart {one.Art}\nicon {one.Icon}"));
        }

        ImGui.SameLine();
        ModelRow();
    }
}
