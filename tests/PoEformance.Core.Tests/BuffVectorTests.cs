using PoEformance.Core.Diagnostics;
using PoEformance.Core.Memory;
using PoEformance.Core.Schema;
using PoEformance.Game.Components;
using PoEformance.Game.Entities;

namespace PoEformance.Core.Tests;

/// <summary>
/// The shape of the player's Buffs vector, settled against real memory.
/// </summary>
/// <remarks>
/// This exists because the schema said the opposite for months and nothing caught it. The
/// entries were read as INLINE StatusEffect structs at 0x50 stride - a "deliberate divergence
/// from GameHelper2", which reads a vector of POINTERS - on the strength of the AHK tool doing
/// the same. The recording says both are wrong, and the failure was silent in the worst way:
/// a real vector's span is 56 to 96 bytes, so dividing by 0x50 floored the count to 0 or 1 and
/// the tool simply reported no buffs. No exception, no empty pointer, nothing to notice - every
/// buff condition just never matched.
///
/// So the assertions here are the ones the game itself settles, not fingerprints. A span of 56
/// bytes CANNOT be a whole number of 0x50 structs; that is arithmetic, and no amount of
/// plausible-looking data gets around it.
///
/// WHAT THIS CANNOT PROVE: the names. A recording only carries reads the running build actually
/// performed, and the build that made this one never followed an element pointer - it was busy
/// dividing by the wrong stride. Reading a real buff name back is a live-game test.
/// </remarks>
public class BuffVectorTests
{
    private const int PointerVector = 0x160;
    private const int PointerVectorEnd = 0x168;
    private const int OldInlineStride = 0x50;

    private static string FixturePath
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures")))
            {
                dir = dir.Parent;
            }

