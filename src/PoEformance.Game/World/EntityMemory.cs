using PoEformance.Game.Ui;

namespace PoEformance.Game.World;

/// <summary>
/// Keeps the things the game has stopped listing because the player walked away from them.
/// </summary>
/// <remarks>
/// THE ENTITY LIST IS A BUBBLE, NOT AN AREA. AwakeEntities holds what is near the player, so
/// an exit found on the way in, a strongbox left for later and a unique nobody picked up all
/// leave the list the moment they are far enough behind - and with them everything this tool
/// had already worked out about them. Nothing about that is a failure to read: the read is
/// correct, the game simply is not saying any more. What is missing is a MEMORY.
///
/// The reference solves it the same way and it is worth stating exactly how, because the two
/// halves are easy to get individually right and jointly useless. GameHelper2's AreaInstance
/// keeps its entities in a dictionary across frames, marks each one invalid when the game
/// stops listing it, and only DELETES it when
///
///     entity.CanExplodeOrRemovedFromGame &amp;&amp; player.DistanceFrom(entity) &lt; NETWORK_BUBBLE_RADIUS
///
/// - that is, when it vanished while the player was standing close enough to have seen it go.
/// Its Radar draws the invalid ones by default; hiding them is an opt-in setting
/// (HideOutsideNetworkBubble). So: absence at a distance is not evidence, absence up close is.
///
/// That second half is the whole safety of this. A chest opened, an item picked up, a shrine
/// taken, an encounter finished - all of them happen with the player standing on top of them,
/// inside the bubble, where the disappearance IS the answer. Only the disappearances that
/// happen out of range are treated as ignorance rather than as news.
///
/// WHAT IS REMEMBERED IS DELIBERATELY NARROW: things that do not move. A place stays where it
/// was found and a drop stays on the floor, so a marker drawn from memory says exactly what it
/// said when it was read. A monster does not, and a remembered monster dot would be a claim
/// about where something is that is wrong the instant it is made - which is the opposite of
/// what an overlay is for, and worse than the empty map it replaces. The reference keeps
/// monsters too; this deliberately does not follow it there.
/// </remarks>
public sealed class EntityMemory
{
    /// <summary>
    /// How close the player must be for a disappearance to count as the thing being gone.
    /// </summary>
    /// <remarks>
    /// In GRID cells, which is what the reference measures in:
    /// <c>AreaInstanceConstants.NETWORK_BUBBLE_RADIUS = 150</c>, with its own note that the
    /// real value is nearer 200, differs per entity type, and was lowered to 150 to remove
    /// false positives.
    ///
    /// MEASURED AGAINST THE RECORDED MAP rather than taken on trust, because this one number
    /// decides whether the memory works or eats itself. Every time a standing thing left the
    /// entity list over that map - 393 of them - how far it was from the player at that moment:
    ///
    /// <code>
    ///   consumed underfoot   1, 8, 8, 10 ... 24, 26     and one at 49
    ///   nothing at all       between 49 and 165
    ///   out of range         165, 166, 167 ... up to 2721
    /// </code>
    ///
    /// The two ways of leaving the list are not a spectrum, they are two clusters with an empty
    /// band between them, and the threshold sits in that band - which is why it can be wrong in
    /// neither direction here. The lone 49 was one frame's hole in the read: the game listed
    /// that item again on the very next frame. This client's bubble is nearer 165 cells than
    /// the 150 the reference settled on, so the reference's number is if anything the more
    /// conservative of the two, and it is kept.
    /// </remarks>
    public const float ForgetWithinCells = 150f;

    /// <summary>How many sightings are worth keeping before the farthest give way.</summary>
    /// <remarks>
    /// A bound on a dictionary fed by a whole map's worth of floor drops, not a view about how
    /// many places an area has - those run to dozens. A long map with nothing picked up is
    /// what fills this, and the farthest ones are the first to go because they are the ones
    /// nobody is walking back for.
    /// </remarks>
    public const int MostRemembered = 1024;

    /// <summary>One thing the game listed once, and when it last did.</summary>
    private readonly record struct Sighting(WorldEntity Entity, long At);

    /// <summary>What has been seen, by ENTITY ID.</summary>
    /// <remarks>
    /// Not by address, and that is measured rather than assumed. Over the recorded map, 200
    /// things came back into the game's own list after having been remembered, and 98 of them
    /// came back AT A DIFFERENT ADDRESS - the same object, the same id, a new entity pointer.
    /// Keyed on the address, every one of those would have been a second marker standing on
    /// top of the first, with nothing to say they were the same thing. The reference keys its
    /// own cache the same way and updates the pointer in place, which is the same finding
    /// arrived at from the other side.
    /// </remarks>
    private readonly Dictionary<uint, Sighting> _seen = [];

    /// <summary>The area these sightings belong to. Ids mean something else in the next one.</summary>
    private uint _area;

    /// <summary>
    /// Whether anything is remembered at all. On; off is for seeing the raw read.
    /// </summary>
    /// <remarks>
    /// Turning it off CLEARS what is held, so switching it back on starts from what the game
    /// is saying now rather than resurrecting sightings from before it was turned off.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>How many sightings are currently held, for a diagnostic readout.</summary>
    public int Count => _seen.Count;

