using PoEformance.Game.World;

namespace PoEformance.Features;

/// <summary>Something worth saying, once.</summary>
/// <param name="Also">How many other things matched at the same moment.</param>
/// <param name="Distance">How far away the thing was, in world units.</param>
public readonly record struct Alert(string Rule, string What, int Also, float Distance, long AtMs)
{
    /// <summary>What a banner says.</summary>
    public string Line => Also > 0
        ? $"{Rule}: {What}  (+{Also} more)"
        : $"{Rule}: {What}";
}

/// <summary>
/// Watches each snapshot and says when something worth knowing about turned up.
/// </summary>
/// <remarks>
/// THE FAILURE MODE IS SPAM, not missing things, and everything in here is shaped by that. A
/// radar that interrupts constantly gets switched off within a map, at which point it also
/// misses everything - so a rule that fires too often is worse than one that does not exist.
///
/// Four things keep it quiet, and all four are load-bearing:
///
///   - ONCE PER ENTITY. Told about a unique monster on the tick it appears, then never again
///     while it stands there. Without this the same monster is announced sixty times a second.
///   - ONE BANNER PER MOMENT. Several matches collapse into the highest-priority one, with a
///     count of the rest. Entering a map otherwise announces everything in it, one after
///     another.
///   - A GAP BETWEEN BANNERS. Even distinct things wait their turn, so walking into a room
///     full of them is one line rather than a strobe.
///   - NOTHING IN TOWN. Nothing there is an event, and a hideout full of them is what makes
///     somebody turn the feature off before they have seen it work.
///
/// Identity is the game's ENTITY ID rather than its address. Addresses get recycled inside an
/// area as things die and spawn, so a recycled one reads as "already told you about that" and
/// the alert is silently lost - the failure you would never trace, because the missing thing
/// leaves nothing behind.
/// </remarks>
public sealed class AlertWatcher
{
    /// <summary>
    /// Shortest gap between two banners.
    /// </summary>
    /// <remarks>
    /// Long enough to read the last one. Below about a second the banners queue behind each
    /// other and the whole thing reads as a flicker rather than as information.
    /// </remarks>
    public const long QuietMs = 2_500;

    /// <summary>
    /// Most entities remembered as "already mentioned" within one area.
    /// </summary>
    /// <remarks>
    /// A bound rather than a real limit: a map has thousands of entities and only the matched
    /// ones land here, so it should never be reached. It is here because "never reached" is a
    /// claim about the game, and an unbounded set that grows for as long as somebody stays in
    /// an area is not a claim worth betting a long session on.
    /// </remarks>
    public const int MaxRemembered = 4096;

    private readonly HashSet<uint> _mentioned = [];
    private uint _area;

    // Null rather than a "long ago" sentinel. long.MinValue looks like the obvious one and is
    // a trap: `now - long.MinValue` overflows to a NEGATIVE gap, so the quiet check reads as
    // "too soon" and the first alert of a session is silently swallowed - forever, since the
    // sentinel never gets replaced. Nothing about that is visible; it just never works.
    private long? _lastAt;

    /// <summary>The rules being watched for. Replaceable, so an editor can change them live.</summary>
    public IReadOnlyList<AlertRule> Rules { get; set; } = DefaultAlerts.Rules;

    /// <summary>Whether anything is watched at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Whether to keep quiet in towns and hideouts.</summary>
    public bool QuietInTown { get; set; } = true;

    /// <summary>The most recent alert, for whoever draws it.</summary>
    public Alert? Latest { get; private set; }

    /// <summary>How many alerts have been raised. For a status readout, and for tests.</summary>
    public int Raised { get; private set; }

    /// <summary>
    /// Looks at one snapshot and returns an alert when there is something to say.
    /// </summary>
    /// <param name="nowMs">
    /// The clock, passed in rather than read. A test that cannot control time cannot check the
    /// quiet gap, and the quiet gap is most of what this class is.
    /// </param>
    public Alert? Look(WorldSnapshot snapshot, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!Enabled || !snapshot.InGame || snapshot.Player is not WorldEntity player)
        {
            return null;
        }

        // A new instance means new entities, and the old ids mean nothing about them. Done
        // before the town check, so the walk back out of a hideout starts clean.
        if (snapshot.AreaHash != _area)
        {
            _area = snapshot.AreaHash;
            _mentioned.Clear();
        }

        if (QuietInTown && !snapshot.Area.IsHostile)
        {
            return null;
        }

        AlertRule? best = null;
        WorldEntity? subject = null;
        float subjectDistance = 0f;
        int matched = 0;

        foreach (WorldEntity entity in snapshot.Entities)
        {
            if (_mentioned.Contains(entity.Id))
            {
                continue;
            }

            float distance = Distance(entity, player);
            AlertRule? hit = FirstMatch(entity, distance);
            if (hit is null)
            {
                continue;
            }

            Remember(entity.Id);
            matched++;

            if (best is null || hit.Priority > best.Priority)
            {
                best = hit;
                subject = entity;
                subjectDistance = distance;
            }
        }

        if (best is null || subject is null)
        {
            return null;
        }

        // Everything matched is still MARKED as mentioned above, even when the gap swallows
        // this banner. Otherwise a busy room re-matches the same things every tick and the
        // moment the gap expires it announces something that has been standing there for
        // twenty seconds.
        if (_lastAt is long last && nowMs - last < QuietMs)
        {
            return null;
        }

        _lastAt = nowMs;
        Raised++;
        Latest = new Alert(best.Name, Describe(subject), matched - 1, subjectDistance, nowMs);
        return Latest;
    }

    /// <summary>The first rule an entity satisfies, taking the highest priority among them.</summary>
    private AlertRule? FirstMatch(WorldEntity entity, float distance)
    {
        AlertRule? best = null;
        foreach (AlertRule rule in Rules)
        {
            if (rule.Matches(entity, distance) && (best is null || rule.Priority > best.Priority))
            {
                best = rule;
            }
        }

        return best;
    }

    /// <summary>
    /// What to call the thing in the banner.
    /// </summary>
    /// <remarks>
    /// The game's own name for a place, and the path's last segment otherwise. "Breach" beats
    /// "BreachObject_01", and for everything else the file name is the only name there is.
    /// </remarks>
    private static string Describe(WorldEntity entity)
        => entity.Poi != PoiKind.None ? entity.PoiName : entity.ShortName;

    private static float Distance(WorldEntity entity, WorldEntity player)
    {
        float dx = entity.WorldX - player.WorldX;
        float dy = entity.WorldY - player.WorldY;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }

    private void Remember(uint id)
    {
        if (_mentioned.Count >= MaxRemembered)
        {
            // Starting over is the honest failure: it may repeat an alert somebody has
            // already seen, which is a great deal better than silently going deaf for the
            // rest of the area.
            _mentioned.Clear();
        }

        _mentioned.Add(id);
    }

    /// <summary>Forgets everything, so the same area can announce itself again.</summary>
    public void Forget()
    {
        _mentioned.Clear();
        _lastAt = null;
        Latest = null;
    }
}
