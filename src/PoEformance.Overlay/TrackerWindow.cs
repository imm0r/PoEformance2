using System.Globalization;
using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.Components;
using PoEformance.Game.World;

namespace PoEformance.Overlay;

/// <summary>
/// The tracker's settings: lines to monsters, rings on dangerous ground, status icons.
/// </summary>
/// <remarks>
/// THE LIST OF LIVE BUFF NAMES IS NOT DECORATION, and it is the reason this is a tab rather
/// than a page in the configuration window. The names a status rule matches on are internal
/// spellings the game never shows anybody - "shocked_70", "stolen_mods_buff_70" - and they are
/// not written down anywhere this tool can read. The only way to learn the name of the thing
/// that just killed you is to have it on you and look, which means the editor has to be
/// reachable while playing and has to show what is on the player RIGHT NOW.
///
/// The same argument decides the ground rules: what a damaging patch of ground is called is
/// discovered from the Effects tab, so the two live beside each other.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class TrackerWindow
{
    private static readonly Vector4 DimText = OverlayInk.Quiet;
    private static readonly Vector4 WarnText = OverlayInk.Warn;
    private static readonly Vector4 GoodText = OverlayInk.Good;

    private const ImGuiColorEditFlags SwatchFlags =
        ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel | ImGuiColorEditFlags.AlphaBar
        | ImGuiColorEditFlags.AlphaPreviewHalf;

    private readonly Func<TrackerSettings> _read;
    private readonly Action<TrackerSettings> _write;
    private readonly Func<IconCache.Picture> _sheet;
    private readonly Func<AnimationNames> _animations;

    /// <summary>Which rule the sheet picker is choosing a tile for, when it is open.</summary>
    private string _picking = string.Empty;

    /// <summary>Which layout profile the combo is showing.</summary>
    private int _profile = 1;

    /// <param name="read">The settings as they stand. Read every frame - the layers share them.</param>
    /// <param name="write">
    /// Called with the changed settings, which applies them and writes them down. One callback
    /// rather than two: a setting applied and not saved is the one that disappears overnight,
    /// and every edit in here is a decision made once.
    /// </param>
    /// <param name="sheet">The icon sheet as loaded, so the editor can show what it is drawing.</param>
    /// <param name="animations">
    /// The animation table, for naming what is being read. A function rather than the table
    /// itself: it is loaded by the app and set on the overlay, and the two happen in that order.
    /// </param>
    public TrackerWindow(
        Func<TrackerSettings> read,
        Action<TrackerSettings> write,
        Func<IconCache.Picture> sheet,
        Func<AnimationNames> animations)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(animations);
        _read = read;
        _write = write;
        _sheet = sheet;
        _animations = animations;
    }

    /// <summary>Draws the tab's content.</summary>
    public void DrawTab(WorldSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        TrackerSettings settings = _read();

        DrawLines(settings);
        ImGui.Separator();
        DrawAim(settings, snapshot);
        ImGui.Separator();
        DrawGroundDanger(settings, snapshot);
        ImGui.Separator();
        DrawStatus(settings, snapshot);
    }

    // ── Where they are pointing ──────────────────────────────────────────────

    /// <summary>The aim rays, and what the monsters on screen are doing right now.</summary>
    /// <remarks>
    /// The live action list is the same idea as the buff names below it, and needed for the same
    /// reason: the animation table came from the PLAYER's own animations, and whether the ids
    /// mean the same thing on a monster is reasonable rather than proven. A list of what is
    /// actually being read - number beside name - is what makes a wrong table obvious instead of
    /// invisible.
    /// </remarks>
    private void DrawAim(TrackerSettings settings, WorldSnapshot snapshot)
    {
        if (!OverlayLayout.Subsection("Where they are pointing"))
        {
            return;
        }

        AimSettings aim = settings.AimOrDefault;

        bool on = aim.Enabled;
        if (OverlayLayout.Toggle("Draw a ray along each monster's facing", ref on))
        {
            _write(settings with { Aim = aim with { Enabled = on } });
        }

        // HOW IT WORKS goes in the tooltip; WHAT NOT TO TRUST stays on screen, and only while
        // the rays are actually being drawn. The caveat is the one thing here somebody could
        // get wrong in a way that costs them a death.
        OverlayLayout.Hint(
            "The game keeps no target - it aims by TURNING, so the facing is the aim as exactly"
            + " as the game has it. Drawing this makes the reader read a facing and an animation"
            + " per monster; watch the Read cost tab.");

        if (on)
        {
            OverlayLayout.Warning(
                "Not a promise of where a skill lands: many attacks lock direction at the start,"
                + " and homing ones ignore facing.");
        }

        bool acting = aim.OnlyWhileActing;
        if (OverlayLayout.Toggle("Only while doing something", ref acting))
        {
            _write(settings with { Aim = aim with { OnlyWhileActing = acting } });
        }

        OverlayLayout.Cell(1);

        bool turn = aim.ShowTurn;
        if (OverlayLayout.Toggle("Show the turn", ref turn))
        {
            _write(settings with { Aim = aim with { ShowTurn = turn } });
        }

        OverlayLayout.Cell(2);

        bool action = aim.ShowAction;
        if (OverlayLayout.Toggle("Name the action", ref action))
        {
            _write(settings with { Aim = aim with { ShowAction = action } });
        }

        OverlayLayout.Cell(3);

        bool player = aim.ShowPlayer;
        if (OverlayLayout.Toggle("And your own", ref player))
        {
            _write(settings with { Aim = aim with { ShowPlayer = player } });
        }

        float length = aim.Length;
        if (OverlayLayout.Narrow.Slider("##length", ref length, 5f, 400f, "%.0f world units"))
        {
            _write(settings with { Aim = aim with { Length = length } });
        }

        ImGui.SameLine();
        float thickness = aim.Thickness;
        if (OverlayLayout.Narrow.Slider("##thickness", ref thickness, 0.5f, 10f, "%.1f wide"))
        {
            _write(settings with { Aim = aim with { Thickness = thickness } });
        }

        ImGui.SameLine();
        Vector4 colour = ImGui.ColorConvertU32ToFloat4(OverlaySettings.ParseColour(aim.Colour));
        if (ImGui.ColorEdit4("##aimcolour", ref colour, SwatchFlags))
        {
            _write(settings with
            {
                Aim = aim with { Colour = OverlaySettings.FormatColour(ImGui.ColorConvertFloat4ToU32(colour)) },
            });
        }

        ImGui.SameLine();
        Vector4 turnColour = ImGui.ColorConvertU32ToFloat4(OverlaySettings.ParseColour(aim.TurnColour));
        if (ImGui.ColorEdit4("##turncolour", ref turnColour, SwatchFlags))
        {
            _write(settings with
            {
                Aim = aim with
                {
                    TurnColour = OverlaySettings.FormatColour(ImGui.ColorConvertFloat4ToU32(turnColour)),
                },
            });
        }

        ImGui.SameLine();
        ImGui.TextColored(DimText, "facing / turning");

        DrawActions(snapshot);
    }

    /// <summary>What the monsters on screen are doing, commonest first.</summary>
    private void DrawActions(WorldSnapshot snapshot)
    {
        var counts = new Dictionary<int, int>();
        int aimed = 0, turning = 0;
        foreach (WorldEntity entity in snapshot.Entities)
        {
            if (entity.Aim is not Aim aim)
            {
                continue;
            }

            aimed++;
            if (aim.IsTurning)
            {
                turning++;
            }

            if (aim.Animation >= 0)
            {
                counts[aim.Animation] = counts.GetValueOrDefault(aim.Animation) + 1;
            }
        }

        ImGui.TextColored(DimText, $"{aimed} entities have a facing this frame, {turning} of them mid-turn");

        if (counts.Count == 0)
        {
            ImGui.TextColored(
                DimText,
                aimed == 0
                    ? "nothing is being read - tick the box above and stand near something."
                    : "no animation ids came back - the Actor component did not read.");
            return;
        }

        if (!ImGui.BeginTable("##actions", 4, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg))
        {
            return;
        }

        try
        {
            ImGui.TableSetupColumn("how many");
            ImGui.TableSetupColumn("id");
            ImGui.TableSetupColumn("name");
            ImGui.TableSetupColumn("counts as");
            ImGui.TableHeadersRow();

            foreach ((int id, int count) in counts.OrderByDescending(entry => entry.Value).Take(12))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGuiText.Mono(count.ToString(CultureInfo.CurrentCulture));

                ImGui.TableNextColumn();
                ImGuiText.Mono(id.ToString(CultureInfo.CurrentCulture));

                ImGui.TableNextColumn();
                string? name = _animations().Of(id);
                if (name is null)
                {
                    ImGui.TextColored(WarnText, "(not in the table)");
                }
                else
                {
                    ImGui.TextUnformatted(ImGuiText.Escape(name));
                }

                // What the ray filter makes of it. The one column that says WHY a monster has
                // no ray while plainly doing something.
                ImGui.TableNextColumn();
                AnimationKind kind = _animations().KindOf(id);
                ImGui.TextColored(
                    kind is AnimationKind.Idle or AnimationKind.Moving ? DimText : GoodText,
                    kind.ToString().ToLowerInvariant());
            }
        }
        finally
        {
            ImGui.EndTable();
        }
    }

    // ── Lines to monsters ────────────────────────────────────────────────────

    /// <summary>
    /// Which rarities get a line. A GROUP rather than a fold, because it is one row.
    /// </summary>
    /// <remarks>
    /// A fold offers to hide something, and what it would hide here is a single line of three
    /// switches - so the header costs a click and a line of chrome to save one line. It only
    /// became worth saying once the folds stopped looking like the section around them: at that
    /// point a fold over one row reads as a fold that forgot what it was for.
    /// </remarks>
    private void DrawLines(TrackerSettings settings)
    {
        OverlayLayout.Group("Lines to monsters");

        MonsterLineSettings lines = settings.LinesOrDefault;

        // THREE SWITCHES ON ONE LINE. They are one setting asked three times - which rarities
        // get a line - so they belong in a row rather than stacked down the page with a
        // paragraph over them.
        const string why = "Drawn from your own feet, so the eye can follow one out of a crowd.";

        (bool unique, string uniqueColour) = Line("Unique", lines.Unique, lines.UniqueColour, why);

        OverlayLayout.Cell(1);
        (bool rare, string rareColour) = Line("Rare", lines.Rare, lines.RareColour, why);

        OverlayLayout.Cell(2);
        (bool magic, string magicColour) = Line("Magic", lines.Magic, lines.MagicColour, why);

        var wanted = new MonsterLineSettings(
            unique, rare, magic, uniqueColour, rareColour, magicColour);

        if (wanted != lines)
        {
            _write(settings with { Lines = wanted });
        }
    }

    /// <summary>One rarity's switch and colour, as they stand after the row was drawn.</summary>
    /// <remarks>
    /// THE TOOLTIP BELONGS TO EVERY ONE OF THESE, so it is asked for here rather than once
    /// after the first. Written outside, it hung off whichever control happened to be drawn
    /// last before it - the "Unique" label - so hovering "Rare" or "Magic" got nothing, and
    /// the one explanation covering all three switches was reachable from a third of them.
    ///
    /// THE WHOLE ROW IS THE TARGET, not the label alone. A BeginGroup/EndGroup pair makes the
    /// three controls one item as far as <c>IsItemHovered</c> is concerned, so the tooltip
    /// answers to the tick and the swatch as well - which is where a pointer actually goes.
    /// </remarks>
    /// <param name="hint">What the tooltip says. The same for every rarity.</param>
    private static (bool On, string Colour) Line(string label, bool on, string colour, string hint)
    {
        ImGui.PushID(label);
        try
        {
            ImGui.BeginGroup();

            bool wanted = on;
            ImGui.Checkbox("##on", ref wanted);

            ImGui.SameLine();
            Vector4 picked = ImGui.ColorConvertU32ToFloat4(OverlaySettings.ParseColour(colour));
            string wantedColour = ImGui.ColorEdit4("##colour", ref picked, SwatchFlags)
                ? OverlaySettings.FormatColour(ImGui.ColorConvertFloat4ToU32(picked))
                : colour;

            ImGui.SameLine();
            ImGui.TextUnformatted(label);

            ImGui.EndGroup();
            OverlayLayout.Hint(hint);

            return (wanted, wantedColour);
        }
        finally
        {
            ImGui.PopID();
        }
    }

    // ── Ground effects ───────────────────────────────────────────────────────

    /// <summary>
    /// The two hazards the game names itself - no rule to write, and no path to guess.
    /// </summary>
    /// <remarks>
    /// Above the rule list rather than below it, because this is the answer for anything it
    /// covers and the rules are the fallback for the rest. Both switches are OFF by default:
    /// they draw over the fight, and a person should choose that.
    /// </remarks>
    private void DrawKnownHazards(TrackerSettings settings)
    {
        bool ground = settings.ShowGroundEffects;
        if (OverlayLayout.Toggle("Ring every ground effect the game marks as one", ref ground))
        {
            _write(settings with { ShowGroundEffects = ground });
        }

        OverlayLayout.Hint(
            "No rule needed: this asks the entity whether it carries a GroundEffect component,"
            + " which is the game's own answer, and reads the countdown out of it.");

        // WHAT IT CANNOT DO stays on screen, because the way this fails is that a hazard has no
        // ring - which looks exactly like the feature being broken, and sends somebody looking
        // for a bug that is not there. Only while it is on: switched off, "it does not cover
        // everything" is not a caveat, it is noise.
        if (ground)
        {
            OverlayLayout.Warning(
                "Does not cover every hazard. A patch of fire with no ring is that, not a bug -"
                + " the rules below are still what covers it.");
            OverlayLayout.Hint(
                "Only two metadata paths have ever been seen carrying that component. The"
                + " GroundOnDeath monster mods carry none, and their paths are refused by the"
                + " noise filter's Daemon class before they are read at all.");
        }

        bool gameRadius = settings.GroundEffectUseGameRadius;
        if (OverlayLayout.Toggle("Size it from the component's own radius", ref gameRadius))
        {
            _write(settings with { GroundEffectUseGameRadius = gameRadius });
        }

        OverlayLayout.Hint(
            "The alternative is the fixed size below. Drawing this IS the experiment: if the ring"
            + " hugs the burning patch it is the radius, and if it does not, switch it off and"
            + " say so.");

        // The one caveat that has to be read BEFORE trusting what is on screen, so it cannot be
        // a tooltip: the number being drawn is a guess, and a ring drawn from a guess looks
        // exactly like a ring drawn from a measurement.
        if (gameRadius)
        {
            OverlayLayout.Warning(
                "That value is a CANDIDATE, not a measurement - a float in the right range that"
                + " nothing has confirmed.");
        }

        float radius = settings.GroundEffectRadius;
        if (OverlayLayout.Narrow.Slider("##groundradius", ref radius, 3f, 150f, "%.0f world units"))
        {
            _write(settings with { GroundEffectRadius = radius });
        }

        OverlayLayout.Hint(
            "WORLD units, not pixels: the ring is a circle on the floor, so it foreshortens and"
            + " shrinks with the camera. Used when the switch above is off.");

        ImGui.SameLine();
        Vector4 groundColour =
            ImGui.ColorConvertU32ToFloat4(OverlaySettings.ParseColour(settings.GroundEffectColour));
        if (ImGui.ColorEdit4("##groundcolour", ref groundColour, SwatchFlags))
        {
            _write(settings with
            {
                GroundEffectColour = OverlaySettings.FormatColour(ImGui.ColorConvertFloat4ToU32(groundColour)),
            });
        }

        ImGui.SameLine();
        bool timer = settings.ShowGroundEffectTimer;
        if (OverlayLayout.Toggle("Seconds left", ref timer))
        {
            _write(settings with { ShowGroundEffectTimer = timer });
        }

        OverlayLayout.Hint(
            "The number sits at 0.0 for a beat before the ring goes - the game's countdown"
            + " reaches zero a measured 0.38 s before it stops listing the effect. A third of"
            + " effects have no timer at all and simply show none.");

        bool labels = settings.ShowGroundEffectLabels;
        if (OverlayLayout.Toggle("Label each ring with its metadata path", ref labels))
        {
            _write(settings with { ShowGroundEffectLabels = labels });
        }

        OverlayLayout.Hint(
            "For working out which patch a ring belongs to, and whether one is MISSING. Prints"
            + " the path, the entity id, the radius being used and the countdown. A hazard the"
            + " game draws but this does not ring will have no label anywhere near it - that is"
            + " the case worth reporting.");

        bool beams = settings.ShowBeams;
        if (OverlayLayout.Toggle("Draw boss beams as the path they occupy", ref beams))
        {
            _write(settings with { ShowBeams = beams });
        }

        OverlayLayout.Hint(
            "Both ends come from the beam's own component, so this is the whole path rather than"
            + " a ring on one end of it - a beam runs up to eleven hundred world units, and the"
            + " chevrons run towards the end that matters.");

        float beamThickness = settings.BeamThickness;
        if (OverlayLayout.Narrow.Slider("##beamthickness", ref beamThickness, 2f, 60f, "%.0f px wide"))
        {
            _write(settings with { BeamThickness = beamThickness });
        }

        OverlayLayout.Hint(
            "The component has no thickness field that survived checking, so a band drawn to look"
            + " like a danger zone would be an invention.");

        ImGui.SameLine();
        Vector4 beamColour = ImGui.ColorConvertU32ToFloat4(OverlaySettings.ParseColour(settings.BeamColour));
        if (ImGui.ColorEdit4("##beamcolour", ref beamColour, SwatchFlags))
        {
            _write(settings with
            {
                BeamColour = OverlaySettings.FormatColour(ImGui.ColorConvertFloat4ToU32(beamColour)),
            });
        }

        // The width is a slider, so it invites being turned up until the band looks like the
        // danger zone - and it is not one. That has to be readable while the beams are drawn,
        // not hidden under a pointer.
        if (beams)
        {
            OverlayLayout.Warning("The width is decoration. Only the line down its middle is measured.");
        }
    }

    private void DrawGroundDanger(TrackerSettings settings, WorldSnapshot snapshot)
    {
        if (!OverlayLayout.Subsection("Dangerous ground"))
        {
            return;
        }

        DrawKnownHazards(settings);

        ImGui.Separator();

        bool on = settings.ShowGroundDanger;
        if (OverlayLayout.Toggle("Ring the ground effects below", ref on))
        {
            _write(settings with { ShowGroundDanger = on });
        }

        OverlayLayout.Hint(
            "The radius is in SCREEN PIXELS, not world units - it does not shrink as the camera"
            + " pulls back. The Effects tab lists what IS being read, with paths: copy the start"
            + " of one into a rule.");

        // The reason this draws nothing while being configured correctly. It stays on screen
        // rather than going in the tooltip because it is the answer to "it is not working",
        // and somebody asking that has already stopped hovering things - but only while the
        // rings are switched on, since off is a perfectly good reason for nothing to appear.
        if (on)
        {
            OverlayLayout.Warning(
                "Nothing appearing? The reader drops hostile ground effects unless the Effects"
                + " tab's \"keep them\" is on, and the noise filter refuses the engine's /fx/ and"
                + " /mat/ nodes before they are read at all.");
        }

        List<GroundDangerRule> rules = [.. settings.GroundDangerOrDefault];
        bool edited = false;
        int removed = -1;

        // A table, like the alert lists: every control in its own column, so the rows line up
        // and stay lined up when the thickness slider comes and goes with "filled" - laid out
        // by hand, that slider vanishing shifted everything after it sideways per row. The
        // path takes the stretch column, so it soaks up the width at every text size.
        if (rules.Count > 0 && ImGui.BeginTable("##ground-rules", 7, ImGuiTableFlags.SizingFixedFit))
        {
            try
            {
                ImGui.TableSetupColumn("on");
                ImGui.TableSetupColumn("colour");
                ImGui.TableSetupColumn("filled");
                ImGui.TableSetupColumn("radius");
                ImGui.TableSetupColumn("thickness");
                ImGui.TableSetupColumn("delete");
                ImGui.TableSetupColumn("path", ImGuiTableColumnFlags.WidthStretch);

                for (int i = 0; i < rules.Count; i++)
                {
                    ImGui.PushID($"ground{i}");
                    try
                    {
                        GroundDangerRule rule = rules[i];
                        ImGui.TableNextRow();

                        ImGui.TableNextColumn();
                        bool enabled = rule.Enabled;
                        if (ImGui.Checkbox("##on", ref enabled))
                        {
                            rules[i] = rule with { Enabled = enabled };
                            edited = true;
                        }

                        ImGui.TableNextColumn();
                        Vector4 colour = ImGui.ColorConvertU32ToFloat4(OverlaySettings.ParseColour(rule.Colour));
                        if (ImGui.ColorEdit4("##colour", ref colour, SwatchFlags))
                        {
                            rules[i] = rule with
                            {
                                Colour = OverlaySettings.FormatColour(ImGui.ColorConvertFloat4ToU32(colour)),
                            };
                            edited = true;
                        }

                        ImGui.TableNextColumn();
                        bool filled = rule.Filled;
                        if (ImGui.Checkbox("filled", ref filled))
                        {
                            rules[i] = rule with { Filled = filled };
                            edited = true;
                        }

                        ImGui.TableNextColumn();
                        int radius = rule.Radius;
                        if (OverlayLayout.Narrow.Slider("##radius", ref radius, 10, 400, "%d px"))
                        {
                            rules[i] = rule with { Radius = radius };
                            edited = true;
                        }

                        // The cell exists whether or not the slider does, which is what keeps
                        // the columns from shifting when "filled" hides it.
                        ImGui.TableNextColumn();
                        if (!rule.Filled)
                        {
                            int thickness = rule.Thickness;
                            if (OverlayLayout.Narrow.Slider("##weight", ref thickness, 1, 5, "%d wide"))
                            {
                                rules[i] = rule with { Thickness = thickness };
                                edited = true;
                            }
                        }

                        ImGui.TableNextColumn();
                        if (ImGui.SmallButton("x"))
                        {
                            // Noted rather than removed here: a return from inside the table
                            // would leave it unended, and removing mid-loop shifts the ids of
                            // every row after it while their controls are still live.
                            removed = i;
                        }

                        ImGui.TableNextColumn();
                        ImGui.SetNextItemWidth(-float.Epsilon);
                        string path = rule.Path;
                        if (ImGui.InputText("##path", ref path, 256))
                        {
                            rules[i] = rule with { Path = path };
                            edited = true;
                        }
                    }
                    finally
                    {
                        ImGui.PopID();
                    }
                }
            }
            finally
            {
                ImGui.EndTable();
            }
        }

        if (removed >= 0)
        {
            rules.RemoveAt(removed);
            edited = true;
        }

        if (ImGui.Button("add a ground rule"))
        {
            rules.Add(new GroundDangerRule("Metadata/Effects/Spells/ground_effects/"));
            edited = true;
        }

        ImGui.SameLine();
        ImGui.TextColored(
            DimText,
            $"{Ringed(settings, snapshot)} entities in this area match a rule right now");

        if (edited)
        {
            _write(settings with { GroundDanger = rules });
        }
    }

    /// <summary>How many entities the rules are currently ringing.</summary>
    /// <remarks>
    /// The one number that separates "my rule is wrong" from "there is nothing here", which
    /// are the two explanations for an empty screen and look identical from the outside.
    /// </remarks>
    private static int Ringed(TrackerSettings settings, WorldSnapshot snapshot)
    {
        int found = 0;
        foreach (WorldEntity entity in snapshot.Entities)
        {
            if (!entity.IsRemembered && settings.GroundDangerOrDefault.Any(rule => rule.Matches(entity)))
            {
                found++;
            }
        }

        return found;
    }

    // ── Status effects ───────────────────────────────────────────────────────

    private void DrawStatus(TrackerSettings settings, WorldSnapshot snapshot)
    {
        if (!OverlayLayout.Subsection("Status effects"))
        {
            return;
        }

        bool player = settings.ShowPlayerStatus;
        if (OverlayLayout.Toggle("Over the player", ref player))
        {
            _write(settings with { ShowPlayerStatus = player });
        }

        OverlayLayout.Cell(1);
        bool monsters = settings.ShowMonsterStatus;
        if (OverlayLayout.Toggle("Over rare and unique monsters", ref monsters))
        {
            _write(settings with { ShowMonsterStatus = monsters });
        }

        // On the switch that carries it rather than under the pair: this is the only setting in
        // here that makes the READER do more work per monster, and it is the monster half that
        // does it - a note under both says it of both.
        OverlayLayout.Hint(
            "Makes the reader read a Buffs component per rare-or-better monster; watch the Read"
            + " cost tab.");

        // Titled rules between the blocks, like the alerts tab: five near-identical control
        // rows in a stack read as one that lost its order, and a hairline does not say where
        // one subject ends.
        OverlayLayout.Group("The icon sheet");
        DrawSheet(settings);

        OverlayLayout.Group("Where the rows sit");
        DrawLayout(settings);
        DrawTextSettings(settings);

        OverlayLayout.Group("On the player");
        DrawRules(settings, settings.PlayerStatusOrDefault, "player",
            rules => settings with { PlayerStatus = rules });

        OverlayLayout.Group("On monsters");
        DrawRules(settings, settings.MonsterStatusOrDefault, "monster",
            rules => settings with { MonsterStatus = rules });

        ImGui.Spacing();
        DrawLiveNames(snapshot);
    }

    /// <summary>The sheet path, and what actually loaded from it.</summary>
    private void DrawSheet(TrackerSettings settings)
    {
        string path = settings.IconSheet;
        if (OverlayLayout.Input("icon sheet", ref path, 512))
        {
            _write(settings with { IconSheet = path });
        }

        OverlayLayout.Next();
        int tile = settings.IconTile;
        if (OverlayLayout.Narrow.Number("tile", ref tile, 8))
        {
            _write(settings with { IconTile = Math.Clamp(tile, 1, 512) });
        }

        if (settings.IconSheet.Length == 0)
        {
            OverlayLayout.Note("No sheet - each effect is drawn as its coloured disc with its own caption.");
            return;
        }

        IconCache.Picture sheet = _sheet();
        if (!sheet.Ready)
        {
            OverlayLayout.Warning("That file did not load - see the Appearance tab for the reason.");
            return;
        }

        int columns = Math.Max(1, sheet.Width / settings.IconTile);
        int rows = Math.Max(1, sheet.Height / settings.IconTile);
        OverlayLayout.Note(
            $"Loaded {sheet.Width}x{sheet.Height}, which is {columns} x {rows} tiles of {settings.IconTile}px.");

        // A sheet that is not a whole number of tiles across is the symptom BOTH of a wrong
        // tile size and of a sheet so large it was shrunk on the way in - and either way every
        // icon lands part way between two of them, which reads as the coordinates being wrong.
        if (sheet.Width % settings.IconTile != 0 || sheet.Height % settings.IconTile != 0)
        {
            OverlayLayout.Warning(
                $"Not a whole number of {settings.IconTile}px tiles - either the tile size is wrong,"
                + $" or the sheet is over {IconCache.MaxSheetEdge}px and was shrunk to fit.");
        }
    }

    /// <summary>Where the rows sit, and the profiles that put them there.</summary>
    private void DrawLayout(TrackerSettings settings)
    {
        float screen = ImGui.GetIO().DisplaySize.Y;
        string[] names = [.. StatusLayoutProfile.All.Select(p => p.Name)];

        OverlayLayout.Narrow.Combo("##profile", ref _profile, names);

        ImGui.SameLine();
        if (ImGui.Button("use this profile"))
        {
            StatusLayoutProfile chosen = StatusLayoutProfile.All[Math.Clamp(_profile, 0, names.Length - 1)];
            _write(settings with
            {
                PlayerLayout = chosen.Player,
                MonsterLayout = chosen.Monster,
                LayoutChosen = true,
            });
        }

        ImGui.SameLine();
        if (ImGui.Button("detect from this screen"))
        {
            _profile = StatusLayoutProfile.All.ToList().IndexOf(StatusLayoutProfile.ForHeight(screen));
        }

        ImGui.SameLine();
        ImGui.TextColored(DimText, $"screen is {(int)ImGui.GetIO().DisplaySize.X}x{(int)screen}");

        StatusIconLayout playerLayout = settings.PlayerLayout ?? StatusLayoutProfile.ForHeight(screen).Player;
        StatusIconLayout monsterLayout = settings.MonsterLayout ?? StatusLayoutProfile.ForHeight(screen).Monster;

        if (Layout("player row", playerLayout, -200, 400) is StatusIconLayout newPlayer)
        {
            _write(settings with { PlayerLayout = newPlayer, LayoutChosen = true });
        }

        if (Layout("monster row", monsterLayout, -400, 400) is StatusIconLayout newMonster)
        {
            _write(settings with { MonsterLayout = newMonster, LayoutChosen = true });
        }
    }

    /// <summary>One row's offsets. Returns what it should become, or null when untouched.</summary>
    private static StatusIconLayout? Layout(string label, StatusIconLayout layout, int lowest, int highest)
    {
        ImGui.PushID(label);
        try
        {
            StatusIconLayout? changed = null;

            int x = layout.X;
            if (OverlayLayout.Narrow.Slider("##x", ref x, -400, 400, "across %d"))
            {
                changed = layout with { X = x };
            }

            ImGui.SameLine();
            int y = layout.Y;
            if (OverlayLayout.Narrow.Slider("##y", ref y, lowest, highest, "down %d"))
            {
                changed = layout with { Y = y };
            }

            ImGui.SameLine();
            int gap = layout.Gap;
            if (OverlayLayout.Narrow.Slider("##gap", ref gap, 0, 50, "gap %d"))
            {
                changed = layout with { Gap = gap };
            }

            ImGui.SameLine();
            ImGui.TextUnformatted(label);
            return changed;
        }
        finally
        {
            ImGui.PopID();
        }
    }

    /// <summary>The shadow, the timer plate, and where the two small numbers sit.</summary>
    private void DrawTextSettings(TrackerSettings settings)
    {
        float alpha = settings.ShadowAlpha;
        if (OverlayLayout.Narrow.Slider("##shadow", ref alpha, 0f, 1f, "shadow %.2f"))
        {
            _write(settings with { ShadowAlpha = alpha });
        }

        ImGui.SameLine();
        int size = settings.ShadowSize;
        if (OverlayLayout.Narrow.Slider("##shadowsize", ref size, 0, 2, "spread %d"))
        {
            _write(settings with { ShadowSize = size });
        }

        ImGui.SameLine();
        Vector4 back = ImGui.ColorConvertU32ToFloat4(OverlaySettings.ParseColour(settings.BarBackColour));
        if (ImGui.ColorEdit4("##barback", ref back, SwatchFlags))
        {
            _write(settings with
            {
                BarBackColour = OverlaySettings.FormatColour(ImGui.ColorConvertFloat4ToU32(back)),
            });
        }

        ImGui.SameLine();
        ImGui.TextColored(DimText, "timer bar background");

        int chargesX = settings.ChargesX;
        if (OverlayLayout.Narrow.Slider("##chargesx", ref chargesX, -64, 64, "stacks x %d"))
        {
            _write(settings with { ChargesX = chargesX });
        }

        ImGui.SameLine();
        int chargesY = settings.ChargesY;
        if (OverlayLayout.Narrow.Slider("##chargesy", ref chargesY, -64, 64, "stacks y %d"))
        {
            _write(settings with { ChargesY = chargesY });
        }

        ImGui.SameLine();
        int timerX = settings.TimerX;
        if (OverlayLayout.Narrow.Slider("##timerx", ref timerX, -64, 64, "timer x %d"))
        {
            _write(settings with { TimerX = timerX });
        }

        ImGui.SameLine();
        int timerY = settings.TimerY;
        if (OverlayLayout.Narrow.Slider("##timery", ref timerY, -64, 64, "timer y %d"))
        {
            _write(settings with { TimerY = timerY });
        }
    }

    /// <summary>One list of status rules, with the sheet picker beneath whichever is picking.</summary>
    private void DrawRules(
        TrackerSettings settings,
        IReadOnlyList<StatusIconRule> current,
        string id,
        Func<IReadOnlyList<StatusIconRule>, TrackerSettings> rebuild)
    {
        List<StatusIconRule> rules = [.. current];
        bool edited = false;
        int removed = -1;

        // The same table the ground rules use, for the same reasons: aligned columns at any
        // text size, and the name soaking up the width in the stretch column.
        if (rules.Count > 0 && ImGui.BeginTable($"##status-rules-{id}", 9, ImGuiTableFlags.SizingFixedFit))
        {
            try
            {
                ImGui.TableSetupColumn("on");
                ImGui.TableSetupColumn("bar");
                ImGui.TableSetupColumn("text");
                ImGui.TableSetupColumn("tile");
                ImGui.TableSetupColumn("pick");
                ImGui.TableSetupColumn("scale");
                ImGui.TableSetupColumn("delete");
                ImGui.TableSetupColumn("label");
                ImGui.TableSetupColumn("name", ImGuiTableColumnFlags.WidthStretch);

                for (int i = 0; i < rules.Count; i++)
                {
                    string key = $"{id}{i}";
                    ImGui.PushID(key);
                    try
                    {
                        StatusIconRule rule = rules[i];
                        ImGui.TableNextRow();

                        ImGui.TableNextColumn();
                        bool enabled = rule.Enabled;
                        if (ImGui.Checkbox("##on", ref enabled))
                        {
                            rules[i] = rule with { Enabled = enabled };
                            edited = true;
                        }

                        ImGui.TableNextColumn();
                        Vector4 bar = ImGui.ColorConvertU32ToFloat4(OverlaySettings.ParseColour(rule.BarColour));
                        if (ImGui.ColorEdit4("##bar", ref bar, SwatchFlags))
                        {
                            rules[i] = rule with
                            {
                                BarColour = OverlaySettings.FormatColour(ImGui.ColorConvertFloat4ToU32(bar)),
                            };
                            edited = true;
                        }

                        ImGui.TableNextColumn();
                        Vector4 text = ImGui.ColorConvertU32ToFloat4(OverlaySettings.ParseColour(rule.TextColour));
                        if (ImGui.ColorEdit4("##text", ref text, SwatchFlags))
                        {
                            rules[i] = rule with
                            {
                                TextColour = OverlaySettings.FormatColour(ImGui.ColorConvertFloat4ToU32(text)),
                            };
                            edited = true;
                        }

                        ImGui.TableNextColumn();
                        DrawTilePreview(settings, rule);

                        ImGui.TableNextColumn();
                        if (ImGui.SmallButton(_picking == key ? "picking" : "pick"))
                        {
                            _picking = _picking == key ? string.Empty : key;
                        }

                        ImGui.TableNextColumn();
                        float scale = rule.IconScale;
                        if (OverlayLayout.Narrow.Slider("##scale", ref scale, 0.2f, 4f, "x%.2f"))
                        {
                            rules[i] = rule with { IconScale = scale };
                            edited = true;
                        }

                        ImGui.TableNextColumn();
                        if (ImGui.SmallButton("x"))
                        {
                            // Noted rather than removed here - see the ground rules.
                            removed = i;
                        }

                        ImGui.TableNextColumn();
                        string label = rule.Label;
                        if (OverlayLayout.Narrow.Input("##label", ref label, 64))
                        {
                            rules[i] = rule with { Label = label };
                            edited = true;
                        }

                        ImGui.TableNextColumn();
                        ImGui.SetNextItemWidth(-float.Epsilon);
                        string name = rule.Name;
                        if (ImGui.InputText("##name", ref name, 128))
                        {
                            rules[i] = rule with { Name = name };
                            edited = true;
                        }
                    }
                    finally
                    {
                        ImGui.PopID();
                    }
                }
            }
            finally
            {
                ImGui.EndTable();
            }
        }

        if (removed >= 0)
        {
            rules.RemoveAt(removed);
            edited = true;
        }

        // BELOW the table rather than inside the picking row's cell: the picker is a
        // full-width scrolling child, and a table cell is the one place that cannot hold it.
        for (int i = 0; i < rules.Count; i++)
        {
            if (_picking != $"{id}{i}")
            {
                continue;
            }

            if (PickTile(settings, rules[i]) is StatusIconRule picked)
            {
                rules[i] = picked;
                _picking = string.Empty;
                edited = true;
            }

            break;
        }

        if (ImGui.Button($"add a rule##{id}"))
        {
            rules.Add(new StatusIconRule("", "new"));
            edited = true;
        }

        if (edited)
        {
            _write(rebuild(rules));
        }
    }

    /// <summary>The tile a rule is pointing at, at a size a row can hold.</summary>
    private void DrawTilePreview(TrackerSettings settings, StatusIconRule rule)
    {
        IconCache.Picture sheet = _sheet();
        if (!sheet.Ready)
        {
            ImGui.TextColored(DimText, "no sheet");
            return;
        }

        float across = (float)settings.IconTile / sheet.Width;
        float down = (float)settings.IconTile / sheet.Height;
        var uv0 = new Vector2(Math.Max(0, rule.IconColumn) * across, Math.Max(0, rule.IconRow) * down);
        ImGui.Image(sheet.Texture, new Vector2(18f, 18f), uv0, uv0 + new Vector2(across, down));

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                string.Create(CultureInfo.CurrentCulture, $"column {rule.IconColumn}, row {rule.IconRow}"));
        }
    }

    /// <summary>
    /// The whole sheet, to click a tile out of. Returns the rule with the clicked tile on it.
    /// </summary>
    /// <remarks>
    /// A SCROLLING CHILD rather than the sheet drawn straight into the tab: a status icon sheet
    /// is a tall strip - the reference's is 256 wide and 3392 down - and drawn at its own size
    /// it would be a tab nobody can reach the bottom of.
    /// </remarks>
    private StatusIconRule? PickTile(TrackerSettings settings, StatusIconRule rule)
    {
        IconCache.Picture sheet = _sheet();
        if (!sheet.Ready)
        {
            ImGui.TextColored(WarnText, "there is no icon sheet to pick from - set one above.");
            return null;
        }

        ImGui.TextColored(DimText, "click a tile:");
        if (!ImGui.BeginChild("##picker", new Vector2(0f, 260f), ImGuiChildFlags.Borders))
        {
            ImGui.EndChild();
            return null;
        }

        StatusIconRule? picked = null;
        try
        {
            Vector2 origin = ImGui.GetCursorScreenPos();
            ImGui.Image(sheet.Texture, new Vector2(sheet.Width, sheet.Height));

            if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                Vector2 clicked = ImGui.GetMousePos() - origin;
                int columns = Math.Max(1, sheet.Width / settings.IconTile);
                int rows = Math.Max(1, sheet.Height / settings.IconTile);

                picked = rule with
                {
                    IconColumn = Math.Clamp((int)(clicked.X / settings.IconTile), 0, columns - 1),
                    IconRow = Math.Clamp((int)(clicked.Y / settings.IconTile), 0, rows - 1),
                };
            }
        }
        finally
        {
            ImGui.EndChild();
        }

        return picked;
    }

    /// <summary>
    /// What is on the player and on the monsters right now, by the game's own names.
    /// </summary>
    /// <remarks>
    /// The half of this tab that makes the rest usable. A rule matches on an internal spelling
    /// nobody is shown anywhere else, so without this the only way to write one is to guess -
    /// and a rule that matches nothing looks exactly like a feature that does not work.
    /// </remarks>
    private static void DrawLiveNames(WorldSnapshot snapshot)
    {
        if (!OverlayLayout.Subsection("What is on things right now"))
        {
            return;
        }

        ImGuiText.Wrapped(DimText, "copy a name into a rule above - matching is loose, so a fragment works.");

        Names("on you", snapshot.PlayerBuffs);

        int shown = 0;
        foreach (WorldEntity monster in snapshot.Entities)
        {
            if (monster.Kind != EntityKind.Monster || monster.Buffs is null || monster.Buffs.All.Count == 0)
            {
                continue;
            }

            Names(monster.ShortName, monster.Buffs);
            if (++shown >= 4)
            {
                break;
            }
        }

        if (shown == 0)
        {
            ImGuiText.Wrapped(
                DimText,
                "no monster buffs are being read - tick \"over rare and unique monsters\" above and"
                + " stand next to one.");
        }
    }

    /// <summary>One thing's buffs, with what the drawing needs from each.</summary>
    private static void Names(string who, ActiveBuffs? buffs)
    {
        if (buffs is null || buffs.All.Count == 0)
        {
            ImGui.TextColored(DimText, $"{who}: nothing");
            return;
        }

        ImGui.TextUnformatted($"{who}:");
        foreach (ActiveBuff buff in buffs.All)
        {
            ImGui.TextColored(
                DimText,
                string.Create(
                    CultureInfo.CurrentCulture,
                    $"    {ImGuiText.Escape(buff.Name)}   {buff.TimeLeft:F1}s of {buff.TotalTime:F1}s"
                    + $"{(buff.Charges > 0 ? $"   x{buff.Charges}" : string.Empty)}"));
        }
    }
}
