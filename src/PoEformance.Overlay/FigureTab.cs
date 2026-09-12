using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.Components;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Overlay;

/// <summary>
/// The tab for one figure written on the game's own HUD: its switch, what it shows and how
/// fresh that can be, and its looks.
/// </summary>
/// <remarks>
/// THE SWITCH IS THE STYLE'S OWN. A drawn thing has exactly one on/off in this tool, the hidden
/// flag of its catalogue entry - see <see cref="FlaskUsesLayer"/> for why a second flag was
/// refused - and until now that flag was reachable only as the tick at the left of a colour
/// row, on a tab named after projectiles. Asked for as a checkbox on the combat page, it is
/// drawn here as a master switch: the same flag, read and written through the same style, so
/// this page and the Appearance page cannot disagree. The row's colour, size and plate sit
/// under it, where somebody who has just switched the figure on will look next.
///
/// ONE CLASS FOR BOTH FIGURES rather than a window each, because the two tabs are the same
/// tab: a switch, a sentence on what the figure is and how fresh it can be, a status line off
/// the snapshot, and the rows. What differs is data, and data is what the constructor takes.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class FigureTab
{
    private static readonly Vector4 DimText = OverlayInk.Quiet;

    /// <summary>What the belt's figure is, for its tab.</summary>
    public const string BeltExplanation =
        "How many uses each flask has left, written on the flask: its charges over the cost of one"
        + " use, both read off the belt itself, so the count is live. Charms are left to the game,"
        + " which prints their own.";

    /// <summary>What the skill bar's figure is, and the one thing to know about its freshness.</summary>
    public const string SkillBarExplanation =
        "Each skill's DPS, written short on its icon. The number is the Skills panel's, read while"
        + " that panel is open - the game computes it nowhere else - so it is as fresh as the last"
        + " look at the panel. After starting the tool, open the Skills panel once.";

    private readonly OverlayStyle _style;
    private readonly Action _save;
    private readonly string _key;
    private readonly string _title;
    private readonly string _explanation;
    private readonly Func<WorldSnapshot, string> _status;
    private readonly StyleRows _rows;

    /// <param name="style">The one style everything drawn reads from.</param>
    /// <param name="save">Writes the style down. Called when the switch moves; the rows call it themselves.</param>
    /// <param name="key">The catalogue key whose hidden flag is the figure's switch.</param>
    /// <param name="title">What the switch says.</param>
    /// <param name="explanation">What the figure is, in a sentence or two.</param>
    /// <param name="groups">The catalogue groups drawn under the switch.</param>
    /// <param name="status">The status line, from the snapshot, every frame the tab is open.</param>
    public FigureTab(
        OverlayStyle style,
        Action save,
        string key,
        string title,
        string explanation,
        string[] groups,
        Func<WorldSnapshot, string> status)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(save);
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentException.ThrowIfNullOrEmpty(title);
        ArgumentNullException.ThrowIfNull(explanation);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(status);
        _style = style;
        _save = save;
        _key = key;
        _title = title;
        _explanation = explanation;
        _status = status;
        _rows = new StyleRows(style, save, groups);
    }

    /// <summary>Draws the tab: the switch, the words, the status line, the rows.</summary>
    public void Draw(WorldSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // Written down at once rather than through the rows' settling: a tick is one click,
        // not a drag, so there is nothing to wait for.
        bool on = _style.Visible(_key);
        if (OverlayLayout.Master(_title, ref on))
        {
            _style.Set(_key, _style[_key] with { Hidden = !on });
            _save();
        }

        ImGuiText.Wrapped(DimText, _explanation);
        ImGuiText.Wrapped(DimText, _status(snapshot));

        ImGui.Separator();
        _rows.Draw();
    }

    /// <summary>While the tab is not on screen: a colour change left behind still lands.</summary>
    public void Idle() => _rows.Idle();

    /// <summary>The belt's line: what is on it, and whether its bar is on screen to write on.</summary>
    public static string BeltStatus(WorldSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.FlaskBelt is not FlaskBelt belt || belt.IsUnknown)
        {
            return "belt not read - run with --flasks for the chain";
        }

        int flasks = 0;
        int charms = 0;
        foreach (EquippedFlask flask in belt.Flasks)
        {
            if (flask.IsCharm)
            {
                charms++;
            }
            else
            {
                flasks++;
            }
        }

        int slots = snapshot.FlaskSlotsOnScreen.Count;
        return $"{flasks} flasks and {charms} charms on the belt; "
            + (slots > 0 ? $"{slots} slots on the HUD" : "flask bar not on screen");
    }

    /// <summary>The skill bar's line: how many icons carry a figure, and what the panel is doing.</summary>
    public static string SkillBarStatus(WorldSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        IReadOnlyList<SkillSlotOnScreen> slots = snapshot.SkillSlotsOnScreen;
        if (slots.Count == 0)
        {
            return "skill bar not on screen";
        }

        IReadOnlyDictionary<ulong, int> dps = snapshot.SkillDpsByKey;
        int written = 0;
        foreach (SkillSlotOnScreen slot in slots)
        {
            if (slot.Key != 0 && dps.ContainsKey(slot.Key))
            {
                written++;
            }
        }

        return $"{written} of {slots.Count} slots carry a figure; panel: {snapshot.SkillsPanelReadout}";
    }
}
