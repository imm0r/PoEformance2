using PoEformance.Game.Components;

namespace PoEformance.Game.World;

/// <summary>
/// Which monsters the game has taken out of the fight for now, by the buff it marks them with.
/// </summary>
/// <remarks>
/// SETTLED BY TWO CAPTURES OF ONE ESSENCE PRISON, before and after the click (2026-10-10, Bluff).
/// An imprisoned monster is a rare with a full bar and a targetable byte of 1, and its mods say
/// nothing - the nameless EncasedMonsterNoActionSpeed the data files offered was NOT on it. What
/// was on it, and on the three white monsters imprisoned with it, was the buff
/// <c>hidden_monster_disable_minions</c>, permanent; after the click all four had lost it, the
/// freed rare carried its essence mods instead, and the prison object beside it read
/// present = 0. GameHelper2 reads the same family on pinnacle bosses, where the buff is
/// <c>hidden_monster</c> plain: a boss between phases is "hidden" in exactly this sense. So the
/// rule is the PREFIX, not one name - any buff starting <see cref="BuffPrefix"/> says the game
/// does not count this monster as fighting, and neither should anything here: not a rule's
/// count, not an aim, not a health bar, not a dot.
///
/// WHY NOT THE PRISON OBJECT. Nearness to a present monolith would cost no read at all, and it
/// is wrong in both directions: the imprisoned pack stood 160 to 360 units from its monolith,
/// and a free monster walking past an unopened one would fall out of every rule while the
/// player stood there to click it. The buff is the game's own answer about the monster itself.
///
/// WHAT IT COSTS, AND HOW THAT IS BOUNDED. A Buffs component is a vector walk plus three reads
/// per entry - the most expensive thing the reader does per monster, which is why the reader
/// pays it only for rares and only while the status icons want it. This question needs every
/// hostile monster, so it is paced instead: a monster is read the first time it is seen and
/// then every <see cref="Stride"/> reads, and keeps its last answer in between. The state this
/// tracks flips once, when the prison opens, so a lag of a few reads is nothing against a
/// cast at a monster the game refuses to hit. Keyed on the monster's Render component, as the
/// corpse filter is, because one monster wears several entities and the walk's pick among them
/// shifts.
/// </remarks>
public sealed class HiddenMonsters
{
    /// <summary>What every buff that hides a monster begins with.</summary>
    public const string BuffPrefix = "hidden_monster";

    /// <summary>Reads between two looks at the same monster. One is every read; eight is an eighth of the cost and a quarter-second lag.</summary>
    public int Stride { get; init; } = 8;

    /// <summary>Drop a monster not seen for this long - it left the area.</summary>
    private const int ForgetMs = 10_000;

    private readonly Dictionary<ulong, Known> _known = [];
    private uint _read;
    private long _lastPrune;

    private readonly record struct Known(bool Hidden, long Seen, uint Read);

    /// <summary>Number of monsters currently remembered - for the diagnostic readout.</summary>
    public int Tracking => _known.Count;

    /// <summary>Marks the start of one read of the world. Called once per read, before any monster is asked about.</summary>
    public void Tick(long nowMs)
    {
        _read++;
        if (nowMs - _lastPrune < ForgetMs)
        {
            return;
        }

        _lastPrune = nowMs;
        var gone = new List<ulong>();
        foreach ((ulong identity, Known known) in _known)
        {
            if (nowMs - known.Seen > ForgetMs)
            {
                gone.Add(identity);
            }
        }

        foreach (ulong identity in gone)
        {
            _known.Remove(identity);
        }
    }

    /// <summary>
    /// Whether this monster is hidden, looking at its buffs when it is its turn to be looked at.
    /// </summary>
    /// <param name="identity">The monster's Render component - see the remarks.</param>
    /// <param name="already">Its buffs, where the reader read them this read for something else - then no second walk is made.</param>
    /// <param name="buffsComponent">Its Buffs component, for the walk when one is due. Zero for a monster without one.</param>
    /// <param name="reader">What walks it.</param>
    /// <param name="mayRead">Whether a walk is allowed at all this read - false for a monster off screen, which keeps what it had.</param>
    /// <param name="nowMs">The clock.</param>
    public bool IsHidden(ulong identity, ActiveBuffs? already, ulong buffsComponent, BuffsReader reader, bool mayRead, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(reader);

        if (already is not null)
        {
            bool fromThem = Any(already);
            _known[identity] = new Known(fromThem, nowMs, _read);
            return fromThem;
        }

        bool remembered = _known.TryGetValue(identity, out Known known);
        if (remembered && (!mayRead || _read - known.Read < (uint)Stride))
        {
            _known[identity] = known with { Seen = nowMs };
            return known.Hidden;
        }

        if (!mayRead)
        {
            // Never looked at and not allowed to look: not hidden, which is what every monster
            // is until the game says otherwise - and it is read the moment it comes on screen.
            return false;
        }

        bool hidden = buffsComponent != 0 && Any(reader.Read(buffsComponent));
        _known[identity] = new Known(hidden, nowMs, _read);
        return hidden;
    }

    /// <summary>Whether any of these buffs hides the monster.</summary>
    public static bool Any(ActiveBuffs buffs)
    {
        ArgumentNullException.ThrowIfNull(buffs);

        foreach (ActiveBuff buff in buffs.All)
        {
            if (buff.Name.StartsWith(BuffPrefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
