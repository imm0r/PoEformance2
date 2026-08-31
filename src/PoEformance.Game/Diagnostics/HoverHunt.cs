using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Components;
using PoEformance.Game.Entities;

namespace PoEformance.Game.Diagnostics;

/// <summary>What one frame of the hover hunt saw.</summary>
/// <param name="Host">The object at InGameState + MouseOverHostPtr, or 0.</param>
/// <param name="Sub">What its +0x3F0 leads to, or 0.</param>
/// <param name="Entity">What the sub-object's +0xA8 holds, or 0.</param>
/// <param name="EntityPath">The metadata path of that entity, when it is one.</param>
/// <param name="BossBytes">Every hostile monster's Monster component byte 0x27, by path.</param>
/// <param name="Companion">What the sub-object's +0xC8 holds, or 0. See CompanionAt.</param>
/// <param name="CompanionRead">How many bytes of the companion's target the reader served.</param>
public sealed record HoverSample(
    ulong Host,
    ulong Sub,
    ulong Entity,
    string EntityPath,
    IReadOnlyList<(string Path, ItemRarity Rarity, byte Flag)> BossBytes,
    ulong Companion = 0,
    int CompanionRead = 0);

/// <summary>
/// Reads the two things nineteen recordings could not answer, because nothing had read them.
/// </summary>
/// <remarks>
/// BOTH QUESTIONS WERE BLOCKED BY THE SAME RULE, not by a hard offset: a replay only serves
/// reads that actually happened, so an offset no build touches is absent from every session
/// ever captured. That is why this exists at all - it is a switch whose whole job is to make
/// the bytes land in a <c>--record</c> file. One capture later
/// (tests/fixtures/session-2026-08-hoverhunt.rec) the two questions stand differently:
///
///  1. THE HOVERED ENTITY - SETTLED. GameHelper2 walks InGameState+0x300 -> +0x3F0 -> +0xA8,
///     and against this client it walks: 143 of 143 non-null readings named an entity the game
///     was listing that same frame, over ten entities of four kinds, and it was null on the
///     other 789. It is read in production now (MouseOverReader, WorldSnapshot.Hovered) and the
///     schema carries the two hop structs. This hunt keeps reading a WINDOW of each object
///     rather than the settled slots, on the same argument --questflags records regions it does
///     not understand: the windows are what the SECOND cursor-tracking slot at sub+0xC8 was
///     found in. It is still unidentified, and it is followed now (see CompanionAt) because the
///     first capture took the offline half of that question as far as it goes - the one thing
///     left is what its target holds, and no committed file has a byte of it.
///  2. THE BOSS BYTE - STILL OPEN, and the capture is the reason to be careful about how that
///     is said. Monster+0x27 came from a reference as an unverified hypothesis, and the run
///     read it 14,462 times: zero every time, across Normal, Magic and Rare, with NO UNIQUE in
///     the area. That is what a working boss flag reads too, so it refutes nothing - see the
///     conclusion in Report, which now says which case was missing instead of concluding from
///     its absence. The pool-cell measurement says how much to read to be thorough: the Monster
///     component's cell is 0x30, so 0x30 bytes IS the whole component.
///
/// The reading is the deliverable. What the bytes mean is decided afterwards, against the
/// file, which is the only way either question has ever been settled here.
/// </remarks>
public sealed class HoverHunt
{
    /// <summary>How much of the host and sub objects to capture around the candidate slots.</summary>
    /// <remarks>
    /// Enough to hold the reference's offset with room either side, and small enough that a
    /// session of it stays shareable: two windows a frame against the recorder's redundancy
    /// filter, which drops every frame in which neither changed.
    /// </remarks>
    public const int WindowBytes = 0x400;

    /// <summary>The whole Monster component - its pool cell is this, so nothing is missed.</summary>
    public const int MonsterComponentBytes = 0x30;

    /// <summary>The sub-object's second cursor-tracking slot, the one nothing has identified.</summary>
    /// <remarks>
    /// It is not in the schema's fields on purpose - what it POINTS AT is unknown, and naming a
    /// field for a pointer whose target nobody has read is how a guess becomes documentation.
    /// It is followed here because a hunt is where an unidentified thing belongs.
    /// </remarks>
    public const int CompanionAt = 0xC8;

    /// <summary>
    /// Window sizes tried at the companion's target, largest first.
    /// </summary>
    /// <remarks>
    /// A ladder rather than one size, because a single read is all-or-nothing: 0x200 bytes off a
    /// small object at the end of a page fails outright and records NOTHING, which is the worst
    /// outcome for a capture whose whole job is to bring bytes home. The measurements say to
    /// expect something small - across 143 hovering frames every value was 16-byte aligned and
    /// neighbouring ones sat 0x10 to 0x40 apart - so the ladder ends small enough to succeed
    /// even if the object really is one cell.
    /// </remarks>
    public static readonly int[] CompanionWindows = [0x200, 0x80, 0x20, 0x10];

