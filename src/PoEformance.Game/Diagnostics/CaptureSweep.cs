using System.Globalization;
using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Entities;
using PoEformance.Game.World;

namespace PoEformance.Game.Diagnostics;

/// <summary>
/// The raw reads the capture key makes once, inside its recording: the game's root objects whole, what each points at, and every component of the entities around the player - with an index saying what sits where.
/// </summary>
/// <remarks>
/// WHY RAW. A recording holds only what was read, and the tool reads only the fields it already
/// understands. The questions a capture is for are the ones nothing reads yet - where the game
/// keeps the light of an area, what a component it has no layout for holds - and those need the
/// bytes AROUND the known fields. So this reads the roots of the chain whole, one hop further to
/// everything they point at, and each nearby entity's components whole; a replay can then answer
/// questions about those bytes that nobody had thought of when the key was pressed.
///
/// BOUNDED, because "everything" is gigabytes: <see cref="RootBytes"/> of each root,
/// <see cref="NeighbourBytes"/> behind each of at most <see cref="MostNeighbours"/> pointers, and
/// <see cref="ComponentBytes"/> of each component of at most <see cref="MostEntities"/> entities
/// within <see cref="EntityReach"/> of the player. A few megabytes before compression.
///
/// A PAGE AT A TIME. A block that runs into an unmapped page fails whole, so each is read in
/// pieces that never cross a 4 KB boundary: the mapped part of a struct at the end of its region
/// is kept rather than lost with the rest.
///
/// THE INDEX IS THE OTHER HALF. Addresses are meaningless a session later; the index names each
/// block - which root, which pointer slot, which entity and component - so a replay can be walked
/// by name.
/// </remarks>
public sealed class CaptureSweep
{
    /// <summary>Bytes read of each root object.</summary>
    public const int RootBytes = 0x2000;

    /// <summary>Bytes read behind each pointer a root holds.</summary>
    public const int NeighbourBytes = 0x800;

    /// <summary>Pointers followed out of the roots, all told.</summary>
    public const int MostNeighbours = 2048;

    /// <summary>Bytes read of each entity, its details and each of its components.</summary>
    public const int ComponentBytes = 0x400;

    /// <summary>How far from the player, in world units, an entity's components are read - a little past what the screen shows.</summary>
    public const float EntityReach = 2500f;

    /// <summary>Entities whose components are read, nearest first.</summary>
    public const int MostEntities = 512;

    private const int Page = 0x1000;

    private readonly IMemoryReader _reader;
    private readonly OffsetSchema _schema;
    private readonly EntityReader _entities;

    /// <param name="reader">What to read through - the recording's reader, so every read lands in it.</param>
    /// <param name="schema">The offsets, for the chain and the entities.</param>
    public CaptureSweep(IMemoryReader reader, OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(schema);
        _reader = reader;
        _schema = schema;
        _entities = new EntityReader(reader, schema);
    }

    /// <summary>Bytes read in all, for the index's last line.</summary>
    public long BytesRead { get; private set; }

    /// <summary>
    /// The whole sweep: the chain's roots and what they point at, then the entities around the player. Returns the index.
    /// </summary>
    /// <param name="gameStatesStatic">The GameStates static.</param>
    /// <param name="entities">The entities a fresh read found - only those the game is listing are followed.</param>
    /// <param name="playerX">The player's world position, for the reach.</param>
    /// <param name="playerY">The player's world position, for the reach.</param>
    public List<string> Run(ulong gameStatesStatic, IReadOnlyList<WorldEntity> entities, float playerX, float playerY)
    {
        ArgumentNullException.ThrowIfNull(entities);
        GameChainAddresses chain = GameChain.Resolve(_reader, _schema, gameStatesStatic);
        var index = new List<string>
        {
            $"chain: state {chain.State}, GameState {Hex(chain.GameState)}, InGameState {Hex(chain.InGameState)}, "
                + $"AreaInstance {Hex(chain.AreaInstance)}, WorldData {Hex(chain.WorldData)}, UiRoot {Hex(chain.UiRoot)}, "
                + $"PlayerEntity {Hex(chain.PlayerEntity)}",
        };

        Roots(
            [
                ("GameState", chain.GameState),
                ("InGameState", chain.InGameState),
                ("AreaInstance", chain.AreaInstance),
                ("WorldData", chain.WorldData),
                ("UiRoot", chain.UiRoot),
                ("PlayerEntity", chain.PlayerEntity),
            ],
            index);
        Entities(entities, playerX, playerY, index);
        index.Add($"{BytesRead:N0} bytes read in all".Replace(',', ' '));
        return index;
    }

