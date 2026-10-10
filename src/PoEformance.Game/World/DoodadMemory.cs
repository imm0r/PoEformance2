using PoEformance.Game.Files;

namespace PoEformance.Game.World;

/// <summary>
/// The scripted objects of the area's rooms the client has listed at any moment of this instance, by entity id - what the frame's read of the network bubble adds to the one survey of the entity maps.
/// </summary>
/// <remarks>
/// WHY. A scripted object - a checkpoint, a power-line piece, an NPC - is the server's entity, and the
/// client holds it only while it stands inside the client's network bubble (RoomDoodad.IsProp has the
/// captures). The survey (SleepingDoodads) runs once per area and sees the bubble as it was that
/// moment: in The Assembly one checkpoint of four, and the checkpoint variant of a wall room could not
/// be told from the plain one anywhere its checkpoint was not. The frame's read walks the awake map
/// thirty times a second, so every scripted object the player comes near passes through it; kept here
/// by id, it stays evidence for its room for the rest of the instance, wherever the player goes next.
///
/// SCRIPTED OBJECTS ONLY, told by their path: the frame's read carries no model, and a plain doodad
/// without its model would match every plain line of every room (RoomDoodadFinder matches on the path
/// alone where either side has no model). The props the survey has, and they do not move.
///
/// NOTHING IS FORGOTTEN WITHIN AN INSTANCE. EntityMemory drops a sighting that vanishes under the
/// player's feet, because a chest opened or an item taken is news; here a line's object having stood
/// where the line put it is the evidence, and it stays evidence once the object is gone again. The
/// next instance hands out the same ids for other things, so a new area hash clears everything.
///
/// ONE THREAD: fed on the frame, read on the frame - a placing takes a copy (<see cref="Held"/>) with it.
/// </remarks>
public sealed class DoodadMemory
{
    private readonly Dictionary<uint, DoodadSighting> _seen = [];
    private uint _area;

    /// <summary>How many sightings are held.</summary>
    public int Count => _seen.Count;

    /// <summary>Counts up whenever a sighting is added, so a placing can tell whether anything arrived since it ran.</summary>
    public int Version { get; private set; }

    /// <summary>
    /// Takes the frame's entities: every one whose path is a stub the rooms name, and not a plain prop's, is kept by id - the first time, where it stood. Returns how many were new.
    /// </summary>
    /// <param name="areaHash">Which instance this is; a change clears what is held. Nought is a frame that read no area and changes nothing.</param>
    /// <param name="entities">The frame's entities, the remembered ones among them.</param>
    /// <param name="stubs">The stubs the area's rooms name, compared without case - see AreaRooms.Stubs.</param>
    public int Notice(uint areaHash, IReadOnlyList<WorldEntity> entities, IReadOnlySet<string> stubs)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(stubs);
        if (areaHash == 0)
        {
            return 0;
        }

        if (areaHash != _area)
        {
            _area = areaHash;
            _seen.Clear();
        }

        var added = 0;
        foreach (WorldEntity entity in entities)
        {
            string path = entity.Path;
            if (path.Length == 0 || _seen.ContainsKey(entity.Id) || RoomDoodad.IsPropPath(path) || !stubs.Contains(path))
            {
                continue;
            }

            _seen[entity.Id] = new DoodadSighting(entity.Id, path, string.Empty, entity.WorldX, entity.WorldY, entity.WorldZ, Asleep: false) { Remembered = true };
            added++;
        }

        if (added > 0)
        {
            Version++;
        }

        return added;
    }

    /// <summary>A copy of what is held, for a placing to take onto its task.</summary>
    public List<DoodadSighting> Held() => [.. _seen.Values];

    /// <summary>
    /// The survey's sightings with the remembered ones the survey does not hold appended - the finder's input. The survey's own list where there is nothing to add.
    /// </summary>
    /// <remarks>By id, the survey's first: a sighting the survey holds is the entity as the walk read it, model and map included, and the remembered copy of it says less.</remarks>
    public static IReadOnlyList<DoodadSighting> Merged(IReadOnlyList<DoodadSighting> found, IReadOnlyList<DoodadSighting> remembered)
    {
        ArgumentNullException.ThrowIfNull(found);
        ArgumentNullException.ThrowIfNull(remembered);
        if (remembered.Count == 0)
        {
            return found;
        }

        var known = new HashSet<uint>(found.Count);
        foreach (DoodadSighting one in found)
        {
            known.Add(one.Id);
        }

        List<DoodadSighting>? merged = null;
        foreach (DoodadSighting one in remembered)
        {
            if (known.Add(one.Id))
            {
                merged ??= [.. found];
                merged.Add(one);
            }
        }

        return merged ?? found;
    }
}
