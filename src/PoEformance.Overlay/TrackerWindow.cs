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
    private static readonly Vector4 DimText = OverlayTheme.Quiet;
    private static readonly Vector4 WarnText = new(1f, 0.7f, 0.35f, 1f);
    private static readonly Vector4 GoodText = new(0.55f, 0.9f, 0.6f, 1f);

    private const ImGuiColorEditFlags SwatchFlags =
        ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel | ImGuiColorEditFlags.AlphaBar
        | ImGuiColorEditFlags.AlphaPreviewHalf;

    private readonly Func<TrackerSettings> _read;
    private readonly Action<TrackerSettings> _write;
    private readonly Func<IconCache.Picture> _sheet;

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
    public TrackerWindow(
        Func<TrackerSettings> read, Action<TrackerSettings> write, Func<IconCache.Picture> sheet)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(sheet);
        _read = read;
        _write = write;
        _sheet = sheet;
    }

    /// <summary>Draws the tab's content.</summary>
    public void DrawTab(WorldSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        TrackerSettings settings = _read();

        DrawLines(settings);
        ImGui.Separator();
        DrawGroundDanger(settings, snapshot);
        ImGui.Separator();
        DrawStatus(settings, snapshot);
    }

    // ── Lines to monsters ────────────────────────────────────────────────────

    private void DrawLines(TrackerSettings settings)
    {
        if (!ImGui.CollapsingHeader("Lines to monsters", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        MonsterLineSettings lines = settings.LinesOrDefault;

        ImGui.TextColored(DimText, "drawn from your own feet, so the eye can follow one out of a crowd.");

        (bool unique, string uniqueColour) = Line("unique", lines.Unique, lines.UniqueColour);
        (bool rare, string rareColour) = Line("rare", lines.Rare, lines.RareColour);
        (bool magic, string magicColour) = Line("magic", lines.Magic, lines.MagicColour);

        var wanted = new MonsterLineSettings(
            unique, rare, magic, uniqueColour, rareColour, magicColour);

        if (wanted != lines)
        {
            _write(settings with { Lines = wanted });
        }
    }

    /// <summary>One rarity's switch and colour, as they stand after the row was drawn.</summary>
    private static (bool On, string Colour) Line(string label, bool on, string colour)
    {
        ImGui.PushID(label);
        try
        {
            bool wanted = on;
            ImGui.Checkbox("##on", ref wanted);

            ImGui.SameLine();
            Vector4 picked = ImGui.ColorConvertU32ToFloat4(OverlaySettings.ParseColour(colour));
            string wantedColour = ImGui.ColorEdit4("##colour", ref picked, SwatchFlags)
                ? OverlaySettings.FormatColour(ImGui.ColorConvertFloat4ToU32(picked))
                : colour;

            ImGui.SameLine();
            ImGui.TextUnformatted(label);
            return (wanted, wantedColour);
        }
        finally
        {
            ImGui.PopID();
        }
    }

    // ── Ground effects ───────────────────────────────────────────────────────

    private void DrawGroundDanger(TrackerSettings settings, WorldSnapshot snapshot)
    {
        if (!ImGui.CollapsingHeader("Dangerous ground"))
        {
            return;
        }

        bool on = settings.ShowGroundDanger;
        if (ImGui.Checkbox("ring the ground effects below", ref on))
        {
            _write(settings with { ShowGroundDanger = on });
        }

        // The two reasons this draws nothing while being configured correctly, said HERE
        // rather than left to be discovered. Both are deliberate rules elsewhere in the
        // reader, and neither is guessable from an overlay that stays empty.
        ImGui.TextColored(
            DimText,
            "    the radius is in SCREEN PIXELS, not world units - it does not shrink as the camera pulls back.");
        ImGui.TextColored(
            WarnText,
            "    nothing appears? the reader drops hostile ground effects unless the Effects tab's"
            + " \"keep them\" is on,");
        ImGui.TextColored(
            WarnText,
            "    and the noise filter refuses the engine's /fx/ and /mat/ nodes before they are read at all.");
        ImGui.TextColored(
            DimText,
            "    the Effects tab lists what IS being read, with paths - copy the start of one into a rule.");

        List<GroundDangerRule> rules = [.. settings.GroundDangerOrDefault];
        bool edited = false;

        for (int i = 0; i < rules.Count; i++)
        {
            ImGui.PushID($"ground{i}");
            try
            {
                GroundDangerRule rule = rules[i];

                bool enabled = rule.Enabled;
                if (ImGui.Checkbox("##on", ref enabled))
                {
                    rules[i] = rule with { Enabled = enabled };
                    edited = true;
                }

                ImGui.SameLine();
                Vector4 colour = ImGui.ColorConvertU32ToFloat4(OverlaySettings.ParseColour(rule.Colour));
                if (ImGui.ColorEdit4("##colour", ref colour, SwatchFlags))
                {
                    rules[i] = rule with
                    {
                        Colour = OverlaySettings.FormatColour(ImGui.ColorConvertFloat4ToU32(colour)),
                    };
                    edited = true;
                }

                ImGui.SameLine();
                bool filled = rule.Filled;
                if (ImGui.Checkbox("filled", ref filled))
                {
                    rules[i] = rule with { Filled = filled };
                    edited = true;
                }

                ImGui.SameLine();
                ImGui.SetNextItemWidth(90f);
                int radius = rule.Radius;
                if (ImGui.SliderInt("##radius", ref radius, 10, 400, "%d px"))
                {
                    rules[i] = rule with { Radius = radius };
                    edited = true;
                }

                if (!rule.Filled)
                {
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(70f);
                    int thickness = rule.Thickness;
                    if (ImGui.SliderInt("##weight", ref thickness, 1, 5, "%d wide"))
                    {
                        rules[i] = rule with { Thickness = thickness };
                        edited = true;
                    }
                }

                ImGui.SameLine();
                if (ImGui.SmallButton("x"))
                {
                    rules.RemoveAt(i);
                    _write(settings with { GroundDanger = rules });
                    return;
                }

                ImGui.SameLine();
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 10f);
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
        if (!ImGui.CollapsingHeader("Status effects"))
        {
            return;
        }

        bool player = settings.ShowPlayerStatus;
        if (ImGui.Checkbox("over the player", ref player))
        {
            _write(settings with { ShowPlayerStatus = player });
        }

        ImGui.SameLine();
        bool monsters = settings.ShowMonsterStatus;
        if (ImGui.Checkbox("over rare and unique monsters", ref monsters))
        {
            _write(settings with { ShowMonsterStatus = monsters });
        }

        // Said next to the switch that carries it, like the projectile tab's warning: this is
        // the only setting in here that makes the READER do more work per monster.
        ImGui.TextColored(
            DimText,
            "    the monster half makes the reader read a Buffs component per rare-or-better"
            + " monster; watch the Read cost tab.");

        DrawSheet(settings);
        DrawLayout(settings);
        DrawTextSettings(settings);

        ImGui.Separator();
        ImGui.TextUnformatted("on the player");
        DrawRules(settings, settings.PlayerStatusOrDefault, "player",
            rules => settings with { PlayerStatus = rules });

        ImGui.Separator();
        ImGui.TextUnformatted("on monsters");
        DrawRules(settings, settings.MonsterStatusOrDefault, "monster",
            rules => settings with { MonsterStatus = rules });

        ImGui.Separator();
        DrawLiveNames(snapshot);
    }

    /// <summary>The sheet path, and what actually loaded from it.</summary>
    private void DrawSheet(TrackerSettings settings)
    {
        ImGui.SetNextItemWidth(360f);
        string path = settings.IconSheet;
        if (ImGui.InputText("icon sheet", ref path, 512))
        {
            _write(settings with { IconSheet = path });
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(90f);
        int tile = settings.IconTile;
        if (ImGui.InputInt("tile", ref tile, 8, 32))
        {
            _write(settings with { IconTile = Math.Clamp(tile, 1, 512) });
        }

        if (settings.IconSheet.Length == 0)
        {
            ImGui.TextColored(
                DimText,
                "    no sheet: each effect is drawn as its coloured disc with its own caption on it.");
            return;
        }

        IconCache.Picture sheet = _sheet();
        if (!sheet.Ready)
        {
            ImGui.TextColored(WarnText, "    that file did not load - see the Appearance tab for the reason.");
            return;
        }

        int columns = Math.Max(1, sheet.Width / settings.IconTile);
        int rows = Math.Max(1, sheet.Height / settings.IconTile);
        ImGui.TextColored(
            GoodText,
            $"    loaded: {sheet.Width}x{sheet.Height}, which is {columns} x {rows} tiles of {settings.IconTile}px");

        // A sheet that is not a whole number of tiles across is the symptom BOTH of a wrong
        // tile size and of a sheet so large it was shrunk on the way in - and either way every
        // icon lands part way between two of them, which reads as the coordinates being wrong.
        if (sheet.Width % settings.IconTile != 0 || sheet.Height % settings.IconTile != 0)
        {
            ImGui.TextColored(
                WarnText,
                $"    that is not a whole number of {settings.IconTile}px tiles - either the tile size is"
                + $" wrong, or the sheet is over {IconCache.MaxSheetEdge}px and was shrunk to fit.");
        }
    }

    /// <summary>Where the rows sit, and the profiles that put them there.</summary>
    private void DrawLayout(TrackerSettings settings)
    {
        float screen = ImGui.GetIO().DisplaySize.Y;
        string[] names = [.. StatusLayoutProfile.All.Select(p => p.Name)];

        ImGui.SetNextItemWidth(120f);
        ImGui.Combo("##profile", ref _profile, names, names.Length);

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

            ImGui.SetNextItemWidth(110f);
            int x = layout.X;
            if (ImGui.SliderInt("##x", ref x, -400, 400, "across %d"))
            {
                changed = layout with { X = x };
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(110f);
            int y = layout.Y;
            if (ImGui.SliderInt("##y", ref y, lowest, highest, "down %d"))
            {
                changed = layout with { Y = y };
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(90f);
            int gap = layout.Gap;
            if (ImGui.SliderInt("##gap", ref gap, 0, 50, "gap %d"))
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
        ImGui.SetNextItemWidth(110f);
        float alpha = settings.ShadowAlpha;
        if (ImGui.SliderFloat("##shadow", ref alpha, 0f, 1f, "shadow %.2f"))
        {
            _write(settings with { ShadowAlpha = alpha });
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(90f);
        int size = settings.ShadowSize;
        if (ImGui.SliderInt("##shadowsize", ref size, 0, 2, "spread %d"))
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

        ImGui.SetNextItemWidth(100f);
        int chargesX = settings.ChargesX;
        if (ImGui.SliderInt("##chargesx", ref chargesX, -64, 64, "stacks x %d"))
        {
            _write(settings with { ChargesX = chargesX });
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(100f);
        int chargesY = settings.ChargesY;
        if (ImGui.SliderInt("##chargesy", ref chargesY, -64, 64, "stacks y %d"))
        {
            _write(settings with { ChargesY = chargesY });
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(100f);
        int timerX = settings.TimerX;
        if (ImGui.SliderInt("##timerx", ref timerX, -64, 64, "timer x %d"))
        {
            _write(settings with { TimerX = timerX });
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(100f);
        int timerY = settings.TimerY;
        if (ImGui.SliderInt("##timery", ref timerY, -64, 64, "timer y %d"))
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

        for (int i = 0; i < rules.Count; i++)
        {
            string key = $"{id}{i}";
            ImGui.PushID(key);
            try
            {
                StatusIconRule rule = rules[i];

                bool enabled = rule.Enabled;
                if (ImGui.Checkbox("##on", ref enabled))
                {
                    rules[i] = rule with { Enabled = enabled };
                    edited = true;
                }

                ImGui.SameLine();
                Vector4 bar = ImGui.ColorConvertU32ToFloat4(OverlaySettings.ParseColour(rule.BarColour));
                if (ImGui.ColorEdit4("##bar", ref bar, SwatchFlags))
                {
                    rules[i] = rule with
                    {
                        BarColour = OverlaySettings.FormatColour(ImGui.ColorConvertFloat4ToU32(bar)),
                    };
                    edited = true;
                }

                ImGui.SameLine();
                Vector4 text = ImGui.ColorConvertU32ToFloat4(OverlaySettings.ParseColour(rule.TextColour));
                if (ImGui.ColorEdit4("##text", ref text, SwatchFlags))
                {
                    rules[i] = rule with
                    {
                        TextColour = OverlaySettings.FormatColour(ImGui.ColorConvertFloat4ToU32(text)),
                    };
                    edited = true;
                }

                ImGui.SameLine();
                DrawTilePreview(settings, rule);

                ImGui.SameLine();
                if (ImGui.SmallButton(_picking == key ? "picking" : "pick"))
                {
                    _picking = _picking == key ? string.Empty : key;
                }

                ImGui.SameLine();
                ImGui.SetNextItemWidth(70f);
                float scale = rule.IconScale;
                if (ImGui.SliderFloat("##scale", ref scale, 0.2f, 4f, "x%.2f"))
                {
                    rules[i] = rule with { IconScale = scale };
                    edited = true;
                }

                ImGui.SameLine();
                if (ImGui.SmallButton("x"))
                {
                    rules.RemoveAt(i);
                    _write(rebuild(rules));
                    return;
                }

                ImGui.SameLine();
                ImGui.SetNextItemWidth(120f);
                string label = rule.Label;
                if (ImGui.InputText("##label", ref label, 64))
                {
                    rules[i] = rule with { Label = label };
                    edited = true;
                }

                ImGui.SameLine();
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 10f);
                string name = rule.Name;
                if (ImGui.InputText("##name", ref name, 128))
                {
                    rules[i] = rule with { Name = name };
                    edited = true;
                }

                if (_picking == key && PickTile(settings, rules[i]) is StatusIconRule picked)
                {
                    rules[i] = picked;
                    _picking = string.Empty;
                    _write(rebuild(rules));
                    return;
                }
            }
            finally
            {
                ImGui.PopID();
            }
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
        if (!ImGui.CollapsingHeader("what is on things right now"))
        {
            return;
        }

        ImGui.TextColored(DimText, "copy a name into a rule above - matching is loose, so a fragment works.");

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
            ImGui.TextColored(
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
