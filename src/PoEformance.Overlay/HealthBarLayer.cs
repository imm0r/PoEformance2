using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.Components;
using PoEformance.Game.World;

namespace PoEformance.Overlay;

/// <summary>
/// Draws a health bar over every monster on screen.
/// </summary>
/// <remarks>
/// WHERE IT GOES WAS ALREADY PROVEN. An entity's Render z minus its model height is exactly
/// where the game floats its own health bar, and the calibration aid in this overlay puts
/// brackets on the game's bars to check it - so this draws at a position the game itself
/// supplies the reference for, rather than at a guessed offset above a dot.
///
/// The reason to want it over the game's own bars: the game shows one for what you are
/// fighting, and this shows all of them, coloured by RARITY. Which of the pack is the rare
/// leader, and how far through it you are, are the two things a fight needs and neither is on
/// screen otherwise.
///
/// The energy shield is drawn as its own segment rather than added to the health, because
/// they behave differently - a shield that recharges between packs is not health regained,
/// and a bar that merges them cannot show that.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class HealthBarLayer
{
    /// <summary>Bar width in pixels, before the chosen scale.</summary>
    private const float Width = 46f;

    /// <summary>Bar height in pixels, before the chosen scale.</summary>
    private const float Height = 5f;

    /// <summary>How far above the game's own bar position this one sits.</summary>
    private const float Lift = 6f;

    /// <summary>
    /// Only draw a monster's bar once it has been hurt.
    /// </summary>
    /// <remarks>
    /// Off by default. A screen of full bars is a screen of noise, but "which of these is
    /// already hurt" is a different question from "which of these is the rare", and only the
    /// second one is answered by drawing all of them. Whoever wants the quiet version can
    /// have it; nobody should have it chosen for them.
    /// </remarks>
    public bool OnlyWhenHurt { get; set; }

    /// <summary>How every drawn thing looks. Shared with the overlay.</summary>
    public OverlayStyle Style { get; set; } = new();

    /// <summary>Draws a bar over each living monster the projection can place.</summary>
    public void Draw(ImDrawListPtr draw, WorldSnapshot snapshot, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!Style.Visible(StyleCatalogue.Keys.HealthBar))
        {
            return;
        }

        float scale = Style.Sized(StyleCatalogue.Keys.HealthBar, 1f);
        float barWidth = Width * scale;
        float barHeight = Height * scale;
        uint back = Style.Colour(StyleCatalogue.Keys.HealthBarBack);
        uint shieldColour = Style.Colour(StyleCatalogue.Keys.HealthBarShield);

        foreach (WorldEntity monster in snapshot.Entities)
        {
            if (monster.Kind != EntityKind.Monster || !monster.Life.IsValid)
            {
                continue;
            }

            int percent = monster.Life.Percent;
            if (OnlyWhenHurt && percent >= 100 && !HasShieldMissing(monster))
            {
                continue;
            }

            // The game's own bar height for this monster - see the type remarks. Not a
            // guessed offset above the feet, which would sit wrong on anything that is not
            // the height of the thing it was tuned against.
            ScreenPoint at = WorldToScreen.Project(
                snapshot.Matrix, monster.WorldX, monster.WorldY, monster.HealthBarZ, width, height);

            if (!at.OnScreen)
            {
                continue;
            }

            var centre = new Vector2(at.X, at.Y - Lift);
            var topLeft = new Vector2(centre.X - (barWidth / 2f), centre.Y - (barHeight / 2f));
            var bottomRight = new Vector2(centre.X + (barWidth / 2f), centre.Y + (barHeight / 2f));

            draw.AddRectFilled(topLeft, bottomRight, back);

            float filled = Math.Clamp(percent / 100f, 0f, 1f);
            if (filled > 0f)
            {
                draw.AddRectFilled(
                    topLeft,
                    new Vector2(topLeft.X + (barWidth * filled), bottomRight.Y),
                    Style.Colour(StyleCatalogue.ForRarity("entity.monster", monster.Rarity)));
            }

            // The shield above the health, as its own strip. Merged into one bar it would
            // read as extra health, and a shield that comes back between packs is not that.
            if (monster.EnergyShield.IsValid && monster.EnergyShield.Current > 0)
            {
                float shield = Math.Clamp(monster.EnergyShield.Percent / 100f, 0f, 1f);
                float strip = Math.Max(1f, barHeight * 0.35f);
                draw.AddRectFilled(
                    topLeft with { Y = topLeft.Y - strip - 1f },
                    new Vector2(topLeft.X + (barWidth * shield), topLeft.Y - 1f),
                    shieldColour);
            }

            draw.AddRect(topLeft, bottomRight, Style.Colour(StyleCatalogue.Keys.DotOutline));
        }
    }

    /// <summary>Whether a monster's shield is down, which counts as hurt.</summary>
    private static bool HasShieldMissing(WorldEntity monster)
        => monster.EnergyShield.IsValid && monster.EnergyShield.Percent < 100;
}