    /// <summary>
    /// Each root whole, then <see cref="NeighbourBytes"/> behind every pointer in it - roots first, so a pointer from one root to another is not read twice.
    /// </summary>
    public void Roots(IReadOnlyList<(string Name, ulong Address)> roots, List<string> index)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(index);
        var seen = new HashSet<ulong>();
        var read = new List<(string Name, ulong Address, byte[] Bytes, bool[] Mapped)>();
        foreach ((string name, ulong address) in roots)
        {
            if (address == 0 || !seen.Add(address))
            {
                continue;
            }

            (byte[] bytes, bool[] mapped, int got) = Block(address, RootBytes);
            index.Add($"root {name} at {Hex(address)}: {got} of {RootBytes} bytes");
            read.Add((name, address, bytes, mapped));
        }

        int followed = 0;
        foreach ((string name, ulong address, byte[] bytes, bool[] mapped) in read)
        {
            for (var at = 0; at + 8 <= bytes.Length; at += 8)
            {
                if (!mapped[at] || !mapped[at + 7])
                {
                    continue;
                }

                ulong pointer = BitConverter.ToUInt64(bytes, at);
                if (!MemoryReaderExtensions.IsPlausiblePointer(pointer) || !seen.Add(pointer))
                {
                    continue;
                }

                if (followed++ >= MostNeighbours)
                {
                    index.Add($"(more than {MostNeighbours} pointers - the rest not followed)");
                    return;
                }

                (_, _, int got) = Block(pointer, NeighbourBytes);
                if (got > 0)
                {
                    index.Add($"  {name}+0x{at:X} -> {Hex(pointer)}: {got} bytes");
                }
            }
        }
    }

    /// <summary>The entities the game is listing within reach, nearest first: each one, its details, and every component, whole.</summary>
    public void Entities(IReadOnlyList<WorldEntity> entities, float playerX, float playerY, List<string> index)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(index);
        float reach = EntityReach * EntityReach;
        var near = entities
            .Where(one => !one.IsRemembered && Squared(one, playerX, playerY) <= reach)
            .OrderBy(one => Squared(one, playerX, playerY))
            .Take(MostEntities)
            .ToList();
        index.Add($"entities: {near.Count} within {EntityReach:0} world units, nearest first");
        foreach (WorldEntity one in near)
        {
            EntityIdentity? identity = _entities.ReadIdentity(one.Address);
            if (identity is not { } known)
            {
                index.Add($"entity {Hex(one.Address)} {one.Path}: no longer readable");
                continue;
            }

            int got = Block(one.Address, ComponentBytes).Got;
            int details = Block(known.Details, ComponentBytes).Got;
            index.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"entity {Hex(one.Address)} id {known.Id} {known.Path} at {one.WorldX:0.#} {one.WorldY:0.#} {one.WorldZ:0.#}: {got} bytes, details {Hex(known.Details)} {details} bytes"));
            foreach ((string component, ulong address) in _entities.Read(known).Components.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                index.Add($"  {component} {Hex(address)}: {Block(address, ComponentBytes).Got} bytes");
            }
        }
    }

    /// <summary>A block read a page at a time: its bytes, which of them were readable, and how many.</summary>
    private (byte[] Bytes, bool[] Mapped, int Got) Block(ulong address, int length)
    {
        var bytes = new byte[length];
        var mapped = new bool[length];
        int got = 0;
        for (var at = 0; at < length;)
        {
            ulong here = address + (ulong)at;
            int take = (int)Math.Min((ulong)(length - at), Page - (here % Page));
            if (_reader.TryRead(here, bytes.AsSpan(at, take)))
            {
                mapped.AsSpan(at, take).Fill(true);
                got += take;
            }

            at += take;
        }

        BytesRead += got;
        return (bytes, mapped, got);
    }

    private static float Squared(WorldEntity one, float x, float y)
        => ((one.WorldX - x) * (one.WorldX - x)) + ((one.WorldY - y) * (one.WorldY - y));

    private static string Hex(ulong address) => "0x" + address.ToString("X", CultureInfo.InvariantCulture);
}
