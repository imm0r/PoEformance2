using PoEformance.Core.Schema;
using PoEformance.Game.Components;
using PoEformance.Game.World;

namespace PoEformance.Core.Tests;

/// <summary>
/// Which monsters the game has taken out of the fight, by the buff it marks them with - and
/// how seldom that buff is looked at.
/// </summary>
/// <remarks>
/// The buff and its prefix come from two captures of one essence prison, before and after the
/// click: hidden_monster_disable_minions on the rare and its three white companions, gone from
/// all four afterwards. The pacing is the half that makes asking every hostile monster
/// affordable, and it is what these pin: a monster is read when first seen, then every Stride
/// reads, and keeps its answer in between.
/// </remarks>
public class HiddenMonstersTests
{
    private const ulong Render = 0x4000_0000_0000;
    private const ulong Component = 0x4000_0001_0000;
    private const ulong VectorAt = 0x4000_0002_0000;
    private const ulong EffectAt = 0x4000_0003_0000;
    private const ulong DefinitionAt = 0x4000_0004_0000;
    private const ulong NameAt = 0x4000_0005_0000;

    private static ActiveBuffs Buffs(params string[] names)
        => new([.. names.Select(name => new ActiveBuff(name, float.PositiveInfinity, float.PositiveInfinity, 1, 0, false))]);

    /// <summary>A Buffs component in fake memory carrying one buff of this name, as the game lays it out.</summary>
    private static (FakeMemoryReader Memory, BuffsReader Reader) OneBuff(OffsetSchema schema, string name)
    {
        var memory = new FakeMemoryReader();
        StructDef buffs = schema.Structs["Buffs"];
        StructDef effect = schema.Structs["StatusEffect"];
        StructDef definition = schema.Structs["BuffDefinition"];
        int pointerSize = (int)buffs.Constants["StatusEffectPointerSize"];

        memory.Place(Component, new byte[0x200]);
        memory.Place(Component + (ulong)buffs.OffsetOf("StatusEffectFirst"), VectorAt);
        memory.Place(Component + (ulong)buffs.OffsetOf("StatusEffectLast"), VectorAt + (ulong)pointerSize);
        memory.Place(VectorAt, EffectAt);
        memory.Place(EffectAt, new byte[0x100]);
        memory.Place(EffectAt + (ulong)effect.OffsetOf("BuffDefinitionPtr"), DefinitionAt);
        memory.Place(EffectAt + (ulong)effect.OffsetOf("TimeLeft"), float.PositiveInfinity);
        memory.Place(EffectAt + (ulong)effect.OffsetOf("TotalTime"), float.PositiveInfinity);
        memory.Place(DefinitionAt, new byte[0x100]);
        memory.Place(DefinitionAt + (ulong)definition.OffsetOf("Name"), NameAt);
        memory.Place(NameAt, new byte[0x100]);
        memory.PlaceUtf16(NameAt, name);

        return (memory, new BuffsReader(memory, schema));
    }

    [Fact]
    public void TheFamilyIsMatchedByItsPrefix()
    {
        // The essence prison's own buff, the plain one GameHelper2 reads on a pinnacle boss
        // between phases, and the ordinary buffs the same captured rare carried beside it.
        Assert.True(HiddenMonsters.Any(Buffs("abyssal_touched", "hidden_monster_disable_minions", "monster_rare_effect_buff")));
        Assert.True(HiddenMonsters.Any(Buffs("hidden_monster")));
        Assert.False(HiddenMonsters.Any(Buffs("visual_archnemesis_mod_display_buff", "monster_rare_effect_buff", "abyssal_touched")));
        Assert.False(HiddenMonsters.Any(ActiveBuffs.None));

        // A prefix, not a substring: a buff that merely mentions the words is not the mark.
        Assert.False(HiddenMonsters.Any(Buffs("not_hidden_monster")));
    }

