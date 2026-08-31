using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Entities;

namespace PoEformance.Core.Tests;

/// <summary>
/// Components of one class come out of one pool, and the cell says how big the class is.
/// </summary>
/// <remarks>
/// This exists for the one question a recording normally cannot touch: what about a field
/// NOBODY READ. A session holds only the bytes some build asked for, so an offset out of a
/// reference is either confirmed or unanswerable - and the cell adds a third answer, because
/// an offset past it cannot be in the object at all. That is what makes Life's three unread
/// vitals (Ward 0x2E8, Divinity 0x338, Spirit 0x380) credible rather than merely copied: the
/// cell is 0x420, so they fit with 0x88 to spare.
///
/// It also cost a documented conclusion its footing. One component containing two identical
/// sub-objects and two components lying side by side look the same from inside the object -
/// same vtable one cell on, same vectors, same inline buffers - and Inventories was read the
/// first way. The discriminator is outside: ask the ENTITY LIST who owns the address one cell
/// on. It is always somebody else.
/// </remarks>
public class ComponentPoolTests
{
    /// <summary>
    /// The cells measured over every committed recording, for the components sampled enough
    /// times to mean something. Asserted against two of the richest sessions here so the
    /// suite stays quick; the full sweep is what these numbers came from.
    /// </summary>
    private static readonly Dictionary<string, ulong> Cells = new(StringComparer.Ordinal)
    {
        ["Actor"] = 0xCE0,
        ["Animated"] = 0x380,
        ["Buffs"] = 0x190,
        ["Inventories"] = 0x150,
        ["Life"] = 0x420,

        // 0x30, which makes Monster one of the smallest components in the game and is the
        // reason --hoverhunt reads the whole thing rather than the one byte it wanted: the
        // component IS 48 bytes, so being complete costs nothing. It also bounds the boss-flag
        // hypothesis at 0x27 - it fits, with one byte to spare.
        ["Monster"] = 0x30,
        ["Pathfinding"] = 0x5A0,
        ["Positioned"] = 0x550,
        ["Render"] = 0x630,
        ["Stats"] = 0x210,
        ["Targetable"] = 0xA0,
    };

