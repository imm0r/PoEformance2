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
    private static readonly Vector4 DimText = OverlayTheme.Quiet;
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

    private PreloadWatch? _preload;
    private PreloadSettings _preloadSettings = PreloadSettings.Default;
    private Action<PreloadSettings>? _savePreload;
    private Action? _showPreloadNow;

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

    /// <summary>
    /// Adds the other kind of alert to this window: what the AREA loaded.
    /// </summary>
    /// <remarks>
    /// One window, two lists, because "what am I told about" is one question however many
    /// mechanisms answer it - somebody who wants to stop being told about strongboxes should
    /// not have to know whether that is decided by an entity in front of them or by a file the
    /// game loaded on the way in.
    ///
    /// The lists themselves stay apart. Their rules share no fields: one asks about rarity and
    /// distance, the other about a fragment of a path in a table read once per area.
    /// </remarks>
    /// <param name="showNow">
    /// Says the card again for the area you are standing in. It is said once per area, so
    /// without this the only way to see a change to it is to take a portal - which is a long
    /// way to go to find out whether a colour is right.
    /// </param>
    public void AttachPreload(
        PreloadWatch watch, PreloadSettings settings, Action<PreloadSettings> save, Action showNow)
    {
        ArgumentNullException.ThrowIfNull(watch);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(save);
        ArgumentNullException.ThrowIfNull(showNow);
        _preload = watch;
        _preloadSettings = settings;
        _savePreload = save;
        _showPreloadNow = showNow;
    }

    /// <summary>Draws the tab's content.</summary>
    public void DrawTab()
    {
        Draw();

        // After the content, so a drag that ended this frame is written down now rather
        // than waiting for whatever happens next - including the tab being switched away.
        Settle();
    }

    /// <summary>While the tab is not in front: a change made and left behind still lands.</summary>
    public void Idle() => Settle();

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

        DrawRules();

        if (_rules.Count == 0)
        {
            ImGuiText.Wrapped(DimText, "nothing watched for - add a rule below, or delete the file to start over");
        }

        ImGui.Separator();
        DrawAdd();

        if (_preload is not null)
        {
            // Real air and a TITLED rule between the two lists, not a hairline: the blocks
            // are near-identical in shape, and butted together they read as one list that
            // has lost its order - which is exactly how it was reported.
            ImGui.Spacing();
            ImGui.Spacing();
            OverlayFonts.SectionTitle("What the area loaded");
            ImGui.Spacing();
            DrawPreload(_preload);
        }
    }

    /// <summary>
    /// The rule rows, as a table: a stretching text column and a control column that sizes
    /// itself.
    /// </summary>
    /// <remarks>
    /// A TABLE rather than the old fixed-pixel pin (SameLine at avail minus 210), because
    /// that pin was a guess about how wide the controls are - and the controls grow with the
    /// text size, while the guess did not, so at any larger font the delete button sat past
    /// the window's edge no matter how wide the window was dragged. The table measures the
    /// control column from its own content, so every row ends at the same right edge at
    /// every text size, and an overlong description clips instead of shoving the controls.
    /// </remarks>
    private void DrawRules()
    {
        if (_rules.Count == 0
            || !ImGui.BeginTable("##alert-rules", 2, ImGuiTableFlags.SizingFixedFit))
        {
            return;
        }

        try
        {
            ImGui.TableSetupColumn("rule", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("controls");

            // Highest priority first, which is the order they would be shown in - so the
            // list reads as "what wins when several happen at once".
            foreach (AlertRule rule in _rules.OrderByDescending(r => r.Priority).ToList())
            {
                DrawRule(rule);
            }
        }
        finally
        {
            ImGui.EndTable();
        }
    }

    /// <summary>The second list: what the area loaded, read once on the way in.</summary>
    private void DrawPreload(PreloadWatch preload)
    {
        bool banner = _preloadSettings.Banner;
        if (ImGui.Checkbox("say it on the way in", ref banner))
        {
            PreloadChanged(_preloadSettings with { Banner = banner });
        }

        ImGui.SameLine();
        bool list = _preloadSettings.List;
        if (ImGui.Checkbox("keep it in the corner", ref list))
        {
            PreloadChanged(_preloadSettings with { List = list });
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 9.5f);
        int minFiles = _preloadSettings.MinFiles;

        // The gate that decides whether the line on the way in is trustworthy. One matching
        // file is a mention somewhere - a pinnacle key exists whether or not the mechanic is
        // in this map - so the banner needs more than that before it interrupts anybody.
        if (ImGui.SliderInt("###preload-minfiles", ref minFiles, 1, 20, "at least %d files"))
        {
            PreloadChanged(_preloadSettings with { MinFiles = minFiles });
        }

        // The card is said once per area, so seeing a change to it otherwise means taking a
        // portal - a long way to go to find out whether a colour works.
        if (_showPreloadNow is not null)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("show it now"))
            {
                _showPreloadNow();
            }
        }

        // The same two-column table as the entity rules above, for the same reason - and so
        // the two lists close at the SAME right edge, instead of each ragged in its own way.
        if (preload.Rules.Count > 0
            && ImGui.BeginTable("##preload-rules", 2, ImGuiTableFlags.SizingFixedFit))
        {
            try
            {
                ImGui.TableSetupColumn("rule", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("controls");

                foreach (PreloadRule rule in preload.Rules)
                {
                    DrawPreloadRule(preload, rule);
                }
            }
            finally
            {
                ImGui.EndTable();
            }
        }

        ImGui.TextColored(
            DimText,
            preload.Rules.Count == 0
                ? "nothing looked for - add one from \"What is in this area\""
                : "add more from \"What is in this area\": search a path, then watch for it");
    }

    private void DrawPreloadRule(PreloadWatch preload, PreloadRule rule)
    {
        ImGui.PushID($"preload-{rule.PathContains}");
        ImGui.TableNextRow();
        ImGui.TableNextColumn();

        bool on = rule.Enabled;
        if (ImGui.Checkbox("###on", ref on))
        {
            ReplacePreload(preload, rule, rule with { Enabled = on });
        }

        ImGui.SameLine();
        ImGui.TextColored(on ? new Vector4(1f, 1f, 1f, 1f) : OffText, rule.Name);

        ImGui.SameLine();
        ImGui.TextColored(DimText, $"path has \"{rule.PathContains}\"");

        ImGui.TableNextColumn();

        // Font-relative rather than a pixel count, so "dangerous" still fits in the box
        // when the text is set larger.
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 7f);
        if (ImGui.BeginCombo("###weight", rule.Weight.ToString().ToLowerInvariant()))
        {
            foreach (PreloadWeight weight in Enum.GetValues<PreloadWeight>())
            {
                if (ImGui.Selectable(weight.ToString().ToLowerInvariant(), weight == rule.Weight))
                {
                    ReplacePreload(preload, rule, rule with { Weight = weight });
                }
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        bool alert = rule.Alert;
        if (ImGui.Checkbox("banner", ref alert))
        {
            ReplacePreload(preload, rule, rule with { Alert = alert });
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("x"))
        {
            preload.UseRules(preload.Rules.Where(known => known != rule));
            PreloadChanged(_preloadSettings);
        }

        ImGui.PopID();
    }

    private void ReplacePreload(PreloadWatch preload, PreloadRule was, PreloadRule now)
    {
        preload.UseRules(preload.Rules.Select(rule => rule == was ? now : rule));
        PreloadChanged(_preloadSettings);
    }

    /// <summary>Takes a change to the preload settings, and remembers to write it down.</summary>
    /// <remarks>
    /// The rules are read back off the watch rather than tracked here: it owns them, the other
    /// window adds to them, and a copy kept in this editor would be stale the moment somebody
    /// pressed "+ watch" on a path.
    /// </remarks>
    private void PreloadChanged(PreloadSettings settings)
    {
        _preloadSettings = settings;
        _unsaved = true;
    }

    private void DrawRule(AlertRule rule)
    {
        ImGui.PushID(rule.Name);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();

        bool on = rule.Enabled;
        if (ImGui.Checkbox("###on", ref on))
        {
            Replace(rule, rule with { Enabled = on });
        }

        ImGui.SameLine();
        ImGui.TextColored(on ? new Vector4(1f, 1f, 1f, 1f) : OffText, rule.Name);

        ImGui.SameLine();
        ImGui.TextColored(DimText, Describe(rule));

        ImGui.TableNextColumn();

        // Wide enough for its longest caption ("anywhere in the area") at the CURRENT text
        // size - the distance is the knob that decides whether a rule is useful, so its
        // label is the one that must never be the thing that gets clipped.
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 9f);
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
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 13.5f);
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
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 10.5f);
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

        if (_preload is not null && _savePreload is not null)
        {
            _savePreload(_preloadSettings with { Rules = _preload.Rules });
        }
    }
}