    [Fact]
    public void BuffsAlreadyReadAreUsedWithoutASecondWalk()
    {
        // The status icons may have walked this rare's buffs this read already; the answer
        // comes off those, and nothing is read - which a reader with nothing in it proves.
        var tracker = new HiddenMonsters();
        var empty = new BuffsReader(new FakeMemoryReader(), RealSessionTests.LiveSchema());

        tracker.Tick(1000);
        Assert.True(tracker.IsHidden(Render, Buffs("hidden_monster_disable_minions"), 0, empty, mayRead: true, 1000));
        Assert.False(tracker.IsHidden(Render + 1, Buffs("monster_rare_effect_buff"), 0, empty, mayRead: true, 1000));
    }

    [Fact]
    public void AMonsterIsReadWhenFirstSeenAndThenEveryStrideReads()
    {
        OffsetSchema schema = RealSessionTests.LiveSchema();
        (FakeMemoryReader memory, BuffsReader reader) = OneBuff(schema, "hidden_monster_disable_minions");
        var tracker = new HiddenMonsters { Stride = 4 };

        tracker.Tick(1000);
        Assert.True(tracker.IsHidden(Render, null, Component, reader, mayRead: true, 1000));

        // The prison opens: the buff's name changes under the same component. The tracker
        // keeps its answer until the monster's turn comes round again...
        memory.PlaceUtf16(NameAt, "monster_rare_effect_buff");
        for (long read = 2; read <= 4; read++)
        {
            tracker.Tick(1000 + (read * 33));
            Assert.True(tracker.IsHidden(Render, null, Component, reader, mayRead: true, 1000 + (read * 33)));
        }

        // ...and on the fourth read after the first it looks again and sees the monster freed.
        tracker.Tick(1200);
        Assert.False(tracker.IsHidden(Render, null, Component, reader, mayRead: true, 1200));
        Assert.Equal(1, tracker.Tracking);
    }

    [Fact]
    public void OffScreenAMonsterKeepsItsAnswerAndAnUnseenOneIsNotHidden()
    {
        OffsetSchema schema = RealSessionTests.LiveSchema();
        (FakeMemoryReader memory, BuffsReader reader) = OneBuff(schema, "hidden_monster_disable_minions");
        var tracker = new HiddenMonsters { Stride = 1 };

        // Never looked at and not allowed to look: not hidden, as every monster is until the
        // game says otherwise - and nothing is remembered about it yet.
        tracker.Tick(1000);
        Assert.False(tracker.IsHidden(Render, null, Component, reader, mayRead: false, 1000));
        Assert.Equal(0, tracker.Tracking);

        // On screen it is read; off screen afterwards it keeps what it had, however the
        // memory changed meanwhile, because it was not allowed to look.
        tracker.Tick(1033);
        Assert.True(tracker.IsHidden(Render, null, Component, reader, mayRead: true, 1033));
        memory.PlaceUtf16(NameAt, "monster_rare_effect_buff");
        tracker.Tick(1066);
        Assert.True(tracker.IsHidden(Render, null, Component, reader, mayRead: false, 1066));
        tracker.Tick(1099);
        Assert.False(tracker.IsHidden(Render, null, Component, reader, mayRead: true, 1099));

        // A monster with no Buffs component at all is not hidden, and is remembered as such.
        Assert.False(tracker.IsHidden(Render + 1, null, 0, reader, mayRead: true, 1099));
        Assert.Equal(2, tracker.Tracking);
    }

    [Fact]
    public void AMonsterNotSeenForTenSecondsIsForgotten()
    {
        var tracker = new HiddenMonsters();
        var empty = new BuffsReader(new FakeMemoryReader(), RealSessionTests.LiveSchema());

        tracker.Tick(0);
        Assert.True(tracker.IsHidden(Render, Buffs("hidden_monster"), 0, empty, mayRead: true, 0));
        tracker.Tick(5_000);
        Assert.Equal(1, tracker.Tracking);

        // Seen again at five seconds, so at fifteen it is five seconds old and stays...
        Assert.True(tracker.IsHidden(Render, Buffs("hidden_monster"), 0, empty, mayRead: true, 5_000));
        tracker.Tick(15_000);
        Assert.Equal(1, tracker.Tracking);

        // ...and at twenty-six it has not been seen for over ten and is dropped.
        tracker.Tick(26_000);
        Assert.Equal(0, tracker.Tracking);
    }
}
