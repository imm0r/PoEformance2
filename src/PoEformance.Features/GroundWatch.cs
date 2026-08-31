using System.Text.Json.Serialization;
using PoEformance.Game.World;

namespace PoEformance.Features;

/// <summary>One patch of dangerous-looking ground the session has seen, and what is known of it.</summary>
/// <param name="Path">The metadata path - what a GroundDangerRule matches on.</param>
/// <param name="HasComponent">
/// Whether the game itself marks it with a GroundEffect component. The single most useful column
/// in the list: TRUE means the component ring already covers it and no rule is needed, FALSE
/// means a rule is the only way it will ever be marked.
/// </param>
/// <param name="OnScreen">Whether one is in the area right now, rather than remembered.</param>
/// <param name="Most">The most seen at once. A path that only ever appears singly is a different
/// thing from one that arrives in twenties, and a rule on the second will fill the screen.</param>
/// <param name="Radius">The candidate radius when the component gave one, else 0.</param>
/// <param name="LastSeenMs">When it was last in the area, for sorting and expiry.</param>
/// <remarks>
/// EVERY PROPERTY IS NAMED FOR THE WIRE, the same trap SeenBuff records: the config window's
/// serializer sets no naming policy, so a record that forgets its JSON names crosses as
/// "Path"/"OnScreen" while the page reads path/onScreen. Nothing throws - the list arrives with
/// the right number of rows and every field undefined, and the picker offers "undefined".
/// </remarks>
public sealed record SeenGround(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("hasComponent")] bool HasComponent,
    [property: JsonPropertyName("onScreen")] bool OnScreen,
    [property: JsonPropertyName("most")] int Most,
    [property: JsonPropertyName("radius")] float Radius,
    [property: JsonPropertyName("lastSeenMs")] long LastSeenMs);

/// <summary>
/// Remembers what dangerous-looking ground a session has seen, so a rule need not be typed blind.
/// </summary>
/// <remarks>
/// THE PROBLEM IT SOLVES IS THE ONE THE BUFF LIST SOLVES, and it is worse here. A rule matches a
/// metadata path - `Metadata/Monsters/MonsterMods/GroundOnDeath/BurningGroundDaemonParent@75` -
/// which is written nowhere a player can see, differs per skill and per league mechanic, and has
/// to be typed exactly enough to match and loosely enough to cover the variants. Getting it from
/// a list of what the game just showed you is the only workflow that works; typing it from
/// memory is how the feature ends up drawing nothing.
///
/// WHY IT REMEMBERS RATHER THAN LISTING WHAT IS THERE NOW: the ground that killed somebody is
/// gone by the time they have alt-tabbed to the config window. Ground effects run 14 to 50
/// seconds and the short ones much less, so a live list would be empty exactly when it is wanted.
///
/// WHAT COUNTS AS DANGEROUS-LOOKING, stated because it is a judgement and a narrow answer would
/// hide the very entities somebody is hunting for. Three ways in, deliberately generous:
///  - it carries a GroundEffect component. Certain, and the ring already covers it.
///  - the reader classified it as an Effect. That is every `Metadata/Effects/...` path.
///  - its path mentions ground at all.
/// Friendly entities are never listed, matching GroundDangerRule.Matches: a rule cannot fire on
/// them, so offering them would be offering a rule that does nothing.
///
/// THE THIRD CLAUSE CANNOT REACH THE ENTITIES IT WAS WRITTEN FOR, and that is worth stating here
/// rather than being rediscovered from an empty dropdown. It was put in for the GroundOnDeath
/// monster mods - the burning, shocked and chilled ground a rare monster leaves behind, which
/// carries no GroundEffect component and is not classified as an Effect. They never arrive:
/// their paths run through `Metadata/Monsters/MonsterMods/...`, and NoiseFilter's Daemon class
/// matches "monstermods" and drops them in WorldReader BEFORE a snapshot exists. Measured, not
/// assumed - the whole sweep capture yields four rows here and not one of them is a daemon.
///
/// The clause stays, for two reasons. The filter is switchable per class, so with Daemon off
/// those paths do arrive and the clause is then the only thing that lists them; and the same
/// upstream drop means a GroundDangerRule written against such a path DRAWS NOTHING with the
/// filter on, whoever typed it - so a list that offered one would be promising a rule that
/// cannot fire. See docs/architecture.md; this is a limit of the reader, not of the list.
///
/// The list is BY PATH, not by entity - twenty burning patches of one kind are one row, with the
/// count beside it - which is what makes it a dropdown rather than a log.
/// </remarks>
public sealed class GroundWatch
{
    /// <summary>How many distinct paths are kept before the oldest are dropped.</summary>
    /// <remarks>
    /// Far below the buff list's bound because the key is a PATH: an area has a few dozen kinds
    /// of effect and hundreds of instances of them, and the instances collapse into the kinds.
    /// A session that reaches this has been through a lot of leagues in one sitting.
    /// </remarks>
    public const int MaxRemembered = 256;

    /// <summary>How long a path not seen again stays in the list.</summary>
    /// <remarks>
    /// Longer than the buff list's window, on the same reasoning pointed the other way: a buff
    /// list an hour old describes a fight nobody is in any more, while the ground effects of a
    /// league mechanic are exactly what somebody sits down to write rules about afterwards.
    /// </remarks>
    public const long RememberMs = 2 * 60 * 60 * 1000;

