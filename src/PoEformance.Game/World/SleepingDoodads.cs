using System.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Components;
using PoEformance.Game.Entities;

namespace PoEformance.Game.World;

/// <summary>One entity of the area whose path a room's doodad line names as its stub, and where it stands.</summary>
/// <param name="Id">The entity's id, as its map keys it.</param>
/// <param name="Path">Its metadata path - the stub, as the room's line writes it.</param>
/// <param name="X">Where it stands, in world units.</param>
/// <param name="Y">Where it stands, in world units.</param>
/// <param name="Z">Its z, counted the game's way - up is minus.</param>
/// <param name="Asleep">Whether it came out of the sleeping map rather than the awake one.</param>
public readonly record struct DoodadSighting(uint Id, string Path, float X, float Y, float Z, bool Asleep);

/// <summary>What one read of the area's entity maps for the rooms' doodads came to - see <see cref="SleepingDoodads"/>.</summary>
/// <param name="SleepingSize">How many entities the sleeping map says it holds.</param>
/// <param name="SleepingNodes">How many of them the walk reached - fewer than the size where the walk's budget bound it.</param>
/// <param name="AwakeNodes">How many the awake map's walk reached, visuals included.</param>
/// <param name="Named">How many of both carried a path at all.</param>
/// <param name="Found">Every entity whose path is one the rooms name, with where it stands.</param>
/// <param name="Milliseconds">How long the whole read took, both maps and every path.</param>
/// <param name="Why">Why there was no read, or empty.</param>
public sealed record DoodadSurvey(
    int SleepingSize, int SleepingNodes, int AwakeNodes, int Named, IReadOnlyList<DoodadSighting> Found, double Milliseconds, string Why)
{
    /// <summary>No read, and why.</summary>
    public static DoodadSurvey Not(string why) => new(0, 0, 0, 0, [], 0d, why);
}

/// <summary>
/// Reads, once, which of the doodads the area's rooms name stand in the area as entities, and where - out of the sleeping entity map as well as the awake one.
/// </summary>
/// <remarks>
/// A MEASUREMENT BEFORE A METHOD. A room's doodad lines are the one thing of a room the game is known
/// to put into memory as it was authored: each line's stub is the entity's own path, and the entity
/// stands where the line put it (LaidRoomModels.HeightsSaid found a pot at exactly its line's -115).
/// So a room's doodads are a point pattern the area could be searched for, which no ground or tile
/// check can mistake - but only if the game keeps them for the whole area, not just the bubble round
/// the player, and only if reading them once is cheap enough to do on every area. Neither is known,
/// and this is what settles both: the size of the sleeping map, how long the walk took, and how many
/// of the rooms' doodads it held.
///
/// NOT THE FRAME'S READ, AND NOTHING OF IT. The snapshot walks the awake map every frame and that
/// walk is the dearest thing the tool does; this one runs on a press, on its own task, with readers
/// of its own (EntityReader keeps a name cache, which is not for sharing between threads), and
/// reads the path first and the position only where the path is one a room names. A whole map's
/// worth of entities costs one path read each and nothing more.
///
/// THE AWAKE MAP TOO, because the sleeping one is the rest of the area and not all of it: whatever
/// is near the player is awake, so a room the player stands in would otherwise read as having no
/// doodads at all. The awake walk is the frame's own, visuals included, and is counted apart.
/// </remarks>
public static class SleepingDoodads
{
    /// <summary>
    /// Most nodes walked per map, and the most the map may claim to hold. An area's sleeping map is bounded by the area; a size past this is a bad read, not a big area.
    /// </summary>
    public const int MostNodes = 200_000;

    /// <summary>
    /// Reads both entity maps of the area for the paths in <paramref name="paths"/>. Never throws on a bad read; a map that cannot be walked counts nought.
    /// </summary>
    /// <param name="reader">The game's memory.</param>
    /// <param name="schema">The offsets.</param>
    /// <param name="areaInstance">The area, as the last snapshot resolved it - nought outside the game.</param>
    /// <param name="paths">The stubs the area's rooms name, compared without case - built with StringComparer.OrdinalIgnoreCase.</param>
    public static DoodadSurvey Read(IMemoryReader reader, OffsetSchema schema, ulong areaInstance, IReadOnlySet<string> paths)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(paths);
        if (!MemoryReaderExtensions.IsPlausiblePointer(areaInstance))
        {
            return DoodadSurvey.Not("not in an area");
        }

        if (paths.Count == 0)
        {
            return DoodadSurvey.Not("the area's rooms name no doodad");
        }

        StructDef area = schema.Structs["AreaInstance"];
        ulong sleeping = areaInstance + (ulong)area.OffsetOf("SleepingEntities");
        ulong awake = areaInstance + (ulong)area.OffsetOf("AwakeEntities");
        long started = Stopwatch.GetTimestamp();
        long size = reader.Read<long>(sleeping + (ulong)schema.Structs["StdMap"].OffsetOf("Size"));

        var maps = new EntityMapReader(reader, schema);
        var entities = new EntityReader(reader, schema);
        var render = new RenderReader(reader, schema);
        var found = new List<DoodadSighting>();
        int named = 0;
        int sleepingNodes = Walk(maps, entities, render, sleeping, paths, asleep: true, found, ref named);
        int awakeNodes = Walk(maps, entities, render, awake, paths, asleep: false, found, ref named);
        return new DoodadSurvey(
            (int)Math.Clamp(size, 0, int.MaxValue),
            sleepingNodes,
            awakeNodes,
            named,
            found,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            string.Empty);
    }

    /// <summary>One map walked: every node's path, and the position of every entity whose path is wanted. Returns how many nodes the walk reached.</summary>
    private static int Walk(
        EntityMapReader maps,
        EntityReader entities,
        RenderReader render,
        ulong mapStruct,
        IReadOnlySet<string> paths,
        bool asleep,
        List<DoodadSighting> found,
        ref int named)
    {
        Dictionary<uint, ulong> pointers = maps.ReadEntityPointers(mapStruct, maxEntities: MostNodes, includeVisuals: true, mostVisits: MostNodes);
        foreach ((uint id, ulong address) in pointers)
        {
            // THE PATH FIRST, the components only for a path a room names: the same economy the
            // frame's read makes, and here it is nearly the whole cost.
            EntityIdentity? identity = entities.ReadIdentity(address);
            if (identity is not { } known || known.Path.Length == 0)
            {
                continue;
            }

            named++;
            if (!paths.Contains(known.Path))
            {
                continue;
            }

            Entity entity = entities.Read(known);
            if (render.Read(entity.Component("Render")) is { } at)
            {
                found.Add(new DoodadSighting(id, known.Path, at.X, at.Y, at.Z, asleep));
            }
        }

        return pointers.Count;
    }
}