    private static string Fixture(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "tests", "fixtures", name);
    }

    /// <summary>Component address -> the entity whose own table listed it, per sampled frame.</summary>
    private static List<Dictionary<ulong, ulong>> Owners(string fixture, string component, int frames = 40)
    {
        OffsetSchema schema = RealSessionTests.Schema();
        using var replay = ReplayMemoryReader.Load(File.OpenRead(Fixture(fixture)));
        ulong gameStates = replay.ResolvedStatics["GameStates"];
        var entities = new EntityReader(replay, schema);
        var map = new EntityMapReader(replay, schema);
        int awake = schema.Structs["AreaInstance"].OffsetOf("AwakeEntities");

        var perFrame = new List<Dictionary<ulong, ulong>>();
        int step = Math.Max(1, replay.FrameCount / frames);
        for (uint frame = 0; frame < replay.FrameCount; frame += (uint)step)
        {
            replay.Seek(frame);
            GameChainAddresses chain = GameChain.Resolve(replay, schema, gameStates);
            if (chain.AreaInstance == 0)
            {
                continue;
            }

            var owners = new Dictionary<ulong, ulong>();
            foreach ((uint _, ulong address) in
                map.ReadEntityPointers(chain.AreaInstance + (ulong)awake, 4096, true))
            {
                if (entities.ReadIdentity(address) is not { } identity || identity.Path.Length == 0)
                {
                    continue;
                }

                ulong at = entities.ReadComponents(address, identity.Details).GetValueOrDefault(component);
                if (at != 0)
                {
                    owners[at] = address;
                }
            }

            if (owners.Count > 1)
            {
                perFrame.Add(owners);
            }
        }

        return perFrame;
    }

    [Fact]
    public void EveryGapBetweenTwoComponentsOfAClassDividesTheCell()
    {
        foreach ((string component, ulong cell) in Cells)
        {
            var addresses = new SortedSet<ulong>();
            foreach (string fixture in (string[])["session-2026-08-map.rec", "session-2026-08-effects.rec"])
            {
                foreach (Dictionary<ulong, ulong> owners in Owners(fixture, component))
                {
                    addresses.UnionWith(owners.Keys);
                }
            }

            // Gaps between pooled neighbours only. A jump to a different slab says nothing
            // about the cell and would drown the measurement in noise.
            List<ulong> gaps = [];
            ulong previous = 0;
            foreach (ulong at in addresses)
            {
                if (previous != 0 && at - previous < 0x20000)
                {
                    gaps.Add(at - previous);
                }

                previous = at;
            }

            Assert.True(gaps.Count >= 10, $"{component}: only {gaps.Count} gaps to judge by");
            Assert.Equal(cell, gaps.Min());

            // The claim is not "the smallest gap is 0x420" - that is one pair - but that EVERY
            // gap is a whole number of cells, which is what an allocator handing out one size
            // produces and what an arbitrary set of addresses does not.
            int divisible = gaps.Count(g => g % cell == 0);
            Assert.True(divisible >= gaps.Count * 0.95,
                $"{component}: only {divisible} of {gaps.Count} gaps divide by 0x{cell:X}");
        }
    }

    [Fact]
    public void OneCellOnIsAnotherEntitysComponent_NeverASecondSubObjectOfThisOne()
    {
        foreach (string component in (string[])["Inventories", "Life", "Buffs", "Positioned", "Render"])
        {
            ulong cell = Cells[component];
            int neighbour = 0, sameEntity = 0;

            foreach (string fixture in (string[])["session-2026-08-map.rec", "session-2026-08-effects.rec"])
            {
                foreach (Dictionary<ulong, ulong> owners in Owners(fixture, component))
                {
                    foreach ((ulong at, ulong entity) in owners)
                    {
                        if (!owners.TryGetValue(at + cell, out ulong other))
                        {
                            continue; // the neighbour is not in the game's list this frame
                        }

                        if (other == entity)
                        {
                            sameEntity++;
                        }
                        else
                        {
                            neighbour++;
                        }
                    }
                }
            }

            Assert.True(neighbour >= 100,
                $"{component}: only {neighbour} sightings of the cell after a component");
            Assert.Equal(0, sameEntity);
        }
    }

    [Fact]
    public void EverySchemaFieldFitsInsideItsComponentsPoolCell()
    {
        // The audit the cell measurement makes possible, over every component at once. A field
        // past its cell cannot be in the object, so this REFUTES an offset without a byte of it
        // ever having been read - which is the whole reason to measure cells rather than just
        // note them. It runs in seconds and needs no game.
        //
        // It passes today on all twenty modelled components. That is a result rather than a
        // formality: several of those offsets came out of a reference for a different client
        // and had nothing but provenance behind them, and this is the first thing that has
        // checked them against the object they claim to sit in.
        OffsetSchema schema = RealSessionTests.Schema();

        // The game names two components differently from the schema, which is a fact about the
        // schema rather than about the game - the readers look them up by the game's name.
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["NPC"] = "Npc",
            ["WorldItem"] = "WorldItemComponent",
        };

        int audited = 0;
        foreach ((string component, ulong cell) in Cells)
        {
            string name = aliases.GetValueOrDefault(component, component);
            if (!schema.Structs.TryGetValue(name, out StructDef? def) || def.Fields.Count == 0)
            {
                continue;
            }

            audited++;
            foreach (FieldDef field in def.Fields)
            {
                int end = field.Offset + FieldTypes.SizeOf(field.Type);
                Assert.True(end <= (int)cell,
                    $"{name}.{field.Name} ends at 0x{end:X} but the {component} pool cell is 0x{cell:X}"
                    + " - the field cannot be inside the component");
            }
        }

        Assert.True(audited >= 8, $"only {audited} components had both a cell and fields");
    }

    [Fact]
    public void LifeHasRoomForTheThreeVitalsNothingHasEverRead()
    {
        OffsetSchema schema = RealSessionTests.Schema();
        StructDef life = schema.Structs["Life"];
        long cell = life.Constants["PoolCell"];

        // The whole point of measuring the cell: these three come from GameHelper2 and no
        // recording contains a byte of them, so the only thing that can be said about them
        // offline is whether they would fit. They do; a cell of 0x280 would have refuted them.
        Assert.Equal(0x420, cell);
        foreach (string vital in (string[])["Ward", "Divinity", "Spirit"])
        {
            Assert.True(life.OffsetOf(vital) + 0x38 < cell,
                $"{vital} at 0x{life.OffsetOf(vital):X} does not fit a 0x{cell:X} component");
        }

        // And the honest half of the same statement, pinned so a later reader does not mistake
        // "in the schema" for "seen": nothing in the fixtures reads them.
        using var replay = ReplayMemoryReader.Load(File.OpenRead(Fixture("session-2026-08-map.rec")));
        var probe = new byte[8];
        foreach (Dictionary<ulong, ulong> owners in Owners("session-2026-08-map.rec", "Life", 8))
        {
            foreach (ulong at in owners.Keys)
            {
                Assert.False(replay.TryRead(at + (ulong)life.OffsetOf("Ward"), probe));
            }
        }
    }
}