    private readonly IMemoryReader _reader;
    private readonly OffsetSchema _schema;
    private readonly EntityReader _entities;
    private readonly EntityMapReader _map;
    private readonly int _awake;
    private readonly int _mouseOverHost;
    private readonly int _rarity;
    private readonly byte[] _window = new byte[WindowBytes];
    private readonly byte[] _monster = new byte[MonsterComponentBytes];

    public HoverHunt(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;
        _schema = schema;
        _entities = new EntityReader(reader, schema);
        _map = new EntityMapReader(reader, schema);
        _awake = schema.Structs["AreaInstance"].OffsetOf("AwakeEntities");
        _mouseOverHost = schema.Structs["InGameState"].OffsetOf("MouseOverHostPtr");
        _subPointerAt = schema.Structs["MouseOverHost"].OffsetOf("SubStructPtr");
        _entityAt = schema.Structs["MouseOverSub"].OffsetOf("HoveredEntity");
        _rarity = schema.Structs["ObjectMagicProperties"].OffsetOf("Rarity");
    }

    /// <summary>Sub-object offset followed out of the host, from the schema now that it holds it.</summary>
    /// <remarks>
    /// These two were hard-coded from the reference while the chain was a hypothesis, which was
    /// right then and is wrong now: the schema carries them as MouseOverHost/MouseOverSub, and
    /// a hunt reading its own copy would keep walking the old offsets after a drift - reporting
    /// "nothing hovered" for a chain that had merely moved.
    /// </remarks>
    private readonly int _subPointerAt;
    private readonly int _entityAt;

    /// <summary>Performs every read for one frame. Null when the chain is not in a world.</summary>
    public HoverSample? SampleFrame(ulong gameStatesStatic)
    {
        GameChainAddresses chain = GameChain.Resolve(_reader, _schema, gameStatesStatic);
        if (chain.InGameState == 0 || chain.AreaInstance == 0)
        {
            return null;
        }

        ulong host = _reader.ReadPointer(chain.InGameState + (ulong)_mouseOverHost);
        ulong sub = 0, entity = 0;
        string path = string.Empty;

        if (MemoryReaderExtensions.IsPlausiblePointer(host))
        {
            // The window first, so it lands in the recording even when the walk below fails.
            _reader.TryRead(host, _window);
            sub = _reader.ReadPointer(host + (ulong)_subPointerAt);
        }

        ulong companion = 0;
        int companionRead = 0;
        if (MemoryReaderExtensions.IsPlausiblePointer(sub))
        {
            _reader.TryRead(sub, _window);
            entity = _reader.ReadPointer(sub + (ulong)_entityAt);
            companion = _reader.ReadPointer(sub + CompanionAt);
        }

        if (MemoryReaderExtensions.IsPlausiblePointer(companion))
        {
            // WHY A LADDER AND NOT A FIXED WINDOW: see CompanionWindows. The first size that
            // reads is kept, and how much it was is carried on the sample - "we read 0x10 of
            // it" and "we read 0x200 of it" are different findings about the object, and a
            // later pass over the file must not have to guess which happened.
            foreach (int size in CompanionWindows)
            {
                if (_reader.TryRead(companion, _window.AsSpan(0, size)))
                {
                    companionRead = size;
                    break;
                }
            }
        }

        if (MemoryReaderExtensions.IsPlausiblePointer(entity)
            && _entities.ReadIdentity(entity) is { } identity)
        {
            path = identity.Path;
        }

        return new HoverSample(
            host, sub, entity, path, ReadBossBytes(chain.AreaInstance), companion, companionRead);
    }

    /// <summary>Byte 0x27 of every hostile monster's Monster component, beside its rarity.</summary>
    /// <remarks>
    /// Rarity is carried with it because that is what the byte has to be judged against: a flag
    /// that is set on exactly the unique monsters is a boss flag, and one that tracks nothing
    /// is a hypothesis that has finally been asked. Reading the whole 0x30 component rather
    /// than the one byte costs nothing and leaves the neighbours in the file.
    /// </remarks>
    private List<(string Path, ItemRarity Rarity, byte Flag)> ReadBossBytes(ulong areaInstance)
    {
        var found = new List<(string, ItemRarity, byte)>();
        foreach ((uint _, ulong address) in _map.ReadEntityPointers(areaInstance + (ulong)_awake))
        {
            if (_entities.ReadIdentity(address) is not { } identity || identity.Path.Length == 0)
            {
                continue;
            }

            IReadOnlyDictionary<string, ulong> components =
                _entities.ReadComponents(address, identity.Details);
            ulong monster = components.GetValueOrDefault("Monster");
            if (monster == 0 || !_reader.TryRead(monster, _monster))
            {
                continue;
            }

            ulong properties = components.GetValueOrDefault("ObjectMagicProperties");
            ItemRarity rarity = properties != 0
                && _reader.TryRead(properties + (ulong)_rarity, out int raw)
                && raw is >= 0 and <= 3
                    ? (ItemRarity)raw
                    : ItemRarity.Unknown;

            found.Add((identity.Path, rarity, _monster[0x27]));
        }

        return found;
    }

