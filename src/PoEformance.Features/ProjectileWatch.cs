using PoEformance.Game.World;

namespace PoEformance.Features;

/// <summary>One place a projectile has been, in world coordinates.</summary>
/// <remarks>
/// World rather than screen, so the trail is projected with the same matrix and at the same
/// moment as everything else drawn over the game. Kept in screen pixels it would be a trail
/// of where the camera USED to be pointing: the projection moves with the player, so a
/// remembered pixel stops meaning the place it was recorded at as soon as anybody walks.
/// </remarks>
public readonly record struct TrailPoint(float X, float Y, float Z);

/// <summary>One sort of projectile that is in the air right now.</summary>
/// <param name="Path">The metadata path, which is the only name most of these have.</param>
/// <param name="Mine">How many of them the game says are on the player's side.</param>
public readonly record struct ProjectileSort(string Path, int Count, int Mine);

/// <summary>
/// Follows the projectiles in flight, so each one can be drawn with the line it came along.
/// </summary>
/// <remarks>
/// WHY A TRAIL AND NOT JUST A DOT. A projectile crosses the screen in a few frames, so a mark
/// on its position is a thing that blinks - by the time the eye finds it, it is somewhere
/// else, and nothing about the mark says which way it is going. A short trail turns the same
/// data into a direction and a speed, which is what somebody looking at their own projectiles
/// is actually asking about: where did that volley go.
///
/// HERE RATHER THAN IN THE LAYER because it is state rather than drawing. The overlay redraws
/// the same snapshot on every VSync frame while the reader produces new ones far more slowly,
/// so a trail built by appending once per DRAW would be a hundred copies of the same point per
/// real position. The rule that avoids it is the honest one anyway: append only where the
/// position CHANGED. A repeated frame adds nothing, a moving projectile always adds, and
/// nothing has to know how the two threads are paced.
///
/// It is also the half a test can reach - the overlay is Windows-only and the tests are not.
/// </remarks>
public sealed class ProjectileWatch
{
    /// <summary>How many positions a trail keeps. Beyond this the oldest goes.</summary>
    /// <remarks>
    /// Short deliberately. The trail is meant to say "this came from over there, going that
    /// way", and a long one turns a screen of volleys into a plate of spaghetti - which hides
    /// the projectiles it was drawn to reveal.
    /// </remarks>
    public const int MostPoints = 12;

    /// <summary>Drop a track the game has not listed for this long.</summary>
    /// <remarks>
    /// Short, because a projectile is short: the thing this holds is gone within a second of
    /// the game dropping it, and a longer memory would only leave dead trails hanging in the
    /// air. Not zero, because the entity list is walked with a cap on it and a busy frame can
    /// miss one sighting of something still in flight.
    /// </remarks>
    public int ForgetMs { get; init; } = 400;

    private sealed class Track
    {
        /// <summary>
        /// What the id belonged to when the track started.
        /// </summary>
        /// <remarks>
        /// Entity ids are handed out again after the entity that had one is gone, and in a
        /// fight of arrows that happens constantly. Without this the new arrow inherits the
        /// old one's trail and is drawn with a line reaching back to wherever the last one
        /// died. The Render component is what identifies the OBJECT - the same reasoning the
        /// corpse filter is keyed on.
        /// </remarks>
        public ulong Render;
        public long Seen;
        public readonly List<TrailPoint> Points = new(MostPoints);
    }

    private static readonly TrailPoint[] NoTrail = [];

    private readonly Dictionary<uint, Track> _tracks = [];
    private uint _area;

    /// <summary>How many projectiles are being followed, for a diagnostic readout.</summary>
    public int Tracking => _tracks.Count;

    /// <summary>Whether this entity is one of the things a projectile overlay is about.</summary>
    public static bool Is(WorldEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return entity.Kind == EntityKind.Projectile;
    }

    /// <summary>Takes one snapshot's worth of projectiles and extends their trails.</summary>
    public void Update(WorldSnapshot snapshot, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // An area change hands every address and id out again, so nothing held can be matched
        // against anything in the new one.
        if (snapshot.AreaHash != _area)
        {
            _area = snapshot.AreaHash;
            _tracks.Clear();
        }

        foreach (WorldEntity entity in snapshot.Entities)
        {
            // Remembered sightings are excluded on principle: they are what the entity looked
            // like when the game last listed it, so following one would extend a trail with a
            // position that has not been read since. Nothing remembers a projectile anyway -
            // see EntityMemory - which makes this a guard rather than a filter.
            if (!Is(entity) || entity.IsRemembered)
            {
                continue;
            }

            if (!_tracks.TryGetValue(entity.Id, out Track? track) || track.Render != entity.Render)
            {
                track = new Track { Render = entity.Render };
                _tracks[entity.Id] = track;
            }

            track.Seen = nowMs;

            var at = new TrailPoint(entity.WorldX, entity.WorldY, entity.WorldZ);
            if (track.Points.Count > 0 && track.Points[^1] == at)
            {
                continue; // the same frame again, or a projectile that has stopped
            }

            track.Points.Add(at);
            if (track.Points.Count > MostPoints)
            {
                track.Points.RemoveAt(0);
            }
        }

        Forget(nowMs);
    }

    /// <summary>Where this projectile has been, oldest first. Empty when it is new.</summary>
    public IReadOnlyList<TrailPoint> TrailOf(WorldEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return _tracks.TryGetValue(entity.Id, out Track? track) && track.Render == entity.Render
            ? track.Points
            : NoTrail;
    }

    /// <summary>What is in the air right now, grouped by path and commonest first.</summary>
    /// <remarks>
    /// The half that makes this answerable rather than decorative. Which entity the game
    /// spawns for a given skill is not written down anywhere we can read, so the list IS the
    /// reference: cast something, see what turns up, and the name of the thing your build puts
    /// on the screen is on the tab in front of you.
    /// </remarks>
    public static IReadOnlyList<ProjectileSort> Sorts(WorldSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var counts = new Dictionary<string, (int Count, int Mine)>(StringComparer.Ordinal);
        foreach (WorldEntity entity in snapshot.Entities)
        {
            if (!Is(entity))
            {
                continue;
            }

            counts.TryGetValue(entity.Path, out (int Count, int Mine) was);
            counts[entity.Path] = (was.Count + 1, was.Mine + (entity.IsFriendly ? 1 : 0));
        }

        var sorts = new List<ProjectileSort>(counts.Count);
        foreach ((string path, (int count, int mine)) in counts)
        {
            sorts.Add(new ProjectileSort(path, count, mine));
        }

        sorts.Sort((left, right) => right.Count.CompareTo(left.Count));
        return sorts;
    }

    /// <summary>Drops the tracks whose projectiles the game has stopped listing.</summary>
    private void Forget(long nowMs)
    {
        foreach ((uint id, Track track) in _tracks)
        {
            if (nowMs - track.Seen > ForgetMs)
            {
                _tracks.Remove(id);
            }
        }
    }
}
