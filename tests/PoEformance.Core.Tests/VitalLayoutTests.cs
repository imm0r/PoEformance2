using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Entities;

namespace PoEformance.Core.Tests;

/// <summary>
/// The vital sub-struct points back at the component it belongs to.
/// </summary>
/// <remarks>
/// Worth a test of its own for the same reason UiElementBase.Self is worth reading: it is a
/// check that a WRONG offset cannot pass. Every other field of a vital is a number in a
/// plausible range, so a base four or eight bytes out still produces something that looks
/// like a health pool; this one either holds the component's exact address or it does not.
///
/// It also settles the one place PoE2's vital differs from the reference. GameHelper2 has a
/// vtable at +0x00 and this back-pointer at +0x08; here the first eight bytes are two small
/// integers - the SAME pair on every entity in the game - and only +0x08 is an address.
/// </remarks>
public class VitalLayoutTests
{
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

    [Fact]
    public void EveryVitalNamesItsOwnLifeComponent()
    {
        OffsetSchema schema = RealSessionTests.Schema();
        StructDef life = schema.Structs["Life"];
        int owner = schema.Structs["Vital"].OffsetOf("OwnerLifeComponent");
        int[] vitals = [life.OffsetOf("Health"), life.OffsetOf("Mana"), life.OffsetOf("EnergyShield")];

        using var replay = ReplayMemoryReader.Load(File.OpenRead(Fixture("session-2026-08-map.rec")));
        ulong gameStates = replay.ResolvedStatics["GameStates"];
        var entities = new EntityReader(replay, schema);
        var map = new EntityMapReader(replay, schema);
        int awake = schema.Structs["AreaInstance"].OffsetOf("AwakeEntities");

        var checkedComponents = new HashSet<ulong>();
        int matched = 0, mismatched = 0;

        int step = Math.Max(1, replay.FrameCount / 30);
        for (uint frame = 0; frame < replay.FrameCount; frame += (uint)step)
        {
            replay.Seek(frame);
            GameChainAddresses chain = GameChain.Resolve(replay, schema, gameStates);
            if (chain.AreaInstance == 0)
            {
                continue;
            }

            foreach ((uint _, ulong address) in
                map.ReadEntityPointers(chain.AreaInstance + (ulong)awake, 4096, true))
            {
                if (entities.ReadIdentity(address) is not { } identity || identity.Path.Length == 0)
                {
                    continue;
                }

                ulong component = entities.ReadComponents(address, identity.Details).GetValueOrDefault("Life");
                if (component == 0 || !checkedComponents.Add(component))
                {
                    continue;
                }

                foreach (int vital in vitals)
                {
                    ulong stored = replay.ReadPointer(component + (ulong)(vital + owner));
                    if (stored == 0)
                    {
                        continue; // the recording never read this vital's header
                    }

                    if (stored == component)
                    {
                        matched++;
                    }
                    else
                    {
                        mismatched++;
                    }
                }
            }
        }

        Assert.True(matched > 300, $"only {matched} vitals answered");
        Assert.Equal(0, mismatched);
    }
}