    /// <summary>Says what the session saw, and what it therefore settles.</summary>
    public static void Report(IReadOnlyList<HoverSample> samples, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(output);

        output.WriteLine();
        output.WriteLine("hover hunt");
        if (samples.Count == 0)
        {
            output.WriteLine("  no frames - the chain never resolved in a world.");
            return;
        }

        int withHost = samples.Count(s => s.Host != 0);
        int withSub = samples.Count(s => s.Sub != 0);
        int withEntity = samples.Count(s => s.Entity != 0);
        var named = samples.Where(s => s.EntityPath.Length > 0).Select(s => s.EntityPath).Distinct().ToList();

        output.WriteLine($"  frames {samples.Count}: host {withHost}, sub-object {withSub}, entity slot {withEntity}");
        if (named.Count > 0)
        {
            output.WriteLine($"  THE SLOT NAMED {named.Count} DISTINCT ENTITIES - the chain reads a hovered entity:");
            foreach (string path in named.Take(10))
            {
                output.WriteLine($"    {path}");
            }
        }
        else if (withSub > 0)
        {
            output.WriteLine("  the entity slot never held one. That is what nothing-hovered looks like AND");
            output.WriteLine("  what a wrong offset looks like - but the windows are in the recording now, so");
            output.WriteLine("  the right slot can be hunted offline. Hover a monster while this runs.");
        }

        // The companion slot. Reported as WHETHER THE BYTES CAME HOME rather than as a finding,
        // because that is the whole reason it is followed: everything else about it was already
        // measurable offline, and the one thing that was not is what its target holds.
        var withCompanion = samples.Where(s => s.Companion != 0).ToList();
        if (withCompanion.Count > 0)
        {
            int read = withCompanion.Count(s => s.CompanionRead > 0);
            int smallest = read == 0 ? 0 : withCompanion.Where(s => s.CompanionRead > 0).Min(s => s.CompanionRead);
            output.WriteLine();
            output.WriteLine($"  sub+0x{CompanionAt:X} (unidentified): set on {withCompanion.Count} frames, "
                + $"{withCompanion.Select(s => s.Companion).Distinct().Count()} distinct");
            output.WriteLine(read == 0
                ? "    its target read on NO frame - every window size failed, so the capture still"
                  + " cannot say what it points at."
                : $"    target captured on {read} of them, smallest window 0x{smallest:X} bytes."
                  + " That is the part that was missing - mine it offline.");
        }

        // The boss byte, pooled over the whole session and shown against rarity, because the
        // question is not what the byte reads but whether it tracks anything.
        var byRarity = new Dictionary<(ItemRarity Rarity, byte Flag), int>();
        foreach ((string _, ItemRarity rarity, byte flag) in samples.SelectMany(s => s.BossBytes))
        {
            byRarity[(rarity, flag)] = byRarity.GetValueOrDefault((rarity, flag)) + 1;
        }

        output.WriteLine();
        output.WriteLine("  Monster+0x27 ('IsBoss', an unverified hypothesis) against rarity:");
        if (byRarity.Count == 0)
        {
            output.WriteLine("    no monster carried a readable Monster component.");
            return;
        }

        foreach (((ItemRarity rarity, byte flag), int count) in byRarity.OrderBy(k => k.Key.Rarity).ThenBy(k => k.Key.Flag))
        {
            output.WriteLine($"    {rarity,-8} byte={flag,-4} sightings={count}");
        }

        // WHAT THIS MAY NOT SAY, learned by nearly saying it. The first capture read the byte
        // 14,462 times and it was zero every time, which looks like a refutation and is not
        // one: there was no unique monster in the area, and zero on every non-unique is what a
        // WORKING boss flag reads. A hunt is only allowed to conclude when the case that
        // separates the hypotheses was actually present, so it says whether it was.
        bool anySet = byRarity.Keys.Any(k => k.Flag != 0);
        bool sawUnique = byRarity.Keys.Any(k => k.Rarity == ItemRarity.Unique);
        output.WriteLine(anySet
            ? "    the byte is not always zero - compare the rows above against what was on screen."
            : sawUnique
                ? "    ZERO ON UNIQUES TOO, and a unique WAS in the list - so 0x27 is not this flag."
                : "    the byte was zero throughout, but NO UNIQUE was in the list, so the question"
                  + " was not asked. Run this in front of a boss.");
    }
}
