using PoEformance.Game.Components;

namespace PoEformance.Features;

/// <summary>One buff the character has had on, and what it was doing when last looked at.</summary>
/// <param name="Name">
/// The ENGINE identifier, which is the only name a buff has from this side.
/// </param>
/// <param name="Active">Whether it is on right now, rather than remembered from earlier.</param>
public sealed record SeenBuff(
    string Name,
    bool Active,
    float TimeLeft,
    int Charges,
    int FlaskSlot,
    long LastSeenMs);

/// <summary>
/// Remembers which buffs a character has had on, so a rule need not be written by guesswork.
/// </summary>
/// <remarks>
/// THE NAME A RULE MATCHES IS NOT THE NAME THE GAME SHOWS. What memory carries is the Id
/// column of BuffDefinitions - the engine identifier, spelled like `flask_effect_life` - and
/// the localised display name is a different column that nothing here reads. So a player
/// looking at "Lightning Infusion" on their own screen has no way to work out what to type,
/// and the reference plugin's answer to that is its own debug window and a lot of scrolling.
///
/// The engine id is the RIGHT thing to match on, which is why this remembers rather than
/// translates: an id stays English on a localised client, where a display name would make
/// every rule stop working the moment somebody changes their game's language.
///
/// It REMEMBERS rather than only listing what is on now, and that is the point. A buff worth
/// writing a rule about is usually one that lasts a few seconds, so the moment somebody
/// switches to the config window to type its name it has already gone. Cast the skill, then
/// go and look.
/// </remarks>
public sealed class BuffWatch
{
    /// <summary>How many names are kept before the oldest are dropped.</summary>
    /// <remarks>
    /// A bound on a set filled from game memory, not a view about how many buffs a character
    /// has. Every unique id in an area lands here - including the ground effects and the
    /// monster auras that wash over the player - so a long session grows it without one.
    /// </remarks>
    public const int MaxRemembered = 512;

    /// <summary>How long a buff no longer on the character stays in the list.</summary>
    /// <remarks>
    /// Long enough to walk to the config window and back, and short enough that a list looked
    /// at an hour later describes this fight rather than the whole session.
    /// </remarks>
    public const long RememberMs = 30 * 60 * 1000;

    private readonly Dictionary<string, SeenBuff> _seen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What has been on, active ones first, then most recently seen.</summary>
    public IReadOnlyList<SeenBuff> Seen
    {
        get
        {
            var all = new List<SeenBuff>(_seen.Values);
            all.Sort(static (left, right) => left.Active != right.Active
                ? right.Active.CompareTo(left.Active)
                : right.LastSeenMs.CompareTo(left.LastSeenMs));

            return all;
        }
    }

    /// <summary>Notes what is on the character now.</summary>
    public void Look(ActiveBuffs? buffs, long nowMs)
    {
        // Everything remembered stops being ACTIVE first, then what is really on is marked
        // again. Anything else and a buff that ended would sit in the list claiming twenty
        // seconds left forever, which is worse than not listing it: it reads as live.
        foreach ((string name, SeenBuff buff) in _seen)
        {
            if (buff.Active)
            {
                _seen[name] = buff with { Active = false };
            }
        }

        if (buffs is not ActiveBuffs on)
        {
            return;
        }

        foreach (ActiveBuff buff in on.All)
        {
            if (string.IsNullOrWhiteSpace(buff.Name))
            {
                continue;
            }

            if (_seen.Count >= MaxRemembered && !_seen.ContainsKey(buff.Name))
            {
                Drop(nowMs);
            }

            _seen[buff.Name] = new SeenBuff(
                buff.Name, true, buff.TimeLeft, buff.Charges, buff.FlaskSlot, nowMs);
        }

        Expire(nowMs);
    }

    /// <summary>Forgets everything - a new character has different buffs.</summary>
    public void Forget() => _seen.Clear();

    private void Expire(long nowMs)
    {
        List<string>? gone = null;
        foreach ((string name, SeenBuff buff) in _seen)
        {
            if (!buff.Active && nowMs - buff.LastSeenMs > RememberMs)
            {
                (gone ??= []).Add(name);
            }
        }

        foreach (string name in gone ?? [])
        {
            _seen.Remove(name);
        }
    }

    /// <summary>Makes room by dropping the oldest inactive entry.</summary>
    private void Drop(long nowMs)
    {
        string? oldest = null;
        long at = long.MaxValue;

        foreach ((string name, SeenBuff buff) in _seen)
        {
            if (!buff.Active && buff.LastSeenMs < at)
            {
                oldest = name;
                at = buff.LastSeenMs;
            }
        }

        // Everything remembered is currently ON - which would be an extraordinary character,
        // and is more likely a read gone wrong. Starting over is the honest response: it may
        // lose a name somebody wanted, which beats refusing to record any new one for the rest
        // of the session.
        if (oldest is null)
        {
            _seen.Clear();
            return;
        }

        _seen.Remove(oldest);
    }
}