            return Path.Combine(dir!.FullName, "tests", "fixtures", "session-2026-08-buffs.rec");
        }
    }

    /// <summary>Every frame's vector, as (component address, first, last).</summary>
    private static List<(ulong Component, ulong First, ulong Last)> Vectors(
        ReplayMemoryReader replay, OffsetSchema schema)
    {
        var reader = new EntityReader(replay, schema);
        var found = new List<(ulong, ulong, ulong)>();

        for (uint frame = 0; frame < replay.FrameCount; frame++)
        {
            replay.Seek(frame);
            GameChainAddresses chain = GameChain.Resolve(replay, schema, replay.ResolvedStatics["GameStates"]);
            ulong component = reader.Read(chain.PlayerEntity)?.Component("Buffs") ?? 0;
            if (component == 0)
            {
                continue;
            }

            ulong first = replay.ReadPointer(component + PointerVector);
            ulong last = replay.ReadPointer(component + PointerVectorEnd);
            if (first != 0 && last > first)
            {
                found.Add((component, first, last));
            }
        }

        return found;
    }

    [Fact]
    public void TheVectorHoldsPointers_NotInlineStructs()
    {
        // The arithmetic that settles it. Over a thousand frames of a character walking a map,
        // the span between the vector's two bounds is ALWAYS a whole number of 8-byte pointers
        // and only coincidentally a whole number of 0x50 structs. One counter-example would be
        // enough to refute the inline reading; there are hundreds.
        var replay = ReplayMemoryReader.Load(File.OpenRead(FixturePath));
        List<(ulong Component, ulong First, ulong Last)> vectors = Vectors(replay, RealSessionTests.Schema());

        Assert.True(vectors.Count > 100, $"only {vectors.Count} frames carried a buff vector");

        int notInline = 0;
        foreach ((ulong _, ulong first, ulong last) in vectors)
        {
            long span = (long)(last - first);
            Assert.True(span % 8 == 0, $"span {span} is not a whole number of pointers");
            if (span % OldInlineStride != 0)
            {
                notInline++;
            }
        }

        Assert.True(
            notInline > vectors.Count / 2,
            $"only {notInline} of {vectors.Count} spans rule out the inline reading");
    }

    [Fact]
    public void AnEntryPointsBackAtTheComponentThatOwnsIt()
    {
        // The structural half, and the reason this is evidence rather than a second guess at the
        // same bytes: dereference an element and its own first field holds THE BUFFS COMPONENT
        // ADDRESS. A StatusEffect knows its owner. A BuffDefinition is a row of a .dat file
        // loaded once and shared by every entity in the game - it could not possibly point at
        // one live component, let alone this one.
        var replay = ReplayMemoryReader.Load(File.OpenRead(FixturePath));
        List<(ulong Component, ulong First, ulong Last)> vectors = Vectors(replay, RealSessionTests.Schema());

        int proven = 0;
        foreach ((ulong component, ulong first, ulong last) in vectors)
        {
            for (ulong at = first; at < last; at += 8)
            {
                ulong element = replay.ReadPointer(at);
                if (MemoryReaderExtensions.IsPlausiblePointer(element)
                    && replay.ReadPointer(element) == component)
                {
                    proven++;
                }
            }
        }

        Assert.True(proven > 0, "no element dereferenced to a struct owned by the Buffs component");
    }

    [Fact]
    public void TheOldStrideLostNearlyEveryBuff()
    {
        // What the bug cost, kept as a number so the regression has a size. The inline reading
        // floors a span of 56-96 bytes to 0 or 1; the pointer reading finds about eight buffs
        // per frame, which is what a character carrying charges, an aura and a ground effect
        // actually has on.
        var replay = ReplayMemoryReader.Load(File.OpenRead(FixturePath));
        List<(ulong Component, ulong First, ulong Last)> vectors = Vectors(replay, RealSessionTests.Schema());

        long inline = vectors.Sum(v => (long)(v.Last - v.First) / OldInlineStride);
        long pointers = vectors.Sum(v => (long)(v.Last - v.First) / 8);

        Assert.True(pointers > inline * 10, $"inline {inline}, pointers {pointers}");
        Assert.InRange(pointers / (double)vectors.Count, 4, 40);
    }

    [Fact]
    public void TheSchemaSaysPointerSized()
    {
        // The constant the reader divides by. Named for what it is rather than kept as
        // "StatusEffectStructSize" with a new value, so re-applying the old meaning means
        // deleting a name rather than editing a number nobody looks at twice.
        OffsetSchema schema = RealSessionTests.Schema();
        Assert.Equal(8u, schema.Structs["Buffs"].Constants["StatusEffectPointerSize"]);
        Assert.False(schema.Structs["Buffs"].Constants.ContainsKey("StatusEffectStructSize"));
    }

    private const ulong Component = 0x2000_0000_0000;
    private const ulong VectorAt = 0x2000_0001_0000;
    private const ulong EffectAt = 0x2000_0002_0000;
    private const ulong DefinitionAt = 0x2000_0003_0000;
    private const ulong NameAt = 0x2000_0004_0000;

    [Fact]
    public void AnEntryIsFollowedThroughItsPointer()
    {
        // The fix itself, on memory laid out the way the recording says the game lays it out:
        // the vector holds one POINTER, and the StatusEffect it leads to is somewhere else
        // entirely. Read as inline structs this same memory yields nothing at all - a span of
        // 8 bytes divided by 0x50 is zero entries - which is what made the bug invisible.
        OffsetSchema schema = RealSessionTests.Schema();
        StructDef effect = schema.Structs["StatusEffect"];
        var reader = new FakeMemoryReader();

        reader.Place(Component + PointerVector, VectorAt);
        reader.Place(Component + PointerVectorEnd, VectorAt + 8);
        reader.Place(VectorAt, EffectAt);
        reader.Place(EffectAt + (ulong)effect.OffsetOf("BuffDefinitionPtr"), DefinitionAt);
        reader.Place(EffectAt + (ulong)effect.OffsetOf("TimeLeft"), 4.5f);
        reader.Place(DefinitionAt + (ulong)schema.Structs["BuffDefinition"].OffsetOf("Name"), NameAt);
        reader.PlaceUtf16(NameAt, "lightning_infusion");

        ActiveBuff buff = Assert.Single(new BuffsReader(reader, schema).Read(Component).All);
        Assert.Equal("lightning_infusion", buff.Name);
        Assert.Equal(4.5f, buff.TimeLeft, 3);
    }

    [Fact]
    public void AGarbageNameIsRefusedRatherThanListed()
    {
        // What the wrong stride actually produced: a pointer that landed on something which
        // was not a string, read out as "䑐⟄翷", and offered in the picker as a buff somebody
        // could click into a rule. A name is what a rule MATCHES, so a plausible-looking wrong
        // one is worse than none - and an engine id is [a-z0-9_] whatever the bytes look like.
        OffsetSchema schema = RealSessionTests.Schema();
        StructDef effect = schema.Structs["StatusEffect"];
        var reader = new FakeMemoryReader();

        reader.Place(Component + PointerVector, VectorAt);
        reader.Place(Component + PointerVectorEnd, VectorAt + 8);
        reader.Place(VectorAt, EffectAt);
        reader.Place(EffectAt + (ulong)effect.OffsetOf("BuffDefinitionPtr"), DefinitionAt);
        reader.Place(DefinitionAt + (ulong)schema.Structs["BuffDefinition"].OffsetOf("Name"), NameAt);
        reader.PlaceUtf16(NameAt, "䑐⟄翿");

        ActiveBuffs buffs = new BuffsReader(reader, schema).Read(Component);

        // Not dropped silently: the walk says it got all the way to a definition and found no
        // name there, which is a different fault from finding no entries.
        Assert.Equal(string.Empty, Assert.Single(buffs.All).Name);
        Assert.Equal(1, buffs.Reading.Defined);
        Assert.Equal(0, buffs.Reading.Named);
    }

    [Fact]
    public void TheWalkSaysHowFarItGot()
    {
        // The point of the whole record. "No buffs" was five different faults wearing one face.
        OffsetSchema schema = RealSessionTests.Schema();
        var reader = new FakeMemoryReader();

        Assert.Equal(
            "no Buffs component on the player",
            new BuffsReader(reader, schema).Read(0).Reading.ToString());

        reader.Place(Component + PointerVector, VectorAt);
        reader.Place(Component + PointerVectorEnd, VectorAt + OldInlineStride + 4);
        Assert.Contains(
            "not a whole number of entries",
            new BuffsReader(reader, schema).Read(Component).Reading.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AVectorThatIsNotAWholeNumberOfPointersIsRefused()
    {
        // Flooring is exactly how the wrong stride hid for as long as it did: a span that made
        // no sense as a list of entries quietly became a SHORTER list instead of a refusal, and
        // a shorter list of plausible garbage looks like a character with no buffs on.
        //
        // The bytes here are laid out the OLD way - a StatusEffect sitting inline at the front
        // of the vector, with a real name behind it - and the span is a whole number of 0x50
        // structs plus four. The old reader read one buff out of this. The new one must not
        // read it as a pointer either, because 84 is not a whole number of pointers.
        OffsetSchema schema = RealSessionTests.Schema();
        StructDef effect = schema.Structs["StatusEffect"];
        var reader = new FakeMemoryReader();

        reader.Place(Component + PointerVector, VectorAt);
        reader.Place(Component + PointerVectorEnd, VectorAt + OldInlineStride + 4);
        reader.Place(VectorAt + (ulong)effect.OffsetOf("BuffDefinitionPtr"), DefinitionAt);
        reader.Place(DefinitionAt + (ulong)schema.Structs["BuffDefinition"].OffsetOf("Name"), NameAt);
        reader.PlaceUtf16(NameAt, "would_have_been_read_inline");

        Assert.Empty(new BuffsReader(reader, schema).Read(Component).All);
    }
}
