using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.Components;
using PoEformance.Game.World;

namespace PoEformance.Overlay;

/// <summary>
/// Draws the buffs and debuffs worth watching over the thing carrying them.
/// </summary>
/// <remarks>
/// PORTED FROM the GameHelper2 Tracker plugin's StatusEffectLogic: a row of icons over each
/// monster and under the player, each with the stack count above it and a draining timer bar
/// below. The game shows none of this for a monster and shows the player's as a strip of
/// identical squares at the edge of the screen, which is the wrong place - what a curse's
/// remaining time is worth knowing at is the moment you are deciding whether to re-cast it, and
/// that decision is made looking at the monster.
///
/// TWO THINGS ARE READ RATHER THAN GUESSED AT, and the difference from the reference is worth
/// recording. The plugin reaches for a stack count and a total duration BY REFLECTION - it
/// tries "Charges", "Charge", "Stacks", "StackCount" in turn and gives up at 0 - because
/// GameHelper's own status effect object does not promise them. Here both are fields of a
/// struct this project decodes itself (<see cref="ActiveBuff"/>), so the timer bar's proportion
/// is the game's own figure rather than a full bar standing in for an unknown one.
///
/// THE ICON SHEET IS THE USER'S OWN FILE, and there is no shipped one. The reference bundles a
/// 256x3392 PNG of status icons; that art is not this project's to redistribute, and a feature
/// that only works with a particular binary sitting beside it is a feature that silently stops
/// working when somebody unzips a new build over an old folder. Without a sheet each icon is
/// drawn as its coloured disc with the rule's own caption on it - which is legible, says which
/// effect it is, and is what every rule looks like until somebody points the setting at a file.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class StatusIconLayer
{
    /// <summary>Smallest an icon is drawn at, whatever scale was asked for.</summary>
    private const float SmallestIcon = 8f;

    /// <summary>
    /// How much of the disc is left showing around the icon, per side.
    /// </summary>
    /// <remarks>
    /// The disc IS the colour coding - it is what makes one glance say which of the icons is
    /// the dangerous one - so the icon is drawn inside it rather than over it.
    /// </remarks>
    private const float IconInset = 0.05f;

    /// <summary>How far down the charge badge sits, as a share of the icon's height.</summary>
    private const float ChargeDrop = 0.12f;

    /// <summary>What to draw and how. Shared with the tracker's other layers.</summary>
    public TrackerSettings Settings { get; set; } = TrackerSettings.Default;

    /// <summary>Where the icon sheet is loaded from, when one was chosen.</summary>
    /// <remarks>
    /// The overlay's own cache rather than one of this layer's: a texture per class is a texture
    /// uploaded twice, and the cache is also where the reason a file did not load is reported.
    /// </remarks>
    public IconCache? Icons { get; set; }

    /// <summary>Draws the rows over the player and over every monster that was read.</summary>
    public void Draw(ImDrawListPtr draw, WorldSnapshot snapshot, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        TrackerSettings settings = Settings;
        if (!settings.ShowPlayerStatus && !settings.ShowMonsterStatus)
        {
            return;
        }

        // Read once for the whole frame rather than per icon: the sheet is one file, and
        // asking the cache sixty times a second for each of a dozen icons is the same answer
        // through a dictionary lookup every time.
        IconCache.Picture sheet = Icons is null || settings.IconSheet.Length == 0
            ? default
            : Icons.PictureFor(settings.IconSheet, IconCache.MaxSheetEdge);

        uint shadow = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, settings.ShadowAlpha));
        uint barBack = OverlaySettings.ParseColour(settings.BarBackColour);

        if (settings.ShowPlayerStatus && snapshot.Player is WorldEntity player)
        {
            DrawRow(
                draw, snapshot, player, snapshot.PlayerBuffs, settings.PlayerStatusOrDefault,
                settings.PlayerLayout ?? StatusLayoutProfile.ForHeight(height).Player,
                sheet, shadow, barBack, width, height);
        }

        if (!settings.ShowMonsterStatus)
        {
            return;
        }

        StatusIconLayout monsterLayout =
            settings.MonsterLayout ?? StatusLayoutProfile.ForHeight(height).Monster;
        IReadOnlyList<StatusIconRule> monsterRules = settings.MonsterStatusOrDefault;

        foreach (WorldEntity monster in snapshot.Entities)
        {
            // Buffs null means the reader was never asked for this monster's - see the rarity
            // floor in WorldReader - so there is nothing to draw and nothing wrong.
            if (monster.Kind != EntityKind.Monster || monster.IsFriendly || monster.Buffs is null)
            {
                continue;
            }

            DrawRow(
                draw, snapshot, monster, monster.Buffs, monsterRules, monsterLayout,
                sheet, shadow, barBack, width, height);
        }
    }

    /// <summary>Draws one entity's row of icons, centred over its position.</summary>
    private void DrawRow(
        ImDrawListPtr draw,
        WorldSnapshot snapshot,
        WorldEntity entity,
        ActiveBuffs? buffs,
        IReadOnlyList<StatusIconRule> rules,
        StatusIconLayout layout,
        IconCache.Picture sheet,
        uint shadow,
        uint barBack,
        int width,
        int height)
    {
        IReadOnlyList<StatusIconHit> hits = StatusIcons.On(rules, buffs);
        if (hits.Count == 0)
        {
            return;
        }

        // The entity's own position with the layout's offset added, exactly as the reference
        // does it. NOT the health bar height: the offsets shipped with the profiles were
        // measured from the feet, so projecting from the top of the model instead would move
        // every row by the height of whatever it is over.
        ScreenPoint at = WorldToScreen.Project(
            snapshot.Matrix, entity.WorldX, entity.WorldY, entity.WorldZ, width, height);

        if (!at.OnScreen)
        {
            return;
        }

        float tile = Settings.IconTile;
        float gap = layout.Gap;

        float total = 0f;
        Span<float> sizes = stackalloc float[hits.Count];
        for (int i = 0; i < hits.Count; i++)
        {
            sizes[i] = Math.Max(SmallestIcon, tile * Math.Max(0.2f, hits[i].Rule.IconScale));
            total += sizes[i] + (i < hits.Count - 1 ? gap : 0f);
        }

        var pen = new Vector2(at.X + layout.X - (total * 0.5f), at.Y + layout.Y);

        for (int i = 0; i < hits.Count; i++)
        {
            StatusIconHit hit = hits[i];
            var size = new Vector2(sizes[i], sizes[i]);
            Vector2 end = pen + size;
            Vector2 centre = pen + (size * 0.5f);
            uint bar = OverlaySettings.ParseColour(hit.Rule.BarColour);
            uint text = OverlaySettings.ParseColour(hit.Rule.TextColour);

            draw.AddCircleFilled(centre, Math.Min(size.X, size.Y) * 0.5f, bar);

            if (sheet.Ready)
            {
                (Vector2 uv0, Vector2 uv1) = TileUv(hit.Rule, sheet, tile);
                Vector2 inset = size * IconInset;
                draw.AddImage(sheet.Texture, pen + inset, end - inset, uv0, uv1);
            }
            else
            {
                // No sheet: the caption, centred on the disc and clipped to what fits. Says
                // WHICH effect it is, which is the whole job an icon was doing.
                string caption = hit.Rule.Caption;
                Vector2 measured = ImGui.CalcTextSize(caption);
                draw.AddText(centre - (measured * 0.5f), text, caption);
            }

            DrawCharges(draw, hit, pen, centre, size, bar, text, shadow);
            DrawTimer(draw, hit, pen, end, size, bar, text, barBack, shadow);

            pen.X += size.X + gap;
        }
    }

    /// <summary>Draws the stack count above an icon, on a plate of the effect's own colour.</summary>
    private void DrawCharges(
        ImDrawListPtr draw,
        StatusIconHit hit,
        Vector2 pen,
        Vector2 centre,
        Vector2 size,
        uint bar,
        uint text,
        uint shadow)
    {
        if (hit.Charges <= 0)
        {
            return;
        }

        string charges = hit.Charges.ToString(System.Globalization.CultureInfo.CurrentCulture);
        Vector2 measured = ImGui.CalcTextSize(charges);

        // The plate is as wide as the number it holds, so a stack of 40 is not clipped by a
        // plate sized for a single digit.
        float plate = measured.X + 8f;
        float drop = (size.Y * ChargeDrop) + Settings.ChargesY;
        float left = centre.X - (plate * 0.5f) + Settings.ChargesX;

        var min = new Vector2(left, pen.Y - measured.Y - 4f + drop);
        var max = new Vector2(left + plate, pen.Y - 2f + drop);
        draw.AddRectFilled(min, max, bar);

        var where = new Vector2(centre.X - (measured.X * 0.5f) + Settings.ChargesX, min.Y + 1f);
        Shadowed(draw, where, charges, shadow);
        draw.AddText(where, text, charges);
    }

    /// <summary>Draws the remaining time under an icon, as a bar that drains.</summary>
    /// <remarks>
    /// The bar and the number say the same thing twice on purpose: the bar is what a glance
    /// mid-fight can read, and the number is what tells you whether "nearly gone" means half a
    /// second or eight of them.
    /// </remarks>
    private void DrawTimer(
        ImDrawListPtr draw,
        StatusIconHit hit,
        Vector2 pen,
        Vector2 end,
        Vector2 size,
        uint bar,
        uint text,
        uint barBack,
        uint shadow)
    {
        if (hit.TimeLeft <= 0f)
        {
            return;
        }

        string timer = hit.Timer;
        Vector2 measured = ImGui.CalcTextSize(timer);

        float barWidth = size.X * 0.9f;
        float barHeight = measured.Y * 1.1f;
        float left = pen.X + ((size.X - barWidth) * 0.5f) + Settings.TimerX;
        float top = end.Y + 2f + Settings.TimerY;

        var min = new Vector2(left, top);
        var max = new Vector2(left + barWidth, top + barHeight);
        draw.AddRectFilled(min, max, barBack);
        draw.AddRectFilled(min, new Vector2(left + (barWidth * hit.Proportion), max.Y), bar);

        var where = new Vector2(left + ((barWidth - measured.X) * 0.5f), top + ((barHeight - measured.Y) * 0.5f));
        Shadowed(draw, where, timer, shadow);
        draw.AddText(where, text, timer);
    }

    /// <summary>
    /// The texture coordinates of one tile of the sheet.
    /// </summary>
    /// <remarks>
    /// Clamped to the sheet's own grid rather than trusted: a coordinate saved against a taller
    /// sheet, or typed by hand, otherwise samples past the edge - which draws whatever the
    /// sampler decides to repeat rather than an obviously wrong icon.
    /// </remarks>
    private static (Vector2 Uv0, Vector2 Uv1) TileUv(StatusIconRule rule, IconCache.Picture sheet, float tile)
    {
        float across = tile / sheet.Width;
        float down = tile / sheet.Height;

        int columns = Math.Max(1, (int)(sheet.Width / tile));
        int rows = Math.Max(1, (int)(sheet.Height / tile));

        var uv0 = new Vector2(
            Math.Clamp(rule.IconColumn, 0, columns - 1) * across,
            Math.Clamp(rule.IconRow, 0, rows - 1) * down);

        return (uv0, uv0 + new Vector2(across, down));
    }

    /// <summary>Writes the same text four ways around, so it survives a bright background.</summary>
    /// <remarks>
    /// A timer drawn over a monster is drawn over whatever that monster is standing in, which
    /// mid-fight is fire. An outline costs four more draws and is the difference between a
    /// number that can be read and one that can be seen to exist.
    /// </remarks>
    private void Shadowed(ImDrawListPtr draw, Vector2 at, string text, uint shadow)
    {
        int size = Settings.ShadowSize;
        if (size <= 0 || (shadow >> 24) == 0)
        {
            return;
        }

        draw.AddText(at with { X = at.X + size }, shadow, text);
        draw.AddText(at with { X = at.X - size }, shadow, text);
        draw.AddText(at with { Y = at.Y + size }, shadow, text);
        draw.AddText(at with { Y = at.Y - size }, shadow, text);
    }
}
