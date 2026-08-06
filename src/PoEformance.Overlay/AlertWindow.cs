using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.Components;
using PoEformance.Game.World;

namespace PoEformance.Overlay;

/// <summary>
/// What to be told about.
/// </summary>
/// <remarks>
/// A list somebody curates, not a page of switches. The thing a person actually wants is
/// "tell me about breaches" - which here is a row they add, rather than a feature request -
/// and the reference tool's ten booleans plus two comma-separated text boxes could not
/// express it at all.
///
/// The distance is given the most room of the adjustable things because it is the knob that
/// decides whether a rule is useful or unbearable. "Rare monster" everywhere in the area is
/// a banner every few seconds; "rare monster within a screen" is a warning.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class AlertWindow
{
    private static readonly Vector4 DimText = new(0.62f, 0.65f, 0.72f, 1f);
    private static readonly Vector4 OffText = new(0.5f, 0.5f, 0.55f, 1f);

    /// <summary>What an added rule watches for, before it is narrowed.</summary>
    private static readonly (string Label, Func<string, AlertRule> Build)[] Templates =
    [
        ("anything whose path contains", text => new AlertRule(Pretty(text), PathContains: text)),
        ("a monster whose path contains", text => new AlertRule(Pretty(text), Kind: EntityKind.Monster, PathContains: text)),
        ("a drop whose path contains", text => new AlertRule(Pretty(text), Kind: EntityKind.WorldItem, PathContains: text)),
    ];

    private readonly AlertWatcher _watcher;
    private readonly Action _save;

    private readonly List<AlertRule> _rules;
    private string _adding = string.Empty;
    private int _template;
    private bool _unsaved;

    public AlertWindow(AlertWatcher watcher, Action save)
    {
        ArgumentNullException.ThrowIfNull(watcher);
        ArgumentNullException.ThrowIfNull(save);
        _watcher = watcher;
        _save = save;

        // A working copy the editor owns, handed back to the watcher on every change. The
        // watcher's own list is whatever it was given - shipped defaults included - and those
        // are shared, so editing that in place would edit them for everybody.
        _rules = [.. watcher.Rules];
        _watcher.Rules = _rules;
    }

    /// <summary>Whether the window is on screen.</summary>
    public bool Visible { get; set; }

    /// <summary>Draws the window.</summary>
    public void Render()
    {
        if (!Visible)
        {
            Settle();
            return;
        }

        ImGui.SetNextWindowSize(new Vector2(600f, 420f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(new Vector2(300f, 100f), ImGuiCond.FirstUseEver);

        bool open = Visible;
        bool expanded = ImGui.Begin("Alerts", ref open, ImGuiWindowFlags.NoFocusOnAppearing);

        // End in a finally: an exception between Begin and End leaves ImGui's stack
        // unbalanced and the assert that follows takes the process down.
        try
        {
            if (expanded)
            {
                Draw();
            }
        }
        finally
        {
            ImGui.End();
        }

        Settle();
        Visible = open;
    }

    private void Draw()
    {
        bool enabled = _watcher.Enabled;
        if (ImGui.Checkbox("watch for these", ref enabled))
        {
            _watcher.Enabled = enabled;
            Changed();
        }

        ImGui.SameLine();
        bool quiet = _watcher.QuietInTown;
        if (ImGui.Checkbox("quiet in town", ref quiet))
        {
            _watcher.QuietInTown = quiet;
            Changed();
        }

        ImGui.SameLine();
        ImGui.TextColored(DimText, $"{_watcher.Raised} raised so far");

        ImGui.Separator();

        // Highest priority first, which is the order they would be shown in - so the list
        // reads as "what wins when several happen at once".
        foreach (AlertRule rule in _rules.OrderByDescending(r => r.Priority).ToList())
        {
            DrawRule(rule);
        }

        if (_rules.Count == 0)
        {
            ImGui.TextColored(DimText, "nothing watched for - add a rule below, or delete the file to start over");
        }

        ImGui.Separator();
        DrawAdd();
    }

    private void DrawRule(AlertRule rule)
    {
        ImGui.PushID(rule.Name);

        bool on = rule.Enabled;
        if (ImGui.Checkbox("###on", ref on))
        {
            Replace(rule, rule with { Enabled = on });
        }

        ImGui.SameLine();
        ImGui.TextColored(on ? new Vector4(1f, 1f, 1f, 1f) : OffText, rule.Name);

        ImGui.SameLine();
        ImGui.TextColored(DimText, Describe(rule));

        // Pinned right, so the numbers line up down the list instead of wandering with the
        // length of each rule's description.
        ImGui.SameLine(ImGui.GetContentRegionAvail().X - 210f);

        ImGui.SetNextItemWidth(150f);
        float within = rule.WithinDistance;
        if (ImGui.SliderFloat(
                "###within",
                ref within,
                0f,
                400f,
                within <= 0f ? "anywhere in the area" : "within %.0f"))
        {
            Replace(rule, rule with { WithinDistance = within });
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("x"))
        {
            _rules.Remove(rule);
            Changed();
        }

        ImGui.PopID();
    }

    private void DrawAdd()
    {
        ImGui.SetNextItemWidth(230f);
        if (ImGui.BeginCombo("###template", Templates[_template].Label))
        {
            for (int i = 0; i < Templates.Length; i++)
            {
                if (ImGui.Selectable(Templates[i].Label, i == _template))
                {
                    _template = i;
                }
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(180f);
        bool entered = ImGui.InputText("###adding", ref _adding, 96, ImGuiInputTextFlags.EnterReturnsTrue);

        ImGui.SameLine();
        bool clicked = ImGui.Button("add");

        // A rule with nothing in it would match EVERY entity in the area, so an empty box
        // adds nothing rather than adding something that has to be found and deleted.
        if ((entered || clicked) && _adding.Trim().Length > 0)
        {
            AlertRule made = Templates[_template].Build(_adding.Trim());
            if (!made.SaysNothing && !_rules.Any(r => r.Name == made.Name))
            {
                _rules.Add(made);
                Changed();
            }

            _adding = string.Empty;
        }

        ImGui.TextColored(DimText, "part of a metadata path - \"breach\", \"strongbox\", \"essence\"");
    }

    /// <summary>What a rule watches for, in a few words.</summary>
    private static string Describe(AlertRule rule)
    {
        List<string> parts = [];

        if (rule.MinRarity != ItemRarity.Unknown)
        {
            parts.Add(rule.MinRarity == ItemRarity.Currency ? "currency" : $"{rule.MinRarity} or better");
        }

        if (rule.Kind != EntityKind.Unknown)
        {
            parts.Add(rule.Kind.ToString().ToLowerInvariant());
        }

        if (rule.Place != PoiKind.None)
        {
            parts.Add(rule.Place.ToString().ToLowerInvariant());
        }

        if (rule.PathContains.Length > 0)
        {
            parts.Add($"path has \"{rule.PathContains}\"");
        }

        return parts.Count > 0 ? string.Join(", ", parts) : "nothing - it will never fire";
    }

    /// <summary>A name for a typed-in rule: the term, with a capital.</summary>
    private static string Pretty(string text)
    {
        string trimmed = text.Trim();
        return trimmed.Length == 0 ? trimmed : char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }

    private void Replace(AlertRule was, AlertRule now)
    {
        int at = _rules.IndexOf(was);
        if (at < 0)
        {
            return;
        }

        _rules[at] = now;
        Changed();
    }

    private void Changed() => _unsaved = true;

    /// <summary>
    /// Writes down a change once nothing is being dragged any more.
    /// </summary>
    /// <remarks>
    /// The same reason the appearance editor waits: a slider reports a new value on every
    /// frame it is held, so saving on each one is sixty file writes a second for one
    /// adjustment. The change itself is live either way - the watcher reads this list.
    /// </remarks>
    private void Settle()
    {
        if (!_unsaved || ImGui.IsAnyItemActive())
        {
            return;
        }

        _unsaved = false;
        _save();
    }
}
