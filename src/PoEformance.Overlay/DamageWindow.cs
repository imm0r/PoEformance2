using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.Components;

namespace PoEformance.Overlay;

/// <summary>
/// How much damage is being done, and to what.
/// </summary>
/// <remarks>
/// The headline is on the status readout, where it can be seen while fighting. This is for
/// the questions an instant cannot answer: which of these is actually taking it, how much of
/// the figure is watched rather than judged, and what the best moment of the map was.
///
/// The split between watched and judged is shown rather than hidden, because it is the one
/// number here that is a decision rather than a measurement - see <see cref="DamageMeter"/>.
/// Somebody who does not trust it can turn it off and watch what the figure does.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DamageWindow
{
    private static readonly Vector4 DimText = new(0.62f, 0.65f, 0.72f, 1f);
    private static readonly Vector4 DpsText = new(1f, 1f, 0.6f, 1f);
    private static readonly Vector4 JudgedText = new(0.85f, 0.75f, 0.5f, 1f);

    // Dimmer than the confident credit, on purpose: this row is the least-known part of the
    // figure, and it should not read as loudly as the part that was watched.
    private static readonly Vector4 SoftText = new(0.78f, 0.62f, 0.42f, 1f);

    private readonly DamageMeter _meter;
    private readonly Func<long> _clock;

    /// <param name="clock">
    /// The same monotonic clock the reader stamps with, so "how long since this was hit"
    /// compares two readings of one clock rather than two different ones.
    /// </param>
    public DamageWindow(DamageMeter meter, Func<long>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(meter);
        _meter = meter;
        _clock = clock ?? (() => Environment.TickCount64);
    }

    /// <summary>Draws the tab's content.</summary>
    public void DrawTab()
    {
        ImGui.TextColored(DpsText, $"{Number(_meter.Dps)} dps");
        ImGui.SameLine();
        ImGui.TextColored(DimText, $"   peak {Number(_meter.Peak)}   this area {Number(_meter.Total)} total");

        ImGui.Separator();

        // WHERE the figure came from, split three ways by how much each part is really
        // KNOWN. A damage number that does not say how much of itself is inferred is a
        // number nobody can check, and on a build that one-shots packs the inferred part is
        // the majority - so the split is the difference between a figure and a claim.
        ImGui.TextColored(DimText, $"watched:   {Number(_meter.Observed)}  off {_meter.Hurt} monsters' health");

        // Nearly certain: something was hurting it, and then it was gone.
        ImGui.TextColored(
            JudgedText,
            $"credited:  {Number(_meter.CreditedHurt)}  from ones we were already hurting");

        // The half that rests entirely on the assumption - and the half a monster that
        // merely walked off would land in.
        ImGui.TextColored(
            SoftText,
            ImGuiText.Escape(
                $"  untouched: {Number(_meter.CreditedUntouched)}  vanished without a scratch seen"
                + $"   ({Share(_meter.CreditedUntouched, _meter.Total)} of the total)"));

        if (_meter.WithheldCount > 0)
        {
            ImGui.TextColored(
                DimText,
                $"  refused:   {Number(_meter.Withheld)}  from {_meter.WithheldCount} that vanished"
                + " too far away to have been killed");
        }

        DrawFurthest();

        bool counting = _meter.CountKills;
        if (ImGui.Checkbox("count what vanished  (off leaves only health seen to fall)", ref counting))
        {
            _meter.CountKills = counting;
        }

        // The reason the switch above is not merely a preference: a dead monster is dropped
        // from the snapshot rather than lingering at zero, so the last chunk of every kill -
        // and the whole of anything deleted between two reads - is never watched falling.
        if (!counting)
        {
            ImGui.TextColored(
                DimText,
                "    with this off the figure is only what was watched, which under-reports"
                + " by the whole of every killing blow.");
        }
        else
        {
            // In SCREENS rather than world units, because that is the unit the judgement is
            // made in - "nothing is killed two screens away" is a statement somebody can
            // agree or disagree with, and "nothing is killed 3826 units away" is not.
            ImGui.SetNextItemWidth(220f);
            float screens = _meter.CreditWithin / DamageMeter.ScreenWorldUnits;
            if (ImGui.SliderFloat("only within", ref screens, 0f, 6f,
                    screens <= 0f ? "any distance" : "%.1f screens"))
            {
                _meter.CreditWithin = screens * DamageMeter.ScreenWorldUnits;
            }

            ImGui.SameLine();
            ImGui.TextColored(DimText, "of where it was last seen");
        }

        ImGui.SetNextItemWidth(220f);
        float smoothing = _meter.SmoothingSeconds;
        if (ImGui.SliderFloat("smoothing", ref smoothing, 0.1f, 5f, "%.2f s"))
        {
            _meter.SmoothingSeconds = smoothing;
        }

        ImGui.Separator();
        DrawTargets();
    }

    /// <summary>
    /// How far away the furthest disappearance was - the check on the whole assumption.
    /// </summary>
    /// <remarks>
    /// Says what the number MEANS rather than just printing it, because it is read once to
    /// settle a question and then never again: a figure that stays inside fighting range is
    /// evidence that everything which went missing died, and one that runs to several
    /// screens is evidence that the game drops distant monsters and the gate is doing real
    /// work. Left unexplained it is just another row nobody knows what to do with.
    /// </remarks>
    private void DrawFurthest()
    {
        if (_meter.Vanished <= 0 || _meter.FurthestVanish < 0f)
        {
            return;
        }

        float screens = _meter.FurthestVanish / DamageMeter.ScreenWorldUnits;
        float gate = _meter.CreditWithin / DamageMeter.ScreenWorldUnits;

        // ONE LINE WHILE NOTHING HAS BEEN REFUSED, because then the two figures are the same
        // event and printing them as two rows invites them to disagree about it - which they
        // did: 1.45 screens was called "beyond killing range" on one row and "clear of the
        // limit" on the next, because the first was judged against a screen and the second
        // against the limit. Two references for one number is how a readout contradicts
        // itself, and a reader who has to work out which row to believe has been given less
        // than one row would have been.
        if (_meter.WithheldCount == 0)
        {
            ImGui.TextColored(
                DimText,
                $"  furthest disappearance: {screens:0.00} screens - every one counted, none refused"
                + (gate > 0f && screens < gate
                    ? $"; the {gate:0.0}-screen limit is above all of them and is doing nothing"
                    : string.Empty));

            return;
        }

        // Both, once the limit has actually refused something - now they are different
        // events and the gap between them is the finding. The limit's job is to sit in that
        // gap: the counted figure well below it means it separates two populations, and the
        // two pressed together means it is cutting through the middle of one.
        ImGui.TextColored(
            SoftText,
            $"  furthest disappearance: {screens:0.00} screens - refused, so beyond the limit");

        if (_meter.FurthestCounted < 0f)
        {
            return;
        }

        float counted = _meter.FurthestCounted / DamageMeter.ScreenWorldUnits;
        bool roomy = gate <= 0f || counted <= gate * 0.75f;

        ImGui.TextColored(
            roomy ? DimText : SoftText,
            $"  furthest one counted:   {counted:0.00} screens"
            + (roomy
                ? "  - clear of the limit, so it is not cutting into kills"
                : "  - close to the limit; it may be refusing real kills, so try widening it"));
    }

    private void DrawTargets()
    {
        IReadOnlyList<DamageTarget> targets = _meter.Targets(_clock());

        if (targets.Count == 0)
        {
            ImGui.TextColored(
                DimText,
                _meter.Measuring
                    ? "nothing being hit right now"
                    : "nothing measured yet - the figures start with the first monster that takes damage");
            return;
        }

        if (!ImGui.BeginTable("damage-targets", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            return;
        }

        try
        {
            ImGui.TableSetupColumn("target", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("left", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("dps", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableHeadersRow();

            foreach (DamageTarget target in targets)
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                // Escaped, because this one is not a line somebody wrote - it is the
                // monster's own name out of the game, and a mod called "100% Increased"
                // would be read as a format specifier.
                ImGui.TextColored(Tint(target.Rarity), ImGuiText.Escape(target.Name));

                ImGui.TableNextColumn();
                ImGui.TextColored(DimText, target.Percent >= 0 ? $"{target.Percent}%%" : "-");

                ImGui.TableNextColumn();
                ImGui.TextColored(DpsText, Number(target.Dps));
            }
        }
        finally
        {
            ImGui.EndTable();
        }
    }

    /// <summary>The game's own rarity colours, so a pack leader reads the same as everywhere else.</summary>
    private static Vector4 Tint(ItemRarity rarity) => rarity switch
    {
        ItemRarity.Unique => new Vector4(0.68f, 0.37f, 0.13f, 1f),
        ItemRarity.Rare => new Vector4(1f, 1f, 0.46f, 1f),
        ItemRarity.Magic => new Vector4(0.53f, 0.53f, 1f, 1f),
        _ => new Vector4(0.85f, 0.85f, 0.85f, 1f),
    };

    /// <summary>What share one part is of the whole, for reading the split at a glance.</summary>
    private static string Share(long part, long whole)
        => whole <= 0 ? "-" : $"{100d * part / whole:0}%";

    /// <summary>
    /// A damage figure short enough to read at a glance.
    /// </summary>
    /// <remarks>
    /// Path of Exile 2 damage runs to seven figures, and the digits past the first three are
    /// noise on a number that changes every frame - "1.4M" answers the question and "1438291"
    /// makes it be worked out.
    /// </remarks>
    public static string Number(double value)
    {
        double magnitude = Math.Abs(value);

        return magnitude >= 1_000_000_000 ? $"{value / 1_000_000_000:0.##}B"
            : magnitude >= 1_000_000 ? $"{value / 1_000_000:0.##}M"
            : magnitude >= 1_000 ? $"{value / 1_000:0.#}k"
            : $"{value:0}";
    }
}