    private readonly Dictionary<string, SeenGround> _seen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Guards <see cref="_seen"/>, which two threads touch.
    /// </summary>
    /// <remarks>
    /// <see cref="Look"/> runs on the reader thread; <see cref="Seen"/> is read by the config
    /// window once a second. The same race BuffWatch guards, with the same consequence: a torn
    /// read or a throw both reach the page as "nothing seen", which is the one answer this must
    /// never invent.
    /// </remarks>
    private readonly Lock _gate = new();

    private long _looks;
    private int _lastCount;

    /// <summary>What has been seen: present ones first, then most recently seen.</summary>
    public IReadOnlyList<SeenGround> Seen
    {
        get
        {
            List<SeenGround> all;
            lock (_gate)
            {
                all = new List<SeenGround>(_seen.Values);
            }

            all.Sort(static (left, right) => left.OnScreen != right.OnScreen
                ? right.OnScreen.CompareTo(left.OnScreen)
                : right.LastSeenMs.CompareTo(left.LastSeenMs));

            return all;
        }
    }

    /// <summary>Why the list looks the way it does, for a panel to explain itself.</summary>
    /// <remarks>
    /// "NOBODY HAS LOOKED YET" IS A DIFFERENT ANSWER FROM "THERE IS NOTHING THERE" - the same
    /// distinction BuffWatch draws, and it matters more here because an empty list is the normal
    /// state in a hideout and the alarming state in a map.
    /// </remarks>
    public string LastRead
    {
        get
        {
            lock (_gate)
            {
                return _looks == 0
                    ? "the reader has not looked at the world yet"
                    : $"{_seen.Count} kinds remembered, {_lastCount} in the area on the last look";
            }
        }
    }

    /// <summary>Notes the dangerous-looking ground in this snapshot.</summary>
    public void Look(WorldSnapshot snapshot, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_gate)
        {
            // Counted BEFORE the early return, so "the reader is running and the area is clear"
            // cannot be mistaken for "the reader never ran".
            _looks++;

            if (!snapshot.InGame)
            {
                return;
            }

            // Everything remembered stops being PRESENT first, then what is really there is
            // marked again - otherwise a patch that burned out would sit in the list claiming to
            // be on screen forever, which is worse than not listing it.
            foreach ((string path, SeenGround seen) in _seen)
            {
                if (seen.OnScreen)
                {
                    _seen[path] = seen with { OnScreen = false };
                }
            }

            var thisLook = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int total = 0;
            foreach (WorldEntity entity in snapshot.Entities)
            {
                if (entity.IsRemembered || entity.IsFriendly || !IsHazardShaped(entity))
                {
                    continue;
                }

                total++;
                thisLook[entity.Path] = thisLook.GetValueOrDefault(entity.Path) + 1;

                SeenGround row = _seen.TryGetValue(entity.Path, out SeenGround? known)
                    ? known
                    : new SeenGround(entity.Path, false, false, 0, 0, nowMs);

                _seen[entity.Path] = row with
                {
                    // ONCE TRUE, ALWAYS TRUE for the component: an entity of a kind that carries
                    // one always does, and a frame where the read failed must not un-mark a
                    // whole path in the list somebody is about to make a decision from.
                    HasComponent = row.HasComponent || entity.IsGroundEffect,
                    OnScreen = true,
                    Radius = entity.GroundRadius ?? row.Radius,
                    LastSeenMs = nowMs,
                };
            }

            _lastCount = total;

            // The high-water mark per path, applied after counting because it needs the whole
            // frame: how many of a thing show up AT ONCE is what says whether a rule on it will
            // ring one patch or forty.
            foreach ((string path, int count) in thisLook)
            {
                SeenGround row = _seen[path];
                if (count > row.Most)
                {
                    _seen[path] = row with { Most = count };
                }
            }

            Forget(nowMs);
        }
    }

    /// <summary>
    /// Whether this entity is the sort of thing somebody would write a ground rule about.
    /// </summary>
    /// <remarks>
    /// Deliberately wider than "carries the component". The component covers only one metadata
    /// path in the sweep capture, so a list of just those would be a list of things the overlay
    /// already rings without any rule - a list with no reason to exist.
    ///
    /// The "ground" clause is the one that cannot deliver with the noise filter on; see the type
    /// remarks. It is kept because it costs a substring check and is correct the moment somebody
    /// turns the Daemon class off.
    /// </remarks>
    private static bool IsHazardShaped(WorldEntity entity)
        => entity.IsGroundEffect
           || entity.Kind == EntityKind.Effect
           || entity.Path.Contains("ground", StringComparison.OrdinalIgnoreCase);

    /// <summary>Drops what has not been seen for a while, and the oldest when full.</summary>
    private void Forget(long nowMs)
    {
        List<string>? stale = null;
        foreach ((string path, SeenGround seen) in _seen)
        {
            if (!seen.OnScreen && nowMs - seen.LastSeenMs > RememberMs)
            {
                (stale ??= []).Add(path);
            }
        }

        if (stale is not null)
        {
            foreach (string path in stale)
            {
                _seen.Remove(path);
            }
        }

        while (_seen.Count > MaxRemembered)
        {
            string? oldest = null;
            long when = long.MaxValue;
            foreach ((string path, SeenGround seen) in _seen)
            {
                if (!seen.OnScreen && seen.LastSeenMs < when)
                {
                    when = seen.LastSeenMs;
                    oldest = path;
                }
            }

            if (oldest is null)
            {
                break; // every row is on screen right now - dropping one would be a lie
            }

            _seen.Remove(oldest);
        }
    }
}
