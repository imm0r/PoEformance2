using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.Entities;

namespace PoEformance.Overlay;

/// <summary>
/// The effect reference book: projectiles, animated effects and ground effects, and their models.
/// </summary>
/// <remarks>
/// THE ITEM BOOK'S PARTS with an effect in the pane. An effect's .ao is the same file format as a
/// monster's, so the portrait draws it - and plays it, where it names a skeleton - with nothing
/// added but a line naming the shader graphs its materials use: the evidence for how they blend,
/// which the renderer does not act on yet. See MaterialFile.Parents.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class EffectBookWindow : BookWindow<EffectBook>
{
    /// <summary>
    /// The fields the rail offers, and what it calls them.
    /// </summary>
    /// <remarks>
    /// THE ONE WITH A HANDFUL OF ANSWERS: projectile, animated or ground. Names and files have
    /// thousands of values and are typed.
    /// </remarks>
    private static readonly (string Label, string Field)[] Offered =
    [
        ("Kind", "kind"),
    ];

    /// <summary>A projectile's row is inked, so the three kinds read apart at a glance.</summary>
    private static readonly Vector4 ProjectileInk = OverlayInk.Name;

    private readonly Func<EffectVisuals> _table;

    private EffectVisuals _of = EffectVisuals.Empty;
    private EffectBook _page = EffectBook.Empty;

    /// <summary>Which of the effect's .ao files the pane shows - an index into its Files.</summary>
    private int _file;

    /// <summary>Whose files that index is into, so a new effect starts at its first.</summary>
    private string _fileOf = string.Empty;

    /// <summary>What the portrait is handed, made once per choice rather than once per frame.</summary>
    private MonsterVariety? _subject;
    private string _subjectKey = string.Empty;

    public EffectBookWindow(Func<EffectVisuals> table)
        : base("effect", EffectBook.Empty, 0.18f, 0.45f)
    {
        ArgumentNullException.ThrowIfNull(table);
        _table = table;
    }

    /// <inheritdoc/>
    protected override string Caption => "Search for any effect with a model";

    /// <inheritdoc/>
    protected override string Grammar => "fireball  ·  kind:projectile  ·  model:ice  ·  files>1";

    /// <inheritdoc/>
    protected override string Noun => "effects";

    /// <inheritdoc/>
    protected override IReadOnlyList<(string Label, string Field)> Rails => Offered;

    /// <summary>Works the table into columns, once per table - see <see cref="BookWindow{TBook}.Current"/>.</summary>
    protected override EffectBook Current()
    {
        EffectVisuals all = _table();
        if (!ReferenceEquals(_of, all))
        {
            _of = all;
            _page = EffectBook.Of(all);
        }

        return _page;
    }

    /// <inheritdoc/>
    protected override string WhyEmpty()
        => _of.Say.Count > 0
            ? _of.Say[0]
            : "No effect table yet - it is read from the install shortly after start-up.";

    /// <inheritdoc/>
    protected override Vector4? Ink(int row) => Page.Projectiles[row] ? ProjectileInk : null;

    /// <summary>What the effect is and which of its files is drawn, then the model under it.</summary>
    protected override bool PaneUnder => true;

    /// <summary>
    /// The bar under the list: a line that says what the effect is - its name, kind, file and how its model is doing - and a line of every switch.
    /// </summary>
    /// <remarks>
    /// INFORMATION ABOVE, SWITCHES BELOW, as the item book's bar - the layout drawn for both from the live client.
    /// </remarks>
    protected override void Pane()
    {
        if (Chosen.Length == 0 || _of.Find(Chosen) is not { } one || one.Files.Count == 0)
        {
            ImGui.TextDisabled("Choose an effect above.");
            return;
        }

        // THE ROW'S FILES ARE ITS VARIANTS AND ITS STATES - several art versions of one projectile,
        // what it looks like stuck in a wall or bouncing - so the choice is offered rather than one
        // picked for it. PER EFFECT: "stuck" chosen on one projectile is not a choice about the
        // next one, whose third file is something else, so a new effect opens on its first.
        if (!string.Equals(_fileOf, one.Path, StringComparison.Ordinal))
        {
            _fileOf = one.Path;
            _file = 0;
        }

        _file = Math.Clamp(_file, 0, one.Files.Count - 1);
        string ao = one.Files[_file].Ao;
        string key = one.Path + "|" + ao;
        if (!string.Equals(key, _subjectKey, StringComparison.Ordinal))
        {
            _subjectKey = key;
            _subject = new MonsterVariety(Name: one.Name, AoFiles: [ao]);
        }

        Model?.Show(_subject, _subjectKey, one.Name);

        // THE LINE OF INFORMATION: the file's name, its whole path on hover - the path alone ran past the bar.
        ImGui.TextUnformatted(one.Name);
        ImGui.SameLine();
        ImGui.TextDisabled(ImGuiText.Escape(one.Kind));
        ImGui.SameLine();
        ImGui.TextDisabled(ImGuiText.Escape(ao[(ao.Replace('\\', '/').LastIndexOf('/') + 1)..]));
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(ImGuiText.Escape(ao));
        }

        if (Model is { } shown)
        {
            ImGui.SameLine();
            shown.DrawState();
        }

        // THE LINE OF SWITCHES, the file picker first where there is a choice.
        if (one.Files.Count > 1)
        {
            ImGui.SetNextItemWidth(MathF.Min(ImGui.GetContentRegionAvail().X, ImGui.GetFontSize() * 16f));
            if (ImGui.BeginCombo("##effect-file", ImGuiText.Escape(one.Files[_file].Label)))
            {
                for (var at = 0; at < one.Files.Count; at++)
                {
                    if (ImGui.Selectable(ImGuiText.Escape(one.Files[at].Label) + "##" + at, at == _file))
                    {
                        _file = at;
                    }
                }

                ImGui.EndCombo();
            }

            ImGui.SameLine();
        }

        if (Model is { } model && model.DrawWindowSwitch())
        {
            ImGui.SameLine();
            ModelRow();
        }
    }
}
