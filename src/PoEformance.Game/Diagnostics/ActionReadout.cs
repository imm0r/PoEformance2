using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Components;
using PoEformance.Game.Entities;
using PoEformance.Game.World;

namespace PoEformance.Game.Diagnostics;

/// <summary>One actor caught doing something, as the schema's action fields describe it.</summary>
/// <param name="Bearing">
/// Degrees between where the action points and where the actor is facing, or NaN when the
/// facing could not be read. The cross-check: an aimed action should agree with the facing.
/// </param>
public sealed record ActionSighting(
    string Who,
    bool IsPlayer,
    ActorAction Action,
    string AnimationName,
    float DistanceFromActor,
    double Bearing);

/// <summary>
/// Reads the action fields that <see cref="ActionHunt"/> found, on the player AND on every
/// monster in the area, and reports what they say.
/// </summary>
/// <remarks>
/// THE HUNT SEARCHES; THIS ONE CHECKS. Once offsets are in the schema, the useful question
/// stops being "where are these fields" and becomes "do they still say sensible things" - on a
/// new patch, in a new area, and above all on MONSTERS, which is the case every measurement so
/// far has missed. Every offset behind the action fields was taken from the player's own actor
/// while the person walked and cast; monsters carry the same component and the same offsets
/// should hold, and until a session says so that is an expectation rather than a finding.
///
/// So this exists to be pointed at a pack of monsters. What it prints is deliberately raw -
/// who, what kind of action, how far it reaches, and whether it agrees with the facing - and
/// the reader is expected to judge it: an action reaching thirty thousand units, or a target
/// on the far side of the map, is the shape a wrong offset takes on an entity nobody checked.
///
/// THE BEARING COLUMN is the one automatic check available, and it is only meaningful for
/// something aimed: the actor faces where it is aiming, so a target's bearing from the action's
/// origin should sit near the facing. A column of small numbers is the fields agreeing with a
/// field found independently a month earlier; a column of noise is a reason to stop trusting
/// them. It reads NaN wherever the facing did not read, which a recording made by an older
/// build will do for every row.
/// </remarks>
public sealed class ActionReadout
{
    /// <summary>Most monsters reported in one pass; a busy area has hundreds.</summary>
    private const int MostMonsters = 24;

    private readonly IMemoryReader _reader;
    private readonly OffsetSchema _schema;
    private readonly ActionReader _actions;
    private readonly EntityReader _entities;
    private readonly RenderReader _render;
    private readonly WorldReader _world;

    public ActionReadout(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;
        _schema = schema;
        _actions = new ActionReader(reader, schema);
        _entities = new EntityReader(reader, schema);
        _render = new RenderReader(reader, schema);
        _world = new WorldReader(reader, schema);
    }

    /// <summary>Everything currently doing something: the player first, then monsters.</summary>
    public List<ActionSighting> Read(ulong gameStatesStatic, AnimationNames names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var sightings = new List<ActionSighting>();

        GameChainAddresses chain = GameChain.Resolve(_reader, _schema, gameStatesStatic);
        if (!chain.InGame)
        {
            return sightings;
        }

        if (Look(chain.PlayerEntity, "player", isPlayer: true, names) is { } player)
        {
            sightings.Add(player);
        }

        WorldSnapshot snapshot = _world.Read(gameStatesStatic);
        foreach (WorldEntity monster in snapshot.Entities
                     .Where(e => e.Kind == EntityKind.Monster && !e.IsFriendly)
                     .Take(MostMonsters))
        {
            if (Look(monster.Address, monster.ShortName, isPlayer: false, names) is { } sighting)
            {
                sightings.Add(sighting);
            }
        }

        return sightings;
    }

    /// <summary>One entity, or null when it has no actor or is doing nothing.</summary>
    private ActionSighting? Look(ulong entityAddress, string who, bool isPlayer, AnimationNames names)
    {
        Entity? entity = _entities.Read(entityAddress);
        ulong actor = entity?.Component("Actor") ?? 0;
        if (actor == 0)
        {
            return null;
        }

        ActorAction action = _actions.Read(actor);
        if (action.Kind == ActionKind.None)
        {
            return null;
        }

        // How far the action's target is from where the actor actually stands - which is not
        // the same as its reach, and is the number that says whether a target is plausible.
        float distance = float.NaN;
        double bearing = double.NaN;
        ulong renderAddress = entity!.Component("Render");
        if (renderAddress != 0 && _render.Read(renderAddress) is { } position)
        {
            distance = MathF.Sqrt(
                ((action.TargetX - position.X) * (action.TargetX - position.X))
                + ((action.TargetY - position.Y) * (action.TargetY - position.Y)));

            if (_render.ReadFacing(renderAddress) is (float angle, _) && action.Reach > 1f)
            {
                float aimed = Facing.FromHeading(action.TargetX - action.OriginX, action.TargetY - action.OriginY);
                bearing = Math.Abs(Facing.Between(aimed, angle)) * 180.0 / Math.PI;
            }
        }

        return new ActionSighting(who, isPlayer, action, names.Label(action.AnimationId), distance, bearing);
    }

    /// <summary>Writes the readout.</summary>
    public static void Report(IReadOnlyList<ActionSighting> sightings, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(sightings);
        ArgumentNullException.ThrowIfNull(output);

        output.WriteLine();
        output.WriteLine("action readout (the schema's action fields, as they read right now)");
        if (sightings.Count == 0)
        {
            output.WriteLine("  nothing is acting - move or cast, or stand near something that does.");
            return;
        }

        output.WriteLine("  who                    kind     id  animation             reach  to-target  bearing");
        foreach (ActionSighting s in sightings)
        {
            string bearing = double.IsNaN(s.Bearing) ? "     --" : $"{s.Bearing,6:F1}°";
            output.WriteLine(
                $"  {Short(s.Who, 22),-22} {s.Action.Kind,-7} {s.Action.RawId,4}  {Short(s.AnimationName, 18),-18}"
                + $" {s.Action.Reach,7:F0}  {s.DistanceFromActor,9:F0}  {bearing}");
        }

        // The monster question, stated rather than assumed: the offsets were all measured on
        // the player, so a row for anything else is the first evidence either way.
        int monsters = sightings.Count(s => !s.IsPlayer);
        output.WriteLine();
        output.WriteLine(monsters == 0
            ? "  no MONSTER was acting in this frame - and monsters are the case none of these"
            + "\n  offsets has ever been checked against. Stand in a fight and look again."
            : $"  {monsters} monster row(s) above are the first look at these fields on anything but"
            + "\n  the player. Judge them: a reach of tens of thousands, or a target nowhere near"
            + "\n  the actor, is what a wrong offset looks like on an entity nobody verified.");

        double[] bearings = [.. sightings.Where(s => !double.IsNaN(s.Bearing)).Select(s => s.Bearing)];
        if (bearings.Length > 0)
        {
            output.WriteLine($"  bearing agreement over {bearings.Length} aimed action(s): "
                + $"median {Median(bearings):F1}°, worst {bearings.Max():F1}° "
                + "(small means the target agrees with the facing, which was found independently)");
        }
        else
        {
            output.WriteLine("  no bearings: the facing did not read. On a replay that means the recording"
                + "\n  was made by a build that never read it.");
        }
    }

    private static double Median(double[] values)
    {
        double[] sorted = [.. values.Order()];
        return sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2.0;
    }

    private static string Short(string text, int most)
        => text.Length <= most ? text : text[..(most - 1)] + "…";
}
