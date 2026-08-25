using System.Text.Json.Serialization;
using PoEformance.Game.Components;

namespace PoEformance.Features;

/// <summary>One buff the character has had on, and what it was doing when last looked at.</summary>
/// <param name="Name">The ENGINE identifier - what a rule matches.</param>
/// <param name="DisplayName">The readable name, or empty when the game did not give one.</param>
/// <param name="Description">The buff's own description text, or empty.</param>
/// <param name="Active">Whether it is on right now, rather than remembered from earlier.</param>
/// <remarks>
/// EVERY PROPERTY IS NAMED FOR THE WIRE. The config window's serializer context sets no naming
/// policy - every record that crosses to the page spells its own JSON names - and a record that
/// forgets goes over as "Name"/"Active" while the page reads name/active. Nothing fails: the
/// list arrives with the right number of rows and every field in it is undefined, so the picker
/// showed the word "undefined" as a buff name and wrote it into the field somebody clicked.
/// </remarks>
public sealed record SeenBuff(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("active")] bool Active,
    [property: JsonPropertyName("timeLeft")] float TimeLeft,
    [property: JsonPropertyName("charges")] int Charges,
    [property: JsonPropertyName("flaskSlot")] int FlaskSlot,
    [property: JsonPropertyName("lastSeenMs")] long LastSeenMs,
    [property: JsonPropertyName("displayName")] string DisplayName = "",
    [property: JsonPropertyName("description")] string Description = "");

/// <summary>
/// Remembers which buffs a character has had on, so a rule need not be written by guesswork.
/// </summary>
/// <remarks>
/// THE NAME A RULE MATCHES IS NOT THE NAME THE GAME SHOWS. What a rule matches is the Id
/// column of BuffDefinitions - the engine identifier, spelled like `fire_wall` - while the
/// game paints "Flame Wall". Some ids are close enough to guess and plenty are not, and the
/// reference plugin's answer to that is its own debug window and a lot of scrolling.
///
/// So BOTH names are carried: the id, which is what a rule uses, and the readable one beside
/// it, which is how somebody finds the id they want. Matching stays on the id deliberately -
/// it is the same on every client, where a display name would make every rule stop working
/// the moment somebody changed their game's language.
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

    /// <summary>
    /// Guards <see cref="_seen"/>, which two threads touch.
    /// </summary>
    /// <remarks>
    /// <see cref="Look"/> runs on the reader thread thirty times a second; <see cref="Seen"/>
    /// is read by the config window once a second. Copying a Dictionary while another thread
    /// resizes it is not a rare race - it is a torn read or a throw, either of which reads
    /// from the page as "no buffs", which is the one answer this class must never invent.
    /// </remarks>
    private readonly Lock _gate = new();

    private BuffRead _lastRead;
    private long _looks;

    /// <summary>What has been on, active ones first, then most recently seen.</summary>
    public IReadOnlyList<SeenBuff> Seen
    {
        get
        {
            List<SeenBuff> all;
            lock (_gate)
            {
                all = new List<SeenBuff>(_seen.Values);
            }

            all.Sort(static (left, right) => left.Active != right.Active
                ? right.Active.CompareTo(left.Active)
                : right.LastSeenMs.CompareTo(left.LastSeenMs));

            return all;
        }
    }

    /// <summary>Where the last walk of the buff vector got to, for a panel to explain itself.</summary>
    /// <remarks>
    /// "NOBODY HAS LOOKED YET" IS A DIFFERENT ANSWER FROM "THERE IS NOTHING THERE", and the
    /// first version of this conflated them: a reader thread that never reached Look left the
    /// default reading behind, which printed as "no Buffs component on the player" - an
    /// assertion about the game, made by code that had never been given anything to assert it
    /// from. The count is what tells the two apart.
    /// </remarks>
    public string LastRead
    {
        get
        {
            lock (_gate)
            {
                return _looks == 0
                    ? "the reader has not looked at the player yet"
                    : _lastRead.ToString();
            }
        }
    }

    /// <summary>Notes what is on the character now.</summary>
    public void Look(ActiveBuffs? buffs, long nowMs)
    {
        lock (_gate)
        {
            // Counted BEFORE the early return, so "the reader is running and the snapshot had
            // no buffs" cannot be mistaken for "the reader never ran".
            _looks++;

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
                _lastRead = default;
                return;
            }

            _lastRead = on.Reading;

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
                    buff.Name, true, buff.TimeLeft, buff.Charges, buff.FlaskSlot, nowMs,
                    buff.DisplayName, buff.Description);
            }

            Expire(nowMs);
        }
    }

    /// <summary>Forgets everything - a new character has different buffs.</summary>
    public void Forget()
    {
        lock (_gate)
        {
            _seen.Clear();
        }
    }

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