    /// <summary>
    /// Whether this is a thing worth keeping after the game stops listing it.
    /// </summary>
    /// <remarks>
    /// Static things only - see the type's remarks for why a monster is not one. Ordered so
    /// the kinds decide first: a rare monster CAN carry a MinimapIcon, which makes it a
    /// point of interest by the icon rule, and a boss remembered where it was standing an
    /// hour ago is exactly the marker this must never draw.
    ///
    /// Ordinary containers - the pots and passage chests that carpet every map - are not
    /// places by <see cref="PointsOfInterest.Classify(string)"/> and so are not remembered
    /// either. That is the same judgement the map markers already make, arrived at once.
    /// </remarks>
    public static bool WorthRemembering(WorldEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return entity.Kind switch
        {
            // A drop stays on the floor. Which one it was and how good it was is the whole
            // value: "there is a rare back at the entrance" is a thing worth being told.
            EntityKind.WorldItem => true,

            EntityKind.Monster or EntityKind.Player or EntityKind.Effect => false,

            _ => entity.IsPlace,
        };
    }

    /// <summary>
    /// Takes what the game listed this read, and hands back what it has stopped listing.
    /// </summary>
    /// <param name="areaHash">Which area this is. A change wipes everything held.</param>
    /// <param name="live">The entities read this frame, in the order they were read.</param>
    /// <param name="player">
    /// Where the player is, for the up-close rule. Null - a frame that could not resolve them -
    /// forgets nothing, because the rule cannot be applied without somewhere to measure from.
    /// </param>
    /// <returns>
    /// The remembered entities, each carrying how long ago the game last listed it. Never
    /// includes anything in <paramref name="live"/>: what the game is saying now always wins.
    /// </returns>
    public IReadOnlyList<WorldEntity> Update(
        uint areaHash, IReadOnlyList<WorldEntity> live, WorldEntity? player, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(live);

        if (!Enabled)
        {
            _seen.Clear();
            return [];
        }

        if (areaHash != _area)
        {
            // A new area hands out the same entity ids for entirely different things, and the
            // positions of the last one are somewhere else entirely. Everything goes.
            _area = areaHash;
            _seen.Clear();
        }

        var listed = new HashSet<uint>(live.Count);
        var objects = new HashSet<ulong>(live.Count);

        foreach (WorldEntity entity in live)
        {
            listed.Add(entity.Id);

            // Zero is "no Render component read", not an object. The reader never lets one
            // through - an entity without a position has nothing to draw - but two of them
            // matching each other would silently delete a sighting, which is the kind of
            // thing that would be looked for everywhere but here.
            if (entity.Render != 0)
            {
                objects.Add(entity.Render);
            }

            // Removed first, then re-added only if it still qualifies. The two are not the
            // same statement: a chest that has just been opened, or a place whose Targetable
            // byte has turned to "not really there", stops being worth remembering - and
            // leaving the older sighting in place would keep re-drawing the state it had
            // before the change the read just saw.
            _seen.Remove(entity.Id);

            if (WorthRemembering(entity))
            {
                _seen[entity.Id] = new Sighting(entity, nowMs);
            }
        }

        Trim(player);

        float forgetWithin = ForgetWithinCells * MapView.WorldToGrid;
        var remembered = new List<WorldEntity>();

        foreach ((uint id, Sighting sighting) in _seen)
        {
            if (listed.Contains(id))
            {
                continue;   // the game is listing it: it travels as itself, not as a memory
            }

            // The same OBJECT under a different entity id. The game wears several entities on
            // one set of components and which of them the map walk yields can change; without
            // this, the old id's sighting would be drawn beside the live entity as a second
            // marker standing on the same spot.
            if (sighting.Entity.Render != 0 && objects.Contains(sighting.Entity.Render))
            {
                _seen.Remove(id);
                continue;
            }

            // Gone from under the player's feet: opened, picked up, expired, or walked off.
            // This is the reference's rule and the reason a memory does not accumulate lies.
            if (player is WorldEntity at && DistanceFrom(sighting.Entity, at) <= forgetWithin)
            {
                _seen.Remove(id);
                continue;
            }

            remembered.Add(sighting.Entity with
            {
                RememberedForMs = (int)Math.Clamp(nowMs - sighting.At, 0, int.MaxValue),
            });
        }

        return remembered;
    }

    /// <summary>Forgets everything, for a reader starting over.</summary>
    public void Clear()
    {
        _seen.Clear();
        _area = 0;
    }

    /// <summary>Drops the farthest sightings once there are more than are worth holding.</summary>
    /// <remarks>
    /// By DISTANCE rather than by age, where there is a player to measure from. The oldest
    /// sighting in a map is usually its entrance - a checkpoint, the way back - which is the
    /// last marker anybody would choose to lose, while the farthest is by definition the one
    /// least likely to be walked back to.
    /// </remarks>
    private void Trim(WorldEntity? player)
    {
        int excess = _seen.Count - MostRemembered;
        if (excess <= 0)
        {
            return;
        }

        List<uint> dropped = player is WorldEntity at
            ? [.. _seen.OrderByDescending(seen => DistanceFrom(seen.Value.Entity, at))
                .Take(excess).Select(seen => seen.Key)]
            : [.. _seen.OrderBy(seen => seen.Value.At).Take(excess).Select(seen => seen.Key)];

        foreach (uint id in dropped)
        {
            _seen.Remove(id);
        }
    }

    /// <summary>Flat distance in world units - the plane the bubble is measured on.</summary>
    private static float DistanceFrom(WorldEntity entity, WorldEntity player)
    {
        float dx = entity.WorldX - player.WorldX;
        float dy = entity.WorldY - player.WorldY;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }
}
