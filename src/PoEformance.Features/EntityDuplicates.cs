using PoEformance.Game.World;

namespace PoEformance.Features;

/// <summary>
/// Whether the entity list is describing the same monster more than once, and how.
/// </summary>
/// <remarks>
/// TWELVE ROWS, FOUR DOTS is what this exists to explain. The entity browser listed twelve
/// monsters of one kind while the map drew four markers for them and the game's own counter
/// said four remained - and the markers come from the same list the browser does, one per
/// entity at its own position, so twelve entries landing on four dots means twelve entries
/// on four positions.
///
/// It is not our tree walk. That keeps a visited set of node addresses and collects into a
/// dictionary keyed by entity id, so neither a node nor an id can be taken twice. The
/// duplicates therefore carry DIFFERENT ids from DIFFERENT nodes, which is a fact about the
/// game's map rather than about the reader.
///
/// Two shapes remain, and they want different fixes, which is the whole reason to count them
/// separately rather than report one "duplicates" number:
///
///   - SHARED ADDRESS: several map keys pointing at one entity object. Literally the same
///     monster read twice, and collapsing them is safe because there is only one of it.
///   - SHARED PLACE: separate objects sitting on a byte-identical position. Collapsing those
///     is a judgement - a pack still stacked on its spawn point looks the same - which is
///     why it is worth knowing whether the cheaper answer suffices before reaching for it.
///
/// GameHelper2's AuraTracker guards against the second with a path-and-position key and
/// against the first with a group-by on id. Writing both means it met both.
///
/// What it costs to be wrong about this: the damage meter credits a monster's remaining pool
/// once per entry when it dies, so a monster listed three times is counted three times.
/// </remarks>
/// <param name="Entries">Monsters in the snapshot, counted as the list presents them.</param>
/// <param name="Addresses">How many distinct entity objects those entries point at.</param>
/// <param name="Places">How many distinct path-and-position combinations they occupy.</param>
public readonly record struct EntityDuplicates(int Entries, int Addresses, int Places)
{
    /// <summary>Entries that were another entry's object all along.</summary>
    public int SharedObject => Math.Max(0, Entries - Addresses);

    /// <summary>Entries standing where another entry already stood.</summary>
    /// <remarks>
    /// Informational, NOT a fault on its own - which the recorded session proves: it holds
    /// pairs of Firewall anomalies on one tile, separate objects that the game stacks
    /// because that is what a wall of fire is. They classify as monsters by their path, so
    /// a rule keyed on position would quietly delete half of every overlapping effect.
    /// </remarks>
    public int SharedPlace => Math.Max(0, Entries - Places);

    /// <summary>
    /// Whether the list is really saying the same thing twice.
    /// </summary>
    /// <remarks>
    /// On the OBJECT alone. Sharing a place is what stacked effects do on purpose; sharing a
    /// Render component is one thing wearing two entities, which is the fault.
    /// </remarks>
    public bool Any => SharedObject > 0;

    /// <summary>Counts the monsters in a snapshot, and how much of it is repetition.</summary>
    /// <remarks>
    /// Monsters only. Ground effects legitimately stack - several instances of one spell on
    /// one tile is what a spell looks like - so counting them would bury the signal under
    /// something that is not a fault.
    /// </remarks>
    public static EntityDuplicates Of(IEnumerable<WorldEntity> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);

        var addresses = new HashSet<ulong>();
        var places = new HashSet<(string Path, int X, int Y, int Z)>();
        int entries = 0;

        foreach (WorldEntity entity in entities)
        {
            if (entity.Kind != EntityKind.Monster)
            {
                continue;
            }

            entries++;

            // The RENDER component, not the entity address. The entity address is not an
            // identity here - the game hands one monster several entity objects - so
            // counting those would report every real monster as unique and find nothing.
            addresses.Add(entity.Render);

            // The reference's key: the exact BITS of the position rather than the value.
            // Two monsters standing on coordinates that agree to the last bit of three
            // floats are not two monsters, and comparing with a tolerance would sweep in
            // genuinely adjacent ones.
            places.Add((
                entity.Path,
                BitConverter.SingleToInt32Bits(entity.WorldX),
                BitConverter.SingleToInt32Bits(entity.WorldY),
                BitConverter.SingleToInt32Bits(entity.WorldZ)));
        }

        return new EntityDuplicates(entries, addresses.Count, places.Count);
    }

    /// <summary>The finding in one line, said in terms of what it would mean.</summary>
    public string Describe()
    {
        if (Entries == 0)
        {
            return "no monsters here to count";
        }

        string counts = $"{Entries} monsters, {Addresses} objects, {Places} places";

        if (!Any)
        {
            // Said out loud rather than left blank, because a place shared by separate
            // objects is the ordinary state of a stacked ground effect and somebody
            // comparing this row against the entity list should not read it as a finding.
            return SharedPlace > 0
                ? $"{counts} - nothing repeated ({SharedPlace} stacked on a shared spot, which is normal)"
                : $"{counts} - nothing repeated";
        }

        return $"{counts}  -  {SharedObject} share an object: one monster wearing several entities";
    }
}
