using PoEformance.Game.Components;
using PoEformance.Game.Ui;
using PoEformance.Game.World;

namespace PoEformance.Features;

/// <summary>
/// Which of the arenas found in the ground has had its boss put down.
/// </summary>
/// <remarks>
/// WHAT THIS IS FOR. The game draws a boss landmark two ways - <c>…BossActive</c> while the
/// thing is alive and <c>…BossInactive</c> once it is not - and the sheet carries both. The
/// arena markers found in the tiles have no such state of their own, so it is worked out here:
/// the picture then says "still to do" or "done" the way the game's own markers do, which on a
/// map with two arenas is the difference between a marker and a decision.
///
/// WHY NOT ASK THE GAME, since it plainly knows. Its own marker entity carries the Active or
/// Inactive name and flips it correctly by construction - but that entity is only in the list
/// once the player is close enough for the game to list it, and it is dropped as noise before
/// anything here sees it (NoiseFilter, "bossroomminimapicon"). Both of those are fixable and
/// neither is free, and the arena wants an answer from across the map, which the entity cannot
/// give. So the state is observed rather than read.
///
/// THE RULE, and it is deliberately conservative in the direction that matters. Clearing an
/// arena that still holds its boss draws "done" over something that will kill you; leaving a
/// cleared one Active draws what it drew yesterday. So a clear needs all three of:
///
///   - a unique monster was seen INSIDE the arena, so there was something to kill;
///   - the player is standing in the arena, so the kill would have been witnessed;
///   - nothing unique has been inside it for <see cref="ClearAfterMs"/>.
///
/// AND IT UNDOES ITSELF. A boss that goes away for a phase and comes back un-clears its arena
/// the moment it is inside again. That is worth more than a longer timer: it means the timer
/// only has to be long enough for the usual case, and the unusual one corrects on screen
/// rather than staying wrong until the area is left.
///
/// Held per AREA INSTANCE. A map re-entered through a portal is the same instance and keeps
/// what was learned; a new map has its own boss, and the hash changing is what says so.
/// </remarks>
public sealed class BossArenas
{
    /// <summary>
    /// How far from an arena's centre still counts as being in it, in world units.
    /// </summary>
    /// <remarks>
    /// A tile is 250 world units and <see cref="TerrainLandmarks"/> clusters tiles within three
    /// of each other into one place, with the landmark at the cluster's centre - so an arena's
    /// half-width is a small number of tiles. Two and a half of them covers the rooms seen
    /// without reaching into the map around them.
    ///
    /// ERRING LARGE IS THE SAFE DIRECTION HERE: too wide counts a monster outside the room as
    /// inside it, which keeps the marker Active, while too narrow puts the boss outside its own
    /// arena and clears it while the fight is on.
    /// </remarks>
    public float ArenaRadius { get; set; } = 600f;

    /// <summary>How long the arena must hold no unique before it counts as done.</summary>
    /// <remarks>
    /// Long enough to sit out the pause where a boss is between phases and briefly not in the
    /// list, short enough that the marker has changed by the time the loot is picked up. A
    /// boss that comes back later un-clears the arena anyway, so this is a delay rather than a
    /// judgement.
    /// </remarks>
    public long ClearAfterMs { get; set; } = 4_000;

    private readonly Dictionary<ulong, long> _emptySince = [];
    private readonly HashSet<ulong> _seen = [];
    private readonly HashSet<ulong> _cleared = [];
    private readonly List<(float X, float Y)> _uniques = [];
    private uint _area;

    /// <summary>How many arenas are marked done in the area being watched.</summary>
    public int ClearedCount => _cleared.Count;

    /// <summary>Whether this arena's boss has been put down, as far as anybody here saw.</summary>
    public bool IsCleared(ulong landmarkId) => _cleared.Contains(landmarkId);

    /// <summary>Forgets everything, which is what entering a new area does.</summary>
    public void Forget()
    {
        _emptySince.Clear();
        _seen.Clear();
        _cleared.Clear();
    }

    /// <summary>
    /// Watches one frame.
    /// </summary>
    /// <remarks>
    /// CALLED EVERY FRAME AND CHEAP BY SHAPE: one pass over the entities to pick out the
    /// uniques - a handful at most, against thousands in the snapshot - and then a distance per
    /// arena per unique, where an area has one or two arenas. Nothing is allocated per frame:
    /// the uniques go into a list that is cleared and refilled.
    ///
    /// Not tied to entering an area, and not tied to the map being open: the kill happens while
    /// somebody is fighting, which is the one moment they are not looking at their map.
    /// </remarks>
    public void Note(WorldSnapshot snapshot, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.AreaHash != _area)
        {
            _area = snapshot.AreaHash;
            Forget();
        }

        if (snapshot.Terrain is not TerrainGrid terrain
            || terrain.Landmarks.Count == 0
            || snapshot.Player is not WorldEntity player)
        {
            return;
        }

        _uniques.Clear();
        foreach (WorldEntity entity in snapshot.Entities)
        {
            // Alive, hostile and unique. A remembered sighting is not evidence that anything is
            // standing there - EntityMemory never keeps monsters, and a boss recalled from
            // where it stood an hour ago is exactly the marker this must not believe.
            if (entity.Kind == EntityKind.Monster
                && entity.Rarity == ItemRarity.Unique
                && !entity.IsFriendly
                && !entity.IsRemembered)
            {
                _uniques.Add((entity.WorldX, entity.WorldY));
            }
        }

        float radius = ArenaRadius * ArenaRadius;

        foreach (TerrainLandmark landmark in terrain.Landmarks)
        {
            if (landmark.Kind != PoiKind.BossArena)
            {
                continue;
            }

            float centreX = landmark.GridX * MapView.WorldToGrid;
            float centreY = landmark.GridY * MapView.WorldToGrid;

            if (Holds(centreX, centreY, radius))
            {
                // Something is in there. Whether it arrived, came back from a phase, or was
                // never gone, the arena is not done - and saying so again costs one set write.
                _seen.Add(landmark.Id);
                _cleared.Remove(landmark.Id);
                _emptySince.Remove(landmark.Id);
                continue;
            }

            // Only what the player could have witnessed. An arena emptying while nobody is in
            // it is the game not listing what is over there, not a kill.
            if (!_seen.Contains(landmark.Id)
                || Away(player, centreX, centreY) > radius)
            {
                _emptySince.Remove(landmark.Id);
                continue;
            }

            if (!_emptySince.TryGetValue(landmark.Id, out long since))
            {
                _emptySince[landmark.Id] = nowMs;
                continue;
            }

            if (nowMs - since >= ClearAfterMs)
            {
                _cleared.Add(landmark.Id);
            }
        }
    }

    /// <summary>Whether any living unique stands within the arena.</summary>
    private bool Holds(float centreX, float centreY, float radiusSquared)
    {
        foreach ((float x, float y) in _uniques)
        {
            float dx = x - centreX;
            float dy = y - centreY;
            if ((dx * dx) + (dy * dy) <= radiusSquared)
            {
                return true;
            }
        }

        return false;
    }

    private static float Away(WorldEntity player, float centreX, float centreY)
    {
        float dx = player.WorldX - centreX;
        float dy = player.WorldY - centreY;
        return (dx * dx) + (dy * dy);
    }
}
