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

    /// <summary>
    /// Draws the tab's content, as four pages rather than one long scroll.
    /// </summary>
    /// <remarks>
    /// FOUR SUBJECTS, AND READING ONE MEANT SCROLLING PAST THREE. This tool had five folds
    /// stacked down a page that did not fit a screen at the smallest text size, and folding
    /// did not help: everything under those folds belongs to this one tool, so folded they
    /// were no nearer and unfolded they were a scroll. Tabs cut the page to the subject being
    /// worked on - see <see cref="OverlayLayout.Tabs"/>.
    ///
    /// THE FOURTH IS NOT A SETTING. What is on the player right now is the thing that makes
    /// the other three usable, because a status rule matches on an internal spelling nobody is
    /// shown anywhere else. It was the last fold at the bottom of the longest page in the
    /// tool; it is a tab of its own now, and clicking a name there writes the rule rather than
    /// asking somebody to copy it upwards.
    /// </remarks>
    public void DrawTab(WorldSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        TrackerSettings settings = _read();

        OverlayLayout.Tabs(
            "tracker-parts",
            ("Monsters & facing", () =>
            {
                DrawLines(settings);
                ImGui.Spacing();
                DrawAim(settings, snapshot);
            }),
            ("Hazards & ground", () => DrawGroundDanger(settings, snapshot)),
            ("Status effects", () => DrawStatus(settings)),
            ("Live inspector", () => DrawLiveNames(settings, snapshot)));
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
        OverlayLayout.Group("Facing Vectors");

        AimSettings aim = settings.AimOrDefault;

        bool on = aim.Enabled;
        if (OverlayLayout.Toggle("Draw Facing Ray", ref on))
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
        if (OverlayLayout.Toggle("Active Only", ref acting))
        {
            _write(settings with { Aim = aim with { OnlyWhileActing = acting } });
        }

        OverlayLayout.Cell(1);

        bool turn = aim.ShowTurn;
        if (OverlayLayout.Toggle("Show Turn", ref turn))
        {
            _write(settings with { Aim = aim with { ShowTurn = turn } });
        }

        OverlayLayout.Cell(2);

        bool action = aim.ShowAction;
        if (OverlayLayout.Toggle("Show Action", ref action))
        {
            _write(settings with { Aim = aim with { ShowAction = action } });
        }

        OverlayLayout.Cell(3);

        bool player = aim.ShowPlayer;
        if (OverlayLayout.Toggle("Include Self", ref player))
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
        OverlayLayout.Group("Lines to Monsters");

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
        if (OverlayLayout.Toggle("Ring Marked Hazards", ref ground))
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
        if (OverlayLayout.Toggle("Use Component Radius", ref gameRadius))
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
        if (OverlayLayout.Toggle("Seconds Left", ref timer))
        {
            _write(settings with { ShowGroundEffectTimer = timer });
        }

        OverlayLayout.Hint(
            "The number sits at 0.0 for a beat before the ring goes - the game's countdown"
            + " reaches zero a measured 0.38 s before it stops listing the effect. A third of"
            + " effects have no timer at all and simply show none.");

        ImGui.SameLine();
        bool names = settings.ShowGroundEffectNames;
        if (OverlayLayout.Toggle("Name Beside Ring", ref names))
        {
            _write(settings with { ShowGroundEffectNames = names });
        }

        OverlayLayout.Hint(
            "The game's own name for the kind of ground - \"Ignited Ground\", \"Sacred Ashes\" -"
            + " written to the RIGHT of the ring so it does not cover the patch. It comes from"
            + " the row index the component carries, resolved against the game's own table, so"
            + " it is the phrase already on your screen rather than anything invented here.");

        bool labels = settings.ShowGroundEffectLabels;
        if (OverlayLayout.Toggle("Show Metadata Path", ref labels))
        {
            _write(settings with { ShowGroundEffectLabels = labels });
        }

        OverlayLayout.Hint(
            "For working out which patch a ring belongs to, and whether one is MISSING. Prints"
            + " the path, the entity id, the radius being used and the countdown. A hazard the"
            + " game draws but this does not ring will have no label anywhere near it - that is"
            + " the case worth reporting.");

        bool beams = settings.ShowBeams;
        if (OverlayLayout.Toggle("Draw Boss Beams", ref beams))
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
        DrawKnownHazards(settings);

        OverlayLayout.Group("Custom Ground Filter Rules");

        bool on = settings.ShowGroundDanger;
        if (OverlayLayout.Toggle("Ring What the Rules Below Match", ref on))
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
        if (rules.Count > 0 && ImGui.BeginTable(
                "##ground-rules", 7, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Resizable))
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

        if (ImGui.Button("Add a Ground Rule"))
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

    private void DrawStatus(TrackerSettings settings)
    {
        bool player = settings.ShowPlayerStatus;
        if (OverlayLayout.Toggle("Over the Player", ref player))
        {
            _write(settings with { ShowPlayerStatus = player });
        }

        OverlayLayout.Cell(1);
        bool monsters = settings.ShowMonsterStatus;
        if (OverlayLayout.Toggle("Rares \u0026 Uniques Only", ref monsters))
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
        OverlayLayout.Group("The Icon Sheet");
        DrawSheet(settings);

        OverlayLayout.Group("Where the Rows Sit");
        DrawLayout(settings);
        DrawTextSettings(settings);

        OverlayLayout.Group("On the Player");
        DrawRules(settings, settings.PlayerStatusOrDefault, "player",
            rules => settings with { PlayerStatus = rules });

        OverlayLayout.Group("On Monsters");
        DrawRules(settings, settings.MonsterStatusOrDefault, "monster",
            rules => settings with { MonsterStatus = rules });
    }

    /// <summary>The sheet path, and what actually loaded from it.</summary>
    private void DrawSheet(TrackerSettings settings)
    {
        string path = settings.IconSheet;
        if (OverlayLayout.Input("Icon Sheet", ref path, 512))
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
        if (ImGui.Button("Use This Profile"))
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
        if (ImGui.Button("Detect from This Screen"))
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

        // A 2x2 GRID, not a row of four. These are two offsets - where the stack count sits and
        // where the timer sits - each with an x and a y, and a row of four puts "stacks y" and
        // "timer x" side by side as though they were a pair. Two rows of two says which two
        // numbers belong together, and each column is one axis: the x of one is directly above
        // the x of the other, which is how you nudge both the same way.
        int chargesX = settings.ChargesX;
        if (OverlayLayout.Narrow.Slider("##chargesx", ref chargesX, -64, 64, "stacks x %d"))
        {
            _write(settings with { ChargesX = chargesX });
        }

        OverlayLayout.Cell(1);
        int chargesY = settings.ChargesY;
        if (OverlayLayout.Narrow.Slider("##chargesy", ref chargesY, -64, 64, "stacks y %d"))
        {
            _write(settings with { ChargesY = chargesY });
        }

        int timerX = settings.TimerX;
        if (OverlayLayout.Narrow.Slider("##timerx", ref timerX, -64, 64, "timer x %d"))
        {
            _write(settings with { TimerX = timerX });
        }

        OverlayLayout.Cell(1);
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
        if (rules.Count > 0 && ImGui.BeginTable(
                $"##status-rules-{id}", 9, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Resizable))
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

        if (ImGui.Button($"Add a Rule##{id}"))
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
    /// What is on the player and on the monsters right now - and a click writes the rule.
    /// </summary>
    /// <remarks>
    /// THE HALF OF THIS TAB THAT MAKES THE REST USABLE. A status rule matches on an internal
    /// spelling nobody is shown anywhere else - "shocked_70", "stolen_mods_buff_70" - so
    /// without this list the only way to write one is to guess, and a rule that matches
    /// nothing looks exactly like a feature that does not work.
    ///
    /// SO CLICKING A NAME WRITES THE RULE. This was a wall of text under an instruction to
    /// "copy a name into a rule above" - which meant reading a name off one part of the page,
    /// scrolling to another, adding an empty rule and typing the name back in from memory,
    /// with every chance to mistype a string that has to match exactly enough. The names are
    /// on screen and the rule list is one call away; asking a person to be the clipboard
    /// between them is work the tool can do.
    ///
    /// TWO PANELS SIDE BY SIDE rather than one column, because the two lists answer different
    /// questions - what is on ME, and what is on THEM - and a click means a different thing in
    /// each: one writes a player rule, the other a monster rule. Stacked, that difference
    /// rests on which heading somebody last scrolled past.
    /// </remarks>
    private void DrawLiveNames(TrackerSettings settings, WorldSnapshot snapshot)
    {
        OverlayLayout.Note("Click an entry to add it as a rule. Matching is loose, so a fragment works.");

        // Half the width each, less the gap between them. Zero height fills what is left, so
        // the two grow with the window rather than at a height chosen here.
        float half = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f;

        DrawBuffPanel(
            "##live-player", "On you", new Vector2(half, 0f), settings.PlayerStatusOrDefault,
            name => _write(_read() with { PlayerStatus = [.. _read().PlayerStatusOrDefault, new StatusIconRule(name)] }),
            panel => panel.Add(0, snapshot.PlayerBuffs is { All.Count: > 0 } ? "you" : string.Empty, snapshot.PlayerBuffs));

        ImGui.SameLine();

        DrawBuffPanel(
            "##live-monsters", "On monsters", new Vector2(half, 0f), settings.MonsterStatusOrDefault,
            name => _write(_read() with { MonsterStatus = [.. _read().MonsterStatusOrDefault, new StatusIconRule(name)] }),
            panel =>
            {
                int shown = 0;
                foreach (WorldEntity monster in snapshot.Entities)
                {
                    if (monster.Kind != EntityKind.Monster || monster.Buffs is null || monster.Buffs.All.Count == 0)
                    {
                        continue;
                    }

                    panel.Add(shown, monster.ShortName, monster.Buffs);
                    if (++shown >= 4)
                    {
                        break;
                    }
                }

                if (shown == 0)
                {
                    OverlayLayout.Note(
                        "Nothing being read - tick \"Rares & uniques only\" on the Status effects tab"
                        + " and stand next to one.");
                }
            });
    }

    /// <summary>One side of the inspector: a bordered list of what is on something.</summary>
    /// <param name="already">The rules that exist, so a name already covered says so.</param>
    /// <param name="add">Writes a new rule for the name that was clicked.</param>
    /// <param name="fill">Puts the things being listed into the panel.</param>
    private static void DrawBuffPanel(
        string id,
        string title,
        Vector2 size,
        IReadOnlyList<StatusIconRule> already,
        Action<string> add,
        Action<BuffPanel> fill)
    {
        if (!ImGui.BeginChild(id, size, ImGuiChildFlags.Borders))
        {
            ImGui.EndChild();
            return;
        }

        try
        {
            OverlayLayout.Group(title);
            fill(new BuffPanel(already, add));
        }
        finally
        {
            // In a finally and unconditionally: EndChild pairs with BeginChild whatever it
            // returned, and an exception between the two leaves ImGui's stack unbalanced.
            ImGui.EndChild();
        }
    }

    /// <summary>
    /// One panel while it is being filled: what is listed, and what a click on it does.
    /// </summary>
    /// <remarks>
    /// A small type rather than four parameters threaded through, because the two sides differ
    /// only in what they list and what a click writes - and passing "the thing a click adds a
    /// rule to" as a loose delegate beside a loose list is how the player's names end up
    /// writing a monster rule.
    /// </remarks>
    private readonly struct BuffPanel(IReadOnlyList<StatusIconRule> already, Action<string> add)
    {
        /// <summary>
        /// Lists one thing's buffs, each of them clickable.
        /// </summary>
        /// <remarks>
        /// THE SAME BUFF APPEARS MORE THAN ONCE. The game lists an instance per source, so a
        /// character running two auras that both grant it shows "arcane_surge" twice, and a
        /// reservation buff can appear three times over. That is a real reading and not a
        /// duplicate to filter out - but it means a row's ImGui id cannot come from the buff's
        /// NAME, because two rows would then share one id. ImGui says so out loud, in a popup
        /// over the panel, and the second row of a colliding pair stops responding to clicks.
        ///
        /// So the id is the POSITION, here and per row: the slot this list occupies in the
        /// panel, and the index of the row within it. Neither can repeat, whatever the game
        /// lists - and two monsters of the same name in one panel would have collided the same
        /// way if the outer scope had come from ShortName.
        /// </remarks>
        /// <param name="slot">Which list this is within its panel. Unique per panel.</param>
        public void Add(int slot, string who, ActiveBuffs? buffs)
        {
            if (buffs is null || buffs.All.Count == 0)
            {
                if (who.Length > 0)
                {
                    OverlayLayout.Note("Nothing on it.");
                }

                return;
            }

            ImGui.PushID(slot);
            try
            {
                if (who.Length > 0)
                {
                    ImGui.TextUnformatted(ImGuiText.Escape(who));
                }

                for (int i = 0; i < buffs.All.Count; i++)
                {
                    Row(i, buffs.All[i]);
                }
            }
            finally
            {
                ImGui.PopID();
            }
        }

        /// <summary>One buff: its name, what the drawing needs, and a click that rules it.</summary>
        /// <param name="at">The row's position, which is its id - see the note on Add.</param>
        private void Row(int at, ActiveBuff buff)
        {
            // A rule already covering this name means clicking again would add a duplicate
            // that can never be told from the first. Said rather than silently refused: a
            // click that does nothing reads as a broken list.
            bool have = false;
            foreach (StatusIconRule rule in already)
            {
                if (rule.Matches(buff.Name))
                {
                    have = true;
                    break;
                }
            }

            ImGui.PushID(at);
            try
            {
                // Selectable rather than text, so the whole row is the target and it lights up
                // under the pointer - which is what says "this does something" without a word.
                if (ImGui.Selectable(ImGuiText.Escape(buff.Name), have) && !have)
                {
                    add(buff.Name);
                }

                if (!have)
                {
                    OverlayLayout.Hint("Click to add a rule for this.");
                }

                ImGui.SameLine();
                ImGuiText.Mono(
                    OverlayInk.Quiet,
                    string.Create(
                        CultureInfo.CurrentCulture,
                        $"{buff.TimeLeft:F1}s of {buff.TotalTime:F1}s"
                        + $"{(buff.Charges > 0 ? $"   x{buff.Charges}" : string.Empty)}"));
            }
            finally
            {
                ImGui.PopID();
            }
        }
    }
}
