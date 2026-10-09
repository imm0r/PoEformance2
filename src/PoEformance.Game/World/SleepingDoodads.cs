using System.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Components;
using PoEformance.Game.Entities;

namespace PoEformance.Game.World;

/// <summary>One entity of the area whose path a room's doodad line names as its stub, and where it stands.</summary>
/// <param name="Id">The entity's id, as its map keys it.</param>
/// <param name="Path">Its metadata path - the stub, as the room's line writes it.</param>
/// <param name="Model">The <c>.ao</c> it loaded, as the room's line writes it - or empty where it has no Animated component or the chain did not read.</param>
/// <param name="X">Where it stands, in world units.</param>
/// <param name="Y">Where it stands, in world units.</param>
/// <param name="Z">Its z, counted the game's way - up is minus.</param>
/// <param name="Asleep">Whether it came out of the sleeping map rather than the awake one.</param>
public readonly record struct DoodadSighting(uint Id, string Path, string Model, float X, float Y, float Z, bool Asleep);

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
/// A MEASUREMENT BEFORE A METHOD, AND THEN THE METHOD'S INPUT. A room's doodad lines are the one
/// thing of a room the game is known to put into memory as it was authored: each line's stub is the
/// entity's own path, and the entity stands where the line put it (LaidRoomModels.HeightsSaid found a
/// pot at exactly its line's -115). So a room's doodads are a point pattern the area can be searched
/// for, which no ground or tile check can mistake - see RoomDoodadFinder. Measured first, in The
/// Assembly: 1274 sleeping and 167 awake entities read in 47 ms, and every doodad line of every
/// workshop room named a path that stood in the area.
///
/// THE MODEL AS WELL AS THE PATH. 684 of that area's 774 sightings carried the one plain path,
/// Metadata/MiscellaneousObjects/Doodad: the path says "a doodad" and nothing more, and a wall room's
/// lines are all of that kind. What tells them apart is the .ao each entity loaded, which the schema
/// carries as Animated.ModelInfoPtr -> AnimatedModelInfo.ModelFileRecordPtr -> FileInfoValue.Name,
/// confirmed against GameHelper2 (its Animated.ReadModelPath, which cuts the name at '@'). Read for
/// every sighting, four reads each, and compared to the line's .ao as paths.
///
/// NOT THE FRAME'S READ, AND NOTHING OF IT. The snapshot walks the awake map every frame and that
/// walk is the dearest thing the tool does; this one runs once per area, on its own task, with readers
/// of its own (EntityReader keeps a name cache, which is not for sharing between threads), and reads
/// the path first and the rest only where the path is one a room names. A whole map's worth of
/// entities costs one path read each and nothing more.
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

        var readers = new Readers(
            reader,
            new EntityMapReader(reader, schema),
            new EntityReader(reader, schema),
            new RenderReader(reader, schema),
            schema.Structs["Animated"].OffsetOf("ModelInfoPtr"),
            schema.Structs["AnimatedModelInfo"].OffsetOf("ModelFileRecordPtr"),
            schema.Structs["FileInfoValue"].OffsetOf("Name"));
        var found = new List<DoodadSighting>();
        int named = 0;
        int sleepingNodes = Walk(readers, sleeping, paths, asleep: true, found, ref named);
        int awakeNodes = Walk(readers, awake, paths, asleep: false, found, ref named);
        return new DoodadSurvey(
            (int)Math.Clamp(size, 0, int.MaxValue),
            sleepingNodes,
            awakeNodes,
            named,
            found,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            string.Empty);
    }

    /// <summary>A model file's name as a room's line would write it: cut at the '@' the record may carry, and with forward slashes.</summary>
    public static string ModelPath(string recordName)
    {
        ArgumentNullException.ThrowIfNull(recordName);
        int at = recordName.IndexOf('@');
        return (at >= 0 ? recordName[..at] : recordName).Replace('\\', '/');
    }

    /// <summary>The readers one survey uses, and the three offsets the model's chain needs.</summary>
    private readonly record struct Readers(
        IMemoryReader Memory, EntityMapReader Maps, EntityReader Entities, RenderReader Render, int ModelInfo, int ModelRecord, int RecordName);

    /// <summary>One map walked: every node's path, and the position and model of every entity whose path is wanted. Returns how many nodes the walk reached.</summary>
    private static int Walk(in Readers readers, ulong mapStruct, IReadOnlySet<string> paths, bool asleep, List<DoodadSighting> found, ref int named)
    {
        Dictionary<uint, ulong> pointers = readers.Maps.ReadEntityPointers(mapStruct, maxEntities: MostNodes, includeVisuals: true, mostVisits: MostNodes);
        foreach ((uint id, ulong address) in pointers)
        {
            // THE PATH FIRST, the components only for a path a room names: the same economy the
            // frame's read makes, and here it is nearly the whole cost.
            EntityIdentity? identity = readers.Entities.ReadIdentity(address);
            if (identity is not { } known || known.Path.Length == 0)
            {
                continue;
            }

            named++;
            if (!paths.Contains(known.Path))
            {
                continue;
            }

            Entity entity = readers.Entities.Read(known);
            if (readers.Render.Read(entity.Component("Render")) is { } at)
            {
                found.Add(new DoodadSighting(id, known.Path, Model(readers, entity.Component("Animated")), at.X, at.Y, at.Z, asleep));
            }
        }

        return pointers.Count;
    }

    /// <summary>The .ao an entity loaded, by the Animated component's chain - or empty where there is no component or a hop does not read.</summary>
    private static string Model(in Readers readers, ulong animated)
    {
        if (animated == 0)
        {
            return string.Empty;
        }

        ulong info = readers.Memory.ReadPointer(animated + (ulong)readers.ModelInfo);
        if (info == 0)
        {
            return string.Empty;
        }

        ulong record = readers.Memory.ReadPointer(info + (ulong)readers.ModelRecord);
        return record == 0 ? string.Empty : ModelPath(readers.Memory.ReadStdWString(record + (ulong)readers.RecordName));
    }
}
