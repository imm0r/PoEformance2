using System.Numerics;
using System.Runtime.Versioning;
using ImGuiNET;
using PoEformance.Features;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Overlay;

/// <summary>
/// Draws the expedition plan: the chain on the map, the next charge in the world.
/// </summary>
/// <remarks>
/// THE MAP CARRIES THE WHOLE CHAIN - numbered placement points joined in order, placed ones
/// grey, the next one green, the rest red, each with its blast ring so overlapping coverage
/// is visible - because the map is where a route is read. The spine the player walks, the
/// exploding props with the area each clears and the holes the shut blockers punch are each
/// their own toggle, since a plan is checked against them and otherwise they crowd it.
///
/// THE WORLD CARRIES ONLY THE NEXT CHARGE: a ring on the ground where it goes and the blast it
/// will cover, projected point by point through the camera matrix with the ground's own height
/// under each, so it sits on the terrain and reads as the game's own circles do. Drawn at
/// 0.95 of the true radius: the ground-plane projection reads a touch larger than the game's
/// own coverage circle, measured by the reference after its base-radius fix, so the shrink is
/// visual only - the planner keeps the true radius.
///
/// Everything here is in GRID cells, the planner's unit; the map projection takes world
/// units, so each point is scaled by the cell size on its way in.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ExpeditionLayer
{
    private const uint Spine = 0xCCFF_CC00u;
    private const uint Ring = 0x3300_D7FFu;
    private const uint Order = 0xFF00_D7FFu;
    private const uint OrderDone = 0x5588_8888u;
    private const uint Placed = 0xFF88_8888u;
    private const uint Next = 0xFF00_FF00u;
    private const uint Later = 0xFF30_30FFu;
    private const uint Detonator = 0xFFFF_FFFFu;
    private const uint Prop = 0xFF3C_A0FFu;
    private const uint GateShut = 0xFF30_30FFu;
    private const uint GateOpen = 0xFF30_FF30u;
    private const uint GateHole = 0x5530_30FFu;
    private const uint Shun = 0xFF40_40FFu;
    private const uint Black = 0xFF00_0000u;
    private const uint White = 0xFFFF_FFFFu;
    private const uint WorldRing = 0xFF00_D7FFu;
    private const uint WorldSpot = 0xFF30_30FFu;

    private const int RingSegments = 28;

    private bool _keyWasDown;

    /// <summary>Draws the plan on the map that is open.</summary>
    public void DrawOnMap(
        ImDrawListPtr draw, MapView map, WorldEntity player, TerrainGrid? terrain, ExpeditionView view, ExpeditionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(settings);

        if (!view.CanPlan && view.Route.Route.Count == 0 && view.Props.Count == 0 && view.Gates.Count == 0)
        {
            return;
        }

        float cell = map.PixelsPerCellEdge;
        foreach (ScreenRect rect in map.Uncovered)
        {
            draw.PushClipRect(new Vector2(rect.Left, rect.Top), new Vector2(rect.Right, rect.Bottom), intersect_with_current_clip_rect: true);
            try
            {
                if (settings.ShowGates)
                {
                    Gates(draw, map, player, terrain, view, cell);
                }

                if (settings.ShowProps)
                {
                    Props(draw, map, player, terrain, view);
                }

                Route(draw, map, player, terrain, view, settings);
                Shunned(draw, map, player, terrain, view);
            }
            finally
            {
                draw.PopClipRect();
            }
        }
    }

    /// <summary>Draws the next charge in the world: where it goes, and what its blast covers.</summary>
    public void DrawWorld(ImDrawListPtr draw, WorldSnapshot snapshot, int width, int height, ExpeditionView view)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(view);

        if (!view.CanPlan || view.Route.Route.Count == 0 || width <= 0 || height <= 0)
        {
            return;
        }

        int next = view.NextIndex;
        if (next < 0 || next >= view.Route.Route.Count)
        {
            return;
        }

        RoutePoint point = view.Route.Route[next];
        TerrainGrid? terrain = snapshot.Terrain;
        float radius = view.EffRadius * 0.95f;

        Vector2 previous = default;
        var started = false;
        for (int i = 0; i <= 36; i++)
        {
            double angle = 2.0 * Math.PI * i / 36;
            float gx = point.Grid.X + (radius * (float)Math.Cos(angle));
            float gy = point.Grid.Y + (radius * (float)Math.Sin(angle));
            ScreenPoint at = WorldToScreen.Project(
                snapshot.Matrix, gx * MapView.WorldToGrid, gy * MapView.WorldToGrid, Height(terrain, gx, gy, point.Z), width, height);
            var here = new Vector2(at.X, at.Y);
            if (started && at.OnScreen)
            {
                draw.AddLine(previous, here, WorldRing, 2f);
            }

            previous = here;
            started = at.OnScreen;
        }

        ScreenPoint spot = WorldToScreen.Project(
            snapshot.Matrix, point.Grid.X * MapView.WorldToGrid, point.Grid.Y * MapView.WorldToGrid,
            Height(terrain, point.Grid.X, point.Grid.Y, point.Z), width, height);
        if (!spot.OnScreen)
        {
            return;
        }

        var centre = new Vector2(spot.X, spot.Y);
        draw.AddCircle(centre, 16f, Black, 24, 5f);
        draw.AddCircle(centre, 16f, WorldSpot, 24, 3f);
        draw.AddCircleFilled(centre, 4f, White);
        string label = $"#{next + 1}";
        Vector2 size = ImGui.CalcTextSize(label);
        draw.AddText(centre + new Vector2(20f, -size.Y * 0.5f) + new Vector2(1f, 1f), Black, label);
        draw.AddText(centre + new Vector2(20f, -size.Y * 0.5f), White, label);
    }

    /// <summary>
    /// The Run hotkey: a press - down now, not down last frame - asks for a plan.
    /// </summary>
    /// <remarks>
    /// Polled from the overlay's frame, which already returns before this when neither the game
    /// nor the tool owns the foreground, so a key meant for another application never reaches
    /// it; and only while there is something to plan, so the key is inert everywhere else.
    /// </remarks>
    public void PollRunKey(ExpeditionWatch watch, ExpeditionSettings settings, ExpeditionView view)
    {
        ArgumentNullException.ThrowIfNull(watch);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(view);

        if (settings.RunKey <= 0)
        {
            _keyWasDown = false;
            return;
        }

        bool down = ScreenInput.IsDown(settings.RunKey);
        if (down && !_keyWasDown && view.CanPlan && !view.Computing)
        {
            watch.RequestRun();
        }

        _keyWasDown = down;
    }

    private static float Height(TerrainGrid? terrain, float gx, float gy, float fallback)
        => terrain is { HasHeights: true }
            ? terrain.HeightAt((int)MathF.Round(gx), (int)MathF.Round(gy))
            : fallback;

    private static Vector2 Project(MapView map, WorldEntity player, TerrainGrid? terrain, Vector2 grid, float z)
        => map.Project(
            grid.X * MapView.WorldToGrid, grid.Y * MapView.WorldToGrid, Height(terrain, grid.X, grid.Y, z),
            player.WorldX, player.WorldY, player.TerrainHeight);

    private static void Route(
        ImDrawListPtr draw, MapView map, WorldEntity player, TerrainGrid? terrain, ExpeditionView view, ExpeditionSettings settings)
    {
        if (!view.HasDetonator)
        {
            return;
        }

        Vector2 previous = Project(map, player, terrain, view.Detonator, view.DetonatorZ);
        draw.AddCircleFilled(previous, 4f, Detonator);

        PlanResult plan = view.Route;
        if (plan.Route.Count == 0)
        {
            return;
        }

        // The spine under the charges, so route and placement read as two separate things.
        if (settings.ShowSpine && plan.SpinePoints.Count >= 2)
        {
            Vector2 from = Project(map, player, terrain, plan.SpinePoints[0], plan.SpineHeights.Count > 0 ? plan.SpineHeights[0] : view.DetonatorZ);
            for (int i = 1; i < plan.SpinePoints.Count; i++)
            {
                float z = i < plan.SpineHeights.Count ? plan.SpineHeights[i] : view.DetonatorZ;
                Vector2 to = Project(map, player, terrain, plan.SpinePoints[i], z);
                draw.AddLine(from, to, Spine, 2f);
                from = to;
            }
        }

        if (settings.ShowRings)
        {
            foreach (RoutePoint point in plan.Route)
            {
                Circle(draw, map, player, terrain, point.Grid, point.Z, view.EffRadius, Ring, 1f);
            }
        }

        int next = view.NextIndex;
        for (int i = 0; i < plan.Route.Count; i++)
        {
            RoutePoint point = plan.Route[i];
            Vector2 at = Project(map, player, terrain, point.Grid, point.Z);
            bool done = i < next;
            bool current = i == next;
            draw.AddLine(previous, at, done ? OrderDone : Order, 2f);
            draw.AddCircleFilled(at, current ? 8f : 6f, done ? Placed : current ? Next : Later);
            string number = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            Vector2 size = ImGui.CalcTextSize(number);
            draw.AddText(at - (size * 0.5f) + new Vector2(1f, 1f), Black, number);
            draw.AddText(at - (size * 0.5f), White, number);
            previous = at;
        }
    }

    private static void Props(ImDrawListPtr draw, MapView map, WorldEntity player, TerrainGrid? terrain, ExpeditionView view)
    {
        foreach (PlanProp prop in view.Props)
        {
            Circle(draw, map, player, terrain, prop.Grid, 0f, prop.Radius, Prop, 1.5f);
            Vector2 centre = Project(map, player, terrain, prop.Grid, 0f);
            draw.AddCircleFilled(centre, 4f, Black);
            draw.AddCircleFilled(centre, 3f, Prop);
        }
    }

    private static void Gates(ImDrawListPtr draw, MapView map, WorldEntity player, TerrainGrid? terrain, ExpeditionView view, float cell)
    {
        float half = Math.Max(1f, 0.5f * cell);
        foreach (ExpeditionGateView gate in view.Gates)
        {
            if (gate.Blocked)
            {
                foreach ((int x, int y) in gate.Footprint)
                {
                    Vector2 at = Project(map, player, terrain, new Vector2(x, y), gate.Z);
                    draw.AddRectFilled(at - new Vector2(half, half), at + new Vector2(half, half), GateHole);
                }
            }

            Vector2 centre = Project(map, player, terrain, gate.Grid, gate.Z);
            draw.AddCircle(centre, 5f, Black, 16, 3f);
            draw.AddCircle(centre, 5f, gate.Blocked ? GateShut : GateOpen, 16, 2f);
        }
    }

    /// <summary>
    /// The shunned relics: a red ring with a bar through it, the sign for "not this one" - drawn
    /// whenever there is a plan to make, since the constraint shapes the route whether or not
    /// the rings are on.
    /// </summary>
    private static void Shunned(ImDrawListPtr draw, MapView map, WorldEntity player, TerrainGrid? terrain, ExpeditionView view)
    {
        var bar = new Vector2(5f, -5f);
        foreach (ExpeditionTargetView target in view.Targets)
        {
            if (!target.Shunned)
            {
                continue;
            }

            Vector2 at = Project(map, player, terrain, target.Grid, target.Z);
            draw.AddCircle(at, 7f, Black, 16, 3.5f);
            draw.AddCircle(at, 7f, Shun, 16, 2f);
            draw.AddLine(at - bar, at + bar, Black, 3.5f);
            draw.AddLine(at - bar, at + bar, Shun, 2f);
        }
    }

    /// <summary>A ring sampled in grid space and projected point by point: the map's skewed ellipse, not a screen circle.</summary>
    private static void Circle(
        ImDrawListPtr draw, MapView map, WorldEntity player, TerrainGrid? terrain, Vector2 centre, float z, float radius, uint colour, float thickness)
    {
        Vector2 previous = default;
        for (int i = 0; i <= RingSegments; i++)
        {
            double angle = 2.0 * Math.PI * i / RingSegments;
            var on = new Vector2(centre.X + (radius * (float)Math.Cos(angle)), centre.Y + (radius * (float)Math.Sin(angle)));
            Vector2 at = Project(map, player, terrain, on, z);
            if (i > 0)
            {
                draw.AddLine(previous, at, colour, thickness);
            }

            previous = at;
        }
    }
}
