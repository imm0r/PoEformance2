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
public sealed record HoverSample(
    ulong Host,
    ulong Sub,
    ulong Entity,
    string EntityPath,
    IReadOnlyList<(string Path, ItemRarity Rarity, byte Flag)> BossBytes);

/// <summary>
/// Reads the two things nineteen recordings cannot answer, because nothing has ever read them.
/// </summary>
/// <remarks>
/// BOTH QUESTIONS ARE BLOCKED BY THE SAME RULE, not by a hard offset: a replay only serves
/// reads that actually happened, so an offset no build touches is absent from every session
/// ever captured. That is why this exists at all - it is a switch whose whole job is to make
/// the bytes land in a <c>--record</c> file.
///
/// WHAT IT READS, and why more than the two candidate slots:
///
///  1. THE HOVERED ENTITY. GameHelper2 walks InGameState+0x300 -> +0x3F0 -> +0xA8. The middle
///     hop resolves against this client and the last reads zero, which is equally what
///     "nothing hovered" and "wrong offset" look like - and the sessions that could have told
///     them apart were captures in which nobody was hovering on purpose. So this reads a
///     WINDOW of each object rather than the two slots, on the same argument --questflags
///     records regions it does not understand: a question that needs the game becomes one that
///     can be re-asked offline as often as it takes, and a wrong reference offset stops being
///     fatal to the capture.
///  2. THE BOSS BYTE. Monster+0x27 has been in the schema as an unverified hypothesis for
///     months and nothing reads it - MonsterSigns derives IsBoss from RARITY instead, so the
///     field has never been exercised. One byte per monster settles it, and the pool-cell
///     measurement says how much to read to be thorough: the Monster component's cell is 0x30,
///     so 0x30 bytes IS the whole component and there is no cheaper way to be complete.
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
        _rarity = schema.Structs["ObjectMagicProperties"].OffsetOf("Rarity");
    }

    /// <summary>Sub-object offset the reference follows out of the host.</summary>
    public const int SubPointerAt = 0x3F0;

    /// <summary>Where the reference expects the hovered entity on the sub-object.</summary>
    public const int EntityAt = 0xA8;

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
            sub = _reader.ReadPointer(host + SubPointerAt);
        }

        if (MemoryReaderExtensions.IsPlausiblePointer(sub))
        {
            _reader.TryRead(sub, _window);
            entity = _reader.ReadPointer(sub + (ulong)EntityAt);
        }

        if (MemoryReaderExtensions.IsPlausiblePointer(entity)
            && _entities.ReadIdentity(entity) is { } identity)
        {
            path = identity.Path;
        }

        return new HoverSample(host, sub, entity, path, ReadBossBytes(chain.AreaInstance));
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

        bool anySet = byRarity.Keys.Any(k => k.Flag != 0);
        output.WriteLine(anySet
            ? "    the byte is not always zero - compare the rows above against what was on screen."
            : "    the byte read ZERO on every monster seen. Either none was a boss, or 0x27 is wrong.");
    }
}
